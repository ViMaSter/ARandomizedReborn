using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using ClientTerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace ARandomizedReborn;

public sealed class UiRestrictionManager : IDisposable
{
    private static readonly string[] EnemyCastAddonNames =
    [
        "CastBarEnemy",
        "_EnemyList",
    ];

    private static readonly string[] MapAddonNames =
    [
        "_NaviMap",
        "AreaMap",
    ];

    private static readonly string[] RetainerAddonNames =
    [
        "RetainerList",
        "RetainerItemTransferList",
        "RetainerItemTransferProgress",
        "RetainerSell",
        "RetainerTaskAsk",
        "RetainerTaskList",
        "RetainerTaskResult",
    ];

    private static readonly string[] AlliedSocietyAddonNames =
    [
        "SatisfactionSupply",
    ];

    private static readonly string[] DeepDungeonAddonNames =
    [
        "DeepDungeonInspect",
        "DeepDungeonMap",
        "DeepDungeonStatus",
        "DeepDungeonSaveData",
        "DeepDungeonScore",
        "DeepDungeonHard",
        "DeepDungeonMenu",
        "DeepDungeonResult",
    ];

    private static readonly string[] DutyAddonNames =
    [
        "ContentsFinder",
        "ContentsFinderConfirm",
        "LookingForGroupDetail",
    ];

    private readonly IAddonLifecycle addonLifecycle;
    private readonly IDataManager dataManager;
    private readonly Configuration configuration;
    private bool enabled;

    public UiRestrictionManager(IAddonLifecycle addonLifecycle, IDataManager dataManager, Configuration configuration)
    {
        this.addonLifecycle = addonLifecycle;
        this.dataManager = dataManager;
        this.configuration = configuration;
    }

    public void SetEnabled(bool enabled)
    {
        if (this.enabled == enabled)
            return;

        this.enabled = enabled;
        if (enabled)
        {
            this.RegisterListeners();
            return;
        }

        this.UnregisterListeners();
    }

    public void Dispose()
    {
        this.SetEnabled(false);
    }

    private void RegisterListeners()
    {
        this.addonLifecycle.RegisterListener(AddonEvent.PostSetup, EnemyCastAddonNames, this.OnEnemyAddon);
        this.addonLifecycle.RegisterListener(AddonEvent.PostUpdate, EnemyCastAddonNames, this.OnEnemyAddon);
        this.addonLifecycle.RegisterListener(AddonEvent.PostSetup, MapAddonNames, this.OnMapAddon);
        this.addonLifecycle.RegisterListener(AddonEvent.PostUpdate, MapAddonNames, this.OnMapAddon);
        this.addonLifecycle.RegisterListener(AddonEvent.PostSetup, RetainerAddonNames, this.OnRetainerAddon);
        this.addonLifecycle.RegisterListener(AddonEvent.PostUpdate, RetainerAddonNames, this.OnRetainerAddon);
        this.addonLifecycle.RegisterListener(AddonEvent.PostSetup, AlliedSocietyAddonNames, this.OnAlliedSocietyAddon);
        this.addonLifecycle.RegisterListener(AddonEvent.PostUpdate, AlliedSocietyAddonNames, this.OnAlliedSocietyAddon);
        this.addonLifecycle.RegisterListener(AddonEvent.PostSetup, DeepDungeonAddonNames, this.OnDeepDungeonAddon);
        this.addonLifecycle.RegisterListener(AddonEvent.PostUpdate, DeepDungeonAddonNames, this.OnDeepDungeonAddon);
        this.addonLifecycle.RegisterListener(AddonEvent.PostSetup, DutyAddonNames, this.OnDutyAddon);
        this.addonLifecycle.RegisterListener(AddonEvent.PostUpdate, DutyAddonNames, this.OnDutyAddon);
    }

