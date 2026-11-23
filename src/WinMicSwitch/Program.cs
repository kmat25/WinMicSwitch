namespace WinMicSwitch;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var instance = new Mutex(false, @"Local\WinMicSwitch.53a5233f", out bool first);
        if (!first) return;
        Ui.UseCurrentLanguage();
        ApplicationConfiguration.Initialize();
        using var context = new TrayContext();
        Application.Run(context);
    }
}
