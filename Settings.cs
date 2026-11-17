using System.Text.Json;

namespace McShared;

public sealed class Settings
{
    public string Server { get; set; } = "";
    public int Port { get; set; } = 25591;
    public string Fingerprint { get; set; } = "";
    public string Player1 { get; set; } = "";
    public string Player2 { get; set; } = "";
    public bool SecondPlayer { get; set; }
    public bool SoundOn { get; set; } = true;

    // Only these fields are stored here. Passwords and Minecraft tokens never touch the disk;
    // remembered logins live DPAPI-encrypted in remembered.json (see RememberedStore).
    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.Settings)) ?? new(); } catch { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Paths.Root);
        File.WriteAllText(Paths.Settings, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
