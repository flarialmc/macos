#!/bin/bash
# Rebuilds the Windows-side shims embedded by Flarial.Runtime.Mac (they run inside Wine). Needs mingw-w64 (brew install mingw-w64).
#   d3d12.dll        stub that refuses D3D12 device creation, so Minecraft uses its D3D11 renderer (DXMT -> Metal)
#   xgameruntime.dll GDK runtime providers Minecraft needs (XAsync/XTaskQueue, XSystem, XNetworking, XUser sign-in); see docs/macos/*
#   mcwinrt.dll      WinRT classes Wine lacks or stubs (ApiInformation, CoreInputView, pickers, ...)
set -euo pipefail
cd "$(dirname "$0")"
CC="${CC:-x86_64-w64-mingw32-gcc}"
FLAGS=(-O1 -Wall -Wno-unused-function -shared -static -static-libgcc -Wl,--kill-at -Wl,--no-insert-timestamp -s)
"$CC" "${FLAGS[@]}" -o d3d12.dll shims/d3d12stub/d3d12stub.c shims/d3d12stub/d3d12.def
"$CC" "${FLAGS[@]}" -o xgameruntime.dll shims/xgameruntime/xgameruntime.c shims/xgameruntime/system.c shims/xgameruntime/taskqueue.c shims/xgameruntime/extra_*.c -lwinhttp -lbcrypt
"$CC" "${FLAGS[@]}" -o mcwinrt.dll shims/winrt/mcwinrt.c -lole32
shasum -a 256 d3d12.dll xgameruntime.dll mcwinrt.dll
