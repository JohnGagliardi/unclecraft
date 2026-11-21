using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;

namespace McShared;

public sealed class BrokerException(string message, string? code = null, JsonNode? body = null) : Exception(message)
{
    public string? Code { get; } = code;
    public JsonNode? Body { get; } = body;
}

public sealed record Lease(string LeaseToken, string Person, string AccountName, string? ServerHost, int ServerPort, bool ServerSignin,
    string? DeviceToken = null, string? DeviceId = null, TunnelInfo? Tunnel = null);
public sealed record McToken(string Username, string Uuid, string AccessToken);
public sealed record ModInfo(string File, long Size, string Sha256);
public sealed record ClientManifest(string MinecraftVersion, string FabricLoaderVersion, List<ModInfo> Mods, bool TestMode);

/// <summary>HTTPS client for the host's broker. Trusts exactly one certificate: the pinned SHA-256 fingerprint.</summary>
public sealed class BrokerClient : IDisposable
{
    private readonly HttpClient _http;
    public string Host { get; }
    public int Port { get; }

    /// <summary>Trusts exactly the certificate whose SHA-256 is the pinned fingerprint (API calls and the game tunnel).</summary>
    public static RemoteCertificateValidationCallback PinCheck(string pinHex) => (_, cert, _, _) =>
        cert != null && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(pinHex), SHA256.HashData(cert.GetRawCertData()));

    public BrokerClient(string host, int port, string fingerprint)
    {
        Host = host;
        Port = port;
        var pin = NormalizeFingerprint(fingerprint);
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = PinCheck(pin) },
            ConnectTimeout = TimeSpan.FromSeconds(5),
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri($"https://{HostPort(host, port)}"), Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>
    /// Parses what a person types as the host's address: a LAN IP, a public IP, a DNS name (uncle.example.org),
    /// optionally with :port, or IPv6 ("[2001:db8::1]:25591" or bare). No private-address restriction here: the
    /// broker decides who may connect, and the pinned fingerprint decides whom we trust.
    /// </summary>
    public static (string host, int port) ParseAddress(string text, int defaultPort)
    {
        var s = text.Trim();
        if (s.Contains("://")) s = new Uri(s).Authority; // tolerate a pasted https://host:port/
        s = s.TrimEnd('/');
        if (s.StartsWith('['))
        {
            var end = s.IndexOf(']');
            var h = end > 0 ? s[1..end] : s.Trim('[', ']');
            var rest = end > 0 ? s[(end + 1)..] : "";
            if (rest.Length > 0 && !rest.StartsWith(':')) throw BadAddress();
            return (h, rest.Length > 0 ? ParsePort(rest[1..]) : defaultPort);
        }
        var colons = s.Count(c => c == ':');
        if (colons == 1)
        {
            var i = s.IndexOf(':');
            return (s[..i], ParsePort(s[(i + 1)..]));
        }
        return (s, defaultPort); // plain host, or bare IPv6
    }

    private static int ParsePort(string s) =>
        int.TryParse(s, System.Globalization.NumberStyles.None, null, out var p) && p is >= 1 and <= 65535 ? p : throw BadAddress();

    private static BrokerException BadAddress() =>
        new("That address doesn't look right. Use an IP like 192.168.1.20 or a name like uncle.example.org.", "bad_address");

    /// <summary>host:port for URLs and Minecraft's --quickPlayMultiplayer (IPv6 gets brackets).</summary>
    public static string HostPort(string host, int port) => (host.Contains(':') ? $"[{host}]" : host) + ":" + port;

    public static string NormalizeFingerprint(string f)
    {
        var hex = new string(f.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 64) throw new BrokerException("The fingerprint should be 64 hex digits (32 pairs like 12:34:...). Ask the host for it.");
        return hex;
    }

    private async Task<JsonNode> Send(HttpMethod method, string path, object? body = null, string? bearer = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body != null) req.Content = JsonContent.Create(body);
        if (bearer != null) req.Headers.Authorization = new("Bearer", bearer);
        HttpResponseMessage res;
        try { res = await _http.SendAsync(req, ct); }
        catch (HttpRequestException e) when (e.InnerException is System.Security.Authentication.AuthenticationException)
        { throw new BrokerException("The server's certificate does not match the fingerprint you entered. Check it with the host.", "bad_fingerprint"); }
        catch (HttpRequestException e)
        { throw new BrokerException($"Can't reach the host's server at {Host} ({e.Message}). Is the host's PC on, and is the address right (home network IP, or the host's internet address/name)?", "unreachable"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { throw new BrokerException($"The host's server at {Host} did not answer in time.", "timeout"); }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            JsonNode? json = null;
            try { json = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text); } catch { /* not JSON */ }
            if (!res.IsSuccessStatusCode)
                throw new BrokerException(json?["message"]?.GetValue<string>() ?? json?["error"]?.GetValue<string>() ?? $"HTTP {(int)res.StatusCode}",
                    json?["error"]?.GetValue<string>() ?? ((int)res.StatusCode).ToString(), json);
            return json ?? new JsonObject();
        }
    }

    public async Task<ClientManifest> GetManifest(CancellationToken ct = default)
    {
        var j = await Send(HttpMethod.Get, "/api/v1/client/manifest", ct: ct);
        var mods = j["mods"]!.AsArray().Select(m => new ModInfo(m!["file"]!.GetValue<string>(), m["size"]!.GetValue<long>(), m["sha256"]!.GetValue<string>())).ToList();
        foreach (var m in mods)
            if (!SafeModName(m.File)) throw new BrokerException($"The host's mod list has a bad file name: {m.File}. Tell the host.", "bad_manifest");
        return new ClientManifest(j["minecraftVersion"]!.GetValue<string>(), j["fabricLoaderVersion"]!.GetValue<string>(), mods,
            j["testMode"]?.GetValue<bool>() ?? false);
    }

    /// <summary>A mod name is a plain "x.jar" file name: no folders, no "..", nothing that could land outside the mods folder.</summary>
    private static bool SafeModName(string f) =>
        f.Length > 4 && f != "." && f != ".." && Path.GetFileName(f) == f && !f.Contains('/') && !f.Contains('\\')
        && f.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);

    public async Task DownloadMod(ModInfo mod, string dest, CancellationToken ct = default)
    {
        HttpResponseMessage sent;
        try { sent = await _http.GetAsync("/api/v1/client/mods/" + Uri.EscapeDataString(mod.File), HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException e) when (e.InnerException is System.Security.Authentication.AuthenticationException)
        { throw new BrokerException("The server's certificate does not match the fingerprint you entered. Check it with the host.", "bad_fingerprint"); }
        catch (HttpRequestException e)
        { throw new BrokerException($"Can't reach the host's server at {Host} to download mod {mod.File} ({e.Message}).", "unreachable"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { throw new BrokerException($"The host's server at {Host} did not answer in time.", "timeout"); }
        using var res = sent;
        if (res.StatusCode != HttpStatusCode.OK) throw new BrokerException($"Could not download mod {mod.File} from the host (HTTP {(int)res.StatusCode}).");
        var tmp = dest + ".tmp";
        try
        {
            await using var fs = File.Create(tmp);
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await IdleCopy.CopyAsync(src, fs, ct);
        }
        catch (TimeoutException) { File.Delete(tmp); throw new BrokerException($"Downloading mod {mod.File} from the host stalled; try again.", "timeout"); }
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(tmp)));
        if (!hash.Equals(mod.Sha256, StringComparison.OrdinalIgnoreCase)) { File.Delete(tmp); throw new BrokerException($"Mod {mod.File} was corrupted in download; try again."); }
        File.Move(tmp, dest, true);
    }

    public async Task<Lease> Login(string person, string password, bool remember, CancellationToken ct = default) =>
        ToLease(await Send(HttpMethod.Post, "/api/v1/sessions",
            new { person, password, serverSignin = true, rememberDevice = remember, deviceLabel = Environment.MachineName }, ct: ct));

    /// <summary>Remembered login: the device token from "Remember me on this PC" instead of a password.</summary>
    public async Task<Lease> LoginWithDevice(string deviceToken, CancellationToken ct = default) =>
        ToLease(await Send(HttpMethod.Post, "/api/v1/sessions", new { deviceToken, serverSignin = true }, ct: ct));

    /// <summary>
    /// First login with an invite code from the host (no password). Success always carries deviceToken/deviceId:
    /// the code makes this PC a remembered device. 409 no_free_account also carries them (code spent, device kept).
    /// </summary>
    /// newPassword (optional, 10+ chars) sets the person's own password; 400 password_too_short leaves the code unspent.
    public async Task<Lease> LoginWithInvite(string inviteCode, string? newPassword = null, CancellationToken ct = default)
    {
        var body = new JsonObject { ["inviteCode"] = inviteCode, ["deviceLabel"] = Environment.MachineName, ["serverSignin"] = true };
        if (!string.IsNullOrEmpty(newPassword)) body["newPassword"] = newPassword;
        return ToLease(await Send(HttpMethod.Post, "/api/v1/sessions", body, ct: ct));
    }

    /// <summary>Invite code typed by a person -> 16 chars, or null. Loose: separators/case ignored (the broker maps O/I/L).</summary>
    public static string? NormalizeInvite(string s)
    {
        var c = new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return c.Length == 16 && c.All(ch => ch < 128) ? c : null;
    }

    /// <summary>Revokes a remembered login on the host (best effort).</summary>
    public async Task ForgetDevice(string deviceToken)
    {
        try { await Send(HttpMethod.Delete, "/api/v1/devices", bearer: deviceToken); } catch (BrokerException) { /* host offline: it expires anyway */ }
    }

    private static Lease ToLease(JsonNode j) => new(
        j["leaseToken"]!.GetValue<string>(),
        j["person"]!.GetValue<string>(),
        j["account"]?["username"]?.GetValue<string>() ?? "?",
        j["server"]?["host"]?.GetValue<string>(),
        j["server"]?["port"]?.GetValue<int>() ?? 25570,
        j["minecraftToken"]?["available"]?.GetValue<bool>() ?? false,
        j["deviceToken"]?.GetValue<string>(),
        j["deviceId"]?.GetValue<string>(),
        j["tunnel"]?["alpn"]?.GetValue<string>() is { Length: > 0 } alpn
            ? new TunnelInfo(alpn, j["tunnel"]?["voicePort"]?.GetValue<int>() ?? 24454) : null);

    public async Task<McToken> GetMinecraftToken(Lease lease, CancellationToken ct = default)
    {
        var j = await Send(HttpMethod.Post, "/api/v1/sessions/minecraft-token", new { }, lease.LeaseToken, ct);
        return new McToken(j["username"]!.GetValue<string>(), j["uuid"]!.GetValue<string>(), j["accessToken"]!.GetValue<string>());
    }

    /// <summary>Read-only lobby view: test mode, free accounts, who is playing. Older brokers: falls back to /health.</summary>
    public async Task<JsonNode> GetStatus(CancellationToken ct = default)
    {
        try { return await Send(HttpMethod.Get, "/api/v1/status", ct: ct); }
        catch (BrokerException e) when (e.Code == "not_found" || e.Code == "404")
        {
            await Send(HttpMethod.Get, "/api/v1/health", ct: ct);
            return new JsonObject { ["ok"] = true, ["testMode"] = false, ["accounts"] = new JsonObject { ["total"] = 0, ["free"] = 0 }, ["playing"] = new JsonArray() };
        }
    }

    public Task Heartbeat(Lease lease, CancellationToken ct = default) => Send(HttpMethod.Post, "/api/v1/sessions/heartbeat", new { }, lease.LeaseToken, ct);

    /// <summary>Gives the account back. While the server still sees the player online it answers 409; it releases on quit itself, so retry briefly.</summary>
    public async Task Release(Lease lease)
    {
        for (var i = 0; i < 6; i++)
        {
            try { await Send(HttpMethod.Delete, "/api/v1/sessions", bearer: lease.LeaseToken); return; }
            catch (BrokerException e) when (e.Code == "still_online") { await Task.Delay(5000); }
            catch (BrokerException e) when (e.Code == "no_session") { return; } // already released by the server
            catch (BrokerException) { return; } // server unreachable: its lease timeout cleans up
        }
    }

    public void Dispose() => _http.Dispose();
}
