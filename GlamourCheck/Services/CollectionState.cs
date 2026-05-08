using System.Collections.Generic;

namespace GlamourCheck.Services;

/// <summary>
/// In-memory read model derived from the current character's SQLite snapshots.
/// Overlay code uses this class for constant-time ownership checks.
/// </summary>
public sealed class CollectionState
{
    private readonly object gate = new();
    private HashSet<uint> allCollected = [];
    private HashSet<uint> inGlamourDresser = [];
    private HashSet<uint> inArmoire = [];
    private Dictionary<uint, string[]> itemSources = [];

    public IReadOnlySet<uint> AllCollected
    {
        get
        {
            lock (gate)
            {
                return new HashSet<uint>(allCollected);
            }
        }
    }

    public IReadOnlySet<uint> InGlamourDresser
    {
        get
        {
            lock (gate)
            {
                return new HashSet<uint>(inGlamourDresser);
            }
        }
    }

    public IReadOnlySet<uint> InArmoire
    {
        get
        {
            lock (gate)
            {
                return new HashSet<uint>(inArmoire);
            }
        }
    }

    public void Reload(string characterKey, ICollectionRepository repository)
    {
        var nextAllCollected = repository.GetCollectedItems(characterKey);
        var nextGlamourDresser = repository.GetCollectedItemsInSource(characterKey, CollectionSource.GlamourDresser);
        var nextArmoire = repository.GetCollectedItemsInSource(characterKey, CollectionSource.Armoire);
        var nextItemSources = repository.GetItemSourceMap(characterKey);

        lock (gate)
        {
            allCollected = nextAllCollected;
            inGlamourDresser = nextGlamourDresser;
            inArmoire = nextArmoire;
            itemSources = nextItemSources;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            allCollected.Clear();
            inGlamourDresser.Clear();
            inArmoire.Clear();
            itemSources.Clear();
        }
    }

    public string[] GetSourcesForItem(uint normalizedItemId)
    {
        lock (gate)
        {
            return itemSources.TryGetValue(normalizedItemId, out var sources) ? sources : [];
        }
    }

    public bool IsCollected(uint normalizedItemId)
    {
        lock (gate)
        {
            return allCollected.Contains(normalizedItemId);
        }
    }

    public bool IsInGlamourDresser(uint normalizedItemId)
    {
        lock (gate)
        {
            return inGlamourDresser.Contains(normalizedItemId);
        }
    }

    public bool IsInArmoire(uint normalizedItemId)
    {
        lock (gate)
        {
            return inArmoire.Contains(normalizedItemId);
        }
    }
}
