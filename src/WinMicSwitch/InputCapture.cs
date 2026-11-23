using System.Runtime.InteropServices;

namespace WinMicSwitch;

// The queued message is dispatched synchronously inside a single-use scope.
// An item click can only claim the specific item hit by this mouse/key message.
internal sealed class InputCapture(ContextMenuStrip menu, Action<string>? trace = null) : IMessageFilter, IDisposable
{
    internal const int TrayCallback = 0x800;
    private sealed record Stamp(object Source, long Tick, long Sequence);
    private Stamp? active;
    private long sequence;
    private nint trayWindow;
    internal object TraySource { get; } = new();
    private long? sentPress;
    private bool drainingSentInput;

    [DllImport("user32.dll")] private static extern uint InSendMessageEx(nint reserved);

    // Explorer can send directly to the NotifyIcon window, bypassing IMessageFilter.
    // Bind the press to this icon and consume it only in its left MouseClick.
    internal void TrayMouseDown(object? sender, MouseEventArgs e)
    {
        sentPress = e.Button == MouseButtons.Left && e.Clicks == 1 && InSendMessageEx(0) != 0
            ? (drainingSentInput ? long.MinValue : Environment.TickCount64) : null;
    }

    internal long? ConsumeTrayClick()
    {
        var press = sentPress;
        sentPress = null;
        var queued = Consume(TraySource);
        if (queued.HasValue || InSendMessageEx(0) == 0) return queued;
        return drainingSentInput ? long.MinValue : press;
    }

    // Sent notifications have no MSG.time. Discard those waiting for the STA
    // after a write/dialog, until its message pump has drained and reached idle.
    internal void EndOperation()
    {
        sentPress = long.MinValue;
        if (drainingSentInput) return;
        drainingSentInput = true;
        Application.Idle += Rearm;
    }

    private void Rearm(object? sender, EventArgs args)
    {
        Application.Idle -= Rearm;
        drainingSentInput = false;
    }

    [DllImport("user32.dll")] private static extern int GetMessageTime();
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint Dispatch(nint hwnd, int msg, nint wParam, nint lParam);

    internal long? Consume(object source)
    {
        var stamp = active;
        active = null;
        if (stamp == null || !ReferenceEquals(stamp.Source, source)) return null;
        trace?.Invoke($"consume #{stamp.Sequence} tick={stamp.Tick} source={(source as ToolStripItem)?.Text ?? "Tray"}");
        return stamp.Tick;
    }

    public bool PreFilterMessage(ref Message m)
    {
        active = null;
        // The shell's right-button callback opens the menu while the hidden tray
        // window owns keyboard focus. WinForms can route Enter from either HWND.
        if (m.Msg == TrayCallback && m.LParam == 0x205)
        {
            Dispatch(m.HWnd, m.Msg, m.WParam, m.LParam);
            if (menu.Visible) trayWindow = m.HWnd;
            return true;
        }
        bool tray = m.Msg == TrayCallback && m.LParam == 0x202;
        bool inMenu = menu.Visible && menu.IsHandleCreated && m.HWnd == menu.Handle;
        var mouseItem = inMenu && m.Msg == 0x202
            ? menu.GetItemAt(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)))
            : null;
        var keyItem = menu.Visible && (inMenu || m.HWnd == trayWindow) && m.Msg == 0x100 && m.WParam == (int)Keys.Enter
            ? menu.Items.Cast<ToolStripItem>().FirstOrDefault(item => item.Selected)
            : null;
        object? source = tray ? TraySource : mouseItem ?? keyItem;
        if (source == null) return false;
        var now = Environment.TickCount64;
        long tick = now - unchecked((uint)now - (uint)GetMessageTime());
        active = new Stamp(source, tick, ++sequence);
        trace?.Invoke($"capture #{sequence} hwnd={m.HWnd:X} msg={m.Msg:X} wParam={m.WParam:X} lParam={m.LParam:X} tick={tick}");
        try
        {
            if (keyItem != null)
            {
                keyItem.PerformClick();
                menu.Close();
            }
            else Dispatch(m.HWnd, m.Msg, m.WParam, m.LParam);
        }
        finally { active = null; }
        return true;
    }

    public void Dispose()
    {
        Application.Idle -= Rearm;
        Application.RemoveMessageFilter(this);
    }
}
