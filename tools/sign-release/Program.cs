using System.Security.Cryptography;
using McShared;

// sign-release <outDir> <version> <commit> <rid>=<file> ...   (private key PEM from env UNCLECRAFT_SIGNING_KEY)
//   writes <outDir>/release.json (size + sha256 of each file, keyed by rid) and release.json.sig.
// sign-release verify <dir> <publicKeyPemFile>
//   checks release.json.sig and that every listed file in <dir> matches its size and hash. Exit 0 = good.

if (args.Length == 3 && args[0] == "verify") return Verify(args[1], args[2]);
if (args.Length < 4) return Usage();

var (outDir, version, commit) = (args[0], args[1], args[2]);
var pem = Environment.GetEnvironmentVariable("UNCLECRAFT_SIGNING_KEY");
if (string.IsNullOrWhiteSpace(pem)) { Console.Error.WriteLine("UNCLECRAFT_SIGNING_KEY is not set"); return 1; }

var files = new Dictionary<string, ReleaseFile>();
foreach (var a in args[3..])
{
    var eq = a.IndexOf('=');
    if (eq <= 0) return Usage();
    var path = a[(eq + 1)..];
    if (!File.Exists(path)) { Console.Error.WriteLine($"missing file: {path}"); return 1; }
    files[a[..eq]] = new ReleaseFile(Path.GetFileName(path), new FileInfo(path).Length, Sha256(path));
}

var manifest = new ReleaseManifest("UncleCraft", version, commit, DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), files).ToJson();
string sig;
try { sig = ReleaseSig.Sign(manifest, pem); }
catch (Exception e) { Console.Error.WriteLine($"signing failed: {e.GetType().Name}"); return 1; } // no message: never echo key material
finally { pem = null; }

Directory.CreateDirectory(outDir);
File.WriteAllBytes(Path.Combine(outDir, "release.json"), manifest);
File.WriteAllText(Path.Combine(outDir, "release.json.sig"), sig);
Console.WriteLine($"signed release.json: UncleCraft {version}, {files.Count} file(s)");
return 0;

static int Verify(string dir, string pubFile)
{
    var manifest = File.ReadAllBytes(Path.Combine(dir, "release.json"));
    if (!ReleaseSig.Verify(manifest, File.ReadAllText(Path.Combine(dir, "release.json.sig")), File.ReadAllText(pubFile)))
    {
        Console.Error.WriteLine("BAD signature");
        return 1;
    }
    var m = ReleaseManifest.Parse(manifest);
    var ok = true;
    foreach (var (rid, f) in m.Files)
    {
        var path = Path.Combine(dir, f.Name);
        var good = File.Exists(path) && new FileInfo(path).Length == f.Size && Sha256(path) == f.Sha256;
        Console.WriteLine($"{(good ? "ok " : "BAD")} {rid} {f.Name}");
        ok &= good;
    }
    Console.WriteLine(ok ? $"verified {m.App} {m.Version} ({m.Commit})" : "verification FAILED");
    return ok ? 0 : 1;
}

static string Sha256(string path)
{
    using var s = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(s));
}

static int Usage()
{
    Console.Error.WriteLine("usage: sign-release <outDir> <version> <commit> <rid>=<file> ...   (key in env UNCLECRAFT_SIGNING_KEY)");
    Console.Error.WriteLine("       sign-release verify <dir> <publicKeyPemFile>");
    return 2;
}
