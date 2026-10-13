using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Flarial.Runtime.Linux;

/// <summary>
/// macOS twin of the Linux dependency check, same API for the setup dialog. Everything the launcher runs ships with macOS
/// (tar, script, stty, open); the only optional system component is Rosetta 2, which runs the x86_64 Wine engine on Apple Silicon.
/// </summary>
public static class SystemDependencies
{
    public sealed record Tool(string Command, string Purpose, string Arch, string Debian, string Fedora, string Suse);
    public sealed record Plan(string Manager, string[] Arguments)
    {
        public string Command => "sudo " + Manager + " " + string.Join(' ', Arguments);
    }

    const string Rosetta = "rosetta";
    public static readonly Tool[] Tools = [new(Rosetta, "running Minecraft (x86_64) on Apple Silicon", "", "", "", "")];

    public static string LogPath => Path.Combine(Paths.Logs, "dependencies.log");

    /// <summary>True on Intel Macs, or when an x86_64 binary can run.</summary>
    public static bool RosettaInstalled
    {
        get
        {
            if (RuntimeInformation.OSArchitecture != Architecture.Arm64) return true;
            try
            {
                using var p = Process.Start(new ProcessStartInfo("/usr/bin/arch", ["-x86_64", "/usr/bin/true"]) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true })!;
                return p.WaitForExit(10000) && p.ExitCode == 0;
            }
            catch { return false; }
        }
    }

    public static List<Tool> Missing(Func<string, bool>? available = null) =>
        Tools.Where(t => !(available ?? (c => c == Rosetta ? RosettaInstalled : ExecutableOnPath(c)))(t.Command)).ToList();

    static bool Executable(string path)
    {
        try { return File.Exists(path) && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0; }
        catch { return false; }
    }

    internal static bool ExecutableOnPath(string command) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':')
        .Any(d => d.Length > 0 && Executable(Path.Combine(d, command)));

    public static Plan? InstallationPlan(IEnumerable<Tool> missing, string? osRelease = null) =>
        missing.Any(t => t.Command == Rosetta) ? new Plan("softwareupdate", ["--install-rosetta", "--agree-to-license"]) : null;

    public static bool ImmutableSystem => false;
    public static string ManualCommand(Plan plan) => plan.Command;
    public static bool CanInstall(Plan plan) => Executable("/usr/bin/osascript") && Executable("/usr/sbin/" + plan.Manager);

    // Only a plan built here from the fixed tool list is passed to privilege elevation (the macOS administrator prompt).
    public static async Task<int> InstallAsync()
    {
        var missing = Missing();
        if (missing.Count == 0) return 0;
        var plan = InstallationPlan(missing);
        if (plan is null || !CanInstall(plan)) throw new InvalidOperationException("Run the command in Terminal, then choose Check again.");
        var shell = "/usr/sbin/" + plan.Manager + " " + string.Join(' ', plan.Arguments);
        var info = Proc.Info("/usr/bin/osascript", ["-e", $"do shell script \"{shell}\" with administrator privileges"]);
        using var log = new StreamWriter(LogPath, true) { AutoFlush = true };
        log.WriteLine($"== {DateTime.UtcNow:O} {plan.Command}");
        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        async Task Pump(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line) lock (log) log.WriteLine(line);
        }
        var output = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
        await process.WaitForExitAsync();
        await output;
        log.WriteLine($"== exit {process.ExitCode}");
        return process.ExitCode;
    }
}
