using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Flarial.Launcher.Dialogs.Metadata;
using Flarial.Launcher.Management;
using Flarial.Launcher.Models;
using Flarial.Launcher.Types;
using Flarial.Runtime.Core;
using Flarial.Runtime.Linux;
using Flarial.Runtime.Game;
using Flarial.Runtime.Versions;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.SourceGenerators;

namespace Flarial.Launcher.ViewModels;

public sealed partial class HomeViewModel : ViewModelBase, IProgress<int>
{
    [Reactive] bool _isLaunching = true;
    [Reactive] string _launcherStatus = "Preparing...";
    [Reactive] string _launcherVersion = FlarialLauncher.Version;

    [Reactive] string _gameVersion = "0.0.0";
    [Reactive] IImmutableSolidColorBrush _gameVersionColor = Brushes.Gray;

    UnsupportedVersionDialog UnsupportedVersionDialog => field ??= new(_mainWindowViewModel.VersionRegistry);

    public AccountModel Account => _mainWindowViewModel._account;

    readonly MainWindowViewModel _mainWindowViewModel;
    readonly AppSettings _settings = ((App)Application.Current!).Settings;

    public ReactiveCommand<RxVoid, RxVoid> Launch { get; }
    public ReactiveCommand<RxVoid, RxVoid> CloseWindow { get; }
    public ReactiveCommand<RxVoid, RxVoid> MinimizeWindow { get; }

    public HomeViewModel(MainWindowViewModel mainWindowViewModel)
    {
        _mainWindowViewModel = mainWindowViewModel;

        Launch = ReactiveCommand.CreateFromTask(OnLaunchAsync);
        CloseWindow = ReactiveCommand.Create(static () => MessageBus.Current.SendMessage(WindowStateArgs.Close));
        MinimizeWindow = ReactiveCommand.Create(static () => MessageBus.Current.SendMessage(WindowStateArgs.Minimize));
    }

    async Task OnLaunchAsync()
    {
        IsLaunching = true;
        LinuxPlatform.LaunchBegin("click");
        try
        {
            if (!await _mainWindowViewModel.EnsureDependenciesAsync()) return;
            var path = _settings.CustomDllPath;
            var beta = _settings.BuildType is BuildType.Beta;
            var alphaV3 = _settings.BuildType is BuildType.AlphaV3;
            var release = _settings.BuildType is BuildType.Release;

            FlarialClient? client = null;
            if (beta) client = FlarialClientBeta._;
            if (alphaV3) client = FlarialClientAlphaV3._;
            if (release) client = FlarialClientRelease._;

            if (!GamingServices.IsInstalled)
            {
                await GamingServicesMissingDialog._.ShowAsync();
                return;
            }

            LinuxPlatform.LaunchPhase("ui: gaming services check");
            var isRunning = Minecraft.IsRunning;
            var isInstalled = Minecraft.IsInstalled;
            LinuxPlatform.LaunchPhase("ui: running/installed check");

            if (isInstalled && !isRunning)
            {
                if (Minecraft.IsSideloaded)
                {
                    if (!await SideloadedBootstrapDialog._.ShowAsync())
                        return;
                }

                if (release && !_mainWindowViewModel.VersionRegistry.IsSupported)
                {
                    await UnsupportedVersionDialog.ShowAsync();
                    return;
                }
            }
            else if (!isRunning)
            {
                await GameNotFoundDialog._.ShowAsync();
                return;
            }

            if (client is null)
            {
                ModificationLibrary library = new(path);

                if (!library.IsLoadable)
                {
                    await InvalidCustomDllDialog._.ShowAsync();
                    return;
                }

                LauncherStatus = "Launching...";
                LinuxPlatform.LaunchPhase("ui: custom dll checked");
                if (!await Task.Run(() => Injector.Launch(library)))
                {
                    await LaunchFailureDialog._.ShowAsync();
                    return;
                }

                return;
            }

            if (beta && !await ClientBetaActiveDialog._.ShowAsync())
                return;

            if (alphaV3 && !await ClientAlphaV3ActiveDialog._.ShowAsync())
                return;

            if (FlarialClient.IsRunning)
            {
                await ClientAlreadyInjectedDialog._.ShowAsync();
                return;
            }

            // verify/download the client DLL while the game starts: injection happens seconds later and waits for it
            LauncherStatus = "Launching...";
            var prepared = Task.Run(async () =>
            {
                try { return await client.DownloadAsync(this); }
                finally { LinuxPlatform.LaunchPhase("ui: client dll verified (parallel with game start)"); }
            });
            var launched = Task.Run(() => client.Launch(prepared));

            if (!await prepared)
            {
                var failure = alphaV3 ? FlarialClientAlphaV3._.Failure : default;

                if (failure is FlarialClientAlphaV3Failure.Unavailable)
                    await ClientAlphaV3UnavailableDialog._.ShowAsync();
                else if (failure is FlarialClientAlphaV3Failure.Unauthorized)
                    await ClientAlphaV3AccessDialog._.ShowAsync();
                else
                    await ClientUpdateFailureDialog._.ShowAsync();

                return;
            }

            if (!await launched)
            {
                await LaunchFailureDialog._.ShowAsync();
                return;
            }
        }
        finally
        {
            IsLaunching = false;
            LauncherStatus = "Ready!";
        }
    }

    public void Report(int value) => LauncherStatus = $"Downloading... {value}%";

    public void OnPackageStatusChanged()
    {
        if (!Minecraft.IsInstalled)
        {
            GameVersion = "0.0.0";
            GameVersionColor = Brushes.Gray;
            return;
        }

        GameVersion = VersionRegistry.InstalledVersion;
        GameVersionColor = _mainWindowViewModel.VersionRegistry.IsSupported ? Brushes.DarkGreen : Brushes.DarkRed;
    }
}