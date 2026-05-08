using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace GlamourCheck.Services;

/// <summary>
/// Shared renderer for duty loot grids and the interactive try-on popup.
/// It groups loot into role blocks, annotates source ownership, and delegates fitting-room
/// actions to <see cref="GearTryOnService"/>.
/// </summary>
public sealed class LootTooltipRenderer
{
    private static readonly string[] ArmorCategoryOrder =
    [
        "Fending", "Striking", "Scouting", "Maiming", "Healing", "Aiming", "Casting",
    ];

    private static readonly string[] SourcePriority =
    [
        CollectionSource.Equipped,
        CollectionSource.GlamourDresser,
        CollectionSource.Armoire,
        CollectionSource.Inventory,
        CollectionSource.ArmoryChest,
        CollectionSource.ChocoboSaddlebag,
        // retainer:* handled by prefix check in GetPrioritySource
    ];

    // ABGR: 0xAABBGGRR
    private static readonly (char Letter, string Name, uint Color)[] SourceLegend =
    [
        ('E', "Equipped",        0xFFFF8080u), // sky blue
        ('G', "Glamour Dresser", 0xFFDC64B4u), // violet
        ('C', "Armoire",         0xFF00A5FFu), // orange
        ('I', "Inventory",       0xFF64DC64u), // green
        ('A', "Armory",          0xFF32DCFFu), // yellow
        ('S', "Saddlebag",       0xFFD2D200u), // cyan
        ('R', "Retainer",        0xFF28C8FFu), // gold
    ];

    private static readonly GearSlot[,] SlotGrid =
    {
        { GearSlot.Head,  GearSlot.Ears    },
        { GearSlot.Body,  GearSlot.Neck    },
        { GearSlot.Hands, GearSlot.Wrists  },
        { GearSlot.Legs,  GearSlot.Ring    },
        { GearSlot.Feet,  GearSlot.Unknown },
    };

    // Canonical job display order for the weapon row.
    private static readonly string[] JobOrder =
    [
        "PLD", "WAR", "DRK", "GNB",
        "WHM", "SCH", "AST", "SGE",
        "MNK", "DRG", "NIN", "SAM", "RPR", "VPR",
        "BRD", "MCH", "DNC",
        "BLM", "SMN", "RDM", "PCT",
    ];

    private static readonly Dictionary<string, string> JobIconFiles = new(StringComparer.Ordinal)
    {
        ["PLD"] = "Paladin_Icon_4.png",
        ["WAR"] = "Warrior_Icon_4.png",
        ["DRK"] = "Dark_Knight_Icon_4.png",
        ["GNB"] = "Gunbreaker_Icon_4.png",
        ["WHM"] = "White_Mage_Icon_4.png",
        ["SCH"] = "Scholar_Icon_4.png",
        ["AST"] = "Astrologian_Icon_4.png",
        ["SGE"] = "Sage_Icon_4.png",
        ["MNK"] = "Monk_Icon_4.png",
        ["DRG"] = "Dragoon_Icon_4.png",
        ["NIN"] = "Ninja_Icon_4.png",
        ["SAM"] = "Samurai_Icon_4.png",
        ["RPR"] = "Reaper_Icon_4.png",
        ["VPR"] = "Viper_Icon_4.png",
        ["BRD"] = "Bard_Icon_4.png",
        ["MCH"] = "Machinist_Icon_4.png",
        ["DNC"] = "Dancer_Icon_4.png",
        ["BLM"] = "Black_Mage_Icon_4.png",
        ["SMN"] = "Summoner_Icon_4.png",
        ["RDM"] = "Red_Mage_Icon_4.png",
        ["PCT"] = "Pictomancer_Icon_4.png",
    };

    private readonly CollectionState collectionState;
    private readonly IDataManager dataManager;
    private readonly GearTryOnService tryOnService;
    private readonly Dictionary<GearSlot, ISharedImmediateTexture> slotTextures;
    private readonly Dictionary<string, ISharedImmediateTexture> jobTextures;
    private bool interactiveTargetHovered;

    public LootTooltipRenderer(
        CollectionState collectionState,
        ITextureProvider textureProvider,
        IDataManager dataManager,
        string pluginDirectory,
        GearTryOnService tryOnService)
    {
        this.collectionState = collectionState;
        this.dataManager = dataManager;
        this.tryOnService = tryOnService;
        slotTextures = LoadSlotTextures(textureProvider, pluginDirectory);
        jobTextures = LoadJobTextures(textureProvider, pluginDirectory);
    }

