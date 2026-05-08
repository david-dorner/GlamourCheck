namespace GlamourCheck.Services;

/// <summary>
/// Stable source keys used in SQLite and in collection-state source summaries.
/// </summary>
public static class CollectionSource
{
    public const string Inventory = "inventory";
    public const string Equipped = "equipped";
    public const string ArmoryChest = "armory_chest";
    public const string GlamourDresser = "glamour_dresser";
    public const string Armoire = "armoire";
    public const string ChocoboSaddlebag = "chocobo_saddlebag";

    public static string Retainer(string retainerId) => $"retainer:{retainerId}";
}
