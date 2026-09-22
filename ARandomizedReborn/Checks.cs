using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace ARandomizedReborn;

/// <param name="RequiredUnlock">The unlock that must be granted before this check is even attemptable.</param>
public sealed record CheckDefinition(
    string Id,
    string DisplayName,
    string Description,
    UnlockKey? RequiredUnlock,
    IReadOnlyList<CheckStepDefinition> Steps)
{
    public bool IsFullyAutomatic => this.Steps.All(step => step.Automatic);
}

public static class Checks
{
    /// <summary>A single step the player ticks off themselves once they've done the thing.</summary>
    private static IReadOnlyList<CheckStepDefinition> Manual(string label)
        => [new("done", label, ProgressStepKind.Flag)];

    /// <summary>A single step the plugin detects and flips on its own.</summary>
    private static IReadOnlyList<CheckStepDefinition> Auto(string label)
        => [new("done", label, ProgressStepKind.Flag, Automatic: true)];

    private static IReadOnlyList<CheckStepDefinition> ManualCount(string id, string label, int target)
        => [new(id, label, ProgressStepKind.Counter, target)];

    private static IReadOnlyList<CheckStepDefinition> AutoCount(string id, string label, int target)
        => [new(id, label, ProgressStepKind.Counter, target, Automatic: true)];

    private static IReadOnlyList<CheckStepDefinition> Steps(params CheckStepDefinition[] steps) => steps;

