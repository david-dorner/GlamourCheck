namespace GlamourCheck.Services;

/// <summary>
/// Lightweight wrapper around persisted Dalamud configuration.
/// </summary>
public sealed class ConfigurationService
{
    public ConfigurationService(Configuration configuration)
    {
        Configuration = configuration;
    }

    public Configuration Configuration { get; }

    public void Save()
    {
        Configuration.Save();
    }
}
