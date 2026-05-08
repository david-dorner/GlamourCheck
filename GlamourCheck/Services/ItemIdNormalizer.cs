namespace GlamourCheck.Services;

/// <summary>
/// Dependency-free item ID normalization for HQ and glamour-variant item IDs.
/// Lumina-aware validation is layered on top in ItemIdentityService.
/// </summary>
public static class ItemIdNormalizer
{
    public const uint HqItemOffset = 1_000_000;
    public const uint ItemVariantModulo = 500_000;

    public static uint NormalizeWithoutSheetLookup(uint itemId)
    {
        if (itemId == 0)
        {
            return 0;
        }

        if (itemId >= HqItemOffset)
        {
            return itemId % HqItemOffset;
        }

        if (itemId >= ItemVariantModulo)
        {
            return itemId % ItemVariantModulo;
        }

        return itemId;
    }
}
