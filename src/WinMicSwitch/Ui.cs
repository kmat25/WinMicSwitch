using System.Globalization;

namespace WinMicSwitch;

internal static class Ui
{
    internal static CultureInfo Language { get; private set; } = CultureInfo.CurrentUICulture;
    internal static void UseCurrentLanguage() => Language = CultureInfo.CurrentUICulture;

    internal static string Text(string japanese, string english) =>
        Language.TwoLetterISOLanguageName == "ja" ? japanese : english;
    internal static string Unknown => Text("状態不明", "Unknown");
    internal static string NoMic => Text("マイクなし", "No microphone");
    internal static string Muted => Text("消音中", "Muted");
    internal static string Unmuted => Text("収音中", "Unmuted");
    internal static string Unverified => Text("未確認", "Unverified");
    internal static string NoDefault => Text("利用可能な一般用の既定マイクを確認できません。", "The default microphone is unavailable.");
    internal static string Changed => Text("操作前に一般用の既定マイクが変わりました。", "The default microphone changed during the operation.");
    internal static string Success => Text("成功", "OK");
}
