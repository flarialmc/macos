# Account payload for the client

The Flarial client no longer does OAuth2 itself (dll-css `Client/Identity/LauncherPayload.cpp`, `FlarialAccountSession.cpp`). The launcher gives it the account access token when it injects the DLL, as the Windows launcher does (upstream `FlarialClient.Loader`).

## Format

A JSON object stored as the **thread description** of the thread that loads the client DLL:

```json
{"access_token":"<token>"}
```

* Signed out: `{"access_token":null}` (what upstream sends; the client ignores it).
* The client reads it with `GetThreadDescription(GetCurrentThread())` in `DLL_PROCESS_ATTACH`, clears it, and accepts it only if it is at most 64 KiB, a JSON object with a non-empty string `access_token` of at most 4096 printable ASCII characters.
* `FlarialClient.CreatePayload` (`src/Flarial.Runtime/Core/FlarialClient/FlarialClient.Loader.cs`) builds it from `FlarialClient.AccessToken`, which `AuthenticationManager` sets on every silent sign-in and clears on logout. Release, beta and alpha (v3) get it; custom DLLs (`Injector.Launch`, `--inject`) get nothing, as upstream.

## Delivery on Linux

1. `FlarialClient<T>.Loader.Launch` collects the DLL's imports, waits for the game, then calls `IInjector.Inject(libraries, pid, payload)`.
2. `InjectorCore` starts `injector.exe` under Wine in the game prefix and passes the payload in the `FLARIAL_LAUNCHER_PAYLOAD` environment variable (never argv, which every local user can read through `/proc/<pid>/cmdline`).
3. `Native/injector.c` takes the variable out of its own environment, loads the dependencies on plain remote threads, and loads the last library (the client) on a remote thread created `CREATE_SUSPENDED`. It calls `SetThreadDescription` on that thread and then resumes it, so the client's `DllMain` finds the payload on its own thread.
4. A failure to set the description is only a warning in `logs/injector.log` (the client then runs signed out). The payload is never logged.

Upstream queues `LoadLibraryW` APCs for every import and the client on one suspended thread and sets the description before resuming it. Here each dependency uses its own short remote thread (an existing behaviour: a dependency that fails to load only produces a warning) and only the client's thread carries the payload.

## What is still synced

The fake `PasswordVault` (`Native/flarial_vault.dll`, `Prefix/VaultBridge.cs`) is kept: the release DLL currently on the CDN (hash `16912286...` at the time of writing) still reads and rotates the refresh token through the Windows PasswordVault (`Flarial Launcher` / `Discord`). Builds that use the payload do not touch it. Remove the bridge once the release channel ships the payload client.

## Checks

* `Flarial.Launcher --selftest-payload`: JSON bytes and the client's acceptance rules.
* `tests/payload-probe/run.sh`: the real `InjectorCore` and `injector.exe` inject a probe DLL (reads and clears its thread description exactly like the client) into a stand-in `Minecraft.Windows.exe` in a scratch Wine prefix, with a 4000 character token, and verify that the token never shows up on a command line.
