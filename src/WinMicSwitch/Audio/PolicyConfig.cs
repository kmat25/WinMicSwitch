using System.Runtime.InteropServices;

namespace WinMicSwitch.Audio;

internal static class PolicyConfig
{
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicy
    {
        void GetMixFormat();
        void GetDeviceFormat();
        void ResetDeviceFormat();
        void SetDeviceFormat();
        void GetProcessingPeriod();
        void SetProcessingPeriod();
        void GetShareMode();
        void SetShareMode();
        void GetPropertyValue();
        void SetPropertyValue();
        void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, Role role);
    }

    public static void Set(string id, Role role)
    {
        var instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"), true)!)!;
        try { ((IPolicy)instance).SetDefaultEndpoint(id, role); }
        finally { Marshal.ReleaseComObject(instance); }
    }
}
