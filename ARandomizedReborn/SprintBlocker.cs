using System;
using System.Collections.Generic;
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

    public bool IsBlocking { get; set; }
    public int SkillLevelCap { get; set; } = 100;
    public int HighlightRed { get; set; } = 64;
    public int HighlightMultiply { get; set; } = 65;

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

        foreach (var row in dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>())
            this.actionLevels[row.RowId] = row.ClassJobLevel;

        foreach (var row in dataManager.GetExcelSheet<GeneralAction>())
        {
            if (row.Name.ToString() != "Sprint")
                continue;

            sprintGeneralActionId = row.RowId;
            break;
        }

        if (sprintGeneralActionId == 0)
            log.Warning("Could not resolve the Sprint GeneralAction row; Sprint blocking will be unavailable.");

        useActionHook = gameInteropProvider.HookFromAddress<ActionManager.Delegates.UseAction>(
            ActionManager.Addresses.UseAction.Value,
            DetourUseAction);
        useActionHook.Enable();

        this.framework.Update += this.UpdateHighlights;
    }

    private bool DetourUseAction(ActionManager* thisPtr, ActionType actionType, uint actionId, ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted)
    {
        if (IsBlocking && sprintGeneralActionId != 0 && actionType == ActionType.GeneralAction && actionId == sprintGeneralActionId)
        {
            UIGlobals.PlayChatSoundEffect(11);
            this.toastGui.ShowError("Sprint is diabled!");
            return false;
        }

        if (this.IsSkillLocked(actionType, actionId, out var unlockLevel))
        {
            UIGlobals.PlayChatSoundEffect(11);
            this.toastGui.ShowError($"Skill unlocks at level {unlockLevel}!");
            return false;
        }

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
        if ((!IsBlocking && this.SkillLevelCap >= 100) || this.actionLevels.Count == 0)
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
        => (this.IsBlocking && actionId == this.sprintGeneralActionId) ||
           this.IsSkillLocked(ActionType.Action, actionId, out _);

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
