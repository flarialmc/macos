using System.Reflection;
using System.Threading.Tasks;
using Flarial.Runtime.Services;

namespace Flarial.Runtime.Core;

// Self-update/migration removed on Linux (original downloaded a replacement package and restarted); see Flarial.Runtime.Windows.
public static class FlarialLauncher
{
    public static string Version { get; } = ReadVersion();

    // version.txt next to the executable (written by release CI), assembly version as fallback.
    static string ReadVersion()
    {
        // macOS bundle: Contents/MacOS holds only code, so version.txt sits in Contents/Resources
        foreach (var f in new[] { "version.txt", System.IO.Path.Combine("..", "Resources", "version.txt") })
            try { if (System.IO.File.ReadAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, f)).Trim() is { Length: > 0 } v) return v; } catch { }
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
    }

    const string AcceptedUri = "https://cdn.flarial.xyz/202.txt";

    public static async Task<bool> CanConnectAsync()
    {
        return await HttpService.ProbeAsync(AcceptedUri, default) is { };
    }
}
