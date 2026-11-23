using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace WinMicSwitch;

// Explorer owns the tray window. NotifyIcon delivers clicks, but has no wheel event.
internal sealed class TrayWheel : NativeWindow, IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Device { public ushort Page, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Identifier { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Header { public uint Type, Size; public nint Device, Param; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MousePacket
    {
        public Header Header;
        public uint FlagsAndPadding;
        public ushort Buttons;
        public short Delta;
        public uint RawButtons;
        public int X, Y;
        public uint Extra;
    }
    [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(in Device device, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(nint handle, uint command, out MousePacket packet, ref uint size, uint headerSize);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(in Identifier icon, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetMessagePos();
    [DllImport("user32.dll")] private static extern int GetMessageTime();
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flag);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int length);

    private readonly Identifier icon;
    private readonly Action<long, int> scroll;
    private readonly NotifyIcon tray;
    private bool hovered;
    internal bool Registered { get; }

    internal TrayWheel(NotifyIcon tray, Action<long, int> scroll)
    {
        this.tray = tray;
        this.scroll = scroll;
        // .NET 10 NotifyIcon does not publicly expose its shell identifier.
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        if (typeof(NotifyIcon).GetField("_window", fields)?.GetValue(tray) is not NativeWindow owner ||
            typeof(NotifyIcon).GetField("_id", fields)?.GetValue(tray) is not uint id) return;
        icon = new Identifier { Size = (uint)Marshal.SizeOf<Identifier>(), Window = owner.Handle, Id = id };
        CreateHandle(new CreateParams { Parent = -3 }); // HWND_MESSAGE
        tray.MouseMove += OnHover;
        Registered = Register(0x100, Handle); // RIDEV_INPUTSINK
    }

    private void OnHover(object? sender, MouseEventArgs e) => hovered = true;
    private static bool Register(uint flags, nint target) =>
        RegisterRawInputDevices(new Device { Page = 1, Usage = 2, Flags = flags, Target = target }, 1, (uint)Marshal.SizeOf<Device>());

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0xff && hovered)
        {
            uint size = (uint)Marshal.SizeOf<MousePacket>();
            if (GetRawInputData(m.LParam, 0x10000003, out var packet, ref size, (uint)Marshal.SizeOf<Header>()) == size &&
                packet.Header.Type == 0 && (packet.Buttons & 0x400) != 0)
            {
                uint pos = GetMessagePos();
                var point = new Point((short)pos, (short)(pos >> 16));
                if (OverIcon(point))
                {
                    long now = Environment.TickCount64;
                    scroll(now - unchecked((uint)now - (uint)GetMessageTime()), packet.Delta);
                }
                else hovered = false;
            }
        }
        base.WndProc(ref m);
    }

    private bool OverIcon(Point point)
    {
        if (Shell_NotifyIconGetRect(icon, out var rect) != 0 ||
            !Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom).Contains(point)) return false;
        var root = GetAncestor(WindowFromPoint(point), 2);
        var name = new StringBuilder(80);
        GetClassName(root, name, name.Capacity);
        return name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or
            "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";
    }

    public void Dispose()
    {
        tray.MouseMove -= OnHover;
        if (Registered) Register(1, 0); // RIDEV_REMOVE
        if (Handle != 0) DestroyHandle();
    }
}
