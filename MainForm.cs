using System.Diagnostics;

namespace McShared;

public sealed class MainForm : Form
{
    public const string AppName = "UncleCraft";

    private readonly Settings _s = Settings.Load();
    private readonly TextBox _server = new() { Width = 260 };
    private readonly TextBox _fingerprint = new() { Width = 420 };
    private readonly FlowLayoutPanel _saved = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
    private readonly TextBox _name1 = new() { Width = 200 };
    private readonly TextBox _pass1 = new() { Width = 200, UseSystemPasswordChar = true };
    private readonly CheckBox _remember1 = new() { Text = "Remember me on this PC", AutoSize = true, Checked = true };
    private readonly Button _play = new() { Text = "Log in and play", Width = 160, Height = 34 };
    private readonly CheckBox _second = new() { Text = "Two players on this PC (split-screen)", AutoSize = true };
    private readonly Label _secondHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly ProgressBar _bar = new() { Width = 420, Height = 14 };
    private readonly Label _status = new() { AutoSize = false, Width = 440, Height = 64 };
    private readonly Dictionary<string, (Button button, Label error)> _rows = new(StringComparer.OrdinalIgnoreCase);
    private PlayerChoice? _pendingP1; // split-screen: first pick waits for the second
    private int _running;
    private bool _busy;
    private bool _testMode;

    public MainForm()
    {
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
        Wide(_secondHint);
        Wide(_saved);
        Wide(new Label { Text = "New here? Log in with your name and password:", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
        Row("Name", _name1);
        Row("Password", _pass1);
        Wide(_remember1);
        Wide(_play);
        Wide(_bar);
        Wide(_status);
        Controls.Add(t);

        _server.Text = _s.Server;
        _fingerprint.Text = _s.Fingerprint;
        _name1.Text = _s.Player1;
        _second.Checked = _s.SecondPlayer;
        _second.CheckedChanged += (_, _) => { _pendingP1 = null; _s.SecondPlayer = _second.Checked; UpdateHint(); RefreshSaved(); };
        _fingerprint.Leave += (_, _) => RefreshSaved();
        _play.Click += async (_, _) =>
        {
            if (_name1.Text.Trim().Length == 0) { Status("Enter your name and password (or an invite code in the name box), or click a saved player above."); return; }
            // Classic window: an invite code typed into the name box with an empty password redeems the invite.
            if (_pass1.Text.Length == 0 && BrokerClient.NormalizeInvite(_name1.Text) is { } code) { await Choose(new InviteChoice(code)); return; }
            await Choose(new PasswordChoice(_name1.Text.Trim(), _pass1.Text, _remember1.Checked));
        };
        AcceptButton = _play;
        FormClosing += (_, e) =>
        {
            if (_running > 0 && MessageBox.Show("Minecraft is still running. Closing this window also disconnects you from the server (the game connection runs through UncleCraft).\n\nClose anyway?",
                    AppName, MessageBoxButtons.YesNo) == DialogResult.No) e.Cancel = true;
        };
        UpdateHint();
        RefreshSaved();
        Status("Enter the host's address and fingerprint once, then click a saved player or log in.");
    }

    /// <summary>Host identity for remembered logins = the pinned fingerprint.</summary>
    private string? HostKey()
    {
        try { return BrokerClient.NormalizeFingerprint(_fingerprint.Text); } catch { return null; }
    }

    private void UpdateHint()
    {
        _secondHint.Text = !_second.Checked ? ""
            : _pendingP1 == null ? "Click player 1, then player 2 (or log in a new player below)."
            : $"Player 1: {_pendingP1.Name}. Now click player 2, or log in a new player below.";
        _secondHint.Visible = _second.Checked;
        _play.Text = _second.Checked && _pendingP1 != null ? "Log in as player 2 and play" : "Log in and play";
    }

    private void RefreshSaved()
    {
        _saved.SuspendLayout();
        _saved.Controls.Clear();
        _rows.Clear();
        var key = HostKey();
        var list = key == null ? [] : RememberedStore.ForHost(key);
        if (list.Count == 0)
            _saved.Controls.Add(new Label { Text = "No saved players on this PC yet.", AutoSize = true, ForeColor = SystemColors.GrayText });
        foreach (var r in list)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
            var picked = _pendingP1 != null && _pendingP1.Name.Equals(r.Person, StringComparison.OrdinalIgnoreCase);
            var play = new Button { Text = picked ? $"Player 1: {r.Person}" : $"Play as {r.Person}", AutoSize = true, MinimumSize = new Size(180, 34) };
            if (picked) play.BackColor = Color.FromArgb(214, 245, 214);
            play.Click += async (_, _) => await Choose(new SavedChoice(r));
            var forget = new LinkLabel { Text = "Forget", AutoSize = true, Margin = new Padding(8, 9, 0, 0) };
            forget.LinkClicked += async (_, _) => await Forget(r);
            var error = new Label { AutoSize = true, ForeColor = Color.Firebrick, Margin = new Padding(8, 9, 0, 0), MaximumSize = new Size(260, 0) };
            row.Controls.Add(play);
            row.Controls.Add(forget);
            row.Controls.Add(error);
            _saved.Controls.Add(row);
            _rows[r.Person] = (play, error);
        }
        _saved.ResumeLayout();
    }

    /// <summary>One click = play. With two players ticked, the first click picks player 1 and the second launches both.</summary>
    private async Task Choose(PlayerChoice c)
    {
        if (_busy) return;
        foreach (var (_, err) in _rows.Values) err.Text = "";
        if (!_second.Checked) { await Play(c, null); return; }
        if (_pendingP1 == null)
        {
            _pendingP1 = c;
            if (c is PasswordChoice) { _name1.Clear(); _pass1.Clear(); } // free the form for player 2
            UpdateHint(); RefreshSaved();
            Status($"Player 1 is {c.Name}. Now click player 2.");
            return;
        }
        if (_pendingP1.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))
        {
            _pendingP1 = null; UpdateHint(); RefreshSaved();
            Status("Unpicked. Click player 1 again.");
            return;
        }
        var p1 = _pendingP1;
        _pendingP1 = null;
        UpdateHint(); RefreshSaved();
        await Play(p1, c);
    }

