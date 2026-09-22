using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
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

    private readonly Configuration configuration;
    private readonly Func<string, bool> reportCheck;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly ITargetManager targetManager;
    private readonly IObjectTable objectTable;
    private readonly IGameInventory gameInventory;
    private readonly IDataManager dataManager;
    private readonly SprintBlocker sprintBlocker;
    private readonly IPluginLog log;

    private readonly HashSet<uint> uldahTerritoryIds = [];
    private uint wakingSandsTerritoryId;

    private bool enabled;
    private int tickCounter;

    public CheckProgressTracker(
        Configuration configuration,
        Func<string, bool> reportCheck,
        IFramework framework,
        IClientState clientState,
        ITargetManager targetManager,
        IObjectTable objectTable,
        IGameInventory gameInventory,
        IDataManager dataManager,
        SprintBlocker sprintBlocker,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.reportCheck = reportCheck;
        this.framework = framework;
        this.clientState = clientState;
        this.targetManager = targetManager;
        this.objectTable = objectTable;
        this.gameInventory = gameInventory;
        this.dataManager = dataManager;
        this.sprintBlocker = sprintBlocker;
        this.log = log;

        this.ResolveTerritories();
    }

    public void SetEnabled(bool value)
    {
        if (this.enabled == value)
            return;

        this.enabled = value;
        if (value)
        {
            this.clientState.TerritoryChanged += this.OnTerritoryChanged;
            this.sprintBlocker.ActionUsed += this.OnActionUsed;
            this.framework.Update += this.OnFrameworkUpdate;
        }
        else
        {
            this.clientState.TerritoryChanged -= this.OnTerritoryChanged;
            this.sprintBlocker.ActionUsed -= this.OnActionUsed;
            this.framework.Update -= this.OnFrameworkUpdate;
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
        }

        if (this.uldahTerritoryIds.Count == 0)
            this.log.Warning("Could not resolve any Ul'dah territory; pray-return-waking-sands tracking will be unavailable.");
        if (this.wakingSandsTerritoryId == 0)
            this.log.Warning("Could not resolve The Waking Sands territory; pray-return-waking-sands tracking will be unavailable.");
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

    private void OnActionUsed(ActionType actionType, uint actionId)
    {
        const string prayCheckId = "pray-return-waking-sands";
        if (!this.IsLockedIn(prayCheckId) &&
            actionType == ActionType.GeneralAction &&
            (actionId == this.sprintBlocker.TeleportGeneralActionId || actionId == this.sprintBlocker.ReturnGeneralActionId))
        {
            this.ResetFlag(prayCheckId, "left-uldah");
            this.ResetFlag(prayCheckId, "arrived");
        }

        const string mountCheckId = "mount-indoors";
        if (!this.IsLockedIn(mountCheckId) && actionType == ActionType.Mount && this.IsIndoors())
        {
            this.SetFlag(mountCheckId, "mounted", true);
            this.SetFlag(mountCheckId, "indoors", true);
        }
    }

    private bool IsIndoors()
    {
        var intendedUse = GameMain.Instance()->CurrentTerritoryIntendedUseId;
        return intendedUse is ClientTerritoryIntendedUse.HousingIndoor or ClientTerritoryIntendedUse.HousingOutdoor
            or ClientTerritoryIntendedUse.Inn or ClientTerritoryIntendedUse.GoldSaucer;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        this.tickCounter++;
        if (this.tickCounter % 30 != 0)
            return;

        this.CheckGearDye();
        this.CheckStackOverflow();
        this.CheckHealTarget();
        this.CheckWellFed();
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
}
