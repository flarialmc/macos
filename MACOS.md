# Flarial Launcher for macOS

This repo is a fork of [flarialmc/linux](https://github.com/flarialmc/linux) (remote `upstream`). The goal is the same launcher on
macOS (Apple Silicon first): run the Windows GDK build of Minecraft Bedrock under Wine and inject the Flarial client.

## Status

**Working on Apple Silicon (verified 2026-10-13, macOS 26.6):** a prefix built by the launcher's own backend runs Minecraft 1.26.50 (GDK)
to an online main menu, with rendering through DXMT and the xgameruntime shim handling GDK. Not yet verified end to end: the launch going
through `xodus-cli run` (download + licence check + decrypt) and client injection.

How the engine works:

- **Wine 11.0** (Gcenx's macOS build, as shipped in WhiskyWine 3.1.1), x86_64 under Rosetta 2. The Whisky app is not used, only the engine.
- **DXMT 0.80** (D3D11 to Metal). Minecraft prefers D3D12, so the launcher puts a stub `d3d12.dll` next to the exe
  (`src/Flarial.Runtime.Mac/Native/shims/d3d12stub`). The stub refuses device creation, so the game falls back to its D3D11 renderer.
- **xgameruntime.dll / mcwinrt.dll shims** in place of WineGDK: GDK runtime providers (XAsync/XTaskQueue, XSystem, XNetworking, XUser
  device-code sign-in) and the WinRT classes Wine lacks. The sources are in `Native/shims`; rebuild them with `Native/build-shims.sh`.
- **xodus-cli** built for arm64 from [flarialmc/xodus](https://github.com/flarialmc/xodus) branch `flarial-macos` (upstream + the
  BedrockOnLinux patches). Whisky's Wine has no `WINE_DLL_FILE_MAP`, so the launch wrapper mirrors the game folder into a private RAM disk
  with the decrypted files and runs Wine from there.

Releases: a `release:` commit on `main` runs `.github/workflows/macos.yml` (macOS runner): bumps `version.txt`, builds `Flarial.app`
(`tools/make-app-macos.sh`), `Flarial.dmg` (`tools/make-dmg-macos.sh`) and the update archive `Flarial.Launcher.macOS.tar.gz`, signs it
with the same key as Linux (secret `LINUX_UPDATE_PRIVATE_KEY`), and pushes all of it to `flarialmc/newcdn` `launcher/macos/` (secrets
`APP_CLIENT_ID`, `APP_PRIVATE_KEY`), which newcdn's `r2-upload.yml` publishes to cdn.flarial.xyz. Updates: the app reads
`Flarial.Launcher.macOS.json`, verifies size, SHA-256, signature and bundled version, moves the old bundle to
`~/Library/Application Support/Flarial/macOS/launcher/previous` and puts the new one in its place; a version that never reaches "Ready!"
is rolled back on the next start. Only works from a writable `Flarial.app` (dragged to Applications), not from the DMG itself.

The app is signed ad hoc, not notarised: on first open macOS asks to allow it (System Settings > Privacy & Security > Open Anyway).
A Developer ID certificate (Apple Developer Program) would remove that. Controllers do not work yet (the GameInput service crashes under
Wine); keyboard and mouse do.

## What carries over unchanged

| Piece | Why it works on macOS |
|---|---|
| `Flarial.Launcher` (Avalonia UI) | Avalonia is cross-platform. Swap `Avalonia.X11` + `SkiaSharp.NativeAssets.Linux` for `Avalonia.Native` + `SkiaSharp.NativeAssets.macOS`. |
| `Flarial.Runtime` (OAuth, DLL download, versions, settings) | Pure .NET, already talks to the OS only through `Platform/Platform.cs`. |
| `injector.exe`, `flarial_bcrypt_shim.dll`, `flarial_vault.dll` | Windows binaries that run inside Wine. Same on macOS. |
| Xbox device-code auth (`Xbox/`) | Plain HTTPS. |
| Prefix registry edits (`HiveEdit`), account payload | File and Wine-level operations. |

## What was replaced

`src/Flarial.Runtime.Mac` compiles the OS-neutral sources of `Flarial.Runtime.Linux` unchanged and swaps out the Linux-specific files
(see the `Exclude` list in its csproj). The public type names stay the same, so the launcher UI is shared. `src/Directory.Build.props`
selects the Mac backend when building on macOS (`FlarialMac=true`, define `MACOS`).

| Linux piece | macOS replacement | State |
|---|---|---|
| GDK-Proton + vkd3d-proton, WineGDK | Wine 11 + DXMT with a D3D12 stub; xgameruntime/mcwinrt shims (`Prefix/PrefixManager.cs`) | Verified: online main menu |
| `umu-run` + Steam Linux Runtime | Dropped; `wine64` runs directly | Done |
| `WINEGDK_PREAUTH_DEVICE` / registry refresh token | `%APPDATA%\xgr\auth.json` for the shim, synced both ways (`Prefix/XgrAuthBridge.cs`) | Written, not yet verified |
| `xodus-cli` (BedrockOnLinux Linux build) | arm64 build of flarialmc/xodus `flarial-macos`, file keyring | Built |
| memfd + `WINE_DLL_FILE_MAP` staging | RAM-disk mirror of the game folder (`Resources/launch-wrapper.sh`) | Written, not yet verified |
| `/proc` scanning | `KERN_PROCARGS2` + `proc_listallpids`. macOS hides other processes' environment, so prefix processes are the ones running our engine's Wine, xodus-cli or the wrapper (`Launch/ProcScan.cs`) | Self-test passes |
| `/proc/pid/maps` module check | libproc region scan plus the last successful injection (`InjectorCore.cs`, `#if MACOS`) | Written |
| `xprop` window check | `CGWindowListCopyWindowInfo` owner pid (`Launch/GameWatch.cs`) | Self-test passes |
| `secret-tool` | Keychain via Security.framework (`MacCredentialStore.cs`) | Written |
| `xdg-open`, zenity, `setsid`, util-linux `script` | `open`, osascript alert, plain spawn, BSD `script` | Done |
| `~/.local/share/Flarial/Linux` | `~/Library/Application Support/Flarial/macOS` | Done |
| distro packages check | Rosetta 2 check and install via the admin prompt (`SystemDependencies.cs`) | Self-test passes |
| `install.sh` / AUR / self-updater | `Flarial.app` in `Flarial.dmg` from cdn.flarial.xyz/launcher/macos; the app swaps itself in place (`Update/LauncherUpdater.cs`) | Self-test passes |

Self-tests (use a scratch HOME; `FLARIAL_MAC_SEED_ENGINE=<Libraries dir>` links an existing engine instead of downloading it):
`--selftest-dependencies`, `--selftest-procscan`, `--selftest-engine`, `--selftest-prefix` (`FLARIAL_GAME_DIR=<unpacked game>` also
installs GameInput), `--selftest-payload`, `--selftest-import`.
