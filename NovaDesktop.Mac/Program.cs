using Avalonia;
using System.Reflection;
using NovaDesktop.Models;
using NovaDesktop.Services;

namespace NovaDesktop.Mac;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--agentos-smoke", StringComparer.Ordinal))
        {
            return RunAgentOsSmokeAsync().GetAwaiter().GetResult();
        }

        if (args.Contains("--startup-smoke", StringComparer.Ordinal))
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(version)
                || !File.Exists(Path.Combine(
                    AppContext.BaseDirectory,
                    "NovaDesktop.Mac.dll"))
                || !File.Exists(Path.Combine(
                    AppContext.BaseDirectory,
                    "Nova.AgentOS.dll")))
            {
                Console.Error.WriteLine("NOVA Mac startup smoke failed.");
                return 2;
            }
            Console.WriteLine($"NOVA Mac {version} startup smoke passed.");
            return 0;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static async Task<int> RunAgentOsSmokeAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "nova-mac-agentos-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var host = new MacAgentOsHost(root);
            await host.EnsureBootedAsync();
            var task = new TaskItem
            {
                Id = "mac-agentos-smoke",
                Title = "Shared AgentOS smoke",
                Description = "Verify persistent task truth on the Mac host.",
                WorkspaceRoot = root,
                Provider = "openai",
                Model = "smoke",
                ExecutionMode = AgentExecutionMode.Ask,
                State = TaskState.Queued
            };
            await host.BeginTaskAsync(task);
            await host.ObserveAsync(
                task,
                new AgentRuntimeEvent(
                    AgentRuntimeEventKind.Thinking,
                    "NOVA Mac",
                    "Smoke",
                    "Shared runtime event",
                    50)
                {
                    ModelRoundCost = 1
                });
            await host.CompleteTaskAsync(task, true, "Smoke completed", 24);
            var restored = host.LoadTasks().SingleOrDefault(item => item.Id == task.Id);
            if (restored?.State != TaskState.Completed
                || restored.Progress != 100
                || restored.ExecutionSequence <= 0)
            {
                Console.Error.WriteLine("NOVA Mac AgentOS persistence smoke failed.");
                return 3;
            }
            Console.WriteLine($"NOVA Mac shared AgentOS {host.Status} smoke passed.");
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Isolated smoke cleanup is best-effort only.
            }
        }
    }
}
