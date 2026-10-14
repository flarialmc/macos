using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Flarial.Runtime.Linux.Update;

public sealed record UpdateManifest(string Version, string Url, string Sha256, long Size, string Signature);

/// <summary>
/// macOS twin of the Linux updater, same public API for the shared UI. The unit is the whole Flarial.app: a signed .tar.gz of the bundle is
/// downloaded, verified (size, sha256, ECDSA P-256 with the same key as Linux, bundled version.txt == manifest version), and swapped in at the
/// running bundle's path; the old bundle is kept as state/previous/Flarial.app. Health: the new version must reach "Ready!" (MarkHealthy)
/// within 60 s of its first start, otherwise the next start swaps the previous bundle back and never offers that version again.
/// Only active when running from a writable Flarial.app (not from a dev build or a translocated/read-only copy). Never exits the process.
/// </summary>
public sealed class LauncherUpdater
{
    public const string ManifestUrl = "https://cdn.flarial.xyz/launcher/macos/Flarial.Launcher.macOS.json";

    const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEyE2XQRgyEM/Gy+/MITOSFhmEFoDY
        /8mmGUIAdy66nSToJU7whtSWcAjZDojWUhB29xSvmAM+bX1h5eknNadUUQ==
        -----END PUBLIC KEY-----
        """;

    readonly string _bundle, _state, _manifestUrl, _publicKey;
    readonly Action<string> _log;

    public LauncherUpdater(string bundle, string? state = null, string? manifestUrl = null, string? publicKeyPem = null, Action<string>? log = null)
    {
        _bundle = bundle.TrimEnd('/');
        _state = state ?? DefaultRoot;
        _manifestUrl = manifestUrl ?? ManifestUrl;
        _publicKey = publicKeyPem ?? PublicKey;
        _log = log ?? DefaultLog;
    }

    public static string DefaultRoot => Path.Combine(Paths.Root, "launcher");

    static void DefaultLog(string m)
    {
        try { Directory.CreateDirectory(Paths.Logs); File.AppendAllText(Path.Combine(Paths.Logs, "updater.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {m}\n"); } catch { }
    }

    void Log(string m) { try { _log(m); } catch { } }

    string Previous => Path.Combine(_state, "previous", "Flarial.app");
    string StartedFile => Path.Combine(_state, "started");
    string HealthyFile => Path.Combine(_state, "healthy");
    string BadVersionFile => Path.Combine(_state, "bad-version");

    static string BundleVersion(string bundle) => File.ReadAllText(Path.Combine(bundle, "Contents", "Resources", "version.txt")).Trim();

    /// <summary>The Flarial.app the process runs from, or null (dev build, translocated or not writable).</summary>
    public static string? RunningBundle()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return null;
        var macos = Path.GetDirectoryName(exe); var contents = Path.GetDirectoryName(macos); var app = Path.GetDirectoryName(contents);
        if (app is null || Path.GetFileName(macos) != "MacOS" || Path.GetFileName(contents) != "Contents" || !app.EndsWith(".app", StringComparison.Ordinal)) return null;
        if (app.Contains("/AppTranslocation/", StringComparison.Ordinal)) return null; // Gatekeeper's read-only copy: the app has to be moved to Applications first
        return Writable(Path.GetDirectoryName(app)!) && Writable(app) ? app : null;
    }

    static bool Writable(string dir)
    {
        try { var probe = Path.Combine(dir, ".flarial-write-" + Environment.ProcessId); File.WriteAllText(probe, ""); File.Delete(probe); return true; }
        catch { return false; }
    }

    public static LauncherUpdater? ForRunningInstall() => RunningBundle() is { } b ? new LauncherUpdater(b) : null;

    public string? CurrentVersion { get { try { return BundleVersion(_bundle); } catch { return null; } } }

    static Version? Parse(string? s) => Version.TryParse(s?.Trim(), out var v) ? v : null;

    // ---- check / install ----

    public async Task<UpdateManifest?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        var json = JsonNode.Parse(await Download.Http.GetStringAsync(_manifestUrl, ct))!;
        var m = new UpdateManifest(json["version"]!.GetValue<string>(), json["url"]!.GetValue<string>(), json["sha256"]!.GetValue<string>().ToLowerInvariant(), json["size"]!.GetValue<long>(), json["signature"]!.GetValue<string>());
        var remote = Parse(m.Version); var local = Parse(currentVersion);
        if (remote is null) throw new InvalidDataException("bad manifest version");
        if (local is not null && remote <= local) return null;
        if (File.Exists(BadVersionFile) && File.ReadAllText(BadVersionFile).Trim() == m.Version) { Log($"skip {m.Version}: rolled back earlier"); return null; }
        if (CurrentVersion == m.Version) return null; // installed already, applies on the next start
        return m;
    }

    /// <summary>Downloads, verifies and swaps the new bundle in. Throws on any failure and leaves the installed bundle untouched.</summary>
    public async Task InstallAsync(UpdateManifest m, Action<long, long>? progress = null, CancellationToken ct = default)
    {
        var staging = Path.Combine(_state, "staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        try
        {
            Log($"downloading {m.Version} from {m.Url}");
            var archive = Path.Combine(staging, "archive.tar.gz");
            await Download.FileAsync(m.Url, archive, m.Sha256, progress, ct);
            if (new FileInfo(archive).Length != m.Size) throw new InvalidDataException("size mismatch");

            using (var f = File.OpenRead(archive))
            using (var ec = ECDsa.Create())
            {
                ec.ImportFromPem(_publicKey);
                if (!ec.VerifyData(f, Convert.FromBase64String(m.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                    throw new InvalidDataException("signature verification failed");
            }

            var x = Path.Combine(staging, "x"); Directory.CreateDirectory(x);
            if (await Proc.RunAsync(Proc.Info("/usr/bin/tar", ["-xzf", archive, "-C", x]), null, TimeSpan.FromMinutes(5), ct) != 0) throw new IOException("tar extract failed");
            var app = Path.Combine(x, "Flarial.app");
            if (!File.Exists(Path.Combine(app, "Contents", "MacOS", "Flarial.Launcher"))) throw new InvalidDataException("archive has no Flarial.app");
            // the manifest json is unsigned: bind it to the signed archive so an old archive cannot pose as a newer version
            if (Parse(BundleVersion(app)) != Parse(m.Version)) throw new InvalidDataException("archive version != manifest version");

            Swap(app);
            Log($"installed {m.Version} at {_bundle}");
        }
        catch (Exception e) { Log($"install {m.Version} failed: {e.Message}"); throw; }
        finally { try { Directory.Delete(staging, true); } catch { } }
    }

    /// <summary>bundle -> previous, new -> bundle; restores the old bundle if the second move fails. The running process keeps its files open.</summary>
    void Swap(string newBundle)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Previous)!);
        if (Directory.Exists(Previous)) Directory.Delete(Previous, true);
        MoveDir(_bundle, Previous);
        try { MoveDir(newBundle, _bundle); }
        catch { MoveDir(Previous, _bundle); throw; }
        try { File.Delete(StartedFile); } catch { }
    }

    /// <summary>A rename when both sides are on one volume (the usual /Applications + ~/Library case), otherwise a ditto copy.</summary>
    static void MoveDir(string from, string to)
    {
        try { Directory.Move(from, to); return; }
        catch (IOException) when (!Directory.Exists(to)) { }
        using var p = Process.Start(new ProcessStartInfo("/usr/bin/ditto", [from, to]) { UseShellExecute = false })!;
        p.WaitForExit();
        if (p.ExitCode != 0) { try { Directory.Delete(to, true); } catch { } throw new IOException($"ditto {from} -> {to} failed ({p.ExitCode})"); }
        Directory.Delete(from, true);
    }

    // ---- health / rollback ----

    public void MarkStarted()
    {
        if (CurrentVersion is not { } v) return;
        Directory.CreateDirectory(_state);
        if (Read(HealthyFile) != v && Read(StartedFile) != v) File.WriteAllText(StartedFile, v);
    }

    public void MarkHealthy()
    {
        if (CurrentVersion is { } v) try { Directory.CreateDirectory(_state); File.WriteAllText(HealthyFile, v); } catch { }
    }

    static string? Read(string f) { try { return File.ReadAllText(f).Trim(); } catch { return null; } }

    /// <summary>If the current version started >60 s ago without ever becoming healthy, swap the previous bundle back. True when swapped.</summary>
    public bool RollbackIfUnhealthy()
    {
        var cur = CurrentVersion;
        if (cur is null || !Directory.Exists(Previous)) return false;
        if (Read(StartedFile) != cur || Read(HealthyFile) == cur) return false;
        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(StartedFile) < TimeSpan.FromSeconds(60)) return false;
        var trash = Path.Combine(_state, "rolled-back.app");
        if (Directory.Exists(trash)) Directory.Delete(trash, true);
        MoveDir(_bundle, trash);
        MoveDir(Previous, _bundle);
        try { Directory.Delete(trash, true); } catch { }
        File.Delete(StartedFile);
        File.WriteAllText(BadVersionFile, cur);
        Log($"rolled back {cur} -> {CurrentVersion} (never became healthy)");
        return true;
    }

    // ---- restart / self-install ----

    /// <summary>Opens the bundle again after a short delay (lets this process release the single-instance mutex). Does not exit.</summary>
    public static void SpawnLauncher()
    {
        if (RunningBundle() is not { } app) return;
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("sleep 1; exec /usr/bin/open -n \"$0\""); psi.ArgumentList.Add(app);
        Process.Start(psi);
    }

    /// <summary>`--install` is a Linux concept; on macOS the app is installed by dragging it to Applications.</summary>
    public static int SelfInstall()
    {
        Console.WriteLine("Drag Flarial.app to Applications; it updates itself from there.");
        return 0;
    }
}
