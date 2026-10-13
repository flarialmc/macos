using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Flarial.Runtime.Linux.Prefix;
using Flarial.Runtime.Platform;

namespace Flarial.Runtime.Linux;

/// <summary>
/// Refresh-token storage in the login Keychain (generic password, service = resource, account = username), through the Security framework
/// so the token never appears on a command line. Falls back to a 0600 file in the launcher data directory when the Keychain is unavailable.
/// </summary>
public sealed class LinuxCredentialStore : ICredentialStore
{
    const string Security = "/System/Library/Frameworks/Security.framework/Security";
    const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    [DllImport(Security)] static extern int SecKeychainFindGenericPassword(nint keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, out uint passwordLength, out nint passwordData, out nint itemRef);
    [DllImport(Security)] static extern int SecKeychainAddGenericPassword(nint keychain, uint serviceLength, byte[] service, uint accountLength, byte[] account, uint passwordLength, byte[] passwordData, nint itemRef);
    [DllImport(Security)] static extern int SecKeychainItemModifyAttributesAndData(nint itemRef, nint attrList, uint length, byte[] data);
    [DllImport(Security)] static extern int SecKeychainItemDelete(nint itemRef);
    [DllImport(Security)] static extern int SecKeychainItemFreeContent(nint attrList, nint data);
    [DllImport(CF)] static extern void CFRelease(nint obj);

    const int errSecItemNotFound = -25300;

    static string FilePath(string resource, string username) => $"credential.{Convert.ToHexString(Encoding.UTF8.GetBytes(resource + "/" + username))}";

    static unsafe (int Status, string? Value, nint Item) Find(string resource, string username, bool keepItem)
    {
        try
        {
            var s = Encoding.UTF8.GetBytes(resource); var a = Encoding.UTF8.GetBytes(username);
            var status = SecKeychainFindGenericPassword(0, (uint)s.Length, s, (uint)a.Length, a, out var len, out var data, out var item);
            if (status != 0) return (status, null, 0);
            string value;
            try { value = Encoding.UTF8.GetString(new ReadOnlySpan<byte>((void*)data, (int)len)); }
            finally { SecKeychainItemFreeContent(0, data); }
            if (!keepItem) { CFRelease(item); item = 0; }
            return (0, value, item);
        }
        catch { return (-1, null, 0); }
    }

    public string? Get(string resource, string username)
    {
        // the injected client may have rotated the refresh token inside the prefix vault: that copy is the newest
        if (VaultBridge.Get(resource, username) is { Length: > 0 } rotated) return rotated;
        var stored = GetStored(resource, username);
        if (stored is { }) VaultBridge.Set(resource, username, stored); // prefix created after login
        return stored;
    }

    static string? GetStored(string resource, string username)
    {
        if (Find(resource, username, false) is { Status: 0, Value: { Length: > 0 } v }) return v;
        try { return File.ReadAllText(FilePath(resource, username)); }
        catch { return null; }
    }

    public void Set(string resource, string username, string value)
    {
        SetStored(resource, username, value);
        VaultBridge.Set(resource, username, value);
    }

    static void SetStored(string resource, string username, string value)
    {
        var data = Encoding.UTF8.GetBytes(value);
        int status;
        try
        {
            var (found, _, item) = Find(resource, username, true);
            if (found == 0)
            {
                try { status = SecKeychainItemModifyAttributesAndData(item, 0, (uint)data.Length, data); }
                finally { CFRelease(item); }
            }
            else
            {
                var s = Encoding.UTF8.GetBytes(resource); var a = Encoding.UTF8.GetBytes(username);
                status = SecKeychainAddGenericPassword(0, (uint)s.Length, s, (uint)a.Length, a, (uint)data.Length, data, 0);
            }
        }
        catch { status = -1; }
        if (status == 0) { try { File.Delete(FilePath(resource, username)); } catch { } return; }

        var path = FilePath(resource, username);
        File.WriteAllText(path, value);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Remove(string resource, string username)
    {
        VaultBridge.Remove(resource, username);
        try
        {
            if (Find(resource, username, true) is { Status: 0, Item: var item })
                try { SecKeychainItemDelete(item); } finally { CFRelease(item); }
        }
        catch { }
        try { File.Delete(FilePath(resource, username)); } catch { }
    }
}
