using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace ARandomizedReborn;

/// <summary>
/// Hooks <see cref="ActionManager.UseAction"/> to optionally block the Sprint general action,
/// regardless of whether it was triggered from a hotbar, keybind, gamepad, or the actions menu.
/// </summary>
public sealed unsafe class SprintBlocker : IDisposable
{
    private readonly Hook<ActionManager.Delegates.UseAction> useActionHook;
    private readonly uint sprintGeneralActionId;
    private readonly uint teleportGeneralActionId;
    private readonly uint returnGeneralActionId;
    private readonly uint gysahlGreensItemId;
    private readonly IToastGui toastGui;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly HashSet<nint> highlightedIcons = [];
    private readonly Dictionary<uint, byte> actionLevels = [];

    private static readonly string[] ActionBarNames =
    [
        "_ActionBar", "_ActionBar01", "_ActionBar02", "_ActionBar03", "_ActionBar04",
        "_ActionBar05", "_ActionBar06", "_ActionBar07", "_ActionBar08", "_ActionBar09",
    ];

    private static readonly string[] CrossBarNames =
    ["_ActionCross", "_ActionDoubleCrossL", "_ActionDoubleCrossR"];

    public bool IsEnabled { get; set; }
    public bool IsBlocking { get; set; }
    public bool UnlockSprint { get; set; }
    public bool UnlockTeleportReturn { get; set; }
    public bool UnlockMounts { get; set; }
    public bool UnlockGatherers { get; set; }
    public bool UnlockCrafters { get; set; }
    public int SkillLevelCap { get; set; } = 100;
    public int HighlightRed { get; set; } = 64;
    public int HighlightMultiply { get; set; } = 65;

    public uint TeleportGeneralActionId => this.teleportGeneralActionId;
    public uint ReturnGeneralActionId => this.returnGeneralActionId;

    /// <summary>Raised whenever an action actually goes through (i.e. was not blocked).</summary>
    public event Action<ActionType, uint>? ActionUsed;

    public SprintBlocker(
        IGameInteropProvider gameInteropProvider,
        IDataManager dataManager,
        IPluginLog log,
        IToastGui toastGui,
        IFramework framework,
        IGameGui gameGui)
    {
        this.toastGui = toastGui;
        this.framework = framework;
        this.gameGui = gameGui;

        foreach (var row in dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>(ClientLanguage.English))
            this.actionLevels[row.RowId] = row.ClassJobLevel;

        foreach (var row in dataManager.GetExcelSheet<GeneralAction>(ClientLanguage.English))
        {
            switch (row.Name.ToString())
            {
                case "Sprint":
                    sprintGeneralActionId = row.RowId;
                    break;
                case "Teleport":
                    teleportGeneralActionId = row.RowId;
                    break;
                case "Return":
                    returnGeneralActionId = row.RowId;
                    break;
            }
        }

        foreach (var row in dataManager.GetExcelSheet<Item>(ClientLanguage.English))
        {
            if (!string.Equals(row.Name.ToString(), "Gysahl Greens", StringComparison.OrdinalIgnoreCase))
                continue;

            gysahlGreensItemId = row.RowId;
            break;
        }

        if (sprintGeneralActionId == 0)
            log.Warning("Could not resolve the Sprint GeneralAction row; Sprint blocking will be unavailable.");
        if (teleportGeneralActionId == 0)
            log.Warning("Could not resolve the Teleport GeneralAction row; Teleport blocking will be unavailable.");
        if (returnGeneralActionId == 0)
            log.Warning("Could not resolve the Return GeneralAction row; Return blocking will be unavailable.");
        if (gysahlGreensItemId == 0)
            log.Warning("Could not resolve the Gysahl Greens Item row; Gysahl Greens blocking will be unavailable.");

        useActionHook = gameInteropProvider.HookFromAddress<ActionManager.Delegates.UseAction>(
            ActionManager.Addresses.UseAction.Value,
            DetourUseAction);
        useActionHook.Enable();

        this.framework.Update += this.UpdateHighlights;
    }

