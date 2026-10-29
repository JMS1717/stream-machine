using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamMachine.Core;

/// <summary>Builds and parses LG webOS SSAP (second-screen) websocket messages.</summary>
public static class Ssap
{
    /// <summary>The permissions the existing pairing key was granted. Changing this set makes the TV ask again.</summary>
    public static readonly string[] ControlPermissions =
        ["LAUNCH", "CONTROL_INPUT_TV", "CONTROL_POWER", "READ_INPUT_DEVICE_LIST", "READ_RUNNING_APPS", "READ_CURRENT_CHANNEL"];

    /// <summary>Volume uses its own key, so asking for audio rights never disturbs the control pairing.</summary>
    public static readonly string[] AudioPermissions = ["CONTROL_AUDIO", "READ_TV_CURRENT_TIME"];

    public const string Hdmi1AppId = "com.webos.app.hdmi1";
    public const string Hdmi1InputId = "HDMI_1";

    public const string UriLaunch = "ssap://system.launcher/launch";
    public const string UriSwitchInput = "ssap://tv/switchInput";
    public const string UriTurnOff = "ssap://system/turnOff";
    public const string UriGetPowerState = "ssap://com.webos.service.tvpower/power/getPowerState";
    public const string UriTurnOnScreen = "ssap://com.webos.service.tvpower/power/turnOnScreen";
    public const string UriVolumeUp = "ssap://audio/volumeUp";
    public const string UriVolumeDown = "ssap://audio/volumeDown";
    public const string UriAudioStatus = "ssap://audio/getStatus";
    public const string UriSetMute = "ssap://audio/setMute";
    public const string UriForegroundApp = "ssap://com.webos.applicationManager/getForegroundAppInfo";
    public const string UriGetVolume = "ssap://audio/getVolume";
    public const string UriSetVolume = "ssap://audio/setVolume";

    /// <summary>New pairings ask for everything at once, so the TV shows a single "allow" prompt.</summary>
    public static readonly string[] AllPermissions = [.. ControlPermissions, .. AudioPermissions];

    public static string Register(string id, string[] permissions, string? clientKey)
    {
        var payload = new JsonObject
        {
            ["forcePairing"] = false,
            ["pairingType"] = "PROMPT",
            ["manifest"] = new JsonObject
            {
                ["manifestVersion"] = 1,
                ["appVersion"] = "1.0",
                ["permissions"] = new JsonArray(permissions.Select(p => (JsonNode)p!).ToArray()),
            },
        };
        if (!string.IsNullOrWhiteSpace(clientKey)) payload["client-key"] = clientKey.Trim();
        return new JsonObject { ["type"] = "register", ["id"] = id, ["payload"] = payload }.ToJsonString();
    }

    public static string Request(string id, string uri, JsonObject? payload = null)
    {
        if (!uri.StartsWith("ssap://", StringComparison.Ordinal)) throw new ArgumentException("not an ssap uri", nameof(uri));
        return new JsonObject { ["type"] = "request", ["id"] = id, ["uri"] = uri, ["payload"] = payload ?? new JsonObject() }.ToJsonString();
    }

    public static string HdmiAppId(int n) => $"com.webos.app.hdmi{Math.Clamp(n, 1, 4)}";
    public static string HdmiInputId(int n) => $"HDMI_{Math.Clamp(n, 1, 4)}";
    public static string LaunchHdmi1(string id) => Request(id, UriLaunch, new JsonObject { ["id"] = Hdmi1AppId });
    public static string SwitchInputHdmi1(string id) => Request(id, UriSwitchInput, new JsonObject { ["inputId"] = Hdmi1InputId });
    public static string SetMute(string id, bool mute) => Request(id, UriSetMute, new JsonObject { ["mute"] = mute });

    public static SsapMessage Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        JsonElement? payload = r.TryGetProperty("payload", out var p) ? p.Clone() : null;
        return new SsapMessage(Str("type"), Str("id"), Str("error"), payload);
    }
}

public sealed record SsapMessage(string Type, string Id, string Error, JsonElement? Payload)
{
    public bool IsError => Type == "error" || (Payload is { ValueKind: JsonValueKind.Object } p &&
                                               p.TryGetProperty("returnValue", out var rv) && rv.ValueKind == JsonValueKind.False);

    public string? PayloadString(string name) =>
        Payload is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public bool? PayloadBool(string name) =>
        Payload is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    public int? PayloadInt(string name) =>
        Payload is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : null;

    /// <summary>Volume from audio/getVolume: top-level "volume" (older webOS) or "volumeStatus.volume" (webOS 5+).</summary>
    public int? Volume
    {
        get
        {
            if (PayloadInt("volume") is int v) return v;
            if (Payload is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("volumeStatus", out var vs) &&
                vs.ValueKind == JsonValueKind.Object && vs.TryGetProperty("volume", out var vv) && vv.TryGetInt32(out var i)) return i;
            return null;
        }
    }

    public bool? Muted
    {
        get
        {
            if (PayloadBool("muted") is bool m) return m;
            if (PayloadBool("mute") is bool m2) return m2;
            if (Payload is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("volumeStatus", out var vs) &&
                vs.ValueKind == JsonValueKind.Object && vs.TryGetProperty("muteStatus", out var ms) && ms.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return ms.GetBoolean();
            return null;
        }
    }

    /// <summary>The pairing prompt notice the TV sends while it waits for "allow".</summary>
    public bool IsPairingPrompt => Type == "response" && PayloadString("pairingType") == "PROMPT";

    public override string ToString() => IsError ? $"{Type}:{(Error.Length > 0 ? Error : PayloadString("errorText"))}" : Type;
}
