using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Flarial.Runtime.Linux.Launch;

/// <summary>
/// macOS twin of the /proc helpers. Argument strings come from sysctl KERN_PROCARGS2, which reads the process's own argument area, so
/// Wine's in-place argv rewrite is visible; works for Rosetta (x86_64) processes. Current macOS no longer returns another process's
/// environment there (even the same user's), so a process is "in our prefix" by what it runs instead of by WINEPREFIX: an executable from
/// our own engine's Wine bin directory (the engine copy is private to the launcher), xodus-cli, or the launch wrapper.
/// </summary>
static class ProcScan
{
    [DllImport("libc", SetLastError = true)] static extern int kill(int pid, int sig);
    [DllImport("libc", SetLastError = true)] static extern int sysctl(int[] name, uint namelen, byte[]? oldp, ref nint oldlenp, nint newp, nint newlen);
    [DllImport("libproc", SetLastError = true)] static extern int proc_listallpids(int[]? buffer, int buffersize);

    const int CTL_KERN = 1, KERN_ARGMAX = 8, KERN_PROCARGS2 = 49;

    public static bool Signal(int pid, int sig) => kill(pid, sig) == 0;

    static int s_argMax;

    /// <summary>(exec path, argv, environment if the kernel still returns it) or null when the process is gone, a zombie or not ours.</summary>
    static (string Exec, string[] Argv, string[] Env)? Args(int pid)
    {
        try
        {
            if (s_argMax == 0)
            {
                var b = new byte[4]; nint l = 4;
                s_argMax = sysctl([CTL_KERN, KERN_ARGMAX], 2, b, ref l, 0, 0) == 0 ? BitConverter.ToInt32(b) : 1 << 20;
            }
            var buf = new byte[s_argMax]; nint len = buf.Length;
            if (sysctl([CTL_KERN, KERN_PROCARGS2, pid], 3, buf, ref len, 0, 0) != 0 || len < 4) return null;
            var argc = BitConverter.ToInt32(buf, 0);
            var pos = 4;
            string Next()
            {
                var end = Array.IndexOf(buf, (byte)0, pos, (int)len - pos);
                if (end < 0) end = (int)len;
                var s = Encoding.UTF8.GetString(buf, pos, end - pos);
                pos = end + 1;
                return s;
            }
            var exec = Next();
            while (pos < len && buf[pos] == 0) pos++; // padding after the exec path
            var argv = new List<string>();
            for (var i = 0; i < argc && pos < len; i++) argv.Add(Next());
            var env = new List<string>();
            while (pos < len && buf[pos] != 0) env.Add(Next());
            return (exec, argv.ToArray(), env.ToArray());
        }
        catch { return null; }
    }

    public static string[] CmdLine(int pid) => Args(pid)?.Argv ?? [];

    /// <summary>The executable the kernel started (wine64-preloader / wine64 for Wine processes), independent of argv rewrites.</summary>
    public static string ExecPath(int pid) => Args(pid)?.Exec ?? "";

    static string Real(string path)
    {
        try { return new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? Path.GetFullPath(path); } catch { return path; }
    }

    /// <summary>Wine bin directory of the engine, symlinks resolved (a seeded dev engine is a link), with a trailing slash.</summary>
    static string WineBin => Real(Path.GetDirectoryName(Paths.Wine)!).TrimEnd('/') + "/";

    static bool InPrefix(int pid)
    {
        if (Args(pid) is not { } a) return false;
        var exec = a.Exec;
        return exec.StartsWith(WineBin, StringComparison.Ordinal) || exec.StartsWith(Real(Paths.Engine).TrimEnd('/') + "/", StringComparison.Ordinal)
            || exec == Paths.XodusBin || a.Argv.Contains(Paths.Wrapper);
    }

    static int[] AllPids()
    {
        var n = proc_listallpids(null, 0);
        if (n <= 0) return [];
        var pids = new int[n + 64];
        n = proc_listallpids(pids, pids.Length * sizeof(int));
        return n <= 0 ? [] : pids[..Math.Min(n, pids.Length)];
    }

    /// <summary>All pids (except ours) running our engine's Wine, xodus-cli or the launch wrapper.</summary>
    public static List<int> PrefixPids()
    {
        List<int> r = [];
        foreach (var pid in AllPids())
            if (pid > 0 && pid != Environment.ProcessId && InPrefix(pid)) r.Add(pid);
        return r;
    }

    static bool IsWine(int pid) =>
        Path.GetFileName(ExecPath(pid)).StartsWith("wine", StringComparison.OrdinalIgnoreCase) ||
        CmdLine(pid).FirstOrDefault() is { } a && Path.GetFileName(a.Replace('\\', '/')).ToLowerInvariant() is var n && (n.StartsWith("wine") || n.EndsWith(".exe"));

    /// <summary>Prefix pids that are actual wine processes (wineserver, wine*, *.exe). Excludes the launch's own pre-wine chain (xodus-cli, wrapper sh).</summary>
    public static List<int> WinePids() => PrefixPids().FindAll(IsWine);

    /// <summary>A Wine process running <paramref name="exe"/> (argv carries its NT path ending in \exe).</summary>
    public static bool Runs(int pid, string exe) =>
        IsWine(pid) && CmdLine(pid).Any(a => a.Replace('\\', '/').EndsWith("/" + exe, StringComparison.OrdinalIgnoreCase));

    [DllImport("libproc")] static extern int proc_pidinfo(int pid, int flavor, ulong arg, byte[] buffer, int buffersize);
    [DllImport("libproc")] static extern int proc_regionfilename(int pid, ulong address, byte[] buffer, uint buffersize);
    const int PROC_PIDREGIONINFO = 7, RegionInfoSize = 96; // struct proc_regioninfo; pri_address at 80, pri_size at 88

    /// <summary>
    /// True when a file-backed memory region of the process has a file name containing <paramref name="fileNameContains"/> (the /proc/pid/maps
    /// check of Linux). Wine maps a PE image from its file only when its sections are page aligned; otherwise the copy is anonymous.
    /// </summary>
    public static bool MapsFile(int pid, string fileNameContains)
    {
        var info = new byte[RegionInfoSize]; var path = new byte[1024];
        ulong addr = 0; string last = "";
        for (var guard = 0; guard < 200_000 && proc_pidinfo(pid, PROC_PIDREGIONINFO, addr, info, info.Length) == info.Length; guard++)
        {
            var start = BitConverter.ToUInt64(info, 80); var size = BitConverter.ToUInt64(info, 88);
            var n = proc_regionfilename(pid, start, path, (uint)path.Length);
            if (n > 0)
            {
                var file = Encoding.UTF8.GetString(path, 0, n);
                if (file != last && Path.GetFileName(file).Contains(fileNameContains, StringComparison.OrdinalIgnoreCase)) return true;
                last = file;
            }
            if (size == 0 || start + size <= addr) break;
            addr = start + size;
        }
        return false;
    }

    public static bool Alive(int pid) => kill(pid, 0) == 0 && Args(pid) is not null;
}
