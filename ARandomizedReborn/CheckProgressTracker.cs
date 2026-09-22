using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
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
    private const uint MinerClassJobId = 16;
    private const uint BotanistClassJobId = 17;
    private const uint CulinarianClassJobId = 15;
    private const int InventorySettleFrames = 15;

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
    private readonly IDataManager dataManager;
    private readonly SprintBlocker sprintBlocker;
    private readonly InteractionRestrictionManager interactionRestrictionManager;
    private readonly IPluginLog log;

    private readonly HashSet<uint> uldahTerritoryIds = [];
    private readonly HashSet<uint> moonTerritoryIds = [];
    private readonly Dictionary<uint, string> startingAetheryteSteps = [];
    private readonly HashSet<uint> cheeseItemIds = [];
    private readonly HashSet<uint> mealItemIds = [];
    private readonly HashSet<uint> oreItemIds = [];
    private uint wakingSandsTerritoryId;

    private bool enabled;
    private bool dungeonRunArmed;
    private bool dungeonRunHadDeath;
    private PendingInventoryAction? gatheringAction;
    private PendingInventoryAction? craftingAction;
    private PendingItemUse? pendingCheeseUse;
    private readonly List<PendingInventoryAction> pendingGatheringActions = [];
    private readonly List<PendingInventoryAction> pendingCraftingActions = [];
    private int tickCounter;

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
        IDataManager dataManager,
        SprintBlocker sprintBlocker,
        InteractionRestrictionManager interactionRestrictionManager,
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
        this.dataManager = dataManager;
        this.sprintBlocker = sprintBlocker;
        this.interactionRestrictionManager = interactionRestrictionManager;
        this.log = log;

        this.ResolveTerritories();
        this.ResolveStartingAetherytes();
        this.ResolveTrackedItems();
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
            this.framework.Update -= this.OnFrameworkUpdate;
            this.dungeonRunArmed = false;
            this.dungeonRunHadDeath = false;
            this.gatheringAction = null;
            this.craftingAction = null;
            this.pendingCheeseUse = null;
            this.pendingGatheringActions.Clear();
            this.pendingCraftingActions.Clear();
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
        }

        if (this.uldahTerritoryIds.Count == 0)
            this.log.Warning("Could not resolve any Ul'dah territory; pray-return-waking-sands tracking will be unavailable.");
        if (this.wakingSandsTerritoryId == 0)
            this.log.Warning("Could not resolve The Waking Sands territory; pray-return-waking-sands tracking will be unavailable.");
        if (this.moonTerritoryIds.Count == 0)
            this.log.Warning("Could not resolve Mare Lamentorum; cheese-on-the-moon tracking will be unavailable.");
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
        }

        if (this.mealItemIds.Count == 0)
            this.log.Warning("Could not resolve any meal items; crafted-food tracking will be unavailable.");
        if (this.cheeseItemIds.Count == 0)
            this.log.Warning("Could not resolve any usable cheese items; cheese-on-the-moon tracking will be unavailable.");
        if (this.oreItemIds.Count == 0)
            this.log.Warning("Could not resolve any ore items; gather-five-ores tracking will be unavailable.");
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        const string checkId = "pray-return-waking-sands";
        if (this.IsLockedIn(checkId))
            return;

        if (this.uldahTerritoryIds.Contains(territoryId))
        {
            this.SetFlag(checkId, "left-uldah", true);
        }
        else if (territoryId == this.wakingSandsTerritoryId && this.GetValue(checkId, "left-uldah") != 0)
        {
            this.SetFlag(checkId, "arrived", true);
        }
    }

    private void OnDutyStarted(IDutyStateEventArgs _)
    {
        this.dungeonRunArmed = GameMain.Instance()->CurrentTerritoryIntendedUseId == ClientTerritoryIntendedUse.Dungeon;
        this.dungeonRunHadDeath = false;
    }

    private void OnDutyWiped(IDutyStateEventArgs _)
    {
        if (this.dungeonRunArmed)
            this.dungeonRunHadDeath = true;
    }

    private void OnDutyCompleted(IDutyStateEventArgs _)
    {
        if (this.dungeonRunArmed && !this.dungeonRunHadDeath)
            this.SetFlag("dungeon-no-deaths", "done", true);

        this.dungeonRunArmed = false;
        this.dungeonRunHadDeath = false;
    }

    private void OnConditionChanged(ConditionFlag flag, bool value)
    {
        if (flag == ConditionFlag.ExecutingGatheringAction)
        {
            if (value)
            {
                var classJobId = this.playerState.ClassJob.RowId;
                if (classJobId is MinerClassJobId or BotanistClassJobId)
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
    }

    private void OnActionUsed(ActionType actionType, uint actionId)
    {
        if (actionType == ActionType.Item && this.moonTerritoryIds.Contains(this.clientState.TerritoryType))
        {
            var baseItemId = NormalizeItemActionId(actionId);
            if (this.cheeseItemIds.Contains(baseItemId))
            {
                var inventory = this.SnapshotPlayerInventory();
                this.pendingCheeseUse = new PendingItemUse
                {
                    ItemId = baseItemId,
                    QuantityBefore = inventory.GetValueOrDefault(baseItemId),
                };
            }
        }

        const string prayCheckId = "pray-return-waking-sands";
        if (!this.IsLockedIn(prayCheckId) &&
            actionType == ActionType.GeneralAction &&
            (actionId == this.sprintBlocker.TeleportGeneralActionId || actionId == this.sprintBlocker.ReturnGeneralActionId))
        {
            this.ResetFlag(prayCheckId, "left-uldah");
            this.ResetFlag(prayCheckId, "arrived");
        }

        if (actionType == ActionType.GeneralAction && actionId == this.sprintBlocker.TeleportGeneralActionId)
        {
            const string aetheryteCheckId = "three-starting-aetherytes";
            this.ResetFlag(aetheryteCheckId, "limsa");
            this.ResetFlag(aetheryteCheckId, "gridania");
            this.ResetFlag(aetheryteCheckId, "uldah");
        }

        const string mountCheckId = "mount-indoors";
        if (!this.IsLockedIn(mountCheckId) && actionType == ActionType.Mount && this.IsIndoors())
        {
            this.SetFlag(mountCheckId, "mounted", true);
            this.SetFlag(mountCheckId, "indoors", true);
        }
    }

    private void OnAetheryteInteracted(uint aetheryteId)
    {
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
        this.CheckDungeonDeath();
        this.ProcessPendingInventoryActions();
        this.ProcessPendingCheeseUse();

        this.tickCounter++;
        if (this.tickCounter % 30 != 0)
            return;

        this.CheckGearDye();
        this.CheckStackOverflow();
        this.CheckHealTarget();
        this.CheckWellFed();
        this.CheckFullArmoryCategory();
        this.CheckEmptyInventory();
        this.CheckRetainerMarketSlots();
    }

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
        }

        for (var index = this.pendingCraftingActions.Count - 1; index >= 0; index--)
        {
            var action = this.pendingCraftingActions[index];
            if (--action.FramesRemaining > 0)
                continue;

            this.pendingCraftingActions.RemoveAt(index);
            var increases = GetIncreasedItems(action.Before, this.SnapshotPlayerInventory());
            if (increases.Any(this.mealItemIds.Contains))
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

        if (target.CurrentHp > 0 && target.CurrentHp < target.MaxHp)
            this.SetFlag(checkId, "target", true);
    }

    private void CheckWellFed()
    {
        const string checkId = "craft-and-eat-food";
        if (this.IsLockedIn(checkId) || this.GetValue(checkId, "eaten") != 0)
            return;

        if (this.objectTable.LocalPlayer is not IBattleChara localPlayer)
            return;

        for (var index = 0; index < localPlayer.StatusList.Length; index++)
        {
            var status = localPlayer.StatusList[index];
            if (status == null)
                continue;

            var name = status.GameData.ValueNullable?.Name.ToString() ?? string.Empty;
            if (name.Contains("Well Fed", StringComparison.OrdinalIgnoreCase))
            {
                this.SetFlag(checkId, "eaten", true);
                return;
            }
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
        if (this.IsLockedIn(checkId))
            return;

        var items = this.gameInventory.GetInventoryItems(GameInventoryType.RetainerMarket);
        if (items.Length == 0)
            return;

        var filledSlots = 0;
        foreach (var item in items)
        {
            if (!item.IsEmpty && item.ItemId != 0)
                filledSlots++;
        }

        this.SetCounterAtLeast(checkId, "slots", filledSlots);
    }

    private void CheckDungeonDeath()
    {
        if (!this.dungeonRunArmed || this.dungeonRunHadDeath)
            return;

        if (this.objectTable.LocalPlayer is ICharacter { CurrentHp: 0 })
            this.dungeonRunHadDeath = true;
    }
}
