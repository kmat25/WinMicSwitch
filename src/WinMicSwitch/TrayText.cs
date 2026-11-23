using System.Globalization;

namespace WinMicSwitch;

internal static class TrayText
{
    internal static string Clean(string value) => string.Concat(value.Select(c => char.IsControl(c) ? ' ' : c));

    internal static string Tooltip(AudioState state)
    {
        string status = state.Muted == true && !state.LostWatch ? Ui.Text("ミュート", "Muted") : state.Status;
        string volume = !state.LostWatch && state.VolumePercent is int percent
            ? $"{percent}% " : "";
        string full = $"WinMicSwitch: {volume}{status}";
        if (state.LostWatch)
        {
            full += Ui.Text("／自動更新停止", " / Updates paused");
            if (state.Muted.HasValue) full += Ui.Text("／最終取得 ", " / Last: ") + AudioState.MuteText(state.Muted);
        }
        var name = Clean(state.Name(state.Current));
        var elements = StringInfo.GetTextElementEnumerator(name);
        string separator = " ";
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (full.Length + separator.Length + element.Length > 63) break;
            full += separator + element;
            separator = "";
        }
        return full;
    }
}
