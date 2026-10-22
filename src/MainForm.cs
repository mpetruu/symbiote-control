using System.Diagnostics;

namespace SymbioteControl;

internal sealed class MainForm : Form
{
    private readonly Button attach = new() { Text = "Attach to game", AutoSize = false, Height = 40, Width = 168 };
    private readonly CheckBox surge = new ReadableCheckBox() { Text = "Never-ending Symbiote Surge", Enabled = false };
    private readonly CheckBox blackSuit = new ReadableCheckBox() { Text = "Black suit + symbiote abilities", Enabled = false };
    private readonly TextBox status = new()
    {
        Text = "Start the game, then attach.", Multiline = true, ReadOnly = true,
        BorderStyle = BorderStyle.None, ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill, TabStop = false
    };
    private readonly System.Windows.Forms.Timer monitor = new() { Interval = 100 };
    private GameProcess? game;
    private FeatureBackend? backend;
    private bool updating;
    private bool busy;
    private bool detachWhenReady;
    private bool closeWhenReady;
    private string? lastBackendReason;
    private string? lastMonitorError;

    public MainForm()
    {
        Text = "Symbiote Control";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(500, 385);
        MinimumSize = new Size(516, 424);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        BackColor = Color.FromArgb(18, 20, 25);
        ForeColor = Color.FromArgb(233, 235, 241);
        Font = new Font("Segoe UI", 10F);
        AutoScaleMode = AutoScaleMode.Dpi;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(26, 20, 26, 18), ColumnCount = 1,
            RowCount = 7, BackColor = BackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        var title = new Label { Text = "SYMBIOTE CONTROL", AutoSize = true, Font = new Font("Segoe UI Semibold", 17F), ForeColor = Color.White };
        var subtitle = new Label { Text = "Marvel’s Spider-Man 2  •  Peter Parker", AutoSize = true, ForeColor = Color.FromArgb(151, 158, 176) };
        attach.FlatStyle = FlatStyle.Flat;
        attach.FlatAppearance.BorderColor = Color.FromArgb(66, 77, 101);
        attach.BackColor = Color.FromArgb(39, 46, 63);
        attach.ForeColor = Color.White;
        attach.Margin = new Padding(0, 6, 0, 8);
        surge.Margin = new Padding(0, 8, 0, 0);
        blackSuit.Margin = new Padding(0, 8, 0, 0);
        status.ForeColor = Color.FromArgb(169, 181, 202);
        status.BackColor = BackColor;
        status.Margin = new Padding(0, 12, 0, 0);
        var footer = new Label { Text = $"Supported game version: {GameFingerprint.ExpectedVersion}", AutoSize = true, Font = new Font("Segoe UI", 8F), ForeColor = Color.FromArgb(127, 136, 153), Anchor = AnchorStyles.Left | AnchorStyles.Bottom };
        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(subtitle, 0, 1);
        layout.Controls.Add(attach, 0, 2);
        layout.Controls.Add(surge, 0, 3);
        layout.Controls.Add(blackSuit, 0, 4);
        layout.Controls.Add(status, 0, 5);
        layout.Controls.Add(footer, 0, 6);
        Controls.Add(layout);
        attach.Click += AttachClicked;
        surge.CheckedChanged += (_, _) => ChangeFeature(true);
        blackSuit.CheckedChanged += (_, _) => ChangeFeature(false);
        monitor.Tick += (_, _) => MonitorGame();
        monitor.Start();
        FormClosing += HandleFormClosing;
    }

