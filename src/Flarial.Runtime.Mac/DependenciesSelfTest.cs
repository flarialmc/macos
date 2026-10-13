using System;
using System.Threading.Tasks;

namespace Flarial.Runtime.Linux;

static class DependenciesSelfTest
{
    public static Task<int> RunAsync()
    {
        var failures = 0;
        void Check(bool result, string name) { Console.WriteLine((result ? "ok   " : "FAIL ") + name); if (!result) failures++; }
        Check(SystemDependencies.Missing(_ => true).Count == 0, "complete setup requires no install");
        var missing = SystemDependencies.Missing(_ => false);
        Check(missing.Count == 1 && missing[0].Command == "rosetta", "only Rosetta can be missing");
        Check(SystemDependencies.InstallationPlan(missing)?.Command == "sudo softwareupdate --install-rosetta --agree-to-license", "Rosetta install plan");
        Check(SystemDependencies.InstallationPlan([]) is null, "nothing to install, no plan");
        Console.WriteLine($"info rosetta installed={SystemDependencies.RosettaInstalled}");
        return Task.FromResult(failures == 0 ? 0 : 1);
    }
}
