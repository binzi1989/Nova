namespace Nova.Maimai.Connector;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            // Chrome adds the extension origin (and sometimes --parent-window) when it
            // launches a Native Messaging Host. Only our own explicit mode tokens may
            // switch the process away from the default native-host mode.
            var mode = args
                .Select(value => value.Trim().ToLowerInvariant())
                .FirstOrDefault(value => value is "native-host" or "mcp" or "smoke")
                ?? "native-host";
            var dataRoot = ReadOption(args, "--data-dir");
            if (mode == "smoke")
            {
                return await SmokeTests.RunAsync(dataRoot);
            }

            var vault = new EncryptedVault(dataRoot);
            var profiles = new ProfileService(vault);
            if (mode == "mcp")
            {
                await new McpServer(profiles).RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
                return 0;
            }
            if (mode == "native-host")
            {
                await new NativeHost(profiles).RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput());
                return 0;
            }

            Console.Error.WriteLine("用法：Nova.Maimai.Connector.exe [native-host|mcp|smoke] [--data-dir PATH]");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.ToString());
            return 1;
        }
    }

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }
        return null;
    }
}
