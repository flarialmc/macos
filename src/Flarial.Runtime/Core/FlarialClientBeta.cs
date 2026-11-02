using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Flarial.Runtime.Game;
using Flarial.Runtime.Services;

namespace Flarial.Runtime.Core;

public sealed class FlarialClientBeta : FlarialClient<FlarialClientBeta>
{
    const string DownloadUri = "https://api.flarial.xyz/api/v2/beta/dll";

    private protected override string HashName => "commitHash";
    private protected override string FileName => "Flarial.Client.Beta.dll";
    private protected override string HashesUri => "https://api.flarial.xyz/api/v2/beta/dll/hash";

    private protected override async Task<bool> VerifyAsync()
    {
        /*
            - Inspect the client's commit hash via an exported symbol.
            - Somewhat "efficient" over an actual hash when updating.
            - The export table is read from the PE image instead of calling GetProcAddress on a loaded module.
        */

        return new ModificationLibrary(FileName).HasExport($"_{await GetRemoteHashAsync()}_");
    }

    private protected override async Task<bool> OnDownloadAsync<T>(T progress)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, DownloadUri);
        request.Headers.Authorization = new("Bearer", AccessToken);

        using var response = await HttpService.SendAsync(request);
        if (!response.IsSuccessStatusCode) return false;

        await response.DownloadAsync(FileName, progress);
        return true;
    }
}