using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Flarial.Runtime.Linux.Engine;

/// <summary>
/// Wine 11.0 (Gcenx macOS build, as shipped in WhiskyWine 3.1.1; x86_64 under Rosetta 2) with DXMT (D3D11 to Metal), packaged in Whisky's Libraries layout and
/// published as a hash-pinned release asset of flarialmc/macos (tools/package-engine-macos.sh). There is no umu / Steam runtime on macOS:
/// the umu members of IEngine are always ready. A seeded dev engine (FLARIAL_MAC_SEED_ENGINE) counts as ready.
/// </summary>
sealed class EngineManager : IEngine
{
    public const string PinnedRevision = "wine11.0-dxmt0.80-r1";
    public const string EngineUrl = "https://github.com/flarialmc/macos/releases/download/engine-" + PinnedRevision + "/flarial-engine-" + PinnedRevision + ".tar.xz";
    const string EngineSha = "ENGINE_MACOS_SHA256";

    static bool Seeded => File.Exists(Path.Combine(Paths.Root, ".seeded"));

    public string ProtonDir => Paths.Engine;
    public string Wine => Paths.Wine;
    public string Wineserver => Paths.Wineserver;
    public string UmuRun => "";
    public string Revision => Seeded ? "seeded" : File.Exists(Paths.EngineRev) ? File.ReadAllText(Paths.EngineRev).Trim() : "";

    public bool IsProtonReady => File.Exists(Wine) && (Seeded || Revision == PinnedRevision);
    public bool IsUmuReady => true;

    public async Task EnsureProtonAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Paths.Ensure();
        if (IsProtonReady) { progress?.Report(1); return; }
        if (!SystemDependencies.RosettaInstalled) throw new InvalidOperationException("Rosetta 2 is required to run Minecraft. Install it from the setup prompt and try again.");

        var tar = Path.Combine(Paths.Cache, $"flarial-engine-{PinnedRevision}.tar.xz");
        await Download.FileAsync(EngineUrl, tar, EngineSha, (d, t) => progress?.Report(t > 0 ? 0.85 * d / t : 0), ct);
        ct.ThrowIfCancellationRequested();

        var tmp = Path.Combine(Paths.EngineDir, ".extract");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        var code = await Proc.RunAsync(Proc.Info("/usr/bin/tar", ["-xf", tar, "-C", tmp]), Path.Combine(Paths.Logs, "setup.log"), TimeSpan.FromMinutes(20), ct);
        if (code != 0) throw new IOException($"tar failed ({code}) extracting the engine");
        progress?.Report(0.95);

        var inner = Path.Combine(tmp, "Libraries");
        if (!File.Exists(Path.Combine(inner, "Wine", "bin", "wine64"))) throw new InvalidDataException("engine archive has no Libraries/Wine/bin/wine64");
        Remove(Paths.Engine);
        Directory.Move(inner, Paths.Engine);
        Directory.Delete(tmp, true);
        File.WriteAllText(Paths.EngineRev, PinnedRevision);
        File.Delete(tar);
        progress?.Report(1);
    }

    public Task EnsureUmuAsync(IProgress<double>? progress, CancellationToken ct)
    {
        progress?.Report(1);
        return Task.CompletedTask;
    }

    static void Remove(string path)
    {
        if (new FileInfo(path).LinkTarget is { }) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, true);
    }
}
