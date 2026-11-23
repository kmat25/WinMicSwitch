using System.Runtime.InteropServices;
using System.Text;
using WinMicSwitch;
using WinMicSwitch.Audio;

internal static partial class Program
{
    [DllImport("user32.dll")] private static extern nint GetActiveWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hwnd, StringBuilder name, int length);

    static void TwoMicrophones(string firstName, string secondName)
    {
        using var observer = new CoreAudio(_ => { });
        var devices = observer.List();
        var first = devices.Single(m => m.Name == firstName);
        var second = devices.Single(m => m.Name == secondName);
        Check(first.Id != second.Id, "distinct capture endpoints");
        var roles = AudioState.Roles.ToDictionary(r => r, r => observer.Default(r)
            ?? throw new InvalidOperationException("Cannot restore a missing default"));
        var mute = new[] { first, second }.ToDictionary(m => m, m => observer.Mute(m.Id));
        foreach (var role in roles)
            Console.WriteLine($"Before {role.Key}: {devices.Single(d => d.Id == role.Value).Name}");
        foreach (var mic in mute) Console.WriteLine($"Before {mic.Key.Name}: mute={mic.Value}");

        using var app = new TrayContext();
        var tray = Field<NotifyIcon>(app, "tray");
        var menu = tray.ContextMenuStrip!;
        var window = Field<NativeWindow>(tray, "_window");
        var state = Field<AudioState>(app, "state");
        bool warned = false;
        using var dialogs = new System.Windows.Forms.Timer { Interval = 100 };
        dialogs.Tick += (_, _) =>
        {
            var hwnd = GetActiveWindow();
            var cls = new StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            if (cls.ToString() == "#32770") { warned = true; Post(hwnd, 0x10, 0, 0); }
        };
        dialogs.Start();

        void Wait(Func<bool> ready, string reason)
        {
            long until = Environment.TickCount64 + 4000;
            do { Application.DoEvents(); Thread.Sleep(20); }
            while (!warned && !ready() && Environment.TickCount64 < until);
            Check(!warned && ready(), reason + " | " + tray.Text);
        }
        void Open()
        {
            Thread.Sleep(60);
            Post(window.Handle, InputCapture.TrayCallback, 1, 0x205);
            Wait(() => menu.Visible, "menu opened");
        }
        ToolStripMenuItem Item(Mic mic) => menu.Items.OfType<ToolStripMenuItem>()
            .Single(item => item.Enabled && item.Text == $"#{state.Number(mic.Id)} {mic.Name}" +
                (state.Current == mic.Id ? "　" + state.Status : ""));
        void Select(Mic mic)
        {
            var previous = mute.Keys.ToDictionary(m => m, m => observer.Mute(m.Id));
            Open();
            var item = Item(mic);
            item.Select();
            Post(menu.Handle, 0x100, (int)Keys.Enter, 0);
            Wait(() => !menu.Visible && AudioState.Roles.All(r => observer.Default(r) == mic.Id)
                && state.Current == mic.Id && state.VolumeWatch && state.Muted == previous[mic]
                && tray.Text.Contains(previous[mic] ? "消音中" : "収音中"),
                "selected all three roles: " + mic.Name);
            Check(previous.All(p => observer.Mute(p.Key.Id) == p.Value), "both mute flags preserved");
            Open();
            Check(Item(mic).Checked && !Item(mic == first ? second : first).Checked, "menu check follows Multimedia");
            menu.Close();
        }
        void ToggleOnlyFirst()
        {
            var before = observer.Mute(first.Id);
            var other = observer.Mute(second.Id);
            Post(window.Handle, InputCapture.TrayCallback, 1, 0x202);
            Wait(() => observer.Mute(first.Id) != before && state.Muted == !before, "left click toggled primary");
            Check(observer.Mute(second.Id) == other, "secondary mute unchanged");
        }

        try
        {
            Wait(() => state.Active(first.Id) && state.Active(second.Id), "both inputs listed");
            observer.SetMute(second.Id, true);
            observer.SetMute(first.Id, false);
            Select(second);
            observer.SetMute(second.Id, false);
            Wait(() => state.Current == second.Id && state.Muted == false && tray.Text.Contains("収音中"), "external USB unmute notification");
            observer.SetMute(second.Id, true);
            Wait(() => state.Muted == true && tray.Text.Contains("消音中"), "external USB mute notification");
            Select(first);
            Select(second);
            Select(first);

            foreach (var split in new[] { Role.Communications, Role.Console })
            {
                observer.SetDefault(second.Id, split);
                var readback = AudioState.Roles.ToDictionary(r => r, r => observer.Default(r));
                Wait(() => readback.All(p => state.Defaults[p.Key].Value == p.Value), "external role change notification");
                if (!AudioState.Roles.All(r => readback[r] == (r == split ? second.Id : first.Id)))
                {
                    Console.WriteLine($"SKIP isolated {split}; Windows reports " + string.Join(", ", readback.Select(p => $"{p.Key}={devices.Single(m => m.Id == p.Value).Name}")));
                    Select(first);
                    continue;
                }
                Check(tray.Text == TrayText.Tooltip(state) && !tray.Text.Contains(AudioState.Label(split)), "split-role tooltip");
                Open();
                foreach (var role in AudioState.Roles)
                    Check(menu.Items.Cast<ToolStripItem>().Any(i => !i.Enabled && i.Text!.Contains(AudioState.Label(role)) &&
                        i.Text.Contains(state.Name(readback[role]))), "menu role: " + role);
                menu.Close();
                ToggleOnlyFirst();
                ToggleOnlyFirst();
                Select(first);
            }
        }
        finally
        {
            var errors = new List<Exception>();
            void Restore(Action action) { try { action(); } catch (Exception e) { errors.Add(e); } }
            foreach (var p in mute) Restore(() => { if (observer.Mute(p.Key.Id) != p.Value) observer.SetMute(p.Key.Id, p.Value); });
            foreach (var p in roles) Restore(() => { if (observer.Default(p.Key) != p.Value) observer.SetDefault(p.Value, p.Key); });
            foreach (var p in roles) Restore(() => Check(observer.Default(p.Key) == p.Value, "restored " + p.Key));
            foreach (var p in mute) Restore(() => Check(observer.Mute(p.Key.Id) == p.Value, "restored mute: " + p.Key.Name));
            if (errors.Count != 0) throw new AggregateException("Restoration", errors);
        }
    }
}