    public static readonly IReadOnlyList<CheckDefinition> Definitions =
    [
        // No unlock required.
        new("pray-return-waking-sands", "Pray, return to the Waking Sands",
            "Travel from Ul'dah to the Waking Sands entirely by foot. Using Teleport or Return resets your progress.",
            null,
            Steps(
                new("left-uldah", "Start out in Ul'dah", ProgressStepKind.Flag, Automatic: true),
                new("arrived", "Arrive at the Waking Sands on foot", ProgressStepKind.Flag, Automatic: true))),
        new("point-blue-alisaie", "/point at Blue Alisaie",
            "Find Alisaie, target her and use the 'Point' emote.", null,
            Auto("Point at Blue Alisaie")),
        new("pet-graha-tia", "Pet the certified best boy",
            "Find G'raha Tia, target him and use the 'Pet' emote.", null,
            Auto("Pet G'raha Tia")),
        new("airship-city-state", "Take an airship to another city-state",
            "Use an airship landing to travel to a different city-state.", null,
            Manual("Arrive in another city-state via airship")),
        new("mark-bill-five-hunts", "Clear a full mark bill",
            "Complete all five hunts on a single (daily) regular mark bill.", null,
            ManualCount("hunts", "Hunts completed on the bill", 5)),
        new("tint-gear-same-color", "Tint all your gear the same color",
            "Dye every equipped piece of gear with the same dye.", null,
            AutoCount("matching", "Equipped pieces sharing one dye", 2)),
        new("fill-armory-category", "Fill an armory chest category",
            "Fill up every slot in one armory chest category.", null,
            Manual("Fill every slot in one armory chest category")),
        new("sit-bench-city-state", "Sit on a bench in any city-state",
            "Use a bench (not /sit on the ground) inside a city-state.", null,
            Manual("Sit on a bench in a city-state")),
        new("die-fall-damage", "Die from fall damage",
            "Take a lethal drop.", null,
            Manual("Die from fall damage")),
        new("cheer-same-job-player", "Cheer a colleague",
            "Find another player with the same job as you and /cheer them.", null,
            Auto("Cheer a player sharing your job")),
        new("flee-five-enemies-no-sprint", "Flee without Sprint",
            "Escape combat with five enemies on you without using Sprint.", null,
            Manual("Escape combat with 5 enemies without Sprint")),
        new("fate-with-player-nearby", "Team FATE",
            "Complete a FATE with at least one other player nearby.", null,
            Manual("Complete a FATE with another player nearby")),
        new("three-starting-aetherytes", "Tour the aetherytes",
            "Interact with all three starting city-state aetherytes without teleporting.", null,
            ManualCount("aetherytes", "Starting aetherytes interacted with", 3)),
        new("heal-hurt-player", "Heal a hurt player",
            "Heal an injured player in the open world.", null,
            Steps(
                new("target", "Target an injured player", ProgressStepKind.Flag, Automatic: true),
                new("heal", "Heal them", ProgressStepKind.Flag))),
        new("wave-gatherer", "Wave to a gatherer",
            "Find a gatherer in the open world and wave at them.", null,
            Manual("Wave to a gatherer")),

        // Housing.
        new("housing-sit-chair", "Sit in a chair in someone's house",
            "Enter another player's or FC house and sit in a chair.", UnlockKey.Housing,
            Manual("Sit in a chair in someone's house")),
        new("housing-guestbook-message", "Write a nice guestbook message",
            "Leave a friendly message in a house guestbook.", UnlockKey.Housing,
            Manual("Write a nice guestbook message")),

        // Allied societies.
        new("society-three-dailies", "Allied society regular",
            "Complete three daily quests for a single allied society.", UnlockKey.AlliedSocieties,
            ManualCount("dailies", "Daily quests completed for one society", 3)),

        // Deep dungeon.
        new("deepdungeon-step-trap", "Step on a trap",
            "Trigger any trap inside a deep dungeon.", UnlockKey.DeepDungeon,
            Manual("Step on a trap")),
        new("deepdungeon-clear-ten-floors", "Clear 10 floors",
            "Clear ten deep dungeon floors.", UnlockKey.DeepDungeon,
            ManualCount("floors", "Floors cleared", 10)),
        new("deepdungeon-die-to-boss", "Die to a deep dungeon boss",
            "Die to a boss fight before reaching the checkpoint.", UnlockKey.DeepDungeon,
            Manual("Die to a boss before the checkpoint")),

        // Inn rooms.
        new("inn-unending-journey-cutscene", "The Unending Journey",
            "Watch any cutscene from The Unending Journey.", UnlockKey.InnRooms,
            Manual("Watch a cutscene from The Unending Journey")),
        new("inn-change-hairstyle", "Change your hairstyle",
            "Use the aesthetician to change your hairstyle.", UnlockKey.InnRooms,
            Manual("Change your hairstyle")),
        new("inn-toy-chest-highscore", "Toy chest highscore",
            "Get a highscore in any toy chest minigame.", UnlockKey.InnRooms,
            Manual("Get a toy chest highscore")),

        // Gold Saucer.
        new("goldsaucer-mini-cactpot", "Play the Mini Cactpot",
            "Buy and play a Mini Cactpot ticket.", UnlockKey.GoldSaucer,
            Manual("Play a Mini Cactpot ticket")),
        new("goldsaucer-triple-triad-win", "Win a match of Triple Triad",
            "Beat any Triple Triad opponent.", UnlockKey.GoldSaucer,
            Manual("Win a Triple Triad match")),
        new("goldsaucer-gate-fail-fast", "Fail a GATE fast",
            "Fail a GATE within the first 30 seconds.", UnlockKey.GoldSaucer,
            Manual("Fail a GATE within 30 seconds")),

        // Gatherers.
        new("gather-time-restricted-fish", "Catch a timed fish",
            "Fish up a fish that only occurs during a specific time of day.", UnlockKey.Gatherers,
            Manual("Catch a time-restricted fish")),
        new("gather-five-ores", "Gather five kinds of ore",
            "Mine five different kinds of ore.", UnlockKey.Gatherers,
            ManualCount("ores", "Different kinds of ore gathered", 5)),
        new("gather-three-zones", "Harvest across three zones",
            "Harvest items from three different zones.", UnlockKey.Gatherers,
            ManualCount("zones", "Different zones harvested in", 3)),
        new("gather-thousand-resource", "Overflow a stack",
            "Gather enough of one resource that it takes up two inventory slots (1000).", UnlockKey.Gatherers,
            AutoCount("stack", "Highest single-item stack gathered", 1000)),

        // Crafters.
        new("craft-and-eat-food", "Craft food, then eat it",
            "Craft any meal and consume it.", UnlockKey.Crafters,
            Steps(
                new("crafted", "Craft a meal", ProgressStepKind.Flag),
                new("eaten", "Eat it (Well Fed)", ProgressStepKind.Flag, Automatic: true))),

        // Trials.
        new("trial-solo-unsynced-low-hp", "Down to the wire",
            "Watch the full intro cutscene, then solo an unsynced trial and win with less than 10% HP.", UnlockKey.Trials,
            Steps(
                new("cutscene", "Watch the full intro cutscene", ProgressStepKind.Flag),
                new("win", "Win solo, unsynced, under 10% HP", ProgressStepKind.Flag))),
        new("trial-fall-off-arena", "Fall off an arena",
            "Take the scenic route off a trial arena.", UnlockKey.Trials,
            Manual("Fall off a trial arena")),

        // Dungeons.
        new("dungeon-greed-win", "Greed and win",
            "Roll 'Greed' on a dungeon item and win it.", UnlockKey.Dungeons,
            Manual("Greed roll and win a dungeon item")),
        new("dungeon-no-deaths", "Flawless run",
            "Finish a dungeon with no deaths.", UnlockKey.Dungeons,
            Manual("Finish a dungeon with no deaths")),
        new("dungeon-final-boss-aoe-hit", "Eat the telegraph",
            "Get hit by a dungeon final boss' AOE attack.", UnlockKey.Dungeons,
            Manual("Get hit by a dungeon final boss' AOE")),

        // Retainers.
        new("retainer-market-flip", "Market board flip",
            "Buy something from the market board and successfully resell it, even at a loss.", UnlockKey.Retainers,
            Steps(
                new("bought", "Buy an item from the market board", ProgressStepKind.Flag),
                new("resold", "Resell it", ProgressStepKind.Flag))),
        new("retainer-venture-complete", "Complete a venture",
            "Start a new venture and wait for it to complete.", UnlockKey.Retainers,
            Steps(
                new("started", "Start a new venture", ProgressStepKind.Flag),
                new("completed", "Wait for it to complete", ProgressStepKind.Flag))),
        new("retainer-empty-inventory", "Empty your inventory",
            "Move everything out of your inventory.", UnlockKey.Retainers,
            Manual("Empty your inventory")),
        new("retainer-fill-sale-slots", "Fill all sale slots",
            "Fill up all 20 of one retainer's sale slots.", UnlockKey.Retainers,
            ManualCount("slots", "Retainer sale slots filled", 20)),

        // Mounts / chocobo.
        new("chocobo-revenge", "Chocobo revenge",
            "Have your chocobo companion die and take revenge on the enemies.", UnlockKey.Mounts,
            Manual("Have your chocobo die and take revenge")),
        new("mount-indoors", "Ride a mount indoors",
            "Mount up indoors somewhere that allows it.", UnlockKey.Mounts,
            Steps(
                new("mounted", "Mount up", ProgressStepKind.Flag, Automatic: true),
                new("indoors", "While indoors somewhere it's allowed", ProgressStepKind.Flag, Automatic: true))),

        // Teleport / Return.
        new("cheese-on-the-moon", "Cheese on the moon",
            "Consume a cheese consumable while on the moon.", UnlockKey.TeleportReturn,
            Manual("Eat cheese while on the moon")),
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
