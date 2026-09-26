using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using InteropGenerator.Runtime;
using AgentId = Dalamud.Game.Agent.AgentId;

namespace ARandomizedReborn;

/// <summary>
/// Replaces the Journal list (quest tree) and JournalDetail contents with bingo squares while keeping the native layout.
/// </summary>
public sealed unsafe class JournalManager : IDisposable
{
    // Fake quest ids for list rows; real quest ids stay well below this.
    private const int FakeIdBase = 0xF000;
    private const int AgentSetTrackingState = 17;
    private const uint GroupHeaderValue = 0xFFFF0002;
    private const uint SectionHeaderValue = 0xFFFD0004;
    private const uint LeafFlags = 0x08;
    private const uint QuestTypeIcon = 61424;
    private const string LevelText = "Lv. 01";

    // Journal node ids / setup values.
    private const uint JournalHeaderNodeId = 10;
    private const uint FullListAreaNodeId = 36; // collision area the list covers in the current view
    private const int JournalCompleteLabelIndex = 8;

    // Three-state button inside a quest row renderer.
    private const uint TrackingButtonNodeId = 5;
    private const uint TrackingHiddenImageNodeId = 5;
    private const uint TrackingBaseImageNodeId = 4;
    private const uint TrackingPriorityImageNodeId = 3;

    // Tree list item layout: 4 uints (packed id/flags, icon, quest type, tracking state) and 2 strings (name, level).
    private const int ListUIntsPerItem = 4;
    private const int ListStringsPerItem = 2;
    private const int MaxListRows = 64;

    // JournalDetail refresh value layout.
    private const int DetailValueCount = 330;
    private const int DetailObjectiveCount = 187;
    private const int DetailObjectiveStart = 188;
    private const int DetailMaxObjectives = 24;

    private static readonly string[] AddonNames = ["Journal", "JournalDetail"];
    private static readonly uint[] HiddenJournalNodeIds = [12, 24]; // genre tabs, genre dropdown
    private static readonly uint[] HiddenDetailNodeIds = [49, 53, 29]; // map/abandon/retry buttons, link button, bottom status
    private const uint DetailTitleNodeId = 38;
    private const uint DetailLevelNodeId = 9;
    private const uint DetailCanvasNodeId = 43;
    private const uint DetailObjectivesSectionNodeId = 9;
    private const int DetailQuestIdIndex = 261;

    private readonly IGameGui gameGui;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IAgentLifecycle agentLifecycle;
    private readonly BingoSession session;
    private readonly CheckProgressTracker progress;
    private readonly ushort completedColor;
    private readonly Dictionary<string, nint> pinnedStrings = [];
    private readonly AtkValue* detailValues;
    private readonly Dictionary<int, (nint Pointer, int Capacity)> detailStrings = [];
    private uint lastRealQuestId;
    private readonly AtkValue* listValues;
    private bool enabled;
    private bool showingCompleted;
    private string? selectedCheckId;
    private int? clickedCellIndex;

    public JournalManager(IGameGui gameGui, IAddonLifecycle addonLifecycle, IAgentLifecycle agentLifecycle, IDataManager dataManager, BingoSession session, CheckProgressTracker progress)
    {
        this.gameGui = gameGui;
        this.addonLifecycle = addonLifecycle;
        this.agentLifecycle = agentLifecycle;
        this.session = session;
        this.progress = progress;
        this.completedColor = QuestTrackerManager.FindCompletedColor(dataManager);
        var size = DetailValueCount * sizeof(AtkValue);
        this.detailValues = (AtkValue*)Marshal.AllocHGlobal(size);
        new Span<byte>(this.detailValues, size).Clear();
        this.listValues = (AtkValue*)Marshal.AllocHGlobal(MaxListRows * (ListUIntsPerItem + ListStringsPerItem) * sizeof(AtkValue));
    }

    private bool IsOvertaking => this.enabled && this.session.HasBoard;

