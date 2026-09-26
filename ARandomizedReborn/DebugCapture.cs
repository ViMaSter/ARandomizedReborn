using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AgentId = Dalamud.Game.Agent.AgentId;

namespace ARandomizedReborn;

// TEMP: captures how the Journal is populated; remove before shipping.
public sealed unsafe class DebugCapture : IDisposable
{
    private static readonly List<object> Entries = [];
    private readonly IAddonLifecycle lifecycle;
    private readonly IAgentLifecycle agentLifecycle;
    private readonly Hook<AtkComponentTreeList.Delegates.LoadAtkValues> loadHook;
    private static readonly string[] Names = ["Journal", "JournalDetail"];

    public DebugCapture(IGameInteropProvider interop, IAddonLifecycle lifecycle, IAgentLifecycle agentLifecycle)
    {
        this.lifecycle = lifecycle;
        this.agentLifecycle = agentLifecycle;
        this.loadHook = interop.HookFromAddress<AtkComponentTreeList.Delegates.LoadAtkValues>(AtkComponentTreeList.Addresses.LoadAtkValues.Value, this.OnLoad);
        this.loadHook.Enable();
        lifecycle.RegisterListener(AddonEvent.PreRefresh, Names, this.OnAddon);
        lifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, Names, this.OnAddon);
        lifecycle.RegisterListener(AddonEvent.PreReceiveEvent, Names, this.OnAddon);
        lifecycle.RegisterListener(AddonEvent.PreSetup, Names, this.OnAddon);
        agentLifecycle.RegisterListener(AgentEvent.PreReceiveEvent, AgentId.QuestJournal, this.OnAgent);
    }

    public static object Snapshot()
    {
        lock (Entries)
            return Entries.ToArray();
    }

    private static void Add(object entry)
    {
        lock (Entries)
        {
            Entries.Add(entry);
            if (Entries.Count > 60)
                Entries.RemoveAt(0);
        }
    }

    private static object[] Values(AtkValue* values, int count)
    {
        var result = new List<object>();
        for (var index = 0; values != null && index < count && index < 2048; index++)
            result.Add($"{index} {values[index].Type} {(values[index].Type == 0 ? string.Empty : values[index].GetValueAsString())}");
        return result.ToArray();
    }

    private void OnLoad(AtkComponentTreeList* list, int count, AtkValue* values, int uintOffset, int stringOffset, int uintPerItem, int stringPerItem, int itemCount, ListComponentCallBackInterface* callback)
    {
        var owner = list->OwnerNode == null ? 0 : (nint)list->OwnerNode;
        Add(new { kind = "LoadAtkValues", list = $"0x{(nint)list:X}", owner = $"0x{owner:X}", count, uintOffset, stringOffset, uintPerItem, stringPerItem, itemCount, callback = $"0x{(nint)callback:X}", values = Values(values, count) });
        this.loadHook.Original(list, count, values, uintOffset, stringOffset, uintPerItem, stringPerItem, itemCount, callback);
    }

    private void OnAddon(AddonEvent type, AddonArgs args)
    {
        switch (args)
        {
            case AddonRefreshArgs refresh:
                Add(new { kind = type.ToString(), args.AddonName, values = Values((AtkValue*)refresh.AtkValues, (int)refresh.AtkValueCount) });
                break;
            case AddonSetupArgs setup:
                Add(new { kind = type.ToString(), args.AddonName, values = Values((AtkValue*)setup.AtkValues, (int)setup.AtkValueCount) });
                break;
            case AddonReceiveEventArgs receive:
                Add(new { kind = type.ToString(), args.AddonName, eventType = receive.AtkEventType, receive.EventParam, data = $"0x{receive.AtkEventData:X}", atkEvent = $"0x{receive.AtkEvent:X}" });
                break;
            default:
                Add(new { kind = type.ToString(), args.AddonName });
                break;
        }
    }

    private void OnAgent(AgentEvent type, AgentArgs args)
    {
        if (args is AgentReceiveEventArgs receive)
            Add(new { kind = "AgentReceiveEvent", receive.EventKind, values = Values((AtkValue*)receive.AtkValues, (int)receive.ValueCount) });
    }

    public void Dispose()
    {
        this.loadHook.Dispose();
        this.lifecycle.UnregisterListener(this.OnAddon);
        this.agentLifecycle.UnregisterListener(AgentEvent.PreReceiveEvent, AgentId.QuestJournal, this.OnAgent);
    }
}
