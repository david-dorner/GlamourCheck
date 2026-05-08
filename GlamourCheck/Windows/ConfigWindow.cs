using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using GlamourCheck.Services;

namespace GlamourCheck.Windows;

/// <summary>
/// Small maintenance surface for GlamourCheck. Gameplay feedback stays in native-game overlays;
/// this window only exposes persistent settings, local seed management, and Garland cache status.
/// </summary>
public class ConfigWindow : Window, IDisposable
{
    private const int PickerVisibleRows = 6;
    private const int MaxLocalSeedVisibleRows = 18;

    private static readonly (string Value, string Label)[] RaidWings =
    [
        ("1", "Wing 1: Accessories"),
        ("2", "Wing 2: Head, Hands, Feet"),
        ("3", "Wing 3: Body, Legs"),
        ("4", "Wing 4: Weapons"),
    ];

    private readonly Configuration configuration;
    private readonly Plugin plugin;

    private IReadOnlyList<OwnedSeedItemInfo> ownedItems = [];
    private IReadOnlyList<GarlandSeedInstanceCandidate> instanceCandidates = [];
    private DateTimeOffset lastOwnedItemsRefresh = DateTimeOffset.MinValue;
    private string itemSearch = string.Empty;
    private string instanceSearch = string.Empty;
    private OwnedSeedItemInfo? selectedOwnedItem;
    private GarlandSeedItemInfo? selectedSeedItem;
    private GarlandSeedInstanceCandidate? selectedInstance;
    private string? selectedWing;
    private string garlandStatus = string.Empty;
    private string seedStatus = string.Empty;
    private GarlandIndexStatus? garlandIndexStatus;
    private int seedSelectionVersion;
    private Task? garlandStatusLoadTask;
    private Task? garlandRefreshTask;
    private Task? seedMetadataLoadTask;
    private Task? seedMutationTask;
    private uint? pendingDeleteInstanceId;

