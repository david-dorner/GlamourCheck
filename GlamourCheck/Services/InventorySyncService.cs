using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;

namespace GlamourCheck.Services;

/// <summary>
/// Maintains per-source collection snapshots from readable game containers.
/// Local containers are refreshed from inventory events and territory changes; server-side
/// containers only replace their snapshots when the corresponding game data is actually loaded.
/// </summary>
public sealed unsafe class InventorySyncService : IDisposable
{
    private static readonly GameInventoryType[] InventoryTypes =
    [
        GameInventoryType.Inventory1,
        GameInventoryType.Inventory2,
        GameInventoryType.Inventory3,
        GameInventoryType.Inventory4,
    ];

    private static readonly GameInventoryType[] EquippedTypes =
    [
        GameInventoryType.EquippedItems,
    ];

    private static readonly GameInventoryType[] ArmoryTypes =
    [
        GameInventoryType.ArmoryMainHand,
        GameInventoryType.ArmoryOffHand,
        GameInventoryType.ArmoryHead,
        GameInventoryType.ArmoryBody,
        GameInventoryType.ArmoryHands,
        GameInventoryType.ArmoryWaist,
        GameInventoryType.ArmoryLegs,
        GameInventoryType.ArmoryFeets,
        GameInventoryType.ArmoryEar,
        GameInventoryType.ArmoryNeck,
        GameInventoryType.ArmoryWrist,
        GameInventoryType.ArmoryRings,
    ];

    private static readonly GameInventoryType[] ChocoboSaddlebagTypes =
    [
        GameInventoryType.SaddleBag1,
        GameInventoryType.SaddleBag2,
        GameInventoryType.PremiumSaddleBag1,
        GameInventoryType.PremiumSaddleBag2,
    ];

    private static readonly GameInventoryType[] RetainerTypes =
    [
        GameInventoryType.RetainerPage1,
        GameInventoryType.RetainerPage2,
        GameInventoryType.RetainerPage3,
        GameInventoryType.RetainerPage4,
        GameInventoryType.RetainerPage5,
        GameInventoryType.RetainerPage6,
        GameInventoryType.RetainerPage7,
        GameInventoryType.RetainerEquippedItems,
        GameInventoryType.RetainerMarket,
    ];

    private readonly IGameInventory gameInventory;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly CharacterContextService characterContextService;
    private readonly ICollectionRepository collectionRepository;
    private readonly CollectionState collectionState;
    private readonly ItemIdentityService itemIdentityService;
    private readonly IPluginLog log;
    private DateTimeOffset lastAutomaticSync = DateTimeOffset.MinValue;
    private DateTimeOffset lastServerSideProbe = DateTimeOffset.MinValue;
    private DateTimeOffset lastAutomaticSyncFailureLog = DateTimeOffset.MinValue;

