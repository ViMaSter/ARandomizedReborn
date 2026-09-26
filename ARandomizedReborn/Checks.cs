using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace ARandomizedReborn;

/// <summary>A game event that, when observed, cancels any in-progress steps for checks declaring it.</summary>
public enum BreakingTrigger
{
    /// <summary>Player used Teleport, Return, or a teleport ticket item.</summary>
    TeleportOrReturn,
}

/// <param name="Reason">Shown to the user (e.g. via toast) when this rule cancels progress.</param>
public sealed record BreakingRule(BreakingTrigger Trigger, string Reason);

/// <param name="RequiredUnlock">The unlock that must be granted before this check is even attemptable.</param>
/// <param name="JournalArea">Location heading used to group this check in the Journal's completed view.</param>
/// <param name="JournalIcon">Icon shown for this check in the Journal list and detail view.</param>
/// <param name="BreakingRules">Game events that reset this check's progress back to zero.</param>
public sealed record CheckDefinition(
    string Id,
    string DisplayName,
    string Description,
    UnlockKey? RequiredUnlock,
    string JournalArea,
    uint JournalIcon,
    IReadOnlyList<CheckStepDefinition> Steps,
    IReadOnlyList<BreakingRule>? BreakingRules = null)
{
    public bool IsFullyAutomatic => this.Steps.All(step => step.Automatic);

    public IReadOnlyList<BreakingRule> Breaks => this.BreakingRules ?? [];

}

public static class Checks
{
    // Journal genre / content type icon ids.
    private const uint QuestIcon = 61419;
    private const uint ScenarioIcon = 61412;
    private const uint DungeonIcon = 61801;
    private const uint TrialIcon = 61804;
    private const uint FateIcon = 61809;
    private const uint CompanionIcon = 61813;
    private const uint SocietyIcon = 61814;
    private const uint GathererIcon = 61815;
    private const uint CommendationIcon = 61817;
    private const uint RetainerIcon = 61818;
    private const uint HuntIcon = 61819;
    private const uint GoldSaucerIcon = 61820;
    private const uint DeepDungeonIcon = 61824;
    private const uint CulinarianIcon = 62317;
    private const uint MinerIcon = 62318;
    private const uint BotanistIcon = 62319;
    private const uint FisherIcon = 62320;
    /// <summary>A single step the plugin detects and flips on its own.</summary>
    private static IReadOnlyList<CheckStepDefinition> Auto(string label)
        => [new("done", label, ProgressStepKind.Flag, Automatic: true)];

    private static IReadOnlyList<CheckStepDefinition> AutoCount(string id, string label, int target)
        => [new(id, label, ProgressStepKind.Counter, target, Automatic: true)];

    private static IReadOnlyList<CheckStepDefinition> Steps(params CheckStepDefinition[] steps) => steps;

    private static IReadOnlyList<BreakingRule> Breaks(params BreakingRule[] rules) => rules;