    private async void AttachClicked(object? sender, EventArgs e)
    {
        if (busy) return;
        busy = true;
        attach.Enabled = false;
        try
        {
            if (game != null)
            {
                Detach();
                status.Text = "Detached. Features were restored.";
                return;
            }
            status.Text = "Checking the running game and executable fingerprint…";
            var opened = await Task.Run(() =>
            {
                var candidates = Process.GetProcessesByName("Spider-Man2");
                if (candidates.Length != 1)
                {
                    foreach (var candidate in candidates) candidate.Dispose();
                    throw new InvalidOperationException(candidates.Length == 0
                        ? "Spider-Man2.exe is not running. Start the game and try again."
                        : "More than one game process is running. Close the extra instance and try again.");
                }
                var process = candidates[0];
                try
                {
                    var path = process.MainModule?.FileName ?? throw new InvalidOperationException("Cannot identify the game executable.");
                    GameFingerprint.Validate(path);
                    return GameProcess.Open(process);
                }
                catch { process.Dispose(); throw; }
            });
            try
            {
                var features = new FeatureBackend(opened);
                game = opened;
                backend = features;
                lastBackendReason = features.AvailabilityReason;
                lastMonitorError = null;
                attach.Text = "Detach";
                status.Text = $"Attached to Spider-Man2.exe (PID {opened.ProcessId}).\r\n{features.AvailabilityReason}";
                RefreshToggles();
            }
            catch { opened.Dispose(); throw; }
        }
        catch (InvalidOperationException error) when (backend?.CleanupPending == true)
        {
            detachWhenReady = true;
            status.Text = error.Message;
        }
        catch (Exception error)
        {
            status.Text = error.Message;
            MessageBox.Show(this, error.Message, "Symbiote Control", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { busy = false; attach.Enabled = true; }
    }

    private void ChangeFeature(bool isSurge)
    {
        if (updating || backend == null) return;
        try
        {
            if (isSurge) backend.SetSurge(surge.Checked);
            else backend.SetBlackSuit(blackSuit.Checked);
            status.Text = isSurge
                ? (backend.SurgeEnabled ? "Never-ending Symbiote Surge is enabled." : "Never-ending Symbiote Surge is off.")
                : (backend.BlackSuitEnabled ? "Black suit + symbiote abilities is enabled." : "Black suit + symbiote abilities is off.");
        }
        catch (Exception error)
        {
            status.Text = error.Message;
            MessageBox.Show(this, error.Message, "Feature change failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { RefreshToggles(); }
    }

    private void RefreshToggles()
    {
        var isAlive = game?.IsAlive == true;
        var surgeChecked = backend?.SurgeEnabled ?? false;
        var blackSuitChecked = backend?.BlackSuitEnabled ?? false;
        var surgeAvailable = backend?.SupportsSurge == true && isAlive;
        var blackSuitAvailable = backend?.SupportsBlackSuit == true && isAlive;
        if (surge.Checked == surgeChecked && blackSuit.Checked == blackSuitChecked
            && surge.Enabled == surgeAvailable && blackSuit.Enabled == blackSuitAvailable)
            return;
        updating = true;
        try
        {
            surge.Checked = surgeChecked;
            blackSuit.Checked = blackSuitChecked;
            surge.Enabled = surgeAvailable;
            blackSuit.Enabled = blackSuitAvailable;
        }
        finally { updating = false; }
    }

    private void MonitorGame()
    {
        if (busy || game == null) return;
        if (!game.IsAlive)
        {
            try { Detach(); status.Text = "The game exited. Start it again, then attach."; }
            catch (Exception error) { status.Text = error.Message; }
            return;
        }
        if (backend == null) return;
        try
        {
            backend.Tick();
            if (detachWhenReady && !backend.CleanupPending)
            {
                Detach();
                status.Text = "Detached. Features were restored.";
                if (closeWhenReady) Close();
                return;
            }
            RefreshToggles();
            var reason = backend.AvailabilityReason;
            if (reason != lastBackendReason || lastMonitorError != null)
                status.Text = $"Attached to Spider-Man2.exe (PID {game.ProcessId}).\r\n{reason}";
            lastBackendReason = reason;
            lastMonitorError = null;
        }
        catch (Exception error)
        {
            // Timer failures stay visible inline and are updated only when the
            // diagnostic changes. Never open repeated modal dialogs at 10 Hz.
            var reason = backend.AvailabilityReason;
            var diagnostic = string.IsNullOrWhiteSpace(reason)
                ? error.Message : $"{reason}\r\n{error.Message}";
            if (diagnostic != lastMonitorError) status.Text = diagnostic;
            lastMonitorError = diagnostic;
            RefreshToggles();
        }
    }

    private void Detach()
    {
        // A failing restoration deliberately keeps the process handle and
        // backend alive so the user can retry; do not silently abandon patches.
        try { backend?.Dispose(); }
        catch { RefreshToggles(); throw; }
        backend = null;
        detachWhenReady = false;
        lastBackendReason = null;
        lastMonitorError = null;
        game?.Dispose();
        game = null;
        attach.Text = "Attach to game";
        RefreshToggles();
    }

    private void HandleFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (busy)
        {
            e.Cancel = true;
            status.Text = "Wait for attachment to finish, then close the menu.";
            return;
        }
        try { Detach(); monitor.Stop(); monitor.Dispose(); }
        catch (InvalidOperationException error) when (backend?.CleanupPending == true)
        {
            e.Cancel = true;
            detachWhenReady = true;
            closeWhenReady = true;
            status.Text = "Finishing cleanup. Return to Peter's gameplay; this menu will close after acknowledgement.\r\n" + error.Message;
        }
        catch (Exception error)
        {
            e.Cancel = true;
            status.Text = "Restoration failed. The menu remains open so you can retry.";
            MessageBox.Show(this, $"Could not restore all changes:\r\n\r\n{error.Message}\r\n\r\nClose the game, or retry detaching before closing this menu.",
                "Restoration failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

// Preserve native checkbox behavior/accessibility while keeping disabled labels
// readable against the dark menu background.
internal sealed class ReadableCheckBox : CheckBox
{
    public ReadableCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        AutoSize = false;
        Size = new Size(440, 32);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        var box = new Rectangle(1, (Height - 17) / 2, 17, 17);
        using var fill = new SolidBrush(Checked ? Color.FromArgb(90, 126, 194) : Color.FromArgb(29, 34, 44));
        e.Graphics.FillRectangle(fill, box);
        using var border = new Pen(Enabled ? Color.FromArgb(164, 177, 201) : Color.FromArgb(88, 98, 116));
        e.Graphics.DrawRectangle(border, box);
        if (Checked)
        {
            using var tick = new Pen(Color.White, 2);
            e.Graphics.DrawLines(tick, [new Point(5, box.Y + 9), new Point(8, box.Y + 12), new Point(15, box.Y + 5)]);
        }
        var text = new Rectangle(28, 0, Width - 30, Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, text,
            Enabled ? Color.FromArgb(233, 235, 241) : Color.FromArgb(151, 158, 176),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, text);
    }
}
