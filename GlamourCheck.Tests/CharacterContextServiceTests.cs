using GlamourCheck.Services;

namespace GlamourCheck.Tests;

public sealed class CharacterContextServiceTests
{
    [Fact]
    public void CreateCharacterKey_UsesStableContentIdFormat()
    {
        Assert.Equal("content:0123456789ABCDEF", CharacterContextService.CreateCharacterKey(0x0123456789ABCDEF));
    }
}
