using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Flarial.Runtime.Linux.Update;

/// <summary>--selftest-updater: fake signed Flarial.app archives served by a local HttpListener; checks install/swap/rollback/tamper rejection.</summary>
static class UpdaterSelfTest
{
    static int _fail;
    static void Check(string name, bool ok) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}"); if (!ok) _fail++; }

    static async Task<bool> Throws(Func<Task> f) { try { await f(); return false; } catch { return true; } }

    static string url = "";

    static void FakeApp(string app, string version)
    {
        var macos = Path.Combine(app, "Contents", "MacOS"); Directory.CreateDirectory(macos);
        Directory.CreateDirectory(Path.Combine(app, "Contents", "Resources"));
        File.WriteAllText(Path.Combine(app, "Contents", "Resources", "version.txt"), version);
        File.WriteAllText(Path.Combine(macos, "Flarial.Launcher"), "#!/bin/sh\n");
    }

    public static async Task<int> RunAsync()
    {
        var tmp = Directory.CreateTempSubdirectory("flarial-updtest").FullName;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = key.ExportSubjectPublicKeyInfoPem();

        var files = new Dictionary<string, byte[]>();
        UpdateManifest Make(string v, string? fileVersion = null, ECDsa? signer = null)
        {
            var build = Path.Combine(tmp, "build-" + v + Guid.NewGuid().ToString("N")[..6]);
            FakeApp(Path.Combine(build, "Flarial.app"), fileVersion ?? v);
            var ar = Path.Combine(tmp, v + ".tar.gz");
            Process.Start("/usr/bin/tar", ["-czf", ar, "-C", build, "Flarial.app"])!.WaitForExit();
            var bytes = File.ReadAllBytes(ar); files["/" + v + ".tar.gz"] = bytes;
            var sig = (signer ?? key).SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            return new(v, $"{url}{v}.tar.gz", Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, Convert.ToBase64String(sig));
        }

        var port = new Random().Next(20000, 40000);
        url = $"http://127.0.0.1:{port}/";
        using var http = new HttpListener(); http.Prefixes.Add(url); http.Start();
        string manifestJson = "";
        _ = Task.Run(async () =>
        {
            while (http.IsListening)
            {
                HttpListenerContext c; try { c = await http.GetContextAsync(); } catch { return; }
                var p = c.Request.Url!.AbsolutePath;
                var b = p == "/m.json" ? System.Text.Encoding.UTF8.GetBytes(manifestJson) : files.GetValueOrDefault(p);
                if (b is null) c.Response.StatusCode = 404; else { c.Response.ContentLength64 = b.Length; await c.Response.OutputStream.WriteAsync(b); }
                c.Response.Close();
            }
        });

        string J(UpdateManifest m) => $"{{\"version\":\"{m.Version}\",\"url\":\"{m.Url}\",\"sha256\":\"{m.Sha256}\",\"size\":{m.Size},\"signature\":\"{m.Signature}\"}}";
        var apps = Path.Combine(tmp, "Applications"); var bundle = Path.Combine(apps, "Flarial.app"); var state = Path.Combine(tmp, "state");
        FakeApp(bundle, "2026.1.1.1");
        var u = new LauncherUpdater(bundle, state, url + "m.json", pub, _ => { });
        async Task<UpdateManifest?> Offer(UpdateManifest m, string installed) { manifestJson = J(m); return await u.CheckAsync(installed); }
        string? Prev() { try { return File.ReadAllText(Path.Combine(state, "previous", "Flarial.app", "Contents", "Resources", "version.txt")).Trim(); } catch { return null; } }

        var v2 = Make("2026.1.1.2"); var v3 = Make("2026.1.1.3");
        Check("current version read from bundle", u.CurrentVersion == "2026.1.1.1");
        Check("no offer when same version", await Offer(Make("2026.1.1.1"), "2026.1.1.1") is null);
        Check("no offer for downgrade", await Offer(v2, "2026.1.1.9") is null);
        var m2 = await Offer(v2, "2026.1.1.1"); Check("check offers newer", m2 is not null);
        await u.InstallAsync(m2!); Check("install v2 swaps bundle, keeps v1 as previous", u.CurrentVersion == "2026.1.1.2" && Prev() == "2026.1.1.1");
        await u.InstallAsync((await Offer(v3, "2026.1.1.2"))!); Check("install v3 -> previous v2", u.CurrentVersion == "2026.1.1.3" && Prev() == "2026.1.1.2");
        Check("staging cleaned", !Directory.Exists(Path.Combine(state, "staging")));

        var v4 = Make("2026.1.1.4");
        Check("tamper: bad sha rejected", await Throws(() => u.InstallAsync(v4 with { Sha256 = new string('0', 64) })) && u.CurrentVersion == "2026.1.1.3");
        Check("tamper: bad size rejected", await Throws(() => u.InstallAsync(v4 with { Size = v4.Size + 1 })) && u.CurrentVersion == "2026.1.1.3");
        Check("tamper: wrong-key signature rejected", await Throws(() => u.InstallAsync(Make("2026.1.1.4", signer: other))) && u.CurrentVersion == "2026.1.1.3");
        Check("tamper: archive/manifest version mismatch rejected", await Throws(() => u.InstallAsync(Make("2026.1.1.5", fileVersion: "2026.1.1.1"))) && u.CurrentVersion == "2026.1.1.3");
        Check("tamper: flipped archive byte rejected", await Throws(async () => { var m = Make("2026.1.1.6"); files["/2026.1.1.6.tar.gz"][10] ^= 1; await u.InstallAsync(m); }) && u.CurrentVersion == "2026.1.1.3");

        // rollback
        u.MarkStarted();
        Check("no rollback within 60s", !u.RollbackIfUnhealthy());
        File.SetLastWriteTimeUtc(Path.Combine(state, "started"), DateTime.UtcNow.AddMinutes(-2));
        u.MarkHealthy();
        Check("no rollback when healthy", !u.RollbackIfUnhealthy());
        File.Delete(Path.Combine(state, "healthy"));
        Check("rollback when never healthy", u.RollbackIfUnhealthy() && u.CurrentVersion == "2026.1.1.2");
        Check("rolled-back version not re-offered", await Offer(v3, "2026.1.1.2") is null);

        http.Stop(); Directory.Delete(tmp, true);
        Console.WriteLine(_fail == 0 ? "updater selftest OK" : $"updater selftest FAILED ({_fail})");
        return _fail == 0 ? 0 : 1;
    }
}
