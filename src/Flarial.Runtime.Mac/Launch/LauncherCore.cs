using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Flarial.Runtime.Linux.Engine;
using Flarial.Runtime.Linux.Prefix;
using Flarial.Runtime.Linux.Xbox;
using Flarial.Runtime.Linux.Xodus;

namespace Flarial.Runtime.Linux.Launch;

/// <summary>
/// macOS launch: `xodus-cli run` (licence check + decrypt) -> Resources/launch-wrapper.sh (RAM-disk mirror of the game folder) -> Whisky
/// Wine 11.0 with DXMT. Same flow as the Linux LauncherCore minus umu/Steam runtime; the Xbox session goes to the xgameruntime shim
/// through %APPDATA%\xgr\auth.json instead of WINEGDK_PREAUTH_DEVICE.
/// </summary>
sealed class LauncherCore(IEngine engine, IXodus xodus, IPrefix prefix, IXboxAuth xbox) : ILauncherCore
{
    const string Exe = "Minecraft.Windows.exe";
    static readonly TimeSpan GameWait = TimeSpan.FromSeconds(120);

    /// <summary>A game process whose window is long gone is a stale instance, not a running game.</summary>
    public bool IsRunning => FindGamePid() is { } pid && !GameWatch.Hung(pid);

    public uint? FindGamePid()
    {
        foreach (var pid in ProcScan.PrefixPids())
            if (ProcScan.Runs(pid, Exe)) return (uint)pid;
        return null;
    }

    public async Task<uint?> LaunchAsync(string gameDir, LaunchSettings settings, CancellationToken ct)
    {
        if (IsRunning) throw new InvalidOperationException("Minecraft is already running.");
        if (!File.Exists(Path.Combine(gameDir, Exe))) throw new FileNotFoundException("Game executable not found.", Path.Combine(gameDir, Exe));
        if (Interlocked.Exchange(ref s_launching, 1) != 0) throw new InvalidOperationException("A launch is already in progress.");
        try { return await LaunchCoreAsync(gameDir, settings, ct); }
        finally { Volatile.Write(ref s_launching, 0); }
    }

    static int s_launching;

    async Task<uint?> LaunchCoreAsync(string gameDir, LaunchSettings settings, CancellationToken ct)
    {
        if (LaunchLog.TakeFresh()) LaunchLog.Phase("LaunchAsync entered " + gameDir); else LaunchLog.Begin("launch " + gameDir);
        if (ProcScan.PrefixPids() is { Count: > 0 } stale)
        {
            LaunchLog.Phase($"stale cleanup: {stale.Count} leftover process(es) in the prefix, no running game");
            await KillAsync(ct);
        }
        ReadyGate.Start();
        var mlog = Path.Combine(Paths.Logs, "minecraft.log");
        LaunchLog.GameLogOffset = File.Exists(mlog) ? new FileInfo(mlog).Length : 0;

        XgrAuthBridge.PullFromGame();
        var xboxTask = Task.Run(async () =>
        {
            try { return await xbox.RefreshAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch { return new XboxSession(false, null, null, null); }
        }, ct);
        var prep = Task.Run(async () =>
        {
            await LaunchLog.Time("prefix ensure setup", prefix.EnsureSetupAsync(null, ct));
            await LaunchLog.Time("prefix prepare game (shims/gameinput)", prefix.PrepareGameAsync(gameDir, null, ct));
            LinkContent(gameDir);
        }, ct);
        WriteWrapper();
        Directory.CreateDirectory(Paths.Logs);
        // xodus decrypt + licence check runs while the Xbox refresh and the prefix prep finish; the wrapper waits for the go file
        var goFile = Path.Combine(Paths.Run, "launch-go");
        try { File.Delete(goFile); } catch { }
        System.Diagnostics.Process? chain = null;
        System.Diagnostics.Process Start()
        {
            var env = BuildEnv(settings);
            env["FLARIAL_GO_FILE"] = goFile;
            env["FLARIAL_LAUNCH_LOG"] = Path.Combine(Paths.Logs, "launch.log");
            return xodus.StartRun(gameDir, Paths.Wrapper, env, gameDir, mlog);
        }
        if (prefix.IsReady)
        {
            chain = Start();
            LaunchLog.Phase("xodus-cli run started early (decrypt + licence check, then wrapper waits for go)");
        }
        try
        {
            var session = await LaunchLog.Time("xbox refresh (parallel)", xboxTask);
            await prep;
            XgrAuthBridge.PushToGame(session.RefreshToken);
            File.WriteAllText(goFile + ".tmp", "true\n"); File.Move(goFile + ".tmp", goFile, true);
            LaunchLog.Phase($"go file written (xbox online={session.Online})");
            if (chain is null) { chain = Start(); LaunchLog.Phase("xodus-cli run started (decrypt + licence check, then wrapper, then wine)"); }
        }
        catch
        {
            try { File.WriteAllText(goFile, "exit 1\n"); } catch { } // release the waiting wrapper
            throw;
        }
        using var _chain = chain!;

        var deadline = DateTime.UtcNow + GameWait;
        DateTime? exited = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (FindGamePid() is { } pid) { LaunchLog.Phase($"game process found (pid {pid})"); return pid; }
            if (chain!.HasExited) { exited ??= DateTime.UtcNow; if (DateTime.UtcNow - exited > TimeSpan.FromSeconds(3)) { LaunchLog.Phase("chain exited, no game process"); return null; } }
            await Task.Delay(150, ct);
        }
        LaunchLog.Phase("timed out waiting for the game process");
        return null;
    }