    private void UnregisterListeners()
    {
        this.addonLifecycle.UnregisterListener(AddonEvent.PostSetup, EnemyCastAddonNames, this.OnEnemyAddon);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostUpdate, EnemyCastAddonNames, this.OnEnemyAddon);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostSetup, MapAddonNames, this.OnMapAddon);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostUpdate, MapAddonNames, this.OnMapAddon);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostSetup, RetainerAddonNames, this.OnRetainerAddon);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostUpdate, RetainerAddonNames, this.OnRetainerAddon);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostSetup, AlliedSocietyAddonNames, this.OnAlliedSocietyAddon);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostUpdate, AlliedSocietyAddonNames, this.OnAlliedSocietyAddon);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostSetup, DeepDungeonAddonNames, this.OnDeepDungeonAddon);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostUpdate, DeepDungeonAddonNames, this.OnDeepDungeonAddon);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostSetup, DutyAddonNames, this.OnDutyAddon);
        this.addonLifecycle.UnregisterListener(AddonEvent.PostUpdate, DutyAddonNames, this.OnDutyAddon);
    }

    private unsafe void OnEnemyAddon(AddonEvent type, AddonArgs args)
    {
        if (!this.configuration.UnlockEnemyCastBars && !args.Addon.IsNull)
            ((AtkUnitBase*)args.Addon.Address)->IsVisible = false;
    }

    private unsafe void OnMapAddon(AddonEvent type, AddonArgs args)
    {
        if (!this.configuration.UnlockMaps && !args.Addon.IsNull)
            ((AtkUnitBase*)args.Addon.Address)->IsVisible = false;
    }

    private unsafe void OnRetainerAddon(AddonEvent type, AddonArgs args)
    {
        if (!this.configuration.UnlockRetainers && !args.Addon.IsNull)
            ((AtkUnitBase*)args.Addon.Address)->IsVisible = false;
    }

    private unsafe void OnAlliedSocietyAddon(AddonEvent type, AddonArgs args)
    {
        if (!this.configuration.UnlockAlliedSocieties && !args.Addon.IsNull)
            ((AtkUnitBase*)args.Addon.Address)->IsVisible = false;
    }

    private unsafe void OnDeepDungeonAddon(AddonEvent type, AddonArgs args)
    {
        if (!this.configuration.UnlockDeepDungeon && !args.Addon.IsNull)
            ((AtkUnitBase*)args.Addon.Address)->IsVisible = false;
    }

    private unsafe void OnDutyAddon(AddonEvent type, AddonArgs args)
    {
        if (!this.ShouldHideDutyJoin(args.AddonName) || args.Addon.IsNull)
            return;

        var addon = (AtkUnitBase*)args.Addon.Address;
        switch (args.AddonName)
        {
            case "ContentsFinder":
                var contentsFinder = (AddonContentsFinder*)addon;
                if (contentsFinder->JoinButton != null)
                    contentsFinder->JoinButton->AtkComponentBase.OwnerNode->AtkResNode.ToggleVisibility(false);
                break;
            case "ContentsFinderConfirm":
                addon->IsVisible = false;
                break;
            case "LookingForGroupDetail":
                var lookingForGroup = (AddonLookingForGroupDetail*)addon;
                if (lookingForGroup->JoinPartyButton != null)
                    lookingForGroup->JoinPartyButton->AtkComponentBase.OwnerNode->AtkResNode.ToggleVisibility(false);
                foreach (var joinAllianceButton in lookingForGroup->JoinAllianceButtons)
                {
                    if (joinAllianceButton.Value != null)
                        joinAllianceButton.Value->AtkComponentBase.OwnerNode->AtkResNode.ToggleVisibility(false);
                }
                break;
        }
    }

    private unsafe bool ShouldHideDutyJoin(string addonName)
    {
        if (this.configuration.UnlockDungeons && this.configuration.UnlockTrials)
            return false;

        var kind = addonName == "LookingForGroupDetail"
            ? this.GetPartyFinderDutyKind()
            : this.GetContentsFinderDutyKind();

        return kind switch
        {
            DutyRestrictionKind.Dungeon => !this.configuration.UnlockDungeons,
            DutyRestrictionKind.Trial => !this.configuration.UnlockTrials,
            _ => !this.configuration.UnlockDungeons || !this.configuration.UnlockTrials,
        };
    }

    private unsafe DutyRestrictionKind GetContentsFinderDutyKind()
    {
        var agentModule = AgentModule.Instance();
        var agent = agentModule == null
            ? null
            : (AgentContentsFinder*)agentModule->GetAgentByInternalId(AgentId.ContentsFinder);
        if (agent == null)
            return DutyRestrictionKind.Unknown;

        var selectedDuty = agent->SelectedDuty;
        if (selectedDuty.ContentType != ContentsType.Regular || selectedDuty.Id == 0)
            return DutyRestrictionKind.Unknown;

        return this.GetContentFinderConditionKind(selectedDuty.Id);
    }

    private unsafe DutyRestrictionKind GetPartyFinderDutyKind()
    {
        var agentModule = AgentModule.Instance();
        var agent = agentModule == null
            ? null
            : (AgentLookingForGroup*)agentModule->GetAgentByInternalId(AgentId.LookingForGroup);
        if (agent == null)
            return DutyRestrictionKind.Unknown;

        var category = agent->LastViewedListing.Category;
        if (category.HasFlag(AgentLookingForGroup.DutyCategory.Dungeons))
            return DutyRestrictionKind.Dungeon;
        if (category.HasFlag(AgentLookingForGroup.DutyCategory.Trials))
            return DutyRestrictionKind.Trial;

        if (agent->LastViewedListing.DutyId != 0)
            return this.GetContentFinderConditionKind(agent->LastViewedListing.DutyId);

        return DutyRestrictionKind.Unknown;
    }

    private DutyRestrictionKind GetContentFinderConditionKind(uint rowId)
    {
        if (!this.dataManager.GetExcelSheet<ContentFinderCondition>(ClientLanguage.English).TryGetRow(rowId, out var row))
            return DutyRestrictionKind.Unknown;

        var intendedUse = this.GetTerritoryIntendedUse(row);
        return intendedUse switch
        {
            ClientTerritoryIntendedUse.Dungeon or ClientTerritoryIntendedUse.VariantDungeon or ClientTerritoryIntendedUse.CriterionDungeon or ClientTerritoryIntendedUse.CriterionDungeonSavage => DutyRestrictionKind.Dungeon,
            ClientTerritoryIntendedUse.Trial => DutyRestrictionKind.Trial,
            _ => GetContentFinderConditionTextKind(row),
        };
    }

    private ClientTerritoryIntendedUse? GetTerritoryIntendedUse(ContentFinderCondition row)
    {
        var territoryTypeProperty = typeof(ContentFinderCondition).GetProperty("TerritoryType");
        if (territoryTypeProperty?.GetValue(row) is not { } territoryTypeRef)
            return null;

        var territoryTypeRowId = GetRowRefId(territoryTypeRef);
        if (territoryTypeRowId == 0 || !this.dataManager.GetExcelSheet<TerritoryType>(ClientLanguage.English).TryGetRow(territoryTypeRowId, out var territoryType))
            return null;

        var intendedUseProperty = typeof(TerritoryType).GetProperty("TerritoryIntendedUse");
        if (intendedUseProperty?.GetValue(territoryType) is not { } intendedUse)
            return null;

        var intendedUseId = GetRowRefId(intendedUse);
        if (intendedUseId != 0)
            return (ClientTerritoryIntendedUse)intendedUseId;

        if (intendedUse is byte byteValue)
            return (ClientTerritoryIntendedUse)byteValue;
        if (intendedUse is uint uintValue)
            return (ClientTerritoryIntendedUse)uintValue;
        if (intendedUse is int intValue)
            return (ClientTerritoryIntendedUse)intValue;

        return null;
    }

    private static DutyRestrictionKind GetContentFinderConditionTextKind(ContentFinderCondition row)
    {
        var text = row.ToString() ?? string.Empty;
        if (text.Contains("Dungeon", StringComparison.OrdinalIgnoreCase))
            return DutyRestrictionKind.Dungeon;
        if (text.Contains("Trial", StringComparison.OrdinalIgnoreCase))
            return DutyRestrictionKind.Trial;

        return DutyRestrictionKind.Unknown;
    }

    private static uint GetRowRefId(object rowRef)
    {
        var rowIdProperty = rowRef.GetType().GetProperty("RowId");
        if (rowIdProperty == null)
            return 0;

        var rowId = rowIdProperty.GetValue(rowRef);
        return rowId switch
        {
            uint uintValue => uintValue,
            ushort ushortValue => ushortValue,
            byte byteValue => byteValue,
            int intValue when intValue > 0 => (uint)intValue,
            _ => 0,
        };
    }

    private enum DutyRestrictionKind
    {
        Unknown,
        Dungeon,
        Trial,
    }
}