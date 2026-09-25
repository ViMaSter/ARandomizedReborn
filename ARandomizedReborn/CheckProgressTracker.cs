using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Game;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.DutyState;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.GoldSaucer;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using DalamudAgentId = Dalamud.Game.Agent.AgentId;
using ClientTerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace ARandomizedReborn;

/// <summary>
/// Owns per-check step progress: stores it in <see cref="Configuration.CheckStepProgress"/>, evaluates
/// whether a check's steps are all satisfied, and watches the game for the steps it can detect on its own.
/// Once a check is complete, further updates to its steps are ignored - matching the board's "stop
/// tracking once done" rule.
/// </summary>
public sealed unsafe class CheckProgressTracker : IDisposable
{
    private const string TargetSuffix = ":target";
    private const string OreEvidencePrefix = "ore:";
    private const string ZoneEvidencePrefix = "zone:";
    private const string SocietyEvidencePrefix = "society:";
    private const string MarketItemEvidencePrefix = "market-item:";
    private const string FoodItemEvidencePrefix = "food-item:";
    private const uint MinerClassJobId = 16;
    private const uint BotanistClassJobId = 17;
    private const uint CulinarianClassJobId = 15;
    private const uint FisherClassJobId = 18;
    private const int InventorySettleFrames = 15;
    private const int PendingLootFrames = 300;
    private const int HairstyleSettleFrames = 1800;

    private sealed class FateAttempt
    {
        public required FateState State { get; set; }
        public bool HadNearbyPlayer { get; set; }
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    }

    private sealed class PendingGreedRoll
    {
        public required uint ItemId { get; init; }
        public required int QuantityBefore { get; init; }
        public int FramesRemaining { get; set; } = PendingLootFrames;
    }

    private sealed class GateAttempt
    {
        public required nint DirectorAddress { get; init; }
        public required DateTime StartedUtc { get; init; }
        public bool Finished { get; set; }
        public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    }

    private sealed class PendingInventoryAction
    {
        public required uint ClassJobId { get; init; }
        public required uint TerritoryId { get; init; }
        public required Dictionary<uint, int> Before { get; init; }
        public int FramesRemaining { get; set; } = InventorySettleFrames;
    }

    private sealed class PendingItemUse
    {
        public required uint ItemId { get; init; }
        public required int QuantityBefore { get; init; }
        public int FramesRemaining { get; set; } = InventorySettleFrames * 2;
    }

    private sealed class PendingHeal
    {
        public required ulong TargetId { get; init; }
        public required uint HpBefore { get; init; }
        public int FramesRemaining { get; set; } = 300;
    }

    private sealed class PendingFoodEat
    {
        public required uint ItemId { get; init; }
        public int FramesRemaining { get; set; } = InventorySettleFrames * 2;
    }

    private sealed class PendingBossAoe
    {
        public required ulong SourceId { get; init; }
        public required uint ActionId { get; init; }
        public required uint HpBefore { get; init; }
    }

    private static readonly GameInventoryType[] ArmoryCategories =
    [
        GameInventoryType.ArmoryMainHand,
        GameInventoryType.ArmoryOffHand,
        GameInventoryType.ArmoryHead,
        GameInventoryType.ArmoryBody,
        GameInventoryType.ArmoryHands,
        GameInventoryType.ArmoryLegs,
        GameInventoryType.ArmoryFeets,
        GameInventoryType.ArmoryEar,
        GameInventoryType.ArmoryNeck,
        GameInventoryType.ArmoryWrist,
        GameInventoryType.ArmoryRings,
        GameInventoryType.ArmorySoulCrystal,
    ];

    private static readonly GameInventoryType[] PlayerInventoryPages =
    [
        GameInventoryType.Inventory1,
        GameInventoryType.Inventory2,
        GameInventoryType.Inventory3,
        GameInventoryType.Inventory4,
    ];

    private readonly Configuration configuration;
    private readonly Func<string, bool> reportCheck;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly IDutyState dutyState;
    private readonly IPlayerState playerState;
    private readonly ITargetManager targetManager;
    private readonly IObjectTable objectTable;
    private readonly IGameInventory gameInventory;
    private readonly IFateTable fateTable;
    private readonly IDataManager dataManager;
    private readonly SprintBlocker sprintBlocker;
    private readonly InteractionRestrictionManager interactionRestrictionManager;
    private readonly IChatGui chatGui;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IAgentLifecycle agentLifecycle;
    private readonly IMarketBoard marketBoard;
    private readonly IBuddyList buddyList;
    private readonly IPartyList partyList;
    private readonly IToastGui toastGui;
    private readonly IPluginLog log;

    private readonly HashSet<uint> uldahTerritoryIds = [];
    private readonly HashSet<uint> moonTerritoryIds = [];
    private readonly Dictionary<uint, string> startingAetheryteSteps = [];
    private readonly Dictionary<uint, string> cityStateTerritoryNames = [];
    private readonly HashSet<uint> cheeseItemIds = [];
    private readonly HashSet<uint> mealItemIds = [];
    private readonly HashSet<uint> oreItemIds = [];
    private readonly HashSet<uint> teleportTicketItemIds = [];
    private readonly HashSet<uint> fallDamageLogMessageIds = [];
    private readonly HashSet<uint> guestbookMessageLogIds = [];
    private readonly HashSet<uint> deepDungeonTrapLogIds = [];
    private readonly HashSet<uint> highScoreLogIds = [];
    private readonly HashSet<uint> marketSaleLogIds = [];
    private readonly HashSet<uint> timeRestrictedFishItemIds = [];
    private readonly HashSet<uint> areaActionIds = [];
    private readonly Dictionary<byte, (int OrderRowId, bool IsComplete, int[] Kills)> markBillStates = [];
    private readonly Dictionary<ushort, (bool IsCompleted, uint SocietyId)> dailyQuestSnapshot = [];
    private uint wakingSandsTerritoryId;

    private bool enabled;
    private bool fleeAttemptArmed;
    private bool fleeAttemptUsedSprint;
    private int fleeAttemptMaxEnemies;
    private bool dungeonRunArmed;
    private bool dungeonRunHadDeath;
    private bool suppressAirshipCheck;
    private readonly Dictionary<BreakingTrigger, DateTime> pendingBreakingTriggers = [];
    private string? lastCityStateName;
    private PendingInventoryAction? gatheringAction;
    private PendingInventoryAction? craftingAction;
    private PendingItemUse? pendingCheeseUse;
    private int previousCactpotStatus;
    private int cactpotPlaysCompleted;
    private PendingHeal? pendingHeal;
    private PendingFoodEat? pendingFoodEat;
    private readonly Dictionary<(ulong SourceId, uint ActionId), PendingBossAoe> pendingBossAoes = [];
    private DateTime lastDungeonBossAoeHitUtc;
    private uint previousCompanionHp;
    private readonly HashSet<ulong> chocoboRevengeTargets = [];
    private int fallDamageFramesRemaining;
    private uint? previousPlayerHp;
    private ulong previousPlayerEntityId;
    private readonly Queue<(DateTime Time, float Height, bool Jumping)> recentPlayerMovement = new();
    private readonly List<PendingInventoryAction> pendingGatheringActions = [];
    private readonly List<PendingInventoryAction> pendingCraftingActions = [];
    private readonly Dictionary<ushort, FateAttempt> fateAttempts = [];
    private readonly Dictionary<(uint ChestObjectId, uint ChestItemIndex), RollResult> lootRollStates = [];
    private readonly List<PendingGreedRoll> pendingGreedRolls = [];
    private byte? lastInnHairstyle;
    private byte? hairstyleBeforeAesthetician;
    private int hairstyleSettleFrames;
    private bool cutsceneReplayArmed;
    private bool cutsceneReplayStarted;
    private DateTime cutsceneReplayArmedUtc;
    private bool trialIntroCutsceneStarted;
    private DateTime trialIntroCutsceneStartedUtc;
    private GateAttempt? gateAttempt;
    private bool dailyQuestSnapshotInitialized;
    private nint deepDungeonDirectorAddress;
    private byte deepDungeonId;
    private byte deepDungeonFloor;
    private bool deepDungeonFloorCounted;
    private int tickCounter;
    private bool trialAttemptArmed;
    private double? trialAttemptMinHpRatio;
    private bool trialAttemptHadOtherPlayer;

    public CheckProgressTracker(
        Configuration configuration,
        Func<string, bool> reportCheck,
        IFramework framework,
        IClientState clientState,
        ICondition condition,
        IDutyState dutyState,
        IPlayerState playerState,
        ITargetManager targetManager,
        IObjectTable objectTable,
        IGameInventory gameInventory,
        IFateTable fateTable,
        IDataManager dataManager,
        SprintBlocker sprintBlocker,
        InteractionRestrictionManager interactionRestrictionManager,
        IChatGui chatGui,
        IAddonLifecycle addonLifecycle,
        IAgentLifecycle agentLifecycle,
        IMarketBoard marketBoard,
        IBuddyList buddyList,
        IPartyList partyList,
        IToastGui toastGui,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.reportCheck = reportCheck;
        this.framework = framework;
        this.clientState = clientState;
        this.condition = condition;
        this.dutyState = dutyState;
        this.playerState = playerState;
        this.targetManager = targetManager;
        this.objectTable = objectTable;
        this.gameInventory = gameInventory;
        this.fateTable = fateTable;
        this.dataManager = dataManager;
        this.sprintBlocker = sprintBlocker;
        this.interactionRestrictionManager = interactionRestrictionManager;
        this.chatGui = chatGui;
        this.addonLifecycle = addonLifecycle;
        this.agentLifecycle = agentLifecycle;
        this.marketBoard = marketBoard;
        this.buddyList = buddyList;
        this.partyList = partyList;
        this.toastGui = toastGui;
        this.log = log;

        this.ResolveTerritories();
        this.ResolveStartingAetherytes();
        this.ResolveTrackedItems();
        this.ResolveFallDamageLogMessages();
        this.ResolveSpecialLogMessages();
    }

