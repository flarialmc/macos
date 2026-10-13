using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Flarial.Runtime.Linux.Engine;

namespace Flarial.Runtime.Linux.Prefix;

/// <summary>
/// The Wine prefix on macOS: created with plain `wine64 wineboot` (no umu), Windows 10, and the shims Whisky's Wine needs for the GDK
/// build (Native/build-shims.sh): xgameruntime.dll and mcwinrt.dll in system32 with their WinRT class registrations, plus the fake
/// PasswordVault shared with Linux. The D3D12 stub is not installed here: the launch wrapper puts it next to the exe.
/// </summary>
internal sealed class PrefixManager(IEngine engine) : IPrefix
{
    const int SetupVersion = 1; // bump to re-apply shims and registry tweaks

    static readonly string[] SystemShims = ["xgameruntime.dll", "mcwinrt.dll", "flarial_vault.dll"];

    // Classes Wine lacks or stubs (E_NOTIMPL) that the GDK build needs, served by mcwinrt.dll (see Native/shims/winrt/mcwinrt.c)
    static readonly string[] McWinRtClasses =
    [
        "Windows.ApplicationModel.DataTransfer.DataTransferManager",
        "Windows.UI.Text.Core.CoreTextServicesManager",
        "Windows.Storage.StorageFile",
        "Windows.Storage.Pickers.FileSavePicker",
        "Microsoft.Windows.Storage.Pickers.FileOpenPicker",
        "Windows.UI.ViewManagement.Core.CoreInputView",
        "Windows.Foundation.Metadata.ApiInformation",
    ];

    static string Tweaks()
    {
        var b = new StringBuilder("""
            Windows Registry Editor Version 5.00

            [HKEY_LOCAL_MACHINE\Software\Microsoft\Windows NT\CurrentVersion\OEM]
            "ConsoleMode"=dword:00000008

            [HKEY_LOCAL_MACHINE\Software\Microsoft\WindowsRuntime\ActivatableClassId\Windows.Security.Credentials.PasswordVault]
            "DllPath"="C:\\windows\\system32\\flarial_vault.dll"
            "ActivationType"=dword:00000000
            "Threading"=dword:00000000
            "TrustLevel"=dword:00000000

            [HKEY_LOCAL_MACHINE\Software\Microsoft\WindowsRuntime\ActivatableClassId\Windows.Security.Credentials.PasswordCredential]
            "DllPath"="C:\\windows\\system32\\flarial_vault.dll"
            "ActivationType"=dword:00000000
            "Threading"=dword:00000000
            "TrustLevel"=dword:00000000

            [HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Internet Settings\WinHttp]
            "DefaultSecureProtocols"=dword:000009a0

            [HKEY_CURRENT_USER\Environment]
            "MICROSOFT_WINDOWSAPPRUNTIME_BOOTSTRAP_INITIALIZE_SHOWUI"="0"
            "MICROSOFT_WINDOWSAPPRUNTIME_BOOTSTRAP_INITIALIZE_FAILFAST"="0"
            "MICROSOFT_WINDOWSAPPRUNTIME_DEPLOYMENT_INITIALIZE_ONERRORSHOWUI"="0"

            [HKEY_CURRENT_USER\Software\Wine\Mac Driver]
            "RetinaMode"="y"

            """);
        foreach (var c in McWinRtClasses)
            b.Append($"\n[HKEY_LOCAL_MACHINE\\Software\\Microsoft\\WindowsRuntime\\ActivatableClassId\\{c}]\n\"DllPath\"=\"C:\\\\windows\\\\system32\\\\mcwinrt.dll\"\n\"ActivationType\"=dword:00000000\n\"Threading\"=dword:00000000\n");
        return b.ToString();
    }

    static string Pfx => Paths.Prefix;
    static string Log => Path.Combine(Paths.Logs, "prefix.log");
    static string Marker => Path.Combine(Pfx, ".flarial-prefix");
    static string Sys32 => Path.Combine(Pfx, "drive_c", "windows", "system32");
    string Want => $"{SetupVersion}|{engine.Revision}|{ShimStamp()}";

    static bool Valid =>
        Directory.Exists(Sys32) && StartsWithReg("system.reg") && StartsWithReg("user.reg");

    static bool StartsWithReg(string f)
    {
        try
        {
            using var s = File.OpenRead(Path.Combine(Pfx, f));
            var b = new byte[22]; var n = s.Read(b);
            return Encoding.ASCII.GetString(b, 0, n) == "WINE REGISTRY Version ";
        }
        catch { return false; }
    }

    public bool IsReady => Valid && File.Exists(Marker) && File.ReadAllText(Marker).Trim() == Want;

