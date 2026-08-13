using System.Text.Json.Nodes;

namespace NovaDesktop.Services;

public interface IMcpClientSession : IAsyncDisposable
{
    bool IsAlive { get; }

    Task<JsonObject> ListToolsAsync(CancellationToken cancellationToken);

    Task<JsonObject> CallToolAsync(
        string toolName,
        JsonObject arguments,
        CancellationToken cancellationToken);
}
