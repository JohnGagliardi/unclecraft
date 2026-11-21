using System.Diagnostics;

namespace McShared;

/// <summary>
/// macOS: device tokens live in the login Keychain (service "UncleCraft"); remembered.json only stores
/// "keychain:&lt;random id&gt;". Uses /usr/bin/security. The token is written through `security -i` on stdin, so it
/// never appears in a command line.
/// </summary>
public static class SecretStore
{
    private const string Service = "UncleCraft";
    private const string Prefix = "keychain:";
    private const int ItemNotFound = 44; // errSecItemNotFound, as `security` reports it

    public static string Protect(string token)
    {
        if (token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new InvalidOperationException("unexpected characters in device token");
        var account = Guid.NewGuid().ToString("N");
        Run(["-i"], $"add-generic-password -U -a {account} -s {Service} -l \"UncleCraft saved login\" -w {token}\n");
        if (Read(account) != token) throw new InvalidOperationException("Keychain did not store the saved login");
        return Prefix + account;
    }

    /// <summary>KeyNotFoundException / FormatException = definitely gone or corrupt; anything else (Deny, cancelled unlock, timeout) may work next time.</summary>
    public static string Unprotect(string stored)
    {
        if (!stored.StartsWith(Prefix)) throw new FormatException("not a Keychain reference");
        return Read(stored[Prefix.Length..]) ?? throw new KeyNotFoundException("saved login missing from Keychain");
    }

    public static void Delete(string stored)
    {
        if (!stored.StartsWith(Prefix)) return;
        try { Run(["delete-generic-password", "-a", stored[Prefix.Length..], "-s", Service], null); } catch { /* already gone */ }
    }

    /// <summary>The token, or null if the item doesn't exist. Throws for any other failure.</summary>
    private static string? Read(string account)
    {
        var (code, output) = Run(["find-generic-password", "-a", account, "-s", Service, "-w"], null);
        if (code == ItemNotFound) return null;
        if (code != 0) throw new InvalidOperationException($"Keychain read failed (security exit code {code})");
        return output.Trim();
    }

    private static (int code, string stdout) Run(string[] args, string? stdin)
    {
        var psi = new ProcessStartInfo("/usr/bin/security")
        {
            UseShellExecute = false, RedirectStandardInput = stdin != null, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        if (stdin != null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
        if (!p.WaitForExit(15_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* exited meanwhile */ }
            throw new TimeoutException("security did not finish within 15 seconds");
        }
        p.WaitForExit(); // let the output readers finish
        errors.GetAwaiter().GetResult();
        return (p.ExitCode, output.GetAwaiter().GetResult());
    }
}