    /// <summary>Paths.Content -> gameDir (the selected build).</summary>
    static void LinkContent(string gameDir)
    {
        var info = new FileInfo(Paths.Content);
        if (info.LinkTarget == gameDir) return;
        if (info.LinkTarget is { }) info.Delete();
        else if (Directory.Exists(Paths.Content) || info.Exists) throw new IOException($"{Paths.Content} is not a symlink.");
        File.CreateSymbolicLink(Paths.Content, gameDir);
    }

    static void Extract(string resource, string target, bool exec)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!;
        using var m = new MemoryStream(); s.CopyTo(m);
        var bytes = m.ToArray();
        if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | (exec ? UnixFileMode.UserExecute : 0));
        File.Move(tmp, target, true);
    }

    static string D3D12Stub => Path.Combine(Paths.Cache, "d3d12-stub", "d3d12.dll");

    static void WriteWrapper()
    {
        Extract("launch-wrapper.sh", Paths.Wrapper, true);
        Extract("d3d12.dll", D3D12Stub, false);
    }

    Dictionary<string, string?> BuildEnv(LaunchSettings s)
    {
        // d3d12=n: the stub next to the exe (it rejects device creation, so the game falls back to D3D11); DXMT provides d3d11/dxgi
        var overrides = "d3d10core=n,b;d3d11=n,b;d3d12=n;dxgi=n,b;winemetal=b;winemenubuilder.exe=d" + (Settings.BlockWineGameInput ? ";gameinput=d" : "") +
            (Environment.GetEnvironmentVariable("WINEDLLOVERRIDES") is { Length: > 0 } o ? ";" + o : "");
        Dictionary<string, string?> env = new()
        {
            ["WINEPREFIX"] = Paths.Prefix,
            ["FLARIAL_WINE"] = engine.Wine,
            ["FLARIAL_D3D12_STUB"] = D3D12Stub,
            ["FLARIAL_EXE_NAME"] = Exe,
            ["FLARIAL_STAGE_FALLBACK"] = Paths.Run,
            ["WINEDLLOVERRIDES"] = overrides,
            // what Whisky sets for the known-good bottle (msync, macOS 15.4+ fixes), see MACOS.md
            ["WINE_ENABLE_POSIX_SIGNALS"] = "1",
            ["WINEMSYNC"] = "1",
            ["WINEESYNC"] = "1",
            ["WINEFSYNC"] = "0",
            ["MTL_DEBUG_LAYER"] = "0",
            ["DXMT_SHADER_CACHE_PATH"] = Paths.GraphicsCache,
            ["XGR_MODS"] = "0", // the launcher injects the client itself (injector.exe), the shim's own mod loader stays off
            ["FLARIAL_WINE_NO_GAMEPAD"] = "1", // optional SDL controller startup can deadlock Wine's helper-window creation
            ["MICROSOFT_WINDOWSAPPRUNTIME_BOOTSTRAP_INITIALIZE_SHOWUI"] = "0",
            ["MICROSOFT_WINDOWSAPPRUNTIME_BOOTSTRAP_INITIALIZE_FAILFAST"] = "0",
            ["MICROSOFT_WINDOWSAPPRUNTIME_DEPLOYMENT_INITIALIZE_ONERRORSHOWUI"] = "0",
            ["WINEDEBUG"] = "-all,+loaddll",
        };
        if (s.Diagnostics) env["WINEDEBUG"] = "fixme-all,+loaddll";
        foreach (var kv in s.CustomEnv.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (kv.IndexOf('=') is > 0 and var i) env[kv[..i]] = kv[(i + 1)..];
        return env;
    }

    public async Task KillAsync(CancellationToken ct)
    {
        var env = new Dictionary<string, string?> { ["WINEPREFIX"] = Paths.Prefix };
        try { await Proc.RunAsync(Proc.Info(engine.Wineserver, ["-k"], env), Path.Combine(Paths.Logs, "minecraft.log"), TimeSpan.FromSeconds(15)); } catch { }
        foreach (var (sig, wait) in new[] { (0, 5), (15, 3), (9, 3) })
        {
            if (sig != 0) foreach (var p in ProcScan.PrefixPids()) ProcScan.Signal(p, sig);
            for (var i = 0; i < wait * 4; i++)
            {
                if (ProcScan.PrefixPids().Count == 0) return;
                await Task.Delay(250, ct);
            }
        }
    }
}
