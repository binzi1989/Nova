using System.Reflection;

namespace NovaDesktop.Services;

public static class NovaProductVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var informational = typeof(NovaProductVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Split('+', 2)[0];
        }

        return typeof(NovaProductVersion).Assembly.GetName().Version?.ToString(3)
               ?? "0.0.0";
    }
}
