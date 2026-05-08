using GlamourCheck.Services;

namespace GlamourCheck.Tests;

public sealed class CollectionRepositoryTests
{
    [Fact]
    public void ReplaceSourceSnapshot_DeduplicatesItemsAndSupportsLookup()
    {
        using var testDatabase = TestDatabase.Create();
        using var repository = testDatabase.CreateRepository();

        repository.UpsertCharacter("character-a", "Tester", 74);
        repository.ReplaceSourceSnapshot(
            "character-a",
            CollectionSource.Inventory,
            [100, 100, 200, 0],
            DateTimeOffset.Parse("2026-05-02T12:00:00Z"));

        Assert.True(repository.IsCollected("character-a", 100));
        Assert.True(repository.IsCollected("character-a", 200));
        Assert.True(repository.IsCollectedInSource("character-a", CollectionSource.Inventory, 100));
        Assert.False(repository.IsCollected("character-a", 300));
        Assert.Equal([100u, 200u], repository.GetCollectedItems("character-a").Order().ToArray());

        var inventory = repository.GetSourceSnapshots("character-a")
            .Single(snapshot => snapshot.SourceKey == CollectionSource.Inventory);
        Assert.Equal(2, inventory.ItemCount);
        Assert.Null(inventory.StaleReason);
    }

    [Fact]
    public void ReplaceSourceSnapshot_RemovesItemsMissingFromLatestSnapshot()
    {
        using var testDatabase = TestDatabase.Create();
        using var repository = testDatabase.CreateRepository();

        repository.UpsertCharacter("character-a", "Tester", 74);
        repository.ReplaceSourceSnapshot("character-a", CollectionSource.Inventory, [100, 200], DateTimeOffset.UtcNow);
        repository.ReplaceSourceSnapshot("character-a", CollectionSource.Inventory, [200, 300], DateTimeOffset.UtcNow);

        Assert.False(repository.IsCollectedInSource("character-a", CollectionSource.Inventory, 100));
        Assert.True(repository.IsCollected("character-a", 200));
        Assert.True(repository.IsCollected("character-a", 300));
        Assert.Equal([200u, 300u], repository.GetCollectedItems("character-a").Order().ToArray());
    }

    [Fact]
    public void IsCollected_RemainsTrueWhileItemExistsInAnySource()
    {
        using var testDatabase = TestDatabase.Create();
        using var repository = testDatabase.CreateRepository();

        repository.UpsertCharacter("character-a", "Tester", 74);
        repository.ReplaceSourceSnapshot("character-a", CollectionSource.Inventory, [100], DateTimeOffset.UtcNow);
        repository.ReplaceSourceSnapshot("character-a", CollectionSource.GlamourDresser, [100], DateTimeOffset.UtcNow);
        repository.ReplaceSourceSnapshot("character-a", CollectionSource.Inventory, [], DateTimeOffset.UtcNow);

        Assert.True(repository.IsCollected("character-a", 100));
        Assert.False(repository.IsCollectedInSource("character-a", CollectionSource.Inventory, 100));
        Assert.True(repository.IsCollectedInSource("character-a", CollectionSource.GlamourDresser, 100));
    }

    [Fact]
    public void GetSourceSnapshots_ReturnsSeededSourcesAndStaleReason()
    {
        using var testDatabase = TestDatabase.Create();
        using var repository = testDatabase.CreateRepository();

        repository.UpsertCharacter("character-a", "Tester", 74);
        repository.ReplaceSourceSnapshot(
            "character-a",
            CollectionSource.ChocoboSaddlebag,
            [400],
            DateTimeOffset.Parse("2026-05-02T12:00:00Z"),
            "sync_failed");

        var snapshots = repository.GetSourceSnapshots("character-a");

        Assert.Contains(snapshots, snapshot => snapshot.SourceKey == CollectionSource.Inventory);
        var saddlebag = snapshots.Single(snapshot => snapshot.SourceKey == CollectionSource.ChocoboSaddlebag);
        Assert.True(saddlebag.IsServerSide);
        Assert.Equal(1, saddlebag.ItemCount);
        Assert.Equal("sync_failed", saddlebag.StaleReason);
    }

    [Fact]
    public void ReplaceSourceSnapshot_StoresDisplayNameForDynamicSources()
    {
        using var testDatabase = TestDatabase.Create();
        using var repository = testDatabase.CreateRepository();

        repository.UpsertCharacter("character-a", "Tester", 74);
        repository.ReplaceSourceSnapshot(
            "character-a",
            CollectionSource.Retainer("0078002E92C25D25"),
            [500],
            DateTimeOffset.UtcNow,
            displayName: "Retainer: Testainer");

        var retainer = repository.GetSourceSnapshots("character-a")
            .Single(snapshot => snapshot.SourceKey == "retainer:0078002E92C25D25");

        Assert.Equal("Retainer: Testainer", retainer.DisplayName);
        Assert.True(retainer.IsServerSide);
    }

