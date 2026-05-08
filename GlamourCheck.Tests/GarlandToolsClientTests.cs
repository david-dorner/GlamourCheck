using System.Net;
using GlamourCheck.Services;

namespace GlamourCheck.Tests;

public sealed class GarlandToolsClientTests
{
    [Fact]
    public void BuildEndpointPaths_UsesDocumentedGarlandPaths()
    {
        Assert.Equal("/db/doc/browse/en/2/instance.json", GarlandToolsClient.BuildInstanceIndexPath("en"));
        Assert.Equal("/db/doc/instance/en/2/123.json", GarlandToolsClient.BuildInstancePath("en", 123));
        Assert.Equal("/db/doc/item/en/3/456.json", GarlandToolsClient.BuildItemPath("en", 456));
        Assert.Equal("/api/search.php?text=The%20Lunar%20Subterrane&lang=en", GarlandToolsClient.BuildSearchPath("en", "The Lunar Subterrane"));
    }

    [Fact]
    public async Task SearchAsync_RequestsSearchEndpointWithEncodedQuery()
    {
        using var handler = new CapturingHandler("""[]""");
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://www.garlandtools.org"),
        };
        using var client = new GarlandToolsClient(httpClient);

        var payload = await client.SearchAsync("Mistwake Casting");

        Assert.Equal("""[]""", payload);
        Assert.Equal(new Uri("https://www.garlandtools.org/api/search.php?text=Mistwake%20Casting&lang=en"), handler.LastRequestUri);
    }

    [Fact]
    public async Task GetInstanceIndexAsync_RequestsDocumentedPath()
    {
        using var handler = new CapturingHandler("""{"browse":[]}""");
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://www.garlandtools.org"),
        };
        using var client = new GarlandToolsClient(httpClient);

        var payload = await client.GetInstanceIndexAsync();

        Assert.Equal("""{"browse":[]}""", payload);
        Assert.Equal(new Uri("https://www.garlandtools.org/db/doc/browse/en/2/instance.json"), handler.LastRequestUri);
    }

    [Fact]
    public async Task FindInstanceIndexMatchesAsync_MatchesCurrentContentNameByGarlandIndexName()
    {
        using var testDatabase = TestDatabase.Create();
        using var repository = testDatabase.CreateRepository();
        using var handler = new CapturingHandler("""
            {
              "browse": [
                { "i": 499, "n": "The Lunar Subterrane", "t": "Dungeons", "min_lvl": 90, "max_lvl": 90 },
                { "i": 1, "n": "The Thousand Maws of Toto-Rak", "t": "Dungeons", "min_lvl": 24, "max_lvl": 27 }
              ]
            }
            """);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://www.garlandtools.org"),
        };
        using var client = new GarlandToolsClient(httpClient);
        var service = new GarlandDropLookupService(client, repository, itemIdentityService: null!);

        var result = await service.FindInstanceIndexMatchesAsync("The Lunar Subterrane");

        var match = Assert.Single(result.Matches);
        Assert.False(result.FromCache);
        Assert.Equal(499u, match.InstanceId);
        Assert.Equal("The Lunar Subterrane", match.Name);
        Assert.Equal("Dungeons", match.Category);
        Assert.Equal(90u, match.MinLevel);
        Assert.Equal(90u, match.MaxLevel);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string payload;

        public CapturingHandler(string payload)
        {
            this.payload = payload;
        }

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload),
            });
        }
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory;

        private TestDatabase(string directory)
        {
            this.directory = directory;
        }

        public static TestDatabase Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "GlamourCheck.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return new TestDatabase(directory);
        }

        public CollectionRepository CreateRepository()
        {
            var repository = new CollectionRepository(Path.Combine(directory, "collection.db"));
            repository.Initialize();
            return repository;
        }

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
