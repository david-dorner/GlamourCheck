using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace GlamourCheck.Services;

/// <summary>
/// Adds cached collection counters to the Duty Finder list and selected-duty details pane.
/// Rendering only consumes visible addon nodes and in-memory summary state; Garland/SQLite work
/// happens on framework ticks or settings actions so scrolling the Duty Finder stays responsive.
/// </summary>
public sealed class DutyFinderSummaryOverlayService : IDisposable
{
    private static readonly TimeSpan BackgroundFetchInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SummaryCacheTtl = TimeSpan.FromDays(1);
    private const int MaxConcurrentSummaryFetches = 2;

    // Duty Finder row counter tuning:
    // These two insets are relative to the native DutyList viewport, not the full
    // Duty Finder window. Increase TopClipInsetPx if counters linger above the
    // first visible duty row. Increase BottomClipInsetPx if counters linger below
    // the last visible duty row.
    // Increase CounterNameGapPx to move counters farther right from the duty name.
    // Adjust CounterVerticalOffsetPx if counters sit too high/low relative to duty text.
    // Adjust DetailCounterVerticalOffsetPx for the selected duty information pane only.
    private const float TopClipInsetPx = -6f;
    private const float BottomClipInsetPx = -6f;
    private const float FallbackTopClipOffsetPx = 195f;
    private const float FallbackBottomClipOffsetPx = 158f;
    private const float CounterNameGapPx = 18f;
    private const float CounterVerticalOffsetPx = -2f;
    private const float DetailCounterVerticalOffsetPx = 0f;
    private const int UnknownOcclusionGroupId = -1;
    private const int AlwaysOcclusionGroupId = -2;

    // HUD/status widgets can receive focus but should not cover Duty Finder counters.
    private static readonly string[] NonOccludingFocusedAddonNames =
    [
        "_ToDoList",
        "ScenarioTree",
    ];

    private static readonly string[][] OcclusionAddonGroups =
    [
        ["ContentsFinder"],
        ["Inventory", "InventoryLarge", "InventoryExpansion", "InventoryGrid0E", "InventoryGrid1E", "InventoryGrid2E", "InventoryGrid3E", "InventoryGrid0", "InventoryGrid1", "InventoryGrid"],
        ["ArmouryBoard"],
        ["InventoryBuddy", "InventoryBuddy2"],
        ["InventoryRetainer", "InventoryRetainerLarge", "RetainerGrid0", "RetainerGrid1", "RetainerGrid2", "RetainerGrid3", "RetainerGrid4"],
        ["Character"],
        ["NeedGreed"],
        ["MiragePrismPrismBoxCrystallize"],
    ];

    private static readonly string[] AlwaysOccludingAddonNames =
    [
        "ItemDetail",
        "ItemDetailCompare",
    ];

    private const int DutyFinderOcclusionGroupId = 0;

    private readonly ConfigurationService configurationService;
    private readonly IDataManager dataManager;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly GarlandDropLookupService dropLookupService;
    private readonly ICollectionRepository repository;
    private readonly CollectionState collectionState;
    private readonly LootTooltipRenderer lootTooltipRenderer;
    private readonly IPluginLog log;
    private readonly object gate = new();
    private readonly SemaphoreSlim summaryFetchSemaphore = new(MaxConcurrentSummaryFetches);
    private readonly Dictionary<string, ContentFinderConditionRow> uniqueContentByName;
    private readonly Dictionary<string, ContentFinderConditionRow> allContentByName;
    // Summary state is keyed by ContentFinderCondition ID. Item ownership is intentionally
    // recomputed from CollectionState at draw time so inventory sync updates are reflected
    // immediately without refetching Garland.
    private readonly Dictionary<uint, DutySummaryState> summaries = [];
    private readonly Dictionary<uint, GarlandLootInfo> lootInfoCache = [];
    private readonly HashSet<uint> pendingSummaryIds = [];
    private readonly HashSet<uint> pendingLootInfoIds = [];
    private readonly Dictionary<uint, ContentFinderConditionRow> observedContent = [];
    // Per-frame native window bounds. Group IDs let one logical source window, such as
    // ContentsFinder plus JournalDetail, avoid hiding its own counters.
    private readonly List<(Vector2 Min, Vector2 Max, int GroupId)> frameOccluders = [];
    private readonly Dictionary<nint, int> sourceAddonPtrToGroup = [];
    private readonly HashSet<nint> nonOccludingAddonPtrs = [];
    private int topFocusedGroupId = -1;
    private DateTimeOffset lastScan = DateTimeOffset.MinValue;
    private DateTimeOffset lastFailureLog = DateTimeOffset.MinValue;
    private DateTimeOffset lastDetailPanelScan = DateTimeOffset.MinValue;
    private nint cachedDetailPanelPtr;
    private nint cachedDetailPanelForFinderPtr;
    private static readonly TimeSpan DetailPanelScanInterval = TimeSpan.FromMilliseconds(500);
    private uint lastKnownTabIndex = uint.MaxValue;
    private uint lastKnownNumEntries = uint.MaxValue;
    private int rowCounterCooldownFrames;
    private const int RowCounterCooldownOnChange = 5;
    // Pinned popup state for the try-on workflow. Zero means the hover-only popup is active.
    private uint pinnedLootContentFinderConditionId;
    private Vector2 pinnedLootPosition;
    private uint lastHoverLootContentFinderConditionId;
    private Vector2 lastHoverLootPosition;
    private bool suppressPinnedLootOutsideClick;
    private bool previousLeftMouseDown;
    private bool frameLeftMouseDown;
    private bool frameLeftClickStarted;
    private bool disposed;

    public DutyFinderSummaryOverlayService(
        ConfigurationService configurationService,
        IDataManager dataManager,
        IFramework framework,
        IGameGui gameGui,
        GarlandDropLookupService dropLookupService,
        ICollectionRepository repository,
        CollectionState collectionState,
        LootTooltipRenderer lootTooltipRenderer,
        IPluginLog log)
    {
        this.configurationService = configurationService;
        this.dataManager = dataManager;
        this.framework = framework;
        this.gameGui = gameGui;
        this.dropLookupService = dropLookupService;
        this.repository = repository;
        this.collectionState = collectionState;
        this.lootTooltipRenderer = lootTooltipRenderer;
        this.log = log;
        uniqueContentByName = BuildUniqueContentByName(dataManager);
        allContentByName = BuildAllContentByName(dataManager);
        HydrateAllValidSummariesFromRepository();

        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        disposed = true;
        framework.Update -= OnFrameworkUpdate;
    }

    public unsafe void Draw()
    {
        if (!configurationService.Configuration.EnableInstanceSummary)
        {
            return;
        }

        try
        {
            BeginInputFrame();
            var addon = gameGui.GetAddonByName<AddonContentsFinder>("ContentsFinder");
            if (addon is not null && addon->AtkUnitBase.IsVisible)
            {
                UpdateRowCounterCooldown(addon);
                BuildFrameOccluders(&addon->AtkUnitBase);
                if (rowCounterCooldownFrames == 0)
                {
                    DrawVisibleDutyRowCounters(addon);
                }
                DrawSelectedDutyCounter(addon);
                DrawPinnedTryOnPopup();
            }
            else
            {
                pinnedLootContentFinderConditionId = 0;
            }
            EndInputFrame();
        }
        catch (Exception exception)
        {
            EndInputFrame();
            LogFailure(exception);
        }
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        if (!configurationService.Configuration.EnableInstanceSummary ||
            DateTimeOffset.UtcNow - lastScan < BackgroundFetchInterval)
        {
            return;
        }

        lastScan = DateTimeOffset.UtcNow;

        try
        {
            var cfAddon = gameGui.GetAddonByName<AddonContentsFinder>("ContentsFinder");
            if (cfAddon is null || !cfAddon->AtkUnitBase.IsVisible)
            {
                return;
            }

            ContentFinderConditionRow[] contentToFetch;
            lock (gate)
            {
                contentToFetch = observedContent.Values.ToArray();
                observedContent.Clear();
            }

            var selectedDutyId = AgentSelectedDutyId();
            if (selectedDutyId != 0 && TryGetContentById(selectedDutyId, out var selectedContent))
            {
                contentToFetch = contentToFetch
                    .Append(selectedContent)
                    .DistinctBy(content => content.ContentFinderConditionId)
                    .ToArray();
            }

            foreach (var content in contentToFetch)
            {
                QueueSummary(content);
            }
        }
        catch (Exception exception)
        {
            LogFailure(exception);
        }
    }

