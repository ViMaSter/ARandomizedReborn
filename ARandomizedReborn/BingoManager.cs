using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Runtime.InteropServices;
using System.Text;

namespace ARandomizedReborn;

/// <summary>Feeds the randomized board into the game's native WeeklyBingo layout.</summary>
public sealed unsafe class BingoManager : IDisposable
{
    private const string AddonName = "WeeklyBingo";
    private const string BonusInfoAddonName = "WeeklyBingoBonusInfo";
    private const int CellCount = 16;
    private const int CompletionValueStart = 1;
    private const int DutyValueStart = 44;
    private const int DescriptionValueStart = 78;
    private const int SecondChanceValueIndex = 35;
    private const int MaxSecondChancePoints = 9;
    private const uint RewardListNodeId = 64;
    private const uint SecondChanceButtonNodeId = 33;

    private readonly IAddonLifecycle addonLifecycle;
    private readonly BingoSession session;
    private readonly Action replaceOneIncomplete;
    private readonly Action shuffleIncomplete;
    private readonly Dictionary<string, nint> pinnedStrings = [];
    private bool enabled;

    public BingoManager(IAddonLifecycle addonLifecycle, BingoSession session, Action replaceOneIncomplete, Action shuffleIncomplete)
    {
        this.addonLifecycle = addonLifecycle;
        this.session = session;
        this.replaceOneIncomplete = replaceOneIncomplete;
        this.shuffleIncomplete = shuffleIncomplete;
    }

