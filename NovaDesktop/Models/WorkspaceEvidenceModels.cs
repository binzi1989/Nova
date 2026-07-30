namespace NovaDesktop.Models;

public sealed record WorkspaceEvidenceFingerprint(
    string WorkspaceRoot,
    string Sha256,
    int FileCount,
    long HashedBytes,
    bool IsComplete,
    DateTimeOffset CapturedAt,
    string Detail);
