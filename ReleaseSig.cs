using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace McShared;

/// <summary>One platform's file in a release: the asset name on the GitHub Release, its size and SHA-256 (hex).</summary>
public sealed record ReleaseFile(string Name, long Size, string Sha256);

/// <summary>
/// release.json: what a GitHub Release holds, signed by the workflow (tools/sign-release) and checked by the app.
/// Files is keyed by runtime id (win-x64, osx-arm64, osx-x64).
/// </summary>
public sealed record ReleaseManifest(string App, string Version, string Commit, string Issued, IReadOnlyDictionary<string, ReleaseFile> Files)
{
    public static ReleaseManifest Parse(byte[] json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var files = new Dictionary<string, ReleaseFile>();
        foreach (var p in r.GetProperty("files").EnumerateObject())
            files[p.Name] = new ReleaseFile(p.Value.GetProperty("name").GetString()!, p.Value.GetProperty("size").GetInt64(),
                p.Value.GetProperty("sha256").GetString()!);
        return new ReleaseManifest(r.GetProperty("app").GetString()!, r.GetProperty("version").GetString()!,
            r.GetProperty("commit").GetString()!, r.GetProperty("issued").GetString()!, files);
    }

    public byte[] ToJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("app", App);
            w.WriteString("version", Version);
            w.WriteString("commit", Commit);
            w.WriteString("issued", Issued);
            w.WriteStartObject("files");
            foreach (var (rid, f) in Files)
            {
                w.WriteStartObject(rid);
                w.WriteString("name", f.Name);
                w.WriteNumber("size", f.Size);
                w.WriteString("sha256", f.Sha256);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }
}

/// <summary>
/// Release signatures: ECDSA P-256 over SHA-256 of the exact release.json bytes, IEEE P1363 format, base64 text in
/// release.json.sig. The app verifies with the public key built in (UpdateKey.pub.txt); only the workflow signs.
/// </summary>
public static class ReleaseSig
{
    public static bool Verify(byte[] manifest, string sigBase64, string publicKeyPem)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            if (key.KeySize != 256) return false;
            return key.VerifyData(manifest, Convert.FromBase64String(sigBase64.Trim()), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch { return false; }
    }

    /// <summary>For tools/sign-release only. privateKeyPem is PKCS#8 ("BEGIN PRIVATE KEY").</summary>
    public static string Sign(byte[] manifest, string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        if (key.KeySize != 256) throw new CryptographicException("signing key is not P-256");
        return Convert.ToBase64String(key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <summary>The public key embedded as UpdateKey.pub.txt, or null while it's still the placeholder (updates off).</summary>
    public static string? BuiltInPublicKey()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("UpdateKey.pub.txt");
            if (s == null) return null;
            var text = new StreamReader(s).ReadToEnd();
            return text.Contains("-----BEGIN PUBLIC KEY-----") ? text : null;
        }
        catch { return null; }
    }

    /// <summary>"0.2.57", "0.2.0-dev+abc" -> major.minor.build; null if it doesn't parse.</summary>
    public static Version? ParseVersion(string s)
    {
        var core = s.Trim().Split('+', '-')[0];
        if (!System.Version.TryParse(core, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    public static bool IsDev(string informationalVersion) => informationalVersion.Contains("-dev");
}
