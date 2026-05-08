using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace GlamourCheck.Services;

/// <summary>
/// Draws the in-duty owned/total counter next to the native duty name in the HUD.
/// Duty changes queue one async Garland lookup; the draw method only renders the latest
/// completed result and never starts network or database work.
/// </summary>
public sealed class InstanceSummaryOverlayService : IDisposable
{
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

    private readonly ConfigurationService configurationService;
    private readonly IClientState clientState;
    private readonly IDataManager dataManager;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly GarlandDropLookupService dropLookupService;
    private readonly CollectionState collectionState;
    private readonly LootTooltipRenderer lootTooltipRenderer;
    private readonly IPluginLog log;
    private readonly object gate = new();

    private CancellationTokenSource? refreshCancellation;
    private DateTimeOffset lastUpdateCheck = DateTimeOffset.MinValue;
    private DateTimeOffset lastFailureLog = DateTimeOffset.MinValue;
    private CurrentDutyKey currentDutyKey;
    private GarlandLootInfo? currentLoot;
    private string statusText = string.Empty;

    public InstanceSummaryOverlayService(
        ConfigurationService configurationService,
        IClientState clientState,
        IDataManager dataManager,
        IFramework framework,
        IGameGui gameGui,
        LootTooltipRenderer lootTooltipRenderer,
        GarlandDropLookupService dropLookupService,
        CollectionState collectionState,
        IPluginLog log)
    {
        this.configurationService = configurationService;
        this.clientState = clientState;
        this.dataManager = dataManager;
        this.framework = framework;
        this.gameGui = gameGui;
        this.lootTooltipRenderer = lootTooltipRenderer;
        this.dropLookupService = dropLookupService;
        this.collectionState = collectionState;
        this.log = log;

        clientState.TerritoryChanged += OnTerritoryChanged;
        framework.Update += OnFrameworkUpdate;
        QueueRefreshForCurrentDuty();
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
        clientState.TerritoryChanged -= OnTerritoryChanged;
        refreshCancellation?.Cancel();
        refreshCancellation?.Dispose();
    }

    public unsafe void Draw()
    {
        if (!configurationService.Configuration.EnableInstanceSummary)
        {
            return;
        }

        try
        {
            var loot = currentLoot;
            if (loot is null || !TryGetDutyNameAnchor(loot.InstanceName, out var textNode))
            {
                return;
            }

            var allItems = LootTooltipRenderer.GetAllItems(loot);
            if (allItems.Count == 0)
            {
                return;
            }

            var (ownedCount, totalCount) = lootTooltipRenderer.GetItemCounts(allItems);
            var counterText = $"{ownedCount.ToString(CultureInfo.InvariantCulture)}/{totalCount.ToString(CultureInfo.InvariantCulture)}";
            var node = &textNode->AtkResNode;
            var nameWidth = GetTextDrawWidth(textNode, loot.InstanceName);
            var nameStartX = GetTextStartX(textNode, nameWidth);
            var nodeHeight = node->Height * GetCumulativeScaleY(node);
            var counterScale = Math.Clamp(nodeHeight / Math.Max(1f, ImGui.GetTextLineHeight()), 0.6f, 2.5f) * 0.7f;
            var counterSize = ImGui.CalcTextSize(counterText) * counterScale;
            const float nameCounterGapPx = 12f;
            var counterRightX = nameStartX - (nameCounterGapPx * counterScale);
            var counterPosition = new Vector2(counterRightX - counterSize.X, node->ScreenY + Math.Max(0, (nodeHeight - counterSize.Y) / 2f));
            var padding = new Vector2(4, 2) * counterScale;
            var counterMin = counterPosition - padding;
            var counterMax = counterPosition + counterSize + padding;
            if (IsObscuredByNativeWindow(counterMin, counterMax))
            {
                return;
            }

            var drawList = ImGui.GetBackgroundDrawList();
            drawList.AddRectFilled(counterMin, counterMax, 0xB0000000, 4f * counterScale);
            drawList.AddRect(counterMin, counterMax, 0x90FFFFFF, 4f * counterScale);
            drawList.AddText(ImGui.GetFont(), ImGui.GetFontSize() * counterScale, counterPosition, 0xFFFFFFFF, counterText);

            // clip: false so the hover check works on a foreground-draw-list rect (no window context)
            if (ImGui.IsMouseHoveringRect(counterMin, counterMax, false))
            {
                lootTooltipRenderer.DrawLootPopup("##glamcheck_loot", loot, allItems, statusText);
            }
        }
        catch (Exception exception)
        {
            LogFailure(exception);
        }
    }

