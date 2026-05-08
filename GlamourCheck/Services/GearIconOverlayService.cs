using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Inventory;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GlamourCheck.Services;

/// <summary>
/// Draws the small collection-state marker on gear icons across native inventory-like addons.
/// The renderer only reads in-memory collection state and visible addon nodes; SQLite and Lumina-heavy
/// lookups are kept out of the draw path. Missing outfit markers may queue one background Garland
/// probe per item ID, but drawing never waits on it.
/// </summary>
public sealed unsafe class GearIconOverlayService : IDisposable
{
    private const float StoredMarkerIconHeight = 17f;
    private const float MissingNonOutfitMarkerIconHeight = 19f;
    private const float MissingOutfitPieceMarkerIconHeight = 18f;
    private const float BaseItemIconSize = 40f;
    private const float BaseCrystallizeRowHeight = 28f;

    private static readonly GameInventoryType[] PlayerInventoryTypes =
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

    // Each inner array is one logical window. Panels in the same group never occlude each other's markers;
    // panels from different groups can occlude each other when they spatially overlap.
    private static readonly string[][] AddonGroups =
    [
        [
            "Inventory",
            "InventoryLarge",
            "InventoryExpansion",
            "InventoryGrid0E",
            "InventoryGrid1E",
            "InventoryGrid2E",
            "InventoryGrid3E",
            "InventoryGrid0",
            "InventoryGrid1",
            "InventoryGrid",
        ],
        ["ArmouryBoard"],
        ["InventoryBuddy", "InventoryBuddy2"],
        ["InventoryRetainer", "InventoryRetainerLarge", "RetainerGrid0", "RetainerGrid1", "RetainerGrid2", "RetainerGrid3", "RetainerGrid4"],
        ["Character"],
        ["NeedGreed"],
        ["MiragePrismPrismBoxCrystallize"],
        ["ContentsFinder"],
    ];

    // HUD/status widgets can receive focus, but they are not real draggable windows
    // and should not cover markers rendered on inventory-like addons.
    private static readonly string[] NonOccludingFocusedAddonNames =
    [
        "_ToDoList",
        "ScenarioTree",
    ];

    private static readonly string[] AlwaysOccludingAddonNames =
    [
        "ItemDetail",
        "ItemDetailCompare",
    ];

    private static readonly string[] PlayerInventoryGridNames =
    [
        "InventoryGrid0E",
        "InventoryGrid1E",
        "InventoryGrid2E",
        "InventoryGrid3E",
        "InventoryGrid0",
        "InventoryGrid1",
        "InventoryGrid",
    ];

    private static readonly string[] RetainerGridNames =
    [
        "RetainerGrid0",
        "RetainerGrid1",
        "RetainerGrid2",
        "RetainerGrid3",
        "RetainerGrid4",
    ];

    private readonly ConfigurationService configurationService;
    private readonly IGameGui gameGui;
    private readonly IGameInventory gameInventory;
    private readonly ItemIdentityService itemIdentityService;
    private readonly CollectionState collectionState;
    private readonly GarlandDropLookupService dropLookupService;
    private readonly IPluginLog log;
    private readonly Dictionary<MarkerVisualState, ISharedImmediateTexture> markerTextures;
    private readonly object outfitStateLock = new();
    private readonly HashSet<uint> outfitItemIds = [];
    private readonly HashSet<uint> nonOutfitItemIds = [];
    private readonly HashSet<uint> pendingOutfitItemIds = [];
    // (Min, Max, GroupId) — GroupId is the AddonGroups index for source addons, -1 for non-source focused addons.
    private readonly List<(Vector2 Min, Vector2 Max, int GroupId)> frameOccluders = [];
    // Maps each source addon's pointer to its AddonGroups index.
    private readonly Dictionary<nint, int> sourceAddonPtrToGroup = [];
    private readonly HashSet<nint> nonOccludingAddonPtrs = [];
    // AddonGroups index of the frontmost focused source window, or -1 if none is focused.
    private int topFocusedGroupId = -1;
    private DateTimeOffset lastFailureLog = DateTimeOffset.MinValue;
    private DateTimeOffset lastOutfitFailureLog = DateTimeOffset.MinValue;
    private nint frameDraggedSlotPtr;
    private uint frameDraggedIconId;
    private bool frameCursorMarkerDrawn;

    public GearIconOverlayService(
        ConfigurationService configurationService,
        IGameGui gameGui,
        IGameInventory gameInventory,
        ITextureProvider textureProvider,
        string pluginDirectory,
        ItemIdentityService itemIdentityService,
        CollectionState collectionState,
        GarlandDropLookupService dropLookupService,
        IPluginLog log)
    {
        this.configurationService = configurationService;
        this.gameGui = gameGui;
        this.gameInventory = gameInventory;
        this.itemIdentityService = itemIdentityService;
        this.collectionState = collectionState;
        this.dropLookupService = dropLookupService;
        this.log = log;
        markerTextures = LoadMarkerTextures(textureProvider, pluginDirectory);
    }

    public void Dispose()
    {
        markerTextures.Clear();
    }