    public void SetEnabled(bool enabled)
    {
        if (this.enabled == enabled)
            return;

        this.enabled = enabled;
        if (enabled)
        {
            this.addonLifecycle.RegisterListener(AddonEvent.PreRefresh, AddonName, this.OnRefresh);
            this.addonLifecycle.RegisterListener(AddonEvent.PreDraw, AddonName, this.OnDraw);
            this.addonLifecycle.RegisterListener(AddonEvent.PostDraw, AddonName, this.OnPostDraw);
            this.addonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, AddonName, this.OnReceiveEvent);
            this.addonLifecycle.RegisterListener(AddonEvent.PreRefresh, BonusInfoAddonName, this.OnBonusInfoRefresh);
            this.addonLifecycle.RegisterListener(AddonEvent.PreDraw, BonusInfoAddonName, this.OnBonusInfoDraw);
            this.addonLifecycle.RegisterListener(AddonEvent.PostDraw, BonusInfoAddonName, this.OnBonusInfoDraw);
            this.addonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, BonusInfoAddonName, this.OnBonusInfoReceiveEvent);
        }
        else
        {
            this.addonLifecycle.UnregisterListener(
                this.OnRefresh, this.OnDraw, this.OnPostDraw, this.OnReceiveEvent,
                this.OnBonusInfoRefresh, this.OnBonusInfoDraw, this.OnBonusInfoReceiveEvent);
        }
    }

    public void Dispose()
    {
        this.SetEnabled(false);
        foreach (var pointer in this.pinnedStrings.Values)
            Marshal.FreeHGlobal(pointer);
        this.pinnedStrings.Clear();
    }

    private void OnRefresh(AddonEvent type, AddonArgs args)
    {
        if (!this.IsOvertaking || args is not AddonRefreshArgs refresh)
            return;

        this.WriteValues((AtkValue*)refresh.AtkValues, (int)refresh.AtkValueCount);
    }

    private void OnDraw(AddonEvent type, AddonArgs args)
    {
        if (!this.IsOvertaking)
            return;

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || !addon->IsReady)
            return;

        this.WriteValues(addon->AtkValues, (int)addon->AtkValuesCount);
        var weeklyBingo = (AddonWeeklyBingo*)addon;
        if (weeklyBingo->DutySlotList.SecondChancesRemaining != null)
            weeklyBingo->DutySlotList.SecondChancesRemaining->SetNumber(this.session.SecondChancePoints);
        var rewardList = addon->GetNodeById(RewardListNodeId);
        if (rewardList != null && rewardList->IsVisible())
            rewardList->ToggleVisibility(false);

        for (var index = 0; index < CellCount; index++)
        {
            var cell = this.session.Cells[index];
            var definition = cell.Definition;
            var slot = weeklyBingo->DutySlotList[index];
            if (slot.TextNode != null)
                slot.TextNode->IsDrawDisabled = true;
            if (slot.DutyImage != null)
            {
                slot.DutyImage->LoadIconTexture(definition?.JournalIcon ?? 61419, 0);
                slot.DutyImage->Width = 40;
                slot.DutyImage->Height = 40;
                slot.DutyImage->ScaleX = 1;
                slot.DutyImage->ScaleY = 1;
                var container = slot.DutyResNode;
                var containerWidth = container == null ? 72 : container->Width;
                var containerHeight = container == null ? 44 : container->Height;
                slot.DutyImage->X = (containerWidth - slot.DutyImage->Width) / 2f;
                slot.DutyImage->Y = (containerHeight - slot.DutyImage->Height) / 2f;
            }
        }

        RewriteNativeText(addon->RootNode);
    }

    private void OnPostDraw(AddonEvent type, AddonArgs args)
    {
        if (!this.IsOvertaking)
            return;

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon != null && addon->IsReady)
            RewriteNativeText(addon->RootNode);
    }

    private void OnBonusInfoRefresh(AddonEvent type, AddonArgs args)
    {
        if (!this.IsOvertaking || args is not AddonRefreshArgs refresh)
            return;

        this.WriteBonusInfoValues((AtkValue*)refresh.AtkValues, (int)refresh.AtkValueCount);
    }

    private void OnBonusInfoDraw(AddonEvent type, AddonArgs args)
    {
        if (!this.IsOvertaking)
            return;

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || !addon->IsReady)
            return;

        this.WriteBonusInfoValues(addon->AtkValues, (int)addon->AtkValuesCount);
        RewriteBonusInfoText(addon->RootNode, this.session.SecondChancePoints);
    }

    private void OnBonusInfoReceiveEvent(AddonEvent type, AddonArgs args)
    {
        if (!this.IsOvertaking || args is not AddonReceiveEventArgs receive ||
            (AtkEventType)receive.AtkEventType != AtkEventType.ButtonClick || receive.AtkEvent == 0)
            return;

        var target = ((AtkEvent*)receive.AtkEvent)->Target;
        var button = (AtkComponentButton*)target;
        var label = button == null || button->ButtonTextNode == null
            ? string.Empty
            : button->ButtonTextNode->NodeText.ToString();
        if (label.Contains("Retry", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("Change one check", StringComparison.OrdinalIgnoreCase))
        {
            receive.PreventOriginal();
            this.replaceOneIncomplete();
        }
        else if (label.Contains("Shuffle", StringComparison.OrdinalIgnoreCase))
        {
            receive.PreventOriginal();
            this.shuffleIncomplete();
        }
    }

    private void OnReceiveEvent(AddonEvent type, AddonArgs args)
    {
        if (!this.IsOvertaking || args is not AddonReceiveEventArgs receive ||
            (AtkEventType)receive.AtkEventType != AtkEventType.ButtonClick || receive.AtkEvent == 0)
            return;

        var addon = (AtkUnitBase*)args.Addon.Address;
        var weeklyBingo = (AddonWeeklyBingo*)addon;
        var button = weeklyBingo->DutySlotList.SecondChanceButton;
        var atkEvent = (AtkEvent*)receive.AtkEvent;
        var target = atkEvent->Target;
        var eventNode = atkEvent->Node;
        var targetNode = (AtkResNode*)target;
        var isSecondChance = button != null &&
                             (target == (AtkEventTarget*)button ||
                              target == (AtkEventTarget*)button->OwnerNode ||
                              eventNode == button->OwnerNode ||
                              (targetNode != null && targetNode->NodeId == SecondChanceButtonNodeId) ||
                              (eventNode != null && eventNode->NodeId == SecondChanceButtonNodeId));
        if (!isSecondChance)
        {
            var optionButton = (AtkComponentButton*)target;
            var label = optionButton == null || optionButton->ButtonTextNode == null
                ? string.Empty
                : optionButton->ButtonTextNode->NodeText.ToString();
            if (label.Contains("Retry", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("Change one check", StringComparison.OrdinalIgnoreCase))
            {
                receive.PreventOriginal();
                this.replaceOneIncomplete();
            }
            else if (label.Contains("Shuffle", StringComparison.OrdinalIgnoreCase))
            {
                receive.PreventOriginal();
                this.shuffleIncomplete();
            }

            return;
        }

        // Let the game create its native second-chance window. Its option events are
        // intercepted above and routed to the randomized board instead of Khloe's book.
    }

    private bool IsOvertaking => this.enabled && this.session.HasBoard;

    private void WriteValues(AtkValue* values, int count)
    {
        if (values == null || count <= DutyValueStart + CellCount - 1 || this.session.Cells.Count < CellCount)
            return;

        for (var index = 0; index < CellCount; index++)
        {
            var cell = this.session.Cells[index];
            values[CompletionValueStart + index].SetBool(cell.IsComplete);
            values[DutyValueStart + index].SetUInt((uint)(index + 1));
            SetString(values + DescriptionValueStart + index, cell.Definition?.Description ?? cell.CheckId);
        }

        if (count > SecondChanceValueIndex)
            values[SecondChanceValueIndex].SetUInt((uint)this.session.SecondChancePoints);
    }

    private void SetString(AtkValue* value, string text)
    {
        if (!this.pinnedStrings.TryGetValue(text, out var pointer))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            pointer = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Marshal.WriteByte(pointer, bytes.Length, 0);
            this.pinnedStrings[text] = pointer;
        }

        value->Type = AtkValueType.String;
        value->String = (byte*)pointer;
    }

    private void WriteBonusInfoValues(AtkValue* values, int count)
    {
        if (values == null || count < 6)
            return;

        SetString(values, $"Second Chance Points: {this.session.SecondChancePoints}/{MaxSecondChancePoints}");
        SetString(values + 1, "Change one check (1 Point)");
        SetString(values + 2, "Shuffle incomplete checks (2 Points)");
        SetString(values + 4, "Replace one incomplete plugin check.");
        SetString(values + 5, "Shuffle all incomplete plugin checks while keeping completed checks.");
    }

    private static void RewriteBonusInfoText(AtkResNode* node, int points)
    {
        if (node == null)
            return;

        if ((ushort)node->Type == 3)
        {
            var text = (AtkTextNode*)node;
            var current = text->NodeText.ToString();
            if (current.Contains("Second Chance Points:", StringComparison.OrdinalIgnoreCase))
                text->SetText($"Second Chance Points: {points}/{MaxSecondChancePoints}");
            else if (current.StartsWith("Retry", StringComparison.OrdinalIgnoreCase))
                text->SetText("Change one check (1 Point)");
            else if (current.StartsWith("Shuffle", StringComparison.OrdinalIgnoreCase))
                text->SetText("Shuffle incomplete checks (2 Points)");
            else if (current.StartsWith("Restores the status", StringComparison.OrdinalIgnoreCase))
                text->SetText("Replace one incomplete plugin check.");
            else if (current.StartsWith("Changes the location", StringComparison.OrdinalIgnoreCase))
                text->SetText("Shuffle all incomplete plugin checks while keeping completed checks.");
            else if (current.StartsWith("Second Chance points can be earned", StringComparison.OrdinalIgnoreCase))
                text->SetText("Plugin second-chance points are used for changing or shuffling incomplete checks.");
        }

        var component = (ushort)node->Type >= 1000 ? ((AtkComponentNode*)node)->Component : null;
        var firstChild = component == null ? node->ChildNode : component->UldManager.RootNode;
        for (var child = firstChild; child != null; child = child->PrevSiblingNode)
            RewriteBonusInfoText(child, points);
    }

    private static void RewriteNativeText(AtkResNode* node)
    {
        if (node == null)
            return;

        if ((ushort)node->Type == 3)
        {
            var text = (AtkTextNode*)node;
            var current = text->NodeText.ToString();
            if (current.Contains("Retry", StringComparison.OrdinalIgnoreCase))
                text->SetText("Change one check (1 Point)");
            else if (current.Contains("Shuffle", StringComparison.OrdinalIgnoreCase))
                text->SetText("Shuffle incomplete checks (2 Points)");
        }

        var component = (ushort)node->Type >= 1000 ? ((AtkComponentNode*)node)->Component : null;
        var firstChild = component == null ? node->ChildNode : component->UldManager.RootNode;
        for (var child = firstChild; child != null; child = child->PrevSiblingNode)
            RewriteNativeText(child);
    }

}