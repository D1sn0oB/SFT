using System.Text.Json.Serialization;

namespace SFTLauncher.Models;

public class VersionManifest
{
    [JsonPropertyName("latest")]
    public LatestVersions Latest { get; set; } = new();

    [JsonPropertyName("versions")]
    public List<MinecraftVersion> Versions { get; set; } = [];
}

public class LatestVersions
{
    [JsonPropertyName("release")]
    public string Release { get; set; } = string.Empty;

    [JsonPropertyName("snapshot")]
    public string Snapshot { get; set; } = string.Empty;
}

public class MinecraftVersion
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("time")]
    public DateTime Time { get; set; }

    [JsonPropertyName("releaseTime")]
    public DateTime ReleaseTime { get; set; }

    [JsonIgnore]
    public string TypeDisplay => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照版",
        _ => Type
    };

    [JsonIgnore]
    public string DisplayName => $"Minecraft {Id}";

    [JsonIgnore]
    public bool IsLatestRelease { get; set; }

    [JsonIgnore]
    public bool IsLatestSnapshot { get; set; }
}
