using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using GlamourCheck.Services;
using GlamourCheck.Windows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace GlamourCheck;

/// <summary>
/// Dalamud entry point and composition root. Keep plugin-level logic limited to service wiring,
/// command registration, window registration, and small settings-facing facade methods.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/glamourcheck";

    public Configuration Configuration { get; init; }
    public ConfigurationService ConfigurationService { get; init; }

    public readonly WindowSystem WindowSystem = new("GlamourCheck");
    private CharacterContextService CharacterContextService { get; init; }
    private ItemIdentityService ItemIdentityService { get; init; }
    private ICollectionRepository CollectionRepository { get; init; }
    private CollectionState CollectionState { get; init; }
    private InventorySyncService InventorySyncService { get; init; }
    private GearIconOverlayService GearIconOverlayService { get; init; }
    private GarlandToolsClient GarlandToolsClient { get; init; }
    private GarlandDropLookupService GarlandDropLookupService { get; init; }
    private GearTryOnService GearTryOnService { get; init; }
    private InstanceSummaryOverlayService InstanceSummaryOverlayService { get; init; }
    private DutyFinderSummaryOverlayService DutyFinderSummaryOverlayService { get; init; }
    private ConfigWindow ConfigWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        ConfigurationService = new ConfigurationService(Configuration);
        ItemIdentityService = new ItemIdentityService(DataManager);
        CollectionRepository = new CollectionRepository(Path.Combine(PluginInterface.ConfigDirectory.FullName, "collection.db"));
        CollectionState = new CollectionState();
        GarlandToolsClient = new GarlandToolsClient();

        CollectionRepository.Initialize();
        CharacterContextService = new CharacterContextService(ClientState, PlayerState, CollectionRepository, CollectionState);
        GarlandDropLookupService = new GarlandDropLookupService(GarlandToolsClient, CollectionRepository, ItemIdentityService);
        GearTryOnService = new GearTryOnService();
        InventorySyncService = new InventorySyncService(GameInventory, Framework, ClientState, DataManager, AddonLifecycle, CharacterContextService, CollectionRepository, CollectionState, ItemIdentityService, Log);
        var lootTooltipRenderer = new LootTooltipRenderer(CollectionState, TextureProvider, DataManager, PluginInterface.AssemblyLocation.Directory!.FullName, GearTryOnService);
        GearIconOverlayService = new GearIconOverlayService(ConfigurationService, GameGui, GameInventory, TextureProvider, PluginInterface.AssemblyLocation.Directory!.FullName, ItemIdentityService, CollectionState, GarlandDropLookupService, Log);
        InstanceSummaryOverlayService = new InstanceSummaryOverlayService(ConfigurationService, ClientState, DataManager, Framework, GameGui, lootTooltipRenderer, GarlandDropLookupService, CollectionState, Log);
        DutyFinderSummaryOverlayService = new DutyFinderSummaryOverlayService(ConfigurationService, DataManager, Framework, GameGui, GarlandDropLookupService, CollectionRepository, CollectionState, lootTooltipRenderer, Log);
        InventorySyncService.SyncAvailableSources(force: true);

        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open GlamourCheck settings.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw += GearIconOverlayService.Draw;
        PluginInterface.UiBuilder.Draw += InstanceSummaryOverlayService.Draw;
        PluginInterface.UiBuilder.Draw += DutyFinderSummaryOverlayService.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        Log.Information($"{PluginInterface.Manifest.Name} loaded.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw -= GearIconOverlayService.Draw;
        PluginInterface.UiBuilder.Draw -= InstanceSummaryOverlayService.Draw;
        PluginInterface.UiBuilder.Draw -= DutyFinderSummaryOverlayService.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        DutyFinderSummaryOverlayService.Dispose();
        InstanceSummaryOverlayService.Dispose();
        GearIconOverlayService.Dispose();
        GarlandToolsClient.Dispose();
        InventorySyncService.Dispose();
        CollectionRepository.Dispose();
        CharacterContextService.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        ToggleConfigUi();
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();

    public async Task<GarlandIndexStatus> RefreshGarlandIndexForSettingsAsync()
    {
        GarlandDropLookupService.ClearCache();
        return await GarlandDropLookupService.GetInstanceIndexStatusAsync().ConfigureAwait(false);
    }

    public Task<GarlandIndexStatus> GetGarlandIndexForSettingsAsync()
    {
        return GarlandDropLookupService.GetInstanceIndexStatusAsync();
    }

    public IReadOnlyList<OwnedSeedItemInfo> GetOwnedSeedItemsForSettings()
    {
        if (CharacterContextService.CurrentCharacterKey is not { } characterKey)
        {
            return [];
        }

        var sourceMap = CollectionRepository.GetItemSourceMap(characterKey);
        return CollectionRepository.GetCollectedItems(characterKey)
            .Select(itemId => ItemIdentityService.TryGetGearItemInfo(itemId, out var gearInfo)
                ? new OwnedSeedItemInfo(
                    gearInfo.ItemId,
                    gearInfo.Name,
                    gearInfo.Slot,
                    gearInfo.ArmorCategory,
                    sourceMap.TryGetValue(gearInfo.ItemId, out var sources)
                        ? sources.Select(FormatSourceKeyForUi).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
                        : [])
                : null)
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Task<GarlandSeedItemInfo> GetSeedItemInfoForSettingsAsync(uint itemId)
    {
        return GarlandDropLookupService.GetSeedItemInfoAsync(itemId);
    }

    public Task<IReadOnlyList<GarlandSeedInstanceCandidate>> GetSeedInstanceCandidatesForSettingsAsync(double? seedPatch)
    {
        return GarlandDropLookupService.GetSeedInstanceCandidatesAsync(seedPatch);
    }

    public async Task<LocalSeededDropSet> CreateSeedForSettingsAsync(uint itemId, uint instanceId, string? wing)
    {
        var seed = await GarlandDropLookupService.CreateLocalSeededDropsAsync(itemId, instanceId, wing).ConfigureAwait(false);
        DutyFinderSummaryOverlayService.InvalidateGarlandInstance(seed.GarlandInstanceId);
        return seed;
    }

    public async Task<LocalSeededDropSet> RebuildSeedForSettingsAsync(LocalSeededDropSummary seed)
    {
        var rebuiltSeed = await GarlandDropLookupService.CreateLocalSeededDropsAsync(seed.SeedItemId, seed.GarlandInstanceId, seed.Wing).ConfigureAwait(false);
        DutyFinderSummaryOverlayService.InvalidateGarlandInstance(rebuiltSeed.GarlandInstanceId);
        return rebuiltSeed;
    }

    public bool DeleteSeedForSettings(uint instanceId)
    {
        var deleted = GarlandDropLookupService.DeleteLocalSeededDrops(instanceId);
        if (deleted)
        {
            DutyFinderSummaryOverlayService.InvalidateGarlandInstance(instanceId);
        }

        return deleted;
    }

    public IReadOnlyList<LocalSeededDropSummary> GetLocalSeededDropSummaries()
    {
        return GarlandDropLookupService.GetLocalSeededDropSummaries();
    }

    private static string FormatSourceKeyForUi(string sourceKey)
    {
        if (sourceKey.StartsWith("retainer:", StringComparison.OrdinalIgnoreCase))
        {
            return "Retainer";
        }

        return sourceKey switch
        {
            CollectionSource.Inventory => "Inventory",
            CollectionSource.Equipped => "Equipped",
            CollectionSource.ArmoryChest => "Armory",
            CollectionSource.GlamourDresser => "Dresser",
            CollectionSource.Armoire => "Armoire",
            CollectionSource.ChocoboSaddlebag => "Saddlebag",
            _ => sourceKey,
        };
    }

}
