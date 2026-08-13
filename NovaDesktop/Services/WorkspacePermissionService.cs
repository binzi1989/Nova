using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NovaDesktop.Services;

public sealed record WorkspacePermissionGrant(
    string Id,
    string WorkspaceRoot,
    string WorkspaceLabel,
    string PermissionKey,
    string ToolName,
    string Description,
    string Platform,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt);

public sealed class WorkspacePermissionService
{
    private readonly string _storePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkspacePermissionService(string? storePath = null)
    {
        _storePath = storePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "permissions",
            "workspace-grants.json");
    }

    public async Task<IReadOnlyList<WorkspacePermissionGrant>> ListAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return await ReadUnsafeAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsGrantedAsync(string workspaceRoot, string permissionKey)
    {
        var workspaceId = WorkspaceId(workspaceRoot);
        await _gate.WaitAsync();
        try
        {
            return (await ReadUnsafeAsync()).Any(grant =>
                grant.Id.Equals(GrantId(workspaceId, permissionKey), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkspacePermissionGrant> GrantAsync(
        string workspaceRoot,
        string permissionKey,
        string toolName,
        string description)
    {
        if (string.IsNullOrWhiteSpace(permissionKey))
        {
            throw new InvalidOperationException("Persistent permission requires a bounded permission key.");
        }

        var normalizedRoot = NormalizeWorkspace(workspaceRoot);
        var workspaceId = WorkspaceId(normalizedRoot);
        var id = GrantId(workspaceId, permissionKey);
        var now = DateTimeOffset.UtcNow;

        await _gate.WaitAsync();
        try
        {
            var grants = (await ReadUnsafeAsync()).ToList();
            var existing = grants.FindIndex(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var grant = new WorkspacePermissionGrant(
                id,
                normalizedRoot,
                Path.GetFileName(normalizedRoot) is { Length: > 0 } label ? label : normalizedRoot,
                permissionKey,
                toolName,
                description,
                OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
                existing >= 0 ? grants[existing].CreatedAt : now,
                now);
            if (existing >= 0) grants[existing] = grant;
            else grants.Add(grant);
            await WriteUnsafeAsync(grants);
            return grant;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RevokeAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            var grants = (await ReadUnsafeAsync()).ToList();
            var removed = grants.RemoveAll(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed) await WriteUnsafeAsync(grants);
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ClearAsync(string? workspaceRoot = null)
    {
        await _gate.WaitAsync();
        try
        {
            var grants = (await ReadUnsafeAsync()).ToList();
            var before = grants.Count;
            if (string.IsNullOrWhiteSpace(workspaceRoot)) grants.Clear();
            else
            {
                var normalized = NormalizeWorkspace(workspaceRoot);
                grants.RemoveAll(item => PathsEqual(item.WorkspaceRoot, normalized));
            }
            if (before != grants.Count) await WriteUnsafeAsync(grants);
            return before - grants.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<WorkspacePermissionGrant>> ReadUnsafeAsync()
    {
        if (!File.Exists(_storePath)) return [];
        try
        {
            await using var stream = File.OpenRead(_storePath);
            return await JsonSerializer.DeserializeAsync<List<WorkspacePermissionGrant>>(stream) ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task WriteUnsafeAsync(IReadOnlyList<WorkspacePermissionGrant> grants)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        var temporaryPath = _storePath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryPath,
            JsonSerializer.Serialize(grants.OrderByDescending(item => item.LastUsedAt), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, _storePath, true);
    }

    private static string NormalizeWorkspace(string value)
        => Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string WorkspaceId(string root)
    {
        var normalized = NormalizeWorkspace(root);
        if (OperatingSystem.IsWindows()) normalized = normalized.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..20];
    }

    private static string GrantId(string workspaceId, string permissionKey)
        => $"{workspaceId}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(permissionKey))).ToLowerInvariant()[..16]}";

    private static bool PathsEqual(string left, string right)
        => left.Equals(right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