    public static IReadOnlyList<GarlandLootItemInfo> GetAllItems(GarlandLootInfo loot)
    {
        return loot.GarlandItems
            .Concat(loot.MatchedItems)
            .Concat(loot.SeededItems)
            .DistinctBy(item => item.ItemId)
            .ToArray();
    }

    public bool DrawLootPopup(
        string windowId,
        GarlandLootInfo loot,
        IReadOnlyList<GarlandLootItemInfo> allItems,
        string? statusText = null,
        Vector2? pinnedPosition = null,
        bool interactive = false,
        bool closeOnOutsideClick = false,
        Action<Vector2>? dragConsumer = null,
        Action<Vector2, Vector2>? boundsConsumer = null)
    {
        const float ContentWidth = 4 * 102f + 3 * 10f;
        ImGui.SetNextWindowPos(pinnedPosition ?? ImGui.GetMousePos(), ImGuiCond.Always, pinnedPosition is null ? new Vector2(1f, 0f) : Vector2.Zero);
        ImGui.SetNextWindowBgAlpha(0.96f);
        ImGui.Begin(
            windowId,
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoMove);

        var closeRequested = false;
        interactiveTargetHovered = false;
        var windowMin = ImGui.GetWindowPos();
        var headerStartY = ImGui.GetCursorPosY();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + ContentWidth);
        if (interactive)
        {
            ImGui.TextDisabled("Left-click: try on. Right-click: add or replace pieces.");
        }
        else
        {
            ImGui.TextDisabled("Left-click the counter to try on gear.");
        }
        ImGui.PopTextWrapPos();
        var headerEndY = ImGui.GetCursorPosY();

        if (interactive)
        {
            var closeLabel = "X##glamcheck_tryon_close";
            var closeWidth = ImGui.CalcTextSize("X").X + ImGui.GetStyle().FramePadding.X * 2f;
            var closePos = new Vector2(
                ImGui.GetWindowWidth() - closeWidth - ImGui.GetStyle().WindowPadding.X,
                ImGui.GetStyle().WindowPadding.Y);
            ImGui.SetCursorPos(closePos);
            interactiveTargetHovered |= ImGui.IsMouseHoveringRect(ImGui.GetCursorScreenPos(), ImGui.GetCursorScreenPos() + new Vector2(closeWidth, ImGui.GetFrameHeight()), false);
            if (ImGui.SmallButton(closeLabel))
            {
                closeRequested = true;
            }
            ImGui.SetCursorPosY(headerEndY);
        }

        var ownedCount = allItems.Count(item => collectionState.IsCollected(item.ItemId));
        ImGui.TextUnformatted($"{loot.InstanceName}  {ownedCount}/{allItems.Count}");

        if (!string.IsNullOrWhiteSpace(statusText))
        {
            ImGui.TextDisabled(statusText);
        }

        var categories = BuildCategoryMap(
            allItems.Where(item => item.Slot is not GearSlot.MainHand and not GearSlot.OffHand));

        DrawCategoryRow(ArmorCategoryOrder.Take(4), categories, interactive);
        ImGui.Dummy(new Vector2(0, 16f));
        DrawCategoryRow(ArmorCategoryOrder.Skip(4), categories, interactive, center: true);
        DrawWeaponSummary(allItems, interactive);
        DrawSourceLegend();

        var windowMax = windowMin + ImGui.GetWindowSize();
        boundsConsumer?.Invoke(windowMin, windowMax);
        if (interactive &&
            dragConsumer is not null &&
            !interactiveTargetHovered &&
            ImGui.IsMouseHoveringRect(windowMin, windowMax, false) &&
            ImGui.IsMouseDragging(ImGuiMouseButton.Left))
        {
            dragConsumer(ImGui.GetIO().MouseDelta);
        }

        if (interactive && closeOnOutsideClick && ImGui.GetIO().MouseClicked[0])
        {
            var mousePos = ImGui.GetMousePos();
            if (mousePos.X < windowMin.X ||
                mousePos.X > windowMax.X ||
                mousePos.Y < windowMin.Y ||
                mousePos.Y > windowMax.Y)
            {
                closeRequested = true;
            }
        }

