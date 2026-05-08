using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GlamourCheck.Services;

/// <summary>
/// Converts Garland payloads into the loot models used by overlays and settings.
/// This service owns remote payload caching, Garland-only loot expansion, local seed persistence,
/// and outfit membership probes so UI code can stay small and non-blocking.
/// </summary>
public sealed class GarlandDropLookupService
{
    private const uint GarlandOutfitCategory = 112;
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromDays(7);
    private static readonly Regex GearCofferNamePattern = new(
        @"^(?<prefix>.+?) (?<slot>Head|Hand|Leg|Chest|Foot|Earring|Necklace|Bracelet|Ring|Weapon)(?: Gear)? Coffer \(IL (?<itemLevel>\d+)\)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex HoloRewardNamePattern = new(
        @"^(?<prefix>.+?) Holo(?<token>armor|blade|chausses|earrings?|gauntlets?|greaves|helm)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly GarlandToolsClient garlandToolsClient;
    private readonly ICollectionRepository repository;
    private readonly ItemIdentityService itemIdentityService;
    private readonly object memoryCacheLock = new();
    private readonly Dictionary<string, GarlandPayloadResult> memoryCache = [];

    public GarlandDropLookupService(
        GarlandToolsClient garlandToolsClient,
        ICollectionRepository repository,
        ItemIdentityService itemIdentityService)
    {
        this.garlandToolsClient = garlandToolsClient;
        this.repository = repository;
        this.itemIdentityService = itemIdentityService;
    }

    public int ClearCache()
    {
        lock (memoryCacheLock)
        {
            memoryCache.Clear();
        }

        return repository.ClearRemoteCache();
    }

    public async Task<GarlandIndexStatus> GetInstanceIndexStatusAsync(CancellationToken cancellationToken = default)
    {
        var payload = await GetOrFetchAsync(
            "garland:browse:en:instance",
            GarlandToolsClient.BuildInstanceIndexPath(GarlandToolsClient.DefaultLanguage),
            garlandToolsClient.GetInstanceIndexAsync,
            cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(payload.PayloadJson);
        var instanceCount = document.RootElement.TryGetProperty("browse", out var browse) && browse.ValueKind == JsonValueKind.Array
            ? browse.GetArrayLength()
            : 0;

        return new GarlandIndexStatus(
            instanceCount,
            payload.FromCache,
            payload.FetchedAtUtc,
            payload.ExpiresAtUtc);
    }

    public async Task<GarlandLootInfo> GetLootInfoAsync(uint garlandInstanceId, CancellationToken cancellationToken = default)
    {
        var payload = await GetOrFetchAsync(
            $"garland:instance:en:{garlandInstanceId}",
            GarlandToolsClient.BuildInstancePath(GarlandToolsClient.DefaultLanguage, garlandInstanceId),
            token => garlandToolsClient.GetInstanceAsync(garlandInstanceId, token),
            cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(payload.PayloadJson);
        var instance = document.RootElement.TryGetProperty("instance", out var instanceElement)
            ? instanceElement
            : document.RootElement;

        var instanceName = GetStringProperty(instance, "name") ?? $"Garland instance {garlandInstanceId}";
        double? instancePatch = TryGetDoubleProperty(instance, "patch", out var patch) ? patch : null;
        var itemLevels = ExtractPartialItemLevels(document.RootElement);
        var garlandItemIds = ExtractGarlandLootItemIds(instance);
        var garlandItems = BuildLootItems(garlandItemIds, itemLevels);
        var cofferDescriptors = ExtractGearCofferDescriptors(instance, document.RootElement);
        var normalRaidTokenItems = cofferDescriptors.Count == 0 && IsRegularEightPlayerRaid(instance)
            ? await ExpandNormalRaidTokenRewardsAsync(instance, cancellationToken).ConfigureAwait(false)
            : [];
        var cofferExpandedItems = await ExpandCofferRewardsAsync(
            cofferDescriptors,
            cancellationToken).ConfigureAwait(false);
        var holoExpandedItems = await ExpandHoloRewardsAsync(
            instance,
            document.RootElement,
            instancePatch,
            cancellationToken).ConfigureAwait(false);

        var expansionSeed = garlandItems.FirstOrDefault(item => item.ItemLevel is not null);
        var expansionPrefix = expansionSeed is null ? null : GetFirstWord(expansionSeed.Name);
        var expansionItemLevel = expansionSeed?.ItemLevel;
        var knownItemIdSet = garlandItems
            .Concat(normalRaidTokenItems)
            .Concat(cofferExpandedItems)
            .Concat(holoExpandedItems)
            .Select(item => item.ItemId)
            .ToHashSet();
        var slotRestrictedExpansion = ShouldRestrictMatchedExpansionToKnownSlots(instance, garlandItems);
        var expansionSlots = slotRestrictedExpansion
            ? garlandItems.Select(item => item.Slot).Where(slot => slot != GearSlot.Unknown).ToHashSet()
            : [];
        var prefixMatchedItems = expansionPrefix is null || expansionItemLevel is null
            ? []
            : (await SearchGarlandGearByPrefixAndItemLevelAsync(
                    expansionPrefix,
                    expansionItemLevel.Value,
                    cancellationToken).ConfigureAwait(false))
                .Where(item => !knownItemIdSet.Contains(item.ItemId))
                .Where(item => !slotRestrictedExpansion || expansionSlots.Contains(item.Slot))
                .OrderBy(item => GetLootCategory(item), StringComparer.Ordinal)
                .ThenBy(item => item.Slot)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ToArray();
        var matchedItems = normalRaidTokenItems
            .Concat(cofferExpandedItems)
            .Concat(holoExpandedItems)
            .Concat(prefixMatchedItems)
            .DistinctBy(item => item.ItemId)
            .OrderBy(item => GetLootCategory(item), StringComparer.Ordinal)
            .ThenBy(item => item.Slot)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
        var localSeed = repository.GetLocalSeededDrops(garlandInstanceId);
        var seededItems = localSeed?.Items ?? [];

        return new GarlandLootInfo(
            garlandInstanceId,
            instanceName,
            instancePatch,
            expansionItemLevel,
            expansionPrefix,
            garlandItems,
            matchedItems,
            seededItems);
    }

    public IReadOnlyList<LocalSeededDropSummary> GetLocalSeededDropSummaries()
    {
        return repository.GetLocalSeededDropSummaries();
    }

    public bool DeleteLocalSeededDrops(uint garlandInstanceId)
    {
        return repository.DeleteLocalSeededDrops(garlandInstanceId);
    }

    public async Task<bool> IsOutfitPieceAsync(uint itemId, CancellationToken cancellationToken = default)
    {
        if (!itemIdentityService.TryGetGearItemInfo(itemId, out var gearInfo))
        {
            return false;
        }

        var itemLevel = itemIdentityService.TryGetItemLevel(gearInfo.ItemId);
        if (itemLevel is null or 0)
        {
            return false;
        }

        var prefix = GetFirstWord(gearInfo.Name);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return false;
        }

        var payload = await GetOrFetchAsync(
            $"garland:search:en:{prefix.ToUpperInvariant()}",
            GarlandToolsClient.BuildSearchPath(GarlandToolsClient.DefaultLanguage, prefix),
            token => garlandToolsClient.SearchAsync(prefix, token),
            cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(payload.PayloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var result in document.RootElement.EnumerateArray())
        {
            if (!result.TryGetProperty("type", out var type) ||
                !type.ValueEquals("item") ||
                !result.TryGetProperty("obj", out var obj) ||
                !TryGetUIntProperty(obj, "i", out var candidateItemId) ||
                !TryGetUIntProperty(obj, "l", out var candidateItemLevel) ||
                candidateItemLevel != itemLevel.Value)
            {
                continue;
            }

            var name = GetStringProperty(obj, "n");
            if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryGetUIntProperty(obj, "t", out var searchCategory) && searchCategory == GarlandOutfitCategory)
            {
                return true;
            }

            if (await IsGarlandOutfitItemAsync(candidateItemId, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<GarlandSeedItemInfo> GetSeedItemInfoAsync(
        uint seedItemId,
        CancellationToken cancellationToken = default)
    {
        var itemPayload = await GetOrFetchAsync(
            $"garland:item:en:{seedItemId}",
            GarlandToolsClient.BuildItemPath(GarlandToolsClient.DefaultLanguage, seedItemId),
            token => garlandToolsClient.GetItemAsync(seedItemId, token),
            cancellationToken).ConfigureAwait(false);

        using var itemDocument = JsonDocument.Parse(itemPayload.PayloadJson);
        var item = itemDocument.RootElement.TryGetProperty("item", out var itemElement)
            ? itemElement
            : itemDocument.RootElement;

        if (!TryGetUIntProperty(item, "id", out var garlandItemId))
        {
            garlandItemId = seedItemId;
        }

        if (!itemIdentityService.TryGetGearItemInfo(garlandItemId, out var gearInfo))
        {
            throw new InvalidOperationException($"Seed item {seedItemId} is not recognized as collectible gear.");
        }

        var seedItemName = GetStringProperty(item, "name") ?? gearInfo.Name;
        var seedItemLevel = TryGetUIntProperty(item, "ilvl", out var itemLevel)
            ? itemLevel
            : itemIdentityService.TryGetItemLevel(gearInfo.ItemId) ?? 0;
        if (seedItemLevel == 0)
        {
            throw new InvalidOperationException($"Seed item {seedItemName} has no item level.");
        }

        return new GarlandSeedItemInfo(
            gearInfo.ItemId,
            seedItemName,
            gearInfo.Slot,
            gearInfo.ArmorCategory,
            seedItemLevel,
            TryGetDoubleProperty(item, "patch", out var itemPatch) ? itemPatch : null,
            GetFirstWord(seedItemName));
    }

    public async Task<IReadOnlyList<GarlandSeedInstanceCandidate>> GetSeedInstanceCandidatesAsync(
        double? seedPatch,
        CancellationToken cancellationToken = default)
    {
        var payload = await GetOrFetchAsync(
            "garland:browse:en:instance",
            GarlandToolsClient.BuildInstanceIndexPath(GarlandToolsClient.DefaultLanguage),
            garlandToolsClient.GetInstanceIndexAsync,
            cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(payload.PayloadJson);
        if (!document.RootElement.TryGetProperty("browse", out var browse) || browse.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var indexRows = browse
            .EnumerateArray()
            .Select(entry => TryGetUIntProperty(entry, "i", out var instanceId)
                ? new
                {
                    InstanceId = instanceId,
                    Name = GetStringProperty(entry, "n") ?? $"Garland instance {instanceId}",
                    Category = GetStringProperty(entry, "t") ?? "unknown category",
                }
                : null)
            .Where(row => row is not null)
            .ToArray();

        var semaphore = new SemaphoreSlim(12);
        var tasks = indexRows.Select(async row =>
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var instancePayload = await GetOrFetchAsync(
                    $"garland:instance:en:{row!.InstanceId}",
                    GarlandToolsClient.BuildInstancePath(GarlandToolsClient.DefaultLanguage, row.InstanceId),
                    token => garlandToolsClient.GetInstanceAsync(row.InstanceId, token),
                    cancellationToken).ConfigureAwait(false);

                using var instanceDocument = JsonDocument.Parse(instancePayload.PayloadJson);
                var instance = instanceDocument.RootElement.TryGetProperty("instance", out var instanceElement)
                    ? instanceElement
                    : instanceDocument.RootElement;
                double? instancePatch = TryGetDoubleProperty(instance, "patch", out var parsedPatch) ? parsedPatch : null;
                if (seedPatch is not null &&
                    (instancePatch is null || Math.Abs(seedPatch.Value - instancePatch.Value) > 0.001))
                {
                    return null;
                }

                var name = GetStringProperty(instance, "name") ?? row.Name;
                var category = GetStringProperty(instance, "category") ?? row.Category;
                var requiresWing = RequiresRaidWing(category, instance, out var wingReason);
                return new GarlandSeedInstanceCandidate(
                    row.InstanceId,
                    name,
                    category,
                    instancePatch,
                    requiresWing,
                    requiresWing ? wingReason : null);
            }
            catch
            {
                return null;
            }
            finally
            {
                semaphore.Release();
            }
        });

        var candidates = await Task.WhenAll(tasks).ConfigureAwait(false);
        return candidates
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .OrderBy(candidate => candidate.Category, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<LocalSeededDropSet> CreateLocalSeededDropsAsync(
        uint seedItemId,
        uint garlandInstanceId,
        string? wing,
        CancellationToken cancellationToken = default)
    {
        var itemPayload = await GetOrFetchAsync(
            $"garland:item:en:{seedItemId}",
            GarlandToolsClient.BuildItemPath(GarlandToolsClient.DefaultLanguage, seedItemId),
            token => garlandToolsClient.GetItemAsync(seedItemId, token),
            cancellationToken).ConfigureAwait(false);
        var instancePayload = await GetOrFetchAsync(
            $"garland:instance:en:{garlandInstanceId}",
            GarlandToolsClient.BuildInstancePath(GarlandToolsClient.DefaultLanguage, garlandInstanceId),
            token => garlandToolsClient.GetInstanceAsync(garlandInstanceId, token),
            cancellationToken).ConfigureAwait(false);

        using var itemDocument = JsonDocument.Parse(itemPayload.PayloadJson);
        using var instanceDocument = JsonDocument.Parse(instancePayload.PayloadJson);
        var item = itemDocument.RootElement.TryGetProperty("item", out var itemElement)
            ? itemElement
            : itemDocument.RootElement;
        var instance = instanceDocument.RootElement.TryGetProperty("instance", out var instanceElement)
            ? instanceElement
            : instanceDocument.RootElement;

        if (!TryGetUIntProperty(item, "id", out var garlandItemId))
        {
            garlandItemId = seedItemId;
        }

        if (!itemIdentityService.TryGetGearItemInfo(garlandItemId, out var seedGearInfo))
        {
            throw new InvalidOperationException($"Seed item {seedItemId} is not recognized as collectible gear.");
        }

        var seedItemName = GetStringProperty(item, "name") ?? seedGearInfo.Name;
        var seedPrefix = GetFirstWord(seedItemName);
        var seedItemLevel = TryGetUIntProperty(item, "ilvl", out var itemLevel)
            ? itemLevel
            : itemIdentityService.TryGetItemLevel(seedGearInfo.ItemId) ?? 0;
        if (seedItemLevel == 0)
        {
            throw new InvalidOperationException($"Seed item {seedItemName} has no item level.");
        }

        double? seedPatch = TryGetDoubleProperty(item, "patch", out var itemPatch) ? itemPatch : null;
        double? instancePatch = TryGetDoubleProperty(instance, "patch", out var parsedInstancePatch) ? parsedInstancePatch : null;
        var instanceName = GetStringProperty(instance, "name") ?? $"Garland instance {garlandInstanceId}";
        var instanceCategory = GetStringProperty(instance, "category") ?? "unknown category";

        if (seedPatch is not null && instancePatch is not null && Math.Abs(seedPatch.Value - instancePatch.Value) > 0.001)
        {
            throw new InvalidOperationException($"Seed item patch {seedPatch.Value} does not match instance patch {instancePatch.Value}.");
        }

        var cofferDescriptors = ExtractGearCofferDescriptors(instance, instanceDocument.RootElement);
        IReadOnlySet<GearSlot> allowedSlots;
        string? normalizedWing = null;
        string slotFilter;
        if (cofferDescriptors.Count > 0)
        {
            allowedSlots = cofferDescriptors.Select(descriptor => descriptor.Slot).ToHashSet();
            slotFilter = string.Join(",", allowedSlots.OrderBy(slot => slot));
            var cofferItems = await ExpandCofferDescriptorsAsync(cofferDescriptors, cancellationToken).ConfigureAwait(false);
            return PersistLocalSeed(
                garlandInstanceId,
                instanceName,
                instanceCategory,
                instancePatch,
                seedGearInfo.ItemId,
                seedItemName,
                seedPrefix,
                seedItemLevel,
                seedPatch,
                normalizedWing,
                slotFilter,
                cofferItems);
        }

        if (RequiresRaidWing(instanceCategory, instance, out var raidReason))
        {
            if (string.IsNullOrWhiteSpace(wing))
            {
                throw new InvalidOperationException($"This looks like a winged raid ({raidReason}). Provide wing 1, 2, 3, or 4.");
            }

            normalizedWing = NormalizeWing(wing);
            allowedSlots = GetRaidWingSlots(normalizedWing);
            slotFilter = string.Join(",", allowedSlots.OrderBy(slot => slot));
        }
        else
        {
            allowedSlots = Enum.GetValues<GearSlot>()
                .Where(slot => slot != GearSlot.Unknown)
                .ToHashSet();
            slotFilter = "all";
        }

        var items = (await SearchGarlandGearByPrefixAndItemLevelAsync(
                seedPrefix,
                seedItemLevel,
                cancellationToken).ConfigureAwait(false))
            .Where(item => allowedSlots.Contains(item.Slot))
            .ToArray();

        return PersistLocalSeed(
            garlandInstanceId,
            instanceName,
            instanceCategory,
            instancePatch,
            seedGearInfo.ItemId,
            seedItemName,
            seedPrefix,
            seedItemLevel,
            seedPatch,
            normalizedWing,
            slotFilter,
            items);
    }

    private LocalSeededDropSet PersistLocalSeed(
        uint garlandInstanceId,
        string instanceName,
        string instanceCategory,
        double? instancePatch,
        uint seedItemId,
        string seedItemName,
        string seedPrefix,
        uint itemLevel,
        double? seedPatch,
        string? wing,
        string slotFilter,
        IReadOnlyList<GarlandLootItemInfo> items)
    {
        var seed = new LocalSeededDropSet(
            garlandInstanceId,
            instanceName,
            instanceCategory,
            instancePatch,
            seedItemId,
            seedItemName,
            seedPrefix,
            itemLevel,
            seedPatch,
            wing,
            slotFilter,
            DateTimeOffset.UtcNow,
            items);
        repository.UpsertLocalSeededDrops(seed);
        return seed;
    }

    private async Task<IReadOnlyList<GarlandLootItemInfo>> SearchGarlandGearByPrefixAndItemLevelAsync(
        string prefix,
        uint itemLevel,
        CancellationToken cancellationToken)
    {
        var payload = await GetOrFetchAsync(
            $"garland:search:en:{prefix.ToUpperInvariant()}",
            GarlandToolsClient.BuildSearchPath(GarlandToolsClient.DefaultLanguage, prefix),
            token => garlandToolsClient.SearchAsync(prefix, token),
            cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(payload.PayloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var itemIds = new SortedSet<uint>();
        var itemLevels = new Dictionary<uint, uint>();
        foreach (var result in document.RootElement.EnumerateArray())
        {
            if (!result.TryGetProperty("type", out var type) ||
                !type.ValueEquals("item") ||
                !result.TryGetProperty("obj", out var obj) ||
                !TryGetUIntProperty(obj, "i", out var itemId) ||
                !TryGetUIntProperty(obj, "l", out var resultItemLevel) ||
                resultItemLevel != itemLevel)
            {
                continue;
            }

            var name = GetStringProperty(obj, "n");
            if (string.IsNullOrWhiteSpace(name) || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            itemIds.Add(itemId);
            itemLevels[itemId] = resultItemLevel;
        }

        return BuildLootItems(itemIds, itemLevels);
    }

    private async Task<IReadOnlyList<GarlandLootItemInfo>> SearchGarlandGearByPrefixPatchAndSlotsAsync(
        string prefix,
        double? patch,
        IReadOnlySet<GearSlot> allowedSlots,
        CancellationToken cancellationToken)
    {
        var payload = await GetOrFetchAsync(
            $"garland:search:en:{prefix.ToUpperInvariant()}",
            GarlandToolsClient.BuildSearchPath(GarlandToolsClient.DefaultLanguage, prefix),
            token => garlandToolsClient.SearchAsync(prefix, token),
            cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(payload.PayloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var itemIds = new SortedSet<uint>();
        var itemLevels = new Dictionary<uint, uint>();
        foreach (var result in document.RootElement.EnumerateArray())
        {
            if (!result.TryGetProperty("type", out var type) ||
                !type.ValueEquals("item") ||
                !result.TryGetProperty("obj", out var obj) ||
                !TryGetUIntProperty(obj, "i", out var itemId))
            {
                continue;
            }

            var name = GetStringProperty(obj, "n");
            if (string.IsNullOrWhiteSpace(name) ||
                !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !itemIdentityService.TryGetGearItemInfo(itemId, out var gearInfo) ||
                !allowedSlots.Contains(gearInfo.Slot))
            {
                continue;
            }

            if (patch is not null)
            {
                var itemPatch = await GetItemPatchAsync(gearInfo.ItemId, cancellationToken).ConfigureAwait(false);
                if (itemPatch is null || Math.Abs(itemPatch.Value - patch.Value) > 0.001)
                {
                    continue;
                }
            }

            itemIds.Add(gearInfo.ItemId);
            if (TryGetUIntProperty(obj, "l", out var itemLevel))
            {
                itemLevels[gearInfo.ItemId] = itemLevel;
            }
        }

        return BuildLootItems(itemIds, itemLevels);
    }

    private async Task<double?> GetItemPatchAsync(uint itemId, CancellationToken cancellationToken)
    {
        var payload = await GetOrFetchAsync(
            $"garland:item:en:{itemId}",
            GarlandToolsClient.BuildItemPath(GarlandToolsClient.DefaultLanguage, itemId),
            token => garlandToolsClient.GetItemAsync(itemId, token),
            cancellationToken).ConfigureAwait(false);

        using var itemDocument = JsonDocument.Parse(payload.PayloadJson);
        var item = itemDocument.RootElement.TryGetProperty("item", out var itemElement)
            ? itemElement
            : itemDocument.RootElement;
        return TryGetDoubleProperty(item, "patch", out var itemPatch) ? itemPatch : null;
    }

    private async Task<bool> IsGarlandOutfitItemAsync(uint itemId, CancellationToken cancellationToken)
    {
        var payload = await GetOrFetchAsync(
            $"garland:item:en:{itemId}",
            GarlandToolsClient.BuildItemPath(GarlandToolsClient.DefaultLanguage, itemId),
            token => garlandToolsClient.GetItemAsync(itemId, token),
            cancellationToken).ConfigureAwait(false);

        using var itemDocument = JsonDocument.Parse(payload.PayloadJson);
        var item = itemDocument.RootElement.TryGetProperty("item", out var itemElement)
            ? itemElement
            : itemDocument.RootElement;
        return TryGetUIntProperty(item, "category", out var category) && category == GarlandOutfitCategory;
    }

    private async Task<IReadOnlyList<uint>> GetTradeCurrencyGearItemIdsAsync(
        uint currencyItemId,
        CancellationToken cancellationToken)
    {
        var payload = await GetOrFetchAsync(
            $"garland:item:en:{currencyItemId}",
            GarlandToolsClient.BuildItemPath(GarlandToolsClient.DefaultLanguage, currencyItemId),
            token => garlandToolsClient.GetItemAsync(currencyItemId, token),
            cancellationToken).ConfigureAwait(false);

        using var itemDocument = JsonDocument.Parse(payload.PayloadJson);
        var item = itemDocument.RootElement.TryGetProperty("item", out var itemElement)
            ? itemElement
            : itemDocument.RootElement;

        if (!item.TryGetProperty("tradeCurrency", out var tradeCurrency) ||
            tradeCurrency.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var itemIds = new SortedSet<uint>();
        foreach (var shop in tradeCurrency.EnumerateArray())
        {
            if (!shop.TryGetProperty("listings", out var listings) ||
                listings.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var listing in listings.EnumerateArray())
            {
                if (!listing.TryGetProperty("item", out var listingItems) ||
                    listingItems.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var listingItem in listingItems.EnumerateArray())
                {
                    if (!TryGetFlexibleUIntProperty(listingItem, "id", out var itemId) ||
                        !itemIdentityService.TryGetGearItemInfo(itemId, out var gearInfo))
                    {
                        continue;
                    }

                    itemIds.Add(gearInfo.ItemId);
                }
            }
        }

        return itemIds.ToArray();
    }

    private async Task<IReadOnlyList<GarlandLootItemInfo>> ExpandCofferRewardsAsync(
        IReadOnlyList<GearCofferDescriptor> descriptors,
        CancellationToken cancellationToken)
    {
        return descriptors.Count == 0
            ? []
            : await ExpandCofferDescriptorsAsync(descriptors, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<GarlandLootItemInfo>> ExpandCofferDescriptorsAsync(
        IReadOnlyList<GearCofferDescriptor> descriptors,
        CancellationToken cancellationToken)
    {
        var results = new List<GarlandLootItemInfo>();
        foreach (var group in descriptors.GroupBy(descriptor => (descriptor.Prefix, descriptor.ItemLevel)))
        {
            var allowedSlots = group.Select(descriptor => descriptor.Slot).ToHashSet();
            var items = await SearchGarlandGearByPrefixAndItemLevelAsync(
                group.Key.Prefix,
                group.Key.ItemLevel,
                cancellationToken).ConfigureAwait(false);
            results.AddRange(items.Where(item => allowedSlots.Contains(item.Slot)));
        }

        return results
            .DistinctBy(item => item.ItemId)
            .OrderBy(item => GetLootCategory(item), StringComparer.Ordinal)
            .ThenBy(item => item.Slot)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<IReadOnlyList<GarlandLootItemInfo>> ExpandHoloRewardsAsync(
        JsonElement instance,
        JsonElement root,
        double? instancePatch,
        CancellationToken cancellationToken)
    {
        var descriptors = ExtractHoloRewardDescriptors(instance, root);
        if (descriptors.Count == 0)
        {
            return [];
        }

        var results = new List<GarlandLootItemInfo>();
        foreach (var group in descriptors.GroupBy(descriptor => descriptor.Prefix))
        {
            var allowedSlots = group.SelectMany(descriptor => descriptor.Slots).ToHashSet();
            var items = await SearchGarlandGearByPrefixPatchAndSlotsAsync(
                group.Key,
                instancePatch,
                allowedSlots,
                cancellationToken).ConfigureAwait(false);
            results.AddRange(items);
        }

        return results
            .DistinctBy(item => item.ItemId)
            .OrderBy(item => GetLootCategory(item), StringComparer.Ordinal)
            .ThenBy(item => item.Slot)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<IReadOnlyList<GarlandLootItemInfo>> ExpandNormalRaidTokenRewardsAsync(
        JsonElement instance,
        CancellationToken cancellationToken)
    {
        var rewardItemIds = ExtractTopLevelItemIds(instance, "rewards");
        if (rewardItemIds.Count == 0)
        {
            return [];
        }

        var exchangeGearItemIds = new SortedSet<uint>();
        foreach (var rewardItemId in rewardItemIds)
        {
            foreach (var itemId in await GetTradeCurrencyGearItemIdsAsync(rewardItemId, cancellationToken).ConfigureAwait(false))
            {
                exchangeGearItemIds.Add(itemId);
            }
        }

        return BuildLootItems(exchangeGearItemIds, new Dictionary<uint, uint>());
    }

    public async Task<(bool FromCache, DateTimeOffset ExpiresAtUtc, IReadOnlyList<GarlandInstanceIndexMatch> Matches)> FindInstanceIndexMatchesAsync(
        string contentName,
        CancellationToken cancellationToken = default)
    {
        var payload = await GetOrFetchAsync(
            "garland:browse:en:instance",
            GarlandToolsClient.BuildInstanceIndexPath(GarlandToolsClient.DefaultLanguage),
            garlandToolsClient.GetInstanceIndexAsync,
            cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(payload.PayloadJson);
        if (!document.RootElement.TryGetProperty("browse", out var browse) || browse.ValueKind != JsonValueKind.Array)
        {
            return (payload.FromCache, payload.ExpiresAtUtc, []);
        }

        var normalizedContentName = NormalizeInstanceName(contentName);
        var matches = new List<GarlandInstanceIndexMatch>();
        foreach (var entry in browse.EnumerateArray())
        {
            var garlandName = GetStringProperty(entry, "n");
            if (string.IsNullOrWhiteSpace(garlandName) || NormalizeInstanceName(garlandName) != normalizedContentName)
            {
                continue;
            }

            if (!TryGetUIntProperty(entry, "i", out var instanceId))
            {
                continue;
            }

            matches.Add(new GarlandInstanceIndexMatch(
                instanceId,
                garlandName,
                GetStringProperty(entry, "t") ?? "unknown category",
                TryGetUIntProperty(entry, "min_lvl", out var minLevel) ? minLevel : null,
                TryGetUIntProperty(entry, "max_lvl", out var maxLevel) ? maxLevel : null));
        }

        return (payload.FromCache, payload.ExpiresAtUtc, matches);
    }

    private async Task<GarlandPayloadResult> GetOrFetchAsync(
        string cacheKey,
        string path,
        Func<CancellationToken, Task<string>> fetch,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        lock (memoryCacheLock)
        {
            if (memoryCache.TryGetValue(cacheKey, out var memoryEntry) && memoryEntry.ExpiresAtUtc > now)
            {
                return memoryEntry with { FromCache = true };
            }
        }

        var cached = repository.GetValidRemoteCacheEntry(cacheKey, now);
        if (cached is not null)
        {
            var cachedResult = new GarlandPayloadResult(
                cached.CacheKey,
                cached.Url,
                cached.PayloadJson,
                FromCache: true,
                cached.FetchedAtUtc,
                cached.ExpiresAtUtc);

            lock (memoryCacheLock)
            {
                memoryCache[cacheKey] = cachedResult;
            }

            return cachedResult;
        }

        var payload = await fetch(cancellationToken).ConfigureAwait(false);
        var fetchedAt = DateTimeOffset.UtcNow;
        var expiresAt = fetchedAt.Add(DefaultCacheTtl);
        repository.UpsertRemoteCacheEntry(cacheKey, path, payload, fetchedAt, expiresAt);

        var result = new GarlandPayloadResult(
            cacheKey,
            path,
            payload,
            FromCache: false,
            fetchedAt,
            expiresAt);

        lock (memoryCacheLock)
        {
            memoryCache[cacheKey] = result;
        }

        return result;
    }

    private static void AddTopLevelItemIds(JsonElement element, string propertyName, ISet<string> rewardFields, ISet<uint> itemIds)
    {
        if (!element.TryGetProperty(propertyName, out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        rewardFields.Add(propertyName);
        AddItemIdsFromArray(itemsElement, itemIds);
    }

    private static SortedSet<uint> ExtractTopLevelItemIds(JsonElement element, string propertyName)
    {
        var itemIds = new SortedSet<uint>();
        if (element.TryGetProperty(propertyName, out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
        {
            AddItemIdsFromArray(itemsElement, itemIds);
        }

        return itemIds;
    }

    private static void AddNestedCofferItemIds(JsonElement element, string propertyName, ISet<string> rewardFields, ISet<uint> itemIds)
    {
        if (!element.TryGetProperty(propertyName, out var containerElement) || containerElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var foundItems = false;
        foreach (var entry in containerElement.EnumerateArray())
        {
            if (entry.TryGetProperty("items", out var directItems) && directItems.ValueKind == JsonValueKind.Array)
            {
                foundItems = true;
                AddItemIdsFromArray(directItems, itemIds);
            }

            if (!entry.TryGetProperty("coffer", out var coffer) ||
                !coffer.TryGetProperty("items", out var cofferItems) ||
                cofferItems.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foundItems = true;
            AddItemIdsFromArray(cofferItems, itemIds);
        }

        if (foundItems)
        {
            rewardFields.Add(propertyName);
        }
    }

    private static void AddItemIdsFromArray(JsonElement itemsElement, ISet<uint> itemIds)
    {
        foreach (var item in itemsElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number && item.TryGetUInt32(out var itemId))
            {
                itemIds.Add(itemId);
            }
        }
    }

    private IReadOnlyList<GarlandLootItemInfo> BuildLootItems(IEnumerable<uint> itemIds, IReadOnlyDictionary<uint, uint> partialItemLevels)
    {
        return itemIds
            .Where(itemId => itemIdentityService.TryGetGearItemInfo(itemId, out _))
            .Select(itemId =>
            {
                itemIdentityService.TryGetGearItemInfo(itemId, out var gearInfo);
                return new GarlandLootItemInfo(
                    gearInfo.ItemId,
                    gearInfo.Name,
                    gearInfo.Slot,
                    gearInfo.ArmorCategory,
                    partialItemLevels.TryGetValue(gearInfo.ItemId, out var partialLevel)
                        ? partialLevel
                        : itemIdentityService.TryGetItemLevel(gearInfo.ItemId),
                    gearInfo.IconId);
            })
            .DistinctBy(item => item.ItemId)
            .OrderBy(item => GetLootCategory(item), StringComparer.Ordinal)
            .ThenBy(item => item.Slot)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static SortedSet<uint> ExtractGarlandLootItemIds(JsonElement instance)
    {
        var itemIds = new SortedSet<uint>();
        AddTopLevelItemIds(instance, "rewards", new HashSet<string>(), itemIds);
        AddTopLevelItemIds(instance, "items", new HashSet<string>(), itemIds);
        AddNestedCofferItemIds(instance, "fights", new HashSet<string>(), itemIds);
        AddNestedCofferItemIds(instance, "coffers", new HashSet<string>(), itemIds);
        return itemIds;
    }

    private static IReadOnlyList<GearCofferDescriptor> ExtractGearCofferDescriptors(JsonElement instance, JsonElement root)
    {
        var rewardItemIds = new HashSet<uint>();
        AddTopLevelItemIds(instance, "rewards", new HashSet<string>(), rewardItemIds);
        if (rewardItemIds.Count == 0 ||
            !root.TryGetProperty("partials", out var partials) ||
            partials.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var descriptors = new List<GearCofferDescriptor>();
        foreach (var partial in partials.EnumerateArray())
        {
            if (!partial.TryGetProperty("type", out var type) ||
                !type.ValueEquals("item") ||
                !partial.TryGetProperty("obj", out var obj) ||
                !TryGetUIntProperty(obj, "i", out var itemId) ||
                !rewardItemIds.Contains(itemId))
            {
                continue;
            }

            var name = GetStringProperty(obj, "n");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var match = GearCofferNamePattern.Match(name);
            if (!match.Success ||
                !uint.TryParse(match.Groups["itemLevel"].Value, out var itemLevel) ||
                !TryMapCofferSlot(match.Groups["slot"].Value, out var slot))
            {
                continue;
            }

            descriptors.Add(new GearCofferDescriptor(
                match.Groups["prefix"].Value,
                slot,
                itemLevel,
                name));
        }

        return descriptors;
    }

    private static IReadOnlyList<HoloRewardDescriptor> ExtractHoloRewardDescriptors(JsonElement instance, JsonElement root)
    {
        var rewardItemIds = new HashSet<uint>();
        AddTopLevelItemIds(instance, "rewards", new HashSet<string>(), rewardItemIds);
        if (rewardItemIds.Count == 0 ||
            !root.TryGetProperty("partials", out var partials) ||
            partials.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var descriptors = new List<HoloRewardDescriptor>();
        foreach (var partial in partials.EnumerateArray())
        {
            if (!partial.TryGetProperty("type", out var type) ||
                !type.ValueEquals("item") ||
                !partial.TryGetProperty("obj", out var obj) ||
                !TryGetUIntProperty(obj, "i", out var itemId) ||
                !rewardItemIds.Contains(itemId))
            {
                continue;
            }

            var name = GetStringProperty(obj, "n");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var match = HoloRewardNamePattern.Match(name);
            if (!match.Success || !TryMapHoloTokenSlots(match.Groups["token"].Value, out var slots))
            {
                continue;
            }

            descriptors.Add(new HoloRewardDescriptor(
                match.Groups["prefix"].Value,
                slots,
                name));
        }

        return descriptors;
    }

    private static bool TryMapCofferSlot(string slotName, out GearSlot slot)
    {
        slot = slotName.ToUpperInvariant() switch
        {
            "HEAD" => GearSlot.Head,
            "HAND" => GearSlot.Hands,
            "LEG" => GearSlot.Legs,
            "CHEST" => GearSlot.Body,
            "FOOT" => GearSlot.Feet,
            "EARRING" => GearSlot.Ears,
            "NECKLACE" => GearSlot.Neck,
            "BRACELET" => GearSlot.Wrists,
            "RING" => GearSlot.Ring,
            "WEAPON" => GearSlot.MainHand,
            _ => GearSlot.Unknown,
        };
        return slot != GearSlot.Unknown;
    }

    private static bool TryMapHoloTokenSlots(string tokenName, out IReadOnlySet<GearSlot> slots)
    {
        slots = tokenName.ToUpperInvariant() switch
        {
            "ARMOR" => new HashSet<GearSlot> { GearSlot.Body },
            "BLADE" => new HashSet<GearSlot> { GearSlot.MainHand, GearSlot.OffHand },
            "CHAUSSES" => new HashSet<GearSlot> { GearSlot.Legs },
            "EARRING" or "EARRINGS" => new HashSet<GearSlot>
            {
                GearSlot.Ears,
                GearSlot.Neck,
                GearSlot.Wrists,
                GearSlot.Ring,
            },
            "GAUNTLET" or "GAUNTLETS" => new HashSet<GearSlot> { GearSlot.Hands },
            "GREAVES" => new HashSet<GearSlot> { GearSlot.Feet },
            "HELM" => new HashSet<GearSlot> { GearSlot.Head },
            _ => new HashSet<GearSlot>(),
        };

        return slots.Count > 0;
    }

    private static bool RequiresRaidWing(string instanceCategory, JsonElement instance, out string reason)
    {
        reason = string.Empty;
        if (!instanceCategory.Contains("Raid", StringComparison.OrdinalIgnoreCase) ||
            instanceCategory.Contains("Alliance", StringComparison.OrdinalIgnoreCase) ||
            instanceCategory.Contains("Chaotic", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var tank = TryGetUIntProperty(instance, "tank", out var tankCount) ? tankCount : 0;
        var healer = TryGetUIntProperty(instance, "healer", out var healerCount) ? healerCount : 0;
        var dps =
            (TryGetUIntProperty(instance, "melee", out var meleeCount) ? meleeCount : 0) +
            (TryGetUIntProperty(instance, "ranged", out var rangedCount) ? rangedCount : 0);
        if (tank == 1 && healer == 2 && dps == 5)
        {
            return false;
        }

        reason = $"{instanceCategory}, composition {tank}/{healer}/{dps}";
        return true;
    }

    private static bool ShouldRestrictMatchedExpansionToKnownSlots(
        JsonElement instance,
        IReadOnlyCollection<GarlandLootItemInfo> garlandItems)
    {
        if (garlandItems.Count == 0 ||
            !TryGetRaidComposition(instance, out var tank, out var healer, out var dps) ||
            tank != 2 ||
            healer != 2 ||
            dps != 4)
        {
            return false;
        }

        var category = GetStringProperty(instance, "category") ?? string.Empty;
        return category.Contains("Raid", StringComparison.OrdinalIgnoreCase) &&
            !category.Contains("Alliance", StringComparison.OrdinalIgnoreCase) &&
            !category.Contains("Chaotic", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRegularEightPlayerRaid(JsonElement instance)
    {
        if (!TryGetRaidComposition(instance, out var tank, out var healer, out var dps) ||
            tank != 2 ||
            healer != 2 ||
            dps != 4)
        {
            return false;
        }

        var category = GetStringProperty(instance, "category") ?? string.Empty;
        return category.Contains("Raid", StringComparison.OrdinalIgnoreCase) &&
            !category.Contains("Alliance", StringComparison.OrdinalIgnoreCase) &&
            !category.Contains("Chaotic", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetRaidComposition(JsonElement instance, out uint tank, out uint healer, out uint dps)
    {
        tank = TryGetUIntProperty(instance, "tank", out var tankCount) ? tankCount : 0;
        healer = TryGetUIntProperty(instance, "healer", out var healerCount) ? healerCount : 0;
        dps =
            (TryGetUIntProperty(instance, "melee", out var meleeCount) ? meleeCount : 0) +
            (TryGetUIntProperty(instance, "ranged", out var rangedCount) ? rangedCount : 0);

        return tank != 0 || healer != 0 || dps != 0;
    }

    private static string NormalizeWing(string wing)
    {
        var normalized = wing.Trim().ToUpperInvariant();
        return normalized switch
        {
            "1" or "W1" or "M1" or "A1" => "1",
            "2" or "W2" or "M2" or "A2" => "2",
            "3" or "W3" or "M3" or "A3" => "3",
            "4" or "W4" or "M4" or "A4" => "4",
            _ => throw new InvalidOperationException("Wing must be 1, 2, 3, or 4."),
        };
    }

    private static IReadOnlySet<GearSlot> GetRaidWingSlots(string wing)
    {
        return wing switch
        {
            "1" => new HashSet<GearSlot> { GearSlot.Ears, GearSlot.Neck, GearSlot.Wrists, GearSlot.Ring },
            "2" => new HashSet<GearSlot> { GearSlot.Head, GearSlot.Hands, GearSlot.Feet },
            "3" => new HashSet<GearSlot> { GearSlot.Body, GearSlot.Legs },
            "4" => new HashSet<GearSlot> { GearSlot.MainHand, GearSlot.OffHand },
            _ => throw new InvalidOperationException("Wing must be 1, 2, 3, or 4."),
        };
    }

    private static Dictionary<uint, uint> ExtractPartialItemLevels(JsonElement root)
    {
        var itemLevels = new Dictionary<uint, uint>();
        if (!root.TryGetProperty("partials", out var partials) || partials.ValueKind != JsonValueKind.Array)
        {
            return itemLevels;
        }

        foreach (var partial in partials.EnumerateArray())
        {
            if (!partial.TryGetProperty("type", out var type) ||
                !type.ValueEquals("item") ||
                !partial.TryGetProperty("obj", out var obj) ||
                !TryGetUIntProperty(obj, "i", out var itemId) ||
                !TryGetUIntProperty(obj, "l", out var itemLevel))
            {
                continue;
            }

            itemLevels[itemId] = itemLevel;
        }

        return itemLevels;
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool TryGetUIntProperty(JsonElement element, string propertyName, out uint value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetUInt32(out value);
    }

    private static bool TryGetFlexibleUIntProperty(JsonElement element, string propertyName, out uint value)
    {
        value = 0;
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetUInt32(out value),
            JsonValueKind.String => uint.TryParse(property.GetString(), out value),
            _ => false,
        };
    }

    private static bool TryGetDoubleProperty(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out value);
    }

    private static string NormalizeInstanceName(string value)
    {
        return string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Replace('\u2019', '\'')
            .ToUpperInvariant();
    }

    private static string GetFirstWord(string value)
    {
        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? value;
    }

    private static string GetLootCategory(GarlandLootItemInfo item)
    {
        return item.Slot is GearSlot.MainHand or GearSlot.OffHand ? "Weapons" : item.ArmorCategory;
    }

    private sealed record GearCofferDescriptor(
        string Prefix,
        GearSlot Slot,
        uint ItemLevel,
        string CofferName);

    private sealed record HoloRewardDescriptor(
        string Prefix,
        IReadOnlySet<GearSlot> Slots,
        string RewardName);
}
