using System.Text.Json;
using System.Text.Json.Serialization;

namespace McShared;

public sealed class Settings
{
    public const int MaxPlayers = 4;

    public string Server { get; set; } = "";
    public int Port { get; set; } = 25591;
    public string Fingerprint { get; set; } = "";
    public string Player1 { get; set; } = "";
    /// <summary>Players on this PC (1 = one game, 2-4 = split-screen).</summary>
    public int PlayerCount { get; set; } = 1;
    /// <summary>Who played on screens 2-4 last time: a person's name, or "guest".</summary>
    public List<string> Players { get; set; } = [];
    public bool SoundOn { get; set; } = true;

    // Before 4-player split-screen: "SecondPlayer": true and "Player2": "<name>". Read once, never written again.
    [JsonPropertyName("SecondPlayer"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacySecondPlayer { get; set; }
    [JsonPropertyName("Player2"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPlayer2 { get; set; }

    // Only these fields are stored here. Passwords and Minecraft tokens never touch the disk;
    // remembered logins live DPAPI-encrypted in remembered.json (see RememberedStore).
    public static Settings Load()
    {
        Settings s;
        try { s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.Settings)) ?? new(); } catch { s = new(); }
        s.Migrate();
        return s;
    }

    private void Migrate()
    {
        Players ??= [];
        if (LegacySecondPlayer == true && PlayerCount <= 1) PlayerCount = 2;
        if (!string.IsNullOrWhiteSpace(LegacyPlayer2) && Players.Count == 0) Players = [LegacyPlayer2.Trim()];
        LegacySecondPlayer = null; LegacyPlayer2 = null; // the next Save drops the old fields
        PlayerCount = Math.Clamp(PlayerCount, 1, MaxPlayers);
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.Root);
        File.WriteAllText(Paths.Settings, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
