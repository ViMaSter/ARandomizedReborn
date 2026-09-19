using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
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

    public bool IsBlocking { get; set; }

    public SprintBlocker(IGameInteropProvider gameInteropProvider, IDataManager dataManager, IPluginLog log, IToastGui toastGui)
    {
        this.toastGui = toastGui;

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
    }

    private bool DetourUseAction(ActionManager* thisPtr, ActionType actionType, uint actionId, ulong targetId, uint extraParam, ActionManager.UseActionMode mode, uint comboRouteId, bool* outOptAreaTargeted)
    {
        if (IsBlocking && sprintGeneralActionId != 0 && actionType == ActionType.GeneralAction && actionId == sprintGeneralActionId)
        {
            UIGlobals.PlayChatSoundEffect(11);
            this.toastGui.ShowError("Sprint is diabled!");
            return false;
        }

        return useActionHook.Original(thisPtr, actionType, actionId, targetId, extraParam, mode, comboRouteId, outOptAreaTargeted);
    }

    public void Dispose()
    {
        useActionHook.Dispose();
    }
}