    private unsafe void UpdateRowCounterCooldown(AddonContentsFinder* addon)
    {
        var tab = addon->SelectedRadioButton;
        var entries = addon->NumEntries;
        var listRefreshing = addon->DutyList is not null &&
                             (addon->DutyList->LayoutRefreshPending || addon->DutyList->IsUpdatePending);

        if (tab != lastKnownTabIndex || entries != lastKnownNumEntries || listRefreshing)
        {
            lastKnownTabIndex = tab;
            lastKnownNumEntries = entries;
            rowCounterCooldownFrames = RowCounterCooldownOnChange;
        }
        else if (rowCounterCooldownFrames > 0)
        {
            rowCounterCooldownFrames--;
        }
    }

    private unsafe void DrawVisibleDutyRowCounters(AddonContentsFinder* addon)
    {
        var dutyList = addon->DutyList;
        if (dutyList is null || dutyList->Items.Count == 0)
            return;
        if (IsGroupedDutyListFullyCollapsed(dutyList))
            return;

        var clipRect = GetDutyListClipRect(&addon->AtkUnitBase, dutyList);
        var seenBands = new HashSet<int>();
        var listBase = (AtkComponentList*)dutyList;

        for (var i = 0; i < (int)dutyList->Items.Count; i++)
        {
            var item = dutyList->Items[i].Value;
            if (item is null)
                continue;

            var itemType = item->UIntValues.Count > 0
                ? (AtkComponentTreeListItemType)item->UIntValues[0]
                : AtkComponentTreeListItemType.Leaf;

            if (itemType == AtkComponentTreeListItemType.CollapsibleGroupHeader ||
                itemType == AtkComponentTreeListItemType.GroupHeader)
                continue;

            // Ask the game whether this item is fully visible in the viewport.
            // checkPartial:false ensures we skip rows that are only partially visible
            // at the top/bottom edge — those counters would be partially covered by the
            // game's UI chrome, which renders on top of our background draw list.
            if (!listBase->IsItemVisible(i, false))
                continue;

            if (item->Renderer is null || item->Renderer->OwnerNode is null)
                continue;

            if (!clipRect.ContainsNode(&item->Renderer->OwnerNode->AtkResNode))
                continue;

            var textNode = FindDutyTextNodeInRenderer(item->Renderer->OwnerNode);
            if (textNode is null || !clipRect.ContainsText(textNode))
                continue;

            var currentText = ReadText(textNode);
            if (!TryGetAnyContentByText(currentText, out var content))
                continue;

            // Cross-reference: the renderer must display this item's own authoritative
            // name. If StringValues[0] differs from what the renderer currently shows,
            // the renderer has been recycled for a different item and must be skipped.
            if (item->StringValues.Count > 0 && item->StringValues[0].HasValue)
            {
                var authoritativeName = item->StringValues[0].ToString();
                if (authoritativeName.Length > 0 &&
                    !NormalizeText(authoritativeName).Equals(NormalizeText(currentText), StringComparison.Ordinal))
                    continue;
            }

            if (!TryGetReadySummary(content.ContentFinderConditionId, out var summary))
            {
                ObserveContentForBackgroundFetch(content);
                continue;
            }

            var band = GetVisibleRowBand(textNode);
            if (!seenBands.Add(band))
                continue;

            DrawCounter(textNode, LootAwareSummary(content.ContentFinderConditionId, summary), hoverable: false, clipRect: clipRect);
        }
    }

    private unsafe AtkTextNode* FindDutyTextNodeInRenderer(AtkComponentNode* ownerNode)
    {
        if (ownerNode is null) return null;
        var rendererBase = ownerNode->Component;
        if (rendererBase is null || rendererBase->UldManager.NodeList is null) return null;

        var visited = new HashSet<nint>();
        foreach (var nodePointer in rendererBase->UldManager.Nodes)
        {
            var result = FindDutyTextNodeInSubtree(nodePointer.Value, visited);
            if (result is not null)
                return result;
        }

        return null;
    }

    private unsafe AtkTextNode* FindDutyTextNodeInSubtree(AtkResNode* node, HashSet<nint> visited)
    {
        if (node is null || !visited.Add((nint)node) || !node->IsVisible())
            return null;

        if (node->GetNodeType() == NodeType.Text)
        {
            var textNode = node->GetAsAtkTextNode();
            if (textNode is not null && node->Width >= 60f)
            {
                var text = ReadText(textNode);
                if (!string.IsNullOrWhiteSpace(text) && TryGetAnyContentByText(text, out _))
                    return textNode;
            }
        }

        if (node->GetNodeType() == NodeType.Component)
        {
            var componentNode = node->GetAsAtkComponentNode();
            var component = componentNode is null ? null : componentNode->Component;
            if (component is not null && component->UldManager.NodeList is not null)
            {
                foreach (var childPointer in component->UldManager.Nodes)
                {
                    var result = FindDutyTextNodeInSubtree(childPointer.Value, visited);
                    if (result is not null) return result;
                }
            }
        }

        var child = node->ChildNode;
        while (child is not null)
        {
            var result = FindDutyTextNodeInSubtree(child, visited);
            if (result is not null) return result;
            child = child->NextSiblingNode;
        }

        return null;
    }

    private unsafe void DrawSelectedDutyCounter(AddonContentsFinder* addon)
    {
        var selectedDutyId = AgentSelectedDutyId();
        DutySummaryState summary;
        AtkTextNode* detailNode;
        uint contentFinderConditionId;
        if (selectedDutyId != 0 &&
            TryGetReadySummary(selectedDutyId, out summary) &&
            (detailNode = FindRightPanelTitleNode(&addon->AtkUnitBase, summary.Name)) is not null)
        {
            contentFinderConditionId = selectedDutyId;
        }
        else if (TryFindRightPanelSelectedDuty(&addon->AtkUnitBase, out var content, out detailNode))
        {
            contentFinderConditionId = content.ContentFinderConditionId;
            if (!TryGetReadySummary(contentFinderConditionId, out summary))
            {
                QueueSummary(content);
                return;
            }
        }
        else
        {
            return;
        }

        ClearPinnedLootIfSelectionChanged(contentFinderConditionId);
        EnsureLootInfoQueued(contentFinderConditionId);

        GarlandLootInfo? lootInfo;
        lock (gate)
        {
            lootInfoCache.TryGetValue(contentFinderConditionId, out lootInfo);
        }

        var capturedLoot = lootInfo;
        DrawCounter(
            detailNode,
            LootAwareSummary(contentFinderConditionId, summary),
            hoverable: true,
            clipRect: null,
            tooltipOverride: capturedLoot is not null
                ? () =>
                {
                    if (pinnedLootContentFinderConditionId != contentFinderConditionId)
                    {
                        lootTooltipRenderer.DrawLootPopup(
                            "##glamcheck_dutyfinder_detail",
                            capturedLoot,
                            LootTooltipRenderer.GetAllItems(capturedLoot),
                            boundsConsumer: (min, _) =>
                            {
                                lastHoverLootContentFinderConditionId = contentFinderConditionId;
                                lastHoverLootPosition = min;
                            });
                    }
                }
                : null,
            onLeftClick: capturedLoot is not null
                ? position => PinTryOnPopup(contentFinderConditionId, position)
                : null,
            ignoreUnknownOccluders: true);
    }

    private void PinTryOnPopup(uint contentFinderConditionId, Vector2 counterPosition)
    {
        _ = counterPosition;
        pinnedLootContentFinderConditionId = contentFinderConditionId;
        pinnedLootPosition = lastHoverLootContentFinderConditionId == contentFinderConditionId
            ? lastHoverLootPosition
            : ImGui.GetMousePos();
        suppressPinnedLootOutsideClick = true;
    }

    private void ClearPinnedLootIfSelectionChanged(uint currentContentFinderConditionId)
    {
        if (pinnedLootContentFinderConditionId == 0 ||
            pinnedLootContentFinderConditionId == currentContentFinderConditionId)
        {
            return;
        }

        pinnedLootContentFinderConditionId = 0;
        suppressPinnedLootOutsideClick = false;
    }