    public InventorySyncService(
        IGameInventory gameInventory,
        IFramework framework,
        IClientState clientState,
        IDataManager dataManager,
        IAddonLifecycle addonLifecycle,
        CharacterContextService characterContextService,
        ICollectionRepository collectionRepository,
        CollectionState collectionState,
        ItemIdentityService itemIdentityService,
        IPluginLog log)
    {
        this.gameInventory = gameInventory;
        this.framework = framework;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.addonLifecycle = addonLifecycle;
        this.characterContextService = characterContextService;
        this.collectionRepository = collectionRepository;
        this.collectionState = collectionState;
        this.itemIdentityService = itemIdentityService;
        this.log = log;

        clientState.Login += OnLogin;
        clientState.TerritoryChanged += OnTerritoryChanged;
        gameInventory.InventoryChanged += OnInventoryChanged;
        framework.Update += OnFrameworkUpdate;
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "MiragePrismPrismBox", OnDresserPreFinalize);
    }

    public InventorySyncResult SyncPlayerLocalSources(bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow - lastAutomaticSync < TimeSpan.FromSeconds(1))
        {
            return new InventorySyncResult(0, false);
        }

        if (characterContextService.CurrentCharacterKey is not { } characterKey)
        {
            return new InventorySyncResult(0, false);
        }

        var syncedAtUtc = DateTimeOffset.UtcNow;
        var inventoryItems = ReadGearItemIds(InventoryTypes);
        var equippedItems = ReadGearItemIds(EquippedTypes);
        var armoryItems = ReadGearItemIds(ArmoryTypes);

        collectionRepository.ReplaceSourceSnapshot(characterKey, CollectionSource.Inventory, inventoryItems, syncedAtUtc);
        collectionRepository.ReplaceSourceSnapshot(characterKey, CollectionSource.Equipped, equippedItems, syncedAtUtc);
        collectionRepository.ReplaceSourceSnapshot(characterKey, CollectionSource.ArmoryChest, armoryItems, syncedAtUtc);
        collectionState.Reload(characterKey, collectionRepository);

        lastAutomaticSync = syncedAtUtc;

        return new InventorySyncResult(
            inventoryItems.Count + equippedItems.Count + armoryItems.Count,
            true);
    }

    public InventorySyncResult SyncAvailableSources(bool force = false)
    {
        var localResult = SyncPlayerLocalSources(force);
        var serverSideCount = 0;
        serverSideCount += SyncGlamourDresserIfAvailable() ?? 0;
        serverSideCount += SyncArmoireIfLoaded() ?? 0;
        serverSideCount += SyncChocoboSaddlebagIfAvailable() ?? 0;
        serverSideCount += SyncActiveRetainerIfAvailable() ?? 0;
        return new InventorySyncResult(localResult.TotalItems + serverSideCount, localResult.Changed || serverSideCount > 0);
    }

    public int? SyncGlamourDresserIfAvailable()
    {
        if (characterContextService.CurrentCharacterKey is not { } characterKey)
        {
            return null;
        }

        var agent = AgentMiragePrismPrismBox.Instance();
        if (agent is null || !agent->IsAddonReady() || agent->Data is null)
        {
            return null;
        }

        var itemIds = new HashSet<uint>();
        foreach (var item in agent->Data->PrismBoxItems)
        {
            if (item.ItemId == 0 || item.Slot >= 800)
            {
                continue;
            }

            var normalizedItemId = itemIdentityService.NormalizeItemId(item.ItemId);
            if (itemIdentityService.IsGearItem(normalizedItemId))
            {
                itemIds.Add(normalizedItemId);
            }
        }

        collectionRepository.ReplaceSourceSnapshot(characterKey, CollectionSource.GlamourDresser, itemIds, DateTimeOffset.UtcNow);
        collectionState.Reload(characterKey, collectionRepository);
        return itemIds.Count;
    }

    public int? SyncActiveRetainerIfAvailable()
    {
        if (characterContextService.CurrentCharacterKey is not { } characterKey)
        {
            return null;
        }

        var retainerManager = RetainerManager.Instance();
        if (retainerManager is null || !retainerManager->IsReady)
        {
            return null;
        }

        var activeRetainer = retainerManager->GetActiveRetainer();
        if (activeRetainer is null || activeRetainer->RetainerId == 0)
        {
            return null;
        }

        if (!IsAnyInventoryTypeReadable(RetainerTypes))
        {
            return null;
        }

        var itemIds = ReadGearItemIds(RetainerTypes);
        var retainerId = activeRetainer->RetainerId.ToString("X16");
        var retainerName = ReadNullTerminatedUtf8(activeRetainer->Name);
        var displayName = string.IsNullOrWhiteSpace(retainerName) ? $"Retainer {retainerId}" : $"Retainer: {retainerName}";
        collectionRepository.ReplaceSourceSnapshot(characterKey, CollectionSource.Retainer(retainerId), itemIds, DateTimeOffset.UtcNow, displayName: displayName);
        collectionState.Reload(characterKey, collectionRepository);
        return itemIds.Count;
    }

    public int? SyncChocoboSaddlebagIfAvailable()
    {
        if (characterContextService.CurrentCharacterKey is not { } characterKey)
        {
            return null;
        }

        if (!IsAnyInventoryTypeReadable(ChocoboSaddlebagTypes))
        {
            return null;
        }

        var itemIds = ReadGearItemIds(ChocoboSaddlebagTypes);
        collectionRepository.ReplaceSourceSnapshot(characterKey, CollectionSource.ChocoboSaddlebag, itemIds, DateTimeOffset.UtcNow);
        collectionState.Reload(characterKey, collectionRepository);
        return itemIds.Count;
    }

    public int? SyncArmoireIfLoaded()
    {
        if (characterContextService.CurrentCharacterKey is not { } characterKey)
        {
            return null;
        }

        var cabinet = &UIState.Instance()->Cabinet;
        if (!cabinet->IsCabinetLoaded())
        {
            return null;
        }

        var itemIds = new HashSet<uint>();
        foreach (var row in dataManager.GetExcelSheet<CabinetSheet>())
        {
            var itemId = row.Item.RowId;
            if (itemId == 0 || !cabinet->IsItemInCabinet(row.RowId))
            {
                continue;
            }

            var normalizedItemId = itemIdentityService.NormalizeItemId(itemId);
            if (itemIdentityService.IsGearItem(normalizedItemId))
            {
                itemIds.Add(normalizedItemId);
            }
        }

        collectionRepository.ReplaceSourceSnapshot(characterKey, CollectionSource.Armoire, itemIds, DateTimeOffset.UtcNow);
        collectionState.Reload(characterKey, collectionRepository);
        return itemIds.Count;
    }

    public void Dispose()
    {
        clientState.Login -= OnLogin;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        gameInventory.InventoryChanged -= OnInventoryChanged;
        framework.Update -= OnFrameworkUpdate;
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "MiragePrismPrismBox", OnDresserPreFinalize);
    }

    private HashSet<uint> ReadGearItemIds(IEnumerable<GameInventoryType> inventoryTypes)
    {
        var itemIds = new HashSet<uint>();
        foreach (var inventoryType in inventoryTypes)
        {
            foreach (ref readonly var item in gameInventory.GetInventoryItems(inventoryType))
            {
                if (item.IsEmpty)
                {
                    continue;
                }

                var normalizedItemId = itemIdentityService.NormalizeItemId(item.BaseItemId != 0 ? item.BaseItemId : item.ItemId);
                if (itemIdentityService.IsGearItem(normalizedItemId))
                {
                    itemIds.Add(normalizedItemId);
                }
            }
        }

        return itemIds;
    }

    private void OnLogin()
    {
        SyncPlayerLocalSources();
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        SyncPlayerLocalSources();
    }

    private void OnDresserPreFinalize(AddonEvent type, AddonArgs args)
    {
        // The glamour dresser is closing. Perform one last sync while the agent data
        // is still valid so that any items removed during this session are recorded.
        // This is especially important when the dresser closes within the 1-second
        // framework-update polling window after a removal.
        try
        {
            SyncGlamourDresserOnClose();
        }
        catch (Exception exception)
        {
            LogAutomaticSyncFailure("glamour dresser close", exception);
        }
    }

    private void SyncGlamourDresserOnClose()
    {
        if (characterContextService.CurrentCharacterKey is not { } characterKey)
        {
            return;
        }

        // Read directly from the agent without the IsAddonReady() guard, since the
        // addon is in the process of being finalized but the agent data is still valid.
        var agent = AgentMiragePrismPrismBox.Instance();
        if (agent is null || agent->Data is null)
        {
            return;
        }

        var itemIds = new HashSet<uint>();
        foreach (var item in agent->Data->PrismBoxItems)
        {
            if (item.ItemId == 0 || item.Slot >= 800)
            {
                continue;
            }

            var normalizedItemId = itemIdentityService.NormalizeItemId(item.ItemId);
            if (itemIdentityService.IsGearItem(normalizedItemId))
            {
                itemIds.Add(normalizedItemId);
            }
        }

        collectionRepository.ReplaceSourceSnapshot(characterKey, CollectionSource.GlamourDresser, itemIds, DateTimeOffset.UtcNow);
        collectionState.Reload(characterKey, collectionRepository);
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        try
        {
            foreach (var change in events)
            {
                if (IsPlayerLocalInventory(change.Item.ContainerType))
                {
                    SyncPlayerLocalSources();
                    return;
                }

                if (IsChocoboSaddlebagInventory(change.Item.ContainerType))
                {
                    SyncChocoboSaddlebagIfAvailable();
                    return;
                }

                if (IsRetainerInventory(change.Item.ContainerType))
                {
                    SyncActiveRetainerIfAvailable();
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            LogAutomaticSyncFailure("inventory change", exception);
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (DateTimeOffset.UtcNow - lastServerSideProbe < TimeSpan.FromSeconds(1))
        {
            return;
        }

        lastServerSideProbe = DateTimeOffset.UtcNow;
        TryAutomaticSync("glamour dresser", SyncGlamourDresserIfAvailable);
        TryAutomaticSync("armoire", SyncArmoireIfLoaded);
        TryAutomaticSync("chocobo saddlebag", SyncChocoboSaddlebagIfAvailable);
        TryAutomaticSync("active retainer", SyncActiveRetainerIfAvailable);
    }

    private static bool IsPlayerLocalInventory(GameInventoryType inventoryType)
    {
        return Array.IndexOf(InventoryTypes, inventoryType) >= 0
            || Array.IndexOf(EquippedTypes, inventoryType) >= 0
            || Array.IndexOf(ArmoryTypes, inventoryType) >= 0;
    }

    private bool IsAnyInventoryTypeReadable(IEnumerable<GameInventoryType> inventoryTypes)
    {
        foreach (var inventoryType in inventoryTypes)
        {
            if (!gameInventory.GetInventoryItems(inventoryType).IsEmpty)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsChocoboSaddlebagInventory(GameInventoryType inventoryType)
    {
        return Array.IndexOf(ChocoboSaddlebagTypes, inventoryType) >= 0;
    }

    private static bool IsRetainerInventory(GameInventoryType inventoryType)
    {
        return Array.IndexOf(RetainerTypes, inventoryType) >= 0;
    }

    private int? TryAutomaticSync(string sourceName, Func<int?> syncAction)
    {
        try
        {
            return syncAction();
        }
        catch (Exception exception)
        {
            LogAutomaticSyncFailure(sourceName, exception);
            return null;
        }
    }

    private void LogAutomaticSyncFailure(string sourceName, Exception exception)
    {
        if (DateTimeOffset.UtcNow - lastAutomaticSyncFailureLog < TimeSpan.FromSeconds(30))
        {
            return;
        }

        lastAutomaticSyncFailureLog = DateTimeOffset.UtcNow;
        log.Warning(exception, "Automatic {SourceName} sync failed; keeping previous snapshot.", sourceName);
    }

    private static string ReadNullTerminatedUtf8(ReadOnlySpan<byte> bytes)
    {
        var terminatorIndex = bytes.IndexOf((byte)0);
        var nameBytes = terminatorIndex >= 0 ? bytes[..terminatorIndex] : bytes;
        return Encoding.UTF8.GetString(nameBytes);
    }
}

public sealed record InventorySyncResult(int TotalItems, bool Changed);