    public static readonly IReadOnlyList<CheckDefinition> Definitions =
    [
        // No unlock required.
        new("pray-return-waking-sands", "Pray, return to the Waking Sands",
            "Travel from Ul'dah to the Waking Sands entirely by foot. Using Teleport or Return resets your progress.",
            null, "Western Thanalan", ScenarioIcon,
            Steps(
                new("left-uldah", "Start out in Ul'dah", ProgressStepKind.Flag, Automatic: true),
                new("arrived", "Arrive at the Waking Sands on foot", ProgressStepKind.Flag, Automatic: true)),
            Breaks(new BreakingRule(BreakingTrigger.TeleportOrReturn, "used Teleport or Return"))),
        new("point-blue-alisaie", "/point at Blue Alisaie",
            "Find Alisaie, target her and use the 'Point' emote.", null, "Anywhere", ScenarioIcon,
            Auto("Point at Blue Alisaie")),
        new("pet-graha-tia", "Pet the certified best boy",
            "Find G'raha Tia, target him and use the 'Pet' emote.", null, "Anywhere", ScenarioIcon,
            Auto("Pet G'raha Tia")),
        new("airship-city-state", "Take an airship to another city-state",
            "Use an airship landing to travel to a different city-state.", null, "City-states", QuestIcon,
            Auto("Arrive in another city-state via airship")),
        new("mark-bill-five-hunts", "Clear a full mark bill",
            "Complete every target on an obtained daily or weekly mark bill.", null, "Open World", HuntIcon,
            AutoCount("hunts", "Bill cleared", 1)),
        new("tint-gear-same-color", "Tint all your gear the same color",
            "Dye every equipped piece of gear with the same dye.", null, "Anywhere", QuestIcon,
            AutoCount("matching", "Equipped pieces sharing one dye", 2)),
        new("fill-armory-category", "Fill an armory chest category",
            "Fill up every slot in one armory chest category.", null, "Anywhere", QuestIcon,
            Auto("Fill every slot in one armory chest category")),
        new("sit-bench-city-state", "Sit on a bench in any city-state",
            "Use a bench (not /sit on the ground) inside a city-state.", null, "City-states", QuestIcon,
            Auto("Sit on a bench in a city-state")),
        new("die-fall-damage", "Die from fall damage",
            "Take a lethal drop.", null, "Open World", QuestIcon,
            Auto("Die from fall damage")),
        new("cheer-same-job-player", "Cheer a colleague",
            "Find another player with the same job as you and /cheer them.", null, "Anywhere", CommendationIcon,
            Auto("Cheer a player sharing your job")),
        new("flee-five-enemies-no-sprint", "Flee without Sprint",
            "Escape combat with five enemies on you without using Sprint.", null, "Open World", QuestIcon,
            Auto("Escape combat with 5 enemies without Sprint")),
        new("fate-with-player-nearby", "Team FATE",
            "Complete a FATE with at least one other player nearby.", null, "Open World", FateIcon,
            Auto("Complete a FATE with another player nearby")),
        new("three-starting-aetherytes", "Tour the aetherytes",
            "Interact with all three starting city-state aetherytes without teleporting.", null, "City-states", QuestIcon,
            Steps(
                new("uldah", "Interact with Ul'dah's aetheryte", ProgressStepKind.Flag, Automatic: true),
                new("gridania", "Interact with Gridania's aetheryte", ProgressStepKind.Flag, Automatic: true),
                new("limsa", "Interact with Limsa Lominsa's aetheryte", ProgressStepKind.Flag, Automatic: true)),
            Breaks(new BreakingRule(BreakingTrigger.TeleportOrReturn, "used Teleport, Return, or a teleport ticket"))),
        new("heal-hurt-player", "Heal a hurt player",
            "Heal an injured player in the open world.", null, "Open World", QuestIcon,
            Steps(
                new("target", "Target an injured player", ProgressStepKind.Flag, Automatic: true),
                new("heal", "Heal them", ProgressStepKind.Flag, Automatic: true))),
        new("wave-gatherer", "Wave to a gatherer",
            "Find a gatherer in the open world and wave at them.", null, "Open World", GathererIcon,
            Auto("Wave to a gatherer")),

        // Housing.
        new("housing-sit-chair", "Sit in a chair in someone's house",
            "Enter another player's or FC house and sit in a chair.", UnlockKey.Housing, "Residential Districts", QuestIcon,
            Auto("Sit in a chair in someone's house")),
        new("housing-guestbook-message", "Write a nice guestbook message",
            "Leave a friendly message in a house guestbook.", UnlockKey.Housing, "Residential Districts", QuestIcon,
            Auto("Write a nice guestbook message")),

        // Allied societies.
        new("society-three-dailies", "Allied society regular",
            "Complete one daily quest for any allied society.", UnlockKey.AlliedSocieties, "Allied Society Settlements", SocietyIcon,
            AutoCount("dailies", "Allied society daily quests completed", 1)),

        // Deep dungeon.
        new("deepdungeon-step-trap", "Step on a trap",
            "Trigger any trap inside a deep dungeon.", UnlockKey.DeepDungeon, "Deep Dungeons", DeepDungeonIcon,
            Auto("Step on a trap")),
        new("deepdungeon-clear-ten-floors", "Clear 10 floors",
            "Clear ten deep dungeon floors.", UnlockKey.DeepDungeon, "Deep Dungeons", DeepDungeonIcon,
            AutoCount("floors", "Floors cleared", 10)),
        new("deepdungeon-die-to-boss", "Die to a deep dungeon boss",
            "Die to a boss fight before reaching the checkpoint.", UnlockKey.DeepDungeon, "Deep Dungeons", DeepDungeonIcon,
            Auto("Die to a boss before the checkpoint")),

        // Inn rooms.
        new("inn-unending-journey-cutscene", "The Unending Journey",
            "Watch any cutscene from The Unending Journey.", UnlockKey.InnRooms, "Inn Rooms", ScenarioIcon,
            Auto("Watch a cutscene from The Unending Journey")),
        new("inn-change-hairstyle", "Change your hairstyle",
            "Use the aesthetician to change your hairstyle.", UnlockKey.InnRooms, "Inn Rooms", QuestIcon,
            Auto("Change your hairstyle")),
        new("inn-toy-chest-highscore", "Toy chest highscore",
            "Get a highscore in any toy chest minigame.", UnlockKey.InnRooms, "Inn Rooms", QuestIcon,
            Auto("Get a toy chest highscore")),

        // Gold Saucer.
        new("goldsaucer-mini-cactpot", "Play the Mini Cactpot",
            "Use all 3 of your daily Mini Cactpot tickets.", UnlockKey.GoldSaucer, "The Gold Saucer", GoldSaucerIcon,
            Auto("Use all 3 daily Mini Cactpot tickets")),
        new("goldsaucer-triple-triad-win", "Win a match of Triple Triad",
            "Beat any Triple Triad opponent.", UnlockKey.GoldSaucer, "The Gold Saucer", GoldSaucerIcon,
            Auto("Win a Triple Triad match")),
        new("goldsaucer-gate-fail-fast", "Fail a GATE fast",
            "Fail a GATE within the first 30 seconds.", UnlockKey.GoldSaucer, "The Gold Saucer", GoldSaucerIcon,
            Auto("Fail a GATE within 30 seconds")),

        // Gatherers.
        new("gather-time-restricted-fish", "Catch a timed fish",
            "Fish up a fish that only occurs during a specific time of day.", UnlockKey.Gatherers, "Fishing Holes", FisherIcon,
            Auto("Catch a time-restricted fish")),
        new("gather-five-ores", "Gather five kinds of ore",
            "Mine five different kinds of ore.", UnlockKey.Gatherers, "Mining Nodes", MinerIcon,
            AutoCount("ores", "Different kinds of ore gathered", 5)),
        new("gather-three-zones", "Harvest across three zones",
            "Harvest items from three different zones.", UnlockKey.Gatherers, "Harvesting Nodes", BotanistIcon,
            AutoCount("zones", "Different zones harvested in", 3)),
        new("gather-thousand-resource", "Overflow a stack",
            "Gather enough of one resource that it takes up two inventory slots (1000).", UnlockKey.Gatherers, "Open World", GathererIcon,
            AutoCount("stack", "Highest single-item stack gathered", 1000)),

        // Crafters.
        new("craft-and-eat-food", "Craft food, then eat it",
            "Craft any meal and consume it.", UnlockKey.Crafters, "Anywhere", CulinarianIcon,
            Steps(
                new("crafted", "Craft a meal", ProgressStepKind.Flag, Automatic: true),
                new("eaten", "Eat it (Well Fed)", ProgressStepKind.Flag, Automatic: true))),

        // Trials.
        new("trial-solo-unsynced-low-hp", "Down to the wire",
            "Watch the full intro cutscene, then solo an unsynced trial and win with less than 10% HP.", UnlockKey.Trials, "Trials", TrialIcon,
            Steps(
                new("cutscene", "Watch the full intro cutscene", ProgressStepKind.Flag, Automatic: true),
                new("win", "Win solo, unsynced, under 10% HP", ProgressStepKind.Flag, Automatic: true))),
        new("trial-fall-off-arena", "Fall off an arena",
            "Take the scenic route off a trial arena.", UnlockKey.Trials, "Trials", TrialIcon,
            Auto("Fall off a trial arena")),

        // Dungeons.
        new("dungeon-greed-win", "Greed and win",
            "Roll 'Greed' on a dungeon item and win it.", UnlockKey.Dungeons, "Dungeons", DungeonIcon,
            Auto("Greed roll and win a dungeon item")),
        new("dungeon-no-deaths", "Flawless run",
            "Finish a dungeon with no deaths.", UnlockKey.Dungeons, "Dungeons", DungeonIcon,
            Auto("Finish a dungeon with no deaths")),
        new("dungeon-final-boss-aoe-hit", "Eat the telegraph",
            "Get hit by a dungeon final boss' AOE attack.", UnlockKey.Dungeons, "Dungeons", DungeonIcon,
            Auto("Get hit by a dungeon final boss' AOE")),

        // Retainers.
        new("retainer-market-flip", "Market board flip",
            "Buy something from the market board and successfully resell it, even at a loss.", UnlockKey.Retainers, "Market Boards", RetainerIcon,
            Steps(
                new("bought", "Buy an item from the market board", ProgressStepKind.Flag, Automatic: true),
                new("resold", "Resell it", ProgressStepKind.Flag, Automatic: true))),
        new("retainer-venture-complete", "Complete a venture",
            "Start a new venture and wait for it to complete.", UnlockKey.Retainers, "Summoning Bells", RetainerIcon,
            Steps(
                new("started", "Start a new venture", ProgressStepKind.Flag, Automatic: true),
                new("completed", "Wait for it to complete", ProgressStepKind.Flag, Automatic: true))),
        new("retainer-empty-inventory", "Empty your inventory",
            "Move everything out of your inventory.", UnlockKey.Retainers, "Summoning Bells", RetainerIcon,
            Auto("Empty your inventory")),
        new("retainer-fill-sale-slots", "Fill all sale slots",
            "Fill up all 20 of one retainer's sale slots.", UnlockKey.Retainers, "Summoning Bells", RetainerIcon,
            AutoCount("slots", "Retainer sale slots filled", 20)),

        // Mounts / chocobo.
        new("chocobo-revenge", "Chocobo revenge",
            "Have your chocobo companion die and take revenge on the enemies.", UnlockKey.Mounts, "Open World", CompanionIcon,
            Auto("Have your chocobo die and take revenge")),
        new("mount-indoors", "Ride a mount indoors",
            "Mount up indoors somewhere that allows it.", UnlockKey.Mounts, "Anywhere", QuestIcon,
            Steps(
                new("mounted", "Mount up", ProgressStepKind.Flag, Automatic: true),
                new("indoors", "While indoors somewhere it's allowed", ProgressStepKind.Flag, Automatic: true))),

        // Teleport / Return.
        new("cheese-on-the-moon", "Cheese on the moon",
            "Consume a cheese consumable while on the moon.", UnlockKey.TeleportReturn, "Mare Lamentorum", QuestIcon,
            Auto("Eat cheese while on the moon")),
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
