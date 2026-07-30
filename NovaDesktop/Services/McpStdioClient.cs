using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NovaDesktop.Services;

public sealed class McpStdioClient : IMcpClientSession
{
    private const string ProtocolVersion = "2025-11-25";
    private readonly Process _process;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly StringBuilder _stderr = new();
    private readonly Task _stderrDrain;
    private int _nextRequestId;

    private McpStdioClient(Process process)
    {
        _process = process;
        _writer = process.StandardInput;
        _reader = process.StandardOutput;
        _stderrDrain = DrainStderrAsync(process.StandardError);
    }

    public static async Task<McpStdioClient> ConnectAsync(
        McpServerRegistration server,
        string workspaceRoot,
        CancellationToken cancellationToken)
    {
        var workingDirectory = ResolveWorkingDirectory(server.WorkingDirectory, workspaceRoot);
        var utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveExecutable(server.Command, workspaceRoot),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = utf8WithoutBom,
            StandardOutputEncoding = utf8WithoutBom,
            StandardErrorEncoding = utf8WithoutBom
        };
        foreach (var argument in server.Arguments)
        {
            if (argument.Contains('\0') || argument.Contains('\r') || argument.Contains('\n'))
            {
                throw new InvalidOperationException("MCP server arguments may not contain control characters.");
            }
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, sourceVariable) in server.EnvironmentVariables)
        {
            if (!IsEnvironmentName(name) || !IsEnvironmentName(sourceVariable))
            {
                throw new InvalidOperationException("MCP environment mappings must use valid environment-variable names.");
            }

            var value = Environment.GetEnvironmentVariable(sourceVariable);
            if (value is not null)
            {
                startInfo.Environment[name] = value;
            }
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start MCP server '{server.Name}'.");
        }

        var client = new McpStdioClient(process);
        try
        {
            await client.InitializeAsync(cancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<JsonObject> ListToolsAsync(CancellationToken cancellationToken)
        => await RequestAsync("tools/list", new JsonObject(), cancellationToken);

    public async Task<JsonObject> CallToolAsync(
        string toolName,
        JsonObject arguments,
        CancellationToken cancellationToken)
        => await RequestAsync(
            "tools/call",
            new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = arguments.DeepClone()
            },
            cancellationToken);

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RequestAsync(
            "initialize",
            new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "nova-desktop",
                    ["title"] = "NOVA Desktop",
                    ["version"] = "0.3.0",
                    ["description"] = "Native Windows agent host"
                }
            },
            cancellationToken);
        await SendAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/initialized"
            },
            cancellationToken);
    }

    private async Task<JsonObject> RequestAsync(
        string method,
        JsonObject parameters,
        CancellationToken cancellationToken)
    {
        var requestId = Interlocked.Increment(ref _nextRequestId);
        await SendAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = requestId,
                ["method"] = method,
                ["params"] = parameters
            },
            cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        while (await _reader.ReadLineAsync(timeout.Token) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject message;
            try
            {
                message = JsonNode.Parse(line)?.AsObject()
                          ?? throw new JsonException("Empty JSON-RPC message.");
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"MCP server wrote invalid stdout data. stderr: {GetStderrTail()}",
                    exception);
            }

            if (message["id"]?.GetValue<int>() == requestId)
            {
                if (message["error"] is JsonObject error)
                {
                    var errorMessage = error["message"]?.GetValue<string>() ?? "Unknown MCP error.";
                    throw new InvalidOperationException($"MCP {method} failed: {errorMessage}");
                }

                return message["result"]?.AsObject()
                       ?? throw new InvalidOperationException($"MCP {method} returned no result.");
            }

            if (message["method"] is not null && message["id"] is not null)
            {
                await SendAsync(
                    new JsonObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["id"] = message["id"]!.DeepClone(),
                        ["error"] = new JsonObject
                        {
                            ["code"] = -32601,
                            ["message"] = "NOVA does not enable server-initiated MCP requests."
                        }
                    },
                    cancellationToken);
            }
        }

        var exitDetail = _process.HasExited ? $" Process exited with code {_process.ExitCode}." : string.Empty;
        throw new InvalidOperationException(
            $"MCP server closed before responding to {method}.{exitDetail} stderr: {GetStderrTail()}");
    }

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _writer.WriteLineAsync(message.ToJsonString().AsMemory(), cancellationToken);
        await _writer.FlushAsync(cancellationToken);
    }

    private async Task DrainStderrAsync(StreamReader reader)
    {
        var buffer = new char[512];
        try
        {
            while (await reader.ReadAsync(buffer) is var read && read > 0)
            {
                lock (_stderr)
                {
                    _stderr.Append(buffer, 0, read);
                    if (_stderr.Length > 6000)
                    {
                        _stderr.Remove(0, _stderr.Length - 6000);
                    }
                }
            }
        }
        catch
        {
            // The process may terminate while stderr is being drained.
        }
    }

    private string GetStderrTail()
    {
        lock (_stderr)
        {
            return _stderr.Length == 0 ? "(none)" : _stderr.ToString();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _writer.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
            try
            {
                await _stderrDrain;
            }
            catch
            {
                // Best-effort cleanup.
            }
            _process.Dispose();
        }
    }

    private static string ResolveWorkingDirectory(string? configured, string workspaceRoot)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var candidate = string.IsNullOrWhiteSpace(configured) ? root : Path.GetFullPath(configured);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("MCP server working directory must stay inside the active workspace.");
        }
        if (!Directory.Exists(candidate))
        {
            throw new DirectoryNotFoundException("The MCP server working directory does not exist.");
        }
        return candidate;
    }

    private static string ResolveExecutable(string command, string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(command)
            || command.Contains(Path.DirectorySeparatorChar)
            || command.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidOperationException("MCP command must be an executable name resolved from PATH.");
        }

        var root = Path.GetFullPath(workspaceRoot);
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var names = Path.HasExtension(command)
            ? new[] { command }
            : OperatingSystem.IsWindows()
                ? new[] { command + ".exe", command + ".cmd", command }
                : new[] { command };
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in names)
            {
                string candidate;
                try
                {
                    candidate = Path.GetFullPath(Path.Combine(entry, name));
                }
                catch
                {
                    continue;
                }
                if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        throw new FileNotFoundException($"MCP executable '{command}' was not found on PATH.");
    }

    private static bool IsEnvironmentName(string value)
        => value.Length is > 0 and <= 128
           && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}