    static byte[] Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
        using var m = new MemoryStream(); s.CopyTo(m);
        return m.ToArray();
    }

    /// <summary>Shim bytes changed (launcher update): reinstall them.</summary>
    static string ShimStamp()
    {
        using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var n in SystemShims) h.AppendData(Resource(n));
        return Convert.ToHexString(h.GetHashAndReset())[..12];
    }

    // ---- process helpers -------------------------------------------------------------------------------------------

    /// <summary>Headless env for wine run directly against the prefix (no menu entries, no Mono/Gecko prompts).</summary>
    static Dictionary<string, string?> WineEnv() => new()
    {
        ["WINEPREFIX"] = Pfx, ["WINEDEBUG"] = "-all", ["WINEDLLOVERRIDES"] = "winemenubuilder.exe=d;mscoree=d;mshtml=d",
    };

    static Task<int> Run(System.Diagnostics.ProcessStartInfo i, TimeSpan timeout, CancellationToken ct) => Proc.RunAsync(i, Log, timeout, ct);

    async Task Wine(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var code = await Run(Proc.Info(engine.Wine, args, WineEnv()), timeout, ct);
        await Run(Proc.Info(engine.Wineserver, ["-w"], WineEnv()), TimeSpan.FromSeconds(60), ct); // registry hits disk
        if (code != 0) throw new IOException($"wine {string.Join(' ', args)} failed ({code}); see {Log}");
    }

    // ---- IPrefix ---------------------------------------------------------------------------------------------------

    public async Task EnsureSetupAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Paths.Ensure();
        if (IsReady) { progress?.Report(1); return; }
        if (!engine.IsProtonReady) await engine.EnsureProtonAsync(null, ct);
        Directory.CreateDirectory(Pfx);

        if (!Valid)
        {
            await Wine(["wineboot", "-i"], TimeSpan.FromMinutes(10), ct);
            if (!Valid) throw new IOException($"wineboot did not create a prefix; see {Log}");
            progress?.Report(0.5);
        }
        await Wine(["winecfg", "-v", "win10"], TimeSpan.FromMinutes(2), ct);

        Directory.CreateDirectory(Sys32);
        foreach (var n in SystemShims) File.WriteAllBytes(Path.Combine(Sys32, n), Resource(n));
        InstallDxmt();
        await ImportRegAsync(Tweaks(), ct);
        File.WriteAllText(Marker, Want);
        progress?.Report(1);
    }

    // DXMT (D3D11 -> Metal), deployed like Whisky's Wine.enableDXMT: the native d3d11/dxgi/d3d10core trio plus the builtin-marked
    // winemetal.dll redirect (its unix half is the engine's winemetal.so). WINEDLLOVERRIDES at launch makes the trio win over wined3d.
    static readonly string[] DxmtDlls = ["d3d11.dll", "dxgi.dll", "d3d10core.dll", "winemetal.dll"];

    void InstallDxmt()
    {
        foreach (var (arch, dir) in new[] { ("x64", "system32"), ("x32", "syswow64") })
        {
            var to = Path.Combine(Pfx, "drive_c", "windows", dir);
            if (!Directory.Exists(to)) continue;
            foreach (var n in DxmtDlls)
            {
                var from = Path.Combine(engine.ProtonDir, "DXMT", arch, n);
                if (!File.Exists(from)) throw new FileNotFoundException("The engine has no DXMT payload (" + arch + "/" + n + ").", from);
                File.Copy(from, Path.Combine(to, n), true);
            }
        }
    }

    async Task ImportRegAsync(string reg, CancellationToken ct)
    {
        var file = Path.Combine(Paths.Run, "prefix-" + Guid.NewGuid().ToString("N") + ".reg");
        Directory.CreateDirectory(Paths.Run);
        await using (var fs = new FileStream(file, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            await fs.WriteAsync(new UTF8Encoding(false).GetBytes(reg.Replace("\r", "").Replace("\n", "\r\n")), ct);
        try { await Wine(["regedit", "/S", WinPath(file)], TimeSpan.FromMinutes(3), ct); }
        finally { File.Delete(file); }
    }

    static string WinPath(string unix) => "Z:" + unix.Replace('/', '\\');

    /// <summary>Linux hands WineGDK the token through the registry; on macOS the xgameruntime shim reads %APPDATA%\xgr\auth.json (XgrAuthBridge).</summary>
    public Task SetRefreshTokenAsync(string? token, CancellationToken ct)
    {
        XgrAuthBridge.PushToGame(token);
        return Task.CompletedTask;
    }

    public async Task PrepareGameAsync(string gameDir, IProgress<double>? progress, CancellationToken ct)
    {
        await InstallGameInputAsync(Path.Combine(gameDir, "Installers", "GameInputRedist.msi"), ct);
        progress?.Report(1);
    }

    /// <summary>
    /// The game loads system32\GameInputRedist.dll when its version is at least that of system32\GameInput.dll (Wine's builtin has none),
    /// so the redist from the game's own Installers folder is installed and its client DLL copied there. Keyboard and mouse work with it;
    /// its service crashes under Wine (NtQueryWnfStateData), so controllers do not yet.
    /// </summary>
    async Task InstallGameInputAsync(string msi, CancellationToken ct)
    {
        if (!File.Exists(msi) || !Valid) return;
        var sha = await HashCache.Sha256Async(msi);
        var done = Path.Combine(Pfx, ".flarial-gameinput");
        var dst = Path.Combine(Sys32, "GameInputRedist.dll");
        if (File.Exists(done) && File.ReadAllText(done).Trim() == sha && File.Exists(dst)) return;
        await Wine(["msiexec", "/i", WinPath(msi), "/qn"], TimeSpan.FromMinutes(3), ct);
        var dll = Path.Combine(Pfx, "drive_c", "Program Files", "Microsoft GameInput", "x64", "GameInputRedist.dll");
        if (!File.Exists(dll)) throw new IOException($"GameInput redist install produced no GameInputRedist.dll; see {Log}");
        File.Copy(dll, dst, true);
        await ImportRegAsync("""
            Windows Registry Editor Version 5.00

            [HKEY_LOCAL_MACHINE\Software\Microsoft\GameInput]
            "RedistDir"="C:\\Program Files\\Microsoft GameInput\\x64"
            """, ct);
        File.WriteAllText(done, sha);
    }
}