    [Fact]
    public void GetValidRemoteCacheEntry_ReturnsNullForMissingCache()
    {
        using var testDatabase = TestDatabase.Create();
        using var repository = testDatabase.CreateRepository();

        var cached = repository.GetValidRemoteCacheEntry("missing", DateTimeOffset.Parse("2026-05-02T12:00:00Z"));

        Assert.Null(cached);
    }

    [Fact]
    public void GetValidRemoteCacheEntry_ReturnsPayloadBeforeExpiry()
    {
        using var testDatabase = TestDatabase.Create();
        using var repository = testDatabase.CreateRepository();
        var fetchedAt = DateTimeOffset.Parse("2026-05-02T12:00:00Z");
        var expiresAt = DateTimeOffset.Parse("2026-05-09T12:00:00Z");

        repository.UpsertRemoteCacheEntry("cache-key", "/url", """{"ok":true}""", fetchedAt, expiresAt);

        var cached = repository.GetValidRemoteCacheEntry("cache-key", DateTimeOffset.Parse("2026-05-03T12:00:00Z"));

        Assert.NotNull(cached);
        Assert.Equal("cache-key", cached.CacheKey);
        Assert.Equal("/url", cached.Url);
        Assert.Equal("""{"ok":true}""", cached.PayloadJson);
        Assert.Equal(fetchedAt, cached.FetchedAtUtc);
        Assert.Equal(expiresAt, cached.ExpiresAtUtc);
    }

    [Fact]
    public void GetValidRemoteCacheEntry_IgnoresExpiredPayload()
    {
        using var testDatabase = TestDatabase.Create();
        using var repository = testDatabase.CreateRepository();

        repository.UpsertRemoteCacheEntry(
            "cache-key",
            "/url",
            "{}",
            DateTimeOffset.Parse("2026-05-02T12:00:00Z"),
            DateTimeOffset.Parse("2026-05-03T12:00:00Z"));

        var cached = repository.GetValidRemoteCacheEntry("cache-key", DateTimeOffset.Parse("2026-05-04T12:00:00Z"));

        Assert.Null(cached);
    }

    [Fact]
    public void UpsertRemoteCacheEntry_ReplacesExistingPayload()
    {
        using var testDatabase = TestDatabase.Create();
        using var repository = testDatabase.CreateRepository();

        repository.UpsertRemoteCacheEntry(
            "cache-key",
            "/old",
            """{"old":true}""",
            DateTimeOffset.Parse("2026-05-02T12:00:00Z"),
            DateTimeOffset.Parse("2026-05-03T12:00:00Z"));
        repository.UpsertRemoteCacheEntry(
            "cache-key",
            "/new",
            """{"new":true}""",
            DateTimeOffset.Parse("2026-05-04T12:00:00Z"),
            DateTimeOffset.Parse("2026-05-05T12:00:00Z"));

        var cached = repository.GetValidRemoteCacheEntry("cache-key", DateTimeOffset.Parse("2026-05-04T13:00:00Z"));

        Assert.NotNull(cached);
        Assert.Equal("/new", cached.Url);
        Assert.Equal("""{"new":true}""", cached.PayloadJson);
    }

    [Fact]
    public void ClearRemoteCache_RemovesCacheRows()
    {
        using var testDatabase = TestDatabase.Create();
        using var repository = testDatabase.CreateRepository();

        repository.UpsertRemoteCacheEntry(
            "cache-key",
            "/url",
            "{}",
            DateTimeOffset.Parse("2026-05-02T12:00:00Z"),
            DateTimeOffset.Parse("2026-05-03T12:00:00Z"));

        var deletedRows = repository.ClearRemoteCache();
        var cached = repository.GetValidRemoteCacheEntry("cache-key", DateTimeOffset.Parse("2026-05-02T13:00:00Z"));

        Assert.Equal(1, deletedRows);
        Assert.Null(cached);
    }

    [Fact]
    public void InstanceDropCache_ReturnsPayloadBeforeExpiry()
    {
        using var testDatabase = TestDatabase.Create();
        using var repository = testDatabase.CreateRepository();
        var fetchedAt = DateTimeOffset.Parse("2026-05-02T12:00:00Z");
        var expiresAt = DateTimeOffset.Parse("2026-05-03T12:00:00Z");

        repository.UpsertInstanceDropCacheEntry(1064, 103, """{"itemIds":[1,2]}""", fetchedAt, expiresAt);

        var cached = repository.GetValidInstanceDropCacheEntry(1064, DateTimeOffset.Parse("2026-05-02T13:00:00Z"));

        Assert.NotNull(cached);
        Assert.Equal(1064u, cached.ContentFinderConditionId);
        Assert.Equal(103u, cached.GarlandInstanceId);
        Assert.Equal("""{"itemIds":[1,2]}""", cached.PayloadJson);
        Assert.Equal(fetchedAt, cached.FetchedAtUtc);
        Assert.Equal(expiresAt, cached.ExpiresAtUtc);
    }

