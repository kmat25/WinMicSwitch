using WinMicSwitch;
using WinMicSwitch.Audio;

internal static partial class Program
{
    static void Hotplug(string usbName, string remainingName)
    {
        using var observer = new CoreAudio(_ => { });
        var initial = observer.List();
        var usb = initial.Single(m => m.Name == usbName);
        var other = initial.Single(m => m.Name == remainingName);
        var roles = AudioState.Roles.ToDictionary(r => r, r => observer.Default(r) ?? throw new InvalidOperationException("Missing default"));
        var mutes = new[] { usb, other }.ToDictionary(m => m.Id, m => observer.Mute(m.Id));
        using var app = new TrayContext();
        var state = Field<AudioState>(app, "state");
        var tray = Field<NotifyIcon>(app, "tray");
        var menu = tray.ContextMenuStrip!;
        var window = Field<NativeWindow>(tray, "_window");
        void Wait(Func<bool> ready, string name, int seconds)
        {
            long end = Environment.TickCount64 + seconds * 1000;
            do { Application.DoEvents(); Thread.Sleep(50); } while (!ready() && Environment.TickCount64 < end);
            Check(ready(), name + " | " + tray.Text);
        }
        void OpenMenu()
        {
            Post(window.Handle, InputCapture.TrayCallback, 1, 0x205);
            Wait(() => menu.Visible, "menu visible", 5);
        }
        Wait(() => state.Active(usb.Id) && state.Active(other.Id), "initial list", 5);
        var usbNumber = state.Number(usb.Id);
        var otherNumber = state.Number(other.Id);
        OpenMenu();
        Console.WriteLine("READY remove USB");
        Wait(() => !state.Active(usb.Id) && state.Active(other.Id) && state.Current == other.Id
            && state.VolumeWatch && state.Muted == observer.Mute(other.Id), "automatic fallback and notification subscription", 300);
        Check(!menu.Visible && state.Number(other.Id) == "1", "stale menu closed and remaining index compacted");
        OpenMenu();
        Check(!menu.Items.Cast<ToolStripItem>().Any(i => i.Text?.Contains(usb.Name) == true)
            && menu.Items.OfType<ToolStripMenuItem>().Any(i => i.Checked && i.Text!.Contains(other.Name)), "removed input absent and remaining input checked");
        Console.WriteLine("READY reconnect USB");
        Wait(() => state.Active(usb.Id) && state.Active(other.Id), "same endpoint returned", 300);
        Wait(() => state.Current == observer.Default(Role.Multimedia) && state.VolumeWatch && state.Muted == observer.Mute(state.Current!), "reconnected default and mute readback", 5);
        Check(state.Number(usb.Id) == usbNumber && state.Number(other.Id) == otherNumber, "both original numbers preserved");
        Console.WriteLine("Windows after replug: " + string.Join(", ", roles.Keys.Select(r => $"{r}=#{state.Number(observer.Default(r))}")));
        foreach (var p in roles) if (observer.Default(p.Key) != p.Value) observer.SetDefault(p.Value, p.Key);
        foreach (var p in mutes) if (observer.Mute(p.Key) != p.Value) observer.SetMute(p.Key, p.Value);
        Check(roles.All(p => observer.Default(p.Key) == p.Value) && mutes.All(p => observer.Mute(p.Key) == p.Value),
            "original three defaults and both mute flags verified");
    }
}
