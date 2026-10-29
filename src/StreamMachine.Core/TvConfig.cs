using System.Net;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace StreamMachine.Core;

/// <summary>One TV the service controls. Several can be set up; each has its own input and on/off rules.</summary>
public sealed record TvEntry
{
    public string Name { get; init; } = "TV";
    public string Mac { get; init; } = "";
    public string? Model { get; init; }
    /// <summary>The HDMI input this PC is plugged into on this TV (1-4).</summary>
    public int Input { get; init; } = 1;
    public bool OnBoot { get; init; } = true;
    public bool OnWake { get; init; } = true;
    public bool OffSleep { get; init; } = true;
    public bool OffShutdown { get; init; } = true;

    /// <summary>Pairing keys and the cached address live in a folder per TV, named by MAC.</summary>
    public string KeyDir(string dataDir) => Path.Combine(dataDir, "keys", KeyFolder(Mac));

    public static string KeyFolder(string mac) => Convert.ToHexString(Core.Mac.Parse(mac)).ToLowerInvariant();

    /// <summary>Applies one validated "set" key (from <see cref="Protocol.Settable"/>).</summary>
    public TvEntry With(string key, string value) => key switch
    {
        "tv-on-boot" => this with { OnBoot = value == "on" },
        "tv-on-wake" => this with { OnWake = value == "on" },
        "tv-off-sleep" => this with { OffSleep = value == "on" },
        "tv-off-shutdown" => this with { OffShutdown = value == "on" },
        "tv-input" => this with { Input = Math.Clamp(value[^1] - '0', 1, 4) },
        _ => this,
    };
}

/// <summary>A TV found on the network during setup.</summary>
public sealed record FoundTv(string Ip, string Mac, string Name, string? Model, bool Paired);

public static class TvScanner
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    /// <summary>LG webOS TVs that answer SSDP right now (they must be on, or in quick-start standby).</summary>
    public static async Task<List<FoundTv>> ScanAsync(Func<string, bool> isPaired, ITvNetwork? net = null, CancellationToken ct = default)
    {
        net ??= new RealTvNetwork();
        var list = new List<FoundTv>();
        foreach (var (ip, location) in await Ssdp.SearchDetailedAsync(TimeSpan.FromSeconds(2.5), ct))
        {
            if (!await net.PortOpenAsync(ip, 3000, TimeSpan.FromMilliseconds(800), ct) && !await net.PortOpenAsync(ip, 3001, TimeSpan.FromMilliseconds(800), ct)) continue;
            var mac = net.ArpResolve(ip);
            if (mac == null) continue;
            var (name, model) = await DescribeAsync(location, ct);
            list.Add(new FoundTv(ip.ToString(), mac, name ?? $"LG TV ({ip})", model, isPaired(mac)));
        }
        return list.OrderBy(t => t.Name).ToList();
    }

    /// <summary>friendlyName and modelName from the TV's UPnP description, if it serves one.</summary>
    internal static async Task<(string? Name, string? Model)> DescribeAsync(string? location, CancellationToken ct)
    {
        if (location == null || !Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme != "http") return (null, null);
        try { return ParseDescription(await Http.GetStringAsync(uri, ct)); }
        catch { return (null, null); }
    }

    internal static (string? Name, string? Model) ParseDescription(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            string? Get(string n) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == n)?.Value.Trim() is { Length: > 0 } v ? v : null;
            var name = Get("friendlyName");
            // Some firmware wraps the name in quotes or adds the model in brackets.
            name = name?.Trim('"', ' ');
            return (name, Get("modelName") ?? Get("modelNumber"));
        }
        catch { return (null, null); }
    }

    /// <summary>
    /// Pairs with a TV: the TV shows one "allow" prompt for every permission Stream Machine uses (power, inputs, volume).
    /// Returns the client key, or throws when the prompt isn't accepted in time.
    /// </summary>
    public static async Task<string> PairAsync(IPAddress ip, string keyDir, TimeSpan acceptWithin, CancellationToken ct = default)
    {
        await using var c = await TvClient.ConnectAsync(ip, TimeSpan.FromSeconds(5), ct: ct);
        var key = await c.RegisterAsync(Ssap.AllPermissions, null, acceptWithin, ct);
        if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("the TV accepted but returned no key");
        Directory.CreateDirectory(keyDir);
        File.WriteAllText(Path.Combine(keyDir, "client-key.txt"), key);
        File.WriteAllText(Path.Combine(keyDir, "permissions.txt"), "all");
        File.WriteAllText(Path.Combine(keyDir, "tv-ip.txt"), ip.ToString());
        Log.Info("tv", "paired", ("ip", ip));
        return key;
    }
}
