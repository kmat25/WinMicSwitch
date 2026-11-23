namespace WinMicSwitch;

// UI thread only. Keep the reservation until its posted continuation runs.
internal sealed class OperationGate(Func<long> clock)
{
    private Action? pending;
    private long boundary = -1;
    internal bool Executing { get; private set; }
    internal bool Busy => Executing || pending != null;
    internal bool Reserved => pending != null;

    internal void Accept(long? tick, bool refreshing, Action execute, Action report, Action completed)
    {
        if (Busy || tick <= boundary) return;
        void Run()
        {
            Executing = true;
            try { if (tick.HasValue) execute(); else report(); }
            finally { boundary = clock(); Executing = false; completed(); }
        }
        if (refreshing) pending = Run;
        else Run();
    }

    internal void RunReserved()
    {
        var run = pending;
        pending = null;
        run?.Invoke();
    }
    internal void Cancel() => pending = null;
}
