using System;
using Flarial.Runtime.Platform;

namespace Flarial.Runtime.Linux;

/// <summary>macOS entry point; keeps the Linux class name so the launcher UI is shared unchanged.</summary>
public static class LinuxPlatform
{
    /// <summary>Launcher settings/data directory (~/Library/Application Support/Flarial/Launcher); always absolute.</summary>
    public static string LauncherDataDirectory => System.IO.Path.Combine(Paths.DataHome, "Flarial", "Launcher");

    /// <summary>Flarial client data folder as the game sees it (%LOCALAPPDATA%\\Flarial\\Client inside the Wine prefix); created on demand.</summary>
    public static string ClientDirectory => System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Paths.Prefix, "drive_c", "users", Paths.WineUser, "AppData", "Local", "Flarial", "Client")).FullName;

    /// <summary>UI hook for backend messages (set by the launcher; shown as a notification).</summary>
    public static Action<string>? Notify { get; set; }

    /// <summary>Dev checks (--selftest-&lt;name&gt;); returns the exit code.</summary>
    public static System.Threading.Tasks.Task<int> SelfTestAsync(string name) => name switch
    {
        "dependencies" => DependenciesSelfTest.RunAsync(),
        "payload" => PayloadSelfTest.RunAsync(),
        "updater" => Update.UpdaterSelfTest.RunAsync(),
        "engine" => Engine.EngineSelfTest.RunAsync(),
        "xodus" => Xodus.XodusSelfTest.RunAsync(),
        "hive" => System.Threading.Tasks.Task.FromResult(Prefix.HiveEdit.SetString(Environment.GetEnvironmentVariable("FLARIAL_HIVE")!, @"Software\\Wine\\WineGDK", "RefreshToken", Environment.GetEnvironmentVariable("FLARIAL_HIVE_TOKEN")) ? 0 : 1),
        "import" => GameContent.SelfTest(),
        "procscan" => Launch.ProcScanSelfTest.RunAsync(),
        "prefix" => PrefixSelfTestAsync(),
        _ => System.Threading.Tasks.Task.FromResult(2)
    };

    /// <summary>--selftest-prefix: creates the Wine prefix (wineboot, win10, shims, DXMT, registry) and checks the result. Use a scratch HOME.</summary>
    static async System.Threading.Tasks.Task<int> PrefixSelfTestAsync()
    {
        Paths.Ensure();
        await Backend.Prefix.EnsureSetupAsync(new Progress<double>(p => Console.WriteLine($"progress {p:0.00}")), default);
        var sys32 = System.IO.Path.Combine(Paths.Prefix, "drive_c", "windows", "system32");
        var failures = 0;
        foreach (var n in new[] { "xgameruntime.dll", "mcwinrt.dll", "flarial_vault.dll", "d3d11.dll", "dxgi.dll", "d3d10core.dll", "winemetal.dll" })
        {
            var ok = System.IO.File.Exists(System.IO.Path.Combine(sys32, n));
            Console.WriteLine((ok ? "ok   " : "FAIL ") + "system32/" + n); if (!ok) failures++;
        }
        var reg = System.IO.File.ReadAllText(System.IO.Path.Combine(Paths.Prefix, "system.reg"));
        foreach (var k in new[] { "Windows.Foundation.Metadata.ApiInformation", "Windows.Security.Credentials.PasswordVault" })
        {
            var ok = reg.Contains(k);
            Console.WriteLine((ok ? "ok   " : "FAIL ") + "registry " + k); if (!ok) failures++;
        }
        Console.WriteLine($"{(Backend.Prefix.IsReady ? "ok  " : "FAIL")} prefix ready, wine user {Paths.WineUser}"); if (!Backend.Prefix.IsReady) failures++;
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Starts a launch.log timeline at the UI click (the backend continues it).</summary>
    public static void LaunchBegin(string what) => Launch.LaunchLog.Begin(what);

    /// <summary>Adds a UI-side phase to launch.log.</summary>
    public static void LaunchPhase(string name) => Launch.LaunchLog.Phase(name);

    /// <summary>Registers the macOS backend. Call once at startup.</summary>
    public static void Use()
    {
        Paths.Ensure();
        Platform.Platform.Game = new LinuxGameService();
        Platform.Platform.Injector = new LinuxInjector();
        Platform.Platform.MicrosoftAccount = new LinuxMicrosoftAccount();
        Platform.Platform.Credentials = new LinuxCredentialStore();
    }
}
