using System;
using System.IO;
using System.Text.Json.Nodes;

namespace Flarial.Runtime.Linux.Prefix;

/// <summary>
/// The xgameruntime shim (Native/shims/xgameruntime, extra_xuser.c) does the in-game Xbox Live sign-in itself and keeps its state in
/// %APPDATA%\xgr\auth.json: client_id, device_id/device_key (its own proof key), msa_refresh_token, xuid, gamertag. It uses the same MSA
/// client (0000000048183522) as the launcher's Xbox device-code login, so the launcher hands it its refresh token and the game signs in
/// silently. Either side may rotate the token: the newer copy wins in both directions. Never logs tokens.
/// </summary>
static class XgrAuthBridge
{
    const string ClientId = "0000000048183522";

    static string AuthFile => Path.Combine(Paths.Prefix, "drive_c", "users", Paths.WineUser, "AppData", "Roaming", "xgr", "auth.json");

    /// <summary>Before the launcher refreshes: adopt a token the game rotated after the launcher last saved its own.</summary>
    public static void PullFromGame()
    {
        try
        {
            if (!File.Exists(AuthFile)) return;
            var o = Json.ReadObject(AuthFile);
            if (o["client_id"]?.GetValue<string>() != ClientId || o["msa_refresh_token"]?.GetValue<string>() is not { Length: > 0 } rt) return;
            if (File.Exists(Paths.MsaToken) && File.GetLastWriteTimeUtc(Paths.MsaToken) >= File.GetLastWriteTimeUtc(AuthFile)) return;
            if (Json.Field(Paths.MsaToken, "refresh_token") == rt) return;
            Json.WriteSecret(Paths.MsaToken, new JsonObject { ["refresh_token"] = rt, ["obtained"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }.ToJsonString(new() { WriteIndented = true }) + "\n");
        }
        catch { }
    }

    /// <summary>After the refresh: give the game the current token (null = signed out of Xbox in the launcher: leave the game's own state alone).</summary>
    public static void PushToGame(string? refreshToken)
    {
        if (string.IsNullOrEmpty(refreshToken)) return;
        try
        {
            var o = File.Exists(AuthFile) ? Json.ReadObject(AuthFile) : new JsonObject();
            if (o["client_id"]?.GetValue<string>() == ClientId && o["msa_refresh_token"]?.GetValue<string>() == refreshToken) return;
            o["version"] = 1;
            o["client_id"] = ClientId;
            o["msa_refresh_token"] = refreshToken;
            o["device_id"] ??= "";
            o["device_key"] ??= ""; // empty: the shim generates its own proof key on first use and saves it here
            Json.WriteSecret(AuthFile, o.ToJsonString(new() { WriteIndented = true }) + "\n");
        }
        catch { }
    }
}
