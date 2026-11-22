using System.Security.Cryptography;
using System.Text.Json;

namespace School.Training;

public static class SimulationDeviceConfiguration
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static SimulatedDeviceState Read(TrainingDevice device, bool startup = false)
    {
        var json = startup ? device.SavedConfigurationJson : device.ConfigurationJson;
        return TryRead(json) ?? new SimulatedDeviceState
        {
            Hostname = DefaultHostname(device.Name),
            Interfaces = Enumerable.Range(0, 4).ToDictionary(index => $"ethernet0/{index}", _ => new SimulatedInterface())
        };
    }

    public static string Serialize(SimulatedDeviceState state) => JsonSerializer.Serialize(state, Json);

    public static string Version(TrainingDevice device)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            device.Name, device.ConfigurationJson, device.SavedConfigurationJson
        });
        return Convert.ToHexStringLower(SHA256.HashData(content));
    }

    public static bool HasStartupConfiguration(TrainingDevice device) => TryRead(device.SavedConfigurationJson) is not null;

    public static bool IsValidHostname(string? hostname) => hostname is { Length: >= 1 and <= 63 }
        && hostname.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    public static bool IsValidDescription(string? description) => description is { Length: <= 100 }
        && !description.Any(char.IsControl);

    private static SimulatedDeviceState? TryRead(string json)
    {
        try
        {
            var state = JsonSerializer.Deserialize<SimulatedDeviceState>(json, Json);
            if (state is not null && IsValidHostname(state.Hostname) && state.Interfaces is { Count: > 0 }
                && state.Interfaces.Values.All(item => item is not null && IsValidDescription(item.Description))) return state;
        }
        catch (JsonException) { }
        return null;
    }

    private static string DefaultHostname(string deviceName)
    {
        // Match existing CLI names, e.g. "模拟交换机 SW-01" becomes "SW-01".
        var token = deviceName.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(IsValidHostname);
        return token?.ToUpperInvariant() ?? "SW-01";
    }
}

public sealed class SimulatedDeviceState
{
    public string Hostname { get; set; } = "";
    public Dictionary<string, SimulatedInterface> Interfaces { get; set; } = new();
}

public sealed class SimulatedInterface
{
    public bool Enabled { get; set; } = true;
    public string Description { get; set; } = "";
}
