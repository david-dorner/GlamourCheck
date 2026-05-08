using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace GlamourCheck.Services;

/// <summary>
/// Thin wrapper around the native fitting-room agent. Use this instead of simulating context-menu clicks.
/// </summary>
public sealed class GearTryOnService
{
    public unsafe void TryOnItems(IEnumerable<uint> itemIds)
    {
        var firstItem = true;
        foreach (var itemId in itemIds.Where(itemId => itemId != 0).Distinct())
        {
            TryOnItemInternal(itemId, append: !firstItem);
            firstItem = false;
        }
    }

    public void TryOnItem(uint itemId)
    {
        TryOnItems([itemId]);
    }

    public void AddToTryOnItems(IEnumerable<uint> itemIds)
    {
        foreach (var itemId in itemIds.Where(itemId => itemId != 0).Distinct())
        {
            TryOnItemInternal(itemId, append: true);
        }
    }

    public void AddToTryOnItem(uint itemId)
    {
        AddToTryOnItems([itemId]);
    }

    private static unsafe void TryOnItemInternal(uint itemId, bool append)
    {
        var agent = AgentTryon.Instance();
        if (agent is not null)
        {
            agent->SaveDeleteOutfit = append;
        }

        AgentTryon.TryOn(0, itemId % 1_000_000u);

        if (agent is not null)
        {
            agent->SaveDeleteOutfit = true;
        }
    }
}
