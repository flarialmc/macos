using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Flarial.Runtime.Linux.Engine;
using Flarial.Runtime.Linux.Launch;

namespace Flarial.Runtime.Linux.Injection;

sealed class InjectorCore(IEngine engine) : IInjectorCore
{
    static bool s_extracted;

    public Task EnsureInjectorExeAsync(CancellationToken ct)
    {
        if (s_extracted && File.Exists(Paths.Injector) && File.Exists(Paths.BcryptShim)) return Task.CompletedTask;
        Extract("injector.exe", Paths.Injector);
        Extract("flarial_bcrypt_shim.dll", Paths.BcryptShim);
        s_extracted = true;
        return Task.CompletedTask;
    }

    static void Extract(string resource, string target)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!;
        using var m = new MemoryStream(); s.CopyTo(m);
        var bytes = m.ToArray();
        if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) return;
        Directory.CreateDirectory(Paths.Cache);
        var tmp = target + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, target, true);
    }

    static bool WinedbgRunning() => ProcScan.PrefixPids().Any(p => ProcScan.CmdLine(p).FirstOrDefault() is { } a && Path.GetFileName(a.Replace('\\', '/')).StartsWith("winedbg", StringComparison.OrdinalIgnoreCase));

    static async Task<bool> GameSurvives(int pid, TimeSpan time, CancellationToken ct)
    {
        for (var end = DateTime.UtcNow + time; DateTime.UtcNow < end; await Task.Delay(250, ct))
            if (!ProcScan.Alive(pid) || WinedbgRunning()) return false;
        return true;
    }

    static readonly System.Text.RegularExpressions.Regex Swapchain = new(@"dxgi_vk_swap_chain_init: Creating swapchain \((\d+) x (\d+)\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Windows launcher parity: inject once the game's main menu has loaded (menu_load_lock deleted, see ReadyGate), no fixed delay.
    /// Fallback when the game never creates that file: the first real (&gt;100x100) vkd3d swapchain, the Linux equivalent of the
    /// "window is visible" check the Windows launcher uses for sideloaded installs. <paramref name="cap"/> is the most we wait
    /// (no signal by then: inject anyway). False = game died.
    /// </summary>
    static async Task<bool> WaitReady(int pid, TimeSpan cap, CancellationToken ct)
    {
        var mode = Settings.InjectWait;
        if (cap <= TimeSpan.Zero || mode == "none") return ProcScan.Alive(pid) && !WinedbgRunning();
        var log = Path.Combine(Paths.Logs, "minecraft.log");
        var pos = LaunchLog.GameLogOffset;
        var end = DateTime.UtcNow + cap; var tail = ""; var swapchain = false;
        while (true)
        {
            if (!ProcScan.Alive(pid) || WinedbgRunning()) return false;
            if (mode == "menu" && ReadyGate.Deleted) { LaunchLog.Phase("ready: menu_load_lock deleted (main menu loaded)"); break; }
            if (!swapchain && pos >= 0)
                try
                {
                    using var f = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    if (f.Length < pos) pos = 0;
                    f.Seek(pos, SeekOrigin.Begin);
                    var text = tail + new StreamReader(f).ReadToEnd();
                    var nl = text.LastIndexOf('\n');
                    pos = f.Length; tail = nl >= 0 ? text[(nl + 1)..] : text; // keep a partial last line for the next read
                    foreach (System.Text.RegularExpressions.Match m in Swapchain.Matches(text))
                        if (int.Parse(m.Groups[1].Value) > 100 && int.Parse(m.Groups[2].Value) > 100) { swapchain = true; LaunchLog.Phase($"swapchain {m.Groups[1]}x{m.Groups[2]} (window exists)"); break; }
                }
                catch { }
            // the game does not use menu_load_lock (never created): the window is the signal
            if (swapchain && (mode == "swapchain" || !ReadyGate.Seen)) { LaunchLog.Phase("ready: window visible, no menu_load_lock"); break; }
            if (DateTime.UtcNow >= end) { LaunchLog.Phase("no ready signal within the wait cap, injecting anyway"); break; }
            await Task.Delay(50, ct);
        }
        if (Settings.InjectSettleMs is > 0 and var settle) await Task.Delay(settle, ct);
        return ProcScan.Alive(pid) && !WinedbgRunning();
    }

    /// <summary>Read (and removed from its own environment) by injector.exe; an environment variable is only readable by this user, unlike argv.</summary>
    internal const string PayloadVariable = "FLARIAL_LAUNCHER_PAYLOAD";

    static string ToWine(string path) => path.Length > 2 && path[1] == ':' && path[2] == '\\' ? path : "Z:" + Path.GetFullPath(path).Replace('/', '\\');

    public async Task<InjectResult> InjectAsync(IReadOnlyList<string> libraries, uint gamePid, TimeSpan delay, string? payload, CancellationToken ct)
    {
        if (libraries.Count == 0) return new(false, -1, "No library to inject.");
        var dll = Path.GetFullPath(libraries[^1]);
        if (!dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || !File.Exists(dll)) return new(false, -1, "The DLL is missing or not a .dll file.");
        var pid = (int)gamePid;
        if (!ProcScan.Alive(pid)) return new(false, -1, "Minecraft is not running.");
        if (WinedbgRunning()) return new(false, -1, "Wine crash handler (winedbg) is active.");

        await EnsureInjectorExeAsync(ct);
        if (!await WaitReady(pid, delay, ct)) return new(false, -1, "Minecraft exited before injection.");
        LaunchLog.Phase($"inject start ({libraries.Count - 1} dependencies + client{(payload is null ? "" : ", launcher payload")})");

        var env = new Dictionary<string, string?> { ["WINEPREFIX"] = Paths.Prefix, ["WINEESYNC"] = "1", ["WINEFSYNC"] = "1", ["WINEDEBUG"] = "-all", [PayloadVariable] = payload };
        var info = Proc.Info(engine.Wine, [Paths.Injector, "Minecraft.Windows.exe", .. libraries.Select(ToWine)], env);
        // a process that was only just created may not be openable yet (not found / OpenProcess / CreateRemoteThread fail): retry briefly with a small backoff
        // injector.exe is bounded: a LoadLibrary hung in the target (loader lock) or a game that died under it must never leave it running forever
        var timeout = TimeSpan.FromSeconds(20 + 2 * libraries.Count);
        var retryEnd = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        int code;
        using var gone = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(async () => { while (!gone.IsCancellationRequested) { await Task.Delay(250); if (!ProcScan.Alive(pid)) { try { gone.Cancel(); } catch (ObjectDisposedException) { } } } });
        for (var attempt = 0; ; attempt++)
        {
            try { code = await Proc.RunAsync(info, Path.Combine(Paths.Logs, "injector.log"), timeout, gone.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                LaunchLog.Phase("injector.exe killed: the game process disappeared during injection");
                return new(false, -1, "Minecraft exited during injection.");
            }
            if (code is not (3 or 4 or 6) || DateTime.UtcNow >= retryEnd || !ProcScan.Alive(pid)) break;
            if (attempt == 0) LaunchLog.Phase($"injector exit {code}, retrying");
            await Task.Delay(Math.Min(100 * (attempt + 1), 500), CancellationToken.None);
        }
        gone.Cancel();
        if (code == -1) LaunchLog.Phase($"injector.exe killed after {timeout.TotalSeconds:0} s (hung in the game process)");
        LaunchLog.Phase($"injector.exe finished (exit {code})");
        if (code != 0) return new(false, code, Describe(code));
#if MACOS
        s_injected = (gamePid, Path.GetFileName(dll));
#endif
        // non-blocking: report success now, log a crash right after injection from the background
        _ = Task.Run(async () => { if (!await GameSurvives(pid, TimeSpan.FromMilliseconds(1500), CancellationToken.None)) LaunchLog.Phase("game exited or crashed within 1.5 s after injection"); });
        return new(true, 0, "Injected.");
    }

    static string Describe(int code) => code switch
    {
        -1 => "Injector timed out (the client's LoadLibrary hung inside Minecraft).",
        2 => "Injector usage error.",
        3 => "Game process not found by the injector (it is not in the injector's wineserver: stale Wine session?).",
        4 => "Could not open the game process.",
        5 => "Could not write to the game process.",
        6 => "Could not create the remote thread.",
        7 => "LoadLibrary failed (bad, 32-bit or missing dependencies).",
        8 => "LoadLibrary timed out.",
        10 => "Could not resolve the game process loader address.",
        _ => $"Injector failed (exit {code})."
    };

#if MACOS
    // the client DLL is usually copied (not file-mapped) by Wine, so the region scan can miss it: remember what injector.exe loaded where
    static (uint Pid, string Name)? s_injected;

    public bool IsModuleLoaded(uint gamePid, string fileNameContains) =>
        s_injected is { } i && i.Pid == gamePid && ProcScan.Alive((int)gamePid) && i.Name.Contains(fileNameContains, StringComparison.OrdinalIgnoreCase)
        || ProcScan.MapsFile((int)gamePid, fileNameContains);
#else
    public bool IsModuleLoaded(uint gamePid, string fileNameContains)
    {
        try
        {
            return File.ReadLines($"/proc/{gamePid}/maps").Any(l =>
                l.IndexOf('/') is >= 0 and var i && Path.GetFileName(l[i..].Replace(" (deleted)", "")).Contains(fileNameContains, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }
#endif
}
