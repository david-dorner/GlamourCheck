using System;
using System.Collections.Generic;

namespace GlamourCheck.Services;

/// <summary>
/// Repository boundary for all durable plugin data.
/// Services use this interface so rendering and sync code never open ad hoc SQLite connections.
/// </summary>
public interface ICollectionRepository : IDisposable
{
    void Initialize();
    void UpsertCharacter(string characterKey, string? displayName, uint? worldId);
    void ReplaceSourceSnapshot(string characterKey, string sourceKey, IEnumerable<uint> normalizedItemIds, DateTimeOffset syncedAtUtc, string? staleReason = null, string? displayName = null);
    bool IsCollected(string characterKey, uint normalizedItemId);
    bool IsCollectedInSource(string characterKey, string sourceKey, uint normalizedItemId);
    HashSet<uint> GetCollectedItems(string characterKey);
    HashSet<uint> GetCollectedItemsInSource(string characterKey, string sourceKey);
    Dictionary<uint, string[]> GetItemSourceMap(string characterKey);
    IReadOnlyList<SourceSnapshotInfo> GetSourceSnapshots(string characterKey);
    void UpsertLocalSeededDrops(LocalSeededDropSet seed);
    LocalSeededDropSet? GetLocalSeededDrops(uint garlandInstanceId);
    IReadOnlyList<LocalSeededDropSummary> GetLocalSeededDropSummaries();
    bool DeleteLocalSeededDrops(uint garlandInstanceId);
    InstanceDropCacheEntry? GetValidInstanceDropCacheEntry(uint contentFinderConditionId, DateTimeOffset nowUtc);
    IReadOnlyList<InstanceDropCacheEntry> GetValidInstanceDropCacheEntries(DateTimeOffset nowUtc);
    void UpsertInstanceDropCacheEntry(uint contentFinderConditionId, uint? garlandInstanceId, string payloadJson, DateTimeOffset fetchedAtUtc, DateTimeOffset expiresAtUtc);
    int DeleteInstanceDropCacheEntriesForGarlandInstance(uint garlandInstanceId);
    RemoteCacheEntry? GetValidRemoteCacheEntry(string cacheKey, DateTimeOffset nowUtc);
    void UpsertRemoteCacheEntry(string cacheKey, string url, string payloadJson, DateTimeOffset fetchedAtUtc, DateTimeOffset expiresAtUtc);
    int ClearRemoteCache();
}
