using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Flarial.Runtime.Linux;

namespace Flarial.Launcher.ViewModels;

public sealed partial class MainWindowViewModel
{
#if MACOS
    const string SystemName = "macOS";
#else
    const string SystemName = "Linux";
#endif

    Task<bool>? _dependencyCheck;

    internal Task<bool> EnsureDependenciesAsync() => _dependencyCheck is { IsCompleted: false }
        ? _dependencyCheck : _dependencyCheck = CheckDependenciesAsync();

    async Task<bool> CheckDependenciesAsync()
    {
        var manual = false;
        while (true)
        {
            var missing = SystemDependencies.Missing();
            if (missing.Count == 0) return true;
            var plan = SystemDependencies.InstallationPlan(missing);
            var canInstall = !manual && plan is { } && SystemDependencies.CanInstall(plan);
            var message = "Flarial needs these tools before you can download or launch Minecraft:\n\n" +
                string.Join('\n', missing.Select(t => "• " + t.Command + " (" + t.Purpose + ")"));
            if (plan is { }) message += "\n\n" + SystemDependencies.ManualCommand(plan);
            message += canInstall
                ? "\n\nInstall packages uses your system's password prompt. Only the missing packages and their dependencies will be installed."
                : SystemDependencies.ImmutableSystem
                    ? "\n\nThis system manages packages differently. Add these tools through your system's package setup, restart if required, then choose Check again."
                    : "\n\nInstall these tools in a terminal, then choose Check again. You can keep using Settings if you skip for now.";
            var buttons = canInstall ? new[] { "Install packages", "Copy command", "Later" }
                : plan is { } && !SystemDependencies.ImmutableSystem ? ["Check again", "Copy command", "Later"] : ["Check again", "Later"];
            var choice = await ShowMessageBoxAsync($"Finish {SystemName} setup", message, buttons);
            if (choice == "Copy command")
            {
                if (Application.Current!.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { Clipboard: { } clipboard } })
                {
                    await clipboard.SetTextAsync(plan!.Command);
                    manual = true;
                    NotificationArea.Add("Install command copied. Run it in a terminal, then choose Check again.");
                }
                continue;
            }
            if (choice == "Check again") continue;
            if (choice != "Install packages") return false;

            int code;
            try { code = await InstallDependenciesAsync(); }
            catch (Exception e)
            {
                await ShowMessageBoxAsync("Packages weren't installed", e.Message + "\n\nYou can copy the command and run it in a terminal.", ["Back"]);
                continue;
            }
            if (SystemDependencies.Missing().Count == 0)
            {
                NotificationArea.Add($"{SystemName} setup complete. You're ready to download and launch Minecraft.");
                return true;
            }
            var reason = code switch
            {
                126 => "The system password prompt was cancelled. No further installation was attempted.",
                127 => "Your system couldn't authorize the installation. Try the command in a terminal.",
                0 => "Some tools are still unavailable. Check your PATH or try the command in a terminal.",
                _ => "The package manager couldn't finish. Check your connection and whether another package manager is running."
            };
            await ShowMessageBoxAsync("Setup isn't finished", reason + "\n\nDetails: " + SystemDependencies.LogPath, ["Back"]);
        }
    }

    async Task<int> InstallDependenciesAsync()
    {
        await _semaphore.WaitAsync();
        var window = (Application.Current!.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        void KeepOpen(object? sender, Avalonia.Controls.WindowClosingEventArgs e) => e.Cancel = true;
        try
        {
            if (window is { }) window.Closing += KeepOpen;
            CurrentDialog = new($"Installing {SystemName} components", "Approve the system password prompt to continue.\n\nYour package manager is installing the missing tools. This can take a few minutes. Keep the launcher open until it finishes.\n\nDetails: " + SystemDependencies.LogPath, []);
            return await Task.Run(SystemDependencies.InstallAsync);
        }
        finally
        {
            CurrentDialog = null;
            if (window is { }) window.Closing -= KeepOpen;
            _semaphore.Release();
        }
    }
}
