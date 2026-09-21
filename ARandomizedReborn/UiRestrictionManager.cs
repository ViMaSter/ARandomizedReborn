using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

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

    private readonly IAddonLifecycle addonLifecycle;
    private readonly Configuration configuration;
    private bool enabled;

    public UiRestrictionManager(IAddonLifecycle addonLifecycle, Configuration configuration)
    {
        this.addonLifecycle = addonLifecycle;
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
            ((AtkUnitBase*)args.Addon.Address)->Close(true);
    }

    private unsafe void OnAlliedSocietyAddon(AddonEvent type, AddonArgs args)
    {
        if (!this.configuration.UnlockAlliedSocieties && !args.Addon.IsNull)
            ((AtkUnitBase*)args.Addon.Address)->Close(true);
    }

    private unsafe void OnDeepDungeonAddon(AddonEvent type, AddonArgs args)
    {
        if (!this.configuration.UnlockDeepDungeon && !args.Addon.IsNull)
            ((AtkUnitBase*)args.Addon.Address)->Close(true);
    }
}