        ImGui.End();
        return closeRequested;
    }

    private static Dictionary<string, GarlandLootItemInfo[]> BuildCategoryMap(IEnumerable<GarlandLootItemInfo> items)
    {
        var buckets = new Dictionary<string, List<GarlandLootItemInfo>>(StringComparer.Ordinal);

        void AddItem(string category, GarlandLootItemInfo item)
        {
            if (!buckets.TryGetValue(category, out var list))
            {
                list = [];
                buckets[category] = list;
            }
            if (!list.Any(existing => existing.Slot == item.Slot))
            {
                list.Add(item);
            }
        }

        foreach (var item in items)
        {
            switch (item.ArmorCategory)
            {
                case "Slaying":
                    AddItem("Striking", item);
                    AddItem("Maiming", item);
                    break;
                case "AimingScouting":
                    AddItem("Aiming", item);
                    AddItem("Scouting", item);
                    break;
                case "Magic":
                    AddItem("Healing", item);
                    AddItem("Casting", item);
                    break;
                case "PhysicalDps":
                    AddItem("Striking", item);
                    AddItem("Maiming", item);
                    AddItem("Scouting", item);
                    AddItem("Aiming", item);
                    break;
                case "War":
                    AddItem("Fending", item);
                    AddItem("Striking", item);
                    AddItem("Maiming", item);
                    AddItem("Scouting", item);
                    AddItem("Aiming", item);
                    break;
                case "All":
                    foreach (var cat in ArmorCategoryOrder)
                        AddItem(cat, item);
                    break;
                default:
                    AddItem(item.ArmorCategory, item);
                    break;
            }
        }

        return buckets.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
    }

    private void DrawCategoryRow(
        IEnumerable<string> categoryNames,
        IReadOnlyDictionary<string, GarlandLootItemInfo[]> categories,
        bool interactive,
        bool center = false)
    {
        const float blockWidth = 102f;
        const float spacing = 10f;
        var names = categoryNames.ToArray();
        if (center)
        {
            var rowWidth = names.Length * blockWidth + (names.Length - 1) * spacing;
            var availableWidth = 4 * blockWidth + 3 * spacing;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (availableWidth - rowWidth) / 2f));
        }
        for (var i = 0; i < names.Length; i++)
        {
            DrawCategoryBlock(names[i], categories.TryGetValue(names[i], out var items) ? items : [], interactive);
            if (i < names.Length - 1)
            {
                ImGui.SameLine(0, spacing);
            }
        }
    }

    private void DrawCategoryBlock(string categoryName, IReadOnlyList<GarlandLootItemInfo> items, bool interactive)
    {
        var start = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();
        const float iconSize = 40f;
        const float iconGapX = 6f;
        const float iconGapY = 4f;
        const float titleHeight = 28f;
        const float sidePadding = 8f;
        const float bottomPadding = 8f;
        const int gridRows = 5;
        const float contentWidth = 2 * iconSize + iconGapX;
        const float contentHeight = gridRows * iconSize + (gridRows - 1) * iconGapY;
        const float blockWidth = contentWidth + 2 * sidePadding;
        const float blockHeight = titleHeight + contentHeight + bottomPadding;

        drawList.AddRectFilled(start, start + new Vector2(blockWidth, blockHeight), 0x22000000, 2f);
        var titleSize = ImGui.CalcTextSize(categoryName);
        var titlePos = start + new Vector2((blockWidth - titleSize.X) / 2f, 6f);
        drawList.AddText(titlePos, 0xFFFFFFFF, categoryName);
        if (interactive && items.Count > 0)
        {
            var titleMin = start;
            var titleMax = start + new Vector2(blockWidth, titleHeight);
            if (ImGui.IsMouseHoveringRect(titleMin, titleMax, false))
            {
                interactiveTargetHovered = true;
                drawList.AddRect(titleMin + new Vector2(2f, 2f), titleMax - new Vector2(2f, 2f), 0xA0FFFFFF, 2f);
                ImGui.SetTooltip($"Left-click: try on {categoryName}\nRight-click: add/replace this set");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    tryOnService.TryOnItems(items.Select(item => item.ItemId));
                }
                else if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                {
                    tryOnService.AddToTryOnItems(items.Select(item => item.ItemId));
                }
            }
        }

        for (var row = 0; row < SlotGrid.GetLength(0); row++)
        {
            for (var col = 0; col < SlotGrid.GetLength(1); col++)
            {
                var slot = SlotGrid[row, col];
                if (slot == GearSlot.Unknown)
                {
                    continue;
                }

                var item = items.FirstOrDefault(e => e.Slot == slot);
                if (item is null)
                {
                    continue;
                }

                var iconPos = start + new Vector2(sidePadding + col * (iconSize + iconGapX), titleHeight + row * (iconSize + iconGapY));
                var collected = collectionState.IsCollected(item.ItemId);
                var sources = collected ? collectionState.GetSourcesForItem(item.ItemId) : [];
                DrawLootIcon(drawList, iconPos, iconSize, item, collected, sources);
                if (interactive)
                {
                    AddItemTryOnHitTarget(drawList, iconPos, iconSize, item);
                }
            }
        }

        ImGui.Dummy(new Vector2(blockWidth, blockHeight));
    }

    private void AddItemTryOnHitTarget(ImDrawListPtr drawList, Vector2 position, float size, GarlandLootItemInfo item)
    {
        var min = position;
        var max = position + new Vector2(size, size);
        if (!ImGui.IsMouseHoveringRect(min, max, false))
        {
            return;
        }

        interactiveTargetHovered = true;
        drawList.AddRect(min, max, 0xD0FFFFFF, 3f, ImDrawFlags.None, 2f);
        ImGui.SetTooltip($"Left-click: try on {item.Name}\nRight-click: add/replace this slot");
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            tryOnService.TryOnItem(item.ItemId);
        }
        else if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            tryOnService.AddToTryOnItem(item.ItemId);
        }
    }

    private void DrawLootIcon(ImDrawListPtr drawList, Vector2 position, float size, GarlandLootItemInfo item, bool collected, string[] sources)
    {
        var min = position;
        var max = position + new Vector2(size, size);
        var overlayTint = collected ? 0x4060FF80u : 0x406060FFu;
        var borderColor = collected ? 0xFF7DFF9Fu : 0xFF8585FFu;

        if (slotTextures.TryGetValue(item.Slot, out var sharedTexture) &&
            sharedTexture.TryGetWrap(out IDalamudTextureWrap? texture, out _))
        {
            drawList.AddImage(texture.Handle, min, max, Vector2.Zero, Vector2.One, 0xFFFFFFFF);
            drawList.AddRectFilled(min, max, overlayTint, 3f);
        }
        else
        {
            drawList.AddRectFilled(min, max, collected ? 0xA0308050u : 0xA0303080u, 3f);
            var label = GetSlotAbbreviation(item.Slot);
            var labelSize = ImGui.CalcTextSize(label);
            drawList.AddText(min + (new Vector2(size, size) - labelSize) / 2f, 0xFFFFFFFF, label);
        }

        drawList.AddRect(min, max, borderColor, 3f);

        if (collected && sources.Length > 0)
        {
            DrawSourceBadge(drawList, min, GetPrioritySource(sources));
        }
    }

    private static void DrawSourceBadge(ImDrawListPtr drawList, Vector2 iconTopLeft, string source)
    {
        const float badgeSize = 13f;
        const float margin = 1f;
        var badgeMin = iconTopLeft + new Vector2(margin, margin);
        var badgeMax = badgeMin + new Vector2(badgeSize, badgeSize);
        var color = GetSourceBadgeColor(source);
        drawList.AddRectFilled(badgeMin, badgeMax, color, 2f);
        var letter = GetSourceLetter(source).ToString();
        var letterSize = ImGui.CalcTextSize(letter);
        drawList.AddText(badgeMin + (new Vector2(badgeSize, badgeSize) - letterSize) / 2f, 0xFF000000u, letter);
    }

    // Returns (owned, total) treating weapon items as job groups — PLD sword+shield count as one.
    public (int Owned, int Total) GetItemCounts(IReadOnlyList<GarlandLootItemInfo> allItems)
    {
        var nonWeapons = allItems.Where(item => item.Slot is not GearSlot.MainHand and not GearSlot.OffHand).ToArray();
        var weapons    = allItems.Where(item => item.Slot is GearSlot.MainHand or GearSlot.OffHand).ToArray();

        var nonWeaponOwned = nonWeapons.Count(item => collectionState.IsCollected(item.ItemId));

        if (weapons.Length == 0)
        {
            return (nonWeaponOwned, nonWeapons.Length);
        }

        var jobMap = BuildWeaponJobMap(weapons);
        var presentJobs = JobOrder.Count(jobMap.ContainsKey);
        var ownedJobs   = JobOrder.Count(j => jobMap.TryGetValue(j, out var e) && e.Collected);
        return (nonWeaponOwned + ownedJobs, nonWeapons.Length + presentJobs);
    }

    private Dictionary<string, WeaponJobEntry> BuildWeaponJobMap(
        IEnumerable<GarlandLootItemInfo> weapons)
    {
        var jobItems = new Dictionary<string, List<(bool Collected, string[] Sources)>>(StringComparer.Ordinal);
        foreach (var weapon in weapons)
        {
            var collected = collectionState.IsCollected(weapon.ItemId);
            var sources   = collected ? collectionState.GetSourcesForItem(weapon.ItemId) : [];
            foreach (var job in GetEquippableJobs(weapon.ItemId))
            {
                if (!jobItems.TryGetValue(job, out var list))
                {
                    list = [];
                    jobItems[job] = list;
                }
                list.Add((collected, sources));
            }
        }

        return jobItems.ToDictionary(
            kv => kv.Key,
            kv =>
            {
                var allCollected = kv.Value.All(w => w.Collected);
                var sources = allCollected
                    ? kv.Value.SelectMany(w => w.Sources).Distinct(StringComparer.Ordinal).ToArray()
                    : [];
                var items = weapons.Where(weapon => GetEquippableJobs(weapon.ItemId).Contains(kv.Key, StringComparer.Ordinal)).ToArray();
                return new WeaponJobEntry(allCollected, sources, items);
            },
            StringComparer.Ordinal);
    }

    private void DrawWeaponSummary(IReadOnlyList<GarlandLootItemInfo> allItems, bool interactive)
    {
        var weapons = allItems.Where(item => item.Slot is GearSlot.MainHand or GearSlot.OffHand).ToArray();
        if (weapons.Length == 0)
        {
            return;
        }

        var jobMap      = BuildWeaponJobMap(weapons);
        var presentJobs = JobOrder.Where(jobMap.ContainsKey).ToArray();
        if (presentJobs.Length == 0)
        {
            return;
        }

        var ownedJobs = presentJobs.Count(j => jobMap[j].Collected);
        ImGui.TextDisabled($"Weapons: {ownedJobs}/{presentJobs.Length}");

        const float iconSize = 32f;
        const float iconGap = 4f;
        const float contentWidth = 4 * 102f + 3 * 10f;
        var iconsPerRow = Math.Max(1, (int)MathF.Floor((contentWidth + iconGap) / (iconSize + iconGap)));

        ImGui.Spacing();
        var drawList = ImGui.GetWindowDrawList();
        for (var i = 0; i < presentJobs.Length; i++)
        {
            var job = presentJobs[i];
            var entry = jobMap[job];
            var iconPos = ImGui.GetCursorScreenPos();
            DrawJobIcon(drawList, iconPos, iconSize, job, entry.Collected, entry.Sources);
            if (interactive)
            {
                AddWeaponTryOnHitTarget(drawList, iconPos, iconSize, job, entry.Items);
            }
            ImGui.Dummy(new Vector2(iconSize, iconSize));
            if (i < presentJobs.Length - 1 && i % iconsPerRow != iconsPerRow - 1)
            {
                ImGui.SameLine(0, iconGap);
            }
        }
    }

    private void AddWeaponTryOnHitTarget(ImDrawListPtr drawList, Vector2 position, float size, string job, IReadOnlyList<GarlandLootItemInfo> items)
    {
        var min = position;
        var max = position + new Vector2(size, size);
        if (!ImGui.IsMouseHoveringRect(min, max, false))
        {
            return;
        }

        interactiveTargetHovered = true;
        drawList.AddRect(min, max, 0xD0FFFFFF, 3f, ImDrawFlags.None, 2f);
        ImGui.SetTooltip($"Left-click: try on {job} weapon\nRight-click: add/replace weapon");
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            tryOnService.TryOnItems(items.Select(item => item.ItemId));
        }
        else if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            tryOnService.AddToTryOnItems(items.Select(item => item.ItemId));
        }
    }

    private void DrawJobIcon(ImDrawListPtr drawList, Vector2 position, float size, string job, bool collected, string[] sources)
    {
        var min = position;
        var max = position + new Vector2(size, size);
        var overlayTint = collected ? 0x4060FF80u : 0x406060FFu;
        var borderColor = collected ? 0xFF7DFF9Fu : 0xFF8585FFu;

        if (jobTextures.TryGetValue(job, out var sharedTexture) &&
            sharedTexture.TryGetWrap(out IDalamudTextureWrap? texture, out _))
        {
            drawList.AddImage(texture.Handle, min, max, Vector2.Zero, Vector2.One, 0xFFFFFFFF);
            drawList.AddRectFilled(min, max, overlayTint, 3f);
        }
        else
        {
            drawList.AddRectFilled(min, max, collected ? 0xA0308050u : 0xA0303080u, 3f);
            var labelSize = ImGui.CalcTextSize(job);
            drawList.AddText(min + (new Vector2(size, size) - labelSize) / 2f, 0xFFFFFFFF, job);
        }

        drawList.AddRect(min, max, borderColor, 3f);

        if (collected && sources.Length > 0)
        {
            DrawSourceBadge(drawList, min, GetPrioritySource(sources));
        }
    }

    private string[] GetEquippableJobs(uint itemId)
    {
        if (!dataManager.GetExcelSheet<Item>().TryGetRow(itemId, out var item) || !item.ClassJobCategory.IsValid)
        {
            return [];
        }

        var cjc = item.ClassJobCategory.Value;
        var result = new List<string>(4);
        if (cjc.PLD || cjc.GLA) result.Add("PLD");
        if (cjc.WAR || cjc.MRD) result.Add("WAR");
        if (cjc.DRK)             result.Add("DRK");
        if (cjc.GNB)             result.Add("GNB");
        if (cjc.WHM || cjc.CNJ) result.Add("WHM");
        if (cjc.SCH || cjc.ACN) result.Add("SCH");
        if (cjc.AST)             result.Add("AST");
        if (cjc.SGE)             result.Add("SGE");
        if (cjc.MNK || cjc.PGL) result.Add("MNK");
        if (cjc.DRG || cjc.LNC) result.Add("DRG");
        if (cjc.NIN || cjc.ROG) result.Add("NIN");
        if (cjc.SAM)             result.Add("SAM");
        if (cjc.RPR)             result.Add("RPR");
        if (cjc.VPR)             result.Add("VPR");
        if (cjc.BRD || cjc.ARC) result.Add("BRD");
        if (cjc.MCH)             result.Add("MCH");
        if (cjc.DNC)             result.Add("DNC");
        if (cjc.BLM || cjc.THM) result.Add("BLM");
        if (cjc.SMN || cjc.ACN) result.Add("SMN");
        if (cjc.RDM)             result.Add("RDM");
        if (cjc.PCT)             result.Add("PCT");
        return [.. result];
    }

    private static void DrawSourceLegend()
    {
        const float contentWidth = 4 * 102f + 3 * 10f;
        ImGui.Separator();
        ImGui.Spacing();
        DrawLegendRow(SourceLegend[..4], contentWidth);
        DrawLegendRow(SourceLegend[4..], contentWidth);
    }

    private static void DrawLegendRow((char Letter, string Name, uint Color)[] items, float contentWidth)
    {
        var drawList = ImGui.GetWindowDrawList();
        const float badgeSize = 13f;
        const float badgeTextGap = 3f;
        const float itemSpacing = 10f;
        var lineHeight = ImGui.GetTextLineHeight();

        var rowWidth = 0f;
        for (var i = 0; i < items.Length; i++)
        {
            rowWidth += badgeSize + badgeTextGap + ImGui.CalcTextSize(items[i].Name).X;
            if (i < items.Length - 1)
            {
                rowWidth += itemSpacing;
            }
        }
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0, (contentWidth - rowWidth) / 2f));

        for (var i = 0; i < items.Length; i++)
        {
            var (letter, name, color) = items[i];
            var badgeOffset = MathF.Max(0, (lineHeight - badgeSize) / 2f);
            var badgePos = ImGui.GetCursorScreenPos() + new Vector2(0, badgeOffset);
            drawList.AddRectFilled(badgePos, badgePos + new Vector2(badgeSize, badgeSize), color, 2f);
            var letterStr = letter.ToString();
            var letterSize = ImGui.CalcTextSize(letterStr);
            drawList.AddText(badgePos + (new Vector2(badgeSize, badgeSize) - letterSize) / 2f, 0xFF000000u, letterStr);
            ImGui.Dummy(new Vector2(badgeSize, lineHeight));
            ImGui.SameLine(0, badgeTextGap);
            ImGui.TextDisabled(name);
            if (i < items.Length - 1)
            {
                ImGui.SameLine(0, itemSpacing);
            }
        }
    }

    private static string GetPrioritySource(string[] sources)
    {
        foreach (var candidate in SourcePriority)
        {
            if (Array.IndexOf(sources, candidate) >= 0)
            {
                return candidate;
            }
        }
        foreach (var source in sources)
        {
            if (source.StartsWith("retainer:", StringComparison.Ordinal))
            {
                return source;
            }
        }
        return sources[0];
    }

    private static char GetSourceLetter(string source) => source switch
    {
        CollectionSource.Equipped       => 'E',
        CollectionSource.GlamourDresser => 'G',
        CollectionSource.Armoire        => 'C',
        CollectionSource.Inventory      => 'I',
        CollectionSource.ArmoryChest    => 'A',
        CollectionSource.ChocoboSaddlebag => 'S',
        _ => 'R',
    };

    private static uint GetSourceBadgeColor(string source) => source switch
    {
        CollectionSource.Equipped         => 0xFFFF8080u,
        CollectionSource.GlamourDresser   => 0xFFDC64B4u,
        CollectionSource.Armoire          => 0xFF00A5FFu,
        CollectionSource.Inventory        => 0xFF64DC64u,
        CollectionSource.ArmoryChest      => 0xFF32DCFFu,
        CollectionSource.ChocoboSaddlebag => 0xFFD2D200u,
        _ => 0xFF28C8FFu,
    };

    private static string GetSlotAbbreviation(GearSlot slot) => slot switch
    {
        GearSlot.Head     => "Hd",
        GearSlot.Body     => "Ch",
        GearSlot.Hands    => "Ha",
        GearSlot.Legs     => "Le",
        GearSlot.Feet     => "Ft",
        GearSlot.Ears     => "Ea",
        GearSlot.Neck     => "Ne",
        GearSlot.Wrists   => "Wr",
        GearSlot.Ring     => "Ri",
        GearSlot.MainHand => "W",
        GearSlot.OffHand  => "OH",
        _ => "?",
    };

    private static Dictionary<GearSlot, ISharedImmediateTexture> LoadSlotTextures(ITextureProvider textureProvider, string pluginDirectory)
    {
        var mediaDir = Path.Combine(pluginDirectory, "Media");
        return new Dictionary<GearSlot, ISharedImmediateTexture>
        {
            [GearSlot.Head]   = textureProvider.GetFromFile(Path.Combine(mediaDir, "slot_head.png")),
            [GearSlot.Body]   = textureProvider.GetFromFile(Path.Combine(mediaDir, "slot_body.png")),
            [GearSlot.Hands]  = textureProvider.GetFromFile(Path.Combine(mediaDir, "slot_hands.png")),
            [GearSlot.Legs]   = textureProvider.GetFromFile(Path.Combine(mediaDir, "slot_legs.png")),
            [GearSlot.Feet]   = textureProvider.GetFromFile(Path.Combine(mediaDir, "slot_feet.png")),
            [GearSlot.Ears]   = textureProvider.GetFromFile(Path.Combine(mediaDir, "slot_ears.png")),
            [GearSlot.Neck]   = textureProvider.GetFromFile(Path.Combine(mediaDir, "slot_neck.png")),
            [GearSlot.Wrists] = textureProvider.GetFromFile(Path.Combine(mediaDir, "slot_wrists.png")),
            [GearSlot.Ring]   = textureProvider.GetFromFile(Path.Combine(mediaDir, "slot_ring.png")),
        };
    }

    private static Dictionary<string, ISharedImmediateTexture> LoadJobTextures(ITextureProvider textureProvider, string pluginDirectory)
    {
        var mediaDir = Path.Combine(pluginDirectory, "Media");
        var result = new Dictionary<string, ISharedImmediateTexture>(StringComparer.Ordinal);
        foreach (var (abbr, filename) in JobIconFiles)
        {
            var path = Path.Combine(mediaDir, filename);
            if (File.Exists(path))
            {
                result[abbr] = textureProvider.GetFromFile(path);
            }
        }
        return result;
    }

    private sealed record WeaponJobEntry(bool Collected, string[] Sources, GarlandLootItemInfo[] Items);
}
