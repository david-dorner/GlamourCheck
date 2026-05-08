using Dalamud.Configuration;
using System;

namespace GlamourCheck;

[Serializable]
/// <summary>
/// Persisted Dalamud configuration. Keep this intentionally small; most runtime state
/// belongs in SQLite or service caches.
/// </summary>
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool EnableGearIconOverlays { get; set; } = true;
    public bool EnableInstanceSummary { get; set; } = true;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
