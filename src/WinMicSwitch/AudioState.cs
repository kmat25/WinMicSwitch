using WinMicSwitch.Audio;

namespace WinMicSwitch;

internal record Result<T>(T Value, string? Error)
{
    internal bool Ok => Error == null;
    internal static Result<T> Read(Func<T> read)
    {
        try { return new(read(), null); }
        catch (Exception ex) when (AudioFailure.Expected(ex)) { return new(default!, $"0x{ex.HResult:X8}"); }
    }
}

internal sealed class AudioState(IAudio audio)
{
    internal static readonly Role[] Roles = [Role.Console, Role.Multimedia, Role.Communications];
    internal static string Label(Role role) => role switch
    {
        Role.Console => Ui.Text("コンソール用", "Console"), Role.Multimedia => Ui.Text("一般用", "Multimedia"), _ => Ui.Text("通信用", "Communications")
    };
    private readonly Dictionary<string, int> numbers = new(StringComparer.Ordinal);
    internal Mic[] Mics { get; private set; } = [];
    internal bool Listed { get; private set; }
    internal Dictionary<Role, Result<string?>> Defaults { get; } = [];
    internal string? Current => Defaults.GetValueOrDefault(Role.Multimedia)?.Value;
    internal bool? Muted { get; private set; }
    internal int? VolumePercent { get; private set; }
    internal bool DeviceWatch { get; private set; }
    internal bool VolumeWatch { get; private set; }
    internal void ClearWatches() { DeviceWatch = false; VolumeWatch = false; }
    internal bool Active(string? id) => Mics.Any(m => m.Id == id);
    internal string Number(string? id) => id != null && numbers.TryGetValue(id, out int n) ? n.ToString() : "—";
    internal string Name(string? id) => Mics.FirstOrDefault(m => m.Id == id)?.Name ?? "";
    internal bool LostWatch => !DeviceWatch || (Active(Current) && !VolumeWatch);
    internal bool Empty => Listed && Mics.Length == 0;
    internal string Status => Empty ? Ui.NoMic : !Muted.HasValue || LostWatch ? Ui.Unknown : MuteText(Muted);
    internal static string MuteText(bool? value) => value switch { true => Ui.Muted, false => Ui.Unmuted, _ => Ui.Unverified };

