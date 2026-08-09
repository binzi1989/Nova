using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Nova.Maimai.Connector;

internal sealed class ProfileRecord
{
    public string Id { get; set; } = string.Empty;
    public string SourceType { get; set; } = "person-profile";
    public string SourceUrl { get; set; } = string.Empty;
    public string SourceUrlHash { get; set; } = string.Empty;
    public string PageTitle { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string CurrentTitle { get; set; } = string.Empty;
    public string CurrentCompany { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string PublicSummary { get; set; } = string.Empty;
    public List<string> VisibleSkills { get; set; } = [];
    public string EvidenceText { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string ContactStatus { get; set; } = "new";
    public DateTimeOffset CapturedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset RetentionUntil { get; set; }
}

internal sealed class CaptureInput
{
    public string SourceType { get; set; } = "person-profile";
    public string SourceUrl { get; set; } = string.Empty;
    public string PageTitle { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string CurrentTitle { get; set; } = string.Empty;
    public string CurrentCompany { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string PublicSummary { get; set; } = string.Empty;
    public List<string> VisibleSkills { get; set; } = [];
    public string EvidenceText { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public int RetentionDays { get; set; } = 90;
}

internal static partial class ProfilePolicy
{
    private static readonly HashSet<string> ContactStatuses =
        ["new", "shortlisted", "contacted_manual", "replied", "archived"];

    [GeneratedRegex(@"(?<!\d)(?:\+?86[- ]?)?1[3-9]\d{9}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    public static ProfileRecord Normalize(CaptureInput input, DateTimeOffset now)
    {
        var canonicalUrl = CanonicalizeMaimaiUrl(input.SourceUrl);
        var urlHash = Sha256(canonicalUrl);
        var sourceType = Clean(input.SourceType, 40);
        if (sourceType.Length == 0)
        {
            sourceType = "person-profile";
        }
        if (sourceType is not ("person-profile" or "community-post-author"))
        {
            throw new InvalidOperationException("不支持的页面对象类型。");
        }
        var name = Clean(input.DisplayName, 100);
        if (name.Length == 0)
        {
            throw new InvalidOperationException("姓名/展示名不能为空。请先在扩展预览中确认当前页面对象。");
        }

        if (input.RetentionDays is < 1 or > 365)
        {
            throw new InvalidOperationException("保留期限必须在 1 到 365 天之间。");
        }

        var purpose = Clean(input.Purpose, 120);
        if (purpose.Length < 2)
        {
            throw new InvalidOperationException("必须填写明确的数据使用目的。");
        }

        return new ProfileRecord
        {
            Id = urlHash[..24],
            SourceType = sourceType,
            SourceUrl = canonicalUrl,
            SourceUrlHash = urlHash,
            PageTitle = Clean(input.PageTitle, 200),
            DisplayName = name,
            CurrentTitle = Clean(input.CurrentTitle, 160),
            CurrentCompany = Clean(input.CurrentCompany, 160),
            Location = Clean(input.Location, 100),
            PublicSummary = Redact(Clean(input.PublicSummary, 2_000)),
            VisibleSkills = input.VisibleSkills
                .Select(value => Redact(Clean(value, 80)))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(30)
                .ToList(),
            EvidenceText = Redact(Clean(input.EvidenceText, 8_000)),
            Purpose = purpose,
            Notes = Redact(Clean(input.Notes, 1_000)),
            CapturedAt = now,
            UpdatedAt = now,
            RetentionUntil = now.AddDays(input.RetentionDays)
        };
    }

    public static string ValidateStatus(string value)
    {
        value = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (!ContactStatuses.Contains(value))
        {
            throw new InvalidOperationException(
                "contact_status 仅支持 new、shortlisted、contacted_manual、replied、archived。");
        }
        return value;
    }

    public static string CanonicalizeMaimaiUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !(uri.Host.Equals("maimai.cn", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.EndsWith(".maimai.cn", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("只接受 https://maimai.cn 或其子域名的页面。");
        }

        var builder = new UriBuilder(uri)
        {
            Host = uri.Host.ToLowerInvariant(),
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    public static string Clean(string? value, int maxLength)
    {
        var cleaned = WhitespaceRegex().Replace(value?.Trim() ?? string.Empty, " ");
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    public static string Redact(string value)
    {
        value = PhoneRegex().Replace(value, "[手机号已剔除]");
        return EmailRegex().Replace(value, "[邮箱已剔除]");
    }

    public static string Sha256(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

internal sealed class VaultEnvelope
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("profiles")]
    public List<ProfileRecord> Profiles { get; set; } = [];
}
