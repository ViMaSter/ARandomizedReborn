using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace ARandomizedReborn;

/// <param name="RequiredUnlock">The unlock that must be granted before this check is even attemptable.</param>
/// <param name="HasAutomaticDetection">False when the check can only be completed through a manual board override.</param>
public sealed record CheckDefinition(
    string Id,
    string DisplayName,
    string Description,
    UnlockKey? RequiredUnlock,
    bool HasAutomaticDetection = false);

public static class Checks
{
    public static readonly IReadOnlyList<CheckDefinition> Definitions =
    [
        // No unlock required.
        new("pray-return-waking-sands", "Pray, return to the Waking Sands",
            "Travel from Ul'dah to the Waking Sands entirely by foot.", null),
        new("point-blue-alisaie", "/point at Blue Alisaie",
            "Find Alisaie, target her and use the 'Point' emote.", null, HasAutomaticDetection: true),
        new("pet-graha-tia", "Pet the certified best boy",
            "Find G'raha Tia, target him and use the 'Pet' emote.", null, HasAutomaticDetection: true),
        new("airship-city-state", "Take an airship to another city-state",
            "Use an airship landing to travel to a different city-state.", null),
        new("mark-bill-five-hunts", "Clear a full mark bill",
            "Complete all five hunts on a single (daily) regular mark bill.", null),
        new("tint-gear-same-color", "Tint all your gear the same color",
            "Dye every equipped piece of gear with the same dye.", null),
        new("fill-armory-category", "Fill an armory chest category",
            "Fill up every slot in one armory chest category.", null),
        new("sit-bench-city-state", "Sit on a bench in any city-state",
            "Use a bench (not /sit on the ground) inside a city-state.", null),
        new("die-fall-damage", "Die from fall damage",
            "Take a lethal drop.", null),
        new("cheer-same-job-player", "Cheer a colleague",
            "Find another player with the same job as you and /cheer them.", null),
        new("flee-five-enemies-no-sprint", "Flee without Sprint",
            "Escape combat with five enemies on you without using Sprint.", null),
        new("fate-with-player-nearby", "Team FATE",
            "Complete a FATE with at least one other player nearby.", null),
        new("three-starting-aetherytes", "Tour the aetherytes",
            "Interact with all three starting city-state aetherytes without teleporting.", null),
        new("heal-hurt-player", "Heal a hurt player",
            "Heal an injured player in the open world.", null),
        new("wave-gatherer", "Wave to a gatherer",
            "Find a gatherer in the open world and wave at them.", null),

        // Housing.
        new("housing-sit-chair", "Sit in a chair in someone's house",
            "Enter another player's or FC house and sit in a chair.", UnlockKey.Housing),
        new("housing-guestbook-message", "Write a nice guestbook message",
            "Leave a friendly message in a house guestbook.", UnlockKey.Housing),

        // Allied societies.
        new("society-three-dailies", "Allied society regular",
            "Complete three daily quests for a single allied society.", UnlockKey.AlliedSocieties),

        // Deep dungeon.
        new("deepdungeon-step-trap", "Step on a trap",
            "Trigger any trap inside a deep dungeon.", UnlockKey.DeepDungeon),
        new("deepdungeon-clear-ten-floors", "Clear 10 floors",
            "Clear ten deep dungeon floors.", UnlockKey.DeepDungeon),
        new("deepdungeon-die-to-boss", "Die to a deep dungeon boss",
            "Die to a boss fight before reaching the checkpoint.", UnlockKey.DeepDungeon),

        // Inn rooms.
        new("inn-unending-journey-cutscene", "The Unending Journey",
            "Watch any cutscene from The Unending Journey.", UnlockKey.InnRooms),
        new("inn-change-hairstyle", "Change your hairstyle",
            "Use the aesthetician to change your hairstyle.", UnlockKey.InnRooms),
        new("inn-toy-chest-highscore", "Toy chest highscore",
            "Get a highscore in any toy chest minigame.", UnlockKey.InnRooms),

        // Gold Saucer.
        new("goldsaucer-mini-cactpot", "Play the Mini Cactpot",
            "Buy and play a Mini Cactpot ticket.", UnlockKey.GoldSaucer),
        new("goldsaucer-triple-triad-win", "Win a match of Triple Triad",
            "Beat any Triple Triad opponent.", UnlockKey.GoldSaucer),
        new("goldsaucer-gate-fail-fast", "Fail a GATE fast",
            "Fail a GATE within the first 30 seconds.", UnlockKey.GoldSaucer),

        // Gatherers.
        new("gather-time-restricted-fish", "Catch a timed fish",
            "Fish up a fish that only occurs during a specific time of day.", UnlockKey.Gatherers),
        new("gather-five-ores", "Gather five kinds of ore",
            "Mine five different kinds of ore.", UnlockKey.Gatherers),
        new("gather-three-zones", "Harvest across three zones",
            "Harvest items from three different zones.", UnlockKey.Gatherers),
        new("gather-thousand-resource", "Overflow a stack",
            "Gather enough of one resource that it takes up two inventory slots (1000).", UnlockKey.Gatherers),

        // Crafters.
        new("craft-and-eat-food", "Craft food, then eat it",
            "Craft any meal and consume it.", UnlockKey.Crafters),

        // Trials.
        new("trial-solo-unsynced-low-hp", "Down to the wire",
            "Watch the full intro cutscene, then solo an unsynced trial and win with less than 10% HP.", UnlockKey.Trials),
        new("trial-fall-off-arena", "Fall off an arena",
            "Take the scenic route off a trial arena.", UnlockKey.Trials),

        // Dungeons.
        new("dungeon-greed-win", "Greed and win",
            "Roll 'Greed' on a dungeon item and win it.", UnlockKey.Dungeons),
        new("dungeon-no-deaths", "Flawless run",
            "Finish a dungeon with no deaths.", UnlockKey.Dungeons),
        new("dungeon-final-boss-aoe-hit", "Eat the telegraph",
            "Get hit by a dungeon final boss' AOE attack.", UnlockKey.Dungeons),

        // Retainers.
        new("retainer-market-flip", "Market board flip",
            "Buy something from the market board and successfully resell it, even at a loss.", UnlockKey.Retainers),
        new("retainer-venture-complete", "Complete a venture",
            "Start a new venture and wait for it to complete.", UnlockKey.Retainers),
        new("retainer-empty-inventory", "Empty your inventory",
            "Move everything out of your inventory.", UnlockKey.Retainers),
        new("retainer-fill-sale-slots", "Fill all sale slots",
            "Fill up all 20 of one retainer's sale slots.", UnlockKey.Retainers),

        // Mounts / chocobo.
        new("chocobo-revenge", "Chocobo revenge",
            "Have your chocobo companion die and take revenge on the enemies.", UnlockKey.Mounts),
        new("mount-indoors", "Ride a mount indoors",
            "Mount up indoors somewhere that allows it.", UnlockKey.Mounts),

        // Teleport / Return.
        new("cheese-on-the-moon", "Cheese on the moon",
            "Consume a cheese consumable while on the moon.", UnlockKey.TeleportReturn),
    ];

    public static readonly FrozenDictionary<string, CheckDefinition> ById =
        Definitions.ToFrozenDictionary(definition => definition.Id);

    public static readonly IReadOnlyList<CheckDefinition> FreeChecks =
        Definitions.Where(definition => definition.RequiredUnlock == null).ToArray();

    public static readonly IReadOnlyList<CheckDefinition> GatedChecks =
        Definitions.Where(definition => definition.RequiredUnlock != null).ToArray();

    public static CheckDefinition? Find(string id)
        => ById.TryGetValue(id, out var definition) ? definition : null;

    public static string DisplayName(string id)
        => Find(id)?.DisplayName ?? id;
}
