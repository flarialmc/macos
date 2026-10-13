# Flarial Launcher for macOS: feasibility and port plan

This repo is a fork of [flarialmc/linux](https://github.com/flarialmc/linux) (remote `upstream`). The goal is the same launcher on
macOS (Apple Silicon first): run the Windows GDK build of Minecraft Bedrock under Wine and inject the Flarial client.

## Verdict

**Feasible, but the hard part is the engine, not the launcher.** The launcher side is about 2 to 3 weeks of work. The engine (a
WineGDK build that runs on macOS with a working graphics path) is the real risk. Nobody has publicly shown Bedrock GDK running on a
Mac yet, so the first milestone has to prove that before anything else is ported.

## What carries over unchanged

| Piece | Why it works on macOS |
|---|---|
| `Flarial.Launcher` (Avalonia UI) | Avalonia is cross-platform. Swap `Avalonia.X11` + `SkiaSharp.NativeAssets.Linux` for `Avalonia.Native` + `SkiaSharp.NativeAssets.macOS`. |
| `Flarial.Runtime` (OAuth, DLL download, versions, settings) | Pure .NET, already talks to the OS only through `Platform/Platform.cs`. |
| `injector.exe`, `flarial_bcrypt_shim.dll`, `flarial_vault.dll` | Windows binaries that run inside Wine. Same on macOS. |
| Xbox device-code auth (`Xbox/`) | Plain HTTPS. |
| Prefix registry edits (`HiveEdit`), account payload | File and Wine-level operations. |

## What has to be replaced

| Linux piece | macOS replacement | Effort |
|---|---|---|
| GDK-Proton engine + vkd3d-proton (D3D12 to Vulkan) | WineGDK patches rebuilt on a macOS Wine (winemac.drv, wow64, x86_64 under Rosetta 2) with a Metal graphics layer. See "Engine" below. | **Large / risky** |
| `umu-run` + Steam Linux Runtime | Drop it. Run `wine` directly; macOS has no container. | Small |
| `xodus-cli` (Linux build from BedrockOnLinux) | Build xodus (Rust, GPL-3.0) for `aarch64-apple-darwin`. Its webview is wry, which supports macOS (WKWebView). | Medium |
| `/proc` scanning (`ProcScan`, `FindGamePid`, `IsModuleLoaded` via `/proc/pid/maps`, `/proc/self/status`) | `sysctl(KERN_PROCARGS2)` / `proc_pidinfo` via P/Invoke for argv and env. Module check through the injector (ask Wine, e.g. `EnumProcessModules` in `injector.exe`) since Mach has no `/proc/pid/maps` that shows PE mappings. | Medium |
| `secret-tool` (libsecret) | Keychain through `/usr/bin/security add-generic-password / find-generic-password`. | Small |
| `xdg-open`, `xprop`, `setsid`, `script` | `open`, window check via `CGWindowListCopyWindowInfo` (or drop it), `posix_spawn` with a new session, a pty from `openpty`. | Small |
| `XDG_*` paths (`~/.local/share/Flarial/Linux`) | `~/Library/Application Support/Flarial/macOS`. | Small |
| `launch-wrapper.sh` | Rewrite without umu/pressure-vessel; the go-file handshake stays. | Small |
| Install / update (`install.sh`, AUR, self-updater) | Signed `.app` bundle in a DMG. Needs an Apple Developer ID for notarisation, otherwise users must right-click > Open. | Medium |

## Engine (the risk)

Bedrock GDK needs three things from Wine: the WineGDK components (GDK runtime, XGameRuntime, XUser), working D3D, and Rosetta-able
x86_64 code.

1. **WineGDK on macOS.** [Weather-OS/WineGDK](https://github.com/Weather-OS/WineGDK) is a Wine fork with no macOS-specific code
   removed, so it should build with the normal macOS Wine toolchain (Gcenx's build scripts, mingw for the PE side, wow64 mode so no
   32-bit host libs are needed). Nobody has published such a build. This is the first thing to try.
2. **Graphics.** vkd3d-proton (what Linux uses) does not run usefully on MoltenVK. Options, best first:
   - **D3D11 path + DXMT** (open source D3D11 to Metal, ships in CrossOver 25+). Reports say Bedrock 26.x runs on WineGDK + DXVK,
     which means the game accepts a D3D11 device. If that holds, DXMT is the cleanest route and is redistributable.
   - **D3D11 path + DXVK on MoltenVK.** Works for many games, slower than DXMT.
   - **D3DMetal (Apple Game Porting Toolkit).** Best D3D12 support, but closed source with a restrictive license: the launcher
     cannot ship it, only use a copy the user installed themselves from Apple.
3. **CPU.** Rosetta 2 handles x86_64 including AVX2 on current macOS, which covers Minecraft and the client DLL.

Fast way to test before building anything: a CrossOver trial on this Mac (CrossOver 26 has D3DMetal 3 and DXMT). It will not have
the WineGDK pieces, so the game may not get past start-up, but it shows whether the graphics side renders.

## Milestones

1. **Engine spike (go/no-go).** Build WineGDK for macOS, decrypt a game with xodus on Linux or a macOS xodus build, run
   `Minecraft.Windows.exe` with DXMT. Done when the main menu renders.
2. **Inject.** Run `injector.exe` against it and confirm the Flarial client loads (bcrypt shim included).
3. **Backend.** Add `src/Flarial.Runtime.Mac` implementing `IGameService`, `IInjector`, `IMicrosoftAccount`, `ICredentialStore`,
   reusing the Linux modules that are OS-neutral (move them to a shared project instead of copying).
4. **UI + packaging.** `Avalonia.Native`, `.app` bundle, DMG, updater.

If milestone 1 fails, the fallback is a launcher that installs into a user-provided CrossOver/GPTK bottle, which works only if those
gain GDK support.
