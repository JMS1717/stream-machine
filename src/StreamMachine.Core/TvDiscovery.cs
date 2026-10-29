using System.Net;

namespace StreamMachine.Core;

/// <summary>The network operations discovery needs; swapped for a fake in tests.</summary>
public interface ITvNetwork
{
    IEnumerable<IPAddress> ArpLookup(string mac);
    string? ArpResolve(IPAddress ip);
    Task<bool> PortOpenAsync(IPAddress ip, int port, TimeSpan timeout, CancellationToken ct);
    Task<List<IPAddress>> SsdpSearchAsync(TimeSpan wait, CancellationToken ct);
}

public sealed class RealTvNetwork : ITvNetwork
{
    public IEnumerable<IPAddress> ArpLookup(string mac) => Arp.Lookup(mac);
    public string? ArpResolve(IPAddress ip) => Arp.Resolve(ip);
    public Task<bool> PortOpenAsync(IPAddress ip, int port, TimeSpan timeout, CancellationToken ct) => Tcp.PortOpenAsync(ip, port, timeout, ct);
    public Task<List<IPAddress>> SsdpSearchAsync(TimeSpan wait, CancellationToken ct) => Ssdp.SearchAsync(wait, ct);
}

public enum DiscoverySource { Arp, Cache, Ssdp }

/// <summary>
/// Finds the TV's current address while its network remote is up: ARP cache by MAC, then the cached IP, then SSDP.
/// Every candidate must have the SSAP port open and answer ARP with the TV's MAC, so a DHCP reshuffle can never
/// make us talk to a different device.
/// </summary>
public sealed class TvDiscovery(string mac, ITvNetwork net)
{
    public string TvMac { get; } = mac;

    public async Task<(IPAddress Ip, DiscoverySource Source)?> FindAsync(IPAddress? cached, bool allowSsdp, TimeSpan probe, CancellationToken ct = default)
    {
        var tried = new HashSet<IPAddress>();
        var candidates = new List<(IPAddress, DiscoverySource)>();
        foreach (var ip in net.ArpLookup(TvMac)) candidates.Add((ip, DiscoverySource.Arp));
        if (cached != null) candidates.Add((cached, DiscoverySource.Cache));

        foreach (var (ip, src) in candidates)
        {
            if (!tried.Add(ip)) continue;
            if (await IsTvAsync(ip, probe, ct)) return (ip, src);
        }
        if (!allowSsdp) return null;
        foreach (var ip in await net.SsdpSearchAsync(TimeSpan.FromSeconds(1.5), ct))
        {
            if (!tried.Add(ip)) continue;
            if (await IsTvAsync(ip, probe, ct)) return (ip, DiscoverySource.Ssdp);
        }
        return null;
    }

    async Task<bool> IsTvAsync(IPAddress ip, TimeSpan probe, CancellationToken ct)
    {
        var open = await net.PortOpenAsync(ip, 3000, probe, ct) || await net.PortOpenAsync(ip, 3001, probe, ct);
        if (!open) return false;
        // Talking to the address just refreshed Windows' ARP entry, so this is current.
        var seen = net.ArpResolve(ip);
        return seen != null && Mac.Same(seen, TvMac);
    }
}