    private unsafe bool IsObscuredByNativeWindow(Vector2 counterMin, Vector2 counterMax)
    {
        foreach (var addonName in AlwaysOccludingAddonNames)
        {
            if (IsObscuredByAddon(addonName, counterMin, counterMax))
            {
                return true;
            }
        }

        var ignoredAddons = new HashSet<nint>();
        foreach (var addonName in NonOccludingFocusedAddonNames)
        {
            var ptr = (nint)gameGui.GetAddonByName<AtkUnitBase>(addonName);
            if (ptr != 0)
            {
                ignoredAddons.Add(ptr);
            }
        }

        var manager = RaptureAtkUnitManager.Instance();
        if (manager is null)
        {
            return false;
        }

        var focusedAddon = ((AtkUnitManager*)manager)->FocusedAddon;
        if (focusedAddon is not null &&
            !ignoredAddons.Contains((nint)focusedAddon) &&
            IsObscuredByAddon(focusedAddon, counterMin, counterMax))
        {
            return true;
        }

        ref var focusedList = ref ((AtkUnitManager*)manager)->FocusedUnitsList;
        for (ushort i = 0; i < focusedList.Count; i++)
        {
            var unit = focusedList.Entries[i].Value;
            if (unit is null || ignoredAddons.Contains((nint)unit))
            {
                continue;
            }

            if (IsObscuredByAddon(unit, counterMin, counterMax))
            {
                return true;
            }
        }

        return false;
    }

    private unsafe bool IsObscuredByAddon(string addonName, Vector2 counterMin, Vector2 counterMax)
    {
        var addon = gameGui.GetAddonByName<AtkUnitBase>(addonName);
        return addon is not null && IsObscuredByAddon(addon, counterMin, counterMax);
    }

    private static unsafe bool IsObscuredByAddon(AtkUnitBase* addon, Vector2 counterMin, Vector2 counterMax)
    {
        if (addon is null || !addon->IsVisible || addon->RootNode is null)
        {
            return false;
        }

        var rootNode = addon->RootNode;
        var min = new Vector2(rootNode->ScreenX, rootNode->ScreenY);
        var max = new Vector2(
            rootNode->ScreenX + (rootNode->Width * GetCumulativeScaleX(rootNode)),
            rootNode->ScreenY + (rootNode->Height * GetCumulativeScaleY(rootNode)));
        return counterMin.X < max.X &&
            counterMax.X > min.X &&
            counterMin.Y < max.Y &&
            counterMax.Y > min.Y;
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        _ = territoryId;
        QueueRefreshForCurrentDuty();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (DateTimeOffset.UtcNow - lastUpdateCheck < TimeSpan.FromSeconds(1))
        {
            return;
        }

        lastUpdateCheck = DateTimeOffset.UtcNow;
        QueueRefreshForCurrentDuty();
    }

    private void QueueRefreshForCurrentDuty()
    {
        var key = ResolveCurrentDutyKey();
        lock (gate)
        {
            if (key.Equals(currentDutyKey))
            {
                return;
            }

            currentDutyKey = key;
            currentLoot = null;
            statusText = string.Empty;
            refreshCancellation?.Cancel();
            refreshCancellation?.Dispose();
            refreshCancellation = new CancellationTokenSource();
            _ = RefreshCurrentDutyAsync(key, refreshCancellation.Token);
        }
    }

    private async Task RefreshCurrentDutyAsync(CurrentDutyKey key, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(key.ContentName))
            {
                SetRefreshResult(key, null, "unknown duty");
                return;
            }

            var matchResult = await dropLookupService.FindInstanceIndexMatchesAsync(key.ContentName, cancellationToken).ConfigureAwait(false);
            if (matchResult.Matches.Count != 1)
            {
                SetRefreshResult(key, null, $"Garland matches={matchResult.Matches.Count}");
                return;
            }

