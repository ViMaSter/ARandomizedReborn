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
    private const int CellCount = 16;
    private const int CompletionValueStart = 1;
    private const int DutyValueStart = 44;
    private const int DescriptionValueStart = 78;
    private const uint RewardListNodeId = 64;

    private readonly IAddonLifecycle addonLifecycle;
    private readonly BingoSession session;
    private readonly Dictionary<string, nint> pinnedStrings = [];
    private bool enabled;

    public BingoManager(IAddonLifecycle addonLifecycle, BingoSession session)
    {
        this.addonLifecycle = addonLifecycle;
        this.session = session;
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
        }
        else
        {
            this.addonLifecycle.UnregisterListener(this.OnRefresh, this.OnDraw);
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
        var rewardList = addon->GetNodeById(RewardListNodeId);
        if (rewardList != null && rewardList->IsVisible())
            rewardList->ToggleVisibility(false);

        var weeklyBingo = (AddonWeeklyBingo*)addon;
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

}