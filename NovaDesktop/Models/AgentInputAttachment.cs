namespace NovaDesktop.Models;

public enum AgentAttachmentKind
{
    Image,
    Text,
    Document
}

public sealed record AgentInputAttachment(
    string Id,
    string FileName,
    string LocalPath,
    string MediaType,
    AgentAttachmentKind Kind,
    long SizeBytes)
{
    public bool IsImage => Kind == AgentAttachmentKind.Image;

    public bool IsDocument => Kind == AgentAttachmentKind.Document;

    public string KindLabel => IsImage ? "图片" : "文件";

    public string SizeLabel
        => SizeBytes >= 1024 * 1024
            ? $"{SizeBytes / 1024d / 1024d:0.0} MB"
            : $"{Math.Max(1, SizeBytes / 1024d):0} KB";
}
