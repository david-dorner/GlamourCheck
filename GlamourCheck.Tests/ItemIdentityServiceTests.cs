using GlamourCheck.Services;

namespace GlamourCheck.Tests;

public sealed class ItemIdentityServiceTests
{
    [Theory]
    [InlineData(1234, 1234)]
    [InlineData(1_001_234, 1234)]
    [InlineData(501_234, 1234)]
    public void NormalizeItemId_RemovesKnownVariantOffsets(uint rawItemId, uint expectedItemId)
    {
        Assert.Equal(expectedItemId, ItemIdNormalizer.NormalizeWithoutSheetLookup(rawItemId));
    }
}