    private async Task Forget(RememberedLogin r)
    {
        if (MessageBox.Show($"Forget the saved login for {r.Person} on this PC?", AppName, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        string? token = null;
        try { token = RememberedStore.Token(r); } catch { /* unreadable: just drop it */ }
        RememberedStore.Remove(r.Host, r.Person);
        if (_pendingP1?.Name.Equals(r.Person, StringComparison.OrdinalIgnoreCase) == true) { _pendingP1 = null; UpdateHint(); }
        RefreshSaved();
        Status($"Forgot {r.Person} on this PC.");
        if (token != null && TryBroker(out var broker)) using (broker) await broker.ForgetDevice(token);
    }

    private bool TryBroker(out BrokerClient broker)
    {
        broker = null!;
        try
        {
            var (host, port) = Address();
            if (host.Length == 0) return false;
            broker = new BrokerClient(host, port, _fingerprint.Text);
            return true;
        }
        catch { return false; }
    }

    private (string host, int port) Address()
    {
        try { return BrokerClient.ParseAddress(_server.Text, _s.Port); } catch { return ("", _s.Port); }
    }

    private void Status(string s)
    {
        if (_testMode) s = "TEST MODE: Microsoft sign-in is off (this PC only).\n" + s;
        if (InvokeRequired) BeginInvoke(() => _status.Text = s); else _status.Text = s;
    }
    private void Progress(int done, int total)
    {
        void Set() { _bar.Maximum = Math.Max(1, total); _bar.Value = Math.Min(done, _bar.Maximum); }
        if (InvokeRequired) BeginInvoke(Set); else Set();
    }

    private async Task<Lease> LoginAs(BrokerClient broker, PlayerChoice c, string hostKey)
    {
        Status($"Logging in {c.Name}...");
        return await Logins.LoginAs(broker, c, hostKey);
    }

    private async Task Play(PlayerChoice p1, PlayerChoice? p2)
    {
        var (host, port) = Address();
        var hostKey = HostKey();
        if (host.Length == 0 || hostKey == null) { Status("Fill in the host's address and fingerprint first."); return; }

        _s.Server = _server.Text.Trim(); _s.Fingerprint = _fingerprint.Text.Trim(); _s.SecondPlayer = _second.Checked;
        _s.Player1 = p1 is PasswordChoice ? p1.Name : _s.Player1; if (p2 != null) _s.Player2 = p2.Name;
        _s.Save();

        _busy = true;
        SetEnabled(false);
        var leases = new List<Lease>();
        BrokerClient? broker = null;
        PlayerChoice? current = null;
        try
        {
            broker = new BrokerClient(host, port, _fingerprint.Text);
            var game = await new GameInstaller(broker, Status, p => Progress(p.DoneFiles, p.TotalFiles)).Install(CancellationToken.None);
            _testMode = game.TestMode;
            Text = _testMode ? $"{Platform.Title} - TEST MODE: Microsoft sign-in is off" : Platform.Title;

            var players = new List<PlayerChoice> { p1 };
            if (p2 != null) players.Add(p2);

            foreach (var c in players)
            {
                current = c;
                var lease = await LoginAs(broker, c, hostKey);
                leases.Add(lease);
                Status($"{lease.Person} gets account {lease.AccountName}.");
            }
            _pass1.Clear();
            RefreshSaved();

            var area = Screen.PrimaryScreen!.WorkingArea;
            var started = leases.ToList();
            int? w = started.Count > 1 ? area.Width / 2 : null, h = started.Count > 1 ? area.Height : null;
            var procs = new List<Process>();
            for (var i = 0; i < started.Count; i++)
            {
                var lease = started[i];
                current = players[i];
                // Right before each launch: in interim mode this is a short-lived one-time join name.
                Status($"Signing in {lease.Person}...");
                var (address, conn) = await GameConnection.Open(broker, hostKey!, lease, lease.ServerHost ?? host);
                Process proc;
                try
                {
                    var token = await broker.GetMinecraftToken(lease);
                    Status($"Starting Minecraft for {lease.Person} -> {address}");
                    proc = GameLauncher.Launch(game, i + 1, token, address, w, h).Proc;
                }
                catch { conn?.Dispose(); throw; }
                procs.Add(proc);
                _ = Watch(broker, lease, proc, conn);
                leases.Remove(lease); // now owned by Watch()
                if (i == 0 && started.Count > 1) await Task.Delay(10_000); // stagger startup
            }
            current = null;
            Status(started.Count > 1 ? "Both games are starting; they will be placed side by side." : "Minecraft is starting and will join the server.");
            if (procs.Count == 2) await WindowTiler.TileSideBySide(procs[0], procs[1]);
        }
        catch (Exception e)
        {
            var msg = e is BrokerException ? e.Message : "Something went wrong: " + e.Message;
            Status(msg);
            if (e is BrokerException { Code: "app_not_approved" })
                MessageBox.Show(e.Message + "\n\nThis is on the host's side; nothing to fix on your PC.", AppName);
            foreach (var l in leases) _ = broker?.Release(l); // give back accounts we took but did not use
            RefreshSaved();
            if (e is BrokerException { Code: "device_token_invalid" } && current != null)
            {
                _name1.Text = current.Name; _pass1.Clear(); _pass1.Focus(); // fall back to the password form
            }
            else if (current is SavedChoice && _rows.TryGetValue(current.Name, out var row))
            {
                row.error.Text = msg; // show the reason on that player's button
            }
        }
        finally { _busy = false; SetEnabled(true); Progress(0, 1); }
    }

    private void SetEnabled(bool on)
    {
        _play.Enabled = on;
        _second.Enabled = on;
        foreach (Control row in _saved.Controls) row.Enabled = on;
    }

    /// <summary>Keeps the lease alive while the game runs, then gives the account back.</summary>
    private async Task Watch(BrokerClient broker, Lease lease, Process proc, IDisposable? conn)
    {
        Interlocked.Increment(ref _running);
        try
        {
            while (!proc.HasExited)
            {
                await Task.WhenAny(proc.WaitForExitAsync(), Task.Delay(60_000));
                if (!proc.HasExited) { try { await broker.Heartbeat(lease); } catch { /* server keeps the lease while the plugin sees us online */ } }
            }
            conn?.Dispose();
            Status($"{lease.Person}'s game closed (exit code {proc.ExitCode}). Giving the account back...");
            await broker.Release(lease);
            Status($"{lease.Person}'s game closed; account {lease.AccountName} is free again.");
        }
        finally { conn?.Dispose(); Interlocked.Decrement(ref _running); }
    }
}
