using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
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
    private readonly Hook<ActionManager.Delegates.UseActionLocation> useActionLocationHook;
    private readonly Hook<Telepo.SelectUseTicketInvoker.Delegates.TeleportWithTickets> teleportWithTicketsHook;
    private readonly Hook<AgentInventoryContext.Delegates.UseItem> useItemHook;
    private readonly uint sprintGeneralActionId;
    private readonly uint teleportGeneralActionId;
    private readonly uint returnGeneralActionId;
    private readonly uint gysahlGreensItemId;
    private readonly HashSet<uint> teleportItemIds = [];
    private readonly IToastGui toastGui;
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
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
    // Nothing lowers this yet, so enabling the randomizer keeps every skill of the current level.
    public int SkillLevelCap { get; set; } = 100;
    public int HighlightRed { get; set; } = 90;
    public int HighlightMultiply { get; set; } = 30;

    public uint TeleportGeneralActionId => this.teleportGeneralActionId;
    public uint ReturnGeneralActionId => this.returnGeneralActionId;
    public uint SprintGeneralActionId => this.sprintGeneralActionId;

    /// <summary>Raised whenever an action actually goes through (i.e. was not blocked).</summary>
    public event Action<ActionType, uint, ulong>? ActionUsed;

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
        this.log = log;

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

        foreach (var row in dataManager.GetExcelSheet<Item>(ClientLanguage.English))
        {
            if (row.Name.ToString().Contains("Aetheryte Ticket", StringComparison.OrdinalIgnoreCase))
                this.teleportItemIds.Add(row.RowId);
        }

        if (sprintGeneralActionId == 0)
            log.Warning("Could not resolve the Sprint GeneralAction row; Sprint blocking will be unavailable.");
        if (teleportGeneralActionId == 0)
            log.Warning("Could not resolve the Teleport GeneralAction row; Teleport blocking will be unavailable.");
        if (returnGeneralActionId == 0)
            log.Warning("Could not resolve the Return GeneralAction row; Return blocking will be unavailable.");
        if (gysahlGreensItemId == 0)
            log.Warning("Could not resolve the Gysahl Greens Item row; Gysahl Greens blocking will be unavailable.");
        if (this.teleportItemIds.Count == 0)
            log.Warning("Could not resolve any Aetheryte Ticket item rows; Aetheryte Ticket blocking will be unavailable.");

        useActionHook = gameInteropProvider.HookFromAddress<ActionManager.Delegates.UseAction>(
            ActionManager.Addresses.UseAction.Value,
            DetourUseAction);
        useActionHook.Enable();

        // Right-click "Use" on an item (and some hotbar item slots) calls straight into
        // UseActionLocation without going through the hooked UseAction entry point above.
        useActionLocationHook = gameInteropProvider.HookFromAddress<ActionManager.Delegates.UseActionLocation>(
            ActionManager.Addresses.UseActionLocation.Value,
            DetourUseActionLocation);
        useActionLocationHook.Enable();

        // Teleport tickets (Aetheryte Ticket, etc.) never touch ActionManager at all; the actual
        // teleport is dispatched straight from the Teleport Town list via this invoker.
        teleportWithTicketsHook = gameInteropProvider.HookFromAddress<Telepo.SelectUseTicketInvoker.Delegates.TeleportWithTickets>(
            Telepo.SelectUseTicketInvoker.Addresses.TeleportWithTickets.Value,
            DetourTeleportWithTickets);
        teleportWithTicketsHook.Enable();

        // Right-click "Use" in the inventory context menu calls this directly and may never reach
        // ActionManager at all (e.g. items that open a follow-up UI, like Aetheryte Tickets).
        useItemHook = gameInteropProvider.HookFromAddress<AgentInventoryContext.Delegates.UseItem>(
            AgentInventoryContext.Addresses.UseItem.Value,
            DetourUseItem);
        useItemHook.Enable();

        this.framework.Update += this.UpdateHighlights;
    }

    private bool DetourUseAction(ActionManager* thisPtr, ActionType actionType, uint actionId, ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted)
    {
        if (!this.IsEnabled)
            return useActionHook.Original(thisPtr, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);

        this.log.Debug($"[SprintBlocker] UseAction: type={actionType} id={actionId} target={targetId}");

        if (this.TryGetBlockReason(actionType, actionId, out var blockReason))
        {
            UIGlobals.PlayChatSoundEffect(11);
            this.toastGui.ShowError(blockReason);
            return false;
        }

        this.ActionUsed?.Invoke(actionType, actionId, targetId);
        return useActionHook.Original(thisPtr, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
    }

    private bool DetourUseActionLocation(ActionManager* thisPtr, ActionType actionType, uint actionId, ulong targetId, System.Numerics.Vector3* location, uint extraParam, byte a7)
    {
        if (!this.IsEnabled)
            return useActionLocationHook.Original(thisPtr, actionType, actionId, targetId, location, extraParam, a7);

        this.log.Debug($"[SprintBlocker] UseActionLocation: type={actionType} id={actionId} target={targetId}");

        // Don't fire ActionUsed/toast here for actions that already passed through DetourUseAction;
        // this hook only needs to catch entry points (e.g. item right-click "Use") that skip it entirely.
        if (this.TryGetBlockReason(actionType, actionId, out var blockReason))
        {
            UIGlobals.PlayChatSoundEffect(11);
            this.toastGui.ShowError(blockReason);
            return false;
        }

        return useActionLocationHook.Original(thisPtr, actionType, actionId, targetId, location, extraParam, a7);
    }

    private bool DetourTeleportWithTickets(Telepo.SelectUseTicketInvoker* thisPtr, uint aetheryteId, byte subIndex)
    {
        this.log.Debug($"[SprintBlocker] TeleportWithTickets: aetheryteId={aetheryteId} subIndex={subIndex}");

        if (!this.IsEnabled || this.UnlockTeleportReturn)
            return teleportWithTicketsHook.Original(thisPtr, aetheryteId, subIndex);

        UIGlobals.PlayChatSoundEffect(11);
        this.toastGui.ShowError("Locked: Teleport");
        return false;
    }

    private long DetourUseItem(AgentInventoryContext* thisPtr, uint itemId, InventoryType inventoryType, uint itemSlot, short a5)
    {
        if (!this.IsEnabled)
            return useItemHook.Original(thisPtr, itemId, inventoryType, itemSlot, a5);

        this.log.Debug($"[SprintBlocker] UseItem: itemId={itemId} inventoryType={inventoryType} itemSlot={itemSlot}");

        if (this.TryGetBlockReason(ActionType.Item, itemId, out var blockReason))
        {
            UIGlobals.PlayChatSoundEffect(11);
            this.toastGui.ShowError(blockReason);
            return 0;
        }

        // This hook is the only entry point reached by items that open a follow-up UI (Aetheryte
        // Tickets, etc.) instead of going through ActionManager, so raise ActionUsed here too.
        this.ActionUsed?.Invoke(ActionType.Item, itemId, 0);
        return useItemHook.Original(thisPtr, itemId, inventoryType, itemSlot, a5);
    }

    private bool TryGetBlockReason(ActionType actionType, uint actionId, out string reason)
    {
        if (this.IsLockedAction(actionType, actionId, out var lockName))
        {
            reason = $"Locked: {lockName}";
            return true;
        }

        if (this.IsSkillLocked(actionType, actionId, out var unlockLevel))
        {
            reason = $"Skill unlocks at level {unlockLevel}!";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    public void Dispose()
    {
        this.framework.Update -= this.UpdateHighlights;
        this.ClearHighlights();
        useActionHook.Dispose();
        useActionLocationHook.Dispose();
        teleportWithTicketsHook.Dispose();
        useItemHook.Dispose();
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

        var hotbarModule = RaptureHotbarModule.Instance();
        for (var slotIndex = 0L; slotIndex < actionBar->ActionBarSlotVector.LongCount; slotIndex++)
        {
            var slot = actionBar->ActionBarSlotVector[slotIndex];
            if (slot.Icon == null)
                continue;

            var hotbarSlot = hotbarModule != null
                ? hotbarModule->GetSlotById((uint)actionBar->RaptureHotbarId, (uint)slotIndex)
                : null;
            var isBlocked = hotbarSlot != null
                ? this.IsBlockedHotbarSlot(hotbarSlot->CommandType, hotbarSlot->CommandId)
                : this.IsBlockedAction((uint)slot.ActionId);
            if (!isBlocked)
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

    private bool IsBlockedHotbarSlot(RaptureHotbarModule.HotbarSlotType commandType, uint commandId)
        => commandType switch
        {
            RaptureHotbarModule.HotbarSlotType.GeneralAction => (!this.UnlockSprint && commandId == this.sprintGeneralActionId) ||
                (!this.UnlockTeleportReturn && (commandId == this.teleportGeneralActionId || commandId == this.returnGeneralActionId)),
            RaptureHotbarModule.HotbarSlotType.Action => this.IsSkillLocked(ActionType.Action, commandId, out _),
            RaptureHotbarModule.HotbarSlotType.Item => (!this.UnlockMounts && commandId == this.gysahlGreensItemId) ||
                (!this.UnlockTeleportReturn && this.teleportItemIds.Contains(commandId)),
            RaptureHotbarModule.HotbarSlotType.Mount => !this.UnlockMounts,
            RaptureHotbarModule.HotbarSlotType.Companion or RaptureHotbarModule.HotbarSlotType.BuddyAction => !this.UnlockMounts,
            RaptureHotbarModule.HotbarSlotType.CraftAction => !this.UnlockCrafters,
            _ => false,
        };

    private bool HasActionBarLocks
        => !this.UnlockSprint || !this.UnlockTeleportReturn || !this.UnlockMounts || !this.UnlockCrafters;

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

        if (!this.UnlockTeleportReturn && actionType == ActionType.Item && this.teleportItemIds.Contains(actionId))
        {
            lockName = "Teleport";
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
