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
    private readonly CheckBox _second = new() { Text = "Two players on this PC (split-screen)", AutoSize = true };
    private readonly CheckBox _guest = new() { Text = "Player 2: guest of player 1 (no account needed)", AutoSize = true, Checked = true, Visible = false };
    private readonly Label _secondHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly ProgressBar _bar = new() { Width = 420, Height = 14 };
    private readonly Label _status = new() { AutoSize = false, Width = 440, Height = 64 };
    private readonly Dictionary<string, Label> _rows = new(StringComparer.OrdinalIgnoreCase); // person -> error label
    private readonly Dictionary<int, TaskCompletionSource<JsonNode?>> _pending = [];
    private List<string> _people = [];
    private (JsonObject choice, string name)? _pendingP1; // split-screen: first pick waits for the second
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
        Wide(_second);
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
        Controls.Add(t);

        _second.CheckedChanged += async (_, _) =>
        {
            _pendingP1 = null; UpdateHint(); RenderSaved();
            try { await Call("setMode", new JsonObject { ["split"] = _second.Checked }); } catch { /* only a preference */ }
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
            _second.Checked = s?["split"]?.GetValue<bool>() ?? false;
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
                    case "starting": _cancel.Enabled = false; Status("Starting Minecraft..."); break;
                    case "booting":
                        Status(_players > 1 ? "Both games are starting; they will be placed side by side." : "Minecraft is starting and will join the server.");
                        break;
                    case "ingame": Status("Minecraft is running. Have fun!"); break;
                }
                break;
        }
    }

    private static string Str(JsonNode m, string key) => m[key]?.GetValue<string>() ?? "";

    // ---------------- form ----------------

    private void UpdateInvite() => _invitePass.Visible = _invitePassLabel.Visible = BrokerClient.NormalizeInvite(_name1.Text) != null;

    private bool GuestP2 => _second.Checked && _features.Contains("guests") && _guest.Checked;

    private void UpdateHint()
    {
        _guest.Visible = _second.Checked && _features.Contains("guests");
        _secondHint.Text = !_second.Checked ? ""
            : GuestP2 ? "Click player 1; a guest plays next to them."
            : _pendingP1 == null ? "Click player 1, then player 2 (or log in a new player below)."
            : $"Player 1: {_pendingP1.Value.name}. Now click player 2, or log in a new player below.";
        _secondHint.Visible = _second.Checked;
        _play.Text = _second.Checked && _pendingP1 != null ? "Log in as player 2 and play" : "Log in and play";
    }

    private void SetSaved(JsonNode? saved)
    {
        _people = saved?.AsArray().Select(n => n?["person"]?.GetValue<string>() ?? "").Where(p => p.Length > 0).ToList() ?? [];
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
            var picked = _pendingP1?.name.Equals(person, StringComparison.OrdinalIgnoreCase) == true;
            var play = new Button { Text = picked ? $"Player 1: {person}" : $"Play as {person}", AutoSize = true, MinimumSize = new Size(180, 34) };
            if (picked) play.BackColor = Color.FromArgb(214, 245, 214);
            play.Click += async (_, _) => await Choose(new JsonObject { ["kind"] = "saved", ["person"] = person }, person);
            var forget = new LinkLabel { Text = "Forget", AutoSize = true, Margin = new Padding(8, 9, 0, 0) };
            forget.LinkClicked += async (_, _) => await Forget(person);
            var error = new Label { AutoSize = true, ForeColor = Color.Firebrick, Margin = new Padding(8, 9, 0, 0), MaximumSize = new Size(260, 0) };
            row.Controls.Add(play);
            row.Controls.Add(forget);
            row.Controls.Add(error);
            _saved.Controls.Add(row);
            _rows[person] = error;
        }
        _saved.ResumeLayout();
    }

    /// <summary>One click = play. With two players ticked, the first click picks player 1 and the second launches both.</summary>
    private async Task Choose(JsonObject c, string name)
    {
        if (_busy) return;
        foreach (var err in _rows.Values) err.Text = "";
        if (!_second.Checked) { await Play(c, null); return; }
        if (GuestP2) { _pendingP1 = null; await Play(c, new JsonObject { ["kind"] = "guest" }); return; }
        if (_pendingP1 == null)
        {
            _pendingP1 = (c, name);
            if (c["kind"]?.GetValue<string>() == "password") { _name1.Clear(); _pass1.Clear(); } // free the form for player 2
            UpdateHint(); RenderSaved();
            Status($"Player 1 is {name}. Now click player 2.");
            return;
        }
        var p1 = _pendingP1.Value;
        _pendingP1 = null;
        UpdateHint(); RenderSaved();
        if (p1.name.Equals(name, StringComparison.OrdinalIgnoreCase)) { Status("Unpicked. Click player 1 again."); return; }
        await Play(p1.choice, c);
    }

    private async Task Forget(string person)
    {
        if (_busy || MessageBox.Show($"Forget the saved login for {person} on this PC?", AppName, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        if (_pendingP1?.name.Equals(person, StringComparison.OrdinalIgnoreCase) == true) { _pendingP1 = null; UpdateHint(); }
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

    private async Task Play(JsonObject p1, JsonObject? p2)
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

            var players = new JsonArray { p1 };
            if (p2 != null) players.Add(p2);
            _players = players.Count;
            _cancel.Enabled = true; // until the "starting" phase
            await Call("play", new JsonObject { ["players"] = players });
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
        _second.Enabled = on;
        _guest.Enabled = on;
        _server.Enabled = on;
        _fingerprint.Enabled = on;
        foreach (Control row in _saved.Controls) row.Enabled = on;
    }

    // ---------------- IGameWindows ----------------

    public (int width, int height)? SplitSize()
    {
        var area = (Screen.FromHandle(Handle) ?? Screen.PrimaryScreen)!.WorkingArea;
        return (area.Width / 2, area.Height);
    }

    public Task Arrange(IReadOnlyList<GameProcess> games) =>
        games.Count == 2 ? WindowTiler.TileSideBySide(games[0].Proc, games[1].Proc) : Task.CompletedTask;

    public void Handover(bool toNewVersion) => UpdateHandover.Run(this, toNewVersion);
}
