using System;
using System.IO;
using System.Linq;

namespace Flarial.Runtime.Linux;

/// <summary>All macOS backend locations. Root = ~/Library/Application Support/Flarial/macOS. Same members as the Linux Paths.</summary>
static class Paths
{
    /// <summary>~/Library/Application Support; never relative.</summary>
    public static string DataHome
    {
        get
        {
            var home = Home;
            if (!string.IsNullOrEmpty(home)) return Path.Combine(home, "Library", "Application Support");
            return Path.Combine(Path.GetTempPath(), "flarial-" + Environment.UserName);
        }
    }

    public static string Home
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home) || !Path.IsPathRooted(home)) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home;
        }
    }

    public static string Root { get; } = Directory.CreateDirectory(Path.Combine(DataHome, "Flarial", "macOS")).FullName;

    static string Dir(params string[] parts) => Path.Combine([Root, .. parts]);

    public static string Cache => Dir("cache");
    public static string Logs => Dir("logs");
    public static string Run => Dir("run");
    /// <summary>Wine + DXMT engine (Whisky's Libraries layout: Wine/bin/wine64, Wine/lib/wine/...).</summary>
    public static string EngineDir => Dir("engine");
    public static string Engine => Dir("engine", "Libraries");
    public static string EngineRev => Dir("engine", "engine.rev");
    public static string Wine => Path.Combine(Engine, "Wine", "bin", "wine64");
    public static string Wineserver => Path.Combine(Engine, "Wine", "bin", "wineserver");
    public static string XodusDir => Dir("xodus");
    public static string XodusBin => Dir("xodus", "xodus-cli");
    public static string XodusHome => Dir("xodus-home");
    public static string Prefix => Dir("prefix");
    public static string Games => Dir("games");
    public static string Content => Dir("content");
    public static string MsaToken => Dir("msa", "token.json");
    public static string PreauthDir => Dir("winegdk-preauth");
    public static string DeviceJson => Dir("winegdk-preauth", "device.json");
    public static string GraphicsCache => Dir("graphics-cache");
    public static string Settings => Dir("settings.json");
    public static string Wrapper => Dir("run", "launch-wrapper.sh");
    public static string Injector => Dir("cache", "injector.exe");
    /// <summary>Wine compatibility shim (bcrypt RSA-OAEP), injected into Minecraft before the client. See docs/wine-bcrypt-oaep.md.</summary>
    public static string BcryptShim => Dir("cache", "flarial_bcrypt_shim.dll");

    /// <summary>Not used on macOS (no umu / Steam runtime); kept so shared code compiles.</summary>
    public static string UmuDir => Dir("umu");
    public static string UmuRun => Dir("umu", "umu-run");

    /// <summary>
    /// The Wine user profile folder name under drive_c/users. Proton always uses "steamuser"; CrossOver-based Wine (Whisky) uses
    /// "crossover", upstream Wine the Unix user name. Read from the prefix once it exists.
    /// </summary>
    public static string WineUser
    {
        get
        {
            try
            {
                var users = Path.Combine(Prefix, "drive_c", "users");
                if (Directory.Exists(users) && Directory.GetDirectories(users).Select(Path.GetFileName).FirstOrDefault(n => n is not ("Public" or "public")) is { } n) return n;
            }
            catch { }
            return "crossover";
        }
    }

    public static string GameDir(string edition, string version) => Dir("games", edition, version);

    public static void Ensure()
    {
        foreach (var d in new[] { Cache, Logs, Run, EngineDir, Games, GraphicsCache })
            Directory.CreateDirectory(d);
        foreach (var d in new[] { Dir("msa"), PreauthDir, XodusHome, GraphicsCache })
        {
            Directory.CreateDirectory(d);
            File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        SeedFromExisting();
    }

    /// <summary>
    /// Dev only: FLARIAL_MAC_SEED_ENGINE=&lt;Whisky Libraries dir&gt; links an existing Wine + DXMT engine (e.g.
    /// ~/Library/Application Support/com.franke.Whisky/Libraries) instead of downloading it. The source is only read.
    /// </summary>
    static void SeedFromExisting()
    {
        if (Environment.GetEnvironmentVariable("FLARIAL_MAC_SEED_ENGINE") is not { Length: > 0 } src) return;
        if (Path.Exists(Engine) || !File.Exists(Path.Combine(src, "Wine", "bin", "wine64"))) return;
        File.CreateSymbolicLink(Engine, src);
        File.WriteAllText(Dir(".seeded"), src);
    }
}
