using System.Runtime.InteropServices;

namespace WinMicSwitch.Audio;

internal record Mic(string Id, string Name);
internal record VolumeSource(string Id, long Generation);
internal interface IAudio
{
    Mic[] List();
    string? Default(Role role);
    bool Mute(string id);
    void SetMute(string id, bool value);
    float Level(string id);
    void SetLevel(string id, float value);
    void SetDefault(string id, Role role);
}

internal sealed class CoreAudio(Action<VolumeSource?> signal) : IAudio, IDisposable
{
    private IEnumeratorApi? enumerator;
    private IEnumeratorApi Devices => enumerator ??= (IEnumeratorApi)Activator.CreateInstance(
        Type.GetTypeFromCLSID(new("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!)!;
    private DeviceEvents? devicesSink;
    private VolumeEvents? volumeSink;
    private IVolume? subscribed;
    private readonly List<(IVolume Volume, VolumeEvents Sink)> detached = [];
    private string? watchedId;
    private long generation;
    internal VolumeSource? Subscription { get; private set; }
    private Guid context = Guid.NewGuid();

    public Mic[] List()
    {
        Devices.EnumAudioEndpoints(1, 1, out var collection);
        try
        {
            collection.GetCount(out var count);
            var result = new List<Mic>();
            for (uint i = 0; i < count; i++)
            {
                collection.Item(i, out var device);
                try
                {
                    device.GetId(out var id);
                    result.Add(new Mic(id, ReadName(device)));
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            return result.ToArray();
        }
        finally { Marshal.ReleaseComObject(collection); }
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref Variant value);
    private static string ReadName(IDeviceApi device)
    {
        try
        {
            device.OpenPropertyStore(0, out var store);
            try
            {
                var key = new PropertyKey { Format = new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
                store.GetValue(ref key, out var value);
                try
                {
                    if (value.Type == 31 && Marshal.PtrToStringUni(value.Pointer) is { Length: > 0 } name) return name;
                }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch (Exception ex) when (AudioFailure.Expected(ex)) { }
        return Ui.Text("名前を取得できません", "Unknown device name");
    }

    public string? Default(Role role)
    {
        int hr = Devices.GetDefaultAudioEndpoint(1, role, out var device);
        if (hr == unchecked((int)0x80070490)) return null;
        Marshal.ThrowExceptionForHR(hr);
        try { device.GetId(out var id); return id; }
        finally { Marshal.ReleaseComObject(device); }
    }

    private IVolume OpenVolume(string id)
    {
        Devices.GetDevice(id, out var device);
        try
        {
            var iid = typeof(IVolume).GUID;
            device.Activate(ref iid, 23, 0, out var endpoint);
            return (IVolume)endpoint;
        }
        finally { Marshal.ReleaseComObject(device); }
    }

    public bool Mute(string id)
    {
        var volume = OpenVolume(id);
        try { volume.GetMute(out var muted); return muted; }
        finally { Marshal.ReleaseComObject(volume); }
    }

    public void SetMute(string id, bool value)
    {
        var volume = OpenVolume(id);
        try { volume.SetMute(value, ref context); }
        finally { Marshal.ReleaseComObject(volume); }
    }

    public float Level(string id)
    {
        var volume = OpenVolume(id);
        try { volume.GetMasterVolumeLevelScalar(out float value); return value; }
        finally { Marshal.ReleaseComObject(volume); }
    }

    public void SetLevel(string id, float value)
    {
        var volume = OpenVolume(id);
        try { volume.SetMasterVolumeLevelScalar(value, ref context); }
        finally { Marshal.ReleaseComObject(volume); }
    }

    public void SetDefault(string id, Role role) => PolicyConfig.Set(id, role);

    internal bool WatchDevices()
    {
        if (devicesSink != null) return true;
        var sink = new DeviceEvents(() => signal(null));
        Devices.RegisterEndpointNotificationCallback(sink);
        devicesSink = sink;
        return true;
    }

    internal bool WatchVolume(string? id)
    {
        if (id == watchedId) return subscribed != null;
        Detach();
        if (id == null) return false;
        var next = new VolumeSource(id, ++generation);
        var sink = new VolumeEvents(() => signal(next));
        var volume = OpenVolume(id);
        try { volume.RegisterControlChangeNotify(sink); }
        catch { Marshal.ReleaseComObject(volume); throw; }
        subscribed = volume;
        volumeSink = sink;
        watchedId = id;
        Subscription = next;
        return true;
    }

    private void Detach()
    {
        watchedId = null;
        Subscription = null;
        if (subscribed == null) return;
        try
        {
            subscribed.UnregisterControlChangeNotify(volumeSink!);
            Marshal.ReleaseComObject(subscribed);
        }
        catch (Exception ex) when (AudioFailure.Expected(ex))
        {
            // Keep the callback alive until one final unregister attempt.
            detached.Add((subscribed, volumeSink!));
        }
        subscribed = null;
        volumeSink = null;
    }

    public void Dispose()
    {
        Detach();
        foreach (var entry in detached)
        {
            try { entry.Volume.UnregisterControlChangeNotify(entry.Sink); }
            catch (Exception ex) when (AudioFailure.Expected(ex)) { }
            finally { Marshal.ReleaseComObject(entry.Volume); }
        }
        detached.Clear();
        if (enumerator == null) return;
        try { if (devicesSink != null) enumerator.UnregisterEndpointNotificationCallback(devicesSink); }
        catch (Exception ex) when (AudioFailure.Expected(ex)) { }
        finally { Marshal.ReleaseComObject(enumerator); enumerator = null; devicesSink = null; }
    }
}

internal static class AudioFailure
{
    internal static bool Expected(Exception e) => e is ExternalException or UnauthorizedAccessException
        or InvalidCastException or ArgumentException or FileNotFoundException or InvalidOperationException;
}
