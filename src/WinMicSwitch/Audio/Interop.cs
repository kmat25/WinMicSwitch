using System.Runtime.InteropServices;

namespace WinMicSwitch.Audio;

internal enum Role { Console, Multimedia, Communications }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumeratorApi
{
    void EnumAudioEndpoints(int flow, uint states, out ICollectionApi devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int flow, Role role, out IDeviceApi device);
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDeviceApi device);
    void RegisterEndpointNotificationCallback(IDeviceEvents sink);
    void UnregisterEndpointNotificationCallback(IDeviceEvents sink);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICollectionApi
{
    void GetCount(out uint count);
    void Item(uint index, out IDeviceApi device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeviceApi
{
    void Activate(ref Guid iid, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    void OpenPropertyStore(uint mode, out IStore store);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out uint state);
}

[StructLayout(LayoutKind.Sequential)]
public struct PropertyKey { public Guid Format; public uint Id; }

[StructLayout(LayoutKind.Explicit, Size=24)]
internal struct Variant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public nint Pointer;
}

[ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IStore
{
    void GetCount(out uint count);
    void GetAt(uint index, out PropertyKey key);
    void GetValue(ref PropertyKey key, out Variant value);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVolume
{
    void RegisterControlChangeNotify(IVolumeEvents sink);
    void UnregisterControlChangeNotify(IVolumeEvents sink);
    void Slot02();
    void Slot03();
    void SetMasterVolumeLevelScalar(float level, ref Guid context);
    void Slot05();
    void GetMasterVolumeLevelScalar(out float level);
    void Slot07();
    void Slot08();
    void Slot09();
    void Slot10();
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComVisible(true), Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeviceEvents
{
    [PreserveSig] int OnDeviceStateChanged(nint id, uint state);
    [PreserveSig] int OnDeviceAdded(nint id);
    [PreserveSig] int OnDeviceRemoved(nint id);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, nint id);
    [PreserveSig] int OnPropertyValueChanged(nint id, PropertyKey key);
}

[ComVisible(true), Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IVolumeEvents { [PreserveSig] int OnNotify(nint data); }

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class DeviceEvents(Action signal) : IDeviceEvents
{
    private int Changed() { signal(); return 0; }
    public int OnDeviceStateChanged(nint id, uint state) => Changed();
    public int OnDeviceAdded(nint id) => Changed();
    public int OnDeviceRemoved(nint id) => Changed();
    public int OnDefaultDeviceChanged(int flow, int role, nint id) => flow is 1 or 2 && role is >= 0 and <= 2 ? Changed() : 0;
    public int OnPropertyValueChanged(nint id, PropertyKey key) => Changed();
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class VolumeEvents(Action signal) : IVolumeEvents
{
    public int OnNotify(nint data) { signal(); return 0; }
}
