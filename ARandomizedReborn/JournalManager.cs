using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Events.EventDataTypes;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ARandomizedReborn;

public sealed unsafe class JournalManager : IDisposable
{
    private readonly IFramework framework;
    private readonly IGameGui gameGui;
    private readonly IAddonLifecycle lifecycle;
    private readonly IAddonEventManager events;
    private readonly BingoSession session;
    private readonly CheckProgressTracker progress;
    private readonly Dictionary<nint, NativePanel> panels = [];
    private bool enabled;
    private bool showCompleted;
    private int page;
    private string? selectedId;
    private string? renderedState;

    public JournalManager(IFramework framework, IGameGui gameGui, IAddonLifecycle lifecycle, IAddonEventManager events, BingoSession session, CheckProgressTracker progress)
    {
        this.framework = framework;
        this.gameGui = gameGui;
        this.lifecycle = lifecycle;
        this.events = events;
        this.session = session;
        this.progress = progress;
    }

    public void SetEnabled(bool enabled)
    {
        if (this.enabled == enabled)
            return;

        this.enabled = enabled;
        if (enabled)
        {
            this.lifecycle.RegisterListener(AddonEvent.PreFinalize, "Journal", this.OnFinalize);
            this.lifecycle.RegisterListener(AddonEvent.PreFinalize, "JournalDetail", this.OnFinalize);
            this.framework.Update += this.OnUpdate;
        }
        else
        {
            this.framework.Update -= this.OnUpdate;
            this.lifecycle.UnregisterListener(AddonEvent.PreFinalize, "Journal", this.OnFinalize);
            this.lifecycle.UnregisterListener(AddonEvent.PreFinalize, "JournalDetail", this.OnFinalize);
            foreach (var address in this.panels.Keys.ToArray())
                this.RemovePanel(address);
            this.renderedState = null;
        }
    }

    public void Dispose() => this.SetEnabled(false);

    private void OnFinalize(AddonEvent type, AddonArgs args)
    {
        this.RemovePanel(args.Addon.Address);
        this.renderedState = null;
    }

    private void OnUpdate(IFramework framework)
    {
        var module = AgentModule.Instance();
        var agent = module == null ? null : (AgentQuestJournal*)module->GetAgentByInternalId(AgentId.QuestJournal);
        if (agent == null || !agent->IsAgentActive() || this.gameGui.GameUiHidden)
            return;

        var journal = (AtkUnitBase*)this.gameGui.GetAddonByName("Journal").Address;
        var detail = (AtkUnitBase*)this.gameGui.GetAddonByName("JournalDetail").Address;
        if (journal == null || detail == null || !journal->IsReady || !detail->IsReady)
            return;

        var cells = this.session.Cells.Where(cell => cell.IsComplete == this.showCompleted && cell.Definition != null).ToArray();
        if (!cells.Any(cell => cell.CheckId == this.selectedId))
            this.selectedId = cells.FirstOrDefault()?.CheckId;

        var state = string.Join("|", cells.Select(cell => $"{cell.CheckId}:{this.session.GetJournalPreference(cell.CheckId)}:" +
            string.Join(",", cell.Definition!.Steps.Select(step => this.progress.GetValue(cell.CheckId, step.Id))))) +
            $"/{this.showCompleted}/{this.page}/{this.selectedId}/{(nint)journal}/{(nint)detail}";
        if (state == this.renderedState && this.panels.ContainsKey((nint)journal) && this.panels.ContainsKey((nint)detail))
        {
            foreach (var panel in this.panels.Values)
            {
                foreach (var (address, _) in panel.Hidden)
                    ((AtkResNode*)address)->ToggleVisibility(false);
            }

            return;
        }

        this.RemovePanel((nint)journal);
        this.RemovePanel((nint)detail);
        this.RenderList(journal, cells);
        this.RenderDetail(detail, cells.FirstOrDefault(cell => cell.CheckId == this.selectedId));
        this.renderedState = state;
    }

