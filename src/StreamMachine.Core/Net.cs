using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace StreamMachine.Core;

public static class Mac
{
    /// <summary>Parses A0-B1-C2-D3-E4-F5, a0:b1:c2:d3:e4:f5 or a0b1c2d3e4f5.</summary>
    public static byte[] Parse(string mac)
    {
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) throw new FormatException($"not a MAC address: {mac}");
        return Convert.FromHexString(hex);
    }

    public static string Format(ReadOnlySpan<byte> mac) => string.Join("-", mac.ToArray().Select(b => b.ToString("X2")));

    public static bool Same(string a, string b)
    {
        try { return Parse(a).AsSpan().SequenceEqual(Parse(b)); } catch { return false; }
    }
}

public static class WakeOnLan
{
    public static byte[] Packet(string mac)
    {
        var m = Mac.Parse(mac);
        var p = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) p[i] = 0xFF;
        for (int r = 0; r < 16; r++) Buffer.BlockCopy(m, 0, p, 6 + r * 6, 6);
        return p;
    }

    /// <summary>The directed broadcast address of an IPv4 address/prefix, e.g. 192.168.0.20/24 -> 192.168.0.255.</summary>
    public static IPAddress Broadcast(IPAddress address, int prefixLength)
    {
        var a = address.GetAddressBytes();
        uint ip = (uint)(a[0] << 24 | a[1] << 16 | a[2] << 8 | a[3]);
        uint mask = prefixLength <= 0 ? 0 : prefixLength >= 32 ? 0xFFFFFFFF : 0xFFFFFFFF << (32 - prefixLength);
        uint bc = ip | ~mask;
        return new IPAddress(new[] { (byte)(bc >> 24), (byte)(bc >> 16), (byte)(bc >> 8), (byte)bc });
    }

    /// <summary>255.255.255.255 plus the broadcast address of every up IPv4 interface, sent from each interface.</summary>
    public static List<(IPAddress Local, IPAddress Target)> Targets()
    {
        var list = new List<(IPAddress, IPAddress)> { (IPAddress.Any, IPAddress.Broadcast) };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(u.Address)) continue;
                if (u.Address.GetAddressBytes() is [169, 254, ..]) continue;
                list.Add((u.Address, Broadcast(u.Address, u.PrefixLength)));
                list.Add((u.Address, IPAddress.Broadcast));
            }
        }
        return list;
    }

    /// <summary>Sends the magic packet to every broadcast target on ports 9 and 7. Returns how many sends succeeded.</summary>
    public static int Send(string mac, IPAddress? knownIp = null)
    {
        var packet = Packet(mac);
        int ok = 0;
        var targets = Targets();
        if (knownIp != null) targets.Add((IPAddress.Any, knownIp));
        foreach (var (local, target) in targets)
        {
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
                s.Bind(new IPEndPoint(local, 0));
                foreach (var port in new[] { 9, 7 }) { s.SendTo(packet, new IPEndPoint(target, port)); ok++; }
            }
            catch { /* the network may still be coming up */ }
        }
        return ok;
    }
}

