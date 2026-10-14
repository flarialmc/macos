#if MACOS
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Flarial.Launcher;

/// <summary>
/// The Dock takes its icon from the app bundle; a bare executable (dev runs) would show a blank document, so the icon is also set at
/// startup through AppKit: [[NSApplication sharedApplication] setApplicationIconImage:[[NSImage alloc] initWithData:png]].
/// </summary>
static class MacDockIcon
{
    const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] static extern nint objc_getClass(string name);
    [DllImport(ObjC)] static extern nint sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern nint Send(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern nint Send(nint receiver, nint selector, nint arg);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern nint Send(nint receiver, nint selector, nint bytes, nuint length);

    public static void Apply()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("AppIcon.png");
            if (s is null) return;
            var png = new byte[s.Length]; s.ReadExactly(png);
            var buffer = Marshal.AllocHGlobal(png.Length);
            try
            {
                Marshal.Copy(png, 0, buffer, png.Length);
                var data = Send(objc_getClass("NSData"), sel_registerName("dataWithBytes:length:"), buffer, (nuint)png.Length);
                var image = Send(Send(objc_getClass("NSImage"), sel_registerName("alloc")), sel_registerName("initWithData:"), data);
                if (image == 0) return;
                var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                Send(app, sel_registerName("setApplicationIconImage:"), image);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { } // cosmetic only
    }
}
#endif
