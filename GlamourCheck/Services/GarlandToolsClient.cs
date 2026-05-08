using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace GlamourCheck.Services;

/// <summary>
/// Typed HTTP wrapper for the Garland Tools document endpoints used by GlamourCheck.
/// It only returns raw JSON; parsing, caching, and fallback rules live in GarlandDropLookupService.
/// </summary>
public sealed class GarlandToolsClient : IDisposable
{
    public const string DefaultLanguage = "en";

    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;

    public GarlandToolsClient()
        : this(new HttpClient
        {
            BaseAddress = new Uri("https://www.garlandtools.org"),
            Timeout = TimeSpan.FromSeconds(15),
        }, ownsHttpClient: true)
    {
    }

    public GarlandToolsClient(HttpClient httpClient, bool ownsHttpClient = false)
    {
        this.httpClient = httpClient;
        this.ownsHttpClient = ownsHttpClient;
    }

    public Task<string> GetInstanceIndexAsync(CancellationToken cancellationToken = default)
    {
        return GetStringAsync(BuildInstanceIndexPath(DefaultLanguage), cancellationToken);
    }

    public Task<string> GetInstanceAsync(uint garlandInstanceId, CancellationToken cancellationToken = default)
    {
        return GetStringAsync(BuildInstancePath(DefaultLanguage, garlandInstanceId), cancellationToken);
    }

    public Task<string> GetItemAsync(uint itemId, CancellationToken cancellationToken = default)
    {
        return GetStringAsync(BuildItemPath(DefaultLanguage, itemId), cancellationToken);
    }

    public Task<string> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        return GetStringAsync(BuildSearchPath(DefaultLanguage, query), cancellationToken);
    }

    public static string BuildInstanceIndexPath(string language)
    {
        return $"/db/doc/browse/{language}/2/instance.json";
    }

    public static string BuildInstancePath(string language, uint garlandInstanceId)
    {
        return $"/db/doc/instance/{language}/2/{garlandInstanceId}.json";
    }

    public static string BuildItemPath(string language, uint itemId)
    {
        return $"/db/doc/item/{language}/3/{itemId}.json";
    }

    public static string BuildSearchPath(string language, string query)
    {
        return $"/api/search.php?text={Uri.EscapeDataString(query)}&lang={language}";
    }

    public void Dispose()
    {
        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }
    }

    private async Task<string> GetStringAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
