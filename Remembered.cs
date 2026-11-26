using System.Text.Json;

namespace McShared;

/// <summary>
/// A remembered login: the host's device token, kept by SecretStore (Windows: DPAPI blob; macOS: Keychain item).
/// Never a password.
/// </summary>
public sealed class RememberedLogin
{
    public string Person { get; set; } = "";
    /// <summary>Normalized fingerprint of the host it belongs to; only offered to that host.</summary>
    public string Host { get; set; } = "";
    public string DeviceId { get; set; } = "";
    /// <summary>SecretStore reference: DPAPI blob (Windows) or "keychain:id" (macOS).</summary>
    public string ProtectedToken { get; set; } = "";
    public DateTime SavedAt { get; set; }
    public DateTime? LastPlayed { get; set; }
    /// <summary>The host lets this person use the test server (from the last login answer); null = not known yet.</summary>
    public bool? TestServer { get; set; }
}

public static class RememberedStore
{
    private static string FilePath => Path.Combine(Paths.Root, "remembered.json");

    public static List<RememberedLogin> All()
    {
        try { return JsonSerializer.Deserialize<List<RememberedLogin>>(File.ReadAllText(FilePath)) ?? []; } catch { return []; }
    }

    public static List<RememberedLogin> ForHost(string host) =>
        All().Where(r => r.Host == host).OrderBy(r => r.Person, StringComparer.OrdinalIgnoreCase).ToList();

    private static void Save(List<RememberedLogin> list)
    {
        Directory.CreateDirectory(Paths.Root);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Saves a device token. If the OS secret store fails, logs it and carries on (the login itself still works).</summary>
    public static void Add(string host, string person, string deviceId, string token)
    {
        string stored;
        try { stored = SecretStore.Protect(token); }
        catch (Exception e) { Log.Error($"could not save the login for {person} in the OS secret store", e); return; }
        var list = All();
        var old = list.Where(r => r.Host == host && r.Person.Equals(person, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var r in old) SecretStore.Delete(r.ProtectedToken);
        list.RemoveAll(old.Contains);
        list.Add(new RememberedLogin { Host = host, Person = person, DeviceId = deviceId, ProtectedToken = stored, SavedAt = DateTime.Now,
            LastPlayed = old.FirstOrDefault()?.LastPlayed, TestServer = old.FirstOrDefault()?.TestServer });
        Save(list);
    }

    /// <summary>Records whether the last login answer carried "testServer" (saves only when it changed).</summary>
    public static void SetTestServer(string host, string person, bool allowed)
    {
        var list = All();
        var hits = list.Where(r => r.Host == host && r.Person.Equals(person, StringComparison.OrdinalIgnoreCase) && r.TestServer != allowed).ToList();
        if (hits.Count == 0) return;
        foreach (var r in hits) r.TestServer = allowed;
        Save(list);
    }

    public static void Touch(string host, string person)
    {
        var list = All();
        foreach (var r in list.Where(r => r.Host == host && r.Person.Equals(person, StringComparison.OrdinalIgnoreCase))) r.LastPlayed = DateTime.Now;
        Save(list);
    }

    /// <summary>The host knows the person's real name; fix a saved entry stored under a placeholder or old spelling.</summary>
    public static void Rename(string host, string from, string to)
    {
        if (from.Equals(to, StringComparison.Ordinal)) return;
        var list = All();
        var entry = list.FirstOrDefault(r => r.Host == host && r.Person.Equals(from, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return;
        foreach (var dup in list.Where(r => r != entry && r.Host == host && r.Person.Equals(to, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            SecretStore.Delete(dup.ProtectedToken);
            list.Remove(dup);
        }
        entry.Person = to;
        Save(list);
    }

    public static void Remove(string host, string person)
    {
        var list = All();
        foreach (var r in list.Where(r => r.Host == host && r.Person.Equals(person, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            SecretStore.Delete(r.ProtectedToken);
            list.Remove(r);
        }
        Save(list);
    }

    public static string Token(RememberedLogin r) => SecretStore.Unprotect(r.ProtectedToken);
}