    private void DrawPinnedTryOnPopup()
    {
        if (pinnedLootContentFinderConditionId == 0)
        {
            return;
        }

        GarlandLootInfo? lootInfo;
        lock (gate)
        {
            lootInfoCache.TryGetValue(pinnedLootContentFinderConditionId, out lootInfo);
        }

        if (lootInfo is null)
        {
            pinnedLootContentFinderConditionId = 0;
            return;
        }

        if (suppressPinnedLootOutsideClick && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            suppressPinnedLootOutsideClick = false;
        }

        if (lootTooltipRenderer.DrawLootPopup(
                "##glamcheck_dutyfinder_detail",
                lootInfo,
                LootTooltipRenderer.GetAllItems(lootInfo),
                pinnedPosition: pinnedLootPosition,
                interactive: true,
                closeOnOutsideClick: !suppressPinnedLootOutsideClick,
                dragConsumer: delta => pinnedLootPosition += delta) ||
            ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            pinnedLootContentFinderConditionId = 0;
            suppressPinnedLootOutsideClick = false;
        }
    }

    private unsafe bool TryFindRightPanelSelectedDuty(
        AtkUnitBase* baseAddon,
        out ContentFinderConditionRow content,
        out AtkTextNode* titleNode)
    {
        content = default;
        titleNode = null;
        if (baseAddon->RootNode is null)
        {
            return false;
        }

        if (TryGetJournalDetailSelectedDuty(baseAddon, out content, out titleNode))
        {
            return true;
        }

        var listRightX = baseAddon->RootNode->ScreenX + 600f;
        var candidates = new List<RightPanelTitleCandidate>();
        if (baseAddon->UldManager.NodeList is not null)
        {
            var visited = new HashSet<nint>();
            foreach (var nodePointer in baseAddon->UldManager.Nodes)
            {
                CollectRightPanelTitleCandidates(nodePointer.Value, listRightX, candidates, visited, skipInvisible: false);
            }
        }

        if (cachedDetailPanelPtr != 0 && cachedDetailPanelForFinderPtr == (nint)baseAddon)
        {
            var cached = (AtkUnitBase*)cachedDetailPanelPtr;
            if (cached->IsVisible && cached->RootNode is not null &&
                cached->UldManager.NodeList is not null &&
                cached->RootNode->ScreenX >= listRightX - 100f)
            {
                var visited = new HashSet<nint>();
                foreach (var nodePointer in cached->UldManager.Nodes)
                {
                    CollectRightPanelTitleCandidates(nodePointer.Value, float.MinValue, candidates, visited, skipInvisible: true);
                }
            }
        }
        else
        {
            var raptureManager = RaptureAtkUnitManager.Instance();
            if (raptureManager is not null)
            {
                ref var allList = ref ((AtkUnitManager*)raptureManager)->AllLoadedUnitsList;
                for (ushort i = 0; i < allList.Count; i++)
                {
                    var unit = allList.Entries[i].Value;
                    if (unit is null || !unit->IsVisible || (nint)unit == (nint)baseAddon ||
                        unit->RootNode is null || unit->UldManager.NodeList is null ||
                        unit->RootNode->ScreenX < listRightX - 100f)
                    {
                        continue;
                    }

                    var visited = new HashSet<nint>();
                    var candidateCountBefore = candidates.Count;
                    foreach (var nodePointer in unit->UldManager.Nodes)
                    {
                        CollectRightPanelTitleCandidates(nodePointer.Value, float.MinValue, candidates, visited, skipInvisible: true);
                    }

                    if (candidates.Count > candidateCountBefore &&
                        LooksLikeDutyFinderDetailPanelPositional(unit, baseAddon))
                    {
                        cachedDetailPanelPtr = (nint)unit;
                        cachedDetailPanelForFinderPtr = (nint)baseAddon;
                        lastDetailPanelScan = DateTimeOffset.UtcNow;
                    }
                }
            }
        }

        var best = candidates
            .OrderByDescending(candidate => candidate.FontSize)
            .ThenBy(candidate => candidate.ScreenY)
            .ThenByDescending(candidate => candidate.ScreenX)
            .FirstOrDefault();
        if (best.TextNodePtr == 0)
        {
            return false;
        }

        titleNode = (AtkTextNode*)best.TextNodePtr;
        content = best.Content;
        return true;
    }

    private unsafe void CollectRightPanelTitleCandidates(
        AtkResNode* node,
        float minX,
        ICollection<RightPanelTitleCandidate> candidates,
        HashSet<nint> visited,
        bool skipInvisible)
    {
        if (node is null || !visited.Add((nint)node))
        {
            return;
        }

        if (skipInvisible && !node->IsVisible())
        {
            return;
        }

        if (node->GetNodeType() == NodeType.Text && node->ScreenX > minX)
        {
            var textNode = node->GetAsAtkTextNode();
            var text = ReadText(textNode);
            if (textNode is not null &&
                textNode->AtkResNode.IsVisible() &&
                textNode->FontSize >= 16 &&
                TryGetAnyContentByText(text, out var content))
            {
                candidates.Add(new RightPanelTitleCandidate(
                    (nint)textNode,
                    content,
                    textNode->FontSize,
                    node->ScreenX,
                    node->ScreenY));
            }
        }

        if (node->GetNodeType() == NodeType.Component)
        {
            var componentNode = node->GetAsAtkComponentNode();
            var component = componentNode is null ? null : componentNode->Component;
            if (component is not null && component->UldManager.NodeList is not null)
            {
                foreach (var childPointer in component->UldManager.Nodes)
                {
                    CollectRightPanelTitleCandidates(childPointer.Value, minX, candidates, visited, skipInvisible);
                }
            }
        }

        var child = node->ChildNode;
        while (child is not null)
        {
            CollectRightPanelTitleCandidates(child, minX, candidates, visited, skipInvisible);
            child = child->NextSiblingNode;
        }
    }

    private unsafe AtkTextNode* FindRightPanelTitleNode(AtkUnitBase* baseAddon, string targetName)
    {
        if (baseAddon->RootNode is null)
        {
            return null;
        }

        if (TryGetJournalDetailTitleNode(baseAddon, targetName, out var journalDetailTitleNode))
        {
            return journalDetailTitleNode;
        }

        var normalizedTarget = NormalizeText(targetName);
        var listRightX = baseAddon->RootNode->ScreenX + 600f;
        AtkTextNode* bestNode = null;
        float bestY = float.MaxValue;

        // Pass 1: walk the addon tree.
        // Skip the IsVisible guard on intermediate nodes — the right-panel component
        // can have a different visibility path and we must not prune those branches.
        if (baseAddon->UldManager.NodeList is not null)
        {
            var visited = new HashSet<nint>();
            foreach (var nodePointer in baseAddon->UldManager.Nodes)
            {
                SearchTitleNodeInTree(nodePointer.Value, normalizedTarget, listRightX, ref bestNode, ref bestY, visited, skipInvisible: false);
            }
        }

        if (bestNode is not null)
        {
            return bestNode;
        }

        // Pass 2: the right detail panel may be a sibling addon anchored next to
        // ContentsFinder / RaidFinder. Use the cached panel ptr when available to
        // avoid iterating AllLoadedUnitsList every frame.
        if (cachedDetailPanelPtr != 0 && cachedDetailPanelForFinderPtr == (nint)baseAddon)
        {
            var cached = (AtkUnitBase*)cachedDetailPanelPtr;
            if (cached->IsVisible && cached->UldManager.NodeList is not null)
            {
                var visited2 = new HashSet<nint>();
                foreach (var nodePointer in cached->UldManager.Nodes)
                {
                    SearchTitleNodeInTree(nodePointer.Value, normalizedTarget, float.MinValue, ref bestNode, ref bestY, visited2, skipInvisible: true);
                }
            }

            return bestNode;
        }

        // Slow path: full scan (only taken when cache is cold, i.e., first open or after refresh).
        var raptureManager = RaptureAtkUnitManager.Instance();
        if (raptureManager is null)
        {
            return null;
        }

        ref var allList = ref ((AtkUnitManager*)raptureManager)->AllLoadedUnitsList;
        for (ushort i = 0; i < allList.Count; i++)
        {
            var unit = allList.Entries[i].Value;
            if (unit is null || !unit->IsVisible || (nint)unit == (nint)baseAddon ||
                unit->RootNode is null || unit->UldManager.NodeList is null ||
                unit->RootNode->ScreenX < listRightX - 100f)
            {
                continue;
            }

            var visited2 = new HashSet<nint>();
            foreach (var nodePointer in unit->UldManager.Nodes)
            {
                SearchTitleNodeInTree(nodePointer.Value, normalizedTarget, float.MinValue, ref bestNode, ref bestY, visited2, skipInvisible: true);
            }

            if (bestNode is not null)
            {
                cachedDetailPanelPtr = (nint)unit;
                cachedDetailPanelForFinderPtr = (nint)baseAddon;
                lastDetailPanelScan = DateTimeOffset.UtcNow;
                return bestNode;
            }
        }

        return null;
    }

