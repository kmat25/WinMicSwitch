using WinMicSwitch;
using WinMicSwitch.Audio;

internal static partial class Program
{
    private sealed class Fake : IAudio
    {
        public Mic[] Mics = [new("b", "USB"), new("a", "Internal")];
        public Dictionary<Role,string> Roles = AudioState.Roles.ToDictionary(r => r, _ => "a");
        public Dictionary<string,bool> Muted = new() { ["a"] = false, ["b"] = true };
        public List<Role> Writes = [];
        public int MuteWrites;
        public float InputLevel = .5f;
        public bool FailLevel;
        public float Level(string id) => FailLevel ? throw new InvalidOperationException() : InputLevel;
        public void SetLevel(string id, float value) { Check(id == Roles[Role.Multimedia], "volume target"); InputLevel = value; }
        public bool FailBefore, FailComm;
        public Action? OnMuteRead;
        public Mic[] List() => Mics;
        public string Default(Role role) => Roles[role];
        public bool Mute(string id)
        {
            OnMuteRead?.Invoke();
            if (FailBefore) { FailBefore = false; throw new InvalidOperationException(); }
            return Muted[id];
        }
        public void SetMute(string id, bool value) { Muted[id] = value; MuteWrites++; }
        public void SetDefault(string id, Role role)
        {
            Writes.Add(role);
            if (FailComm && role == Role.Communications) throw new InvalidOperationException();
            Roles[role] = id;
        }
    }

    static void AudioTests()
    {
        MenuTests();
        var fake = new Fake();
        var state = new AudioState(fake);
        void Refresh(bool device = true, bool volume = true) => state.Refresh(() => device, id => volume && id != null);
        Refresh();
        Check(state.Status == "収音中" && fake.MuteWrites == 0 && fake.Writes.Count == 0, "startup read-only");
        Check(TrayText.Tooltip(state) == "WinMicSwitch: 50% 収音中 Internal", "Japanese tooltip shows input volume first");
        fake.FailLevel = true;
        Refresh();
        Check(TrayText.Tooltip(state) == "WinMicSwitch: 収音中 Internal", "failed level read clears old volume only");
        fake.FailLevel = false;
        Check(state.Number("a") == "1" && state.Number("b") == "2", "initial numbering");
        Check(state.AdjustVolume(120) == null && fake.InputLevel == .52f, "one notch raises volume by two points");
        Check(state.AdjustVolume(-60) == null && fake.InputLevel == .52f, "partial notch waits");
        Check(state.AdjustVolume(-60) == null && Math.Abs(fake.InputLevel - .5f) < .000001f, "two half-notches lower volume by two points");
        Check(state.AdjustVolume(-6000) == null && fake.InputLevel == 0f, "wheel clamps at zero");
        Check(state.AdjustVolume(7200) == null && fake.InputLevel == 1f && fake.MuteWrites == 0, "wheel clamps at full without changing mute");
        fake.Roles[Role.Communications] = "b";
        Check(state.Toggle() == null && fake.Muted["a"] && fake.Muted["b"] && fake.MuteWrites == 1, "toggle multimedia only");
        Check(state.Toggle() == null && !fake.Muted["a"], "unmute");
        fake.FailBefore = true;
        fake.FailComm = true;
        var partial = state.Select("b");
        Check(partial != null && partial.Contains("選択先を確認／設定 0x") && partial.Contains("変更前 未確認"), "partial API failure and readback match");
        Check(fake.Writes.SequenceEqual(AudioState.Roles) && fake.MuteWrites == 2, "three roles in order and no mute writes");
        Refresh(false);
        Check(state.Status == "状態不明" && state.Muted == true, "notification failure preserves last read");
        Refresh(volume:false);
        Check(state.Status == "状態不明", "volume notification failure");
        Refresh();
        Check(TrayText.Tooltip(state) == "WinMicSwitch: 100% 消音中 USB", "muted microphone retains its volume");
        Check(state.Status == "消音中", "notification recovery");
        fake.Mics = [];
        Refresh(false);
        Check(state.Status == "マイクなし", "zero devices takes precedence");
        fake.Mics = [new("b", "Renamed")];
        Refresh();
        Check(state.Number("b") == "1" && state.Number("a") == "—", "remaining device renumbered");
        fake.Mics = [new("b", string.Concat(Enumerable.Repeat("🎙️&\r\n", 50)))];
        Refresh(false);
        string tip = TrayText.Tooltip(state);
        Check(tip.Length <= 63 && !tip.Contains('\n') && !char.IsHighSurrogate(tip[^1]) &&
            tip.Contains("自動更新停止") && tip.Contains("最終取得"), "tooltip retains notification caveat with a long Unicode name");
        fake.Mics = [new("a","a"), new("b","b")];
        fake.OnMuteRead = () => fake.Roles[Role.Multimedia] = "a";
        Check(state.Toggle() != null && fake.MuteWrites == 2, "default changes between mute read and write");
        fake.OnMuteRead = null;
        fake.FailBefore = true;
        try { state.Toggle(); }
        catch (InvalidOperationException) { }
        Check(fake.MuteWrites == 2, "failed initial mute read prevents a write");
        fake.Mics = [];
        int count = fake.Writes.Count;
        Check(state.Select("b")!.Contains("未実行") && fake.Writes.Count == count, "disconnected selection never writes");
    }

