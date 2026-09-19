using Dalamud.Configuration;
using System;

namespace ARandomizedReborn;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;
    public bool SomePropertyToBeSavedAndWithADefault { get; set; } = true;
    public bool DisableSprint { get; set; } = false;
    public int SprintHighlightRed { get; set; } = 90;
    public int SprintHighlightMultiply { get; set; } = 30;

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