    private bool DetourUseAction(ActionManager* thisPtr, ActionType actionType, uint actionId, ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted)
    {
        if (!this.IsEnabled)
            return useActionHook.Original(thisPtr, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);

        if (this.IsLockedAction(actionType, actionId, out var lockName))
        {
            UIGlobals.PlayChatSoundEffect(11);
            this.toastGui.ShowError($"Locked: {lockName}");
            return false;
        }

        if (this.IsSkillLocked(actionType, actionId, out var unlockLevel))
        {
            UIGlobals.PlayChatSoundEffect(11);
            this.toastGui.ShowError($"Skill unlocks at level {unlockLevel}!");
            return false;
        }

        this.ActionUsed?.Invoke(actionType, actionId);
        return useActionHook.Original(thisPtr, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
    }

    public void Dispose()
    {
        this.framework.Update -= this.UpdateHighlights;
        this.ClearHighlights();
        useActionHook.Dispose();
    }

    private unsafe void UpdateHighlights(IFramework _)
    {
        this.ClearHighlights();
        if (!this.IsEnabled || (!this.HasActionBarLocks && this.SkillLevelCap >= 100) || this.actionLevels.Count == 0)
            return;

        foreach (var addonName in ActionBarNames)
            this.HighlightActionBar(this.gameGui.GetAddonByName<AddonActionBarBase>(addonName));

        foreach (var addonName in CrossBarNames)
        {
            var actionCross = this.gameGui.GetAddonByName<AddonActionCross>(addonName);
            if (actionCross != null)
                this.HighlightActionBar(&actionCross->AddonActionBarBase);
        }
    }

    private unsafe void HighlightActionBar(AddonActionBarBase* actionBar)
    {
        if (actionBar == null)
            return;

        for (var slotIndex = 0L; slotIndex < actionBar->ActionBarSlotVector.LongCount; slotIndex++)
        {
            var slot = actionBar->ActionBarSlotVector[slotIndex];
            if (slot.Icon == null || !this.IsBlockedAction((uint)slot.ActionId))
                continue;

            var iconAddress = (nint)slot.Icon;
            SetIconHighlight(slot.Icon, true);
            this.highlightedIcons.Add(iconAddress);
        }
    }

    private bool IsSkillLocked(ActionType actionType, uint actionId, out byte unlockLevel)
    {
        unlockLevel = 0;
        if (actionType != ActionType.Action || this.SkillLevelCap >= 100 || actionId == 0)
            return false;

        var adjustedActionId = this.GetAdjustedActionId(actionId);
        return this.actionLevels.TryGetValue(adjustedActionId, out unlockLevel) && unlockLevel > this.SkillLevelCap;
    }

    private bool IsBlockedAction(uint actionId)
        => (!this.UnlockSprint && actionId == this.sprintGeneralActionId) ||
           (!this.UnlockTeleportReturn && (actionId == this.teleportGeneralActionId || actionId == this.returnGeneralActionId)) ||
           this.IsSkillLocked(ActionType.Action, actionId, out _);

    private bool HasActionBarLocks
        => !this.UnlockSprint || !this.UnlockTeleportReturn;

    private bool IsLockedAction(ActionType actionType, uint actionId, out string lockName)
    {
        lockName = string.Empty;
        if (actionType == ActionType.GeneralAction)
        {
            if (!this.UnlockSprint && this.sprintGeneralActionId != 0 && actionId == this.sprintGeneralActionId)
            {
                lockName = "Sprint";
                return true;
            }

            if (!this.UnlockTeleportReturn && this.teleportGeneralActionId != 0 && actionId == this.teleportGeneralActionId)
            {
                lockName = "Teleport";
                return true;
            }

            if (!this.UnlockTeleportReturn && this.returnGeneralActionId != 0 && actionId == this.returnGeneralActionId)
            {
                lockName = "Return";
                return true;
            }
        }

        if (!this.UnlockMounts && actionType == ActionType.Mount)
        {
            lockName = "Mounts";
            return true;
        }

        if (!this.UnlockMounts && actionType is ActionType.Companion or ActionType.BuddyAction)
        {
            lockName = "mount + chocobo";
            return true;
        }

        if (!this.UnlockMounts && actionType == ActionType.Item && this.gysahlGreensItemId != 0 && actionId == this.gysahlGreensItemId)
        {
            lockName = "Gysahl Greens";
            return true;
        }

        if (!this.UnlockCrafters && actionType == ActionType.CraftAction)
        {
            lockName = "crafters";
            return true;
        }

        if (actionType == ActionType.Action && this.actionLevels.ContainsKey(this.GetAdjustedActionId(actionId)))
        {
            var playerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            if (playerState != null)
            {
                if (!this.UnlockCrafters && IsCrafterClassJob(playerState->CurrentClassJobId))
                {
                    lockName = "crafters";
                    return true;
                }

                if (!this.UnlockGatherers && IsGathererClassJob(playerState->CurrentClassJobId))
                {
                    lockName = "gatherers";
                    return true;
                }
            }
        }

        return false;
    }

    private static bool IsCrafterClassJob(byte classJobId)
        => classJobId is >= 8 and <= 15;

    private static bool IsGathererClassJob(byte classJobId)
        => classJobId is >= 16 and <= 18;

    private uint GetAdjustedActionId(uint actionId)
    {
        var actionManager = ActionManager.Instance();
        return actionManager == null ? actionId : actionManager->GetAdjustedActionId(actionId);
    }

    private unsafe void ClearHighlights()
    {
        foreach (var iconAddress in this.highlightedIcons)
            SetIconHighlight((AtkComponentNode*)iconAddress, false);

        this.highlightedIcons.Clear();
    }

    private unsafe void SetIconHighlight(AtkComponentNode* icon, bool highlighted)
    {
        if (icon == null)
            return;

        var red = highlighted ? (short)Math.Clamp(this.HighlightRed, 0, 255) : (short)0;
        var multiply = highlighted ? (byte)Math.Clamp(this.HighlightMultiply, 0, 100) : (byte)100;
        icon->AtkResNode.AddRed = red;
        icon->AtkResNode.AddRed_2 = red;
        icon->AtkResNode.AddGreen = 0;
        icon->AtkResNode.AddGreen_2 = 0;
        icon->AtkResNode.AddBlue = 0;
        icon->AtkResNode.AddBlue_2 = 0;
        icon->AtkResNode.MultiplyRed = multiply;
        icon->AtkResNode.MultiplyGreen = multiply;
        icon->AtkResNode.MultiplyBlue = multiply;
        icon->AtkResNode.MultiplyRed_2 = multiply;
        icon->AtkResNode.MultiplyGreen_2 = multiply;
        icon->AtkResNode.MultiplyBlue_2 = multiply;
    }
}
