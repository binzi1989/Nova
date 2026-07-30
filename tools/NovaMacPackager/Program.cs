using System.Formats.Tar;
using System.IO.Compression;

const UnixFileMode DirectoryMode =
    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
const UnixFileMode ExecutableMode = DirectoryMode;
const UnixFileMode FileMode =
    UnixFileMode.UserRead | UnixFileMode.UserWrite
    | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

if (args.Length != 3)
{
    Console.Error.WriteLine(
        "Usage: NovaMacPackager <source-directory> <output.zip> <output.tar.gz>");
    return 2;
}

var sourceDirectory = Path.GetFullPath(args[0]);
var zipPath = Path.GetFullPath(args[1]);
var tarGzipPath = Path.GetFullPath(args[2]);
if (!Directory.Exists(sourceDirectory))
{
    Console.Error.WriteLine($"Source directory does not exist: {sourceDirectory}");
    return 3;
}

Directory.CreateDirectory(
    Path.GetDirectoryName(zipPath)
    ?? throw new InvalidOperationException("ZIP path has no parent directory."));
CreateZip(sourceDirectory, zipPath);
CreateTarGzip(sourceDirectory, tarGzipPath);
Console.WriteLine($"ZIP:    {zipPath}");
Console.WriteLine($"TAR.GZ: {tarGzipPath}");
return 0;

static void CreateZip(string sourceDirectory, string outputPath)
{
    if (File.Exists(outputPath))
    {
        File.Delete(outputPath);
    }

    using var file = File.Create(outputPath);
    using var archive = new ZipArchive(file, ZipArchiveMode.Create);
    foreach (var directory in Directory.EnumerateDirectories(
                 sourceDirectory,
                 "*",
                 SearchOption.AllDirectories))
    {
        var relative = NormalizeRelativePath(sourceDirectory, directory) + "/";
        var entry = archive.CreateEntry(relative, CompressionLevel.NoCompression);
        entry.ExternalAttributes = UnixExternalAttributes(isDirectory: true, executable: true);
    }
    foreach (var path in Directory.EnumerateFiles(
                 sourceDirectory,
                 "*",
                 SearchOption.AllDirectories))
    {
        var relative = NormalizeRelativePath(sourceDirectory, path);
        var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
        entry.ExternalAttributes = UnixExternalAttributes(
            isDirectory: false,
            executable: IsMacExecutable(relative));
        entry.LastWriteTime = File.GetLastWriteTime(path);
        using var input = File.OpenRead(path);
        using var output = entry.Open();
        input.CopyTo(output);
    }
}

static void CreateTarGzip(string sourceDirectory, string outputPath)
{
    if (File.Exists(outputPath))
    {
        File.Delete(outputPath);
    }

    using var file = File.Create(outputPath);
    using var gzip = new GZipStream(file, CompressionLevel.Optimal);
    using var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);
    foreach (var directory in Directory.EnumerateDirectories(
                 sourceDirectory,
                 "*",
                 SearchOption.AllDirectories))
    {
        var relative = NormalizeRelativePath(sourceDirectory, directory) + "/";
        writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, relative)
        {
            Mode = DirectoryMode
        });
    }
    foreach (var path in Directory.EnumerateFiles(
                 sourceDirectory,
                 "*",
                 SearchOption.AllDirectories))
    {
        var relative = NormalizeRelativePath(sourceDirectory, path);
        using var input = File.OpenRead(path);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, relative)
        {
            DataStream = input,
            Mode = IsMacExecutable(relative) ? ExecutableMode : FileMode
        });
    }
}

static string NormalizeRelativePath(string root, string path)
    => Path.GetRelativePath(root, path).Replace('\\', '/');

static bool IsMacExecutable(string relativePath)
    => relativePath.Equals(
        "NOVA.app/Contents/MacOS/NovaDesktop.Mac",
        StringComparison.Ordinal)
       || relativePath.EndsWith(".command", StringComparison.OrdinalIgnoreCase)
       || relativePath.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);

static int UnixExternalAttributes(bool isDirectory, bool executable)
{
    const int regularFile = 0x8000;
    const int directory = 0x4000;
    var permission = executable ? 0x1ED : 0x1A4; // 0755 or 0644
    return unchecked(((isDirectory ? directory : regularFile) | permission) << 16);
}
