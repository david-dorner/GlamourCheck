namespace GlamourCheck.Services;

public sealed record GearItemInfo(
    uint ItemId,
    GearSlot Slot,
    string ArmorCategory,
    string Name,
    uint IconId
);