    [Fact]
    public void InstanceDropCache_ReturnsAllValidPayloadsBeforeExpiry()
    {
        using var testDatabase = TestDatabase.Create();
        using var repository = testDatabase.CreateRepository();

        repository.UpsertInstanceDropCacheEntry(
            1064,
            103,
            """{"itemIds":[1]}""",
            DateTimeOffset.Parse("2026-05-02T12:00:00Z"),
            DateTimeOffset.Parse("2026-05-04T12:00:00Z"));
        repository.UpsertInstanceDropCacheEntry(
            1065,
            104,
            """{"itemIds":[2]}""",
            DateTimeOffset.Parse("2026-05-02T12:00:00Z"),
            DateTimeOffset.Parse("2026-05-03T12:00:00Z"));

        var cached = repository.GetValidInstanceDropCacheEntries(DateTimeOffset.Parse("2026-05-03T13:00:00Z"));

        var entry = Assert.Single(cached);
        Assert.Equal(1064u, entry.ContentFinderConditionId);
    }

    [Fact]
    public void InstanceDropCache_IgnoresExpiredPayload()
    {
        using var testDatabase = TestDatabase.Create();
        using var repository = testDatabase.CreateRepository();

        repository.UpsertInstanceDropCacheEntry(
            1064,
            103,
            """{"itemIds":[1,2]}""",
            DateTimeOffset.Parse("2026-05-02T12:00:00Z"),
            DateTimeOffset.Parse("2026-05-03T12:00:00Z"));

        var cached = repository.GetValidInstanceDropCacheEntry(1064, DateTimeOffset.Parse("2026-05-04T12:00:00Z"));

        Assert.Null(cached);
    }

    [Fact]
    public void InstanceDropCache_UpsertReplacesExistingPayload()
    {
        using var testDatabase = TestDatabase.Create();
        using var repository = testDatabase.CreateRepository();

        repository.UpsertInstanceDropCacheEntry(
            1064,
            103,
            """{"itemIds":[1]}""",
            DateTimeOffset.Parse("2026-05-02T12:00:00Z"),
            DateTimeOffset.Parse("2026-05-03T12:00:00Z"));
        repository.UpsertInstanceDropCacheEntry(
            1064,
            104,
            """{"itemIds":[2]}""",
            DateTimeOffset.Parse("2026-05-04T12:00:00Z"),
            DateTimeOffset.Parse("2026-05-05T12:00:00Z"));

        var cached = repository.GetValidInstanceDropCacheEntry(1064, DateTimeOffset.Parse("2026-05-04T13:00:00Z"));

        Assert.NotNull(cached);
        Assert.Equal(104u, cached.GarlandInstanceId);
        Assert.Equal("""{"itemIds":[2]}""", cached.PayloadJson);
    }

    [Fact]
    public void LocalSeededDrops_CanBeUpsertedReadAndDeleted()
    {
        using var testDatabase = TestDatabase.Create();
        using var repository = testDatabase.CreateRepository();
        var createdAt = DateTimeOffset.Parse("2026-05-03T12:00:00Z");

        repository.UpsertLocalSeededDrops(new LocalSeededDropSet(
            30146,
            "AAC Cruiserweight M1 (Savage)",
            "Raids",
            7.2,
            46716,
            "Babyface Champion's Earring Coffer (IL 760)",
            "Babyface",
            760,
            7.2,
            "1",
            "Ears,Neck,Wrists,Ring",
            createdAt,
            [
                new GarlandLootItemInfo(47000, "Babyface Champion's Earrings of Fending", GearSlot.Ears, "Fending", 760, 55547),
                new GarlandLootItemInfo(47001, "Babyface Champion's Ring of Fending", GearSlot.Ring, "Fending", 760, 54748),
            ]));

        var seed = repository.GetLocalSeededDrops(30146);
        var summaries = repository.GetLocalSeededDropSummaries();

        Assert.NotNull(seed);
        Assert.Equal("AAC Cruiserweight M1 (Savage)", seed.InstanceName);
        Assert.Equal("1", seed.Wing);
        Assert.Equal(2, seed.Items.Count);
        var summary = Assert.Single(summaries);
        Assert.Equal(30146u, summary.GarlandInstanceId);
        Assert.Equal(2, summary.ItemCount);

        Assert.True(repository.DeleteLocalSeededDrops(30146));
        Assert.Null(repository.GetLocalSeededDrops(30146));
        Assert.Empty(repository.GetLocalSeededDropSummaries());
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
