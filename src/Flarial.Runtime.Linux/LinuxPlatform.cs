using System;
using Flarial.Runtime.Platform;

namespace Flarial.Runtime.Linux;

public static class LinuxPlatform
{
    /// <summary>Launcher settings/data directory ($XDG_DATA_HOME/Flarial/Launcher); always absolute.</summary>
    public static string LauncherDataDirectory => System.IO.Path.Combine(Paths.DataHome, "Flarial", "Launcher");

    /// <summary>Flarial client data folder as the game sees it (%LOCALAPPDATA%\\Flarial\\Client inside the Wine prefix); created on demand.</summary>
    public static string ClientDirectory => System.IO.Directory.CreateDirectory(System.IO.Path.Combine(Paths.Prefix, "drive_c", "users", Paths.WineUser, "AppData", "Local", "Flarial", "Client")).FullName;

    /// <summary>UI hook for backend messages (set by the launcher; shown as a notification).</summary>
    public static Action<string>? Notify { get; set; }

    /// <summary>Dev checks (--selftest-engine / --selftest-xodus); returns the exit code.</summary>
    public static System.Threading.Tasks.Task<int> SelfTestAsync(string name) => name switch
    {
        "dependencies" => DependenciesSelfTest.RunAsync(),
        "payload" => PayloadSelfTest.RunAsync(),
        "updater" => Update.UpdaterSelfTest.RunAsync(),
        "engine" => Engine.EngineSelfTest.RunAsync(),
        "umu" => System.Threading.Tasks.Task.FromResult(Launch.LauncherCore.ExtractUmu() is var d && d.EndsWith(".d") ? 0 : 1),
        "xodus" => Xodus.XodusSelfTest.RunAsync(),
        "hive" => System.Threading.Tasks.Task.FromResult(Prefix.HiveEdit.SetString(Environment.GetEnvironmentVariable("FLARIAL_HIVE")!, @"Software\\Wine\\WineGDK", "RefreshToken", Environment.GetEnvironmentVariable("FLARIAL_HIVE_TOKEN")) ? 0 : 1),
        "import" => GameContent.SelfTest(),
        _ => System.Threading.Tasks.Task.FromResult(2)
    };

    /// <summary>Starts a launch.log timeline at the UI click (the backend continues it).</summary>
    public static void LaunchBegin(string what) => Launch.LaunchLog.Begin(what);

    /// <summary>Adds a UI-side phase to launch.log.</summary>
    public static void LaunchPhase(string name) => Launch.LaunchLog.Phase(name);

    /// <summary>Registers the Linux backend. Call once at startup.</summary>
    public static void Use()
    {
        Paths.Ensure(); // creates the data dirs and applies the dev seed (FLARIAL_LINUX_SEED_FROM) before anything scans for installed games
        Platform.Platform.Game = new LinuxGameService();
        Platform.Platform.Injector = new LinuxInjector();
        Platform.Platform.MicrosoftAccount = new LinuxMicrosoftAccount();
        Platform.Platform.Credentials = new LinuxCredentialStore();
    }
}