            var loot = await dropLookupService.GetLootInfoAsync(matchResult.Matches[0].InstanceId, cancellationToken).ConfigureAwait(false);
            SetRefreshResult(key, loot, string.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            SetRefreshResult(key, null, exception.Message);
            LogFailure(exception);
        }
    }

    private void SetRefreshResult(CurrentDutyKey key, GarlandLootInfo? loot, string status)
    {
        lock (gate)
        {
            if (!key.Equals(currentDutyKey))
            {
                return;
            }

            currentLoot = loot;
            statusText = status;
        }
    }

    private CurrentDutyKey ResolveCurrentDutyKey()
    {
        var territoryTypeId = clientState.TerritoryType;
        var contentFinderConditionId = GetCurrentContentFinderConditionId();
        var (contentName, candidateCount) = ResolveCurrentContentName(territoryTypeId, contentFinderConditionId);
        return new CurrentDutyKey(territoryTypeId, contentFinderConditionId, contentName, candidateCount);
    }

    private (string Name, int CandidateCount) ResolveCurrentContentName(uint territoryTypeId, uint contentFinderConditionId)
    {
        var sheet = dataManager.GetExcelSheet<ContentFinderCondition>();
        if (contentFinderConditionId != 0 && sheet.TryGetRow(contentFinderConditionId, out var currentContent))
        {
            return (currentContent.Name.ExtractText(), 1);
        }

        var candidates = sheet
            .Where(content => content.TerritoryType.RowId == territoryTypeId)
            .Select(content => content.Name.ExtractText())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct()
            .ToArray();

        return (candidates.Length == 1 ? candidates[0] : string.Empty, candidates.Length);
    }

    private static unsafe uint GetCurrentContentFinderConditionId()
    {
        var gameMain = GameMain.Instance();
        return gameMain is null ? 0 : (uint)gameMain->CurrentContentFinderConditionId;
    }

    private unsafe bool TryGetDutyNameAnchor(string instanceName, out AtkTextNode* textNode)
    {
        textNode = null;
        var addon = gameGui.GetAddonByName<AddonToDoList>("_ToDoList");
        if (addon is null || !addon->AtkUnitBase.IsVisible)
        {
            return false;
        }

        var normalizedInstanceName = NormalizeText(instanceName);
        foreach (var nodePointer in addon->AtkUnitBase.UldManager.Nodes)
        {
            if (TryFindTextNode(nodePointer.Value, normalizedInstanceName, new HashSet<nint>(), out textNode))
            {
                return true;
            }
        }

        foreach (var nodePointer in addon->DutyFinderTextNodes)
        {
            var candidate = nodePointer.Value;
            if (candidate is not null && candidate->AtkResNode.IsVisible())
            {
                textNode = candidate;
                return true;
            }
        }

        if (addon->DutyTimerTextNode is not null && addon->DutyTimerTextNode->AtkResNode.IsVisible())
        {
            textNode = addon->DutyTimerTextNode;
            return true;
        }

        return false;
    }

    private static unsafe bool TryFindTextNode(
        AtkResNode* node,
        string normalizedText,
        HashSet<nint> visitedNodes,
        out AtkTextNode* textNode)
    {
        textNode = null;
        if (node is null || !visitedNodes.Add((nint)node) || !node->IsVisible())
        {
            return false;
        }

        if (node->GetNodeType() == NodeType.Text)
        {
            var candidate = node->GetAsAtkTextNode();
            if (candidate is not null && NormalizeText(candidate->NodeText.ToString()) == normalizedText)
            {
                textNode = candidate;
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
                    if (TryFindTextNode(childPointer.Value, normalizedText, visitedNodes, out textNode))
                    {
                        return true;
                    }
                }
            }
        }

        var child = node->ChildNode;
        while (child is not null)
        {
            if (TryFindTextNode(child, normalizedText, visitedNodes, out textNode))
            {
                return true;
            }

            child = child->NextSiblingNode;
        }

        return false;
    }

    private static unsafe float GetTextDrawWidth(AtkTextNode* textNode, string fallbackText)
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

        if (width > 0 && width < textNode->AtkResNode.Width)
        {
            return width;
        }

        return ImGui.CalcTextSize(fallbackText).X;
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
        log.Warning(exception, "Instance summary overlay failed; it will retry automatically.");
    }

    private readonly record struct CurrentDutyKey(
        uint TerritoryTypeId,
        uint ContentFinderConditionId,
        string ContentName,
        int CandidateCount);
}