    private void RenderList(AtkUnitBase* addon, BingoCell[] cells)
    {
        var panel = this.CreatePanel(addon);
        if (panel == null)
            return;

        this.AddText(panel, 18, 28, 130, "Current", !this.showCompleted, () => this.ChangeView(false));
        this.AddText(panel, 164, 28, 130, "Complete", this.showCompleted, () => this.ChangeView(true));

        var entries = new List<(BingoCell? Cell, string Label)>();
        if (this.showCompleted)
        {
            entries.AddRange(cells.OrderBy(cell => cell.Definition!.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(cell => ((BingoCell?)cell, cell.Definition!.DisplayName)));
        }
        else
        {
            foreach (var group in cells.GroupBy(cell => cell.RequiredUnlock)
                         .OrderBy(group => group.Key.HasValue ? 1 : 0)
                         .ThenBy(group => group.Key?.ToString()))
            {
                var heading = group.Key is { } required
                    ? Unlocks.Definitions.First(unlock => unlock.Key == required).DisplayName
                    : "No unlock required";
                entries.Add((null, heading));
                entries.AddRange(group.OrderBy(cell => cell.Definition!.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(cell => ((BingoCell?)cell, cell.Definition!.DisplayName)));
            }
        }

        const int pageSize = 14;
        this.page = Math.Clamp(this.page, 0, Math.Max(0, (entries.Count - 1) / pageSize));
        for (var index = this.page * pageSize; index < Math.Min(entries.Count, (this.page + 1) * pageSize); index++)
        {
            var (cell, label) = entries[index];
            var y = 68 + ((index % pageSize) * 31);
            if (cell == null)
            {
                this.AddText(panel, 18, y, 470, label, false);
                continue;
            }

            this.AddText(panel, 24, y, 23, "+", true);
            this.AddText(panel, 52, y, this.showCompleted ? (ushort)382 : (ushort)335, label, cell.CheckId == this.selectedId,
                () => { this.selectedId = cell.CheckId; this.renderedState = null; });
            this.AddText(panel, this.showCompleted ? 440 : 389, y, 65, "Lv. 01", false);
            if (!this.showCompleted)
            {
                var preference = this.session.GetJournalPreference(cell.CheckId);
                this.AddText(panel, 465, y, 52, preference switch { 1 => "[+]", 2 => "[X]", _ => "[ ]" }, preference == 1,
                    () => { this.session.CycleJournalPreference(cell.CheckId); this.renderedState = null; });
            }
        }

        if (this.page > 0)
            this.AddText(panel, 20, 516, 105, "< Previous", false, () => { this.page--; this.renderedState = null; });
        if ((this.page + 1) * pageSize < entries.Count)
            this.AddText(panel, 370, 516, 115, "Next >", false, () => { this.page++; this.renderedState = null; });
        this.FinishPanel(panel);
    }

    private void RenderDetail(AtkUnitBase* addon, BingoCell? cell)
    {
        var panel = this.CreatePanel(addon);
        if (panel == null)
            return;

        if (cell?.Definition is { } definition)
        {
            this.AddText(panel, 35, 39, 475, definition.DisplayName, true);
            this.AddText(panel, 35, 76, 150, "Lv. 01", false);
            this.AddText(panel, 35, 105, 56, "+", true);
            this.AddText(panel, 35, 180, 475, "Objective", true);
            for (var index = 0; index < Math.Min(definition.Steps.Count, 9); index++)
            {
                var step = definition.Steps[index];
                var done = cell.IsComplete || this.progress.IsStepSatisfied(definition.Id, step);
                var text = step.Kind == ProgressStepKind.Counter
                    ? $"{step.Label} ({this.progress.GetValue(definition.Id, step.Id)}/{this.progress.GetTarget(definition.Id, step)})"
                    : step.Label;
                this.AddText(panel, 42, 220 + (index * 40), 465, text, !done);
            }
        }

        this.FinishPanel(panel);
    }

    private void ChangeView(bool completed)
    {
        this.showCompleted = completed;
        this.page = 0;
        this.selectedId = null;
        this.renderedState = null;
    }

    private NativePanel? CreatePanel(AtkUnitBase* addon)
    {
        var root = addon->RootNode;
        if (root == null || root->ChildNode == null)
            return null;

        var panel = new NativePanel((nint)addon);
        for (var node = root->ChildNode; node != null; node = node->PrevSiblingNode)
        {
            if (node == (AtkResNode*)addon->WindowNode)
                continue;

            panel.Hidden.Add(((nint)node, node->IsVisible()));
            node->ToggleVisibility(false);
        }

        this.panels[(nint)addon] = panel;
        return panel;
    }

    private void AddText(NativePanel panel, float x, float y, ushort width, string text, bool highlighted, Action? click = null)
    {
        var addon = (AtkUnitBase*)panel.Address;
        var root = addon->RootNode;
        var node = IMemorySpace.GetUISpace()->Create<AtkTextNode>();
        if (node == null)
            return;

        node->NodeId = (uint)(0xF000 + panel.Nodes.Count);
        node->Type = NodeType.Text;
        node->NodeFlags = NodeFlags.AnchorLeft | NodeFlags.AnchorTop | NodeFlags.Enabled;
        if (click != null)
            node->NodeFlags |= NodeFlags.RespondToMouse | NodeFlags.HasCollision | NodeFlags.EmitsEvents;
        node->DrawFlags = 12;
        node->SetWidth(width);
        node->SetHeight(26);
        node->SetPositionFloat(x, y);
        node->FontSize = 14;
        node->TextFlags = TextFlags.Edge | TextFlags.Ellipsis;
        node->SetText(text);
        node->TextColor = highlighted
            ? new ByteColor { R = 245, G = 206, B = 107, A = 255 }
            : new ByteColor { R = 222, G = 218, B = 207, A = 255 };
        node->EdgeColor = new ByteColor { R = 22, G = 20, B = 18, A = 255 };

        this.AttachNode(panel, (AtkResNode*)node);
        if (click != null)
        {
            var handle = this.events.AddEvent((nint)addon, (nint)node, AddonEventType.MouseClick, (_, _) => click());
            if (handle != null)
                panel.Handles.Add(handle);
        }
    }

    private void AttachNode(NativePanel panel, AtkResNode* node)
    {
        var root = ((AtkUnitBase*)panel.Address)->RootNode;
        var last = root->ChildNode;
        while (last->PrevSiblingNode != null)
            last = last->PrevSiblingNode;
        last->PrevSiblingNode = node;
        node->ParentNode = root;
        node->NextSiblingNode = last;
        root->ChildCount++;
        panel.Nodes.Add((nint)node);
    }

    private void FinishPanel(NativePanel panel)
    {
        var addon = (AtkUnitBase*)panel.Address;
        addon->UldManager.UpdateDrawNodeList();
        addon->UpdateCollisionNodeList(false);
    }

    private void RemovePanel(nint address)
    {
        if (!this.panels.Remove(address, out var panel))
            return;

        var addon = (AtkUnitBase*)address;
        foreach (var handle in panel.Handles)
            this.events.RemoveEvent(handle);

        foreach (var nodeAddress in panel.Nodes)
        {
            var node = (AtkResNode*)nodeAddress;
            if (node->NextSiblingNode != null)
                node->NextSiblingNode->PrevSiblingNode = node->PrevSiblingNode;
            if (node->PrevSiblingNode != null)
                node->PrevSiblingNode->NextSiblingNode = node->NextSiblingNode;
            node->ParentNode->ChildCount--;
            node->Destroy(true);
        }

        foreach (var (nodeAddress, visible) in panel.Hidden)
            ((AtkResNode*)nodeAddress)->ToggleVisibility(visible);
        addon->UldManager.UpdateDrawNodeList();
        addon->UpdateCollisionNodeList(false);
    }

    private sealed class NativePanel(nint address)
    {
        public nint Address { get; } = address;
        public List<(nint Address, bool Visible)> Hidden { get; } = [];
        public List<nint> Nodes { get; } = [];
        public List<IAddonEventHandle> Handles { get; } = [];
    }
}