    static void MenuTests()
    {
        var fake = new Fake { Mics = [new("a", "Internal")] };
        var state = new AudioState(fake);
        using var menu = new ContextMenuStrip();
        string? selected = null;
        void Build(bool watch = true)
        {
            state.Refresh(() => watch, _ => true);
            TrayContext.BuildMenu(menu, state, (_, id) => selected = id, () => { });
        }
        string?[] Labels() => menu.Items.OfType<ToolStripMenuItem>().Select(i => i.Text).ToArray();
        Build();
        Check(Labels().SequenceEqual(["#1 Internal　収音中", "終了"]), "one device appears once");
        Check(!menu.Items[0].CanSelect, "title cannot be selected");
        System.Globalization.CultureInfo.CurrentUICulture = new("en-US");
        Ui.UseCurrentLanguage();
        Build();
        Check(TrayText.Tooltip(state) == "WinMicSwitch: 50% Unmuted Internal" && Labels().Contains("Exit"), "English display language");
        System.Globalization.CultureInfo.CurrentUICulture = new("fr-FR");
        Ui.UseCurrentLanguage();
        Build();
        Check(TrayText.Tooltip(state) == "WinMicSwitch: 50% Unmuted Internal" && Labels().Contains("Exit"), "English fallback");
        System.Globalization.CultureInfo.CurrentUICulture = new("ja-JP");
        Ui.UseCurrentLanguage();
        fake.Mics = [new("a", "Internal"), new("b", "USB")];
        foreach (var role in AudioState.Roles) fake.Roles[role] = "b";
        Build();
        Check(Labels().SequenceEqual(["#1 Internal", "#2 USB　消音中", "終了"]), "only current device shows mute");
        var current = menu.Items.OfType<ToolStripMenuItem>().Single(i => i.Checked);
        current.PerformClick();
        Check(current.Text == "#2 USB　消音中" && selected == "b", "checked row selects its endpoint ID");
        Build(false);
        Check(Labels().Contains("#2 USB　状態不明") && Labels().Contains("最終取得　消音中"), "last read distinguished from current status");
        fake.Mics = [];
        Build();
        Check(Labels().Contains("利用可能なマイクがありません"), "empty list explanation");
    }

    static void WindowsSmoke()
    {
        using var audio = new CoreAudio(_ => { });
        var state = new AudioState(audio);
        state.Refresh(audio.WatchDevices, audio.WatchVolume);
        Console.WriteLine($"Windows {Environment.OSVersion.Version} x64={Environment.Is64BitProcess} listed={state.Listed} count={state.Mics.Length} deviceWatch={state.DeviceWatch} volumeWatch={state.VolumeWatch} status={state.Status}");
        foreach (var role in AudioState.Roles)
            Console.WriteLine($"{role}: {state.Describe(state.Defaults[role])} {state.Defaults[role].Error}");
        using var context = new TrayContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) => { timer.Stop(); context.ExitThread(); };
        timer.Start();
        Application.Run(context);
        var previous = state.Defaults.ToArray();
        bool? muted = state.Muted;
        state.Refresh(audio.WatchDevices, audio.WatchVolume);
        Check(previous.All(p => state.Defaults[p.Key] == p.Value) && state.Muted == muted, "tray startup/shutdown preserves observed roles and mute");
    }
}
