using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Flarial.Runtime.Linux.Launch;

/// <summary>
/// --selftest-procscan (use a scratch HOME): a stand-in "wine" binary placed in the engine's bin directory is found as a prefix process,
/// its argv is read back through KERN_PROCARGS2, and the window query does not throw. A process outside the engine is ignored.
/// </summary>
static class ProcScanSelfTest
{
    public static async Task<int> RunAsync()
    {
        var failures = 0;
        void Check(bool result, string name) { Console.WriteLine((result ? "ok   " : "FAIL ") + name); if (!result) failures++; }
        Paths.Ensure();
        if (File.Exists(Paths.Wine) && !File.Exists(Path.Combine(Paths.Root, ".selftest"))) { Console.WriteLine("refusing to touch a real engine: run with a scratch HOME"); return 2; }
        var bin = Path.GetDirectoryName(Paths.Wine)!;
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(Paths.Root, ".selftest"), "");
        var fake = Path.Combine(bin, "wine-selftest");
        File.Delete(fake); File.CreateSymbolicLink(fake, "/bin/bash"); // exec path = the path given to execve, as for the real engine

        using var wine = Process.Start(Proc.Info(fake, ["-c", "sleep 5; true", @"Z:\games\Minecraft.Windows.exe"]))!;
        using var other = Process.Start(Proc.Info("/bin/sleep", ["5"]))!;
        await Task.Delay(300);
        Check(ProcScan.CmdLine(wine.Id) is [_, "-c", "sleep 5; true", @"Z:\games\Minecraft.Windows.exe"], "argv read back");
        Check(ProcScan.ExecPath(other.Id) == "/bin/sleep", "exec path read back");
        Check(ProcScan.PrefixPids().Contains(wine.Id), "engine process found");
        Check(ProcScan.WinePids().Contains(wine.Id), "engine process counted as wine");
        Check(ProcScan.Runs(wine.Id, "Minecraft.Windows.exe"), "game exe recognised from argv");
        Check(!ProcScan.PrefixPids().Contains(other.Id), "process outside the engine ignored");
        Check(ProcScan.Alive(wine.Id), "alive");
        Check(!GameWatch.Hung((uint)wine.Id), "window query works (no window, not yet hung)");
        wine.Kill(); other.Kill(); await wine.WaitForExitAsync(); await other.WaitForExitAsync();
        Check(!ProcScan.Alive(wine.Id), "dead after kill");
        File.Delete(fake);
        return failures == 0 ? 0 : 1;
    }
}
