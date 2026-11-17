using System.Security.Cryptography;
using System.Text;

namespace McShared;

/// <summary>Windows: device tokens are encrypted with DPAPI for the current Windows user; the blob lives in remembered.json.</summary>
public static class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("mc-shared-device-v1");

    /// <summary>Returns what remembered.json stores (never the plain token).</summary>
    public static string Protect(string token) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));

    public static string Unprotect(string stored) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser));

    public static void Delete(string stored) { /* the blob is deleted with its remembered.json entry */ }
}
