using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaDesktop.Services;

public sealed class CrashRecoveryService
{
    private static readonly Regex OpenAiKeyPattern = new(
        @"\bsk-[A-Za-z0-9_-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerPattern = new(
        @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]{8,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string _dataDirectory;
    private readonly string _crashDirectory;
    private readonly string _markerPath;

    public CrashRecoveryService(string? dataDirectory = null)
    {
        _dataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NOVA");
        _crashDirectory = Path.Combine(_dataDirectory, "crashes");
        _markerPath = Path.Combine(_dataDirectory, "session.lock.json");
        Directory.CreateDirectory(_crashDirectory);
        HadUncleanShutdown = File.Exists(_markerPath);
    }

    public bool HadUncleanShutdown { get; }
    public string CrashDirectory => _crashDirectory;

    public void StartSession()
    {
        Directory.CreateDirectory(_dataDirectory);
        var marker = JsonSerializer.Serialize(new
        {
            process_id = Environment.ProcessId,
            started_at = DateTimeOffset.Now,
            version = GetVersion()
        });
        File.WriteAllText(_markerPath, marker);
    }

    public void MarkCleanExit()
    {
        if (File.Exists(_markerPath))
        {
            File.Delete(_markerPath);
        }
    }

    public string? WriteCrashReport(Exception exception, string origin, bool fatal)
    {
        try
        {
            Directory.CreateDirectory(_crashDirectory);
            var path = Path.Combine(
                _crashDirectory,
                $"crash-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.json");
            var report = new
            {
                timestamp = DateTimeOffset.Now,
                origin,
                fatal,
                version = GetVersion(),
                process_id = Environment.ProcessId,
                process_uptime_seconds = Environment.TickCount64 / 1000,
                os = Environment.OSVersion.VersionString,
                runtime = Environment.Version.ToString(),
                machine = Environment.MachineName,
                exception = SerializeException(exception)
            };
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static object SerializeException(Exception exception)
        => new
        {
            type = exception.GetType().FullName,
            message = Redact(exception.Message),
            stack_trace = Redact(exception.StackTrace ?? string.Empty),
            inner = exception.InnerException is null ? null : SerializeException(exception.InnerException)
        };

    private static string Redact(string value)
        => BearerPattern.Replace(
            OpenAiKeyPattern.Replace(value, "[REDACTED_API_KEY]"),
            "Bearer [REDACTED]");

    private static string GetVersion()
        => Assembly.GetExecutingAssembly()
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
               ?.InformationalVersion
           ?? FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? string.Empty).ProductVersion
           ?? "0.0.0";
}
