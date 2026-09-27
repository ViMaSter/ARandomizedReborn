using System;
using System.Collections.Generic;

namespace ARandomizedReborn;

public enum UnlockKey
{
    Housing,
    AlliedSocieties,
    DeepDungeon,
    InnRooms,
    GoldSaucer,
    Gatherers,
    Crafters,
    EnemyCastBars,
    Maps,
    Trials,
    AutoRun,
    Dungeons,
    Retainers,
    Mounts,
    Sprint,
    TeleportReturn,
}

public sealed record UnlockDefinition(UnlockKey Key, string DisplayName, string Description);

public static class Unlocks
{
    public static readonly IReadOnlyList<UnlockDefinition> Definitions =
    [
        new(UnlockKey.Housing, "Unlock visiting player / FC houses", "Allows entering player and Free Company houses."),
        new(UnlockKey.AlliedSocieties, "Unlock beast tribes / allied societies", "Allows starting, continuing, and completing allied society quests."),
        new(UnlockKey.DeepDungeon, "Unlock Palace of the Dead / deep dungeon", "Allows opening and entering deep dungeon content."),
        new(UnlockKey.InnRooms, "Unlock inn rooms", "Allows entering inn rooms."),
        new(UnlockKey.GoldSaucer, "Unlock Gold Saucer", "Allows Gold Saucer travel and interactions."),
        new(UnlockKey.Gatherers, "Unlock gatherers", "Allows gatherer jobs and gathering actions."),
        new(UnlockKey.Crafters, "Unlock crafters", "Allows crafter jobs and crafting actions."),
        new(UnlockKey.EnemyCastBars, "Unlock enemy cast bars + attack names", "Allows enemy cast bars and attack names to show according to your normal settings."),
        new(UnlockKey.Maps, "Unlock maps and minimap", "Allows maps and minimap to show according to your normal settings."),
        new(UnlockKey.Trials, "Unlock trials", "Allows queueing for trials."),
        new(UnlockKey.AutoRun, "Unlock auto-run", "Allows keyboard and gamepad auto-run."),
        new(UnlockKey.Dungeons, "Unlock dungeons", "Allows queueing for dungeons."),
        new(UnlockKey.Retainers, "Unlock retainer access", "Allows retainer bell and retainer access."),
        new(UnlockKey.Mounts, "Unlock mount + chocobo", "Allows mounting and chocobo companion access."),
        new(UnlockKey.Sprint, "Unlock sprint", "Allows Sprint."),
        new(UnlockKey.TeleportReturn, "Unlock teleport + return", "Allows Teleport and Return."),
    ];

    public static bool Get(Configuration configuration, UnlockKey key)
        => key switch
        {
            UnlockKey.Housing => configuration.UnlockHousing,
            UnlockKey.AlliedSocieties => configuration.UnlockAlliedSocieties,
            UnlockKey.DeepDungeon => configuration.UnlockDeepDungeon,
            UnlockKey.InnRooms => configuration.UnlockInnRooms,
            UnlockKey.GoldSaucer => configuration.UnlockGoldSaucer,
            UnlockKey.Gatherers => configuration.UnlockGatherers,
            UnlockKey.Crafters => configuration.UnlockCrafters,
            UnlockKey.EnemyCastBars => configuration.UnlockEnemyCastBars,
            UnlockKey.Maps => configuration.UnlockMaps,
            UnlockKey.Trials => configuration.UnlockTrials,
            UnlockKey.AutoRun => configuration.UnlockAutoRun,
            UnlockKey.Dungeons => configuration.UnlockDungeons,
            UnlockKey.Retainers => configuration.UnlockRetainers,
            UnlockKey.Mounts => configuration.UnlockMounts,
            UnlockKey.Sprint => configuration.UnlockSprint,
            UnlockKey.TeleportReturn => configuration.UnlockTeleportReturn,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, null),
        };

    public static void Set(Configuration configuration, UnlockKey key, bool value)
    {
        switch (key)
        {
            case UnlockKey.Housing:
                configuration.UnlockHousing = value;
                break;
            case UnlockKey.AlliedSocieties:
                configuration.UnlockAlliedSocieties = value;
                break;
            case UnlockKey.DeepDungeon:
                configuration.UnlockDeepDungeon = value;
                break;
            case UnlockKey.InnRooms:
                configuration.UnlockInnRooms = value;
                break;
            case UnlockKey.GoldSaucer:
                configuration.UnlockGoldSaucer = value;
                break;
            case UnlockKey.Gatherers:
                configuration.UnlockGatherers = value;
                break;
            case UnlockKey.Crafters:
                configuration.UnlockCrafters = value;
                break;
            case UnlockKey.EnemyCastBars:
                configuration.UnlockEnemyCastBars = value;
                break;
            case UnlockKey.Maps:
                configuration.UnlockMaps = value;
                break;
            case UnlockKey.Trials:
                configuration.UnlockTrials = value;
                break;
            case UnlockKey.AutoRun:
                configuration.UnlockAutoRun = value;
                break;
            case UnlockKey.Dungeons:
                configuration.UnlockDungeons = value;
                break;
            case UnlockKey.Retainers:
                configuration.UnlockRetainers = value;
                break;
            case UnlockKey.Mounts:
                configuration.UnlockMounts = value;
                break;
            case UnlockKey.Sprint:
                configuration.UnlockSprint = value;
                break;
            case UnlockKey.TeleportReturn:
                configuration.UnlockTeleportReturn = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(key), key, null);
        }
    }
}