using System;
using System.Collections.Generic;

namespace GlamourCheck.Services;

// Small immutable DTOs shared by settings, overlays, persistence, and Garland lookup.
// Keep these free of UI or database behavior so they can be serialized and tested easily.

public sealed record GarlandIndexStatus(
    int InstanceCount,
    bool FromCache,
    DateTimeOffset FetchedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record GarlandInstanceIndexMatch(
    uint InstanceId,
    string Name,
    string Category,
    uint? MinLevel,
    uint? MaxLevel);

public sealed record GarlandLootInfo(
    uint InstanceId,
    string InstanceName,
    double? InstancePatch,
    uint? ExpansionItemLevel,
    string? ExpansionPrefix,
    IReadOnlyList<GarlandLootItemInfo> GarlandItems,
    IReadOnlyList<GarlandLootItemInfo> MatchedItems,
    IReadOnlyList<GarlandLootItemInfo> SeededItems);

public sealed record GarlandLootItemInfo(
    uint ItemId,
    string Name,
    GearSlot Slot,
    string ArmorCategory,
    uint? ItemLevel,
    uint IconId);

public sealed record OwnedSeedItemInfo(
    uint ItemId,
    string Name,
    GearSlot Slot,
    string ArmorCategory,
    IReadOnlyList<string> Sources);

public sealed record GarlandSeedItemInfo(
    uint ItemId,
    string Name,
    GearSlot Slot,
    string ArmorCategory,
    uint ItemLevel,
    double? Patch,
    string Prefix);

public sealed record GarlandSeedInstanceCandidate(
    uint InstanceId,
    string Name,
    string Category,
    double? Patch,
    bool RequiresWing,
    string? WingReason);

public sealed record LocalSeededDropSet(
    uint GarlandInstanceId,
    string InstanceName,
    string InstanceCategory,
    double? InstancePatch,
    uint SeedItemId,
    string SeedItemName,
    string SeedPrefix,
    uint ItemLevel,
    double? SeedPatch,
    string? Wing,
    string SlotFilter,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<GarlandLootItemInfo> Items);

public sealed record LocalSeededDropSummary(
    uint GarlandInstanceId,
    string InstanceName,
    string InstanceCategory,
    uint SeedItemId,
    string SeedItemName,
    string SeedPrefix,
    uint ItemLevel,
    double? SeedPatch,
    string? Wing,
    string SlotFilter,
    int ItemCount,
    DateTimeOffset CreatedAtUtc);
