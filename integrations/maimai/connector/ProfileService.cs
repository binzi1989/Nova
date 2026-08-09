namespace Nova.Maimai.Connector;

internal sealed class ProfileService
{
    private readonly EncryptedVault _vault;

    public ProfileService(EncryptedVault vault)
    {
        _vault = vault;
    }

    public ProfileRecord Capture(CaptureInput input, DateTimeOffset? capturedAt = null)
    {
        var candidate = ProfilePolicy.Normalize(input, capturedAt ?? DateTimeOffset.UtcNow);
        return _vault.Update(profiles =>
        {
            var existing = profiles.FirstOrDefault(profile => profile.SourceUrlHash == candidate.SourceUrlHash);
            if (existing is null)
            {
                profiles.Add(candidate);
                return candidate;
            }

            candidate.Id = existing.Id;
            candidate.CapturedAt = existing.CapturedAt;
            candidate.ContactStatus = existing.ContactStatus;
            if (candidate.Notes.Length == 0)
            {
                candidate.Notes = existing.Notes;
            }
            profiles[profiles.IndexOf(existing)] = candidate;
            return candidate;
        });
    }

    public IReadOnlyList<ProfileRecord> List(int limit = 50, string? status = null)
    {
        limit = Math.Clamp(limit, 1, 200);
        status = string.IsNullOrWhiteSpace(status) ? null : ProfilePolicy.ValidateStatus(status);
        return _vault.Read(profiles => profiles
            .Where(profile => status is null || profile.ContactStatus == status)
            .OrderByDescending(profile => profile.UpdatedAt)
            .Take(limit)
            .ToArray());
    }

    public IReadOnlyList<ProfileRecord> Search(string query, int limit = 30)
    {
        query = ProfilePolicy.Clean(query, 200);
        if (query.Length == 0)
        {
            throw new InvalidOperationException("query 不能为空。");
        }
        limit = Math.Clamp(limit, 1, 100);
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return _vault.Read(profiles => profiles
            .Select(profile => new
            {
                Profile = profile,
                Haystack = string.Join(' ', profile.DisplayName, profile.CurrentTitle,
                    profile.CurrentCompany, profile.Location, profile.PublicSummary,
                    string.Join(' ', profile.VisibleSkills), profile.Notes)
            })
            .Where(item => tokens.All(token => item.Haystack.Contains(token, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => item.Profile.UpdatedAt)
            .Take(limit)
            .Select(item => item.Profile)
            .ToArray());
    }

    public ProfileRecord Get(string id)
        => _vault.Read(profiles => profiles.FirstOrDefault(profile => profile.Id == id)
            ?? throw new KeyNotFoundException($"未找到档案 {id}。"));

    public ProfileRecord RecordContactStatus(string id, string status, string? note)
    {
        status = ProfilePolicy.ValidateStatus(status);
        note = ProfilePolicy.Redact(ProfilePolicy.Clean(note, 1_000));
        return _vault.Update(profiles =>
        {
            var profile = profiles.FirstOrDefault(item => item.Id == id)
                          ?? throw new KeyNotFoundException($"未找到档案 {id}。");
            profile.ContactStatus = status;
            if (note.Length > 0)
            {
                profile.Notes = note;
            }
            profile.UpdatedAt = DateTimeOffset.UtcNow;
            return profile;
        });
    }

    public bool Delete(string id)
        => _vault.Update(profiles => profiles.RemoveAll(profile => profile.Id == id) > 0);

    public int PurgeExpired(DateTimeOffset? now = null)
    {
        var cutoff = now ?? DateTimeOffset.UtcNow;
        return _vault.Update(profiles => profiles.RemoveAll(profile => profile.RetentionUntil <= cutoff));
    }

    public object Stats()
        => _vault.Read(profiles => new
        {
            total = profiles.Count,
            by_status = profiles
                .GroupBy(profile => profile.ContactStatus)
                .ToDictionary(group => group.Key, group => group.Count()),
            earliest_expiry = profiles.Count == 0
                ? (DateTimeOffset?)null
                : profiles.Min(profile => profile.RetentionUntil),
            data_directory = _vault.Root,
            encrypted_at_rest = true,
            collection_mode = "user-click-visible-page-only"
        });
}
