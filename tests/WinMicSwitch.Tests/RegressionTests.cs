using System.Reflection;
using WinMicSwitch;
using WinMicSwitch.Audio;

internal static partial class Program
{
    static void GateTests()
    {
        long now = (long)uint.MaxValue + 3;
        var gate = new OperationGate(() => now);
        int writes = 0, reports = 0, completed = 0;
        void Submit(long? tick, bool reading = false) => gate.Accept(tick, reading,
            () => { writes++; Submit(null); Submit(now); },
            () => { reports++; Submit(null); Submit(now); },
            () => completed++);
        Submit(null, true);
        Submit(now);
        Check(gate.Reserved && writes == 0 && reports == 0, "defer unverified input until refresh returns");
        gate.RunReserved();
        Check(reports == 1 && writes == 0 && completed == 1, "no nested report or write");
        Submit(now - 5); Submit(now);
        Check(completed == 1, "old and equal timestamps rejected across 32-bit wrap");
        Submit(++now, true);
        Submit(null);
        gate.RunReserved();
        Check(writes == 1 && reports == 1 && completed == 2, "first verified action survives subsequent unknown input");
        Submit(++now, true);
        gate.Cancel();
        gate.RunReserved();
        Check(writes == 1 && !gate.Busy, "shutdown discards reservation");
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;

    static void LiveTray()
    {
        int notifications = 0;
        using var observer = new CoreAudio(source => { if (source != null) Interlocked.Increment(ref notifications); });
        var id = observer.Default(Role.Multimedia) ?? throw new InvalidOperationException("既定入力がありません");
        Check(AudioState.Roles.All(role => observer.Default(role) == id), "live test starts with all roles on one microphone");
        bool initial = observer.Mute(id);
        float initialLevel = observer.Level(id);
        observer.WatchVolume(id);
        using var app = new TrayContext();
        var tray = Field<NotifyIcon>(app, "tray");
        var menu = tray.ContextMenuStrip!;
        var window = Field<NativeWindow>(tray, "_window");
        void PumpUntil(Func<bool> condition)
        {
            var until = Environment.TickCount64 + 3000;
            do { Application.DoEvents(); Thread.Sleep(15); } while (!condition() && Environment.TickCount64 < until);
            Check(condition(), "Windows readback and delivery | " + tray.Text);
        }
        try
        {
            PumpUntil(() => tray.Text.Contains(initial ? "消音中" : "収音中"));
            int direction = initialLevel > .5f ? -120 : 120;
            var state = Field<AudioState>(app, "state");
            Check(Field<TrayWheel>(app, "wheel").Registered, "raw mouse registration");
            Check(state.AdjustVolume(direction) == null && Math.Abs(observer.Level(id) - initialLevel - direction / 120f * .02f) <= .01f &&
                observer.Mute(id) == initial, "live volume readback preserves mute");
            int changedPercent = (int)MathF.Round(observer.Level(id) * 100, MidpointRounding.AwayFromZero);
            PumpUntil(() => tray.Text.StartsWith($"WinMicSwitch: {changedPercent}% "));
            observer.SetLevel(id, initialLevel);
            int originalPercent = (int)MathF.Round(initialLevel * 100, MidpointRounding.AwayFromZero);
            PumpUntil(() => tray.Text.StartsWith($"WinMicSwitch: {originalPercent}% "));
            Post(window.Handle, InputCapture.TrayCallback, 1, 0x202);
            PumpUntil(() => observer.Mute(id) != initial && notifications > 0);
            Check(tray.Text.Contains(initial ? "収音中" : "消音中"), "live tray toggle reflected in Core Audio and tooltip");
            Thread.Sleep(32);
            Post(window.Handle, InputCapture.TrayCallback, 1, 0x202);
            PumpUntil(() => observer.Mute(id) == initial && notifications >= 2);
            observer.SetMute(id, !initial);
            PumpUntil(() => tray.Text.Contains(initial ? "収音中" : "消音中"));
            observer.SetMute(id, initial);
            PumpUntil(() => tray.Text.Contains(initial ? "消音中" : "収音中"));
            Post(window.Handle, InputCapture.TrayCallback, 1, 0x205);
            PumpUntil(() => menu.Visible);
            var item = menu.Items.OfType<ToolStripMenuItem>().Single(m => m.Checked);
            item.Select();
            Post(window.Handle, 0x100, (int)Keys.Enter, 0);
            PumpUntil(() => !menu.Visible);
            Check(AudioState.Roles.All(role => observer.Default(role) == id) && observer.Mute(id) == initial, "same-device selection preserves all three roles and mute");
        }
        finally
        {
            if (observer.Level(id) != initialLevel) observer.SetLevel(id, initialLevel);
            if (observer.Mute(id) != initial) observer.SetMute(id, initial);
        }
    }
}