    public void SetEnabled(bool enabled)
    {
        if (this.enabled == enabled)
            return;

        this.enabled = enabled;
        if (enabled)
        {
            this.addonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, AddonNames[0], this.OnJournalUpdate);
            this.addonLifecycle.RegisterListener(AddonEvent.PreDraw, AddonNames[0], this.OnJournalUpdate);
            this.addonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, AddonNames[0], this.OnJournalReceiveEvent);
            this.addonLifecycle.RegisterListener(AddonEvent.PreRefresh, AddonNames[1], this.OnDetailRefresh);
            this.addonLifecycle.RegisterListener(AddonEvent.PostRefresh, AddonNames[1], this.OnDetailRefresh);
            this.addonLifecycle.RegisterListener(AddonEvent.PreDraw, AddonNames[1], this.OnDetailDraw);
            this.agentLifecycle.RegisterListener(AgentEvent.PreReceiveEvent, AgentId.QuestJournal, this.OnAgentReceiveEvent);
        }
        else
        {
            this.addonLifecycle.UnregisterListener(this.OnJournalUpdate, this.OnJournalReceiveEvent, this.OnDetailRefresh, this.OnDetailDraw);
            this.agentLifecycle.UnregisterListener(AgentEvent.PreReceiveEvent, AgentId.QuestJournal, this.OnAgentReceiveEvent);
        }

        // Force the game to rebuild the native contents from scratch next time.
        foreach (var name in AddonNames)
        {
            var addon = (AtkUnitBase*)this.gameGui.GetAddonByName(name).Address;
            if (addon != null && addon->IsVisible)
                addon->Close(true);
        }
    }

    public void Dispose()
    {
        this.SetEnabled(false);
        Marshal.FreeHGlobal((nint)this.detailValues);
        foreach (var (pointer, _) in this.detailStrings.Values)
            Marshal.FreeHGlobal(pointer);
        // Pinned list strings and list values are intentionally kept alive: closed addons may still reference them until finalized.
    }

    private enum RowKind { Group, Section, Leaf }

    private sealed record Row(RowKind Kind, uint Value0, uint Icon, uint QuestType, string Text, string Extra, BingoCell? Cell);

    private static RowKind KindOf(AtkComponentTreeListItem* item)
        => item->Type.HasFlag(TreeListItemType.Group) ? RowKind.Group
            : item->Type.HasFlag(TreeListItemType.SectionHeader) ? RowKind.Section
            : RowKind.Leaf;

    /// <summary>The header text shows the active view; compare it with the addon's own localized "Current"/"Complete" labels.</summary>
    private static bool ShowingCompleted(AtkUnitBase* journal)
    {
        var header = journal->GetTextNodeById(JournalHeaderNodeId);
        if (header == null || journal->AtkValuesCount <= JournalCompleteLabelIndex)
            return false;

        var label = journal->AtkValues[JournalCompleteLabelIndex];
        return label.Type is AtkValueType.String or AtkValueType.ConstString or AtkValueType.ManagedString &&
               header->NodeText.ToString() == label.String.ToString();
    }

    private List<Row> BuildRows(bool completed)
    {
        var cells = this.session.Cells
            .Select((cell, index) => (cell, index))
            .Where(entry => entry.cell.Definition != null && entry.cell.IsComplete == completed)
            .ToArray();

        var rows = new List<Row>
        {
            new(RowKind.Group, GroupHeaderValue, 0, 0, completed ? "Completed Bingo Squares" : "Bingo Squares",
                $"{cells.Length}/{this.session.Cells.Count}", null),
        };

        var groups = completed
            ? cells.GroupBy(entry => entry.cell.Definition!.JournalArea)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => (Heading: group.Key, Entries: group.AsEnumerable()))
            : cells.GroupBy(entry => entry.cell.RequiredUnlock)
                .OrderBy(group => group.Key.HasValue ? (int)group.Key.Value : -1)
                .Select(group => (Heading: group.Key is { } key
                    ? Unlocks.Definitions.First(definition => definition.Key == key).DisplayName
                    : "No unlock required", Entries: group.AsEnumerable()));

        foreach (var (heading, entries) in groups)
        {
            rows.Add(new(RowKind.Section, SectionHeaderValue, 0, 0, heading, string.Empty, null));
            foreach (var (cell, index) in entries.OrderBy(entry => entry.cell.Definition!.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new(RowKind.Leaf, ((uint)(FakeIdBase + index) << 16) | LeafFlags, cell.Definition!.JournalIcon, 1,
                    cell.Definition.DisplayName, LevelText, cell));
            }
        }

        var last = rows.FindLastIndex(row => row.Cell != null);
        if (last >= 0)
            rows[last] = rows[last] with { Value0 = rows[last].Value0 | (uint)TreeListItemType.LastItemInGroup };

        if (!rows.Any(row => row.Cell?.CheckId == this.selectedCheckId))
            this.selectedCheckId = rows.FirstOrDefault(row => row.Cell != null)?.Cell!.CheckId;

        return rows;
    }

    private uint TrackingState(Row row, bool completed)
        => row.Cell == null || completed ? 0u : (uint)this.session.GetJournalPreference(row.Cell.CheckId);

    private void OnJournalUpdate(AddonEvent type, AddonArgs args)
    {
        if (type == AddonEvent.PreDraw)
            this.clickedCellIndex = null;

        if (!this.IsOvertaking)
            return;

        var addon = (AtkUnitBase*)args.Addon.Address;
        var list = FindTreeList(addon);
        if (list == null)
            return;

        var completed = ShowingCompleted(addon);
        this.showingCompleted = completed;
        var rows = this.BuildRows(completed);
        if (!this.Matches(list, rows, completed))
            this.Write(list, rows, completed);

        if (type == AddonEvent.PreDraw)
            this.SyncTrackingButtons(list, rows, completed);

        if (completed)
        {
            foreach (var nodeId in HiddenJournalNodeIds)
            {
                var node = addon->GetNodeById(nodeId);
                if (node != null && node->IsVisible())
                    node->ToggleVisibility(false);
            }

            // Without tabs and dropdown the list can take the same area it has in the current view.
            var fullArea = addon->GetNodeById(FullListAreaNodeId);
            var owner = (AtkResNode*)((AtkComponentBase*)list)->OwnerNode;
            if (fullArea != null && owner != null && (owner->Y != fullArea->Y || owner->Height != fullArea->Height))
            {
                owner->SetPositionFloat(owner->X, fullArea->Y);
                ((AtkComponentList*)list)->SetSize(owner->Width, fullArea->Height);
            }
        }
    }

    private bool Matches(AtkComponentTreeList* list, List<Row> rows, bool completed)
    {
        if (list->Items.Count != rows.Count)
            return false;

        for (var index = 0; index < rows.Count; index++)
        {
            var item = list->Items[index].Value;
            var row = rows[index];
            if (item == null || KindOf(item) != row.Kind || item->UIntValues.Count < 4 || item->StringValues.Count < 2 ||
                item->UIntValues[0] != row.Value0 || item->UIntValues[1] != row.Icon || item->UIntValues[3] != this.TrackingState(row, completed) ||
                (nint)item->StringValues[0].Value != this.Pin(row.Text) || (nint)item->StringValues[1].Value != this.Pin(row.Extra))
                return false;
        }

        return true;
    }

    /// <summary>Rebuilds the tree through the component's own loader so items, renderers and caches stay consistent.</summary>
    private void Write(AtkComponentTreeList* list, List<Row> rows, bool completed)
    {
        var count = Math.Min(rows.Count, MaxListRows);
        var stringOffset = count * ListUIntsPerItem;
        var values = this.listValues;
        new Span<byte>(values, MaxListRows * (ListUIntsPerItem + ListStringsPerItem) * sizeof(AtkValue)).Clear();
        for (var index = 0; index < count; index++)
        {
            var row = rows[index];
            var uints = values + (index * ListUIntsPerItem);
            SetUInt(uints, row.Value0);
            SetUInt(uints + 1, row.Icon);
            SetUInt(uints + 2, row.QuestType);
            SetUInt(uints + 3, this.TrackingState(row, completed));

            var strings = values + stringOffset + (index * ListStringsPerItem);
            SetString(strings, this.Pin(row.Text));
            SetString(strings + 1, this.Pin(row.Extra));
        }

        list->LoadAtkValues(stringOffset + (count * ListStringsPerItem), values, 0, stringOffset, ListUIntsPerItem, ListStringsPerItem, count,
            ((AtkComponentList*)list)->CallBackInterface);

        for (var index = 0; index < count && index < list->Items.Count; index++)
        {
            var item = list->Items[index].Value;
            if (item != null && rows[index].Kind == RowKind.Group)
                item->State |= TreeListItemState.Expanded;
        }

        var selected = rows.FindIndex(row => row.Cell != null && row.Cell.CheckId == this.selectedCheckId);
        ((AtkComponentList*)list)->SelectedItemIndex = selected;
        list->LayoutRefreshPending = true;

        static void SetUInt(AtkValue* value, uint number)
        {
            value->Type = AtkValueType.UInt;
            value->UInt = number;
        }

        // Plain (unmanaged) strings: the list keeps these pointers, which point into our pinned string cache.
        static void SetString(AtkValue* value, nint text)
        {
            value->Type = AtkValueType.String;
            value->String = (byte*)text;
        }
    }

    /// <summary>Mirrors the tracking state onto the row's three-state button (normal / priority / hidden).</summary>
    private void SyncTrackingButtons(AtkComponentTreeList* list, List<Row> rows, bool completed)
    {
        var manager = &((AtkComponentBase*)list)->UldManager;
        for (var nodeIndex = 0; manager->NodeList != null && nodeIndex < manager->NodeListCount; nodeIndex++)
        {
            var node = manager->NodeList[nodeIndex];
            if (node == null || (ushort)node->Type < 1000 || !node->IsVisible())
                continue;

            var component = ((AtkComponentNode*)node)->Component;
            if (component == null || component->GetComponentType() != ComponentType.ListItemRenderer)
                continue;

            var index = ((AtkComponentListItemRenderer*)component)->ListItemIndex;
            if (index < 0 || index >= rows.Count || rows[index].Kind != RowKind.Leaf)
                continue;

            var buttonNode = component->GetNodeById(TrackingButtonNodeId);
            if (buttonNode == null || (ushort)buttonNode->Type < 1000)
                continue;

            SetVisible(buttonNode, !completed);
            var button = ((AtkComponentNode*)buttonNode)->Component;
            if (completed || button == null)
                continue;

            var state = this.TrackingState(rows[index], completed);
            SetVisible(button->GetNodeById(TrackingHiddenImageNodeId), state == 2);
            SetVisible(button->GetNodeById(TrackingBaseImageNodeId), state != 2);
            SetVisible(button->GetNodeById(TrackingPriorityImageNodeId), state == 1);
        }

        static void SetVisible(AtkResNode* node, bool visible)
        {
            if (node != null && node->IsVisible() != visible)
                node->ToggleVisibility(visible);
        }
    }

    private void OnJournalReceiveEvent(AddonEvent type, AddonArgs args)
    {
        if (!this.IsOvertaking || args is not AddonReceiveEventArgs receive || receive.AtkEventData == 0 ||
            (AtkEventType)receive.AtkEventType is not (AtkEventType.ListItemClick or AtkEventType.ListItemDoubleClick or AtkEventType.ListItemHighlight or AtkEventType.ListItemSelect))
            return;

        var data = &((AtkEventData*)receive.AtkEventData)->ListItemData;
        if (data->ListItem == null || data->ListItem->UIntValues.Count == 0)
            return;

        var cellIndex = (int)(data->ListItem->UIntValues[0] >> 16) - FakeIdBase;
        if (cellIndex < 0 || cellIndex >= this.session.Cells.Count)
            return;

        this.clickedCellIndex = cellIndex;

        // Our rows have no map, context menu or chat link to offer.
        if (data->MouseButtonId != 0 || (AtkEventType)receive.AtkEventType == AtkEventType.ListItemDoubleClick)
            receive.PreventOriginal();
    }

    private void OnAgentReceiveEvent(AgentEvent type, AgentArgs args)
    {
        if (!this.IsOvertaking || args is not AgentReceiveEventArgs receive || receive.ValueCount < 2)
            return;

        var values = (AtkValue*)receive.AtkValues;
        if (values == null || values[0].Type != AtkValueType.Int)
            return;

        var kind = values[0].Int;
        var id = values[1].Type switch
        {
            AtkValueType.Int => values[1].Int,
            AtkValueType.UInt => (int)values[1].UInt,
            _ => -1,
        };

        var cellIndex = id - FakeIdBase;
        if (cellIndex < 0 || cellIndex >= this.session.Cells.Count)
        {
            if (this.clickedCellIndex is not { } clicked)
                return;
            cellIndex = clicked;
        }

        receive.PreventOriginal();
        this.clickedCellIndex = null;
        var cell = this.session.Cells[cellIndex];
        if (kind == AgentSetTrackingState)
        {
            if (!cell.IsComplete && receive.ValueCount >= 4 && values[3].Type == AtkValueType.Int)
                this.session.SetJournalPreference(cell.CheckId, values[3].Int);
            return;
        }

        this.selectedCheckId = cell.CheckId;
        this.RefreshDetail();
    }

    private void OnDetailRefresh(AddonEvent type, AddonArgs args)
    {
        if (!this.IsOvertaking || args is not AddonRefreshArgs refresh)
            return;

        if (type == AddonEvent.PostRefresh)
        {
            this.ApplyDetailHeader((AtkUnitBase*)args.Addon.Address);
            return;
        }

        var incoming = (AtkValue*)refresh.AtkValues;
        if (incoming != null && incoming != this.detailValues && refresh.AtkValueCount > DetailQuestIdIndex &&
            incoming[DetailQuestIdIndex].Type == AtkValueType.UInt && incoming[DetailQuestIdIndex].UInt != 0)
            this.lastRealQuestId = incoming[DetailQuestIdIndex].UInt;

        this.BuildRows(this.showingCompleted);
        this.FillDetail();
        refresh.AtkValues = (nint)this.detailValues;
        refresh.AtkValueCount = DetailValueCount;
    }

    /// <summary>Writes title and level straight into their nodes in case the addon hides them for non-quest data.</summary>
    private void ApplyDetailHeader(AtkUnitBase* addon)
    {
        var definition = this.session.Cells.FirstOrDefault(entry => entry.CheckId == this.selectedCheckId)?.Definition;
        var title = addon->GetTextNodeById(DetailTitleNodeId);
        if (title != null)
        {
            title->SetText(definition?.DisplayName ?? string.Empty);
            title->ToggleVisibility(definition != null);
        }

        var level = addon->GetTextNodeById(DetailLevelNodeId);
        if (level != null)
            level->SetText(definition == null ? string.Empty : LevelText);
    }

    private void OnDetailDraw(AddonEvent type, AddonArgs args)
    {
        if (!this.IsOvertaking)
            return;

        var addon = (AtkUnitBase*)args.Addon.Address;
        foreach (var nodeId in HiddenDetailNodeIds)
        {
            var node = addon->GetNodeById(nodeId);
            if (node != null && node->IsVisible())
                node->ToggleVisibility(false);
        }

        // Only the objectives section of the journal canvas is relevant for bingo squares.
        var canvasNode = addon->GetNodeById(DetailCanvasNodeId);
        var canvas = canvasNode != null && (ushort)canvasNode->Type >= 1000 ? ((AtkComponentNode*)canvasNode)->Component : null;
        if (canvas == null)
            return;

        for (var section = canvas->UldManager.RootNode; section != null; section = section->PrevSiblingNode)
        {
            if (section->NodeId != DetailObjectivesSectionNodeId && section->IsVisible())
                section->ToggleVisibility(false);
        }
    }

    private void RefreshDetail()
    {
        var detail = (AtkUnitBase*)this.gameGui.GetAddonByName(AddonNames[1]).Address;
        if (detail == null || !detail->IsReady)
            return;

        this.FillDetail();
        detail->OnRefresh(DetailValueCount, this.detailValues);
    }

    private void FillDetail()
    {
        var values = this.detailValues;
        for (var index = 0; index < DetailValueCount; index++)
        {
            values[index].Type = AtkValueType.Null;
            values[index].UInt64 = 0;
        }

        var cell = this.session.Cells.FirstOrDefault(entry => entry.CheckId == this.selectedCheckId);
        var definition = cell?.Definition;

        values[0].SetUInt(1);
        values[1].SetUInt(0);
        SetString(2, definition == null ? string.Empty : LevelText);
        values[4].SetUInt(1);
        SetString(5, definition?.DisplayName ?? "No bingo squares");
        values[6].SetUInt(definition == null ? 0u : QuestTypeIcon);
        values[7].SetUInt(definition?.JournalIcon is >= 62300 and < 62500 ? definition.JournalIcon : 0); // class/job crest
        values[8].SetUInt(0); // banner image
        values[9].SetUInt(0);
        values[10].SetUInt(0);
        SetString(11, string.Empty);
        SetString(12, string.Empty); // description
        SetString(13, string.Empty); // quest giver
        SetString(14, string.Empty);
        values[18].SetUInt(0); // reward count
        values[89].SetUInt(0); // optional reward count
        values[135].SetUInt(0); // completion bonus count
        SetString(136, string.Empty);

        var objectives = cell == null ? [] : this.GetObjectives(cell);
        values[DetailObjectiveCount].SetUInt((uint)objectives.Count);
        for (var index = 0; index < objectives.Count; index++)
            SetEncoded(DetailObjectiveStart + index, objectives[index]);

        values[212].SetUInt(0);
        SetString(236, string.Empty);
        values[260].SetUInt(0);
        values[DetailQuestIdIndex].SetUInt(this.lastRealQuestId);
        values[262].SetUInt(0);
        SetString(263, string.Empty);
        SetString(264, string.Empty);
        SetString(265, string.Empty);
        values[266].SetBool(false);
        values[267].SetBool(false);
        values[268].SetBool(false);
        values[269].SetUInt(0); // summary count
        values[294].SetUInt(0);
        values[306].SetUInt(0);
        values[324].SetUInt(0);
        values[325].SetUInt(0);
        values[328].SetBool(false);
        SetString(329, string.Empty);

        void SetString(int index, string text)
            => SetEncoded(index, new SeStringBuilder().AddText(text).Build().EncodeWithNullTerminator());

        // Plain strings like the game sends; each slot owns a buffer that outlives the refresh call.
        void SetEncoded(int index, byte[] encoded)
        {
            if (!this.detailStrings.TryGetValue(index, out var slot) || slot.Capacity < encoded.Length)
            {
                if (slot.Pointer != 0)
                    Marshal.FreeHGlobal(slot.Pointer);
                slot = (Marshal.AllocHGlobal(Math.Max(encoded.Length, 256)), Math.Max(encoded.Length, 256));
                this.detailStrings[index] = slot;
            }

            Marshal.Copy(encoded, 0, slot.Pointer, encoded.Length);
            values[index].Type = AtkValueType.String;
            values[index].String = (byte*)slot.Pointer;
        }
    }

    private List<byte[]> GetObjectives(BingoCell cell)
    {
        var definition = cell.Definition!;
        var result = new List<byte[]>();
        foreach (var step in definition.Steps.Take(DetailMaxObjectives))
        {
            var done = cell.IsComplete || this.progress.IsStepSatisfied(definition.Id, step);
            var text = definition.Id == "fill-armory-category" && !done && this.progress.GetFullestArmoryCategory() is { } armory
                ? $"Fill {armory.Name}: {armory.Filled}/{armory.Capacity}"
                : step.Kind == ProgressStepKind.Counter
                    ? $"{step.Label} ({(cell.IsComplete ? this.progress.GetTarget(definition.Id, step) : this.progress.GetValue(definition.Id, step.Id))}/{this.progress.GetTarget(definition.Id, step)})"
                    : step.Label;

            var builder = new SeStringBuilder();
            if (done)
                builder.AddUiForeground(this.completedColor);
            builder.AddText(text);
            if (done)
                builder.AddUiForegroundOff();
            result.Add(builder.Build().EncodeWithNullTerminator());
        }

        return result;
    }

    private nint Pin(string text)
    {
        if (this.pinnedStrings.TryGetValue(text, out var pointer))
            return pointer;

        var bytes = Encoding.UTF8.GetBytes(text);
        pointer = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        Marshal.WriteByte(pointer, bytes.Length, 0);
        this.pinnedStrings[text] = pointer;
        return pointer;
    }

    private static AtkComponentTreeList* FindTreeList(AtkUnitBase* addon)
    {
        if (addon == null || addon->UldManager.NodeList == null)
            return null;

        for (var index = 0; index < addon->UldManager.NodeListCount; index++)
        {
            var node = addon->UldManager.NodeList[index];
            if (node == null || (ushort)node->Type < 1000)
                continue;

            var component = ((AtkComponentNode*)node)->Component;
            if (component != null && component->GetComponentType() == ComponentType.TreeList)
                return (AtkComponentTreeList*)component;
        }

        return null;
    }
}