public static class Arp
{
    [StructLayout(LayoutKind.Sequential)]
    struct MIB_IPNETROW
    {
        public int dwIndex;
        public int dwPhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] bPhysAddr;
        public uint dwAddr;
        public int dwType;
    }

    [DllImport("iphlpapi.dll")] static extern int GetIpNetTable(IntPtr table, ref int size, bool order);
    [DllImport("iphlpapi.dll")] static extern int SendARP(uint dest, uint src, byte[] mac, ref int len);

    /// <summary>IPv4 addresses the ARP/neighbour cache maps to <paramref name="mac"/>.</summary>
    public static List<IPAddress> Lookup(string mac)
    {
        if (!OperatingSystem.IsWindows()) return ParseProcArp(ReadProcArp()).Where(e => Mac.Same(e.Mac, mac)).Select(e => e.Ip).ToList();
        var want = Mac.Parse(mac);
        var found = new List<IPAddress>();
        int size = 0;
        GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size <= 0) return found;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buf, ref size, false) != 0) return found;
            int n = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<MIB_IPNETROW>();
            for (int i = 0; i < n; i++)
            {
                var row = Marshal.PtrToStructure<MIB_IPNETROW>(buf + 4 + i * rowSize);
                if (row.dwPhysAddrLen != 6 || row.dwType == 2 /* invalid */) continue;
                if (row.bPhysAddr.AsSpan(0, 6).SequenceEqual(want)) found.Add(new IPAddress(row.dwAddr));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return found;
    }

    /// <summary>The address's MAC: a real ARP request on Windows; the kernel's neighbour table on Linux (callers talk to the
    /// address first, which refreshes it). Null when nothing answers.</summary>
    public static string? Resolve(IPAddress ip)
    {
        if (!OperatingSystem.IsWindows()) return ParseProcArp(ReadProcArp()).Where(e => e.Ip.Equals(ip)).Select(e => e.Mac).FirstOrDefault();
        var mac = new byte[6];
        int len = mac.Length;
        uint dest = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
        return SendARP(dest, 0, mac, ref len) == 0 && len == 6 ? Mac.Format(mac) : null;
    }

    static string ReadProcArp()
    {
        try { return File.ReadAllText("/proc/net/arp"); } catch { return ""; }
    }

    /// <summary>Parses Linux /proc/net/arp ("IP address HW type Flags HW address Mask Device"); incomplete entries are skipped.</summary>
    internal static List<(IPAddress Ip, string Mac)> ParseProcArp(string text)
    {
        var list = new List<(IPAddress, string)>();
        foreach (var line in text.Split('\n').Skip(1))
        {
            var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 4 || f[2] == "0x0" || f[3] == "00:00:00:00:00:00") continue;
            if (IPAddress.TryParse(f[0], out var ip)) list.Add((ip, Mac.Format(Mac.Parse(f[3]))));
        }
        return list;
    }
}

public static class Ssdp
{
    public const string WebOsSearchTarget = "urn:lge-com:service:webos-second-screen:1";

    public static string SearchMessage(string st, int mx = 1) =>
        "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\n" + $"MX: {mx}\r\nST: {st}\r\n\r\n";

    /// <summary>True when an SSDP response is for the webOS second-screen service.</summary>
    public static bool IsWebOsResponse(string response) =>
        response.StartsWith("HTTP/1.1 200", StringComparison.OrdinalIgnoreCase) &&
        response.Contains(WebOsSearchTarget, StringComparison.OrdinalIgnoreCase);

    public static async Task<List<IPAddress>> SearchAsync(TimeSpan wait, CancellationToken ct = default) =>
        (await SearchDetailedAsync(wait, ct)).Select(r => r.Ip).ToList();

    /// <summary>Every webOS TV that answered, with the LOCATION of its UPnP description (for its name).</summary>
    public static async Task<List<(IPAddress Ip, string? Location)>> SearchDetailedAsync(TimeSpan wait, CancellationToken ct = default)
    {
        var found = new List<(IPAddress Ip, string? Location)>();
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var msg = Encoding.ASCII.GetBytes(SearchMessage(WebOsSearchTarget));
        var group = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
        try { await udp.SendAsync(msg, group, ct); await Task.Delay(100, ct); await udp.SendAsync(msg, group, ct); }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return found; }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(wait);
        while (!cts.IsCancellationRequested)
        {
            try
            {
                var r = await udp.ReceiveAsync(cts.Token);
                var text = Encoding.ASCII.GetString(r.Buffer);
                if (IsWebOsResponse(text) && !found.Any(f => f.Ip.Equals(r.RemoteEndPoint.Address)))
                    found.Add((r.RemoteEndPoint.Address, Header(text, "LOCATION")));
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { break; }
        }
        return found;
    }

    internal static string? Header(string response, string name)
    {
        foreach (var line in response.Split("\r\n"))
        {
            var i = line.IndexOf(':');
            if (i > 0 && line[..i].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return line[(i + 1)..].Trim();
        }
        return null;
    }
}

public static class Tcp
{
    public static async Task<bool> PortOpenAsync(IPAddress ip, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        using var c = new TcpClient(AddressFamily.InterNetwork);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await c.ConnectAsync(ip, port, cts.Token); return c.Connected; }
        catch { return false; }
    }
}
