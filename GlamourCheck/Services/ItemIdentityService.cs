namespace GlamourCheck.Services;

using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using System;

/// <summary>
/// Normalizes item IDs and resolves gear metadata from Lumina.
/// Role/category inference comes from equip permissions, not item-name suffixes.
/// </summary>
public sealed class ItemIdentityService
{
    private readonly IDataManager? dataManager;

    public ItemIdentityService(IDataManager? dataManager = null)
    {
        this.dataManager = dataManager;
    }

    public uint NormalizeItemId(uint itemId)
    {
        if (itemId == 0)
        {
            return 0;
        }

        if (dataManager is not null)
        {
            if (ItemExists(itemId))
            {
                return itemId;
            }

            var hqNormalized = itemId % ItemIdNormalizer.HqItemOffset;
            if (hqNormalized != 0 && ItemExists(hqNormalized))
            {
                return hqNormalized;
            }

            var variantNormalized = itemId % ItemIdNormalizer.ItemVariantModulo;
            if (variantNormalized != 0 && ItemExists(variantNormalized))
            {
                return variantNormalized;
            }
        }

        return ItemIdNormalizer.NormalizeWithoutSheetLookup(itemId);
    }

    public bool TryGetGearItemInfo(uint rawItemId, out GearItemInfo gearItemInfo)
    {
        gearItemInfo = null!;

        if (dataManager is null)
        {
            return false;
        }

        var normalizedItemId = NormalizeItemId(rawItemId);
        if (!dataManager.GetExcelSheet<Item>().TryGetRow(normalizedItemId, out var item))
        {
            return false;
        }

        var slot = ResolveSlot(item);
        if (slot == GearSlot.Unknown)
        {
            return false;
        }

        var name = item.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("Dated", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        gearItemInfo = new GearItemInfo(
            normalizedItemId,
            slot,
            ResolveArmorCategory(item, slot),
            name,
            item.Icon);
        return true;
    }

    public bool IsGearItem(uint rawItemId)
    {
        return TryGetGearItemInfo(rawItemId, out _);
    }

    public uint? TryGetItemLevel(uint itemId)
    {
        if (dataManager is null || !dataManager.GetExcelSheet<Item>().TryGetRow(NormalizeItemId(itemId), out var item))
        {
            return null;
        }

        return item.LevelItem.RowId;
    }

    private static GearSlot ResolveSlot(Item item)
    {
        if (!item.EquipSlotCategory.IsValid)
        {
            return GearSlot.Unknown;
        }

        var category = item.EquipSlotCategory.Value;
        if (category.MainHand > 0) return GearSlot.MainHand;
        if (category.OffHand > 0) return GearSlot.OffHand;
        if (category.Head > 0) return GearSlot.Head;
        if (category.Body > 0) return GearSlot.Body;
        if (category.Gloves > 0) return GearSlot.Hands;
        if (category.Legs > 0) return GearSlot.Legs;
        if (category.Feet > 0) return GearSlot.Feet;
        if (category.Ears > 0) return GearSlot.Ears;
        if (category.Neck > 0) return GearSlot.Neck;
        if (category.Wrists > 0) return GearSlot.Wrists;
        if (category.FingerR > 0 || category.FingerL > 0) return GearSlot.Ring;
        return GearSlot.Unknown;
    }

    private bool ItemExists(uint itemId)
    {
        return dataManager!.GetExcelSheet<Item>().TryGetRow(itemId, out _);
    }

    private static string ResolveArmorCategory(Item item, GearSlot slot)
    {
        if (slot is GearSlot.MainHand or GearSlot.OffHand)
        {
            return "Weapon";
        }

        if (!item.ClassJobCategory.IsValid)
        {
            return "Unknown";
        }

        var cjc = item.ClassJobCategory.Value;

        var hasTanks = cjc.GLA || cjc.PLD || cjc.MRD || cjc.WAR || cjc.DRK || cjc.GNB;
        var hasHealers = cjc.CNJ || cjc.WHM || cjc.SCH || cjc.AST || cjc.SGE;
        var hasCasters = cjc.THM || cjc.BLM || cjc.ACN || cjc.SMN || cjc.RDM || cjc.PCT;
        var hasAiming = cjc.ARC || cjc.BRD || cjc.MCH || cjc.DNC;
        var hasScouting = cjc.ROG || cjc.NIN || cjc.VPR;
        var hasStriking = cjc.PGL || cjc.MNK || cjc.SAM;
        var hasMaiming = cjc.LNC || cjc.DRG || cjc.RPR;

        var groupCount = (hasTanks ? 1 : 0) + (hasHealers ? 1 : 0) + (hasCasters ? 1 : 0)
                       + (hasAiming ? 1 : 0) + (hasScouting ? 1 : 0) + (hasStriking ? 1 : 0) + (hasMaiming ? 1 : 0);

        if (groupCount == 1)
        {
            if (hasTanks) return "Fending";
            if (hasHealers) return "Healing";
            if (hasCasters) return "Casting";
            if (hasAiming) return "Aiming";
            if (hasScouting) return "Scouting";
            if (hasStriking) return "Striking";
            if (hasMaiming) return "Maiming";
        }

        if (hasHealers && hasCasters && !hasTanks && !hasAiming && !hasScouting && !hasStriking && !hasMaiming)
        {
            return "Magic";
        }

        if (hasTanks && hasHealers && hasCasters && hasAiming && hasScouting && hasStriking && hasMaiming)
        {
            return "All";
        }

        if (hasTanks && hasAiming && hasScouting && hasStriking && hasMaiming && !hasHealers && !hasCasters)
        {
            return "War";
        }

        if (!hasTanks && hasAiming && hasScouting && hasStriking && hasMaiming && !hasHealers && !hasCasters)
        {
            return "PhysicalDps";
        }

        if (hasStriking && hasMaiming && !hasTanks && !hasHealers && !hasCasters && !hasAiming && !hasScouting)
        {
            return "Slaying";
        }

        if (hasAiming && hasScouting && !hasTanks && !hasHealers && !hasCasters && !hasStriking && !hasMaiming)
        {
            return "AimingScouting";
        }

        return "Unknown";
    }
}
