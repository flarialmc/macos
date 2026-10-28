# Remote loader regression

Build `remote-loader-fixture.c` as an x64 DLL at preferred base `0x60000000`, with relocations enabled and `/DYNAMICBASE:NO`. Build `remote-loader.c` as an x64 console program with the same Windows SDK and static CRT as the injector. Keep build output in a temporary directory.

Run under the Wine engine:

```sh
"$WINE_BIN" remote-loader.exe 'Z:\absolute\scratch\remote-loader-fixture.dll'
```

The parent loads the fixture at its preferred base. Its child reserves that address before loading the same DLL, forcing a different base. The parent compares the resolved export with the address reported by the child, then starts a remote thread and checks its return value. Only the test-owned child is terminated afterward.

Compile a second copy of `remote-loader.c` with `TEST_LOCAL_ADDRESS` defined to reproduce the former behavior. That version must exit 1 with different DLL bases and an incorrect target address. The corrected version must exit 0 with a successful remote call. The failing version rejects the address before executing it, so the regression does not deliberately crash the child.
