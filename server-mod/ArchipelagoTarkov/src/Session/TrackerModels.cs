using System.Text.Json.Serialization;

namespace ArchipelagoTarkov.Session;

/// <summary>GET /archipelago/client/tracker. Field names match the client plugin's JsonUtility classes.</summary>
public sealed class TrackerResponse
{
    [JsonPropertyName("linked")] public bool Linked { get; set; }
    /// <summary>Map key of the raid in progress, or null outside a raid.</summary>
    [JsonPropertyName("raidMap")] public string? RaidMap { get; set; }
    [JsonPropertyName("maps")] public List<TrackerMap> Maps { get; set; } = [];
    [JsonPropertyName("anywhere")] public List<TrackerGroup> Anywhere { get; set; } = [];
}

public sealed class TrackerMap
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("current")] public bool Current { get; set; }
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("groups")] public List<TrackerGroup> Groups { get; set; } = [];
}

public sealed class TrackerGroup
{
    public TrackerGroup(string title, List<string> entries, int? count = null)
    {
        Title = title;
        Entries = entries;
        Count = count ?? entries.Count;
    }

    [JsonPropertyName("title")] public string Title { get; set; }
    /// <summary>How many checks (may be more than the entries listed, e.g. loot types).</summary>
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("entries")] public List<string> Entries { get; set; }
}
