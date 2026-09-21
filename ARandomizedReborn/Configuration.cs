using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace ARandomizedReborn;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;
    public bool EnableRandomizer { get; set; } = false;
    public bool DisableSprint { get; set; } = false;
    public bool UnlockHousing { get; set; } = false;
    public bool UnlockAlliedSocieties { get; set; } = false;
    public bool UnlockDeepDungeon { get; set; } = false;
    public bool UnlockInnRooms { get; set; } = false;
    public bool UnlockGoldSaucer { get; set; } = false;
    public bool UnlockGatherers { get; set; } = false;
    public bool UnlockCrafters { get; set; } = false;
    public bool UnlockEnemyCastBars { get; set; } = false;
    public bool UnlockMaps { get; set; } = false;
    public bool UnlockTrials { get; set; } = false;
    public bool UnlockAutoRun { get; set; } = false;
    public bool UnlockDungeons { get; set; } = false;
    public bool UnlockRetainers { get; set; } = false;
    public bool UnlockMounts { get; set; } = false;
    public bool UnlockSprint { get; set; } = false;
    public bool UnlockTeleportReturn { get; set; } = false;
    public BingoDifficulty BingoDifficulty { get; set; } = BingoDifficulty.Medium;
    public List<BingoCell> BingoBoard { get; set; } = [];
    public HashSet<string> CompletedChecks { get; set; } = [];
    public HashSet<string> ManuallyCompletedChecks { get; set; } = [];
    public bool BingoWon { get; set; } = false;
    public int SkillLevelCap { get; set; } = 100;
    public int SprintHighlightRed { get; set; } = 90;
    public int SprintHighlightMultiply { get; set; } = 30;

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