    private unsafe void SearchTitleNodeInTree(
        AtkResNode* node,
        string normalizedTargetName,
        float minX,
        ref AtkTextNode* bestNode,
        ref float bestY,
        HashSet<nint> visited,
        bool skipInvisible)
    {
        if (node is null || !visited.Add((nint)node))
        {
            return;
        }

        if (skipInvisible && !node->IsVisible())
        {
            return;
        }

        if (node->GetNodeType() == NodeType.Text && node->ScreenX > minX)
        {
            var textNode = node->GetAsAtkTextNode();
            if (textNode is not null && textNode->AtkResNode.IsVisible())
            {
                var text = ReadText(textNode);
                // Prefer the topmost match: the duty title is always at the top of the
                // detail panel; location-field text nodes (e.g. "Location: The Dead Ends")
                // appear lower and must not win over the real title.
                if (!string.IsNullOrWhiteSpace(text) &&
                    NormalizeText(text) == normalizedTargetName &&
                    node->ScreenY < bestY)
                {
                    bestNode = textNode;
                    bestY = node->ScreenY;
                }
            }
        }

        if (node->GetNodeType() == NodeType.Component)
        {
            var componentNode = node->GetAsAtkComponentNode();
            var component = componentNode is null ? null : componentNode->Component;
            if (component is not null && component->UldManager.NodeList is not null)
            {
                foreach (var childPointer in component->UldManager.Nodes)
                {
                    SearchTitleNodeInTree(childPointer.Value, normalizedTargetName, minX, ref bestNode, ref bestY, visited, skipInvisible);
                }
            }
        }

        var child = node->ChildNode;
        while (child is not null)
        {
            SearchTitleNodeInTree(child, normalizedTargetName, minX, ref bestNode, ref bestY, visited, skipInvisible);
            child = child->NextSiblingNode;
        }
    }

    private void ObserveContentForBackgroundFetch(ContentFinderConditionRow content)
    {
        lock (gate)
        {
            if (!summaries.TryGetValue(content.ContentFinderConditionId, out var summary) ||
                summary.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                observedContent[content.ContentFinderConditionId] = content;
            }
        }
    }

    private unsafe void DrawCounter(
        AtkTextNode* textNode,
        CurrentDutySummary summary,
        bool hoverable,
        ClipRect? clipRect,
        System.Action? tooltipOverride = null,
        Action<Vector2>? onLeftClick = null,
        bool ignoreUnknownOccluders = false)
    {
        var text = ReadText(textNode);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var counterText = $"{summary.OwnedCount.ToString(CultureInfo.InvariantCulture)}/{summary.TotalCount.ToString(CultureInfo.InvariantCulture)}";
        var textSize = GetTextDrawSize(textNode, text);
        var textWidth = textSize.X;
        var node = &textNode->AtkResNode;
        var x = GetTextStartX(textNode, textWidth) + textWidth + CounterNameGapPx;
        var nodeHeight = node->Height * GetCumulativeScaleY(node);
        var verticalOffset = hoverable ? DetailCounterVerticalOffsetPx : CounterVerticalOffsetPx;
        var anchorHeight = hoverable ? textSize.Y : nodeHeight;
        var counterScale = Math.Clamp(anchorHeight / Math.Max(1f, ImGui.GetTextLineHeight()), 0.6f, 2.5f) * 0.55f;
        var counterSize = ImGui.CalcTextSize(counterText) * counterScale;
        var y = node->ScreenY + Math.Max(0, (anchorHeight - counterSize.Y) / 2f) + verticalOffset;
        var counterPosition = new Vector2(x, y);
        var padding = new Vector2(4, 2) * counterScale;
        var counterMin = counterPosition - padding;
        var counterMax = counterPosition + counterSize + padding;
        if (clipRect is { } rect && !rect.Contains(counterMin, counterMax))
        {
            return;
        }

        if (IsCounterOccluded(counterMin, counterMax, DutyFinderOcclusionGroupId, ignoreUnknownOccluders))
        {
            return;
        }

        var drawList = ImGui.GetBackgroundDrawList();
        var counterBackgroundColor = summary.TotalCount > 0 && summary.OwnedCount >= summary.TotalCount
            ? 0xB0287F46u
            : 0xB0000000u;
        var counterBorderColor = summary.TotalCount > 0 && summary.OwnedCount >= summary.TotalCount
            ? 0xB07DFF9Fu
            : 0x90FFFFFFu;
        drawList.AddRectFilled(counterMin, counterMax, counterBackgroundColor, 4f * counterScale);
        drawList.AddRect(counterMin, counterMax, counterBorderColor, 4f * counterScale);
        drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize() * counterScale, counterPosition, 0xFFFFFFFF, counterText);

        var counterHovered = hoverable && ImGui.IsMouseHoveringRect(counterMin, counterMax, false);
        if (counterHovered && onLeftClick is not null && frameLeftClickStarted)
        {
            onLeftClick(counterMin);
        }

