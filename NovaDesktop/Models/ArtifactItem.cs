using System.Text.Json.Serialization;

namespace NovaDesktop.Models;

public sealed record ArtifactItem(
    string Type,
    string Title,
    string Subtitle,
    string Icon,
    string Accent,
    string Preview = "",
    string Location = "",
    string Id = "",
    string TaskId = "",
    string WorkspaceRoot = "",
    int Version = 1,
    DateTimeOffset? CreatedAt = null)
{
    [JsonIgnore]
    public string VersionLabel => $"v{Math.Max(Version, 1)}";

    [JsonIgnore]
    public string CreatedLabel => (CreatedAt ?? DateTimeOffset.Now)
        .ToLocalTime()
        .ToString("yyyy-MM-dd HH:mm");
}
