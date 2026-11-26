using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace McShared;

/// <summary>What the broker says about its game tunnel (lease "tunnel"); null = an older broker, connect directly.</summary>
public sealed record TunnelInfo(string Alpn, int VoicePort);

/// <summary>
/// Where Minecraft should connect, and what to stop when that game exits.
/// With a tunnel-capable broker (and a host that isn't this PC), Minecraft joins 127.0.0.1:&lt;free port&gt; and every
/// connection is carried inside TLS to the broker's HTTPS port, pinned to the same fingerprint as the API calls.
/// Simple Voice Chat then sends its UDP to 127.0.0.1 too (it uses the address the game joined), so a local relay
/// forwards it to the host's voice port. Its key exchange travels inside the tunnel.
/// </summary>
public static class GameConnection
{
    /// <summary>target "test": the host's test server. It only exists behind the tunnel (also when the host is this PC),
    /// and it has no voice chat, so no voice relay is started.</summary>
    public static async Task<(string address, IDisposable? stop)> Open(BrokerClient broker, string pinHex, Lease lease, string directHost, string? target = null)
    {
        var test = target == "test";
        if (test && lease.Tunnel == null)
            throw new BrokerException("The host's server is too old for the test server (no encrypted game connection). Ask the host to update it.", "no_tunnel");
        if (!test && (lease.Tunnel == null || IsThisPc(broker.Host)))
            return (BrokerClient.HostPort(directHost, lease.ServerPort), null);
        var tunnel = new GameTunnel(broker.Host, broker.Port, pinHex, lease.LeaseToken, lease.Tunnel!.Alpn, lease.Person, target);
        try
        {
            await tunnel.Start();
            if (test) { Log.Info($"test server for {lease.Person}: no voice relay"); return ($"127.0.0.1:{tunnel.LocalPort}", new Both(tunnel, null)); }
            var voice = VoiceRelay.Acquire(broker.Host, lease.Tunnel.VoicePort);
            return ($"127.0.0.1:{tunnel.LocalPort}", new Both(tunnel, voice));
        }
        catch { tunnel.Dispose(); throw; }
    }

    private sealed class Both(IDisposable a, IDisposable? b) : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) { a.Dispose(); b?.Dispose(); } }
    }

    /// <summary>The host is this PC: the game connection never leaves the machine, so no tunnel is needed.</summary>
    public static bool IsThisPc(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(u => u.Address.Equals(ip));
        }
        catch { return false; }
    }
}

