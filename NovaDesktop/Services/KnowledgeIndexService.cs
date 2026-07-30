using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NovaDesktop.Models;

namespace NovaDesktop.Services;

public sealed record IndexedKnowledgeDocument(
    string Id,
    string WorkspaceRoot,
    string RelativePath,
    string Title,
    string Extension,
    string Sha256,
    long SizeBytes,
    int ChunkCount,
    DateTimeOffset IndexedAt);

public sealed record IndexedKnowledgeChunk(
    string DocumentId,
    int Index,
    int StartLine,
    string Content);

public sealed record KnowledgeIndexSnapshot(
    DateTimeOffset UpdatedAt,
    IReadOnlyList<IndexedKnowledgeDocument> Documents,
    IReadOnlyList<IndexedKnowledgeChunk> Chunks);

public sealed record KnowledgeIndexSummary(
    string WorkspaceRoot,
    int ScannedFiles,
    int IndexedFiles,
    int ReusedFiles,
    int RemovedFiles,
    int SkippedFiles,
    int ChunkCount,
    long IndexedBytes,
    DateTimeOffset CompletedAt);

public sealed record KnowledgeSearchResult(
    string DocumentId,
    string RelativePath,
    string Title,
    int StartLine,
    double Score,
    string Snippet);

public sealed class KnowledgeIndexService
{
    private const int MaximumFiles = 1000;
    private const long MaximumFileBytes = 1024L * 1024L;
    private const long MaximumWorkspaceBytes = 50L * 1024L * 1024L;
    private const int ChunkCharacters = 1600;
    private const int ChunkOverlap = 180;
    private const string ArtifactPathPrefix = ".nova-artifacts";

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", ".nova", "bin", "obj", "node_modules", "packages", ".dotnet-home"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".txt", ".rst", ".adoc", ".json", ".jsonl", ".yml", ".yaml", ".toml",
        ".xml", ".csv", ".tsv", ".cs", ".xaml", ".props", ".targets", ".sln", ".csproj",
        ".ts", ".tsx", ".js", ".jsx", ".css", ".scss", ".html", ".py", ".rs", ".go",
        ".java", ".kt", ".sql", ".ps1", ".sh", ".wxml", ".wxss", ".wxs", ".axml",
        ".acss", ".swan", ".ttml", ".ttss"
    };

    private readonly string _indexPath;
    private readonly string _artifactOutputRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public KnowledgeIndexService(
        string? indexPath = null,
        string? artifactOutputRoot = null)
    {
        _indexPath = indexPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "knowledge-index.json");
        _artifactOutputRoot = artifactOutputRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA",
            "outputs");
    }

    public string IndexPath => _indexPath;

    public KnowledgeIndexSnapshot GetSnapshot()
    {
        if (!File.Exists(_indexPath))
        {
            return new KnowledgeIndexSnapshot(DateTimeOffset.MinValue, [], []);
        }
        try
        {
            return JsonSerializer.Deserialize<KnowledgeIndexSnapshot>(
                       File.ReadAllText(_indexPath),
                       _options)
                   ?? new KnowledgeIndexSnapshot(DateTimeOffset.MinValue, [], []);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new InvalidOperationException($"Unable to read knowledge index '{_indexPath}'.", exception);
        }
    }

    public async Task<KnowledgeIndexSummary> IndexWorkspaceAsync(
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        workspaceRoot = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(workspaceRoot))
        {
            throw new InvalidOperationException("Workspace does not exist.");
        }
        var workspacePrefix = workspaceRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var existing = GetSnapshot();
            var existingDocuments = existing.Documents
                .Where(document => document.WorkspaceRoot.Equals(workspaceRoot, StringComparison.OrdinalIgnoreCase))
                .Where(document => !IsArtifactDocument(document))
                .ToDictionary(document => document.RelativePath, StringComparer.OrdinalIgnoreCase);
            var existingChunks = existing.Chunks
                .GroupBy(chunk => chunk.DocumentId)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

            var candidates = EnumerateCandidateFiles(workspaceRoot)
                .Take(MaximumFiles + 1)
                .ToArray();
            if (candidates.Length > MaximumFiles)
            {
                throw new InvalidOperationException($"Workspace contains more than the {MaximumFiles} file indexing limit.");
            }

            var documents = existing.Documents
                .Where(document => !document.WorkspaceRoot.Equals(workspaceRoot, StringComparison.OrdinalIgnoreCase)
                                   || IsArtifactDocument(document))
                .ToList();
            var chunks = existing.Chunks
                .Where(chunk => documents.Any(document => document.Id == chunk.DocumentId))
                .ToList();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var indexedFiles = 0;
            var reusedFiles = 0;
            var skippedFiles = 0;
            var indexedBytes = 0L;

            foreach (var path in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.GetFullPath(path);
                if (!fullPath.StartsWith(workspacePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    skippedFiles++;
                    continue;
                }
                var file = new FileInfo(fullPath);
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || file.Attributes.HasFlag(FileAttributes.Hidden)
                    || file.Length == 0
                    || file.Length > MaximumFileBytes
                    || indexedBytes + file.Length > MaximumWorkspaceBytes)
                {
                    skippedFiles++;
                    continue;
                }

                var relativePath = Path.GetRelativePath(workspaceRoot, fullPath);
                seen.Add(relativePath);
                var hash = await ComputeHashAsync(fullPath, cancellationToken);
                if (existingDocuments.TryGetValue(relativePath, out var previous)
                    && previous.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)
                    && existingChunks.TryGetValue(previous.Id, out var previousChunks))
                {
                    documents.Add(previous);
                    chunks.AddRange(previousChunks);
                    indexedBytes += previous.SizeBytes;
                    reusedFiles++;
                    continue;
                }

                string content;
                try
                {
                    content = await File.ReadAllTextAsync(fullPath, cancellationToken);
                }
                catch (Exception exception) when (
                    exception is IOException
                    or UnauthorizedAccessException
                    or DecoderFallbackException)
                {
                    skippedFiles++;
                    continue;
                }
                if (content.IndexOf('\0') >= 0)
                {
                    skippedFiles++;
                    continue;
                }

                var documentId = CreateDocumentId(workspaceRoot, relativePath);
                var documentChunks = CreateChunks(documentId, content).ToArray();
                documents.Add(new IndexedKnowledgeDocument(
                    documentId,
                    workspaceRoot,
                    relativePath,
                    CreateTitle(relativePath, content),
                    file.Extension,
                    hash,
                    file.Length,
                    documentChunks.Length,
                    DateTimeOffset.Now));
                chunks.AddRange(documentChunks);
                indexedBytes += file.Length;
                indexedFiles++;
            }

            var removedFiles = existingDocuments.Keys.Count(path => !seen.Contains(path));
            var snapshot = new KnowledgeIndexSnapshot(
                DateTimeOffset.Now,
                documents.OrderBy(document => document.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
                chunks.ToArray());
            await SaveAsync(snapshot, cancellationToken);
            return new KnowledgeIndexSummary(
                workspaceRoot,
                candidates.Length,
                indexedFiles,
                reusedFiles,
                removedFiles,
                skippedFiles,
                snapshot.Documents.Count(document =>
                    document.WorkspaceRoot.Equals(workspaceRoot, StringComparison.OrdinalIgnoreCase)),
                indexedBytes,
                DateTimeOffset.Now) with
            {
                ChunkCount = snapshot.Documents
                    .Where(document => document.WorkspaceRoot.Equals(workspaceRoot, StringComparison.OrdinalIgnoreCase))
                    .Sum(document => document.ChunkCount)
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> UpsertArtifactsAsync(
        string workspaceRoot,
        IReadOnlyList<ArtifactItem> artifacts,
        CancellationToken cancellationToken)
    {
        workspaceRoot = Path.GetFullPath(workspaceRoot);
        if (!Directory.Exists(workspaceRoot))
        {
            throw new InvalidOperationException("Workspace does not exist.");
        }

        var outputRoot = Path.GetFullPath(_artifactOutputRoot)
                             .TrimEnd(Path.DirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = GetSnapshot();
            var documents = snapshot.Documents.ToList();
            var chunks = snapshot.Chunks.ToList();
            var indexed = 0;
            foreach (var artifact in artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(artifact.Id)
                    || string.IsNullOrWhiteSpace(artifact.Location)
                    || string.IsNullOrWhiteSpace(artifact.Preview))
                {
                    continue;
                }

                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(artifact.Location);
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
                {
                    continue;
                }
                if (!fullPath.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(fullPath))
                {
                    continue;
                }
                var file = new FileInfo(fullPath);
                if (file.Length is <= 0 or > MaximumFileBytes)
                {
                    continue;
                }

                var relativePath = Path.Combine(
                    ArtifactPathPrefix,
                    artifact.TaskId,
                    $"{artifact.Id}-v{artifact.Version}{file.Extension}");
                var documentId = CreateDocumentId(workspaceRoot, relativePath);
                var existingDocument = documents.FirstOrDefault(document =>
                    document.Id.Equals(documentId, StringComparison.OrdinalIgnoreCase));
                var hash = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(artifact.Preview))).ToLowerInvariant();
                if (existingDocument is not null
                    && existingDocument.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                documents.RemoveAll(document =>
                    document.Id.Equals(documentId, StringComparison.OrdinalIgnoreCase));
                chunks.RemoveAll(chunk =>
                    chunk.DocumentId.Equals(documentId, StringComparison.OrdinalIgnoreCase));
                var artifactChunks = CreateChunks(documentId, artifact.Preview).ToArray();
                documents.Add(new IndexedKnowledgeDocument(
                    documentId,
                    workspaceRoot,
                    relativePath,
                    artifact.Title,
                    file.Extension,
                    hash,
                    Encoding.UTF8.GetByteCount(artifact.Preview),
                    artifactChunks.Length,
                    artifact.CreatedAt ?? DateTimeOffset.Now));
                chunks.AddRange(artifactChunks);
                indexed++;
            }

            if (indexed > 0)
            {
                await SaveAsync(
                    new KnowledgeIndexSnapshot(
                        DateTimeOffset.Now,
                        documents
                            .OrderBy(document => document.RelativePath, StringComparer.OrdinalIgnoreCase)
                            .ToArray(),
                        chunks.ToArray()),
                    cancellationToken);
            }
            return indexed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<KnowledgeSearchResult> Search(
        string query,
        string? workspaceRoot = null,
        int maximumResults = 12)
    {
        query = query.Trim();
        if (query.Length is < 2 or > 500)
        {
            throw new InvalidOperationException("Knowledge query must contain 2-500 characters.");
        }
        maximumResults = Math.Clamp(maximumResults, 1, 50);
        var snapshot = GetSnapshot();
        var documents = snapshot.Documents
            .Where(document => string.IsNullOrWhiteSpace(workspaceRoot)
                               || document.WorkspaceRoot.Equals(
                                   Path.GetFullPath(workspaceRoot),
                                   StringComparison.OrdinalIgnoreCase))
            .ToDictionary(document => document.Id, StringComparer.OrdinalIgnoreCase);
        var terms = Tokenize(query).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (terms.Length == 0)
        {
            return [];
        }

        return snapshot.Chunks
            .Where(chunk => documents.ContainsKey(chunk.DocumentId))
            .Select(chunk =>
            {
                var document = documents[chunk.DocumentId];
                var score = Score(query, terms, document, chunk);
                return new KnowledgeSearchResult(
                    document.Id,
                    document.RelativePath,
                    document.Title,
                    chunk.StartLine,
                    Math.Round(score, 2),
                    CreateSnippet(chunk.Content, query, terms));
            })
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(maximumResults)
            .ToArray();
    }

    public string SearchJson(string query, string? workspaceRoot, int maximumResults = 12)
        => JsonSerializer.Serialize(new
        {
            query,
            workspace = workspaceRoot,
            results = Search(query, workspaceRoot, maximumResults)
        });

    public IReadOnlyList<IndexedKnowledgeDocument> GetDocuments(string? workspaceRoot = null)
        => GetSnapshot().Documents
            .Where(document => string.IsNullOrWhiteSpace(workspaceRoot)
                               || document.WorkspaceRoot.Equals(
                                   Path.GetFullPath(workspaceRoot),
                                   StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(document => document.IndexedAt)
            .ToArray();

    public string ListDocumentsJson(string? workspaceRoot = null)
    {
        var documents = GetDocuments(workspaceRoot);
        return JsonSerializer.Serialize(new
        {
            index_path = _indexPath,
            workspace = workspaceRoot,
            count = documents.Count,
            chunks = documents.Sum(document => document.ChunkCount),
            bytes = documents.Sum(document => document.SizeBytes),
            documents
        });
    }

    private static bool IsArtifactDocument(IndexedKnowledgeDocument document)
        => document.RelativePath.StartsWith(
            ArtifactPathPrefix + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)
           || document.RelativePath.StartsWith(
               ArtifactPathPrefix + Path.AltDirectorySeparatorChar,
               StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateCandidateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> childDirectories;
            IEnumerable<string> files;
            try
            {
                childDirectories = Directory.EnumerateDirectories(directory).ToArray();
                files = Directory.EnumerateFiles(directory).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in childDirectories)
            {
                var info = new DirectoryInfo(child);
                if (!IgnoredDirectories.Contains(info.Name)
                    && !info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    && !info.Attributes.HasFlag(FileAttributes.Hidden))
                {
                    pending.Push(child);
                }
            }
            foreach (var file in files)
            {
                if (TextExtensions.Contains(Path.GetExtension(file)))
                {
                    yield return file;
                }
            }
        }
    }

    private static IEnumerable<IndexedKnowledgeChunk> CreateChunks(string documentId, string content)
    {
        if (content.Length == 0)
        {
            yield break;
        }
        var start = 0;
        var index = 0;
        while (start < content.Length && index < 100)
        {
            var length = Math.Min(ChunkCharacters, content.Length - start);
            if (start + length < content.Length)
            {
                var boundary = content.LastIndexOf('\n', start + length - 1, length);
                if (boundary > start + ChunkCharacters / 2)
                {
                    length = boundary - start + 1;
                }
            }
            var chunk = content.Substring(start, length).Trim();
            if (chunk.Length > 0)
            {
                var startLine = 1 + content.AsSpan(0, start).Count('\n');
                yield return new IndexedKnowledgeChunk(documentId, index++, startLine, chunk);
            }
            if (start + length >= content.Length)
            {
                break;
            }
            start += Math.Max(1, length - ChunkOverlap);
        }
    }

    private static double Score(
        string query,
        IReadOnlyList<string> terms,
        IndexedKnowledgeDocument document,
        IndexedKnowledgeChunk chunk)
    {
        var score = 0d;
        if (chunk.Content.Contains(query, StringComparison.CurrentCultureIgnoreCase))
        {
            score += 12;
        }
        if (document.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || document.RelativePath.Contains(query, StringComparison.CurrentCultureIgnoreCase))
        {
            score += 9;
        }
        foreach (var term in terms)
        {
            score += CountOccurrences(chunk.Content, term) * (term.Length >= 4 ? 1.8 : 1.1);
            if (document.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            {
                score += 3;
            }
            if (document.RelativePath.Contains(term, StringComparison.CurrentCultureIgnoreCase))
            {
                score += 2;
            }
        }
        return score;
    }

    private static IEnumerable<string> Tokenize(string value)
    {
        foreach (Match match in Regex.Matches(value.ToLowerInvariant(), @"[a-z0-9_]{2,}|[\p{IsCJKUnifiedIdeographs}]{2,}"))
        {
            var token = match.Value;
            if (Regex.IsMatch(token, @"^[\p{IsCJKUnifiedIdeographs}]+$") && token.Length > 2)
            {
                for (var index = 0; index < token.Length - 1; index++)
                {
                    yield return token.Substring(index, 2);
                }
            }
            else
            {
                yield return token;
            }
        }
    }

    private static int CountOccurrences(string content, string term)
    {
        var count = 0;
        var offset = 0;
        while ((offset = content.IndexOf(term, offset, StringComparison.CurrentCultureIgnoreCase)) >= 0)
        {
            count++;
            offset += Math.Max(1, term.Length);
            if (count >= 20)
            {
                break;
            }
        }
        return count;
    }

    private static string CreateSnippet(
        string content,
        string query,
        IReadOnlyList<string> terms)
    {
        var index = content.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0)
        {
            index = terms
                .Select(term => content.IndexOf(term, StringComparison.CurrentCultureIgnoreCase))
                .Where(value => value >= 0)
                .DefaultIfEmpty(0)
                .Min();
        }
        var start = Math.Max(0, index - 140);
        var length = Math.Min(420, content.Length - start);
        return (start > 0 ? "…" : string.Empty)
               + Regex.Replace(content.Substring(start, length), @"\s+", " ").Trim()
               + (start + length < content.Length ? "…" : string.Empty);
    }

    private static string CreateTitle(string relativePath, string content)
    {
        var firstHeading = content
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith('#'));
        if (!string.IsNullOrWhiteSpace(firstHeading))
        {
            return firstHeading.TrimStart('#', ' ')[..Math.Min(firstHeading.TrimStart('#', ' ').Length, 120)];
        }
        return Path.GetFileNameWithoutExtension(relativePath);
    }

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string CreateDocumentId(string workspaceRoot, string relativePath)
    {
        var hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(workspaceRoot.ToLowerInvariant() + "|" + relativePath.ToLowerInvariant()));
        return "document-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private async Task SaveAsync(
        KnowledgeIndexSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_indexPath)
                        ?? throw new InvalidOperationException("Knowledge index path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = _indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(snapshot, _options),
                cancellationToken);
            File.Move(temporary, _indexPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
