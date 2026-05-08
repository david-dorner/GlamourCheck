using GlamourCheck.Services;

namespace GlamourCheck.Tests;

public sealed class CollectionStateTests
{
    [Fact]
    public void Reload_PopulatesGlobalAndSpecialSourceSets()
    {
        using var testDatabase = TestDatabase.Create();
        using var repository = testDatabase.CreateRepository();
        var state = new CollectionState();

        repository.UpsertCharacter("character-a", "Tester", 74);
        repository.ReplaceSourceSnapshot("character-a", CollectionSource.Inventory, [100], DateTimeOffset.UtcNow);
        repository.ReplaceSourceSnapshot("character-a", CollectionSource.GlamourDresser, [200], DateTimeOffset.UtcNow);
        repository.ReplaceSourceSnapshot("character-a", CollectionSource.Armoire, [300], DateTimeOffset.UtcNow);

        state.Reload("character-a", repository);

        Assert.True(state.IsCollected(100));
        Assert.True(state.IsCollected(200));
        Assert.True(state.IsCollected(300));
        Assert.Contains(200u, state.InGlamourDresser);
        Assert.Contains(300u, state.InArmoire);
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