/// <summary>One game's local listener: 127.0.0.1:&lt;free port&gt; -> TLS (pinned) -> broker -> Paper.</summary>
public sealed class GameTunnel(string host, int port, string pinHex, string leaseToken, string alpn, string person, string? target = null) : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, IDisposable> _open = new();
    public int LocalPort { get; private set; }

    /// <summary>Checks the tunnel answers (TLS, pin, hello) before Minecraft starts, then listens.</summary>
    public async Task Start()
    {
        await using (var probe = await Connect(_cts.Token)) { } // clear error now instead of "Connection lost" in game
        _listener.Start();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Log.Info($"tunnel for {person}: 127.0.0.1:{LocalPort} -> {BrokerClient.HostPort(host, port)} (TLS, pinned){(target != null ? $", target {target} server" : "")}");
        _ = AcceptLoop();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); } catch { return; }
            _ = Serve(c);
        }
    }

    private async Task Serve(TcpClient local)
    {
        var id = Guid.NewGuid();
        _open[id] = local;
        try
        {
            local.NoDelay = true;
            await using var remote = await Connect(_cts.Token);
            _open[id] = new Both(local, remote);
            var ls = local.GetStream();
            var up = ls.CopyToAsync(remote, _cts.Token);
            var down = remote.CopyToAsync(ls, _cts.Token);
            await Task.WhenAny(up, down);
        }
        catch (BrokerException e) { Log.Info($"tunnel for {person}: {e.Message}"); }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception e) { Log.Error($"tunnel for {person}", e); }
        finally { if (_open.TryRemove(id, out var d)) d.Dispose(); local.Dispose(); }
    }

    private sealed class Both(IDisposable a, IAsyncDisposable b) : IDisposable
    {
        public void Dispose() { a.Dispose(); _ = b.DisposeAsync(); }
    }

    /// <summary>TLS to the broker with ALPN uc-game/1, the hello line, and its answer. Throws BrokerException on refusal.</summary>
    private async Task<SslStream> Connect(CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        SslStream? ssl = null;
        // one deadline for the whole setup (connect, TLS, hello, answer): a host that goes quiet must not hang Play
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using (var c = CancellationTokenSource.CreateLinkedTokenSource(t.Token))
            {
                c.CancelAfter(TimeSpan.FromSeconds(8));
                try { await tcp.ConnectAsync(host, port, c.Token); }
                catch (Exception e) when (e is SocketException or OperationCanceledException)
                { throw new BrokerException($"Can't reach the host's server at {host} for the game connection.", "unreachable"); }
            }
            ssl = new SslStream(tcp.GetStream(), false);
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = IPAddress.TryParse(host, out _) ? "" : host,
                    RemoteCertificateValidationCallback = BrokerClient.PinCheck(pinHex),
                    ApplicationProtocols = [new SslApplicationProtocol(alpn)],
                }, t.Token);
            }
            catch (System.Security.Authentication.AuthenticationException)
            { throw new BrokerException("The server's certificate does not match the fingerprint you entered. Check it with the host.", "bad_fingerprint"); }
            if (ssl.NegotiatedApplicationProtocol.ToString() != alpn)
                throw new BrokerException("The host's server is too old for the encrypted game connection. Ask the host to update it.", "no_tunnel");

            var hello = new JsonObject { ["v"] = 1, ["lease"] = leaseToken };
            if (target != null) hello["target"] = target; // only for the test server: the live hello is unchanged
            await ssl.WriteAsync(Encoding.UTF8.GetBytes(hello.ToJsonString() + "\n"), t.Token);
            await ssl.FlushAsync(t.Token);
            var answer = await ReadLine(ssl, t.Token);
            var j = JsonNode.Parse(answer);
            if (j?["ok"]?.GetValue<bool>() != true)
            {
                var code = j?["error"]?.GetValue<string>() ?? "tunnel_refused";
                throw new BrokerException(j?["message"]?.GetValue<string>() ?? RefusedText(code), code);
            }
            return ssl;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && (e is OperationCanceledException || (t.IsCancellationRequested && e is IOException)))
        {
            if (ssl != null) await ssl.DisposeAsync();
            tcp.Dispose();
            throw new BrokerException($"Can't reach the host's server at {host} for the game connection (it stopped answering).", "unreachable");
        }
        catch
        {
            if (ssl != null) await ssl.DisposeAsync();
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Wording for a refusal that came without the broker's own message (the code is kept as the error code).</summary>
    private static string RefusedText(string code) => code switch
    {
        "test_not_allowed" => "This account can't use the test server.",
        "test_off" => "The test server is switched off on the host.",
        "test_starting" => "The test server is still starting. Try again in a minute.",
        "test_down" => "The test server isn't running.",
        "busy" => "The test server is busy right now.",
        _ => "The host refused the game connection.",
    };

    /// <summary>Reads one '\n'-terminated line byte by byte, so no Minecraft bytes after it are swallowed.</summary>
    private static async Task<string> ReadLine(Stream s, CancellationToken ct)
    {
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(TimeSpan.FromSeconds(10));
        var buf = new List<byte>();
        var one = new byte[1];
        while (buf.Count < 4096)
        {
            if (await s.ReadAsync(one, t.Token) == 0) throw new BrokerException("The host closed the game connection.", "tunnel_closed");
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(buf.ToArray());
            buf.Add(one[0]);
        }
        throw new BrokerException("The host sent an unexpected answer.", "tunnel_bad_answer");
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
        foreach (var d in _open.Values) { try { d.Dispose(); } catch { /* closing */ } }
        _open.Clear();
        Log.Info($"tunnel for {person} closed");
    }
}

