using System.Text.Json.Nodes;

namespace McShared;

/// <summary>
/// Classic WinForms window, used when the WebView2 runtime is missing. All logic lives in AppCore: this form sends it
/// the same JSON requests the pixel page does and shows its replies and events.
/// </summary>
public sealed class MainForm : Form, IGameWindows, IUpdateHost
{
    public const string AppName = "UncleCraft";

    private readonly AppCore _core;
    private readonly TextBox _server = new() { Width = 260 };
    private readonly TextBox _fingerprint = new() { Width = 420 };
    private readonly FlowLayoutPanel _saved = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
    private readonly TextBox _name1 = new() { Width = 200 };
    private readonly TextBox _pass1 = new() { Width = 200, UseSystemPasswordChar = true };
    private readonly Label _invitePassLabel = new() { Text = "New password", AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly TextBox _invitePass = new() { Width = 200, UseSystemPasswordChar = true, PlaceholderText = "optional, 10+ characters" };
    private readonly CheckBox _remember1 = new() { Text = "Remember me on this PC", AutoSize = true, Checked = true };
    private readonly Button _play = new() { Text = "Log in and play", Width = 160, Height = 34 };
    private readonly Button _cancel = new() { Text = "Cancel", Width = 90, Height = 34, Enabled = false };
    private readonly ComboBox _count = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
    private readonly CheckBox _guest = new() { Text = "Player 2: guest of player 1 (no account needed)", AutoSize = true, Checked = true, Visible = false };
    private readonly Label _secondHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly ProgressBar _bar = new() { Width = 420, Height = 14 };
    private readonly Label _status = new() { AutoSize = false, Width = 440, Height = 64 };
    private readonly Dictionary<string, Label> _rows = new(StringComparer.OrdinalIgnoreCase); // person -> error label
    private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> _pending = [];
    private List<string> _people = [];
    private HashSet<string> _testPeople = new(StringComparer.OrdinalIgnoreCase); // saved people the host allows on the test server
    private readonly List<(JsonObject choice, string name)> _picked = []; // split-screen: picks wait for the last one
    private int _nextId;
    private int _players; // in the current launch
    private bool _busy;
    private bool _testMode;
    private HashSet<string> _features = []; // from the broker's status: "guests"

    public MainForm()
    {
        _core = new AppCore(Post, this, this);
        Text = Platform.Title;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { /* keep default */ }
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 10f);

        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(14), Dock = DockStyle.Fill };
        void Row(string label, Control c) { t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }); t.Controls.Add(c); }
        void Wide(Control c) { t.Controls.Add(c); t.SetColumnSpan(c, 2); }
        Label Heading(string s) => new() { Text = s, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 12, 3, 3) };

        Wide(Heading("Host's server (ask the host once)"));
        Row("Address", _server);
        Row("Fingerprint", _fingerprint);
        Wide(Heading("Who's playing? Click your name to play."));
        _count.Items.AddRange(["1 player", "2 players (split-screen)", "3 players (split-screen)", "4 players (split-screen)"]);
        _count.SelectedIndex = 0;
        Row("Players on this PC", _count);
        Wide(_guest);
        Wide(_secondHint);
        Wide(_saved);
        Wide(new Label { Text = "New here? Log in with your name and password:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        Row("Name", _name1);
        Row("Password", _pass1);
        t.Controls.Add(_invitePassLabel); t.Controls.Add(_invitePass); // invite code in the name box: pick your own password
        Wide(_remember1);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        buttons.Controls.Add(_play);
        buttons.Controls.Add(_cancel);
        Wide(buttons);
        Wide(_bar);
        Wide(_status);
        Wide(new Label { Text = AppCore.VersionText, AutoSize = true, Anchor = AnchorStyles.Right, ForeColor = SystemColors.GrayText, Font = new Font(Font.FontFamily, 8f) });
        Controls.Add(t);

        _count.SelectedIndexChanged += async (_, _) =>
        {
            _picked.Clear(); UpdateHint(); RenderSaved();
            try { await Call("setMode", new JsonObject { ["players"] = Count }); } catch { /* only a preference */ }
        };
        _guest.CheckedChanged += (_, _) => UpdateHint();
        _name1.TextChanged += (_, _) => UpdateInvite();
        _play.Click += async (_, _) =>
        {
            if (_name1.Text.Trim().Length == 0) { Status("Enter your name and password (or an invite code in the name box), or click a saved player above."); return; }
            // Classic window: an invite code typed into the name box with an empty password redeems the invite.
            if (_pass1.Text.Length == 0 && BrokerClient.NormalizeInvite(_name1.Text) is { } code)
            {
                var pw = _invitePass.Text;
                if (pw.Length > 0 && pw.Length < 10) { Status("Your new password needs at least 10 characters (or leave it empty)."); _invitePass.Focus(); return; }
                var invite = new JsonObject { ["kind"] = "invite", ["code"] = code };
                if (pw.Length > 0) invite["newPassword"] = pw;
                await Choose(invite, "Invite " + code[..4]);
                return;
            }
            var name = _name1.Text.Trim();
            await Choose(new JsonObject { ["kind"] = "password", ["person"] = name, ["password"] = _pass1.Text, ["remember"] = _remember1.Checked }, name);
        };
        _cancel.Click += async (_, _) =>
        {
            _cancel.Enabled = false;
            Status("Stopping...");
            try { await Call("cancel"); } catch { /* Play reports the outcome */ }
        };
        AcceptButton = _play;
        Load += async (_, _) => await Init();
        FormClosing += (_, e) =>
        {
            if (_core.Running > 0 && MessageBox.Show("Minecraft is still running. Closing this window also disconnects you from the server (the game connection runs through UncleCraft).\n\nClose anyway?",
                    AppName, MessageBoxButtons.YesNo) == DialogResult.No) e.Cancel = true;
        };
        UpdateInvite();
        UpdateHint();
        RenderSaved();
        Status("Enter the host's address and fingerprint once, then click a saved player or log in.");
    }

    private async Task Init()
    {
        try
        {
            var d = await Call("init");
            var s = d?["settings"];
            _server.Text = s?["server"]?.GetValue<string>() ?? "";
            _fingerprint.Text = s?["fingerprint"]?.GetValue<string>() ?? "";
            _name1.Text = s?["player1"]?.GetValue<string>() ?? "";
            _count.SelectedIndex = Math.Clamp(s?["playerCount"]?.GetValue<int>() ?? 1, 1, Settings.MaxPlayers) - 1;
            SetSaved(d?["saved"]);
            SetFeatures(d?["features"]);
            if (d?["update"]?["notice"]?.GetValue<string>() is { } notice) Status(notice);
            if (d?["configured"]?.GetValue<bool>() == true)
            {
                try { SetFeatures((await Call("status"))?["features"]); } catch (UiError) { /* host offline: no new options */ }
            }
        }
        catch (UiError e) { Status(e.Message); }
    }

    /// <summary>The new options appear only on a broker that lists them, so an older host looks exactly as before.</summary>
    private void SetFeatures(JsonNode? features)
    {
        _features = BrokerClient.Features(new JsonObject { ["features"] = features?.DeepClone() });
        UpdateHint();
    }

    // ---------------- talking to AppCore ----------------

    /// <summary>Sends a request the way the pixel page does and waits for its reply (errors come back as UiError).</summary>
    private Task<JsonNode?> Call(string type, JsonObject? args = null)
    {
        var m = args ?? new JsonObject();
        var id = ++_nextId;
        m["id"] = id; m["type"] = type;
        var tcs = new TaskCompletionSource<JsonNode?>();
        _pending[id] = tcs;
        _ = _core.HandleMessage(m.ToJsonString());
        return tcs.Task;
    }

    private void Post(string json)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => OnMessage(json)); } catch (InvalidOperationException) { /* closing */ }
    }

    private void OnMessage(string json)
    {
        var m = JsonNode.Parse(json)!;
        if (m["id"]?.GetValue<int>() is { } id and > 0 && _pending.Remove(id, out var tcs))
        {
            if (m["ok"]?.GetValue<bool>() == true) tcs.SetResult(m["data"]);
            else
            {
                var err = m["error"];
                tcs.SetException(new UiError(err?["code"]?.GetValue<string>() ?? "error",
                    err?["message"]?.GetValue<string>() ?? "Something went wrong.", err?["person"]?.GetValue<string>()));
            }
            return;
        }
        switch (m["event"]?.GetValue<string>())
        {
            case "status": Status(m["text"]?.GetValue<string>() ?? ""); break;
            case "progress" when Str(m, "stage") == "update": // the UncleCraft download: one file, show bytes (in KB)
                Progress((int)((m["doneBytes"]?.GetValue<long>() ?? 0) / 1024), (int)((m["totalBytes"]?.GetValue<long>() ?? 0) / 1024)); break;
            case "progress": Progress((int)(m["doneFiles"]?.GetValue<long>() ?? 0), (int)(m["totalFiles"]?.GetValue<long>() ?? 0)); break;
            case "update": if (m["notice"]?.GetValue<string>() is { } notice) Status(notice); break;
            case "saved": _pass1.Clear(); _invitePass.Clear(); SetSaved(m["saved"]); break; // logins are done
            case "closed": Status($"{Str(m, "person")}'s game closed; account {Str(m, "account")} is free again."); break;
            case "phase":
                switch (m["phase"]?.GetValue<string>())
                {
                    case "update": Status($"Updating UncleCraft to v{Str(m, "version")}..."); break;
                    case "update-apply": _cancel.Enabled = false; Status("Updating UncleCraft: starting the new version..."); break;
                    case "login": Status($"Logging in {Str(m, "person")}..."); break;
                    case "test": Status("Starting the test server... This takes 30-60 seconds."); break;
                    case "starting": _cancel.Enabled = false; Status("Starting Minecraft..."); break;
                    case "booting":
                        Status(_players > 1 ? $"The {_players} games are starting; they will be arranged on this screen." : "Minecraft is starting and will join the server.");
                        break;
                    case "ingame": Status("Minecraft is running. Have fun!"); break;
                }
                break;
        }
    }

    private static string Str(JsonNode m, string key) => m[key]?.GetValue<string>() ?? "";

    // ---------------- form ----------------

    private void UpdateInvite() => _invitePass.Visible = _invitePassLabel.Visible = BrokerClient.NormalizeInvite(_name1.Text) != null;

    private int Count => _count.SelectedIndex + 1;
    private bool Split => Count > 1;
    /// <summary>"The rest are guests": player 1 is picked, players 2-N play as guests of player 1.</summary>
    private bool Guests => Split && _features.Contains("guests") && _guest.Checked;

    private void UpdateHint()
    {
        _guest.Visible = Split && _features.Contains("guests");
        _guest.Text = Count <= 2 ? "Player 2: guest of player 1 (no account needed)" : $"Players 2-{Count}: guests of player 1 (no accounts needed)";
        var next = _picked.Count + 1;
        _secondHint.Text = !Split ? ""
            : Guests ? (Count == 2 ? "Click player 1; a guest plays next to them." : $"Click player 1; {Count - 1} guests play next to them.")
            : _picked.Count == 0 ? $"Click player 1, then players 2-{Count} (or log in a new player below)."
            : $"Picked: {string.Join(", ", _picked.Select(p => p.name))}. Now click player {next}, or log in a new player below.";
        _secondHint.Visible = Split;
        _play.Text = Split && !Guests && _picked.Count > 0 ? $"Log in as player {next}" + (next == Count ? " and play" : "") : "Log in and play";
    }

    private void SetSaved(JsonNode? saved)
    {
        _people = saved?.AsArray().Select(n => n?["person"]?.GetValue<string>() ?? "").Where(p => p.Length > 0).ToList() ?? [];
        _testPeople = new(saved?.AsArray().Where(n => n?["testServer"]?.GetValue<bool?>() == true)
            .Select(n => n!["person"]?.GetValue<string>() ?? "") ?? [], StringComparer.OrdinalIgnoreCase);
        RenderSaved();
    }

    private void RenderSaved()
    {
        _saved.SuspendLayout();
        _saved.Controls.Clear();
        _rows.Clear();
        if (_people.Count == 0)
            _saved.Controls.Add(new Label { Text = "No saved players on this PC yet.", AutoSize = true, ForeColor = SystemColors.GrayText });
        foreach (var person in _people)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2), Enabled = !_busy };
            var pickedAt = _picked.FindIndex(p => p.name.Equals(person, StringComparison.OrdinalIgnoreCase));
            var picked = pickedAt >= 0;
            var play = new Button { Text = picked ? $"Player {pickedAt + 1}: {person}" : $"Play as {person}", AutoSize = true, MinimumSize = new Size(180, 34) };
            if (picked) play.BackColor = Color.FromArgb(214, 245, 214);
            play.Click += async (_, _) => await Choose(new JsonObject { ["kind"] = "saved", ["person"] = person }, person);
            var forget = new LinkLabel { Text = "Forget", AutoSize = true, Margin = new Padding(8, 9, 0, 0) };
            forget.LinkClicked += async (_, _) => await Forget(person);
            var error = new Label { AutoSize = true, ForeColor = Color.Firebrick, Margin = new Padding(8, 9, 0, 0), MaximumSize = new Size(260, 0) };
            row.Controls.Add(play);
            if (!Split && _testPeople.Contains(person)) // the host's test server: one player only, never the live world
            {
                var test = new Button { Text = "Test server", AutoSize = true, MinimumSize = new Size(0, 34), BackColor = Color.FromArgb(241, 91, 181), ForeColor = Color.White };
                test.Click += async (_, _) =>
                {
                    if (_busy) return;
                    foreach (var err in _rows.Values) err.Text = "";
                    await Play([new JsonObject { ["kind"] = "saved", ["person"] = person }], "test");
                };
                row.Controls.Add(test);
            }
            row.Controls.Add(forget);
            row.Controls.Add(error);
            _saved.Controls.Add(row);
            _rows[person] = error;
        }
        _saved.ResumeLayout();
    }

    /// <summary>One click = play. In split-screen each click picks the next player and the last pick launches them all;
    /// with guests ticked, clicking player 1 launches them with guests on the other screens.</summary>
    private async Task Choose(JsonObject c, string name)
    {
        if (_busy) return;
        foreach (var err in _rows.Values) err.Text = "";
        if (!Split) { await Play([c]); return; }
        if (Guests)
        {
            _picked.Clear();
            await Play([c, .. Enumerable.Range(0, Count - 1).Select(_ => new JsonObject { ["kind"] = "guest" })]);
            return;
        }
        var at = _picked.FindIndex(p => p.name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
        {
            _picked.RemoveAt(at);
            UpdateHint(); RenderSaved();
            Status($"Unpicked {name}. Click player {_picked.Count + 1}.");
            return;
        }
        _picked.Add((c, name));
        if (c["kind"]?.GetValue<string>() == "password") { _name1.Clear(); _pass1.Clear(); } // free the form for the next player
        if (_picked.Count < Count)
        {
            UpdateHint(); RenderSaved();
            Status($"Player {_picked.Count} is {name}. Now click player {_picked.Count + 1}.");
            return;
        }
        var players = _picked.Select(p => p.choice).ToList();
        _picked.Clear();
        UpdateHint(); RenderSaved();
        await Play(players);
    }

    private async Task Forget(string person)
    {
        if (_busy || MessageBox.Show($"Forget the saved login for {person} on this PC?", AppName, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        if (_picked.RemoveAll(p => p.name.Equals(person, StringComparison.OrdinalIgnoreCase)) > 0) { UpdateHint(); RenderSaved(); }
        Status($"Forgetting {person}...");
        try
        {
            var d = await Call("forget", new JsonObject { ["person"] = person });
            SetSaved(d?["saved"]);
            Status($"Forgot {person} on this PC.");
        }
        catch (UiError e) { Status(e.Message); }
    }

    private void Status(string s)
    {
        if (_testMode) s = "TEST MODE: Microsoft sign-in is off (this PC only).\n" + s;
        _status.Text = s;
    }

    private void Progress(int done, int total)
    {
        _bar.Maximum = Math.Max(1, total);
        _bar.Value = Math.Min(done, _bar.Maximum);
    }

    private async Task Play(List<JsonObject> chosen, string? target = null)
    {
        _busy = true;
        SetEnabled(false);
        try
        {
            // saves the address and fingerprint (after checking the host answers) and tells us about test mode
            var setup = await Call("setup", new JsonObject { ["server"] = _server.Text, ["fingerprint"] = _fingerprint.Text });
            _testMode = setup?["testMode"]?.GetValue<bool>() ?? false;
            Text = _testMode ? $"{Platform.Title} - TEST MODE: Microsoft sign-in is off" : Platform.Title;
            _fingerprint.Text = setup?["settings"]?["fingerprint"]?.GetValue<string>() ?? _fingerprint.Text;
            SetSaved(setup?["saved"]);
            SetFeatures(setup?["features"]);

            var players = new JsonArray([.. chosen]);
            _players = players.Count;
            _cancel.Enabled = true; // until the "starting" phase
            var req = new JsonObject { ["players"] = players };
            if (target != null) { req["target"] = target; Status("TEST SERVER: getting ready..."); }
            await Call("play", req);
        }
        catch (UiError e)
        {
            Status(e.Message);
            if (e.Code == "app_not_approved")
                MessageBox.Show(e.Message + "\n\nThis is on the host's side; nothing to fix on your PC.", AppName);
            if (e.Code == "java_failed")
                MessageBox.Show(e.Message, AppName); // a "closed" event may overwrite the status line when it gives the account back
            if (e.Code == "device_token_invalid" && e.Person is { } p && !p.StartsWith("Invite ", StringComparison.Ordinal))
            {
                _name1.Text = p; _pass1.Clear(); _pass1.Focus(); // fall back to the password form
            }
            else if (e.Code != "cancelled" && e.Person != null && _rows.TryGetValue(e.Person, out var error))
            {
                error.Text = e.Message; // show the reason on that player's button
            }
        }
        finally { _busy = false; _cancel.Enabled = false; SetEnabled(true); Progress(0, 1); }
    }

    private void SetEnabled(bool on)
    {
        _play.Enabled = on;
        _count.Enabled = on;
        _guest.Enabled = on;
        _server.Enabled = on;
        _fingerprint.Enabled = on;
        foreach (Control row in _saved.Controls) row.Enabled = on;
    }

    // ---------------- IGameWindows ----------------

    /// <summary>The working area of the screen this window is on: games are sized and tiled there.</summary>
    private Rectangle LauncherArea() => (Screen.FromHandle(Handle) ?? Screen.PrimaryScreen)!.WorkingArea;

    public (int width, int height)? SplitSize(int index, int count) => WindowTiler.LaunchSize(index, count, LauncherArea());

    public Task<Rectangle?> SplitTile(int index, int count) => Task.FromResult(WindowTiler.TileRect(index, count, LauncherArea()));

    public Task Arrange(IReadOnlyList<GameProcess> games) => WindowTiler.Tile(games, LauncherArea());

    public void Handover(bool toNewVersion) => UpdateHandover.Run(this, toNewVersion);
}