    internal bool Refresh(Func<bool> watchDevices, Func<string?, bool> watchVolume)
    {
        DeviceWatch = Result<bool>.Read(watchDevices).Value;
        var list = Result<Mic[]>.Read(audio.List);
        Listed = list.Ok;
        Mics = (list.Value ?? []).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Id, StringComparer.Ordinal).ToArray();
        numbers.Clear();
        for (int i = 0; i < Mics.Length; i++) numbers[Mics[i].Id] = i + 1;
        foreach (var role in Roles) Defaults[role] = Result<string?>.Read(() => audio.Default(role));
        var id = Current;
        if (id != wheelEndpoint || !Active(id)) wheelRemainder = 0;
        VolumeWatch = Result<bool>.Read(() => watchVolume(Active(id) ? id : null)).Value;
        Muted = null;
        VolumePercent = null;
        if (id == null || !Active(id)) return Listed && id != null;
        var mute = Result<bool>.Read(() => audio.Mute(id));
        var level = Result<float>.Read(() => audio.Level(id));
        var check = Result<string?>.Read(() => audio.Default(Role.Multimedia));
        if (!check.Ok || check.Value != id)
        {
            Defaults[Role.Multimedia] = check;
            return check.Ok;
        }
        if (level.Ok && level.Value is >= 0 and <= 1)
            VolumePercent = (int)MathF.Round(level.Value * 100, MidpointRounding.AwayFromZero);
        if (mute.Ok) Muted = mute.Value;
        return false;
    }

    internal string? Toggle()
    {
        var id = audio.Default(Role.Multimedia);
        if (id == null || !Available(id)) return Ui.NoDefault;
        bool wanted = !audio.Mute(id);
        if (audio.Default(Role.Multimedia) != id) return Ui.Changed;
        var write = Write(() => audio.SetMute(id, wanted));
        var after = Result<bool>.Read(() => audio.Mute(id));
        var role = Result<string?>.Read(() => audio.Default(Role.Multimedia));
        if (write.Ok && after.Ok && after.Value == wanted && role.Ok && role.Value == id) return null;
        return Ui.Text($"対象 #{Number(id)}　設定 {write.Error ?? "成功"}／再取得 {MuteText(after.Ok ? after.Value : null)}\n一般用の既定入力　{Describe(role)}",
            $"Microphone #{Number(id)}: write {write.Error ?? Ui.Success}, readback {MuteText(after.Ok ? after.Value : null)}\nMultimedia default: {Describe(role)}");
    }

    private string? wheelEndpoint;
    private int wheelRemainder;

    internal string? AdjustVolume(int delta)
    {
        var id = audio.Default(Role.Multimedia);
        if (id == null || !Available(id)) return Ui.NoDefault;
        if (wheelEndpoint != id)
        {
            wheelEndpoint = id;
            wheelRemainder = 0;
        }
        // High-resolution wheels may send several smaller deltas for one notch.
        int steps = Math.DivRem(wheelRemainder + delta, 120, out wheelRemainder);
        if (steps == 0) return null;
        float current = audio.Level(id);
        float target = Math.Clamp(current + steps * .02f, 0f, 1f);
        if (audio.Default(Role.Multimedia) != id) return Ui.Changed;
        if (target != current) audio.SetLevel(id, target);
        float actual = audio.Level(id);
        if (audio.Default(Role.Multimedia) != id) return Ui.Changed;
        return Math.Abs(actual - target) <= .01f ? null :
            Ui.Text($"入力音量の再取得値は {actual:P0} です。", $"Input volume read back as {actual:P0}.");
    }

    private bool Available(string id) => audio.List().Any(m => m.Id == id);
    private static Result<bool> Write(Action write) => Result<bool>.Read(() => { write(); return true; });
    internal string Describe(Result<string?> role) => !role.Ok ? Ui.Unverified : role.Value == null ? Ui.Text("既定なし", "No default") : $"#{Number(role.Value)} {Name(role.Value)}";

    internal string? Select(string id)
    {
        bool available = Result<bool>.Read(() => Available(id)).Value;
        var before = available ? Result<bool>.Read(() => audio.Mute(id)) : new Result<bool>(false, "対象を確認できません");
        var writes = new Dictionary<Role, string?>();
        foreach (var role in Roles)
        {
            available = available && Result<bool>.Read(() => Available(id)).Value;
            writes[role] = available ? Write(() => audio.SetDefault(id, role)).Error : Ui.Text("未実行（対象を確認できません）", "Skipped (microphone unavailable)");
        }
        var lines = new List<string>();
        bool success = true;
        foreach (var role in Roles)
        {
            var actual = Result<string?>.Read(() => audio.Default(role));
            bool match = actual.Ok && actual.Value == id;
            success &= match && writes[role] == null;
            lines.Add(Ui.Text($"{Label(role)}　{(match ? "選択先を確認" : Describe(actual))}／設定 {writes[role] ?? Ui.Success}",
                $"{Label(role)}: {(match ? "Selected microphone confirmed" : Describe(actual))}, write {writes[role] ?? Ui.Success}"));
        }
        var after = Result<bool>.Read(() => audio.Mute(id));
        if (success && before.Ok && after.Ok && before.Value == after.Value) return null;
        lines.Add(Ui.Text($"選択先のミュート　変更前 {MuteText(before.Ok ? before.Value : null)} → 変更後 {MuteText(after.Ok ? after.Value : null)}",
            $"Selected microphone: {MuteText(before.Ok ? before.Value : null)} → {MuteText(after.Ok ? after.Value : null)}"));
        return string.Join(Environment.NewLine, lines);
    }
}
