using System;
using System.IO;
using System.Threading.Tasks;

namespace Flarial.Runtime.Linux.Engine;

/// <summary>--selftest-engine: engine present, wine64 runs under Rosetta and reports its version.</summary>
static class EngineSelfTest
{
    public static async Task<int> RunAsync()
    {
        Paths.Ensure();
        IEngine e = new EngineManager();
        Console.WriteLine($"engine ready={e.IsProtonReady} rev={e.Revision}\nwine={e.Wine}\nwineserver={e.Wineserver} exists={File.Exists(e.Wineserver)}");
        if (!File.Exists(e.Wine)) return 1;
        var log = Path.Combine(Paths.Logs, "selftest-engine.log");
        var code = await Proc.RunAsync(Proc.Info(e.Wine, ["--version"]), log, TimeSpan.FromMinutes(1));
        Console.WriteLine($"wine64 --version exit={code}: {(File.Exists(log) ? File.ReadAllText(log).Trim().Split('\n')[^1] : "")}");
        return e.IsProtonReady && code == 0 ? 0 : 1;
    }
}
