using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Flarial.Runtime.Game;
using Flarial.Runtime.Services;

namespace Flarial.Runtime.Core;

public abstract partial class FlarialClient<T> : FlarialClient where T : FlarialClient<T>, new()
{
    public static readonly T _ = new();

    private protected FlarialClient()
    {
        if (_ is null) return;
        throw new InvalidOperationException();
    }

    /// <param name="prepared">Optional verify/download of this client still in flight: the game is launched concurrently and injection waits for it.</param>
    public override bool Launch(Task<bool>? prepared = null)
    {
        if (!IsRunning && Loader.Launch(prepared))
        {
            PostAnalytics();
            return true;
        }
        return false;
    }
}

public abstract partial class FlarialClient
{
    const string ClassName = "Flarial Client";

    private protected abstract string HashName { get; }
    private protected abstract string FileName { get; }
    private protected abstract string HashesUri { get; }

    public abstract bool Launch(Task<bool>? prepared = null);
    private protected abstract Task<bool> VerifyAsync();
    private protected abstract Task<bool> OnDownloadAsync<T>(T progress) where T : IProgress<int>;

    // Clients that only replace their cache after a verified download keep it on failure.
    private protected virtual bool DeleteBeforeDownload => true;

    internal static string? AccessToken
    {
        set => Interlocked.Exchange(ref field, value);
        get => Interlocked.CompareExchange(ref field, null, null);
    }

    private protected FlarialClient() { }

    private protected async Task<string> GetRemoteHashAsync()
    {
        var json = await HttpService.GetJsonAsync<Dictionary<string, string>>(HashesUri);
        return json[HashName];
    }

    public static bool IsRunning => Platform.Platform.Injector.IsClientRunning;

    public async Task<bool> DownloadAsync<T>(T progress) where T : IProgress<int>
    {
        if (await VerifyAsync())
            return true;

        if (DeleteBeforeDownload)
        {
            try { File.Delete(FileName); }
            catch { return false; }
        }

        return await OnDownloadAsync(progress);
    }
}