    public void Draw()
    {
        if (!configurationService.Configuration.EnableGearIconOverlays)
        {
            return;
        }

        try
        {
            BuildFrameOccluders();
            CaptureDragState();
            DrawPlayerInventoryOverlays();
            DrawArmoryOverlays();
            DrawSaddlebagOverlays();
            DrawRetainerOverlays();
            DrawEquippedOverlays();
            DrawNeedGreedOverlays();
            DrawGlamourDresserCrystallizeOverlays();
            DrawItemDetailTooltipOverlay();
        }
        catch (Exception exception)
        {
            LogFailure(exception);
        }
    }

    private void CaptureDragState()
    {
        frameDraggedSlotPtr = 0;
        frameDraggedIconId = 0;
        frameCursorMarkerDrawn = false;

        var stage = AtkStage.Instance();
        if (stage is null || !stage->DragDropManager.IsDragging)
        {
            return;
        }

        var draggedSlot = stage->DragDropManager.DragDropS;
        if (draggedSlot is null || draggedSlot->AtkComponentIcon is null)
        {
            return;
        }

        frameDraggedSlotPtr = (nint)draggedSlot;
        frameDraggedIconId = draggedSlot->AtkComponentIcon->IconId;
    }

    private void DrawPlayerInventoryOverlays()
    {
        var iconStatusMap = BuildIconStatusMap(PlayerInventoryTypes);
        if (iconStatusMap.Count == 0)
        {
            return;
        }

        foreach (var gridName in PlayerInventoryGridNames)
        {
            DrawInventoryGridSlots(gridName, iconStatusMap);
        }

        TryDrawCursorMarker(iconStatusMap);
    }

    private void DrawArmoryOverlays()
    {
        var addon = gameGui.GetAddonByName<AddonArmouryBoard>("ArmouryBoard");
        if (addon is null || !addon->AtkUnitBase.IsVisible)
        {
            return;
        }

        var iconStatusMap = BuildIconStatusMap(ArmoryTypes);
        if (iconStatusMap.Count == 0)
        {
            return;
        }

        var hostAddonPtr = (nint)addon;
        foreach (var slotPointer in addon->Slots)
        {
            DrawDragDropSlotIfMatch(slotPointer.Value, iconStatusMap, hostAddonPtr);
        }

        TryDrawCursorMarker(iconStatusMap);
    }

    private void DrawSaddlebagOverlays()
    {
        var addon = gameGui.GetAddonByName<AddonInventoryBuddy>("InventoryBuddy");
        if (addon is null || !addon->AtkUnitBase.IsVisible)
        {
            return;
        }

        var iconStatusMap = BuildIconStatusMap(ChocoboSaddlebagTypes);
        if (iconStatusMap.Count == 0)
        {
            return;
        }

        var hostAddonPtr = (nint)addon;
        foreach (var slotPointer in addon->Slots)
        {
            DrawDragDropSlotIfMatch(slotPointer.Value, iconStatusMap, hostAddonPtr);
        }

        TryDrawCursorMarker(iconStatusMap);
    }

    private void DrawRetainerOverlays()
    {
        var iconStatusMap = BuildIconStatusMap(RetainerTypes);
        if (iconStatusMap.Count == 0)
        {
            return;
        }

        foreach (var gridName in RetainerGridNames)
        {
            DrawInventoryGridSlots(gridName, iconStatusMap);
        }

        TryDrawCursorMarker(iconStatusMap);
    }

    private void DrawEquippedOverlays()
    {
        var addon = gameGui.GetAddonByName<AddonCharacter>("Character");
        if (addon is null || !addon->AtkUnitBase.IsVisible)
        {
            return;
        }

        var iconStatusMap = BuildIconStatusMap(EquippedTypes);
        if (iconStatusMap.Count == 0)
        {
            return;
        }

        DrawComponentNodesInAddon(&addon->AtkUnitBase, iconStatusMap, (nint)addon);
        TryDrawCursorMarker(iconStatusMap);
    }

    private void DrawNeedGreedOverlays()
    {
        var addon = gameGui.GetAddonByName<AddonNeedGreed>("NeedGreed");
        if (addon is null || !addon->AtkUnitBase.IsVisible || addon->NumItems <= 0)
        {
            return;
        }

        var iconStatusMap = BuildNeedGreedIconStatusMap(addon);
        if (iconStatusMap.Count == 0)
        {
            return;
        }

        DrawComponentNodesInAddon(&addon->AtkUnitBase, iconStatusMap, (nint)addon);
    }

    private void DrawGlamourDresserCrystallizeOverlays()
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>("MiragePrismPrismBoxCrystallize");
        if (addon is null || !addon->IsVisible)
        {
            return;
        }

        var itemMap = BuildCrystallizeTextStatusMap();
        var agent = AgentMiragePrismPrismBox.Instance();
        if (agent is not null && agent->Data is not null && agent->Data->CrystallizeItemCount > 0)
        {
            AddAgentCrystallizeItemsToTextStatusMap(itemMap, agent->Data);
        }

        if (!TryGetCrystallizeListClip(addon, out var clipRect))
        {
            return;
        }

