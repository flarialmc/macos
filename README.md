# Flarial Launcher for macOS

The Flarial Client launcher for Minecraft Bedrock Edition (Windows GDK build), running on macOS through Wine. A fork of
[flarialmc/linux](https://github.com/flarialmc/linux) with a macOS backend; see [MACOS.md](MACOS.md) for the port details.

## Features

- Flarial OAuth sign-in
- Release, Beta or Custom client DLL
- Install, select and delete Minecraft Bedrock GDK versions, downloaded with your own Microsoft account
- Xbox Live sign-in inside the game (friends, servers, Realms)
- The Wine engine (Wine 11 + DXMT) and Wine prefix are downloaded and managed for you
- Signed and notarized `Flarial.app` that updates itself

## Requirements

- A Mac with Apple Silicon, macOS 14 or newer
- Rosetta 2 (the launcher offers to install it if missing)
- A Microsoft account that owns Minecraft for Windows (Bedrock)
- Disk space for the engine plus the game itself

Keyboard and mouse work; controllers do not yet (the GameInput service crashes under Wine).

## Install

Download [Flarial.dmg](https://cdn.flarial.xyz/launcher/macos/Flarial.dmg), open it and drag `Flarial.app` to Applications.

Run it from Applications, not from the DMG: the launcher replaces its own bundle when it updates, so it needs a writable location.

### Uninstall

Delete `Flarial.app` from Applications. To also remove games, the Wine prefix and logins, delete
`~/Library/Application Support/Flarial/macOS`.

## Using it

1. Open the launcher and sign in to Flarial (Settings > Accounts).
2. Sign in to your Microsoft account (Settings > Accounts > Microsoft), then open Settings > Versions and download a Minecraft version.
3. Press Launch. The first launch is slow: the engine is downloaded and the Wine prefix is created. Later launches are much faster.

## Where data lives

Everything is under `~/Library/Application Support/Flarial/macOS`:

| Path | Contents |
|---|---|
| `games/` | Downloaded Minecraft versions (Settings > General > Open Installation Directory) |
| `prefix/` | Wine prefix; the Flarial client folder lives inside it (Settings > General > Open Client Folder); game data, worlds and packs are in its `AppData/Roaming/Minecraft Bedrock` (Open Data Directory, or use the Import buttons for .mcpack/.mcworld/.mcaddon/.mctemplate files) |
| `engine/`, `xodus/` | Wine + DXMT engine and xodus-cli |
| `xodus-home/`, `msa/`, `winegdk-preauth/` | Microsoft/Xbox login state (Flarial credentials are in the Keychain) |
| `launcher/` | Previous launcher bundle kept for rollback after an update |
| `logs/` | Logs |
| `settings.json` | Optional advanced settings (`inject_delay`, `inject_settle_ms`, `custom_env`, `ray_tracing`, `diagnostics`) |

## Troubleshooting

Logs are in `~/Library/Application Support/Flarial/macOS/logs`: `launcher.log`, `launch.log` (launch timeline), `minecraft.log` (game
output), `injector.log`, plus `xodus.log`, `xbox.log`, `prefix.log`, `setup.log`, `dependencies.log` and `updater.log`.

- **"Device group is full"**: your Microsoft account has reached its device limit. Remove a device at <https://account.microsoft.com/devices/content> and try again.
- **First launch takes a long time**: expected, see above. Do not close the launcher while it is installing.
- **Game refuses to launch with a license error**: the account must own Minecraft for Windows, and a license check happens on every launch, so you need to be online.
- **The launcher does not update**: it only updates itself when `Flarial.app` is in a writable folder such as Applications, not when run from the DMG.

## How it works

- The game is downloaded with `xodus-cli` (arm64 build of [flarialmc/xodus](https://github.com/flarialmc/xodus) branch `flarial-macos`) using your Microsoft account, and decrypted on every launch after a license check. Game files are never redistributed.
- The game runs on Wine 11 (x86_64 under Rosetta 2) with DXMT translating D3D11 to Metal. A stub `d3d12.dll` makes the game fall back to its D3D11 renderer.
- `xgameruntime.dll` / `mcwinrt.dll` shims provide the GDK runtime and the WinRT classes Wine lacks, including Xbox device-code sign-in.
- The decrypted game is staged in a private RAM disk by a launch wrapper, then the Flarial client DLL is injected with a small Windows injector run under Wine, which also hands the client your Flarial account access token, see [docs/account-payload.md](docs/account-payload.md).

Architecture:

- `src/Flarial.Launcher`: Avalonia UI and view models, ported 1:1 from [flarialmc/Flarial.Launcher](https://github.com/flarialmc/Flarial.Launcher)
- `src/Flarial.Runtime`: cross-platform backend (HTTP, OAuth, DLL download, version registry, settings) that talks to the OS only through `Platform/Platform.cs`
- `src/Flarial.Runtime.Mac`: macOS backend. Compiles the OS-neutral sources of `Flarial.Runtime.Linux` unchanged and replaces the Linux-specific parts (engine, prefix, launch wrapper, process scan, Keychain, updater); GDK shims in `Native/shims`
- `src/Flarial.Runtime.Linux`: Linux backend, shared with upstream
- `src/Flarial.Runtime.Windows`: original Windows sources, synced with upstream and not built

`src/Directory.Build.props` selects the Mac backend when building on macOS.

## Building from source

Requires the .NET 10 SDK on macOS.

```sh
dotnet run --project src/Flarial.Launcher
tools/make-app-macos.sh                          # publish/Flarial.app (ad hoc signed unless MACOS_SIGN_IDENTITY is set)
tools/make-dmg-macos.sh publish/Flarial.app Flarial.dmg
```

The GDK shims are rebuilt with `src/Flarial.Runtime.Mac/Native/build-shims.sh`; the engine and xodus-cli release assets are packaged with
`tools/package-engine-macos.sh` and `tools/build-xodus-macos.sh`.

Self-tests (run them with a scratch home so they never touch your install: `T=$(mktemp -d); HOME=$T Flarial.Launcher --selftest-<name>`):
`--selftest-dependencies`, `--selftest-procscan`, `--selftest-engine`, `--selftest-prefix`, `--selftest-payload`, `--selftest-import`.
`FLARIAL_MAC_SEED_ENGINE=<Libraries dir>` links an existing engine instead of downloading it.

## Releasing

Pushing a commit to `main` whose subject starts with `release:` starts a release in two stages:

1. `.github/workflows/macos.yml` bumps `version.txt`, builds and signs `Flarial.app` with the Developer ID certificate and submits it to
   Apple's notary service, parking it on a draft release `mac-pending-<version>`.
2. `.github/workflows/macos-publish.yml` checks the submission every 20 minutes. Once accepted, it staples the app, builds, notarizes and
   staples `Flarial.dmg`, signs the update archive and pushes everything to `flarialmc/newcdn` `launcher/macos/`, which serves it from
   cdn.flarial.xyz.

See [MACOS.md](MACOS.md) for the secrets and the update flow.

## Contributing

The UI must match the upstream Windows launcher. Do not restyle it; any necessary difference is recorded in [docs/ui-deviations.md](docs/ui-deviations.md). Backend changes should go through the interfaces in `Platform/Platform.cs`.

## License

GPL-3.0, see [LICENSE](LICENSE), matching upstream Flarial.Launcher. Third-party components and their licenses are listed in [CREDITS.md](CREDITS.md).

## Disclaimer

This project is not affiliated with, endorsed by or sponsored by Mojang Studios, Microsoft or Apple. Minecraft is a trademark of Mojang/Microsoft. You must own a legitimate copy of Minecraft for Windows; the launcher downloads the game from Microsoft's servers under your own license and does not bypass licensing. Piracy is not supported.