        if (counterHovered)
        {
            if (tooltipOverride is not null)
            {
                tooltipOverride();
            }
            else
            {
                ImGui.SetNextWindowPos(ImGui.GetMousePos(), ImGuiCond.Always, new Vector2(1f, 0f));
                ImGui.SetNextWindowBgAlpha(0.96f);
                ImGui.Begin(
                    "##glamcheck_dutyfinder_summary",
                    ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoNav |
                    ImGuiWindowFlags.NoMove);
                ImGui.TextUnformatted($"{summary.Name}  {counterText}");
                ImGui.TextDisabled("Loot data loading...");
                ImGui.End();
            }
        }
    }

    private void BeginInputFrame()
    {
        frameLeftMouseDown = IsLeftMouseDown();
        frameLeftClickStarted = frameLeftMouseDown && !previousLeftMouseDown;
    }

    private void EndInputFrame()
    {
        previousLeftMouseDown = frameLeftMouseDown;
    }

    private bool IsLeftMouseDown()
    {
        return ImGui.IsMouseDown(ImGuiMouseButton.Left) ||
            (GetAsyncKeyState(0x01) & 0x8000) != 0;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    private unsafe void BuildFrameOccluders(AtkUnitBase* dutyFinderAddon)
    {
        frameOccluders.Clear();
        sourceAddonPtrToGroup.Clear();
        nonOccludingAddonPtrs.Clear();

        foreach (var addonName in NonOccludingFocusedAddonNames)
        {
            var ptr = (nint)gameGui.GetAddonByName<AtkUnitBase>(addonName);
            if (ptr != 0)
            {
                nonOccludingAddonPtrs.Add(ptr);
            }
        }

        for (var groupId = 0; groupId < OcclusionAddonGroups.Length; groupId++)
        {
            foreach (var addonName in OcclusionAddonGroups[groupId])
            {
                var ptr = (nint)gameGui.GetAddonByName<AtkUnitBase>(addonName);
                if (ptr != 0)
                {
                    sourceAddonPtrToGroup[ptr] = groupId;
                }
            }
        }

        foreach (var addonName in AlwaysOccludingAddonNames)
        {
            var unit = gameGui.GetAddonByName<AtkUnitBase>(addonName);
            if (unit is not null && unit->IsVisible)
            {
                AddOccluderRect(unit, AlwaysOcclusionGroupId);
            }
        }

        topFocusedGroupId = -1;
        var manager = RaptureAtkUnitManager.Instance();
        if (manager is not null)
        {
            // Classify the detail panel using a cached pointer — the full node-tree
            // scan is expensive and must not run every frame.
            RefreshCachedDetailPanel(manager, dutyFinderAddon);
            TryRegisterJournalDetailPanel(dutyFinderAddon);
            if (cachedDetailPanelPtr != 0 && !sourceAddonPtrToGroup.ContainsKey(cachedDetailPanelPtr))
            {
                sourceAddonPtrToGroup[cachedDetailPanelPtr] = DutyFinderOcclusionGroupId;
            }

            var focusedAddon = ((AtkUnitManager*)manager)->FocusedAddon;
            if (focusedAddon is not null && !nonOccludingAddonPtrs.Contains((nint)focusedAddon))
            {
                if (!sourceAddonPtrToGroup.TryGetValue((nint)focusedAddon, out var focusedGroupId))
                {
                    // Before treating this as a blocking occluder, check whether it's the
                    // detail panel that hasn't been cached yet (e.g., duty just selected,
                    // or rate-limited scan ran before the title text appeared).
                    // Scanning a single addon is cheap enough to do on demand.
                    if (TryClassifyAsDetailPanel(focusedAddon, dutyFinderAddon))
                    {
                        focusedGroupId = DutyFinderOcclusionGroupId;
                        sourceAddonPtrToGroup[(nint)focusedAddon] = DutyFinderOcclusionGroupId;
                    }
                    else if (IsInsideCachedDetailPanel(focusedAddon, dutyFinderAddon))
                    {
                        focusedGroupId = DutyFinderOcclusionGroupId;
                        sourceAddonPtrToGroup[(nint)focusedAddon] = DutyFinderOcclusionGroupId;
                    }
                    else if (LooksLikeDutyFinderDetailPanelPositional(focusedAddon, dutyFinderAddon))
                    {
                        // Positional match but the title-text scan failed (text not yet
                        // rendered, or duty name doesn't map to a known content entry).
                        // Treat as same group so the counter stays visible; don't add as
                        // a blocker or cache the pointer until the full scan succeeds.
                        focusedGroupId = DutyFinderOcclusionGroupId;
                    }
                    else
                    {
                        AddOccluderRect(focusedAddon, UnknownOcclusionGroupId);
                        focusedGroupId = UnknownOcclusionGroupId;
                    }
                }

                topFocusedGroupId = focusedGroupId;
            }

            // FocusedUnitsList contains the full focus chain (parent addons included).
            // Only update topFocusedGroupId for recognized addons; unknown ancestors in
            // the chain are NOT added as -1 blockers — only the primary FocusedAddon above
            // gets that treatment. This prevents container/ancestor addons from randomly
            // blocking the counter when the user clicks in the detail panel.
            ref var focusedList = ref ((AtkUnitManager*)manager)->FocusedUnitsList;
            for (ushort i = 0; i < focusedList.Count; i++)
            {
                var unit = focusedList.Entries[i].Value;
                if (unit is null || !unit->IsVisible || nonOccludingAddonPtrs.Contains((nint)unit))
                {
                    continue;
                }

                if (sourceAddonPtrToGroup.TryGetValue((nint)unit, out var focusedGroupId))
                {
                    topFocusedGroupId = focusedGroupId;
                }
                else if (TryClassifyAsDetailPanel(unit, dutyFinderAddon))
                {
                    sourceAddonPtrToGroup[(nint)unit] = DutyFinderOcclusionGroupId;
                    topFocusedGroupId = DutyFinderOcclusionGroupId;
                }
                else if (IsInsideCachedDetailPanel(unit, dutyFinderAddon))
                {
                    sourceAddonPtrToGroup[(nint)unit] = DutyFinderOcclusionGroupId;
                    topFocusedGroupId = DutyFinderOcclusionGroupId;
                }
                else if (LooksLikeDutyFinderDetailPanelPositional(unit, dutyFinderAddon))
                {
                    topFocusedGroupId = DutyFinderOcclusionGroupId;
                }
                // Unknown ancestors: skip without blocking.
            }
        }

        foreach (var (addonPtr, groupId) in sourceAddonPtrToGroup)
        {
            var unit = (AtkUnitBase*)addonPtr;
            if (unit->IsVisible)
            {
                AddOccluderRect(unit, groupId);
            }
        }
    }

    private unsafe bool TryRegisterJournalDetailPanel(AtkUnitBase* dutyFinderAddon)
    {
        var journalDetail = gameGui.GetAddonByName<AddonJournalDetail>("JournalDetail");
        if (journalDetail is null || !journalDetail->AtkUnitBase.IsVisible ||
            !IsJournalDetailForDutyFinder(journalDetail, dutyFinderAddon))
        {
            return false;
        }

        var ptr = (nint)journalDetail;
        sourceAddonPtrToGroup[ptr] = DutyFinderOcclusionGroupId;
        cachedDetailPanelPtr = ptr;
        cachedDetailPanelForFinderPtr = (nint)dutyFinderAddon;
        lastDetailPanelScan = DateTimeOffset.UtcNow;
        return true;
    }

    private unsafe bool TryGetJournalDetailSelectedDuty(
        AtkUnitBase* dutyFinderAddon,
        out ContentFinderConditionRow content,
        out AtkTextNode* titleNode)
    {
        content = default;
        titleNode = null;

        var journalDetail = gameGui.GetAddonByName<AddonJournalDetail>("JournalDetail");
        if (journalDetail is null || !journalDetail->AtkUnitBase.IsVisible ||
            !IsJournalDetailForDutyFinder(journalDetail, dutyFinderAddon))
        {
            return false;
        }

        var dutyNameNode = journalDetail->DutyNameTextNode;
        var dutyName = ReadText(dutyNameNode);
        if (dutyNameNode is null || !dutyNameNode->AtkResNode.IsVisible() ||
            !TryGetAnyContentByText(dutyName, out content))
        {
            return false;
        }

        titleNode = dutyNameNode;
        return true;
    }

    private unsafe bool TryGetJournalDetailTitleNode(
        AtkUnitBase* dutyFinderAddon,
        string targetName,
        out AtkTextNode* titleNode)
    {
        titleNode = null;

        var journalDetail = gameGui.GetAddonByName<AddonJournalDetail>("JournalDetail");
        if (journalDetail is null || !journalDetail->AtkUnitBase.IsVisible ||
            !IsJournalDetailForDutyFinder(journalDetail, dutyFinderAddon))
        {
            return false;
        }

        var dutyNameNode = journalDetail->DutyNameTextNode;
        if (dutyNameNode is null || !dutyNameNode->AtkResNode.IsVisible() ||
            NormalizeText(ReadText(dutyNameNode)) != NormalizeText(targetName))
        {
            return false;
        }

        titleNode = dutyNameNode;
        return true;
    }

    private unsafe bool IsJournalDetailForDutyFinder(AddonJournalDetail* journalDetail, AtkUnitBase* dutyFinderAddon)
    {
        if (journalDetail is null ||
            dutyFinderAddon is null ||
            !journalDetail->AtkUnitBase.IsVisible ||
            journalDetail->DutyNameTextNode is null ||
            !journalDetail->DutyNameTextNode->AtkResNode.IsVisible())
        {
            return false;
        }

        var dutyName = ReadText(journalDetail->DutyNameTextNode);
        if (string.IsNullOrWhiteSpace(dutyName) ||
            !TryGetAnyContentByText(dutyName, out var content))
        {
            return false;
        }

        var selectedDutyId = AgentSelectedDutyId();
        if (selectedDutyId != 0 && content.ContentFinderConditionId == selectedDutyId)
        {
            return true;
        }

        return LooksLikeDutyFinderDetailPanelPositional((AtkUnitBase*)journalDetail, dutyFinderAddon);
    }

    private unsafe bool LooksLikeDutyFinderDetailPanel(AtkUnitBase* unit, AtkUnitBase* dutyFinderAddon)
    {
        if (unit is null ||
            dutyFinderAddon is null ||
            unit == dutyFinderAddon ||
            unit->RootNode is null ||
            dutyFinderAddon->RootNode is null ||
            unit->UldManager.NodeList is null)
        {
            return false;
        }

        var dutyRoot = dutyFinderAddon->RootNode;
        var unitRoot = unit->RootNode;
        var dutyMaxX = dutyRoot->ScreenX + (dutyRoot->Width * GetCumulativeScaleX(dutyRoot));
        if (unitRoot->ScreenX < dutyMaxX - 60f ||
            Math.Abs(unitRoot->ScreenY - dutyRoot->ScreenY) > 120f ||
            unitRoot->Width < 250 ||
            unitRoot->Height < 250)
        {
            return false;
        }

        var visited = new HashSet<nint>();
        foreach (var nodePointer in unit->UldManager.Nodes)
        {
            if (ContainsDutyTitleText(nodePointer.Value, visited))
            {
                return true;
            }
        }

        return false;
    }

    private unsafe void RefreshCachedDetailPanel(RaptureAtkUnitManager* manager, AtkUnitBase* dutyFinderAddon)
    {
        // Fast path: validate existing cache with a positional check (no node scan).
        if (cachedDetailPanelPtr != 0 && cachedDetailPanelForFinderPtr == (nint)dutyFinderAddon)
        {
            var cached = (AtkUnitBase*)cachedDetailPanelPtr;
            if (cached->IsVisible && LooksLikeDutyFinderDetailPanelPositional(cached, dutyFinderAddon))
            {
                return;
            }
        }

        cachedDetailPanelPtr = 0;
        cachedDetailPanelForFinderPtr = 0;

        // Rate-limit the expensive full scan.
        if (DateTimeOffset.UtcNow - lastDetailPanelScan < DetailPanelScanInterval)
        {
            return;
        }

        lastDetailPanelScan = DateTimeOffset.UtcNow;
        ref var allList = ref ((AtkUnitManager*)manager)->AllLoadedUnitsList;
        for (ushort i = 0; i < allList.Count; i++)
        {
            var unit = allList.Entries[i].Value;
            if (unit is not null && unit->IsVisible &&
                (nint)unit != (nint)dutyFinderAddon &&
                LooksLikeDutyFinderDetailPanel(unit, dutyFinderAddon))
            {
                cachedDetailPanelPtr = (nint)unit;
                cachedDetailPanelForFinderPtr = (nint)dutyFinderAddon;
                return;
            }
        }
    }

    private static unsafe bool LooksLikeDutyFinderDetailPanelPositional(AtkUnitBase* unit, AtkUnitBase* dutyFinderAddon)
    {
        if (unit is null || dutyFinderAddon is null || unit->RootNode is null || dutyFinderAddon->RootNode is null)
        {
            return false;
        }

        var dutyRoot = dutyFinderAddon->RootNode;
        var unitRoot = unit->RootNode;
        var dutyMaxX = dutyRoot->ScreenX + (dutyRoot->Width * GetCumulativeScaleX(dutyRoot));
        return unitRoot->ScreenX >= dutyMaxX - 60f &&
               Math.Abs(unitRoot->ScreenY - dutyRoot->ScreenY) <= 120f &&
               unitRoot->Width >= 250 &&
               unitRoot->Height >= 250;
    }

    private unsafe bool TryClassifyAsDetailPanel(AtkUnitBase* unit, AtkUnitBase* dutyFinderAddon)
    {
        if (!LooksLikeDutyFinderDetailPanelPositional(unit, dutyFinderAddon) ||
            !LooksLikeDutyFinderDetailPanel(unit, dutyFinderAddon))
        {
            return false;
        }

        cachedDetailPanelPtr = (nint)unit;
        cachedDetailPanelForFinderPtr = (nint)dutyFinderAddon;
        lastDetailPanelScan = DateTimeOffset.UtcNow;
        return true;
    }

    private unsafe bool IsInsideCachedDetailPanel(AtkUnitBase* unit, AtkUnitBase* dutyFinderAddon)
    {
        if (unit is null ||
            dutyFinderAddon is null ||
            cachedDetailPanelPtr == 0 ||
            cachedDetailPanelForFinderPtr != (nint)dutyFinderAddon ||
            (nint)unit == (nint)dutyFinderAddon ||
            (nint)unit == cachedDetailPanelPtr)
        {
            return false;
        }

        var detailPanel = (AtkUnitBase*)cachedDetailPanelPtr;
        if (!detailPanel->IsVisible || !LooksLikeDutyFinderDetailPanelPositional(detailPanel, dutyFinderAddon))
        {
            return false;
        }

        if (!TryGetUnitRect(unit, out var unitMin, out var unitMax) ||
            !TryGetUnitRect(detailPanel, out var panelMin, out var panelMax))
        {
            return false;
        }

        var unitCenter = (unitMin + unitMax) / 2f;
        var centerInsidePanel =
            unitCenter.X >= panelMin.X &&
            unitCenter.X <= panelMax.X &&
            unitCenter.Y >= panelMin.Y &&
            unitCenter.Y <= panelMax.Y;
        if (centerInsidePanel)
        {
            return true;
        }

        var overlapMin = Vector2.Max(unitMin, panelMin);
        var overlapMax = Vector2.Min(unitMax, panelMax);
        if (overlapMax.X <= overlapMin.X || overlapMax.Y <= overlapMin.Y)
        {
            return false;
        }

        var overlapArea = (overlapMax.X - overlapMin.X) * (overlapMax.Y - overlapMin.Y);
        var unitArea = Math.Max(1f, (unitMax.X - unitMin.X) * (unitMax.Y - unitMin.Y));
        return overlapArea / unitArea >= 0.75f;
    }

    private unsafe bool ContainsDutyTitleText(AtkResNode* node, HashSet<nint> visited)
    {
        if (node is null || !visited.Add((nint)node) || !node->IsVisible())
        {
            return false;
        }

        if (node->GetNodeType() == NodeType.Text)
        {
            var textNode = node->GetAsAtkTextNode();
            if (textNode is not null &&
                textNode->FontSize >= 16 &&
                TryGetAnyContentByText(ReadText(textNode), out _))
            {
                return true;
            }
        }

        if (node->GetNodeType() == NodeType.Component)
        {
            var componentNode = node->GetAsAtkComponentNode();
            var component = componentNode is null ? null : componentNode->Component;
            if (component is not null && component->UldManager.NodeList is not null)
            {
                foreach (var childPointer in component->UldManager.Nodes)
                {
                    if (ContainsDutyTitleText(childPointer.Value, visited))
                    {
                        return true;
                    }
                }
            }
        }

        var child = node->ChildNode;
        while (child is not null)
        {
            if (ContainsDutyTitleText(child, visited))
            {
                return true;
            }

            child = child->NextSiblingNode;
        }

        return false;
    }

    private unsafe void AddOccluderRect(AtkUnitBase* unit, int groupId)
    {
        if (!TryGetUnitRect(unit, out var min, out var max))
        {
            return;
        }

        frameOccluders.Add((min, max, groupId));
    }

    private static unsafe bool TryGetUnitRect(AtkUnitBase* unit, out Vector2 min, out Vector2 max)
    {
        min = default;
        max = default;
        if (unit is null || !unit->IsVisible)
        {
            return false;
        }

        var rootNode = unit->RootNode;
        if (rootNode is null)
        {
            return false;
        }

        min = new Vector2(rootNode->ScreenX, rootNode->ScreenY);
        max = new Vector2(
            rootNode->ScreenX + (rootNode->Width * GetCumulativeScaleX(rootNode)),
            rootNode->ScreenY + (rootNode->Height * GetCumulativeScaleY(rootNode)));
        return max.X > min.X && max.Y > min.Y;
    }

    private bool IsCounterOccluded(Vector2 counterMin, Vector2 counterMax, int hostGroupId, bool ignoreUnknownOccluders = false)
    {
        foreach (var (min, max, groupId) in frameOccluders)
        {
            if (ignoreUnknownOccluders && groupId == UnknownOcclusionGroupId)
            {
                continue;
            }

            if (groupId == hostGroupId)
            {
                continue;
            }

            if (hostGroupId == topFocusedGroupId && groupId >= 0)
            {
                continue;
            }

            if (counterMin.X < max.X &&
                counterMax.X > min.X &&
                counterMin.Y < max.Y &&
                counterMax.Y > min.Y)
            {
                return true;
            }
        }

        return false;
    }

    private static unsafe int GetVisibleRowBand(AtkTextNode* textNode)
    {
        var node = &textNode->AtkResNode;
        var centerY = node->ScreenY + (node->Height * GetCumulativeScaleY(node) / 2f);
        return (int)MathF.Round(centerY / 4f);
    }

    private static unsafe bool IsGroupedDutyListFullyCollapsed(AtkComponentTreeList* dutyList)
    {
        if (dutyList is null || dutyList->Items.Count == 0)
        {
            return false;
        }

        var collapsibleHeaderCount = 0;
        var expandedHeaderCount = 0;
        for (var index = 0; index < dutyList->Items.Count; index++)
        {
            var item = dutyList->Items[index].Value;
            if (item is null || item->UIntValues.Count == 0)
            {
                continue;
            }

            if ((AtkComponentTreeListItemType)item->UIntValues[0] != AtkComponentTreeListItemType.CollapsibleGroupHeader)
            {
                continue;
            }

            collapsibleHeaderCount++;
            if (IsTreeListGroupExpanded(item))
            {
                expandedHeaderCount++;
            }
        }

        return collapsibleHeaderCount > 0 && expandedHeaderCount == 0;
    }

    private static unsafe bool IsTreeListGroupExpanded(AtkComponentTreeListItem* item)
    {
        // FFXIVClientStructs currently leaves these header flags unnamed; local comments
        // identify byte 0x42 bit 0 as the expanded state for tree-list groups.
        return (((byte*)item)[0x42] & 1) != 0;
    }

    private bool TryGetContentByText(string text, out ContentFinderConditionRow content)
    {
        return uniqueContentByName.TryGetValue(NormalizeText(text), out content);
    }

    private bool TryGetAnyContentByText(string text, out ContentFinderConditionRow content)
    {
        var key = NormalizeText(text);
        return uniqueContentByName.TryGetValue(key, out content) ||
               allContentByName.TryGetValue(key, out content);
    }

    private bool TryGetContentById(uint contentFinderConditionId, out ContentFinderConditionRow content)
    {
        var sheet = dataManager.GetExcelSheet<ContentFinderCondition>();
        if (sheet.TryGetRow(contentFinderConditionId, out var row))
        {
            var name = row.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
            {
                content = new ContentFinderConditionRow(contentFinderConditionId, name);
                return true;
            }
        }

        content = default;
        return false;
    }

    private CurrentDutySummary LootAwareSummary(uint cfcId, DutySummaryState summary)
    {
        GarlandLootInfo? lootInfo;
        lock (gate) { lootInfoCache.TryGetValue(cfcId, out lootInfo); }
        if (lootInfo is null)
        {
            return summary.ToCurrent(collectionState);
        }

        var allItems = LootTooltipRenderer.GetAllItems(lootInfo);
        var (owned, total) = lootTooltipRenderer.GetItemCounts(allItems);
        return new CurrentDutySummary(summary.Name, owned, total);
    }

    private bool TryGetReadySummary(uint contentFinderConditionId, out DutySummaryState summary)
    {
        lock (gate)
        {
            if (summaries.TryGetValue(contentFinderConditionId, out var cachedSummary) &&
                cachedSummary.ItemIds.Length > 0 &&
                string.IsNullOrWhiteSpace(cachedSummary.Error))
            {
                summary = cachedSummary;
                return true;
            }
        }

        summary = null!;
        return false;
    }

    private void QueueSummary(ContentFinderConditionRow content)
    {
        var cfcId = content.ContentFinderConditionId;

        bool summaryReady;
        lock (gate)
        {
            summaryReady = summaries.TryGetValue(cfcId, out var s) && s.ExpiresAtUtc > DateTimeOffset.UtcNow;
        }

        if (summaryReady)
        {
            EnsureLootInfoQueued(cfcId);
            return;
        }

        if (TryHydrateSummaryFromRepository(content))
        {
            EnsureLootInfoQueued(cfcId);
            return;
        }

        lock (gate)
        {
            if ((summaries.TryGetValue(cfcId, out var summary) &&
                    summary.ExpiresAtUtc > DateTimeOffset.UtcNow) ||
                !pendingSummaryIds.Add(cfcId))
            {
                return;
            }
        }

        _ = RefreshSummaryAsync(content);
    }

    private void EnsureLootInfoQueued(uint cfcId)
    {
        uint? garlandInstanceId;
        lock (gate)
        {
            if (lootInfoCache.ContainsKey(cfcId) || pendingLootInfoIds.Contains(cfcId))
            {
                return;
            }

            summaries.TryGetValue(cfcId, out var s);
            garlandInstanceId = s?.GarlandInstanceId;
            if (garlandInstanceId is not null)
            {
                pendingLootInfoIds.Add(cfcId);
            }
        }

        if (garlandInstanceId is { } instanceId)
        {
            _ = FetchAndCacheLootInfoAsync(cfcId, instanceId);
        }
    }

    private async Task FetchAndCacheLootInfoAsync(uint cfcId, uint garlandInstanceId)
    {
        try
        {
            var loot = await dropLookupService.GetLootInfoAsync(garlandInstanceId).ConfigureAwait(false);
            if (disposed)
            {
                return;
            }

            lock (gate)
            {
                lootInfoCache[cfcId] = loot;
            }
        }
        catch (Exception exception)
        {
            LogFailure(exception);
        }
        finally
        {
            lock (gate)
            {
                pendingLootInfoIds.Remove(cfcId);
            }
        }
    }

    public void InvalidateGarlandInstance(uint garlandInstanceId)
    {
        lock (gate)
        {
            var summaryIds = summaries
                .Where(pair => pair.Value.GarlandInstanceId == garlandInstanceId)
                .Select(pair => pair.Key)
                .ToArray();

            foreach (var summaryId in summaryIds)
            {
                summaries.Remove(summaryId);
                lootInfoCache.Remove(summaryId);
                pendingSummaryIds.Remove(summaryId);
                pendingLootInfoIds.Remove(summaryId);
                observedContent.Remove(summaryId);
            }
        }

        repository.DeleteInstanceDropCacheEntriesForGarlandInstance(garlandInstanceId);
    }

    private async Task RefreshSummaryAsync(ContentFinderConditionRow content)
    {
        await summaryFetchSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (TryHydrateSummaryFromRepository(content))
            {
                EnsureLootInfoQueued(content.ContentFinderConditionId);
                return;
            }

            var matchResult = await dropLookupService.FindInstanceIndexMatchesAsync(content.Name).ConfigureAwait(false);
            if (disposed)
            {
                return;
            }

            if (matchResult.Matches.Count != 1)
            {
                SetSummary(content, null, [], $"Garland matches={matchResult.Matches.Count}", persist: true);
                return;
            }

            var loot = await dropLookupService.GetLootInfoAsync(matchResult.Matches[0].InstanceId).ConfigureAwait(false);
            if (disposed)
            {
                return;
            }

            lock (gate)
            {
                lootInfoCache[content.ContentFinderConditionId] = loot;
            }

            var items = loot.GarlandItems
                .Concat(loot.MatchedItems)
                .Concat(loot.SeededItems)
                .DistinctBy(item => item.ItemId)
                .ToArray();
            SetSummary(
                content,
                matchResult.Matches[0].InstanceId,
                items.Select(item => item.ItemId).ToArray(),
                string.Empty,
                persist: true);
        }
        catch (Exception exception)
        {
            SetSummary(content, null, [], exception.Message, persist: false);
            LogFailure(exception);
        }
        finally
        {
            summaryFetchSemaphore.Release();
        }
    }

    private bool TryHydrateSummaryFromRepository(ContentFinderConditionRow content)
    {
        var cached = repository.GetValidInstanceDropCacheEntry(content.ContentFinderConditionId, DateTimeOffset.UtcNow);
        if (cached is null)
        {
            return false;
        }

        return TrySetSummaryFromCachedPayload(content, cached);
    }

    private void HydrateAllValidSummariesFromRepository()
    {
        foreach (var cached in repository.GetValidInstanceDropCacheEntries(DateTimeOffset.UtcNow))
        {
            if (TryGetContentById(cached.ContentFinderConditionId, out var content))
            {
                TrySetSummaryFromCachedPayload(content, cached);
            }
        }
    }

    private bool TrySetSummaryFromCachedPayload(ContentFinderConditionRow content, InstanceDropCacheEntry cached)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<DutySummaryCachePayload>(cached.PayloadJson);
            if (payload is null)
            {
                return false;
            }

            lock (gate)
            {
                pendingSummaryIds.Remove(content.ContentFinderConditionId);
                pendingLootInfoIds.Remove(content.ContentFinderConditionId);
                summaries[content.ContentFinderConditionId] = new DutySummaryState(
                    content.Name,
                    payload.ItemIds.Distinct().ToArray(),
                    cached.GarlandInstanceId,
                    cached.FetchedAtUtc,
                    cached.ExpiresAtUtc,
                    string.Empty);
            }

            return true;
        }
        catch (Exception exception)
        {
            LogFailure(exception);
            return false;
        }
    }

    private void SetSummary(ContentFinderConditionRow content, uint? garlandInstanceId, uint[] itemIds, string error, bool persist)
    {
        var fetchedAt = DateTimeOffset.UtcNow;
        var expiresAt = fetchedAt.Add(SummaryCacheTtl);
        var distinctItemIds = itemIds.Distinct().Order().ToArray();
        lock (gate)
        {
            pendingSummaryIds.Remove(content.ContentFinderConditionId);
            pendingLootInfoIds.Remove(content.ContentFinderConditionId);
            summaries[content.ContentFinderConditionId] = new DutySummaryState(
                content.Name,
                distinctItemIds,
                garlandInstanceId,
                fetchedAt,
                expiresAt,
                error);
        }

        if (persist)
        {
            var payload = JsonSerializer.Serialize(new DutySummaryCachePayload(1, distinctItemIds));
            repository.UpsertInstanceDropCacheEntry(
                content.ContentFinderConditionId,
                garlandInstanceId,
                payload,
                fetchedAt,
                expiresAt);
        }
    }

    private static Dictionary<string, ContentFinderConditionRow> BuildUniqueContentByName(IDataManager dataManager)
    {
        return dataManager.GetExcelSheet<ContentFinderCondition>()
            .Select(row => new ContentFinderConditionRow(row.RowId, row.Name.ExtractText()))
            .Where(row => !string.IsNullOrWhiteSpace(row.Name))
            .GroupBy(row => NormalizeText(row.Name), StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
    }

    private static Dictionary<string, ContentFinderConditionRow> BuildAllContentByName(IDataManager dataManager)
    {
        var result = new Dictionary<string, ContentFinderConditionRow>(StringComparer.Ordinal);
        foreach (var row in dataManager.GetExcelSheet<ContentFinderCondition>())
        {
            var name = row.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var key = NormalizeText(name);
            result.TryAdd(key, new ContentFinderConditionRow(row.RowId, name));
        }

        return result;
    }

    private static unsafe uint AgentSelectedDutyId()
    {
        var agent = AgentContentsFinder.Instance();
        if (agent is null || agent->InterfaceSub.SelectedDutyId <= 0)
        {
            return 0;
        }

        return (uint)agent->InterfaceSub.SelectedDutyId;
    }

    private static unsafe float GetTextDrawWidth(AtkTextNode* textNode, string fallbackText)
        => GetTextDrawSize(textNode, fallbackText).X;

    private static unsafe Vector2 GetTextDrawSize(AtkTextNode* textNode, string fallbackText)
    {
        ushort width = 0;
        ushort height = 0;
        var textByteCount = Encoding.UTF8.GetByteCount(fallbackText);
        Span<byte> textBytes = textByteCount <= 255 ? stackalloc byte[256] : new byte[textByteCount + 1];
        Encoding.UTF8.GetBytes(fallbackText, textBytes);
        textBytes[textByteCount] = 0;

        fixed (byte* textPointer = textBytes)
        {
            textNode->GetTextDrawSize(&width, &height, textPointer, considerScale: true);
        }

        if (width > 0 && height > 0)
        {
            return new Vector2(width, height);
        }

        return ImGui.CalcTextSize(fallbackText);
    }

    private static unsafe float GetTextStartX(AtkTextNode* textNode, float textWidth)
    {
        var node = &textNode->AtkResNode;
        var scaledNodeWidth = node->Width * GetCumulativeScaleX(node);
        var extraWidth = Math.Max(0, scaledNodeWidth - textWidth);

        return textNode->AlignmentType switch
        {
            AlignmentType.Top or AlignmentType.Center or AlignmentType.Bottom => node->ScreenX + extraWidth / 2f,
            AlignmentType.TopRight or AlignmentType.Right or AlignmentType.BottomRight => node->ScreenX + extraWidth,
            _ => node->ScreenX,
        };
    }

    private static unsafe float GetCumulativeScaleX(AtkResNode* node)
    {
        var scale = 1f;
        var current = node;
        while (current is not null)
        {
            scale *= Math.Max(0.01f, current->ScaleX);
            current = current->ParentNode;
        }

        return scale;
    }

    private static unsafe float GetCumulativeScaleY(AtkResNode* node)
    {
        var scale = 1f;
        var current = node;
        while (current is not null)
        {
            scale *= Math.Max(0.01f, current->ScaleY);
            current = current->ParentNode;
        }

        return scale;
    }

    private static unsafe string ReadText(AtkTextNode* textNode)
    {
        return textNode is null ? string.Empty : textNode->NodeText.ToString();
    }

    private static string NormalizeText(string value)
    {
        return string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Replace('’', '\'')
            .ToUpperInvariant();
    }

    private void LogFailure(Exception exception)
    {
        if (DateTimeOffset.UtcNow - lastFailureLog < TimeSpan.FromSeconds(30))
        {
            return;
        }

        lastFailureLog = DateTimeOffset.UtcNow;
        log.Warning(exception, "Duty Finder summary overlay failed; it will retry automatically.");
    }

    private readonly record struct ContentFinderConditionRow(uint ContentFinderConditionId, string Name);

    private readonly record struct RightPanelTitleCandidate(
        nint TextNodePtr,
        ContentFinderConditionRow Content,
        byte FontSize,
        float ScreenX,
        float ScreenY);

    private sealed record DutySummaryState(
        string Name,
        uint[] ItemIds,
        uint? GarlandInstanceId,
        DateTimeOffset FetchedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        string Error)
    {
        public CurrentDutySummary ToCurrent(CollectionState collectionState)
        {
            return new CurrentDutySummary(
                Name,
                ItemIds.Count(collectionState.IsCollected),
                ItemIds.Length);
        }
    }

    private readonly record struct CurrentDutySummary(string Name, int OwnedCount, int TotalCount);

    private sealed record DutySummaryCachePayload(int Version, uint[] ItemIds);

    private readonly record struct ClipRect(float Left, float Top, float Right, float Bottom)
    {
        public unsafe bool ContainsText(AtkTextNode* textNode)
        {
            var node = &textNode->AtkResNode;
            return node->ScreenY >= Top && node->ScreenY + (node->Height * GetCumulativeScaleY(node)) <= Bottom;
        }

        public bool Contains(Vector2 min, Vector2 max)
        {
            return min.X >= Left && max.X <= Right && min.Y >= Top && max.Y <= Bottom;
        }

        public unsafe bool ContainsNode(AtkResNode* node)
        {
            return node->ScreenY >= Top && node->ScreenY + (node->Height * GetCumulativeScaleY(node)) <= Bottom;
        }
    }

    private static unsafe ClipRect GetDutyListClipRect(AtkUnitBase* addon, AtkComponentTreeList* dutyList)
    {
        if (TryGetDutyListViewportClipRect(dutyList, out var dutyListClipRect))
        {
            return dutyListClipRect;
        }

        var rootNode = addon->RootNode;
        if (rootNode is null)
        {
            return default;
        }

        var scaleX = GetCumulativeScaleX(rootNode);
        var scaleY = GetCumulativeScaleY(rootNode);
        var left = rootNode->ScreenX;
        var top = rootNode->ScreenY + (FallbackTopClipOffsetPx * scaleY);
        var right = rootNode->ScreenX + (rootNode->Width * scaleX);
        var bottom = rootNode->ScreenY + (rootNode->Height * scaleY) - (FallbackBottomClipOffsetPx * scaleY);
        return new ClipRect(left, top, right, bottom);
    }

    private static unsafe bool TryGetDutyListViewportClipRect(AtkComponentTreeList* dutyList, out ClipRect clipRect)
    {
        clipRect = default;
        if (dutyList is null)
        {
            return false;
        }

        var listBase = (AtkComponentList*)dutyList;
        if (listBase->CollisionNode is not null &&
            TryCreateClipRect(&listBase->CollisionNode->AtkResNode, TopClipInsetPx, BottomClipInsetPx, out clipRect))
        {
            return true;
        }

        var ownerNode = listBase->AtkComponentBase.OwnerNode;
        return ownerNode is not null &&
            TryCreateClipRect(&ownerNode->AtkResNode, TopClipInsetPx, BottomClipInsetPx, out clipRect);
    }

    private static unsafe bool TryCreateClipRect(
        AtkResNode* node,
        float topInset,
        float bottomInset,
        out ClipRect clipRect)
    {
        clipRect = default;
        if (node is null || !node->IsVisible() || node->Width <= 0 || node->Height <= 0)
        {
            return false;
        }

        var scaleX = GetCumulativeScaleX(node);
        var scaleY = GetCumulativeScaleY(node);
        var left = node->ScreenX;
        var top = node->ScreenY + (topInset * scaleY);
        var right = node->ScreenX + (node->Width * scaleX);
        var bottom = node->ScreenY + (node->Height * scaleY) - (bottomInset * scaleY);
        if (right <= left || bottom <= top)
        {
            return false;
        }

        clipRect = new ClipRect(left, top, right, bottom);
        return true;
    }
}