    public ConfigWindow(Plugin plugin) : base("GlamourCheck###GlamourCheckSettings")
    {
        Flags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;

        Size = new Vector2(760, 640);
        SizeCondition = ImGuiCond.FirstUseEver;

        this.plugin = plugin;
        configuration = plugin.ConfigurationService.Configuration;
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        RefreshOwnedItemsIfNeeded();

        if (ImGui.BeginTabBar("settings_tabs"))
        {
            if (ImGui.BeginTabItem("General"))
            {
                DrawGeneralTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Seeding"))
            {
                DrawSeedingTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Garland Data"))
            {
                DrawGarlandTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        DrawDeleteConfirmation();
    }

    private void DrawGeneralTab()
    {
        ImGui.Spacing();
        var enabled = configuration.EnableGearIconOverlays;
        if (ImGui.Checkbox("Show collection markers on gear icons", ref enabled))
        {
            configuration.EnableGearIconOverlays = enabled;
            configuration.Save();
        }
        ImGui.TextDisabled("Shows collection state on gear icons in supported game windows.");
    }

    private void DrawGarlandTab()
    {
        EnsureGarlandStatusLoadStarted();

        ImGui.Spacing();
        DrawGarlandStatusCard();

        ImGui.Spacing();
        ImGui.TextWrapped("Reset cached Garland payloads and Duty Finder summaries, then fetch a fresh instance index. Local seeds are kept.");

        var busy = garlandRefreshTask is { IsCompleted: false };
        if (busy)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Resync Garland data", new Vector2(190, 26)))
        {
            garlandStatus = "Refreshing Garland instance index...";
            garlandRefreshTask = RefreshGarlandAsync();
        }

        if (busy)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(string.IsNullOrWhiteSpace(garlandStatus) ? "Ready" : garlandStatus);
    }

    private void DrawGarlandStatusCard()
    {
        if (garlandStatusLoadTask is { IsCompleted: false } || garlandRefreshTask is { IsCompleted: false })
        {
            ImGui.TextDisabled("Loading Garland index details...");
        }
        else if (garlandIndexStatus is null)
        {
            ImGui.TextDisabled("Garland index details are not loaded yet.");
        }

        if (ImGui.BeginTable("garland_status", 2, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Field", ImGuiTableColumnFlags.WidthFixed, 150);
            ImGui.TableSetupColumn("Value");
            DrawGarlandStatusRow("Instance index", garlandIndexStatus is null ? "Unknown" : $"{garlandIndexStatus.InstanceCount.ToString(CultureInfo.InvariantCulture)} duties");
            DrawGarlandStatusRow("Source", garlandIndexStatus is null ? "Unknown" : garlandIndexStatus.FromCache ? "Cache" : "Network");
            DrawGarlandStatusRow("Fetched", garlandIndexStatus is null ? "Unknown" : garlandIndexStatus.FetchedAtUtc.ToString("u", CultureInfo.InvariantCulture));
            DrawGarlandStatusRow("Expires", garlandIndexStatus is null ? "Unknown" : garlandIndexStatus.ExpiresAtUtc.ToString("u", CultureInfo.InvariantCulture));
            DrawGarlandStatusRow("Local seeds", plugin.GetLocalSeededDropSummaries().Count.ToString(CultureInfo.InvariantCulture));
            ImGui.EndTable();
        }
    }

    private static void DrawGarlandStatusRow(string label, string value)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextDisabled(label);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(value);
    }

    private void DrawSeedingTab()
    {
        ImGui.Spacing();
        ImGui.TextWrapped("Create local duty loot tables when Garland has not added the drops yet. Pick one owned item from the set, then pick the duty it belongs to.");
        ImGui.Spacing();

        if (ImGui.BeginTable("seed_builder", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            DrawOwnedItemPicker();
            ImGui.TableNextColumn();
            DrawInstancePicker();
            ImGui.EndTable();
        }

        DrawSeedActionBar();
        ImGui.Separator();
        DrawExistingSeeds();
    }

    private void DrawOwnedItemPicker()
    {
        ImGui.TextUnformatted("1. Seed item");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##seed_item_search", "Search owned gear...", ref itemSearch, 128);

        var filteredItems = ownedItems
            .Where(item => MatchesSearch(itemSearch, item.Name, item.ArmorCategory, item.Slot.ToString()))
            .ToArray();

        DrawPickerFrame("##owned_seed_items_frame", filteredItems.Length);
        if (filteredItems.Length == 0)
        {
            ImGui.TextDisabled(string.IsNullOrWhiteSpace(itemSearch) ? "No owned gear loaded." : "No owned gear matches.");
        }

        foreach (var item in filteredItems)
        {
            var selected = selectedOwnedItem?.ItemId == item.ItemId;
            var label = $"{item.Name}  [{item.ArmorCategory} / {FormatSlot(item.Slot)}]##owned_{item.ItemId}";
            if (ImGui.Selectable(label, selected))
            {
                SelectOwnedItem(item);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(string.Join(", ", item.Sources));
            }
        }
        ImGui.EndChild();

        if (selectedSeedItem is not null)
        {
            ImGui.TextDisabled($"IL {selectedSeedItem.ItemLevel} | Patch {FormatPatch(selectedSeedItem.Patch)} | Prefix {selectedSeedItem.Prefix}");
        }
        else if (seedMetadataLoadTask is { IsCompleted: false })
        {
            ImGui.TextDisabled("Loading item metadata...");
        }
        else
        {
            ImGui.TextDisabled(ownedItems.Count == 0 ? "No owned gear is known yet." : "Select an owned gear item.");
        }
    }

    private void DrawInstancePicker()
    {
        ImGui.TextUnformatted("2. Duty");
        if (selectedSeedItem is null)
        {
            ImGui.BeginDisabled();
        }

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##seed_instance_search", "Search matching duties...", ref instanceSearch, 128);

        var filteredInstances = instanceCandidates
            .Where(instance => MatchesSearch(instanceSearch, instance.Name, instance.Category))
            .ToArray();

        DrawPickerFrame("##seed_instances_frame", filteredInstances.Length);
        if (filteredInstances.Length == 0)
        {
            ImGui.TextDisabled(selectedSeedItem is null ? "Select an item first." : "No matching duties.");
        }

        foreach (var instance in filteredInstances)
        {
            var selected = selectedInstance?.InstanceId == instance.InstanceId;
            var label = $"{instance.Name}  [{instance.Category}]##instance_{instance.InstanceId}";
            if (ImGui.Selectable(label, selected))
            {
                selectedInstance = instance;
                selectedWing = instance.RequiresWing ? selectedWing : null;
                seedStatus = string.Empty;
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"Garland {instance.InstanceId} | Patch {FormatPatch(instance.Patch)}");
            }
        }
        ImGui.EndChild();

        if (selectedSeedItem is null)
        {
            ImGui.EndDisabled();
            ImGui.TextDisabled("Select an item first.");
            return;
        }

        if (seedMetadataLoadTask is { IsCompleted: false })
        {
            ImGui.TextDisabled("Loading duties for selected patch...");
            return;
        }

        ImGui.TextDisabled(instanceCandidates.Count == 0
            ? "No matching duties found for that patch."
            : $"{instanceCandidates.Count} patch-matched duties loaded.");

        if (selectedInstance?.RequiresWing == true)
        {
            DrawWingSelector(selectedInstance);
        }
    }

    private static void DrawPickerFrame(string id, int itemCount)
    {
        var rowHeight = ImGui.GetTextLineHeightWithSpacing();
        var visibleRows = Math.Max(PickerVisibleRows, Math.Min(itemCount, PickerVisibleRows));
        ImGui.BeginChild(
            id,
            new Vector2(-1, (visibleRows * rowHeight) + 8),
            true);
    }

    private void DrawWingSelector(GarlandSeedInstanceCandidate instance)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted("Raid wing");
        ImGui.TextDisabled(instance.WingReason ?? "This raid needs a wing-specific slot filter.");
        foreach (var wing in RaidWings)
        {
            var selected = selectedWing == wing.Value;
            if (ImGui.RadioButton(wing.Label, selected))
            {
                selectedWing = wing.Value;
            }
        }
    }

    private void DrawSeedActionBar()
    {
        ImGui.Spacing();
        var busy = seedMutationTask is { IsCompleted: false };
        var canSeed = selectedOwnedItem is not null &&
            selectedInstance is not null &&
            !busy &&
            (selectedInstance.RequiresWing == false || !string.IsNullOrWhiteSpace(selectedWing));

        if (!canSeed)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button("Seed selected duty", new Vector2(180, 28)))
        {
            seedStatus = "Creating local seed...";
            seedMutationTask = CreateSeedAsync();
        }

        if (!canSeed)
        {
            ImGui.EndDisabled();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(string.IsNullOrWhiteSpace(seedStatus) ? "Select an item and duty to seed." : seedStatus);
    }

    private void DrawExistingSeeds()
    {
        ImGui.TextUnformatted("Local seeds");
        var seeds = plugin.GetLocalSeededDropSummaries();
        if (seeds.Count == 0)
        {
            ImGui.TextDisabled("No local seeds.");
            return;
        }

        var rowHeight = ImGui.GetTextLineHeightWithSpacing();
        var visibleRows = Math.Min(seeds.Count, MaxLocalSeedVisibleRows);
        var listHeight = ((1 + (visibleRows * 2)) * rowHeight) + 12;
        ImGui.BeginChild("local_seeds_list", new Vector2(0, listHeight), true);
        if (!ImGui.BeginTable("seeded_drop_sets", 5, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.EndChild();
            return;
        }

        ImGui.TableSetupColumn("Duty");
        ImGui.TableSetupColumn("Seed");
        ImGui.TableSetupColumn("Filter");
        ImGui.TableSetupColumn("Items");
        ImGui.TableSetupColumn("Actions");
        ImGui.TableHeadersRow();

        foreach (var seed in seeds)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextWrapped($"{seed.InstanceName} ({FormatPatch(seed.SeedPatch)})");
            ImGui.TableNextColumn();
            ImGui.TextWrapped($"{seed.SeedItemName} | IL {seed.ItemLevel}");
            ImGui.TableNextColumn();
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(seed.Wing) ? seed.SlotFilter : $"Wing {seed.Wing}: {seed.SlotFilter}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(seed.ItemCount.ToString(CultureInfo.InvariantCulture));
            ImGui.TableNextColumn();

            var busy = seedMutationTask is { IsCompleted: false };
            if (busy)
            {
                ImGui.BeginDisabled();
            }

            if (ImGui.SmallButton($"Rebuild##rebuild_{seed.GarlandInstanceId}"))
            {
                seedStatus = $"Rebuilding {seed.InstanceName}...";
                seedMutationTask = RebuildSeedAsync(seed);
            }

            ImGui.SameLine();
            if (ImGui.SmallButton($"Delete##delete_{seed.GarlandInstanceId}"))
            {
                pendingDeleteInstanceId = seed.GarlandInstanceId;
                ImGui.OpenPopup("Delete local seed?");
            }

            if (busy)
            {
                ImGui.EndDisabled();
            }
        }

        ImGui.EndTable();
        ImGui.EndChild();
    }

    private void DrawDeleteConfirmation()
    {
        if (!ImGui.BeginPopupModal("Delete local seed?", ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextWrapped("Delete this local seed? Garland data is not affected.");
        if (ImGui.Button("Delete", new Vector2(110, 26)))
        {
            if (pendingDeleteInstanceId is { } instanceId)
            {
                seedStatus = plugin.DeleteSeedForSettings(instanceId)
                    ? "Deleted local seed."
                    : "No local seed existed.";
            }

            pendingDeleteInstanceId = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(110, 26)))
        {
            pendingDeleteInstanceId = null;
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private void SelectOwnedItem(OwnedSeedItemInfo item)
    {
        selectedOwnedItem = item;
        selectedSeedItem = null;
        selectedInstance = null;
        selectedWing = null;
        instanceSearch = string.Empty;
        instanceCandidates = [];
        seedStatus = string.Empty;
        var version = ++seedSelectionVersion;
        seedMetadataLoadTask = LoadSeedMetadataAsync(item.ItemId, version);
    }

    private async Task LoadSeedMetadataAsync(uint itemId, int version)
    {
        try
        {
            var seedItem = await plugin.GetSeedItemInfoForSettingsAsync(itemId).ConfigureAwait(false);
            if (version != seedSelectionVersion)
            {
                return;
            }

            selectedSeedItem = seedItem;
            seedStatus = $"Loaded {seedItem.Name}; loading patch {FormatPatch(seedItem.Patch)} duties...";
            var candidates = await plugin.GetSeedInstanceCandidatesForSettingsAsync(seedItem.Patch).ConfigureAwait(false);
            if (version != seedSelectionVersion)
            {
                return;
            }

            instanceCandidates = candidates;
            seedStatus = $"Loaded {candidates.Count} matching duties.";
        }
        catch (Exception exception)
        {
            if (version == seedSelectionVersion)
            {
                seedStatus = $"Seed metadata failed: {exception.Message}";
            }
        }
    }

    private async Task CreateSeedAsync()
    {
        try
        {
            var seed = await plugin.CreateSeedForSettingsAsync(
                selectedOwnedItem!.ItemId,
                selectedInstance!.InstanceId,
                selectedInstance.RequiresWing ? selectedWing : null).ConfigureAwait(false);
            seedStatus = $"Seeded {seed.Items.Count} items for {seed.InstanceName}.";
        }
        catch (Exception exception)
        {
            seedStatus = $"Seed failed: {exception.Message}";
        }
    }

    private async Task RebuildSeedAsync(LocalSeededDropSummary summary)
    {
        try
        {
            var seed = await plugin.RebuildSeedForSettingsAsync(summary).ConfigureAwait(false);
            seedStatus = $"Rebuilt {seed.Items.Count} items for {seed.InstanceName}.";
        }
        catch (Exception exception)
        {
            seedStatus = $"Rebuild failed: {exception.Message}";
        }
    }

    private async Task RefreshGarlandAsync()
    {
        try
        {
            var info = await plugin.RefreshGarlandIndexForSettingsAsync().ConfigureAwait(false);
            garlandIndexStatus = info;
            garlandStatus = $"Index has {info.InstanceCount} duties; expires {info.ExpiresAtUtc:u}.";
        }
        catch (Exception exception)
        {
            garlandStatus = $"Refresh failed: {exception.Message}";
        }
    }

    private void EnsureGarlandStatusLoadStarted()
    {
        if (garlandIndexStatus is not null || garlandStatusLoadTask is { IsCompleted: false } || garlandRefreshTask is { IsCompleted: false })
        {
            return;
        }

        garlandStatusLoadTask = LoadGarlandInfoAsync();
    }

    private async Task LoadGarlandInfoAsync()
    {
        try
        {
            garlandIndexStatus = await plugin.GetGarlandIndexForSettingsAsync().ConfigureAwait(false);
            garlandStatus = "Ready";
        }
        catch (Exception exception)
        {
            garlandStatus = $"Status load failed: {exception.Message}";
        }
    }

    private void RefreshOwnedItemsIfNeeded()
    {
        if (DateTimeOffset.UtcNow - lastOwnedItemsRefresh < TimeSpan.FromSeconds(1))
        {
            return;
        }

        ownedItems = plugin.GetOwnedSeedItemsForSettings();
        lastOwnedItemsRefresh = DateTimeOffset.UtcNow;
    }

    private static bool MatchesSearch(string search, params string[] fields)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return true;
        }

        return fields.Any(field => field.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatPatch(double? patch)
    {
        return patch?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?";
    }

    private static string FormatSlot(GearSlot slot)
    {
        return slot switch
        {
            GearSlot.MainHand => "Weapon",
            GearSlot.OffHand => "Off-hand",
            GearSlot.Body => "Chest",
            GearSlot.Hands => "Hands",
            GearSlot.Ears => "Earrings",
            GearSlot.Neck => "Choker",
            GearSlot.Wrists => "Bracelet",
            _ => slot.ToString(),
        };
    }
}
