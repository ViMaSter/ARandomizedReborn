using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace ARandomizedReborn;

public sealed unsafe class InteractionRestrictionManager : IDisposable
{
    private readonly IGameInteropProvider gameInteropProvider;
    private readonly IToastGui toastGui;
    private readonly Configuration configuration;
    private Hook<TargetSystem.Delegates.InteractWithObject>? interactionHook;

    public event Action<uint>? AetheryteInteracted;

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

        var aetheryteId = gameObject != null && gameObject->ObjectKind == ObjectKind.Aetheryte
            ? gameObject->BaseId
            : 0;
        var result = this.interactionHook!.Original(targetSystem, gameObject, checkLineOfSight);
        if (aetheryteId != 0)
            this.AetheryteInteracted?.Invoke(aetheryteId);

        return result;
    }

    private bool IsBlocked(GameObject* gameObject, out string lockName)
    {
        lockName = string.Empty;
        var intendedUse = GameMain.Instance()->CurrentTerritoryIntendedUseId;
        var objectKind = gameObject->ObjectKind;
        var eventContent = GetEventContent(gameObject);

        if (!this.configuration.UnlockAlliedSocieties && this.IsAlliedSocietyQuest(gameObject))
        {
            lockName = "beast tribes / allied societies";
            return true;
        }

        if (!this.configuration.UnlockHousing && IsHousingTerritory(intendedUse) && objectKind is ObjectKind.HousingEventObject or ObjectKind.EventObj)
        {
            lockName = "visiting player / FC houses";
            return true;
        }

        if (!this.configuration.UnlockHousing && eventContent is EventHandlerContent.HousingAethernet)
        {
            lockName = "visiting player / FC houses";
            return true;
        }

        if (!this.configuration.UnlockGoldSaucer && intendedUse == TerritoryIntendedUse.GoldSaucer && objectKind is ObjectKind.EventNpc or ObjectKind.EventObj)
        {
            lockName = "Gold Saucer";
            return true;
        }

        if (!this.configuration.UnlockGoldSaucer && IsGoldSaucerEvent(eventContent))
        {
            lockName = "Gold Saucer";
            return true;
        }

        if (!this.configuration.UnlockDeepDungeon && intendedUse == TerritoryIntendedUse.DeepDungeon && objectKind is ObjectKind.EventNpc or ObjectKind.EventObj)
        {
            lockName = "Palace of the Dead / deep dungeon";
            return true;
        }

        if (!this.configuration.UnlockDeepDungeon && eventContent is EventHandlerContent.DeepDungeon or EventHandlerContent.InstanceContentGuide)
        {
            lockName = "Palace of the Dead / deep dungeon";
            return true;
        }

        if (!this.configuration.UnlockInnRooms && intendedUse == TerritoryIntendedUse.Inn && objectKind is ObjectKind.EventNpc or ObjectKind.EventObj)
        {
            lockName = "inn rooms";
            return true;
        }

        if (!this.configuration.UnlockInnRooms && this.IsInnWarp(gameObject))
        {
            lockName = "inn rooms";
            return true;
        }

        if (!this.configuration.UnlockRetainers &&
            (objectKind == ObjectKind.Retainer ||
             (objectKind is ObjectKind.EventObj or ObjectKind.HousingEventObject &&
              gameObject->GetName().ToString().Equals("Summoning Bell", StringComparison.OrdinalIgnoreCase))))
        {
            lockName = "retainer access";
            return true;
        }

        return false;
    }

    private static bool IsHousingTerritory(TerritoryIntendedUse intendedUse)
        => intendedUse is TerritoryIntendedUse.HousingOutdoor or TerritoryIntendedUse.HousingIndoor;

    private bool IsAlliedSocietyQuest(GameObject* gameObject)
    {
        if (GetEventContent(gameObject) != EventHandlerContent.Quest || gameObject->EventHandler == null)
            return false;

        var questEventHandler = (QuestEventHandler*)gameObject->EventHandler;
        return questEventHandler->BeastTribeId != 0 || questEventHandler->SatisfactionNpc != 0 || questEventHandler->DailyQuestPool != 0;
    }

    private bool IsInnWarp(GameObject* gameObject)
    {
        if (GetEventContent(gameObject) != EventHandlerContent.Warp || gameObject->EventHandler == null)
            return false;

        var warpEventHandler = (WarpEventHandler*)gameObject->EventHandler;
        return warpEventHandler->CustomDefineSheetName.ToString().Contains("Inn", StringComparison.OrdinalIgnoreCase);
    }

    private static EventHandlerContent GetEventContent(GameObject* gameObject)
    {
        if (gameObject->EventHandler != null)
            return gameObject->EventHandler->Info.EventId.ContentId;

        return gameObject->EventId.ContentId;
    }

    private static bool IsGoldSaucerEvent(EventHandlerContent eventContent)
        => eventContent is EventHandlerContent.GoldSaucerArcadeMachine
            or EventHandlerContent.GoldSaucerTalk
            or EventHandlerContent.LotteryDaily
            or EventHandlerContent.LotteryWeekly
            or EventHandlerContent.TripleTriad;
}