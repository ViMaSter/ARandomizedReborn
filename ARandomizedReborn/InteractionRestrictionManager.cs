using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace ARandomizedReborn;

public sealed unsafe class InteractionRestrictionManager : IDisposable
{
    private readonly IGameInteropProvider gameInteropProvider;
    private readonly IToastGui toastGui;
    private readonly Configuration configuration;
    private Hook<TargetSystem.Delegates.InteractWithObject>? interactionHook;

    public InteractionRestrictionManager(
        IGameInteropProvider gameInteropProvider,
        IToastGui toastGui,
        Configuration configuration)
    {
        this.gameInteropProvider = gameInteropProvider;
        this.toastGui = toastGui;
        this.configuration = configuration;
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            this.EnableHook();
            return;
        }

        this.DisableHook();
    }

    public void Dispose()
    {
        this.DisableHook();
    }

    private void EnableHook()
    {
        if (this.interactionHook != null)
            return;

        this.interactionHook = this.gameInteropProvider.HookFromAddress<TargetSystem.Delegates.InteractWithObject>(
            TargetSystem.MemberFunctionPointers.InteractWithObject,
            this.OnInteractWithObject);
        this.interactionHook.Enable();
    }

    private void DisableHook()
    {
        if (this.interactionHook == null)
            return;

        this.interactionHook.Dispose();
        this.interactionHook = null;
    }

    private ulong OnInteractWithObject(TargetSystem* targetSystem, GameObject* gameObject, bool checkLineOfSight)
    {
        if (gameObject != null && this.IsBlocked(gameObject, out var lockName))
        {
            this.toastGui.ShowError($"Locked: {lockName}");
            return 0;
        }

        return this.interactionHook!.Original(targetSystem, gameObject, checkLineOfSight);
    }

    private bool IsBlocked(GameObject* gameObject, out string lockName)
    {
        lockName = string.Empty;
        var intendedUse = GameMain.Instance()->CurrentTerritoryIntendedUseId;
        var objectKind = gameObject->ObjectKind;

        if (!this.configuration.UnlockHousing && IsHousingTerritory(intendedUse) && objectKind is ObjectKind.HousingEventObject or ObjectKind.EventObj)
        {
            lockName = "visiting player / FC houses";
            return true;
        }

        if (!this.configuration.UnlockGoldSaucer && intendedUse == TerritoryIntendedUse.GoldSaucer && objectKind is ObjectKind.EventNpc or ObjectKind.EventObj)
        {
            lockName = "Gold Saucer";
            return true;
        }

        if (!this.configuration.UnlockDeepDungeon && intendedUse == TerritoryIntendedUse.DeepDungeon && objectKind is ObjectKind.EventNpc or ObjectKind.EventObj)
        {
            lockName = "Palace of the Dead / deep dungeon";
            return true;
        }

        if (!this.configuration.UnlockInnRooms && intendedUse == TerritoryIntendedUse.Inn && objectKind is ObjectKind.EventNpc or ObjectKind.EventObj)
        {
            lockName = "inn rooms";
            return true;
        }

        if (!this.configuration.UnlockRetainers && objectKind == ObjectKind.Retainer)
        {
            lockName = "retainer access";
            return true;
        }

        return false;
    }

    private static bool IsHousingTerritory(TerritoryIntendedUse intendedUse)
        => intendedUse is TerritoryIntendedUse.HousingOutdoor or TerritoryIntendedUse.HousingIndoor;
}