/// <summary>
/// Simple Voice Chat's UDP, relayed: 127.0.0.1:&lt;voice port&gt; on this PC to the host's voice port. One relay serves
/// every game on this PC (split screen), keyed by each game's own UDP source port. Plain UDP, like without the tunnel;
/// the voice packets are encrypted by Simple Voice Chat with the key that now arrives inside the tunnel.
/// </summary>
public sealed class VoiceRelay : IDisposable
{
    private static readonly object Gate = new();
    private static VoiceRelay? _shared;
    private static int _refs;

    private readonly UdpClient _local;
    private readonly IPEndPoint _server;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<IPEndPoint, (UdpClient up, long last)> _flows = new();

    /// <summary>Starts (or shares) the relay. Returns null if the port is taken here, so the game still starts without voice.</summary>
    public static IDisposable? Acquire(string host, int port)
    {
        lock (Gate)
        {
            if (_shared == null)
            {
                try { _shared = new VoiceRelay(host, port); }
                catch (Exception e) { Log.Info($"voice relay not started (127.0.0.1:{port}: {e.Message}); voice chat won't connect"); return null; }
            }
            _refs++;
            return new Ref();
        }
    }

    private sealed class Ref : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (Gate) { if (--_refs == 0) { _shared?.Dispose(); _shared = null; } }
        }
    }

    private VoiceRelay(string host, int port)
    {
        var addr = IPAddress.TryParse(host, out var ip) ? ip
            : Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? Dns.GetHostAddresses(host)[0];
        _server = new IPEndPoint(addr, port);
        _local = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        NoConnReset(_local);
        Log.Info($"voice relay: udp 127.0.0.1:{port} -> {_server}");
        _ = FromGames();
        _ = Sweep();
    }

    /// <summary>Windows reports an ICMP "port unreachable" as an error on the next receive; ignore it.</summary>
    private static void NoConnReset(UdpClient u)
    {
        if (OperatingSystem.IsWindows()) { try { u.Client.IOControl(-1744830452 /* SIO_UDP_CONNRESET */, [0], null); } catch { /* best effort */ } }
    }

    private async Task FromGames()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _local.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            var now = Environment.TickCount64;
            if (!_flows.TryGetValue(r.RemoteEndPoint, out var f))
            {
                var up = new UdpClient(_server.AddressFamily);
                NoConnReset(up);
                up.Connect(_server);
                f = (up, now);
                _flows[r.RemoteEndPoint] = f;
                _ = FromServer(r.RemoteEndPoint, up);
            }
            _flows[r.RemoteEndPoint] = (f.up, now);
            try { await f.up.SendAsync(r.Buffer, _cts.Token); } catch { /* dropped, like any UDP packet */ }
        }
    }

    private async Task FromServer(IPEndPoint game, UdpClient up)
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await up.ReceiveAsync(_cts.Token); }
            catch (SocketException) { continue; }
            catch { return; } // cancelled or swept
            try { await _local.SendAsync(r.Buffer, game, _cts.Token); } catch { /* dropped */ }
        }
    }

    /// <summary>Forgets flows idle for 2 minutes (a game that left; Simple Voice Chat keeps alive every few seconds).</summary>
    private async Task Sweep()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(30_000, _cts.Token); } catch { return; }
            var old = Environment.TickCount64 - 120_000;
            foreach (var (k, v) in _flows) if (v.last < old && _flows.TryRemove(k, out var gone)) gone.up.Dispose();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _local.Dispose();
        foreach (var v in _flows.Values) v.up.Dispose();
        _flows.Clear();
        Log.Info("voice relay stopped");
    }
}
