using System.Runtime.InteropServices;
using Microsoft.Win32;
using StreamMachine.Core;

namespace StreamMachine.Agent;

/// <summary>Default playback device (the TV's HDMI audio) for all three roles: console, multimedia, communications.</summary>
public static class Audio
{
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int ctx, IntPtr p, out IntPtr o);
        int OpenPropertyStore(int access, out IntPtr store);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    // Undocumented but stable since Vista; what the Sound control panel itself uses.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        int GetMixFormat(); int GetDeviceFormat(); int ResetDeviceFormat(); int SetDeviceFormat();
        int GetProcessingPeriod(); int SetProcessingPeriod(); int GetShareMode(); int SetShareMode();
        int GetPropertyValue(); int SetPropertyValue();
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        int SetEndpointVisibility();
    }

    static readonly Guid EnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid PolicyConfigClsid = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    const string RenderKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    const string DescriptionProp = "{a45c254e-df1c-4efd-8020-67d146a850e0},2";
    const string InterfaceProp = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";

    public sealed record Endpoint(string Id, string Name, bool Active);

    /// <summary>Playback endpoints from the registry (readable by users): id "{0.0.0.00000000}.{guid}" and "Speakers (Device)" style name.</summary>
    public static List<Endpoint> RenderEndpoints()
    {
        var list = new List<Endpoint>();
        using var root = Registry.LocalMachine.OpenSubKey(RenderKey);
        if (root == null) return list;
        foreach (var guid in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(guid);
            using var props = root.OpenSubKey(guid + @"\Properties");
            if (k == null || props == null) continue;
            var state = Convert.ToInt32(k.GetValue("DeviceState") ?? 0);
            var desc = props.GetValue(DescriptionProp) as string ?? "";
            var iface = props.GetValue(InterfaceProp) as string ?? "";
            list.Add(new Endpoint("{0.0.0.00000000}." + guid, iface.Length > 0 ? $"{desc} ({iface})" : desc, (state & 0xF) == 1));
        }
        return list;
    }

    public static string? DefaultId(int role)
    {
        try
        {
            var e = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClsid)!)!;
            if (e.GetDefaultAudioEndpoint(0, role, out var d) != 0) return null;
            d.GetId(out var id);
            return id;
        }
        catch { return null; }
    }

    public static Endpoint? Find(string match) =>
        RenderEndpoints().Where(e => e.Active && e.Name.Contains(match, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

    /// <summary>True when the matching device is the default for every role.</summary>
    public static bool IsDefault(string match)
    {
        var ep = Find(match);
        return ep != null && Enumerable.Range(0, 3).All(r => string.Equals(DefaultId(r), ep.Id, StringComparison.OrdinalIgnoreCase));
    }

    /// <param name="roles">0 console, 1 multimedia, 2 communications; all three when null.</param>
    public static string SetDefault(string match, IEnumerable<int>? roles = null)
    {
        var ep = Find(match);
        if (ep == null) return $"no active playback device matching '{match}'";
        var pc = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(PolicyConfigClsid)!)!;
        foreach (var role in roles ?? [0, 1, 2])
        {
            var hr = pc.SetDefaultEndpoint(ep.Id, role);
            if (hr != 0) return $"SetDefaultEndpoint role {role} failed: 0x{hr:X8}";
        }
        Log.Info("audio", "default playback and communications device set", ("device", ep.Name));
        return "default: " + ep.Name;
    }
}