    public void SetEnabled(bool value)
    {
        if (this.enabled == value)
            return;

        this.enabled = value;
        if (value)
        {
            this.clientState.TerritoryChanged += this.OnTerritoryChanged;
            this.dutyState.DutyStarted += this.OnDutyStarted;
            this.dutyState.DutyWiped += this.OnDutyWiped;
            this.dutyState.DutyCompleted += this.OnDutyCompleted;
            this.condition.ConditionChange += this.OnConditionChanged;
            this.sprintBlocker.ActionUsed += this.OnActionUsed;
            this.interactionRestrictionManager.AetheryteInteracted += this.OnAetheryteInteracted;
            this.chatGui.LogMessage += this.OnLogMessage;
            this.chatGui.ChatMessageHandled += this.OnChatMessage;
            this.marketBoard.ItemPurchased += this.OnMarketItemPurchased;
            this.addonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerTaskAsk", this.OnVentureStartedAddon);
            this.addonLifecycle.RegisterListener(AddonEvent.PostSetup, "RetainerTaskResult", this.OnVentureCompletedAddon);
            this.addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "TripleTriad", this.OnTripleTriadClosing);
            this.addonLifecycle.RegisterListener(AddonEvent.PostSetup, "CharaMake", this.OnAestheticianOpened);
            this.addonLifecycle.RegisterListener(AddonEvent.PreFinalize, "CharaMake", this.OnAestheticianClosing);
            this.agentLifecycle.RegisterListener(AgentEvent.PostShow, DalamudAgentId.FateReward, this.OnFateRewardShown);
            this.agentLifecycle.RegisterListener(AgentEvent.PostShow, DalamudAgentId.CutsceneReplay, this.OnCutsceneReplayShown);
            this.framework.Update += this.OnFrameworkUpdate;
        }
        else
        {
            this.clientState.TerritoryChanged -= this.OnTerritoryChanged;
            this.dutyState.DutyStarted -= this.OnDutyStarted;
            this.dutyState.DutyWiped -= this.OnDutyWiped;
            this.dutyState.DutyCompleted -= this.OnDutyCompleted;
            this.condition.ConditionChange -= this.OnConditionChanged;
            this.sprintBlocker.ActionUsed -= this.OnActionUsed;
            this.interactionRestrictionManager.AetheryteInteracted -= this.OnAetheryteInteracted;
            this.chatGui.LogMessage -= this.OnLogMessage;
            this.chatGui.ChatMessageHandled -= this.OnChatMessage;
            this.marketBoard.ItemPurchased -= this.OnMarketItemPurchased;
            this.addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerTaskAsk", this.OnVentureStartedAddon);
            this.addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "RetainerTaskResult", this.OnVentureCompletedAddon);
            this.addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "TripleTriad", this.OnTripleTriadClosing);
            this.addonLifecycle.UnregisterListener(AddonEvent.PostSetup, "CharaMake", this.OnAestheticianOpened);
            this.addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, "CharaMake", this.OnAestheticianClosing);
            this.agentLifecycle.UnregisterListener(AgentEvent.PostShow, DalamudAgentId.FateReward, this.OnFateRewardShown);
            this.agentLifecycle.UnregisterListener(AgentEvent.PostShow, DalamudAgentId.CutsceneReplay, this.OnCutsceneReplayShown);
            this.framework.Update -= this.OnFrameworkUpdate;
            this.dungeonRunArmed = false;
            this.dungeonRunHadDeath = false;
            this.suppressAirshipCheck = false;
            this.pendingBreakingTriggers.Clear();
            this.gatheringAction = null;
            this.craftingAction = null;
            this.pendingCheeseUse = null;
            this.previousCactpotStatus = 0;
            this.cactpotPlaysCompleted = 0;
            this.pendingHeal = null;
            this.pendingFoodEat = null;
            this.trialAttemptArmed = false;
            this.trialAttemptMinHpRatio = null;
            this.trialAttemptHadOtherPlayer = false;
            this.pendingBossAoes.Clear();
            this.chocoboRevengeTargets.Clear();
            this.previousCompanionHp = 0;
            this.fallDamageFramesRemaining = 0;
            this.previousPlayerHp = null;
            this.recentPlayerMovement.Clear();
            this.markBillStates.Clear();
            this.dailyQuestSnapshot.Clear();
            this.dailyQuestSnapshotInitialized = false;
            this.ClearDeepDungeonTracking();
            this.pendingGatheringActions.Clear();
            this.pendingCraftingActions.Clear();
            this.ResetTransientContentTracking();
        }
    }

    public void Dispose() => this.SetEnabled(false);

    public bool IsLockedIn(string checkId) => this.configuration.CompletedChecks.Contains(checkId);

    public int GetValue(string checkId, string stepId)
        => this.configuration.CheckStepProgress.TryGetValue(checkId, out var steps) && steps.TryGetValue(stepId, out var value) ? value : 0;

    public int GetTarget(string checkId, CheckStepDefinition step)
        => this.configuration.CheckStepProgress.TryGetValue(checkId, out var steps) && steps.TryGetValue(step.Id + TargetSuffix, out var target)
            ? target
            : step.Target;

    public bool IsStepSatisfied(string checkId, CheckStepDefinition step)
        => step.Kind switch
        {
            ProgressStepKind.Flag => this.GetValue(checkId, step.Id) != 0,
            ProgressStepKind.Counter => this.GetValue(checkId, step.Id) >= this.GetTarget(checkId, step),
            _ => false,
        };

    /// <summary>Marks every step of a check as done; used after a manual board override.</summary>
    public void MarkAllStepsComplete(string checkId)
    {
        var definition = Checks.Find(checkId);
        if (definition == null)
            return;

        var bucket = this.GetOrCreateBucket(checkId);
        foreach (var step in definition.Steps)
            bucket[step.Id] = step.Kind == ProgressStepKind.Counter ? this.GetTarget(checkId, step) : 1;

        this.configuration.Save();
    }

    public void ClearProgress(string checkId)
    {
        this.configuration.CheckStepProgress.Remove(checkId);
        this.configuration.Save();
    }

    public void SetFlag(string checkId, string stepId, bool value)
    {
        if (this.IsLockedIn(checkId))
            return;

        this.GetOrCreateBucket(checkId)[stepId] = value ? 1 : 0;
        this.Evaluate(checkId);
        this.configuration.Save();
    }

    public void ResetFlag(string checkId, string stepId)
    {
        if (this.IsLockedIn(checkId))
            return;

        this.GetOrCreateBucket(checkId)[stepId] = 0;
        this.configuration.Save();
    }

    /// <summary>Grace window before an armed trigger (e.g. a teleport whose destination zone never actually
    /// changes) is applied anyway, so it can never get stuck armed forever waiting for a follow-up event.</summary>
    private static readonly TimeSpan PendingBreakingTriggerExpiry = TimeSpan.FromSeconds(5);

    /// <summary>Arms a breaking trigger; applied either by the matching follow-up event (see
    /// <see cref="ConsumeBreakingTrigger"/>) or, failing that, automatically after <see cref="PendingBreakingTriggerExpiry"/>.</summary>
    private void ArmBreakingTrigger(BreakingTrigger trigger)
        => this.pendingBreakingTriggers[trigger] = DateTime.UtcNow;

    /// <summary>Applies an armed trigger immediately, if one is pending.</summary>
    private void ConsumeBreakingTrigger(BreakingTrigger trigger)
    {
        if (!this.pendingBreakingTriggers.Remove(trigger))
            return;

        this.ApplyBreakingTrigger(trigger);
    }

    /// <summary>Safety net for triggers whose expected follow-up event never arrives.</summary>
    private void ExpirePendingBreakingTriggers()
    {
        if (this.pendingBreakingTriggers.Count == 0)
            return;

        var now = DateTime.UtcNow;
        foreach (var trigger in this.pendingBreakingTriggers
            .Where(pair => now - pair.Value >= PendingBreakingTriggerExpiry)
            .Select(pair => pair.Key)
            .ToArray())
        {
            this.pendingBreakingTriggers.Remove(trigger);
            this.ApplyBreakingTrigger(trigger);
        }
    }

    /// <summary>Resets progress (with a toast) for every not-yet-completed check declaring this trigger.</summary>
    private void ApplyBreakingTrigger(BreakingTrigger trigger)
    {
        foreach (var definition in Checks.Definitions)
        {
            if (this.IsLockedIn(definition.Id))
                continue;

            foreach (var rule in definition.Breaks)
            {
                if (rule.Trigger == trigger)
                    this.ResetCheckProgress(definition, rule.Reason);
            }
        }
    }

    /// <summary>Clears all recorded progress for a check and, if it actually had any, tells the user why.</summary>
    private void ResetCheckProgress(CheckDefinition definition, string reason)
    {
        if (!this.configuration.CheckStepProgress.TryGetValue(definition.Id, out var bucket) || bucket.Count == 0)
            return;

        if (!bucket.Values.Any(value => value != 0))
            return;

        bucket.Clear();
        this.configuration.Save();
        this.toastGui.ShowQuest($"Check {definition.DisplayName} reset: {reason}");
    }

    public void AdjustCounter(string checkId, string stepId, int delta)
    {
        if (this.IsLockedIn(checkId))
            return;

        var bucket = this.GetOrCreateBucket(checkId);
        var current = bucket.TryGetValue(stepId, out var value) ? value : 0;
        bucket[stepId] = Math.Max(0, current + delta);
        this.Evaluate(checkId);
        this.configuration.Save();
    }

    /// <summary>Raises the stored value to at least <paramref name="value"/>; never lowers it.</summary>
    private void SetCounterAtLeast(string checkId, string stepId, int value, int? dynamicTarget = null)
    {
        if (this.IsLockedIn(checkId))
            return;

        var bucket = this.GetOrCreateBucket(checkId);
        var current = bucket.TryGetValue(stepId, out var existing) ? existing : 0;
        var changed = false;

        if (value > current)
        {
            bucket[stepId] = value;
            changed = true;
        }

        if (dynamicTarget is { } target && (!bucket.TryGetValue(stepId + TargetSuffix, out var existingTarget) || existingTarget != target))
        {
            bucket[stepId + TargetSuffix] = target;
            changed = true;
        }

        if (!changed)
            return;

        this.Evaluate(checkId);
        this.configuration.Save();
    }

    private Dictionary<string, int> GetOrCreateBucket(string checkId)
    {
        if (!this.configuration.CheckStepProgress.TryGetValue(checkId, out var bucket))
        {
            bucket = [];
            this.configuration.CheckStepProgress[checkId] = bucket;
        }

        return bucket;
    }

    private void Evaluate(string checkId)
    {
        if (this.IsLockedIn(checkId))
            return;

        var definition = Checks.Find(checkId);
        if (definition == null)
            return;

        if (!definition.Steps.All(step => this.IsStepSatisfied(checkId, step)))
            return;

        this.reportCheck(checkId);
    }

    private void ResolveTerritories()
    {
        foreach (var row in this.dataManager.GetExcelSheet<TerritoryType>(ClientLanguage.English))
        {
            var placeName = row.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;
            if (placeName.Contains("Ul'dah", StringComparison.OrdinalIgnoreCase))
                this.uldahTerritoryIds.Add(row.RowId);
            else if (placeName.Contains("Waking Sands", StringComparison.OrdinalIgnoreCase))
                this.wakingSandsTerritoryId = row.RowId;

            if (placeName.Contains("Mare Lamentorum", StringComparison.OrdinalIgnoreCase))
                this.moonTerritoryIds.Add(row.RowId);

            if (row.TerritoryIntendedUse.RowId == (uint)ClientTerritoryIntendedUse.Town)
            {
                var cityState = placeName switch
                {
                    var name when name.Contains("Limsa Lominsa", StringComparison.OrdinalIgnoreCase) => "Limsa Lominsa",
                    var name when name.Contains("Gridania", StringComparison.OrdinalIgnoreCase) => "Gridania",
                    var name when name.Contains("Ul'dah", StringComparison.OrdinalIgnoreCase) => "Ul'dah",
                    _ => null,
                };

                if (cityState != null)
                    this.cityStateTerritoryNames[row.RowId] = cityState;
            }
        }

        if (this.uldahTerritoryIds.Count == 0)
            this.log.Warning("Could not resolve any Ul'dah territory; pray-return-waking-sands tracking will be unavailable.");
        if (this.wakingSandsTerritoryId == 0)
            this.log.Warning("Could not resolve The Waking Sands territory; pray-return-waking-sands tracking will be unavailable.");
        if (this.moonTerritoryIds.Count == 0)
            this.log.Warning("Could not resolve Mare Lamentorum; cheese-on-the-moon tracking will be unavailable.");
        if (this.cityStateTerritoryNames.Count == 0)
            this.log.Warning("Could not resolve any city-state territories; airship-city-state tracking will be unavailable.");
    }

    private void ResolveStartingAetherytes()
    {
        foreach (var row in this.dataManager.GetExcelSheet<Aetheryte>(ClientLanguage.English))
        {
            if (!row.IsAetheryte)
                continue;

            var placeName = row.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;
            var stepId = placeName switch
            {
                var name when name.Contains("Limsa Lominsa", StringComparison.OrdinalIgnoreCase) => "limsa",
                var name when name.Contains("Gridania", StringComparison.OrdinalIgnoreCase) => "gridania",
                var name when name.Contains("Ul'dah", StringComparison.OrdinalIgnoreCase) => "uldah",
                _ => null,
            };

            if (stepId != null && !this.startingAetheryteSteps.ContainsValue(stepId))
                this.startingAetheryteSteps[row.RowId] = stepId;
        }

        foreach (var stepId in new[] { "limsa", "gridania", "uldah" })
        {
            if (!this.startingAetheryteSteps.ContainsValue(stepId))
                this.log.Warning($"Could not resolve the {stepId} starting aetheryte; three-starting-aetherytes tracking will be unavailable for that city.");
        }
    }

    private void ResolveTrackedItems()
    {
        foreach (var row in this.dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>(ClientLanguage.English))
        {
            if (row.CastType > 1)
                this.areaActionIds.Add(row.RowId);
        }

        foreach (var row in this.dataManager.GetExcelSheet<Item>(ClientLanguage.English))
        {
            var name = row.Name.ToString();
            var uiCategory = row.ItemUICategory.ValueNullable?.Name.ToString() ?? string.Empty;
            var searchCategory = row.ItemSearchCategory.ValueNullable?.Name.ToString() ?? string.Empty;

            if (string.Equals(uiCategory, "Meal", StringComparison.OrdinalIgnoreCase) && row.ItemAction.RowId != 0)
            {
                this.mealItemIds.Add(row.RowId);
                if (name.Contains("Cheese", StringComparison.OrdinalIgnoreCase))
                    this.cheeseItemIds.Add(row.RowId);
            }

            if (string.Equals(searchCategory, "Stone", StringComparison.OrdinalIgnoreCase) &&
                name.Contains("Ore", StringComparison.OrdinalIgnoreCase))
            {
                this.oreItemIds.Add(row.RowId);
            }

            if (name.Contains("Aetheryte Ticket", StringComparison.OrdinalIgnoreCase) && row.ItemAction.RowId != 0)
                this.teleportTicketItemIds.Add(row.RowId);
        }

        foreach (var row in this.dataManager.GetExcelSheet<FishingNoteInfo>(ClientLanguage.English))
        {
            if (row.TimeRestriction != 0 && row.Item.RowId != 0)
                this.timeRestrictedFishItemIds.Add(row.Item.RowId);
        }

        if (this.mealItemIds.Count == 0)
            this.log.Warning("Could not resolve any meal items; crafted-food tracking will be unavailable.");
        if (this.cheeseItemIds.Count == 0)
            this.log.Warning("Could not resolve any usable cheese items; cheese-on-the-moon tracking will be unavailable.");
        if (this.oreItemIds.Count == 0)
            this.log.Warning("Could not resolve any ore items; gather-five-ores tracking will be unavailable.");
        if (this.teleportTicketItemIds.Count == 0)
            this.log.Warning("Could not resolve any Aetheryte Ticket items; teleport-ticket tracking will be unavailable.");
        else
            this.log.Debug($"[CheckProgressTracker] Resolved {this.teleportTicketItemIds.Count} Aetheryte Ticket item id(s): {string.Join(", ", this.teleportTicketItemIds)}");
    }

    /// <summary>Scans the LogMessage sheet for the fall-damage combat log line instead of hardcoding a curated ID.</summary>
    private void ResolveFallDamageLogMessages()
    {
        foreach (var row in this.dataManager.GetExcelSheet<LogMessage>(ClientLanguage.English))
        {
            var text = row.Text.ToString();
            if (text.Contains("fall", StringComparison.OrdinalIgnoreCase) && text.Contains("damage", StringComparison.OrdinalIgnoreCase))
                this.fallDamageLogMessageIds.Add(row.RowId);
        }
    }

    private void ResolveSpecialLogMessages()
    {
        foreach (var row in this.dataManager.GetExcelSheet<LogMessage>(ClientLanguage.English))
        {
            var text = row.Text.ToString();
            if (text.Contains("guestbook", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("message", StringComparison.OrdinalIgnoreCase) || text.Contains("entry", StringComparison.OrdinalIgnoreCase)))
            {
                this.guestbookMessageLogIds.Add(row.RowId);
            }

            if (text.Contains("trap", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("trigger", StringComparison.OrdinalIgnoreCase) || text.Contains("activate", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("spring", StringComparison.OrdinalIgnoreCase) || text.Contains("set off", StringComparison.OrdinalIgnoreCase)))
            {
                this.deepDungeonTrapLogIds.Add(row.RowId);
            }

            if (text.Contains("high score", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("new record", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("personal best", StringComparison.OrdinalIgnoreCase))
            {
                this.highScoreLogIds.Add(row.RowId);
            }

            if (text.Contains("sold", StringComparison.OrdinalIgnoreCase) &&
                (text.Contains("market", StringComparison.OrdinalIgnoreCase) || text.Contains("retainer", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("sale", StringComparison.OrdinalIgnoreCase)))
            {
                this.marketSaleLogIds.Add(row.RowId);
            }
        }

        if (this.guestbookMessageLogIds.Count == 0)
            this.log.Warning("Could not resolve a guestbook confirmation message; housing-guestbook-message tracking will be unavailable.");
        if (this.deepDungeonTrapLogIds.Count == 0)
            this.log.Warning("Could not resolve a deep-dungeon trap message; deepdungeon-step-trap tracking will be unavailable.");
        if (this.highScoreLogIds.Count == 0)
            this.log.Warning("Could not resolve a high-score message; inn-toy-chest-highscore tracking will be unavailable.");
        if (this.marketSaleLogIds.Count == 0)
            this.log.Warning("Could not resolve a market-sale message; retainer-market-flip resale tracking will be unavailable.");
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        this.ResetTransientContentTracking();

        // Applied before territory-based flag updates below so a teleport landing back in
        // Ul'dah still re-arms "left-uldah" instead of the break immediately wiping it out again.
        this.ConsumeBreakingTrigger(BreakingTrigger.TeleportOrReturn);

        const string checkId = "pray-return-waking-sands";
        if (!this.IsLockedIn(checkId))
        {
            if (this.uldahTerritoryIds.Contains(territoryId))
            {
                this.SetFlag(checkId, "left-uldah", true);
            }
            else if (territoryId == this.wakingSandsTerritoryId && this.GetValue(checkId, "left-uldah") != 0)
            {
                this.SetFlag(checkId, "arrived", true);
            }
        }

        this.CheckAirshipCityState(territoryId);
    }

    /// <summary>City-states aren't otherwise connected on foot, so a direct hop between two of them (without an
    /// intervening teleport/return) can only have happened via airship.</summary>
    private void CheckAirshipCityState(uint territoryId)
    {
        const string checkId = "airship-city-state";
        var isCityState = this.cityStateTerritoryNames.TryGetValue(territoryId, out var cityStateName);
        var teleported = this.suppressAirshipCheck;
        this.suppressAirshipCheck = false;

        if (!this.IsLockedIn(checkId) && isCityState && !teleported &&
            this.lastCityStateName != null && this.lastCityStateName != cityStateName)
        {
            this.SetFlag(checkId, "done", true);
        }

        if (isCityState)
            this.lastCityStateName = cityStateName;
    }

    private void OnDutyStarted(IDutyStateEventArgs _)
    {
        this.dungeonRunArmed = GameMain.Instance()->CurrentTerritoryIntendedUseId == ClientTerritoryIntendedUse.Dungeon;
        this.dungeonRunHadDeath = false;
        this.trialIntroCutsceneStarted = false;

        this.trialAttemptArmed = GameMain.Instance()->CurrentTerritoryIntendedUseId == ClientTerritoryIntendedUse.Trial &&
            !this.playerState.IsLevelSynced;
        this.trialAttemptMinHpRatio = null;
        this.trialAttemptHadOtherPlayer = false;
    }

    private void OnDutyWiped(IDutyStateEventArgs _)
    {
        if (this.dungeonRunArmed)
            this.dungeonRunHadDeath = true;

        this.trialAttemptArmed = false;
        this.trialAttemptMinHpRatio = null;
        this.trialAttemptHadOtherPlayer = false;

        this.ClearDeepDungeonTracking();
        this.ClearLootTracking();
    }

    private void OnDutyCompleted(IDutyStateEventArgs args)
    {
        if (this.dungeonRunArmed && !this.dungeonRunHadDeath)
            this.SetFlag("dungeon-no-deaths", "done", true);

        if (this.dungeonRunArmed && DateTime.UtcNow - this.lastDungeonBossAoeHitUtc <= TimeSpan.FromMinutes(3))
            this.SetFlag("dungeon-final-boss-aoe-hit", "done", true);

        if (this.trialAttemptArmed && this.trialAttemptMinHpRatio is { } minHpRatio && minHpRatio > 0 && minHpRatio < 0.10 &&
            !this.trialAttemptHadOtherPlayer)
        {
            this.SetFlag("trial-solo-unsynced-low-hp", "win", true);
        }

        if (this.deepDungeonDirectorAddress != 0 && !this.deepDungeonFloorCounted)
        {
            this.deepDungeonFloorCounted = true;
            this.AdjustCounter("deepdungeon-clear-ten-floors", "floors", 1);
        }

        this.dungeonRunArmed = false;
        this.dungeonRunHadDeath = false;
        this.trialAttemptArmed = false;
        this.trialAttemptMinHpRatio = null;
        this.trialAttemptHadOtherPlayer = false;
        this.ClearDeepDungeonTracking();
        this.ClearLootTracking();
    }

    /// <summary>Watermarks the lowest HP% seen so post-fight regen can't hide a near-death win.</summary>
    private void TrackTrialAttempt()
    {
        if (!this.trialAttemptArmed)
            return;

        if (this.objectTable.LocalPlayer is not ICharacter { MaxHp: > 0, CurrentHp: > 0 } localPlayer)
            return;

        var ratio = localPlayer.CurrentHp / (double)localPlayer.MaxHp;
        if (this.trialAttemptMinHpRatio is not { } current || ratio < current)
            this.trialAttemptMinHpRatio = ratio;

        if (!this.trialAttemptHadOtherPlayer &&
            this.objectTable.Any(gameObject => gameObject.ObjectKind == ObjectKind.Pc && gameObject.EntityId != localPlayer.EntityId))
        {
            this.trialAttemptHadOtherPlayer = true;
        }
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag == ConditionFlag.ExecutingGatheringAction)
        {
            if (value)
            {
                var classJobId = this.playerState.ClassJob.RowId;
                if (classJobId is MinerClassJobId or BotanistClassJobId or FisherClassJobId)
                {
                    this.gatheringAction = new PendingInventoryAction
                    {
                        ClassJobId = classJobId,
                        TerritoryId = this.clientState.TerritoryType,
                        Before = this.SnapshotPlayerInventory(),
                    };
                }
            }
            else if (this.gatheringAction != null)
            {
                this.pendingGatheringActions.Add(this.gatheringAction);
                this.gatheringAction = null;
            }
        }
        else if (flag == ConditionFlag.ExecutingCraftingAction)
        {
            if (value && this.playerState.ClassJob.RowId == CulinarianClassJobId)
            {
                this.craftingAction = new PendingInventoryAction
                {
                    ClassJobId = CulinarianClassJobId,
                    TerritoryId = this.clientState.TerritoryType,
                    Before = this.SnapshotPlayerInventory(),
                };
            }
            else if (!value && this.craftingAction != null)
            {
                this.pendingCraftingActions.Add(this.craftingAction);
                this.craftingAction = null;
            }
        }
        else if (flag is ConditionFlag.WatchingCutscene or ConditionFlag.WatchingCutscene78)
        {
            if (value && GameMain.Instance()->CurrentTerritoryIntendedUseId == ClientTerritoryIntendedUse.Trial &&
                !this.trialIntroCutsceneStarted)
            {
                this.trialIntroCutsceneStarted = true;
                this.trialIntroCutsceneStartedUtc = DateTime.UtcNow;
            }
            else if (!value && this.trialIntroCutsceneStarted &&
                     !this.condition[ConditionFlag.WatchingCutscene] &&
                     !this.condition[ConditionFlag.WatchingCutscene78])
            {
                if (DateTime.UtcNow - this.trialIntroCutsceneStartedUtc >= TimeSpan.FromSeconds(5))
                    this.SetFlag("trial-solo-unsynced-low-hp", "cutscene", true);

                this.trialIntroCutsceneStarted = false;
            }

            if (value && this.cutsceneReplayArmed)
            {
                this.cutsceneReplayStarted = true;
            }
            else if (!value && this.cutsceneReplayStarted &&
                     !this.condition[ConditionFlag.WatchingCutscene] &&
                     !this.condition[ConditionFlag.WatchingCutscene78])
            {
                this.ClearCutsceneReplayTracking();
                this.SetFlag("inn-unending-journey-cutscene", "done", true);
            }
        }
    }

    private void OnActionUsed(ActionType actionType, uint actionId, ulong targetId)
    {
        this.log.Debug($"[CheckProgressTracker] ActionUsed: type={actionType} id={actionId} target={targetId}");

        if (actionType == ActionType.GeneralAction && actionId == this.sprintBlocker.SprintGeneralActionId)
            this.fleeAttemptUsedSprint = true;

        if (actionType == ActionType.Action &&
            this.objectTable.SearchById(targetId) is IBattleChara { ObjectKind: ObjectKind.Pc } target &&
            target.EntityId != this.objectTable.LocalPlayer?.EntityId && target.CurrentHp > 0 && target.CurrentHp < target.MaxHp &&
            !this.IsPartyMember(target.EntityId))
        {
            this.SetFlag("heal-hurt-player", "target", true);
            this.pendingHeal = new PendingHeal { TargetId = targetId, HpBefore = target.CurrentHp };
        }

        if (actionType == ActionType.Item)
        {
            var baseItemId = NormalizeItemActionId(actionId);
            if (this.moonTerritoryIds.Contains(this.clientState.TerritoryType) && this.cheeseItemIds.Contains(baseItemId))
            {
                var inventory = this.SnapshotPlayerInventory();
                this.pendingCheeseUse = new PendingItemUse
                {
                    ItemId = baseItemId,
                    QuantityBefore = inventory.GetValueOrDefault(baseItemId),
                };
            }

            if (this.HasCraftedFoodEvidence("craft-and-eat-food", baseItemId) && this.GetValue("craft-and-eat-food", "eaten") == 0)
                this.pendingFoodEat = new PendingFoodEat { ItemId = baseItemId };
        }

        var usedItemId = actionType == ActionType.Item ? NormalizeItemActionId(actionId) : 0u;
        var isTeleportOrReturn = (actionType == ActionType.GeneralAction &&
            (actionId == this.sprintBlocker.TeleportGeneralActionId || actionId == this.sprintBlocker.ReturnGeneralActionId)) ||
            (actionType == ActionType.Item && this.teleportTicketItemIds.Contains(usedItemId));

        if (isTeleportOrReturn)
        {
            this.log.Debug($"[CheckProgressTracker] Teleport/Return detected (type={actionType} id={actionId} normalizedItemId={usedItemId}); arming breaking trigger.");
            this.suppressAirshipCheck = true;
            this.ArmBreakingTrigger(BreakingTrigger.TeleportOrReturn);
        }

        const string mountCheckId = "mount-indoors";
        if (!this.IsLockedIn(mountCheckId) && actionType == ActionType.Mount && this.IsIndoors())
        {
            this.SetFlag(mountCheckId, "mounted", true);
            this.SetFlag(mountCheckId, "indoors", true);
        }
    }

    /// <summary>Marks the fall-damage log line as pending; confirmed a few frames later once HP actually hits 0.</summary>
    private void OnLogMessage(Dalamud.Game.Chat.ILogMessage message)
    {
        if (!this.IsLockedIn("die-fall-damage") && this.fallDamageLogMessageIds.Contains(message.LogMessageId))
        {
            this.fallDamageFramesRemaining = InventorySettleFrames;
        }

        if (this.guestbookMessageLogIds.Contains(message.LogMessageId) &&
            GameMain.Instance()->CurrentTerritoryIntendedUseId is ClientTerritoryIntendedUse.HousingIndoor or ClientTerritoryIntendedUse.HousingOutdoor)
        {
            this.SetFlag("housing-guestbook-message", "done", true);
        }

        if (this.deepDungeonTrapLogIds.Contains(message.LogMessageId) && this.condition[ConditionFlag.InDeepDungeon])
            this.SetFlag("deepdungeon-step-trap", "done", true);

        if (this.highScoreLogIds.Contains(message.LogMessageId) && this.IsInInn() && this.condition[ConditionFlag.PlayingMiniGame])
            this.SetFlag("inn-toy-chest-highscore", "done", true);

        if (this.marketSaleLogIds.Contains(message.LogMessageId) && this.MarketSaleMatchesPurchasedItem(message))
            this.SetFlag("retainer-market-flip", "resold", true);
    }

    /// <summary>Catches the case where all 3 daily Mini Cactpot tickets were already used before this session could count them.</summary>
    private void OnChatMessage(Dalamud.Game.Chat.IChatMessage message)
    {
        if (!this.IsLockedIn("mark-bill-five-hunts") &&
            message.Message.TextValue.Contains("mark bills objectives complete!", StringComparison.OrdinalIgnoreCase) &&
            this.markBillStates.Values.Any(state => !state.IsComplete))
        {
            this.log.Information("[HuntProbe] mark bill completion confirmed by chat fallback");
            this.SetCounterAtLeast("mark-bill-five-hunts", "hunts", 1);
        }

        if (message.LogKind != Dalamud.Game.Text.XivChatType.NPCDialogue)
            return;

        if (message.Message.TextValue.Contains("only purchase three Mini Cactpot tickets a day", StringComparison.OrdinalIgnoreCase))
        {
            this.cactpotPlaysCompleted = 3;
            this.SetFlag("goldsaucer-mini-cactpot", "done", true);
        }
    }

    private void OnMarketItemPurchased(Dalamud.Game.Network.Structures.IMarketBoardPurchase purchase)
    {
        const string checkId = "retainer-market-flip";
        if (this.IsLockedIn(checkId) || purchase.CatalogId == 0)
            return;

        var bucket = this.GetOrCreateBucket(checkId);
        bucket[$"{MarketItemEvidencePrefix}{purchase.CatalogId}"] = 1;
        bucket["bought"] = 1;
        this.Evaluate(checkId);
        this.configuration.Save();
    }

    private bool MarketSaleMatchesPurchasedItem(Dalamud.Game.Chat.ILogMessage message)
    {
        if (!this.configuration.CheckStepProgress.TryGetValue("retainer-market-flip", out var bucket))
            return false;

        var purchasedIds = bucket.Keys
            .Where(key => key.StartsWith(MarketItemEvidencePrefix, StringComparison.Ordinal))
            .Select(key => uint.TryParse(key.AsSpan(MarketItemEvidencePrefix.Length), out var itemId) ? itemId : 0)
            .Where(itemId => itemId != 0)
            .ToHashSet();
        if (purchasedIds.Count == 0)
            return false;

        for (var index = 0; index < message.ParameterCount; index++)
        {
            if (message.TryGetIntParameter(index, out var value) && value > 0 && purchasedIds.Contains((uint)value))
                return true;

            if (!message.TryGetStringParameter(index, out var text))
                continue;

            var valueText = text.ToString();
            foreach (var itemId in purchasedIds)
            {
                if (this.dataManager.GetExcelSheet<Item>(ClientLanguage.English).TryGetRow(itemId, out var item) &&
                    valueText.Contains(item.Name.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void OnVentureStartedAddon(AddonEvent type, AddonArgs args)
        => this.SetFlag("retainer-venture-complete", "started", true);

    private void OnVentureCompletedAddon(AddonEvent type, AddonArgs args)
        => this.SetFlag("retainer-venture-complete", "completed", true);

    private void OnAestheticianOpened(AddonEvent type, AddonArgs args)
    {
        if (this.IsLockedIn("inn-change-hairstyle") || !this.IsInInn())
            return;

        this.hairstyleBeforeAesthetician = this.objectTable.LocalPlayer is ICharacter localPlayer
            ? localPlayer.Customize[(int)CustomizeIndex.HairStyle]
            : this.lastInnHairstyle;
        this.hairstyleSettleFrames = 0;
    }

    private void OnAestheticianClosing(AddonEvent type, AddonArgs args)
    {
        if (this.hairstyleBeforeAesthetician.HasValue)
            this.hairstyleSettleFrames = HairstyleSettleFrames;
    }

    private void OnCutsceneReplayShown(AgentEvent type, AgentArgs args)
    {
        if (this.IsLockedIn("inn-unending-journey-cutscene") || !this.IsInInn())
            return;

        this.cutsceneReplayArmed = true;
        this.cutsceneReplayStarted = false;
        this.cutsceneReplayArmedUtc = DateTime.UtcNow;
    }

    private void OnFateRewardShown(AgentEvent type, AgentArgs args)
    {
        if (args.Agent.IsNull)
            return;

        var agent = args.GetAgentPointer<AgentFateReward>();
        for (var index = agent->Rewards.Count - 1; index >= 0; index--)
        {
            ref var reward = ref agent->Rewards[index];
            if (reward.Type == AgentFateReward.RewardType.FateReward)
            {
                this.TryCompleteTeamFate(reward);
                return;
            }

            if (reward.Type == AgentFateReward.RewardType.GoldSaucerReward)
            {
                this.TryCompleteFastGate(reward);
                return;
            }
        }
    }

    private void TryCompleteTeamFate(AgentFateReward.Reward reward)
    {
        const string checkId = "fate-with-player-nearby";
        if (this.IsLockedIn(checkId) || !reward.IsSuccess)
            return;

        if (reward.Id <= ushort.MaxValue && this.fateAttempts.TryGetValue((ushort)reward.Id, out var attempt) && attempt.HadNearbyPlayer)
        {
            this.SetFlag(checkId, "done", true);
            return;
        }

        var recentNearbyAttempt = this.fateAttempts.Values.Any(candidate =>
            candidate.HadNearbyPlayer &&
            candidate.State is FateState.Ending or FateState.Ended &&
            DateTime.UtcNow - candidate.LastSeenUtc <= TimeSpan.FromSeconds(10));
        if (recentNearbyAttempt)
            this.SetFlag(checkId, "done", true);
    }

    private void TryCompleteFastGate(AgentFateReward.Reward reward)
    {
        const string checkId = "goldsaucer-gate-fail-fast";
        if (this.IsLockedIn(checkId) || reward.IsSuccess)
            return;

        this.CheckGateAttempt();
        if (this.gateAttempt is not { Finished: true } attempt)
            return;

        var elapsed = DateTime.UtcNow - attempt.StartedUtc;
        if (elapsed >= TimeSpan.Zero && elapsed <= TimeSpan.FromSeconds(30))
            this.SetFlag(checkId, "done", true);

        this.gateAttempt = null;
    }

    /// <summary>The match addon has no explicit win/lose flag; tally board card ownership as it closes instead.</summary>
    private unsafe void OnTripleTriadClosing(AddonEvent type, AddonArgs args)
    {
        const string checkId = "goldsaucer-triple-triad-win";
        if (this.IsLockedIn(checkId) || args.Addon.IsNull)
            return;

        var addon = (AddonTripleTriad*)args.Addon.Address;
        var blueCount = 0;
        var redCount = 0;
        foreach (var card in addon->Board)
        {
            if (!card.HasCard)
                continue;

            if (card.CardOwner == CardOwner.Blue)
                blueCount++;
            else if (card.CardOwner == CardOwner.Red)
                redCount++;
        }

        if (blueCount > redCount)
            this.SetFlag(checkId, "done", true);
    }

    private void OnAetheryteInteracted(uint aetheryteId)
    {
        this.log.Debug($"[CheckProgressTracker] AetheryteInteracted: aetheryteId={aetheryteId}");

        const string checkId = "three-starting-aetherytes";
        if (this.IsLockedIn(checkId) || !this.startingAetheryteSteps.TryGetValue(aetheryteId, out var stepId))
            return;

        this.SetFlag(checkId, stepId, true);
    }

    private bool IsIndoors()
    {
        var intendedUse = GameMain.Instance()->CurrentTerritoryIntendedUseId;
        return intendedUse is ClientTerritoryIntendedUse.HousingIndoor or ClientTerritoryIntendedUse.HousingOutdoor
            or ClientTerritoryIntendedUse.Inn or ClientTerritoryIntendedUse.GoldSaucer;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        this.CheckDungeonBossAoe();
        this.CheckChocoboRevenge();
        this.CheckFleeAttempt();
        this.CheckDeepDungeonDeath();
        this.CheckDungeonDeath();
        this.ProcessPendingInventoryActions();
        this.ProcessPendingCheeseUse();
        this.CheckMiniCactpot();
        this.ProcessPendingHeal();
        this.ProcessPendingFoodEat();
        this.TrackPlayerHpChanges();
        this.ProcessPendingFallDamage();
        this.CaptureInnHairstyle();
        this.ProcessPendingHairstyleChange();
        this.ExpireCutsceneReplayTracking();
        this.TrackTrialAttempt();
        this.ExpirePendingBreakingTriggers();

        this.tickCounter++;
        if (this.tickCounter % 30 != 0)
            return;

        this.CheckGearDye();
        this.CheckSeatedInChair();
        this.CheckMarkBills();
        this.CheckSocietyDailies();
        this.CheckDeepDungeonFloors();
        this.CheckStackOverflow();
        this.CheckHealTarget();
        this.CheckCraftedFoodSold();
        this.CheckFullArmoryCategory();
        this.CheckEmptyInventory();
        this.CheckRetainerMarketSlots();
        this.CheckFateAttempts();
        this.CheckLootRolls();
        this.ProcessPendingGreedRolls();
        this.CheckGateAttempt();
    }

    private void CheckSeatedInChair()
    {
        if (this.objectTable.LocalPlayer is not ICharacter localPlayer)
            return;

        var character = (Character*)localPlayer.Address;
        if (character == null || character->EmoteController.GetPosture() != EmoteController.Posture.SittingInChair)
            return;

        if (this.cityStateTerritoryNames.ContainsKey(this.clientState.TerritoryType))
            this.SetFlag("sit-bench-city-state", "done", true);

        if (GameMain.Instance()->CurrentTerritoryIntendedUseId == ClientTerritoryIntendedUse.HousingIndoor)
            this.SetFlag("housing-sit-chair", "done", true);
    }

    private void CheckMarkBills()
    {
        const string checkId = "mark-bill-five-hunts";
        if (this.IsLockedIn(checkId))
            return;

        var mobHunt = MobHunt.Instance();
        if (mobHunt == null)
            return;

        var orders = this.dataManager.GetSubrowExcelSheet<MobHuntOrder>(ClientLanguage.English);
        for (byte markIndex = 0; markIndex < MobHunt.MaxMarkIndex; markIndex++)
        {
            var obtained = mobHunt->IsMarkBillObtained(markIndex);
            var hadPrevious = this.markBillStates.TryGetValue(markIndex, out var previous);
            if (!obtained && !hadPrevious)
                continue;

            var orderRowId = obtained ? mobHunt->GetObtainedHuntOrderRowId(markIndex) : 0;
            var killCounts = Enumerable.Range(0, 5).Select(targetIndex => mobHunt->GetKillCount(markIndex, (byte)targetIndex)).ToArray();
            var trackedOrderRowId = obtained ? orderRowId : hadPrevious ? previous.OrderRowId : 0;
            if (trackedOrderRowId <= 0)
                continue;

            var isComplete = true;
            var targetIndices = new List<ushort>();
            for (ushort targetIndex = 0; targetIndex < 5; targetIndex++)
            {
                if (!orders.TryGetSubrow((uint)trackedOrderRowId, targetIndex, out var order))
                    break;

                if (order.NeededKills <= 0)
                    continue;

                targetIndices.Add(targetIndex);
                if (killCounts[targetIndex] < order.NeededKills)
                    isComplete = false;
            }

            if (targetIndices.Count == 0)
                isComplete = false;

            if (!obtained)
            {
                this.markBillStates.Remove(markIndex);
                if (hadPrevious && !previous.IsComplete && isComplete &&
                    targetIndices.Any(targetIndex => killCounts[targetIndex] > previous.Kills[targetIndex]))
                {
                    this.log.Information($"[HuntProbe] bill index={markIndex} completed on final kill (order={trackedOrderRowId})");
                    this.SetCounterAtLeast(checkId, "hunts", 1);
                }

                continue;
            }

            this.markBillStates[markIndex] = (orderRowId, isComplete, killCounts);
            if (hadPrevious && previous.OrderRowId == orderRowId && !previous.IsComplete && isComplete)
            {
                this.log.Information($"[HuntProbe] bill index={markIndex} completed while obtained (order={orderRowId})");
                this.SetCounterAtLeast(checkId, "hunts", 1);
                return;
            }
        }
    }

    private void CheckSocietyDailies()
    {
        const string checkId = "society-three-dailies";
        if (this.IsLockedIn(checkId))
            return;

        var questManager = QuestManager.Instance();
        if (questManager == null)
            return;

        var questSheet = this.dataManager.GetExcelSheet<Quest>(ClientLanguage.English, "Quest");
        var current = new Dictionary<ushort, (bool IsCompleted, uint SocietyId)>();
        foreach (ref var dailyQuest in questManager->DailyQuests)
        {
            if (dailyQuest.QuestId == 0 ||
                !questSheet.TryGetRow(0x10000u + dailyQuest.QuestId, out var quest) ||
                quest.BeastTribe.RowId == 0)
            {
                continue;
            }

            current[dailyQuest.QuestId] = (dailyQuest.IsCompleted, quest.BeastTribe.RowId);
        }

        if (this.dailyQuestSnapshotInitialized)
        {
            foreach (var (questId, previous) in this.dailyQuestSnapshot)
            {
                if (previous.IsCompleted && !current.ContainsKey(questId))
                    this.AddSocietyDailyEvidence(previous.SocietyId, questId);
            }
        }

        this.dailyQuestSnapshot.Clear();
        foreach (var (questId, state) in current)
            this.dailyQuestSnapshot[questId] = state;
        this.dailyQuestSnapshotInitialized = true;
    }

    private void AddSocietyDailyEvidence(uint societyId, ushort questId)
    {
        const string checkId = "society-three-dailies";
        var bucket = this.GetOrCreateBucket(checkId);
        var resetCycle = (DateTimeOffset.UtcNow - TimeSpan.FromHours(15)).ToUnixTimeSeconds() / (24 * 60 * 60);
        var societyPrefix = $"{SocietyEvidencePrefix}{societyId}:";
        if (!bucket.TryAdd($"{societyPrefix}{resetCycle}:{questId}", 1))
            return;

        var count = bucket.Keys.Count(key => key.StartsWith(societyPrefix, StringComparison.Ordinal));
        bucket["dailies"] = Math.Max(bucket.GetValueOrDefault("dailies"), count);
        this.Evaluate(checkId);
        this.configuration.Save();
    }

    private void CheckDeepDungeonFloors()
    {
        const string checkId = "deepdungeon-clear-ten-floors";
        if (this.IsLockedIn(checkId))
            return;

        if (!this.condition[ConditionFlag.InDeepDungeon])
        {
            this.ClearDeepDungeonTracking();
            return;
        }

        var eventFramework = EventFramework.Instance();
        var director = eventFramework == null ? null : eventFramework->GetInstanceContentDeepDungeon();
        if (director == null || director->Floor == 0)
            return;

        var directorAddress = (nint)director;
        if (this.deepDungeonDirectorAddress != directorAddress || this.deepDungeonId != director->DeepDungeonId || this.deepDungeonFloor == 0)
        {
            this.deepDungeonDirectorAddress = directorAddress;
            this.deepDungeonId = director->DeepDungeonId;
            this.deepDungeonFloor = director->Floor;
            this.deepDungeonFloorCounted = false;
            return;
        }

        if (director->Floor > this.deepDungeonFloor)
        {
            this.AdjustCounter(checkId, "floors", director->Floor - this.deepDungeonFloor);
            this.deepDungeonFloor = director->Floor;
            this.deepDungeonFloorCounted = false;
        }
        else if (director->Floor < this.deepDungeonFloor)
        {
            this.deepDungeonFloor = director->Floor;
            this.deepDungeonFloorCounted = false;
        }
    }

    private void ClearDeepDungeonTracking()
    {
        this.deepDungeonDirectorAddress = 0;
        this.deepDungeonId = 0;
        this.deepDungeonFloor = 0;
        this.deepDungeonFloorCounted = false;
    }

    private void CheckFateAttempts()
    {
        if (this.IsLockedIn("fate-with-player-nearby") || this.objectTable.LocalPlayer is not IPlayerCharacter localPlayer)
            return;

        var now = DateTime.UtcNow;
        foreach (var fate in this.fateTable)
        {
            if (fate.State is not (FateState.Running or FateState.Ending or FateState.Ended))
                continue;

            if (Vector3.Distance(localPlayer.Position, fate.Position) > fate.Radius)
                continue;

            var hasNearbyPlayer = this.objectTable.Any(gameObject =>
                gameObject.ObjectKind == ObjectKind.Pc &&
                gameObject.EntityId != localPlayer.EntityId &&
                Vector3.Distance(gameObject.Position, fate.Position) <= fate.Radius);

            if (!this.fateAttempts.TryGetValue(fate.FateId, out var attempt))
            {
                attempt = new FateAttempt { State = fate.State };
                this.fateAttempts[fate.FateId] = attempt;
            }

            attempt.State = fate.State;
            attempt.HadNearbyPlayer |= hasNearbyPlayer;
            attempt.LastSeenUtc = now;
        }

        foreach (var fateId in this.fateAttempts
                     .Where(pair => now - pair.Value.LastSeenUtc > TimeSpan.FromSeconds(15))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            this.fateAttempts.Remove(fateId);
        }
    }

    private void CheckLootRolls()
    {
        if (!this.dungeonRunArmed || this.IsLockedIn("dungeon-greed-win"))
            return;

        var loot = Loot.Instance();
        if (loot == null)
            return;

        var activeKeys = new HashSet<(uint ChestObjectId, uint ChestItemIndex)>();
        foreach (ref var item in loot->Items)
        {
            if (item.ItemId == 0)
                continue;

            var key = (item.ChestObjectId, item.ChestItemIndex);
            activeKeys.Add(key);
            var previous = this.lootRollStates.GetValueOrDefault(key, RollResult.Unknown);
            if (item.RollResult == RollResult.Greeded && previous != RollResult.Greeded)
            {
                var inventory = this.SnapshotPlayerInventory();
                this.pendingGreedRolls.Add(new PendingGreedRoll
                {
                    ItemId = item.ItemId,
                    QuantityBefore = inventory.GetValueOrDefault(item.ItemId),
                });
            }

            this.lootRollStates[key] = item.RollResult;
        }

        foreach (var key in this.lootRollStates.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
            this.lootRollStates.Remove(key);
    }

    private void ProcessPendingGreedRolls()
    {
        if (this.pendingGreedRolls.Count == 0)
            return;

        var inventory = this.SnapshotPlayerInventory();
        for (var index = this.pendingGreedRolls.Count - 1; index >= 0; index--)
        {
            var roll = this.pendingGreedRolls[index];
            if (inventory.GetValueOrDefault(roll.ItemId) > roll.QuantityBefore)
            {
                this.pendingGreedRolls.Clear();
                this.SetFlag("dungeon-greed-win", "done", true);
                return;
            }

            if (--roll.FramesRemaining <= 0)
                this.pendingGreedRolls.RemoveAt(index);
        }
    }

    private void CheckGateAttempt()
    {
        if (this.IsLockedIn("goldsaucer-gate-fail-fast"))
            return;

        var manager = GoldSaucerManager.Instance();
        var director = manager == null ? null : manager->CurrentGFateDirector;
        if (director == null)
        {
            if (this.gateAttempt != null && DateTime.UtcNow - this.gateAttempt.LastSeenUtc > TimeSpan.FromSeconds(10))
                this.gateAttempt = null;
            return;
        }

        var flags = director->Flags;
        if (!flags.HasFlag(GFateDirectorFlag.IsJoined))
            return;

        var address = (nint)director;
        if (this.gateAttempt == null || this.gateAttempt.DirectorAddress != address)
        {
            var startTimestamp = director->GoldSaucerDirector.Director.DirectorStartTimestamp;
            var startedUtc = startTimestamp > 0
                ? DateTimeOffset.FromUnixTimeSeconds(startTimestamp).UtcDateTime
                : DateTime.UtcNow;
            this.gateAttempt = new GateAttempt
            {
                DirectorAddress = address,
                StartedUtc = startedUtc,
            };
        }

        this.gateAttempt.Finished |= flags.HasFlag(GFateDirectorFlag.IsFinished);
        this.gateAttempt.LastSeenUtc = DateTime.UtcNow;
    }

    private void ProcessPendingHairstyleChange()
    {
        if (!this.hairstyleBeforeAesthetician.HasValue || this.hairstyleSettleFrames <= 0)
            return;

        if (!this.IsInInn())
        {
            this.ClearHairstyleTracking();
            return;
        }

        if (this.objectTable.LocalPlayer is ICharacter localPlayer &&
            localPlayer.Customize[(int)CustomizeIndex.HairStyle] != this.hairstyleBeforeAesthetician.Value)
        {
            this.ClearHairstyleTracking();
            this.SetFlag("inn-change-hairstyle", "done", true);
            return;
        }

        if (--this.hairstyleSettleFrames <= 0)
            this.ClearHairstyleTracking();
    }

    private void CaptureInnHairstyle()
    {
        if (this.hairstyleBeforeAesthetician.HasValue || !this.IsInInn() || this.objectTable.LocalPlayer is not ICharacter localPlayer)
            return;

        this.lastInnHairstyle = localPlayer.Customize[(int)CustomizeIndex.HairStyle];
    }

    private void ExpireCutsceneReplayTracking()
    {
        if (!this.cutsceneReplayArmed)
            return;

        if (!this.IsInInn() || DateTime.UtcNow - this.cutsceneReplayArmedUtc > TimeSpan.FromMinutes(10))
            this.ClearCutsceneReplayTracking();
    }

    private void ResetTransientContentTracking()
    {
        this.fateAttempts.Clear();
        this.ClearLootTracking();
        this.ClearHairstyleTracking();
        this.ClearCutsceneReplayTracking();
        this.gateAttempt = null;
        this.pendingBossAoes.Clear();
        this.lastDungeonBossAoeHitUtc = default;
        this.chocoboRevengeTargets.Clear();
        this.previousCompanionHp = 0;
        this.lastInnHairstyle = null;
    }

    private void ClearLootTracking()
    {
        this.lootRollStates.Clear();
        this.pendingGreedRolls.Clear();
    }

    private void ClearHairstyleTracking()
    {
        this.hairstyleBeforeAesthetician = null;
        this.hairstyleSettleFrames = 0;
    }

    private void ClearCutsceneReplayTracking()
    {
        this.cutsceneReplayArmed = false;
        this.cutsceneReplayStarted = false;
        this.cutsceneReplayArmedUtc = default;
    }

    private bool IsInInn()
        => GameMain.Instance()->CurrentTerritoryIntendedUseId == ClientTerritoryIntendedUse.Inn;

    private void ProcessPendingCheeseUse()
    {
        if (this.pendingCheeseUse == null)
            return;

        var inventory = this.SnapshotPlayerInventory();
        if (inventory.GetValueOrDefault(this.pendingCheeseUse.ItemId) < this.pendingCheeseUse.QuantityBefore)
        {
            this.pendingCheeseUse = null;
            this.SetFlag("cheese-on-the-moon", "done", true);
            return;
        }

        if (--this.pendingCheeseUse.FramesRemaining <= 0)
            this.pendingCheeseUse = null;
    }

    /// <summary>Counts completed plays via the AgentLotteryDaily payout status; marks done once all 3 daily allowances are used.</summary>
    private void CheckMiniCactpot()
    {
        var agentModule = AgentModule.Instance();
        var agent = agentModule == null ? null : (AgentLotteryDaily*)agentModule->GetAgentByInternalId(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId.LotteryDaily);
        var status = agent == null ? 0 : agent->Status;

        if (status == 4 && this.previousCactpotStatus != 4 && ++this.cactpotPlaysCompleted >= 3)
            this.SetFlag("goldsaucer-mini-cactpot", "done", true);

        this.previousCactpotStatus = status;
    }

    private void ProcessPendingHeal()
    {
        if (this.pendingHeal == null)
            return;

        if (this.objectTable.SearchById(this.pendingHeal.TargetId) is IBattleChara target &&
            target.CurrentHp > this.pendingHeal.HpBefore)
        {
            this.pendingHeal = null;
            this.SetFlag("heal-hurt-player", "heal", true);
            return;
        }

        if (--this.pendingHeal.FramesRemaining <= 0)
            this.pendingHeal = null;
    }

    /// <summary>Only counts "eaten" once Well Fed shows up after actually using a crafted food item.</summary>
    private void ProcessPendingFoodEat()
    {
        if (this.pendingFoodEat == null)
            return;

        if (this.objectTable.LocalPlayer is IBattleChara localPlayer && HasWellFedStatus(localPlayer))
        {
            this.pendingFoodEat = null;
            this.SetFlag("craft-and-eat-food", "eaten", true);
            return;
        }

        if (--this.pendingFoodEat.FramesRemaining <= 0)
            this.pendingFoodEat = null;
    }

    private static bool HasWellFedStatus(IBattleChara character)
    {
        for (var index = 0; index < character.StatusList.Length; index++)
        {
            var status = character.StatusList[index];
            if (status == null)
                continue;

            var name = status.GameData.ValueNullable?.Name.ToString() ?? string.Empty;
            if (name.Contains("Well Fed", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void TrackPlayerHpChanges()
    {
        if (this.objectTable.LocalPlayer is not ICharacter localPlayer)
        {
            this.previousPlayerHp = null;
            this.recentPlayerMovement.Clear();
            return;
        }

        var currentHp = localPlayer.CurrentHp;
        var position = localPlayer.Position;
        var character = (Character*)localPlayer.Address;
        var jumping = character != null && character->IsJumping();
        if (this.previousPlayerEntityId != localPlayer.EntityId)
            this.recentPlayerMovement.Clear();

        var now = DateTime.UtcNow;
        this.recentPlayerMovement.Enqueue((now, position.Y, jumping));
        while (this.recentPlayerMovement.Peek().Time < now - TimeSpan.FromSeconds(4))
            this.recentPlayerMovement.Dequeue();

        if (this.previousPlayerHp is { } previousHp && this.previousPlayerEntityId == localPlayer.EntityId && currentHp < previousHp)
        {
            var highestY = this.recentPlayerMovement.Max(sample => sample.Height);
            var wasJumping = this.recentPlayerMovement.Any(sample => sample.Jumping);
            if (currentHp == 0 && wasJumping && highestY - position.Y >= 8f)
                this.fallDamageFramesRemaining = InventorySettleFrames;

        }

        this.previousPlayerHp = currentHp;
        this.previousPlayerEntityId = localPlayer.EntityId;
    }

    /// <summary>Confirms death after a fall-damage log signal or a lethal descent.</summary>
    private void ProcessPendingFallDamage()
    {
        if (this.fallDamageFramesRemaining <= 0)
            return;

        if (this.objectTable.LocalPlayer is ICharacter { CurrentHp: 0 })
        {
            this.fallDamageFramesRemaining = 0;
            this.SetFlag("die-fall-damage", "done", true);
            if (GameMain.Instance()->CurrentTerritoryIntendedUseId == ClientTerritoryIntendedUse.Trial)
                this.SetFlag("trial-fall-off-arena", "done", true);
            return;
        }

        this.fallDamageFramesRemaining--;
    }

    private static uint NormalizeItemActionId(uint actionId)
        => actionId is >= 1_000_000 and < 2_000_000 ? actionId - 1_000_000 : actionId;

    private void ProcessPendingInventoryActions()
    {
        for (var index = this.pendingGatheringActions.Count - 1; index >= 0; index--)
        {
            var action = this.pendingGatheringActions[index];
            if (--action.FramesRemaining > 0)
                continue;

            this.pendingGatheringActions.RemoveAt(index);
            var increases = GetIncreasedItems(action.Before, this.SnapshotPlayerInventory());
            if (increases.Count == 0)
                continue;

            if (action.ClassJobId == MinerClassJobId)
            {
                foreach (var itemId in increases.Where(this.oreItemIds.Contains))
                    this.AddDistinctEvidence("gather-five-ores", "ores", OreEvidencePrefix, itemId);
            }
            else if (action.ClassJobId == BotanistClassJobId)
            {
                this.AddDistinctEvidence("gather-three-zones", "zones", ZoneEvidencePrefix, action.TerritoryId);
            }
            else if (action.ClassJobId == FisherClassJobId && increases.Any(this.timeRestrictedFishItemIds.Contains))
            {
                this.SetFlag("gather-time-restricted-fish", "done", true);
            }
        }

        for (var index = this.pendingCraftingActions.Count - 1; index >= 0; index--)
        {
            var action = this.pendingCraftingActions[index];
            if (--action.FramesRemaining > 0)
                continue;

            this.pendingCraftingActions.RemoveAt(index);
            var increases = GetIncreasedItems(action.Before, this.SnapshotPlayerInventory());
            var craftedFood = increases.Where(this.mealItemIds.Contains).ToList();
            if (craftedFood.Count == 0)
                continue;

            var bucket = this.GetOrCreateBucket("craft-and-eat-food");
            foreach (var itemId in craftedFood)
                bucket[FoodItemEvidencePrefix + itemId] = 1;

            this.SetFlag("craft-and-eat-food", "crafted", true);
        }
    }

    private Dictionary<uint, int> SnapshotPlayerInventory()
    {
        var totals = new Dictionary<uint, int>();
        foreach (var page in PlayerInventoryPages)
        {
            foreach (var item in this.gameInventory.GetInventoryItems(page))
            {
                if (item.IsEmpty || item.BaseItemId == 0)
                    continue;

                totals[item.BaseItemId] = totals.GetValueOrDefault(item.BaseItemId) + item.Quantity;
            }
        }

        return totals;
    }

    private static HashSet<uint> GetIncreasedItems(IReadOnlyDictionary<uint, int> before, IReadOnlyDictionary<uint, int> after)
    {
        var increased = new HashSet<uint>();
        foreach (var (itemId, quantity) in after)
        {
            if (quantity > before.GetValueOrDefault(itemId))
                increased.Add(itemId);
        }

        return increased;
    }

    private void AddDistinctEvidence(string checkId, string stepId, string prefix, uint value)
    {
        if (this.IsLockedIn(checkId))
            return;

        var bucket = this.GetOrCreateBucket(checkId);
        if (!bucket.TryAdd(prefix + value, 1))
            return;

        var count = bucket.Keys.Count(key => key.StartsWith(prefix, StringComparison.Ordinal));
        bucket[stepId] = Math.Max(bucket.GetValueOrDefault(stepId), count);
        this.Evaluate(checkId);
        this.configuration.Save();
    }

    private void CheckGearDye()
    {
        const string checkId = "tint-gear-same-color";
        if (this.IsLockedIn(checkId))
            return;

        var equipped = this.gameInventory.GetInventoryItems(GameInventoryType.EquippedItems);
        var dyedGroups = new Dictionary<byte, int>();
        var totalEquipped = 0;

        foreach (var item in equipped)
        {
            if (item.IsEmpty || item.ItemId == 0)
                continue;

            totalEquipped++;
            var stain = item.Stains.Length > 0 ? item.Stains[0] : (byte)0;
            if (stain == 0)
                continue;

            dyedGroups[stain] = dyedGroups.GetValueOrDefault(stain) + 1;
        }

        if (totalEquipped == 0 || dyedGroups.Count == 0)
            return;

        var bestMatch = dyedGroups.Values.Max();
        this.SetCounterAtLeast(checkId, "matching", bestMatch, totalEquipped);
    }

    private void CheckStackOverflow()
    {
        const string checkId = "gather-thousand-resource";
        if (this.IsLockedIn(checkId))
            return;

        GameInventoryType[] pages = [GameInventoryType.Inventory1, GameInventoryType.Inventory2, GameInventoryType.Inventory3, GameInventoryType.Inventory4];
        var totals = new Dictionary<uint, int>();

        foreach (var page in pages)
        {
            foreach (var item in this.gameInventory.GetInventoryItems(page))
            {
                if (item.IsEmpty || item.ItemId == 0)
                    continue;

                totals[item.ItemId] = totals.GetValueOrDefault(item.ItemId) + item.Quantity;
            }
        }

        if (totals.Count == 0)
            return;

        this.SetCounterAtLeast(checkId, "stack", totals.Values.Max());
    }

    private void CheckHealTarget()
    {
        const string checkId = "heal-hurt-player";
        if (this.IsLockedIn(checkId) || this.GetValue(checkId, "target") != 0)
            return;

        if (this.targetManager.Target is not IBattleChara { ObjectKind: ObjectKind.Pc } target)
            return;

        if (target.CurrentHp > 0 && target.CurrentHp < target.MaxHp && !this.IsPartyMember(target.EntityId))
            this.SetFlag(checkId, "target", true);
    }

    private bool IsPartyMember(uint entityId)
    {
        for (var index = 0; index < this.partyList.Length; index++)
        {
            if (this.partyList[index]?.EntityId == entityId)
                return true;
        }

        return false;
    }

    private bool HasCraftedFoodEvidence(string checkId, uint itemId)
        => this.configuration.CheckStepProgress.TryGetValue(checkId, out var bucket) &&
           bucket.ContainsKey(FoodItemEvidencePrefix + itemId);

    /// <summary>Selling/discarding/trading a crafted food away resets "crafted" - they have to make it again.</summary>
    private void CheckCraftedFoodSold()
    {
        const string checkId = "craft-and-eat-food";
        if (this.IsLockedIn(checkId) || this.GetValue(checkId, "eaten") != 0)
            return;

        if (!this.configuration.CheckStepProgress.TryGetValue(checkId, out var bucket))
            return;

        var foodKeys = bucket.Keys.Where(key => key.StartsWith(FoodItemEvidencePrefix, StringComparison.Ordinal)).ToList();
        if (foodKeys.Count == 0)
            return;

        var inventory = this.SnapshotPlayerInventory();
        foreach (var key in foodKeys)
        {
            var itemId = uint.Parse(key.AsSpan(FoodItemEvidencePrefix.Length));
            if (inventory.GetValueOrDefault(itemId) == 0)
                bucket.Remove(key);
        }

        if (!bucket.Keys.Any(key => key.StartsWith(FoodItemEvidencePrefix, StringComparison.Ordinal)))
        {
            bucket["crafted"] = 0;
            this.configuration.Save();
        }
    }

    private void CheckFullArmoryCategory()
    {
        const string checkId = "fill-armory-category";
        if (this.IsLockedIn(checkId))
            return;

        foreach (var category in ArmoryCategories)
        {
            var items = this.gameInventory.GetInventoryItems(category);
            if (items.Length == 0)
                continue;

            var full = true;
            foreach (var item in items)
            {
                if (!item.IsEmpty && item.ItemId != 0)
                    continue;

                full = false;
                break;
            }

            if (!full)
                continue;

            this.SetFlag(checkId, "done", true);
            return;
        }
    }

    private void CheckEmptyInventory()
    {
        const string checkId = "retainer-empty-inventory";
        if (this.IsLockedIn(checkId) || this.objectTable.LocalPlayer == null)
            return;

        foreach (var page in PlayerInventoryPages)
        {
            var items = this.gameInventory.GetInventoryItems(page);
            if (items.Length == 0)
                return;

            foreach (var item in items)
            {
                if (!item.IsEmpty && item.ItemId != 0)
                    return;
            }
        }

        this.SetFlag(checkId, "done", true);
    }

    private void CheckRetainerMarketSlots()
    {
        const string checkId = "retainer-fill-sale-slots";
        if (this.configuration.ManuallyCompletedChecks.Contains(checkId))
            return;

        var retainerManager = RetainerManager.Instance();
        if (retainerManager == null || !retainerManager->IsReady)
            return;

        var filledSlots = 0;
        for (uint index = 0; index < retainerManager->GetRetainerCount(); index++)
        {
            var retainer = retainerManager->GetRetainerBySortedIndex(index);
            if (retainer != null && retainer->MarketItemCount > filledSlots)
                filledSlots = retainer->MarketItemCount;
        }

        if (this.GetValue(checkId, "slots") == filledSlots)
            return;

        this.GetOrCreateBucket(checkId)["slots"] = filledSlots;
        this.Evaluate(checkId);
        this.configuration.Save();
    }

    private void CheckDungeonDeath()
    {
        if (!this.dungeonRunArmed || this.dungeonRunHadDeath)
            return;

        if (this.objectTable.LocalPlayer is ICharacter { CurrentHp: 0 })
            this.dungeonRunHadDeath = true;
    }

    private void CheckFleeAttempt()
    {
        var inCombat = this.condition[ConditionFlag.InCombat];
        if (!inCombat)
        {
            if (this.fleeAttemptArmed && this.fleeAttemptMaxEnemies >= 5 && !this.fleeAttemptUsedSprint &&
                this.objectTable.LocalPlayer is ICharacter { CurrentHp: > 0 })
            {
                this.SetFlag("flee-five-enemies-no-sprint", "done", true);
            }

            this.fleeAttemptArmed = false;
            this.fleeAttemptUsedSprint = false;
            this.fleeAttemptMaxEnemies = 0;
            return;
        }

        this.fleeAttemptArmed = true;
        if (this.objectTable.LocalPlayer is not ICharacter localPlayer)
            return;

        var enemies = this.objectTable.Count(gameObject =>
            gameObject.ObjectKind == ObjectKind.BattleNpc &&
            gameObject is IBattleChara { CurrentHp: > 0, TargetObjectId: var targetId } &&
            (targetId == localPlayer.GameObjectId || targetId == localPlayer.EntityId) &&
            Vector3.Distance(gameObject.Position, localPlayer.Position) <= 30f);
        this.fleeAttemptMaxEnemies = Math.Max(this.fleeAttemptMaxEnemies, enemies);
    }

    private void CheckDeepDungeonDeath()
    {
        if (!this.condition[ConditionFlag.InDeepDungeon] ||
            this.objectTable.LocalPlayer is not ICharacter { CurrentHp: 0 })
        {
            return;
        }

        var eventFramework = EventFramework.Instance();
        var director = eventFramework == null ? null : eventFramework->GetInstanceContentDeepDungeon();
        if (director != null && director->LayoutInitializationType == 6)
            this.SetFlag("deepdungeon-die-to-boss", "done", true);
    }

    private void CheckDungeonBossAoe()
    {
        if (!this.dungeonRunArmed || this.objectTable.LocalPlayer is not ICharacter localPlayer)
            return;

        foreach (var gameObject in this.objectTable)
        {
            if (gameObject is not IBattleNpc { IsCasting: true, BattleNpcKind: BattleNpcSubKind.Combatant } enemy ||
                !this.areaActionIds.Contains(enemy.CastActionId) ||
                !this.dataManager.GetExcelSheet<BNpcBase>(ClientLanguage.English).TryGetRow(enemy.BaseId, out var npc) || npc.Rank <= 1)
            {
                continue;
            }

            var key = (enemy.EntityId, enemy.CastActionId);
            this.pendingBossAoes.TryAdd(key, new PendingBossAoe
            {
                SourceId = enemy.EntityId,
                ActionId = enemy.CastActionId,
                HpBefore = localPlayer.CurrentHp,
            });
        }

        foreach (var (key, pending) in this.pendingBossAoes.ToArray())
        {
            var sourceStillCasting = this.objectTable.SearchById(pending.SourceId) is IBattleChara source &&
                                     source.IsCasting && source.CastActionId == pending.ActionId;
            if (sourceStillCasting)
                continue;

            this.pendingBossAoes.Remove(key);
            if (localPlayer.CurrentHp < pending.HpBefore)
                this.lastDungeonBossAoeHitUtc = DateTime.UtcNow;
        }
    }

    private void CheckChocoboRevenge()
    {
        var companion = this.buddyList.CompanionBuddy;
        var currentHp = companion?.CurrentHP ?? 0;
        if (this.previousCompanionHp > 0 && currentHp == 0 && this.objectTable.LocalPlayer is ICharacter localPlayer)
        {
            this.chocoboRevengeTargets.Clear();
            foreach (var gameObject in this.objectTable)
            {
                if (gameObject is IBattleNpc { BattleNpcKind: BattleNpcSubKind.Combatant, CurrentHp: > 0 } enemy &&
                    Vector3.Distance(enemy.Position, localPlayer.Position) <= 30f)
                {
                    this.chocoboRevengeTargets.Add(enemy.EntityId);
                }
            }
        }

        this.previousCompanionHp = currentHp;
        if (this.chocoboRevengeTargets.Count == 0 || this.objectTable.LocalPlayer is not ICharacter { CurrentHp: > 0 })
            return;

        foreach (var targetId in this.chocoboRevengeTargets.ToArray())
        {
            if (this.objectTable.SearchById(targetId) is ICharacter { CurrentHp: 0 })
            {
                this.chocoboRevengeTargets.Clear();
                this.SetFlag("chocobo-revenge", "done", true);
                return;
            }
        }
    }
}
