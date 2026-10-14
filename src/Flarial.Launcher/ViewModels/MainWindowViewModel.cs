using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using Flarial.Launcher.Dialogs.Metadata;
using Flarial.Launcher.Management;
using Flarial.Launcher.Models;
using Flarial.Runtime.Core;
using Flarial.Runtime.Game;
using Flarial.Runtime.Versions;
using ReactiveUI;

namespace Flarial.Launcher.ViewModels;

public sealed partial class MainWindowViewModel : ViewModelBase
{
    readonly SemaphoreSlim _semaphore = new(1, 1);

    public MessageBoxViewModel? CurrentDialog
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public HomeViewModel HomeViewModel { get; }
    public SettingsViewModel SettingsViewModel { get; }
    public NotificationAreaViewModel NotificationArea { get; }
    public VersionRegistry VersionRegistry { get; private set; }

    internal readonly AccountModel _account;
    readonly AppSettings _settings = ((App)Application.Current!).Settings;

    public MainWindowViewModel()
    {
        HomeViewModel = new HomeViewModel(this);
        SettingsViewModel = new SettingsViewModel(this);
        NotificationArea = new NotificationAreaViewModel();
        Flarial.Runtime.Linux.LinuxPlatform.Notify = message => Dispatcher.UIThread.Post(() => NotificationArea.Add(message));

        _account = new();
        VersionRegistry = null!;
    }

    public async Task<string> ShowMessageBoxAsync(string title, string message, IEnumerable<string> buttons)
    {
        await _semaphore.WaitAsync(); try
        {
            try
            {
                CurrentDialog = new(title, message, buttons);
                return await CurrentDialog.Result;
            }
            finally { CurrentDialog = null; }
        }
        finally { _semaphore.Release(); }
    }

    async Task LoginWithDiscordAsync()
    {
        HomeViewModel.LauncherStatus = "Authenticating...";
        await SettingsViewModel.SettingsGeneralViewModel.LoginAsync();
        SettingsViewModel.SettingsGeneralViewModel.LoginActive = false;
    }

    public async void OnLoaded()
    {
        Flarial.Runtime.Linux.Update.LauncherUpdater.ForRunningInstall()?.MarkHealthy();
        await EnsureDependenciesAsync();
        if (!await FlarialLauncher.CanConnectAsync())
        {
            await ConnectionFailureDialog._.ShowAsync();
            Environment.Exit(1);
            return;
        }

        var loginWithDiscordTask = LoginWithDiscordAsync();
        VersionRegistry = await VersionRegistry.GetAsync();

        _ = Task.Run(() =>
        {
            foreach (var version in VersionRegistry) Dispatcher.UIThread.Post(() =>
            {
                SettingsViewModel.SettingsVersionsViewModel.Versions.Add(new(this, version));
            }, DispatcherPriority.Background);
        });

        HomeViewModel.OnPackageStatusChanged();
        Minecraft.PackageStatusChanged += HomeViewModel.OnPackageStatusChanged;

        await loginWithDiscordTask;

        HomeViewModel.LauncherStatus = "Ready!";
        HomeViewModel.IsLaunching = false;

        if (Flarial.Runtime.Linux.Update.LauncherUpdater.ForRunningInstall() is { } updater)
        {
            updater.MarkHealthy();
            if (_settings.AutomaticUpdates) _ = UpdateLauncherAsync(updater);
        }
    }

    // Silent background update (see docs/ui-deviations.md): deferred while the game runs or a version installs; never exits on its own.
    async Task UpdateLauncherAsync(Flarial.Runtime.Linux.Update.LauncherUpdater updater)
    {
        static void Log(string m) { try { System.IO.File.AppendAllText(System.IO.Path.Combine(Flarial.Runtime.Linux.LinuxPlatform.LogsDirectory, "updater.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {m}\n"); } catch { } }
        bool Busy() => Minecraft.IsRunning || SettingsViewModel.SettingsVersionsViewModel.IsInstalling;
        try
        {
            while (Busy()) await Task.Delay(TimeSpan.FromSeconds(30));
            var manifest = await updater.CheckAsync(FlarialLauncher.Version);
            if (manifest is null) return;
            while (Busy()) await Task.Delay(TimeSpan.FromSeconds(30));
            await updater.InstallAsync(manifest);
            if (!await LauncherUpdateAvailableDialog._.ShowAsync()) return;
            if (Busy()) { NotificationArea.Add("Launcher update will apply on the next start."); return; }
            Flarial.Runtime.Linux.Update.LauncherUpdater.SpawnLauncher();
            ((Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Shutdown();
        }
        catch (Exception e) { Log("auto-update failed: " + e.Message); }
    }
}