        DrawCrystallizeTextMarkers(addon, itemMap, clipRect, (nint)addon);
    }

    private void DrawItemDetailTooltipOverlay()
    {
        var addon = gameGui.GetAddonByName<AddonItemDetail>("ItemDetail");
        if (addon is null || !addon->AddonItemDetailBase.AtkUnitBase.IsVisible)
        {
            return;
        }

        var agent = AgentItemDetail.Instance();
        if (agent is null || agent->ItemId == 0)
        {
            return;
        }

        if (!itemIdentityService.TryGetGearItemInfo(agent->ItemId, out var gearInfo))
        {
            return;
        }

        var status = CreateMarkerStatus(gearInfo);
        var iconStatusMap = new Dictionary<uint, MarkerStatus>
        {
            [gearInfo.IconId] = status,
            [gearInfo.IconId + 1_000_000] = status,
        };

        DrawComponentNodesInAddon(
            &addon->AddonItemDetailBase.AtkUnitBase,
            iconStatusMap,
            hostAddonPtr: 0);
    }

    private void DrawInventoryGridSlots(string addonName, IReadOnlyDictionary<uint, MarkerStatus> iconStatusMap)
    {
        var grid = gameGui.GetAddonByName<AddonInventoryGrid>(addonName);
        if (grid is null || !grid->AtkUnitBase.IsVisible)
        {
            return;
        }

        var hostAddonPtr = (nint)grid;
        foreach (var slotPointer in grid->Slots)
        {
            DrawDragDropSlotIfMatch(slotPointer.Value, iconStatusMap, hostAddonPtr);
        }
    }

    private void DrawDragDropSlotIfMatch(AtkComponentDragDrop* slot, IReadOnlyDictionary<uint, MarkerStatus> iconStatusMap, nint hostAddonPtr)
    {
        if (slot is null || slot->AtkComponentIcon is null)
        {
            return;
        }

        var iconId = slot->AtkComponentIcon->IconId;
        if (iconId == 0)
        {
            return;
        }

        if (frameDraggedIconId != 0 && iconId == frameDraggedIconId)
        {
            return;
        }

        if (iconStatusMap.TryGetValue(iconId, out var status))
        {
            DrawMarker(slot, status.VisualState, hostAddonPtr);
        }
    }

    private void DrawIconComponentIfMatch(AtkComponentIcon* icon, IReadOnlyDictionary<uint, MarkerStatus> iconStatusMap, nint hostAddonPtr)
    {
        if (icon is null || icon->IconId == 0)
        {
            return;
        }

        if (!iconStatusMap.TryGetValue(icon->IconId, out var status))
        {
            return;
        }

        if (!TryGetMarkerAnchor(icon, out var anchor))
        {
            return;
        }

        DrawMarkerAt(anchor.Position, status.VisualState, hostAddonPtr, anchor.Scale);
    }

    private void DrawComponentNodesInAddon(AtkUnitBase* addon, IReadOnlyDictionary<uint, MarkerStatus> iconStatusMap, nint hostAddonPtr)
    {
        if (addon is null || addon->UldManager.NodeList is null)
        {
            return;
        }

        var visitedNodes = new HashSet<nint>();
        foreach (var nodePointer in addon->UldManager.Nodes)
        {
            DrawComponentNodeTreeIfMatch(nodePointer.Value, iconStatusMap, visitedNodes, hostAddonPtr);
        }
    }

    private void DrawComponentNodeTreeIfMatch(
        AtkResNode* node,
        IReadOnlyDictionary<uint, MarkerStatus> iconStatusMap,
        HashSet<nint> visitedNodes,
        nint hostAddonPtr)
    {
        if (node is null || !visitedNodes.Add((nint)node) || !node->IsVisible())
        {
            return;
        }

        if (node->GetNodeType() == NodeType.Component)
        {
            var componentNode = node->GetAsAtkComponentNode();
            var component = componentNode is null ? null : componentNode->Component;
            if (component is not null)
            {
                DrawComponentIfMatch(component, iconStatusMap, hostAddonPtr);

                if (component->UldManager.NodeList is not null)
                {
                    foreach (var childNodePointer in component->UldManager.Nodes)
                    {
                        DrawComponentNodeTreeIfMatch(childNodePointer.Value, iconStatusMap, visitedNodes, hostAddonPtr);
                    }
                }
            }
        }

        var childNode = node->ChildNode;
        while (childNode is not null)
        {
            DrawComponentNodeTreeIfMatch(childNode, iconStatusMap, visitedNodes, hostAddonPtr);
            childNode = childNode->NextSiblingNode;
        }
    }

    private void DrawComponentIfMatch(AtkComponentBase* component, IReadOnlyDictionary<uint, MarkerStatus> iconStatusMap, nint hostAddonPtr)
    {
        var componentType = GetComponentType(component);
        if (componentType == ComponentType.DragDrop)
        {
            DrawDragDropSlotIfMatch((AtkComponentDragDrop*)component, iconStatusMap, hostAddonPtr);
        }
        else if (componentType == ComponentType.Icon)
        {
            DrawIconComponentIfMatch((AtkComponentIcon*)component, iconStatusMap, hostAddonPtr);
        }
    }

    private void DrawCrystallizeTextMarkers(
        AtkUnitBase* addon,
        IReadOnlyDictionary<string, CrystallizeTextStatus> itemMap,
        CrystallizeClipRect clipRect,
        nint hostAddonPtr)
    {
        if (itemMap.Count == 0 || addon->UldManager.NodeList is null)
        {
            return;
        }

        var candidatesByRow = new Dictionary<int, CrystallizeMarkerCandidate>();
        var visitedNodes = new HashSet<nint>();
        foreach (var nodePointer in addon->UldManager.Nodes)
        {
            CollectCrystallizeTextNodeMarkerCandidates(nodePointer.Value, itemMap, visitedNodes, clipRect, candidatesByRow);
        }

        foreach (var candidate in candidatesByRow.Values)
        {
            DrawMarkerAt(
                candidate.MarkerPosition,
                candidate.MarkerStatus.VisualState,
                hostAddonPtr,
                candidate.MarkerScale);
        }
    }

    private Dictionary<string, CrystallizeTextStatus> BuildCrystallizeTextStatusMap()
    {
        var map = new Dictionary<string, CrystallizeTextStatus>(StringComparer.Ordinal);
        foreach (var inventoryType in PlayerInventoryTypes.Concat(ArmoryTypes).Concat(EquippedTypes))
        {
            foreach (ref readonly var item in gameInventory.GetInventoryItems(inventoryType))
            {
                if (item.IsEmpty)
                {
                    continue;
                }

                AddCrystallizeItemToTextStatusMap(map, item.BaseItemId != 0 ? item.BaseItemId : item.ItemId);
            }
        }

        return map;
    }

    private void AddAgentCrystallizeItemsToTextStatusMap(Dictionary<string, CrystallizeTextStatus> map, MiragePrismPrismBoxData* data)
    {
        var itemCount = Math.Min(data->CrystallizeItemCount, data->CrystallizeItems.Length);
        for (var index = 0; index < itemCount; index++)
        {
            var crystallizeItem = data->CrystallizeItems[index];
            AddCrystallizeItemToTextStatusMap(map, crystallizeItem.ItemId);
        }
    }

    private void AddCrystallizeItemToTextStatusMap(Dictionary<string, CrystallizeTextStatus> map, uint rawItemId)
    {
        if (rawItemId == 0 || !itemIdentityService.TryGetGearItemInfo(rawItemId, out var gearInfo))
        {
            return;
        }

        var normalizedName = NormalizeText(gearInfo.Name);
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return;
        }

        var status = CreateMarkerStatus(gearInfo);
        map[normalizedName] = new CrystallizeTextStatus(gearInfo.Name, status);
    }

    private void CollectCrystallizeTextNodeMarkerCandidates(
        AtkResNode* node,
        IReadOnlyDictionary<string, CrystallizeTextStatus> itemMap,
        HashSet<nint> visitedNodes,
        CrystallizeClipRect clipRect,
        IDictionary<int, CrystallizeMarkerCandidate> candidatesByRow)
    {
        if (node is null || !visitedNodes.Add((nint)node) || !node->IsVisible())
        {
            return;
        }

        if (node->GetNodeType() == NodeType.Text)
        {
            var textNode = node->GetAsAtkTextNode();
            var text = ReadUtf8String(&textNode->NodeText);
            var normalizedText = string.IsNullOrWhiteSpace(text) ? string.Empty : NormalizeText(text);
            if (normalizedText.Length > 0 && TryGetCrystallizeTextStatus(normalizedText, itemMap, out var status))
            {
                AddCrystallizeTextMarkerCandidate(textNode, text, status, clipRect, candidatesByRow);
            }
        }

        if (node->GetNodeType() == NodeType.Component)
        {
            var componentNode = node->GetAsAtkComponentNode();
            var component = componentNode is null ? null : componentNode->Component;
            if (component is not null && component->UldManager.NodeList is not null)
            {
                foreach (var childNodePointer in component->UldManager.Nodes)
                {
                    CollectCrystallizeTextNodeMarkerCandidates(childNodePointer.Value, itemMap, visitedNodes, clipRect, candidatesByRow);
                }
            }
        }

        var childNode = node->ChildNode;
        while (childNode is not null)
        {
            CollectCrystallizeTextNodeMarkerCandidates(childNode, itemMap, visitedNodes, clipRect, candidatesByRow);
            childNode = childNode->NextSiblingNode;
        }
    }

    private static bool TryGetCrystallizeTextStatus(
        string normalizedText,
        IReadOnlyDictionary<string, CrystallizeTextStatus> itemMap,
        out CrystallizeTextStatus status)
    {
        if (itemMap.TryGetValue(normalizedText, out status))
        {
            return true;
        }

        foreach (var item in itemMap)
        {
            if (normalizedText.Contains(item.Key, StringComparison.Ordinal))
            {
                status = item.Value;
                return true;
            }
        }

        status = default;
        return false;
    }

    private static void AddCrystallizeTextMarkerCandidate(
        AtkTextNode* textNode,
        string text,
        CrystallizeTextStatus status,
        CrystallizeClipRect clipRect,
        IDictionary<int, CrystallizeMarkerCandidate> candidatesByRow)
    {
        var width = GetTextDrawWidth(textNode, text);
        var scale = GetCumulativeScale(&textNode->AtkResNode);
        var markerScale = Math.Clamp((textNode->AtkResNode.Height * scale.Y) / BaseCrystallizeRowHeight, 0.6f, 2.5f);
        var markerPosition = new Vector2(
            textNode->AtkResNode.ScreenX + width - 25f,
            textNode->AtkResNode.ScreenY + (textNode->AtkResNode.Height * scale.Y / 2f));
        if (markerPosition.X <= 0 || markerPosition.Y <= 0 || !clipRect.Contains(markerPosition))
        {
            return;
        }

        var rowKey = (int)MathF.Round((markerPosition.Y - clipRect.Top) / 38f);
        var candidate = new CrystallizeMarkerCandidate(markerPosition, status.MarkerStatus, markerScale, text.Length);
        if (!candidatesByRow.TryGetValue(rowKey, out var existingCandidate) ||
            candidate.TextLength >= existingCandidate.TextLength)
        {
            candidatesByRow[rowKey] = candidate;
        }
    }

    private static bool TryGetCrystallizeListClip(AtkUnitBase* addon, out CrystallizeClipRect clipRect)
    {
        clipRect = default;
        var rootNode = addon->RootNode;
        if (rootNode is null || rootNode->ScreenX <= 0 || rootNode->ScreenY <= 0)
        {
            return false;
        }

        var rootScale = GetCumulativeScale(rootNode);
        var left = rootNode->ScreenX;
        var top = rootNode->ScreenY + (118f * rootScale.Y);
        var right = rootNode->ScreenX + (rootNode->Width * rootScale.X);
        var bottom = rootNode->ScreenY + (rootNode->Height * rootScale.Y) - (50f * rootScale.Y);
        if (right <= left || bottom <= top)
        {
            return false;
        }

        clipRect = new CrystallizeClipRect(left, top, right, bottom);
        return true;
    }

    private Dictionary<uint, MarkerStatus> BuildIconStatusMap(IEnumerable<GameInventoryType> inventoryTypes)
    {
        var map = new Dictionary<uint, MarkerStatus>();

        foreach (var invType in inventoryTypes)
        {
            foreach (ref readonly var item in gameInventory.GetInventoryItems(invType))
            {
                if (item.IsEmpty)
                {
                    continue;
                }

                AddItemToIconStatusMap(map, item.BaseItemId != 0 ? item.BaseItemId : item.ItemId);
            }
        }

        return map;
    }

    private Dictionary<uint, MarkerStatus> BuildNeedGreedIconStatusMap(AddonNeedGreed* addon)
    {
        var map = new Dictionary<uint, MarkerStatus>();
        var itemCount = Math.Min(addon->NumItems, addon->Items.Length);

        for (var i = 0; i < itemCount; i++)
        {
            var item = addon->Items[i];
            if (item.ItemId == 0 || item.IconId == 0)
            {
                continue;
            }

            if (!itemIdentityService.TryGetGearItemInfo(item.ItemId, out var gearInfo))
            {
                continue;
            }

            var status = CreateMarkerStatus(gearInfo);

            map[item.IconId] = status;
            map[item.IconId + 1_000_000] = status;
        }

        return map;
    }

    private void AddItemToIconStatusMap(Dictionary<uint, MarkerStatus> map, uint rawItemId)
    {
        if (!itemIdentityService.TryGetGearItemInfo(rawItemId, out var gearInfo))
        {
            return;
        }

        var status = CreateMarkerStatus(gearInfo);

        map[gearInfo.IconId] = status;
        map[gearInfo.IconId + 1_000_000] = status;
    }

    private MarkerStatus CreateMarkerStatus(GearItemInfo gearInfo)
    {
        var normalizedItemId = gearInfo.ItemId;
        if (collectionState.IsInArmoire(normalizedItemId) || collectionState.IsInGlamourDresser(normalizedItemId))
        {
            return new MarkerStatus(MarkerVisualState.Stored);
        }

        if (IsWeapon(gearInfo))
        {
            return new MarkerStatus(MarkerVisualState.MissingNonOutfit);
        }

        if (IsKnownOutfitPiece(normalizedItemId))
        {
            return new MarkerStatus(MarkerVisualState.MissingOutfitPiece);
        }

        QueueOutfitLookup(normalizedItemId);
        return new MarkerStatus(MarkerVisualState.MissingNonOutfit);
    }

    private static bool IsWeapon(GearItemInfo gearInfo)
    {
        return gearInfo.Slot is GearSlot.MainHand or GearSlot.OffHand;
    }

    private bool IsKnownOutfitPiece(uint normalizedItemId)
    {
        lock (outfitStateLock)
        {
            return outfitItemIds.Contains(normalizedItemId);
        }
    }

    private void QueueOutfitLookup(uint normalizedItemId)
    {
        lock (outfitStateLock)
        {
            if (outfitItemIds.Contains(normalizedItemId) ||
                nonOutfitItemIds.Contains(normalizedItemId) ||
                !pendingOutfitItemIds.Add(normalizedItemId))
            {
                return;
            }
        }

        _ = dropLookupService.IsOutfitPieceAsync(normalizedItemId).ContinueWith(
            task => CompleteOutfitLookup(normalizedItemId, task),
            TaskScheduler.Default);
    }

    private void CompleteOutfitLookup(uint normalizedItemId, Task<bool> task)
    {
        if (task.IsCompletedSuccessfully)
        {
            lock (outfitStateLock)
            {
                pendingOutfitItemIds.Remove(normalizedItemId);
                if (task.Result)
                {
                    outfitItemIds.Add(normalizedItemId);
                }
                else
                {
                    nonOutfitItemIds.Add(normalizedItemId);
                }
            }
            return;
        }

        lock (outfitStateLock)
        {
            pendingOutfitItemIds.Remove(normalizedItemId);
        }

        if (DateTimeOffset.UtcNow - lastOutfitFailureLog >= TimeSpan.FromSeconds(30))
        {
            lastOutfitFailureLog = DateTimeOffset.UtcNow;
            log.Debug(task.Exception, "Outfit marker lookup failed; item will remain in the non-outfit missing state for now.");
        }
    }

    private void TryDrawCursorMarker(IReadOnlyDictionary<uint, MarkerStatus> iconStatusMap)
    {
        if (frameCursorMarkerDrawn || frameDraggedSlotPtr == 0 || frameDraggedIconId == 0)
        {
            return;
        }

        if (!iconStatusMap.TryGetValue(frameDraggedIconId, out var status))
        {
            return;
        }

        var draggedSlot = (AtkComponentDragDrop*)frameDraggedSlotPtr;
        var dragAnchor = ResolveDragMarkerAnchor(draggedSlot);
        DrawMarkerAt(dragAnchor.Position, status.VisualState, markerScale: dragAnchor.Scale);
        frameCursorMarkerDrawn = true;
    }

    private static Vector2 ResolveDragMarkerPosition(AtkComponentDragDrop* draggedSlot)
        => ResolveDragMarkerAnchor(draggedSlot).Position;

    private static MarkerAnchor ResolveDragMarkerAnchor(AtkComponentDragDrop* draggedSlot)
    {
        var icon = draggedSlot->AtkComponentIcon;
        if (icon != null)
        {
            var iconImage = icon->IconImage;
            if (iconImage != null && iconImage->AtkResNode.IsVisible() && iconImage->AtkResNode.ScreenX > 0)
            {
                var scale = GetMarkerScale(&iconImage->AtkResNode);
                return new MarkerAnchor(
                    new Vector2(iconImage->AtkResNode.ScreenX + (6f * scale), iconImage->AtkResNode.ScreenY + (6f * scale)),
                    scale);
            }

            var outerNode = icon->OuterResNode;
            if (outerNode != null)
            {
                var outerImage = outerNode->GetAsAtkImageNode();
                if (outerImage != null && outerImage->AtkResNode.ScreenX > 0)
                {
                    var scale = GetMarkerScale(&outerImage->AtkResNode);
                    return new MarkerAnchor(
                        new Vector2(outerImage->AtkResNode.ScreenX + (6f * scale), outerImage->AtkResNode.ScreenY + (6f * scale)),
                        scale);
                }
            }
        }

        var activeNode = draggedSlot->AtkDragDropInterface.ActiveNode;
        if (activeNode != null && activeNode->ScreenX > 0 && activeNode->ScreenY > 0)
        {
            var scale = GetMarkerScale(activeNode);
            return new MarkerAnchor(
                new Vector2(activeNode->ScreenX + (10f * scale), activeNode->ScreenY + (10f * scale)),
                scale);
        }

        return new MarkerAnchor(ImGui.GetMousePos() + new Vector2(6, 6), 1f);
    }

    private void DrawMarker(AtkComponentDragDrop* slot, MarkerVisualState visualState, nint hostAddonPtr)
    {
        if (!TryGetMarkerAnchor(slot, out var anchor))
        {
            return;
        }

        DrawMarkerAt(anchor.Position, visualState, hostAddonPtr, anchor.Scale);
    }

    private void DrawMarkerAt(Vector2 markerPosition, MarkerVisualState visualState, nint hostAddonPtr = 0, float markerScale = 1f)
    {
        if (IsOccluded(markerPosition, hostAddonPtr))
        {
            return;
        }

        markerScale = Math.Clamp(markerScale, 0.6f, 2.5f);
        if (TryDrawMarkerTexture(markerPosition, visualState, markerScale))
        {
            return;
        }

        var drawList = ImGui.GetBackgroundDrawList();
        var backgroundColor = visualState switch
        {
            MarkerVisualState.Stored => 0xDD1E7F46u,
            MarkerVisualState.MissingOutfitPiece => 0xDDB8860Bu,
            _ => 0xDD343A40u,
        };
        var strokeColor = 0xFFEFFAF2u;

        drawList.AddCircleFilled(markerPosition, 6.5f * markerScale, 0xCC000000);
        drawList.AddCircleFilled(markerPosition, 5.5f * markerScale, backgroundColor);
        if (visualState == MarkerVisualState.Stored)
        {
            drawList.AddLine(markerPosition + (new Vector2(-3.0f, 0.0f) * markerScale), markerPosition + (new Vector2(-0.8f, 3.0f) * markerScale), strokeColor, 1.8f * markerScale);
            drawList.AddLine(markerPosition + (new Vector2(-0.8f, 3.0f) * markerScale), markerPosition + (new Vector2(3.8f, -3.2f) * markerScale), strokeColor, 1.8f * markerScale);
        }
        else
        {
            drawList.AddLine(markerPosition + (new Vector2(-3.0f, -3.0f) * markerScale), markerPosition + (new Vector2(3.0f, 3.0f) * markerScale), strokeColor, 1.7f * markerScale);
            drawList.AddLine(markerPosition + (new Vector2(3.0f, -3.0f) * markerScale), markerPosition + (new Vector2(-3.0f, 3.0f) * markerScale), strokeColor, 1.7f * markerScale);
        }
    }

    private static Dictionary<MarkerVisualState, ISharedImmediateTexture> LoadMarkerTextures(
        ITextureProvider textureProvider,
        string pluginDirectory)
    {
        var mediaDir = Path.Combine(pluginDirectory, "Media");
        return new Dictionary<MarkerVisualState, ISharedImmediateTexture>
        {
            [MarkerVisualState.Stored] = textureProvider.GetFromFile(Path.Combine(mediaDir, "collected.png")),
            // These two asset assignments match the final in-game visual pass. Do not
            // swap them based on filename alone without rechecking the actual PNG art.
            [MarkerVisualState.MissingNonOutfit] = textureProvider.GetFromFile(Path.Combine(mediaDir, "missing-outfit.png")),
            [MarkerVisualState.MissingOutfitPiece] = textureProvider.GetFromFile(Path.Combine(mediaDir, "missing-non-outfit.png")),
        };
    }

    private bool TryDrawMarkerTexture(Vector2 markerPosition, MarkerVisualState visualState, float markerScale)
    {
        if (!markerTextures.TryGetValue(visualState, out var sharedTexture) ||
            !sharedTexture.TryGetWrap(out IDalamudTextureWrap? texture, out _))
        {
            return false;
        }

        var aspectRatio = texture.Width > 0 && texture.Height > 0
            ? texture.Width / (float)texture.Height
            : 1f;
        var markerHeight = visualState switch
        {
            MarkerVisualState.Stored => StoredMarkerIconHeight * markerScale,
            MarkerVisualState.MissingNonOutfit => MissingNonOutfitMarkerIconHeight * markerScale,
            MarkerVisualState.MissingOutfitPiece => MissingOutfitPieceMarkerIconHeight * markerScale,
            _ => StoredMarkerIconHeight * markerScale,
        };

        var size = new Vector2(markerHeight * aspectRatio, markerHeight);
        var min = markerPosition - size / 2f;
        ImGui.GetBackgroundDrawList().AddImage(texture.Handle, min, min + size);
        return true;
    }

    private static bool TryGetMarkerAnchor(AtkComponentDragDrop* slot, out MarkerAnchor anchor)
    {
        anchor = default;
        return slot->AtkComponentIcon is not null && TryGetMarkerAnchor(slot->AtkComponentIcon, out anchor);
    }

    private static bool TryGetMarkerAnchor(AtkComponentIcon* icon, out MarkerAnchor anchor)
    {
        anchor = default;

        var node = icon->IconImage;
        if (node is null || !node->AtkResNode.IsVisible())
        {
            var outerNode = icon->OuterResNode;
            node = outerNode is null ? null : outerNode->GetAsAtkImageNode();
        }

        if (node is not null)
        {
            var scale = GetMarkerScale(&node->AtkResNode);
            var markerPosition = new Vector2(node->AtkResNode.ScreenX + (6f * scale), node->AtkResNode.ScreenY + (6f * scale));
            anchor = new MarkerAnchor(markerPosition, scale);
            return markerPosition.X > 0 && markerPosition.Y > 0;
        }

        var componentNode = icon->AtkComponentBase.OwnerNode;
        if (componentNode is not null)
        {
            var scale = GetMarkerScale(&componentNode->AtkResNode);
            var markerPosition = new Vector2(componentNode->AtkResNode.ScreenX + (6f * scale), componentNode->AtkResNode.ScreenY + (6f * scale));
            anchor = new MarkerAnchor(markerPosition, scale);
            return markerPosition.X > 0 && markerPosition.Y > 0;
        }

        return false;
    }

    private static float GetMarkerScale(AtkResNode* node)
    {
        if (node is null)
        {
            return 1f;
        }

        var scale = GetCumulativeScale(node);
        var scaledHeight = Math.Max(node->Height * scale.Y, node->Width * scale.X);
        return Math.Clamp(scaledHeight / BaseItemIconSize, 0.6f, 2.5f);
    }

    private static float GetTextDrawWidth(AtkTextNode* textNode, string fallbackText)
    {
        if (textNode is null)
        {
            return ImGui.CalcTextSize(fallbackText).X;
        }

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

        return width > 0 ? width : ImGui.CalcTextSize(fallbackText).X;
    }

    private static Vector2 GetCumulativeScale(AtkResNode* node)
    {
        var scale = Vector2.One;
        var current = node;
        while (current is not null)
        {
            scale *= new Vector2(current->ScaleX, current->ScaleY);
            current = current->ParentNode;
        }

        return scale;
    }

    private static string NormalizeText(string value)
    {
        return string.Join(' ', value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Replace('\u2019', '\'')
            .ToUpperInvariant();
    }

    private static string ReadUtf8String(FFXIVClientStructs.FFXIV.Client.System.String.Utf8String* value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var span = value->AsSpan();
        return span.IsEmpty ? string.Empty : Encoding.UTF8.GetString(span);
    }

    private static ComponentType GetComponentType(AtkComponentBase* component)
    {
        if (component->UldManager.Objects is not null)
        {
            return ((AtkUldComponentInfo*)component->UldManager.Objects)->ComponentType;
        }

        return component->GetComponentType();
    }

    private void BuildFrameOccluders()
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

        // Build source addon ptr → group index map.
        for (var g = 0; g < AddonGroups.Length; g++)
        {
            foreach (var addonName in AddonGroups[g])
            {
                var ptr = (nint)gameGui.GetAddonByName<AtkUnitBase>(addonName);
                if (ptr != 0)
                {
                    sourceAddonPtrToGroup[ptr] = g;
                }
            }
        }

        foreach (var addonName in AlwaysOccludingAddonNames)
        {
            var unit = gameGui.GetAddonByName<AtkUnitBase>(addonName);
            if (unit is not null && unit->IsVisible)
            {
                AddOccluderRect(unit, -1);
            }
        }

        // Determine which source group is frontmost. Use FocusedAddon first (single direct pointer),
        // then fall back to scanning FocusedUnitsList (last source addon found = most recently focused).
        topFocusedGroupId = -1;
        var manager = RaptureAtkUnitManager.Instance();
        if (manager is not null)
        {
            var focusedAddon = ((AtkUnitManager*)manager)->FocusedAddon;
            if (focusedAddon is not null && !nonOccludingAddonPtrs.Contains((nint)focusedAddon))
            {
                if (sourceAddonPtrToGroup.TryGetValue((nint)focusedAddon, out var focusedGroupId))
                {
                    topFocusedGroupId = focusedGroupId;
                }
            }

            ref var list = ref ((AtkUnitManager*)manager)->FocusedUnitsList;
            for (ushort i = 0; i < list.Count; i++)
            {
                var unit = list.Entries[i].Value;
                if (unit is null || !unit->IsVisible)
                {
                    continue;
                }

                var unitPtr = (nint)unit;
                if (nonOccludingAddonPtrs.Contains(unitPtr))
                {
                    continue;
                }

                if (sourceAddonPtrToGroup.TryGetValue(unitPtr, out var gId))
                {
                    // Track the most recently focused source group as fallback.
                    topFocusedGroupId = gId;
                }
                else
                {
                    // Non-source focused addons (dialogs, menus, etc.) occlude everything.
                    AddOccluderRect(unit, -1);
                }
            }
        }

        // All visible source addons are included so that windows from different groups can
        // occlude each other when they spatially overlap (e.g. armory board over inventory).
        foreach (var (addonPtr, groupId) in sourceAddonPtrToGroup)
        {
            var unit = (AtkUnitBase*)addonPtr;
            if (unit->IsVisible)
            {
                AddOccluderRect(unit, groupId);
            }
        }
    }

    private void AddOccluderRect(AtkUnitBase* unit, int groupId)
    {
        var rootNode = unit->RootNode;
        if (rootNode is null)
        {
            return;
        }

        var scale = GetCumulativeScale(rootNode);
        var min = new Vector2(rootNode->ScreenX, rootNode->ScreenY);
        var max = new Vector2(
            rootNode->ScreenX + (rootNode->Width * scale.X),
            rootNode->ScreenY + (rootNode->Height * scale.Y));
        if (max.X > min.X && max.Y > min.Y)
        {
            frameOccluders.Add((min, max, groupId));
        }
    }

    private bool IsOccluded(Vector2 point, nint hostAddonPtr)
    {
        if (hostAddonPtr == 0)
        {
            return false;
        }

        var hostGroupId = sourceAddonPtrToGroup.TryGetValue(hostAddonPtr, out var g) ? g : -2;

        foreach (var (min, max, groupId) in frameOccluders)
        {
            // Same-group panels (e.g. inventory sub-grids) never occlude each other.
            if (groupId == hostGroupId)
            {
                continue;
            }

            // The frontmost focused source window is never occluded by other source windows.
            // Non-source focused addons (dialogs, groupId == -1) still occlude even focused windows.
            if (hostGroupId == topFocusedGroupId && groupId >= 0)
            {
                continue;
            }

            if (point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y)
            {
                return true;
            }
        }

        return false;
    }

    private void LogFailure(Exception exception)
    {
        if (DateTimeOffset.UtcNow - lastFailureLog < TimeSpan.FromSeconds(30))
        {
            return;
        }

        lastFailureLog = DateTimeOffset.UtcNow;
        log.Warning(exception, "Gear icon overlay draw failed; overlays will retry next frame.");
    }

    private readonly record struct MarkerAnchor(Vector2 Position, float Scale);

    private readonly record struct CrystallizeMarkerCandidate(Vector2 MarkerPosition, MarkerStatus MarkerStatus, float MarkerScale, int TextLength);

    private readonly record struct CrystallizeClipRect(float Left, float Top, float Right, float Bottom)
    {
        public bool Contains(Vector2 point)
        {
            return point.X >= Left && point.X <= Right && point.Y >= Top && point.Y <= Bottom;
        }
    }

    private readonly record struct CrystallizeTextStatus(string ItemName, MarkerStatus MarkerStatus);

    private readonly record struct MarkerStatus(MarkerVisualState VisualState);

    private enum MarkerVisualState
    {
        MissingNonOutfit,
        MissingOutfitPiece,
        Stored,
    }
}
