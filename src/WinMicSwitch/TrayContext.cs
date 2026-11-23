using WinMicSwitch.Audio;

namespace WinMicSwitch;

internal sealed class TrayContext : ApplicationContext
{
    private readonly Control dispatcher = new();
    private readonly NotifyIcon tray = new();
    private readonly ContextMenuStrip menu = new();
    private readonly TrayWheel wheel;
    private readonly InputCapture input;
    private readonly CoreAudio audio;
    private readonly AudioState state;
    private readonly object sync = new();
    private readonly HashSet<VolumeSource?> notifications = [];
    private bool posted, refreshing;
    private volatile bool stopping;
    private readonly OperationGate gate = new(() => Environment.TickCount64);

    private Icon? icon;
    private (string Status, string Number, int Dpi, int Ink) iconKey;
    private bool retrying;

    internal TrayContext()
    {
        _ = dispatcher.Handle;
        audio = new CoreAudio(Signal);
        state = new AudioState(audio);
        input = new InputCapture(menu);
        Application.AddMessageFilter(input);
        tray.ContextMenuStrip = menu;
        tray.MouseDown += input.TrayMouseDown;
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) Accept(input.ConsumeTrayClick(), state.Toggle);
        };
        SetIcon();
        tray.Text = $"WinMicSwitch: {Ui.Unknown}";
        tray.Visible = true;
        wheel = new TrayWheel(tray, (tick, delta) =>
        {
            if (!menu.Visible) Accept(tick, () => state.AdjustVolume(delta));
        });
        menu.Opening += (_, e) =>
        {
            if (Busy || refreshing) { e.Cancel = true; return; }
            Refresh();
            BuildMenu(menu, state, (item, id) => Accept(input.Consume(item), () => state.Select(id)), ExitThread, wheel.Registered);
            e.Cancel = Busy;
        };
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnAppearanceChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnAppearanceChanged;
        Signal(null);
    }

    private void OnAppearanceChanged(object? sender, EventArgs args) => Signal(null);

    private bool Busy => gate.Busy || stopping;

    private void Signal(VolumeSource? source)
    {
        lock (sync)
        {
            if (stopping) return;
            notifications.Add(source);
            if (posted) return;
            posted = true;
            dispatcher.BeginInvoke(Drain);
        }
    }

    private void Drain()
    {
        lock (sync)
        {
            posted = false;
            if (Busy || refreshing) return;
            bool current = notifications.Contains(null) || notifications.Contains(audio.Subscription);
            notifications.Clear();
            if (!current) return;
        }
        if (menu.Visible) menu.Close();
        Refresh();
    }

    private void Refresh()
    {
        refreshing = true;
        try
        {
            bool inconsistent = state.Refresh(audio.WatchDevices, audio.WatchVolume);
            if (!state.Listed)
            {
                audio.Dispose(); // Recreate the enumerator after an audio-service failure.
                state.ClearWatches();
            }
            if (inconsistent && !retrying) { retrying = true; Signal(null); }
            else retrying = false;
            SetIcon();
            tray.Text = TrayText.Tooltip(state);
        }
        finally { refreshing = false; Resume(); }
    }

    private void Resume()
    {
        if (gate.Executing || refreshing) return;
        if (stopping) { dispatcher.BeginInvoke(ExitThread); return; }
        if (gate.Reserved)
        {
            dispatcher.BeginInvoke(() =>
            {
                if (stopping || refreshing) return;
                gate.RunReserved();
            });
            return;
        }
        lock (sync)
        {
            if (notifications.Count > 0 && !posted)
            {
                posted = true;
                dispatcher.BeginInvoke(Drain);
            }
        }
    }

    private void Accept(long? tick, Func<string?> operation)
    {
        if (stopping) return;
        void Report(string message) => MessageBox.Show(message, "WinMicSwitch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        void Execute()
        {
            string? result;
            try { result = operation(); }
            catch (Exception ex) when (AudioFailure.Expected(ex)) { result = Ui.Text($"マイクを操作できません（0x{ex.HResult:X8}）。", $"Microphone operation failed (0x{ex.HResult:X8})."); }
            Refresh();
            if (result != null && !stopping) Report(result);
        }
        gate.Accept(tick, refreshing, Execute, () => Report(Ui.Text("入力元を確認できません。設定操作を中止しました。", "Could not verify the input. Settings were left unchanged.")), () =>
        {
            input.EndOperation();
            Resume();
        });
    }
    private void SetIcon()
    {
        var ink = TrayIconFactory.Foreground();
        var key = (state.Status, Number: state.Empty ? "—" : state.Number(state.Current), Dpi: dispatcher.DeviceDpi,
            ink.ToArgb());
        if (icon != null && key == iconKey) return;
        var next = TrayIconFactory.Create(key.Status, key.Number, key.Dpi, ink);
        tray.Icon = next;
        icon?.Dispose();
        icon = next;
        iconKey = key;
    }

    internal static void BuildMenu(ContextMenuStrip menu, AudioState state, Action<ToolStripMenuItem, string> select, Action exit, bool wheelAvailable = true)
    {
        while (menu.Items.Count > 0) menu.Items[0].Dispose();
        void Line(string text) => menu.Items.Add(TrayText.Clean(text).Replace("&", "&&")).Enabled = false;
        menu.Items.Add(new ToolStripLabel("WinMicSwitch"));
        if (!wheelAvailable) Line(Ui.Text("ホイール入力を利用できません", "Wheel input unavailable"));
        if (state.Mics.Length > 0 && !state.Active(state.Current))
            Line(Ui.Text("一般用の既定マイクを確認できません", "Default microphone unavailable"));
        if (state.LostWatch)
        {
            Line(Ui.Text("自動更新停止　", "Updates paused: ") + (!state.DeviceWatch ? Ui.Text("デバイス通知 ", "device notifications ") : "") + (state.Active(state.Current) && !state.VolumeWatch ? Ui.Text("ミュート通知", "mute notifications") : ""));
            if (state.Muted.HasValue) Line(Ui.Text("最終取得　", "Last read: ") + AudioState.MuteText(state.Muted));
        }
        if (state.Defaults.Values.Any(v => !v.Ok || v.Value == null || v.Value != state.Current))
            foreach (var role in AudioState.Roles) Line(AudioState.Label(role) + "　" + state.Describe(state.Defaults[role]));
        menu.Items.Add(new ToolStripSeparator());
        if (!state.Listed) Line(Ui.Text("マイク一覧を取得できません", "Microphone list unavailable"));
        else if (state.Empty) Line(Ui.Text("利用可能なマイクがありません", "No microphones available"));
        foreach (var mic in state.Mics)
        {
            bool current = mic.Id == state.Current;
            string label = $"#{state.Number(mic.Id)} {TrayText.Clean(mic.Name).Replace("&", "&&")}";
            if (current) label += "　" + state.Status;
            var item = new ToolStripMenuItem(label) { Checked = current };
            item.Click += (_, _) => select(item, mic.Id);
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Ui.Text("終了", "Exit"), null, (_, _) => exit());
    }

    protected override void ExitThreadCore()
    {
        lock (sync) { stopping = true; notifications.Clear(); }
        gate.Cancel();
        if (refreshing || gate.Executing) return;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (sync) stopping = true;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnAppearanceChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnAppearanceChanged;
            wheel.Dispose();
            input.Dispose();
            tray.Visible = false;
            audio.Dispose();
            tray.Dispose();
            icon?.Dispose();
            menu.Dispose();
            dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}
