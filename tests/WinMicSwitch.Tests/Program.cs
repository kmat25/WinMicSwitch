using System.Reflection;
using System.Runtime.InteropServices;
using WinMicSwitch;

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint="PostMessageW")]
    static extern bool Post(nint hwnd, int msg, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint="SendNotifyMessageW")]
    static extern bool Notify(nint hwnd, int msg, nint wParam, nint lParam);

    static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }

    [STAThread]
    static void Main(string[] args)
    {
        System.Globalization.CultureInfo.CurrentUICulture = new("ja-JP");
        Application.EnableVisualStyles();
        if (args is ["--hotplug", var usbName, var remainingName])
        {
            Hotplug(usbName, remainingName);
            return;
        }
        if (args is ["--two-mics", var first, var second])
        {
            TwoMicrophones(first, second);
            return;
        }
        using var menu = new ContextMenuStrip();
        using var capture = new InputCapture(menu, Console.WriteLine);
        Application.AddMessageFilter(capture);
        using var tray = new NotifyIcon { Icon = SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        int clicks = 0;
        void Click(object source)
        {
            Check(capture.Consume(source) is long tick && tick <= Environment.TickCount64, "queued input timestamp");
            Check(capture.Consume(source) == null, "single consumption");
            clicks++;
        }
        long? trayTick = null;
        tray.MouseDown += capture.TrayMouseDown;
        tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            trayTick = capture.ConsumeTrayClick();
            clicks++;
        };
        // Only the test needs the WinForms-owned window to inject a queued shell callback.
        var window = (NativeWindow)typeof(NotifyIcon).GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tray)!;
        Post(window.Handle, InputCapture.TrayCallback, 1, 0x202);
        Application.DoEvents();
        Check(clicks == 1 && trayTick.HasValue, "queued tray callback to MouseClick");
        void SentClick()
        {
            var hwnd = window.Handle;
            Task.Run(() =>
            {
                if (!Notify(hwnd, InputCapture.TrayCallback, 1, 0x201) ||
                    !Notify(hwnd, InputCapture.TrayCallback, 1, 0x202))
                    throw new InvalidOperationException("Cannot send tray notifications");
            }).GetAwaiter().GetResult();
            Application.DoEvents();
        }
        SentClick();
        Check(clicks == 2 && trayTick >= 0 && capture.ConsumeTrayClick() == null, "sent tray press is single-use");
        capture.EndOperation();
        SentClick();
        Check(clicks == 3 && trayTick == long.MinValue, "discard sent callbacks pending at completion");
        var mic = menu.Items.Add("テストマイク");
        mic.Click += (_, _) => Click(mic);
        menu.Show(20, 20);
        var pos = mic.Bounds;
        nint point = ((pos.Top + 2) << 16) | (pos.Left + 8);
        Post(menu.Handle, 0x201, 1, point);
        Post(menu.Handle, 0x202, 0, point);
        Application.DoEvents();
        Check(clicks == 4, "menu mouse selection");
        menu.Show(20, 20);
        mic.Select();
        Post(menu.Handle, 0x100, (int)Keys.Enter, 0);
        Application.DoEvents();
        Check(clicks == 5, "menu keyboard selection");
        long completedAt = -1;
        long? delivered = null;
        using (var delayed = new NotifyIcon { Icon = SystemIcons.Application, Visible = true })
        {
            delayed.MouseClick += (_, _) => delivered = capture.Consume(capture.TraySource);
            var delayedWindow = Field<NativeWindow>(delayed, "_window");
            Post(delayedWindow.Handle, InputCapture.TrayCallback, 1, 0x202);
            Thread.Sleep(80); // Simulate an STA blocked in a synchronous settings call.
            completedAt = Environment.TickCount64;
            Application.DoEvents();
        }
        Check(delivered < completedAt, "delayed tray input keeps its pre-completion queue time");
        capture.Dispose();
        tray.Visible = false;
        AudioTests();
        GateTests();
        WindowsSmoke();
        if (args.Contains("--live-tray")) LiveTray();
    }
}
