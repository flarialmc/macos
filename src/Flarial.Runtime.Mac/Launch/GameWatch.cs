using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Flarial.Runtime.Linux.Launch;

/// <summary>
/// Detects a game process that outlived its window (Wine sometimes never exits after the window is closed). macOS twin of the xprop
/// version: asks the window server (CGWindowListCopyWindowInfo) whether any on-screen window is owned by the game pid. Owner pids need
/// no Screen Recording permission (only window titles do). Failure to query = unknown = never "hung".
/// </summary>
static class GameWatch
{
    static readonly TimeSpan Gone = TimeSpan.FromSeconds(5), NeverShown = TimeSpan.FromSeconds(60);
    static readonly object s_lock = new();
    static uint s_pid; static bool s_seen; static DateTime? s_noWindowSince, s_checked;

    const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [DllImport(CG)] static extern nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
    [DllImport(CF)] static extern nint CFArrayGetCount(nint array);
    [DllImport(CF)] static extern nint CFArrayGetValueAtIndex(nint array, nint index);
    [DllImport(CF)] static extern nint CFDictionaryGetValue(nint dict, nint key);
    [DllImport(CF)] static extern bool CFNumberGetValue(nint number, int type, out long value);
    [DllImport(CF)] static extern void CFRelease(nint obj);
    [DllImport("libSystem.dylib")] static extern nint dlopen(string path, int mode);
    [DllImport("libSystem.dylib")] static extern nint dlsym(nint handle, string symbol);

    const uint kCGWindowListOptionAll = 0, kCGWindowListExcludeDesktopElements = 1 << 4, kCGNullWindowID = 0;
    const int kCFNumberSInt64Type = 4;

    static nint s_ownerPidKey = -1;

    /// <summary>kCGWindowOwnerPID is an exported CFStringRef constant: read its value from the framework.</summary>
    static nint OwnerPidKey()
    {
        if (s_ownerPidKey != -1) return s_ownerPidKey;
        var h = dlopen(CG, 1);
        var sym = h == 0 ? 0 : dlsym(h, "kCGWindowOwnerPID");
        return s_ownerPidKey = sym == 0 ? 0 : Marshal.ReadIntPtr(sym);
    }

    static bool? HasWindow(uint pid)
    {
        try
        {
            var key = OwnerPidKey();
            if (key == 0) return null;
            var list = CGWindowListCopyWindowInfo(kCGWindowListOptionAll | kCGWindowListExcludeDesktopElements, kCGNullWindowID);
            if (list == 0) return null;
            try
            {
                for (nint i = 0, n = CFArrayGetCount(list); i < n; i++)
                {
                    var owner = CFDictionaryGetValue(CFArrayGetValueAtIndex(list, i), key);
                    if (owner != 0 && CFNumberGetValue(owner, kCFNumberSInt64Type, out var p) && p == pid) return true;
                }
                return false;
            }
            finally { CFRelease(list); }
        }
        catch { return null; }
    }

    /// <summary>True when the game process has had no window for several seconds after having one (or for a minute since it started).</summary>
    public static bool Hung(uint pid)
    {
        if (ReadyGate.Waiting) return false; // startup in progress (<= inject wait cap): after it, the "never shown for 60 s" rule applies
        lock (s_lock)
        {
            var now = DateTime.UtcNow;
            if (s_pid != pid) { s_pid = pid; s_seen = false; s_noWindowSince = null; s_checked = null; }
            if (s_checked is { } c && now - c < TimeSpan.FromSeconds(1.5)) return Verdict(pid, now);
            s_checked = now;
            switch (HasWindow(pid))
            {
                case true: s_seen = true; s_noWindowSince = null; break;
                case false: s_noWindowSince ??= now; break;
                default: s_noWindowSince = null; break; // cannot query
            }
            return Verdict(pid, now);
        }
    }

    static bool Verdict(uint pid, DateTime now)
    {
        if (s_noWindowSince is not { } since || now - since < Gone) return false;
        if (s_seen) return true;
        try { return DateTime.Now - Process.GetProcessById((int)pid).StartTime >= NeverShown; } catch { return false; }
    }
}
