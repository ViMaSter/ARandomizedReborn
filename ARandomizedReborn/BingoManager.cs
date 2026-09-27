using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Events.EventDataTypes;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Graphics;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Runtime.InteropServices;
using System.Text;
using AgentId = Dalamud.Game.Agent.AgentId;

namespace ARandomizedReborn;

/// <summary>Feeds the randomized board into the game's native WeeklyBingo layout.</summary>
public sealed unsafe class BingoManager : IDisposable
{
    private const string AddonName = "WeeklyBingo";
    private const string BonusInfoAddonName = "WeeklyBingoBonusInfo";
    private const string ConfirmAddonName = "SelectYesno";
    private const ulong ContextYesNoEventKind = 100;
    private const uint WondrousTailsItemId = 2002023;
    private const int CellCount = 16;
    private const int CompletionValueStart = 1;
    private const int DutyValueStart = 44;
    private const int DescriptionValueStart = 78;
    private const int SecondChanceValueIndex = 35;
    private const int MaxSecondChancePoints = 9;
    private const int AgentSelectDuty = 2;
    private const uint DeadlineNodeId = 8;
    private const uint WindowNodeId = 129;
    private const uint WindowTitleNodeId = 3;
    private const uint DutyContainerNodeId = 9;
    private const uint DutyBonusNodeId = 13; // second-chance clover inside a duty button
    private const uint DutySealedOverlayNodeId = 12;
    private const uint DutyFrameNodeId = 16;
    private const uint MessageNodeId = 34;
    private const uint StickerContainerNodeId = 41;
    private const uint StickerCountNodeId = 60;
    private const uint StickerMaxNodeId = 61;
    private const uint StickerImageNodeId = 2;
    private const uint RewardListPanelNodeId = 62;
    private const uint RewardListTitleNodeId = 63;
    private const uint SecondChanceButtonNodeId = 33;
    private const uint MenuNodeIdBase = 0x52420000;
    private static readonly uint[] RewardListRowNodeIds = [64, 77, 88, 99];
    private static readonly ByteColor MenuTextColor = new() { R = 0x4A, G = 0x35, B = 0x20, A = 255 };
    private static readonly ByteColor MenuHoverColor = new() { R = 0xA0, G = 0x4A, B = 0x18, A = 255 };

    private readonly Plugin plugin;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IAgentLifecycle agentLifecycle;
    private readonly IAddonEventManager addonEventManager;
    private readonly BingoSession session;
    private readonly Configuration configuration;
    private readonly Dictionary<string, nint> pinnedStrings = [];
    private readonly List<MenuRow> menu;
    private readonly List<nint> menuNodes = [];
    private readonly List<nint> cellNodes = [];
    private readonly List<IAddonEventHandle> events = [];
    private nint menuAddon;
    private bool enabled;
    private bool openedByPlugin;
    private Action? pendingConfirm;
    private uint confirmAddonId;
    private int centerConfirmFrames;

    public BingoManager(Plugin plugin, IAddonLifecycle addonLifecycle, IAgentLifecycle agentLifecycle, IAddonEventManager addonEventManager, BingoSession session, Configuration configuration)
    {
        this.plugin = plugin;
        this.addonLifecycle = addonLifecycle;
        this.agentLifecycle = agentLifecycle;
        this.addonEventManager = addonEventManager;
        this.session = session;
        this.configuration = configuration;

        this.menu =
        [
            new(() => $"Randomizer: {(configuration.EnableRandomizer ? "On" : "Off")}",
                () => "Turn this off to play normally. Your board and progress are kept and resume when you turn it back on.",
                () => plugin.SetRandomizerEnabled(!configuration.EnableRandomizer)),
            new(() => $"Hint mode: {(configuration.BingoHintModeEnabled ? "On" : "Off")}",
                () => "When on, squares whose required unlock is still locked are tinted red. When off (default), you have to guess.",
                () =>
                {
                    configuration.BingoHintModeEnabled = !configuration.BingoHintModeEnabled;
                    configuration.Save();
                }),
        ];
        foreach (var difficulty in Enum.GetValues<BingoDifficulty>())
        {
            this.menu.Add(new(
                () => $"New session: {difficulty}",
                () => $"{DifficultyLabel(difficulty)} ({BingoBoard.FreeCheckTarget(difficulty)} free).\nStarting a new session locks every unlock again and shuffles the board.",
                () => this.Confirm(
                    $"Start a new {difficulty} session?\nEvery unlock is locked again and the board is shuffled.",
                    () => plugin.StartNewSession(difficulty))));
        }

        this.menu.Add(new(() => "Settings", () => "Open the plugin settings.", plugin.ToggleConfigUi));
        this.menu.Add(new(() => "Debug", () => "Open the debug window with every check and its state.", plugin.ToggleDebugUi));

        this.addonLifecycle.RegisterListener(AddonEvent.PreSetup, AddonName, this.OnSetup);
        this.addonLifecycle.RegisterListener(AddonEvent.PreRefresh, AddonName, this.OnRefresh);
        this.addonLifecycle.RegisterListener(AddonEvent.PreDraw, AddonName, this.OnDraw);
        this.addonLifecycle.RegisterListener(AddonEvent.PostDraw, AddonName, this.OnPostDraw);
        this.addonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, AddonName, this.OnReceiveEvent);
        this.addonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonName, this.OnFinalize);
        this.addonLifecycle.RegisterListener(AddonEvent.PreRefresh, BonusInfoAddonName, this.OnBonusInfoRefresh);
        this.addonLifecycle.RegisterListener(AddonEvent.PreDraw, BonusInfoAddonName, this.OnBonusInfoDraw);
        this.addonLifecycle.RegisterListener(AddonEvent.PostDraw, BonusInfoAddonName, this.OnBonusInfoDraw);
        this.addonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, BonusInfoAddonName, this.OnBonusInfoReceiveEvent);
        this.addonLifecycle.RegisterListener(AddonEvent.PreFinalize, ConfirmAddonName, this.OnConfirmFinalize);
        this.addonLifecycle.RegisterListener(AddonEvent.PostUpdate, ConfirmAddonName, this.OnConfirmUpdate);
        this.agentLifecycle.RegisterListener(AgentEvent.PreReceiveEvent, AgentId.WeeklyBingo, this.OnAgentReceiveEvent);
        this.agentLifecycle.RegisterListener(AgentEvent.PreReceiveEvent, AgentId.Context, this.OnContextReceiveEvent);
    }

    /// <summary>Overtakes the native board while the randomizer runs; <see cref="OpenBoard"/> overtakes it regardless.</summary>
    public void SetEnabled(bool enabled)
    {
        if (this.enabled == enabled)
            return;

        var wasOvertaking = this.IsOvertaking;
        this.enabled = enabled;
        if (wasOvertaking != this.IsOvertaking)
            CloseAddon();
    }

    /// <summary>Opens the native board through the Wondrous Tails key item. False if the player has no journal.</summary>
    public bool OpenBoard()
    {
        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.KeyItems);
        var slot = -1;
        for (var index = 0; container != null && index < container->Size && slot < 0; index++)
        {
            if (container->Items[index].ItemId == WondrousTailsItemId)
                slot = index;
        }

        if (slot < 0)
            return false;

        var agent = AgentModule.Instance()->GetAgentByInternalId(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId.WeeklyBingo);
        if (!agent->IsAgentActive())
        {
            this.UseJournal(slot);
            return true;
        }

        if (this.IsOvertaking)
        {
            this.openedByPlugin = true;
            return true;
        }

        // The native book is open (or still closing); reopen it once it is gone so the setup values come from us.
        CloseAddon();
        this.ReopenWhenClosed(slot, 30);
        return true;
    }

    private void ReopenWhenClosed(int slot, int attempts)
    {
        Plugin.Framework.RunOnTick(() =>
        {
            var agent = AgentModule.Instance()->GetAgentByInternalId(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId.WeeklyBingo);
            if (!agent->IsAgentActive() && RaptureAtkUnitManager.Instance()->GetAddonByName(AddonName) == null)
                this.UseJournal(slot);
            else if (attempts > 0)
                this.ReopenWhenClosed(slot, attempts - 1);
        }, delayTicks: 5);
    }

    private void UseJournal(int slot)
    {
        this.openedByPlugin = true;
        AgentInventoryContext.Instance()->UseItem(WondrousTailsItemId, InventoryType.KeyItems, (uint)slot);
    }

    public void Dispose()
    {
        this.addonLifecycle.UnregisterListener(
            this.OnSetup, this.OnRefresh, this.OnDraw, this.OnPostDraw, this.OnReceiveEvent, this.OnFinalize,
            this.OnBonusInfoRefresh, this.OnBonusInfoDraw, this.OnBonusInfoReceiveEvent, this.OnConfirmFinalize, this.OnConfirmUpdate);
        this.agentLifecycle.UnregisterListener(AgentEvent.PreReceiveEvent, AgentId.WeeklyBingo, this.OnAgentReceiveEvent);
        this.agentLifecycle.UnregisterListener(AgentEvent.PreReceiveEvent, AgentId.Context, this.OnContextReceiveEvent);
        this.DestroyMenu();
        if (this.pendingConfirm != null)
            CloseConfirmDialog(this.confirmAddonId);

        // The open addon and its tooltips still point at our strings, so close it and leak them instead of freeing.
        if (this.IsOvertaking)
            CloseAddon();
    }

    private bool IsOvertaking => this.openedByPlugin || (this.enabled && this.session.HasBoard);

    private static void CloseAddon()
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(AddonName);
        if (addon != null)
            addon->Close(true);
    }

    private static void CloseConfirmDialog(uint addonId)
    {
        var dialog = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)addonId);
        if (dialog != null && dialog->NameString == ConfirmAddonName)
            dialog->Close(true);
    }

    /// <summary>Shows the game's Yes/No dialog centered on the board and runs <paramref name="onYes"/> if confirmed.</summary>
    private void Confirm(string text, Action onYes)
    {
        if (this.pendingConfirm != null)
            CloseConfirmDialog(this.confirmAddonId);

        var context = AgentContext.Instance();
        context->OpenYesNo(text);
        this.confirmAddonId = context->YesNoAddon;
        this.pendingConfirm = onYes;
        this.centerConfirmFrames = 3;
    }

    private void OnConfirmUpdate(AddonEvent type, AddonArgs args)
    {
        var dialog = (AtkUnitBase*)args.Addon.Address;
        if (this.centerConfirmFrames <= 0 || dialog->Id != this.confirmAddonId)
            return;

        // The game places the dialog in its first update after setup; move it before it is ever drawn.
        this.centerConfirmFrames--;
        var board = RaptureAtkUnitManager.Instance()->GetAddonByName(AddonName);
        if (board == null || dialog->RootNode == null || board->RootNode == null)
            return;

        var boardWidth = board->RootNode->Width * board->Scale;
        var boardHeight = board->RootNode->Height * board->Scale;
        var dialogWidth = dialog->RootNode->Width * dialog->Scale;
        var dialogHeight = dialog->RootNode->Height * dialog->Scale;
        dialog->SetPosition(
            (short)(board->X + ((boardWidth - dialogWidth) / 2)),
            (short)(board->Y + ((boardHeight - dialogHeight) / 2)));
    }

    private void OnContextReceiveEvent(AgentEvent type, AgentArgs args)
    {
        if (this.pendingConfirm == null || args is not AgentReceiveEventArgs receive ||
            receive.EventKind != ContextYesNoEventKind || receive.ValueCount < 1 ||
            AgentContext.Instance()->YesNoAddon != this.confirmAddonId)
            return;

        // Our dialog has no game action behind it, so keep the context agent from acting on it.
        receive.PreventOriginal();
        var values = (AtkValue*)receive.AtkValues;
        var confirmed = values != null && values[0].Type == AtkValueType.Int && values[0].Int == 0;
        var action = this.pendingConfirm;
        this.pendingConfirm = null;
        CloseConfirmDialog(this.confirmAddonId);
        if (confirmed)
            action();
    }

    private void OnConfirmFinalize(AddonEvent type, AddonArgs args)
    {
        if (((AtkUnitBase*)args.Addon.Address)->Id == this.confirmAddonId)
            this.pendingConfirm = null;
    }

    private void OnSetup(AddonEvent type, AddonArgs args)
    {
        if (!this.IsOvertaking || args is not AddonSetupArgs setup)
            return;

        this.WriteValues((AtkValue*)setup.AtkValues, (int)setup.AtkValueCount);
    }

    private void OnRefresh(AddonEvent type, AddonArgs args)
    {
        if (!this.IsOvertaking || args is not AddonRefreshArgs refresh)
            return;

        this.WriteValues((AtkValue*)refresh.AtkValues, (int)refresh.AtkValueCount);
    }

    private void OnFinalize(AddonEvent type, AddonArgs args)
    {
        this.DestroyMenu();
        this.openedByPlugin = false;
        if (this.pendingConfirm != null)
            CloseConfirmDialog(this.confirmAddonId);
    }

    private void OnDraw(AddonEvent type, AddonArgs args)
    {
        if (!this.IsOvertaking)
            return;

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || !addon->IsReady)
            return;

        this.EnsureMenu(addon);
        this.UpdateMenu();
        this.WriteValues(addon->AtkValues, (int)addon->AtkValuesCount);
        var weeklyBingo = (AddonWeeklyBingo*)addon;
        if (weeklyBingo->DutySlotList.SecondChancesRemaining != null)
            weeklyBingo->DutySlotList.SecondChancesRemaining->SetNumber(this.session.SecondChancePoints);
        HideRewardList(addon);

        var hasBoard = this.session.HasBoard;
        foreach (var nodeId in new[] { DutyContainerNodeId, StickerContainerNodeId, SecondChanceButtonNodeId, StickerCountNodeId, StickerMaxNodeId })
            SetVisible(addon->GetNodeById(nodeId), hasBoard);

        SetText(addon->GetNodeById(MessageNodeId), this.BuildMessage());
        var window = (AtkComponentNode*)addon->GetNodeById(WindowNodeId);
        if (window != null && window->Component != null)
            SetText(window->Component->UldManager.SearchNodeById(WindowTitleNodeId), "A Randomized Reborn");
        if (!hasBoard)
            return;

        var completed = this.session.Cells.Count(cell => cell.IsComplete);
        SetText(addon->GetNodeById(DeadlineNodeId), $"{this.session.Difficulty}  -  Cleared {completed}/{CellCount}");
        SetText(addon->GetNodeById(StickerCountNodeId), completed.ToString());
        SetText(addon->GetNodeById(StickerMaxNodeId), $"/{CellCount}");

        var winningLine = this.session.HasWon ? this.session.WinningLine : null;
        var tooltips = &AtkStage.Instance()->TooltipManager;
        for (var index = 0; index < CellCount; index++)
        {
            var cell = this.session.Cells[index];
            var definition = cell.Definition;
            var slot = weeklyBingo->DutySlotList[index];
            if (slot.TextNode != null)
                slot.TextNode->IsDrawDisabled = true;
            if (slot.DutyButton != null)
            {
                // Undo the native "sealed" and second-chance looks; completion is shown by our tint instead.
                var uld = &slot.DutyButton->UldManager;
                SetVisible(uld->SearchNodeById(DutyBonusNodeId), false);
                SetVisible(uld->SearchNodeById(DutySealedOverlayNodeId), false);
                var frame = uld->SearchNodeById(DutyFrameNodeId);
                if (frame != null)
                    frame->Color.A = 255;
            }

            if (slot.DutyImage != null)
            {
                slot.DutyImage->LoadIconTexture(definition?.JournalIcon ?? 61419, 0);
                slot.DutyImage->Color.A = 255;
                slot.DutyImage->Width = 40;
                slot.DutyImage->Height = 40;
                slot.DutyImage->ScaleX = 1;
                slot.DutyImage->ScaleY = 1;
                var container = slot.DutyResNode;
                var containerWidth = container == null ? 72 : container->Width;
                var containerHeight = container == null ? 44 : container->Height;
                slot.DutyImage->X = (containerWidth - slot.DutyImage->Width) / 2f;
                slot.DutyImage->Y = (containerHeight - slot.DutyImage->Height) / 2f;
                this.TintCell((AtkResNode*)slot.DutyImage, cell, index, winningLine);
            }

            // The native tooltip keeps its own copy of the setup text, so point it at the current one.
            if (slot.DutyButton != null && tooltips->TooltipMap.TryGetValue((AtkResNode*)slot.DutyButton->OwnerNode, out var info, false))
                info.Value->AtkTooltipArgs.TextArgs.Text = this.Pin(this.BuildCellTooltip(cell));

            var sticker = weeklyBingo->StickerSlotList[index].Button;
            if (sticker != null)
                SetVisible(sticker->UldManager.SearchNodeById(StickerImageNodeId), cell.IsComplete);
        }
    }

    private void OnPostDraw(AddonEvent type, AddonArgs args)
    {
        if (!this.IsOvertaking)
            return;

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon != null && addon->IsReady)
        {
            HideRewardList(addon);
            RewriteNativeText(addon->RootNode);
        }
    }

    /// <summary>Keeps completed squares from entering Khloe's seal placement; clicks are handled by <see cref="OnCellClick"/>.</summary>
    private void OnAgentReceiveEvent(AgentEvent type, AgentArgs args)
    {
        if (!this.IsOvertaking || args is not AgentReceiveEventArgs receive || receive.ValueCount < 2)
            return;

        var values = (AtkValue*)receive.AtkValues;
        if (values != null && values[0].Type == AtkValueType.Int && values[0].Int == AgentSelectDuty)
            receive.PreventOriginal();
    }

    private void OnCellClick(AddonEventType type, AddonEventData data)
    {
        var index = this.cellNodes.IndexOf(data.NodeTargetPointer);
        if (!this.session.HasBoard || index < 0 || index >= CellCount || this.session.Cells[index].IsComplete)
            return;

        var checkId = this.session.Cells[index].CheckId;
        this.Confirm(
            $"If the detection didn't work, this would complete \"{Checks.DisplayName(checkId)}\" and grant \"{UnlockName(this.session.Cells[index].Reward)}\".\nAre you sure?",
            () => this.plugin.CompleteCheckManually(checkId));
    }

    private string BuildMessage()
    {
        if (this.session.IsGenerating)
        {
            var progress = this.session.Progress;
            return $"{progress?.Phase ?? "Generating..."} ({(int)((progress?.Fraction ?? 0f) * 100)}%)\nChecking that every square is reachable and that every line needs at least one unlock.";
        }

        if (this.session.GenerationFailure != null)
            return $"Generation failed: {this.session.GenerationFailure}";

        if (!this.session.HasBoard)
            return "No board yet. Pick a difficulty on the right to roll one.";

        var intro = this.session.HasWon ? "BINGO! You win - keep going for the rest." : "Complete a square to receive its Unlock.";
        return $"{intro}\nClick a square to mark it complete if automatic detection missed it.";
    }

    private string BuildCellTooltip(BingoCell cell)
    {
        var definition = cell.Definition;
        var hintMode = this.configuration.BingoHintModeEnabled;
        var builder = new StringBuilder();
        builder.Append(definition?.DisplayName ?? cell.CheckId);
        if (!string.IsNullOrEmpty(definition?.Description))
            builder.Append('\n').Append(definition.Description);
        builder.Append('\n').Append(cell.RequiredUnlock is { } required
            ? $"Requires: {UnlockName(required)}{(hintMode ? this.session.IsCellAttemptable(cell) ? " (granted)" : " (still locked)" : string.Empty)}"
            : "No unlock required");
        builder.Append('\n').Append($"Grants: {UnlockName(cell.Reward)}");
        if (cell.IsComplete)
            builder.Append('\n').Append(cell.ManualOverride ? "Completed (manual override)" : "Completed (auto-detected)");
        else if (definition is { IsFullyAutomatic: false })
            builder.Append('\n').Append("No automatic detection - mark this one yourself.");
        return builder.ToString();
    }

    private void TintCell(AtkResNode* node, BingoCell cell, int index, int[]? winningLine)
    {
        var locked = !cell.IsComplete && this.configuration.BingoHintModeEnabled && !this.session.IsCellAttemptable(cell);
        var winning = winningLine?.Contains(index) == true;
        node->MultiplyRed = 100;
        node->MultiplyGreen = (byte)(locked ? 45 : 100);
        node->MultiplyBlue = (byte)(locked ? 45 : 100);
        node->AddRed = (short)(winning ? 60 : 0);
        node->AddGreen = (short)(winning ? 45 : cell.IsComplete ? 35 : 0);
        node->AddBlue = 0;
    }

    private void EnsureMenu(AtkUnitBase* addon)
    {
        if (this.menuAddon == (nint)addon)
            return;

        this.DestroyMenu();
        var panel = addon->GetNodeById(RewardListPanelNodeId);
        if (panel == null)
            return;

        for (var index = 0; index < this.menu.Count; index++)
        {
            var text = IMemorySpace.GetUISpace()->Create<AtkTextNode>();
            var node = (AtkResNode*)text;
            node->Type = NodeType.Text;
            node->NodeId = MenuNodeIdBase + (uint)index;
            node->NodeFlags = NodeFlags.AnchorLeft | NodeFlags.AnchorTop | NodeFlags.Visible | NodeFlags.Enabled |
                              NodeFlags.RespondToMouse | NodeFlags.HasCollision | NodeFlags.EmitsEvents;
            node->Color = new ByteColor { R = 255, G = 255, B = 255, A = 255 };
            node->MultiplyRed = node->MultiplyGreen = node->MultiplyBlue = 100;
            node->ScaleX = node->ScaleY = 1;
            node->SetPositionFloat(-30, 34 + (index * 30));
            node->SetWidth(236);
            node->SetHeight(24);
            text->TextColor = MenuTextColor;
            text->FontSize = 14;
            text->LineSpacing = 24;
            text->AlignmentFontType = (byte)AlignmentType.Left;

            node->ParentNode = panel;
            var last = panel->ChildNode;
            if (last == null)
            {
                panel->ChildNode = node;
            }
            else
            {
                while (last->PrevSiblingNode != null)
                    last = last->PrevSiblingNode;
                last->PrevSiblingNode = node;
                node->NextSiblingNode = last;
            }

            panel->ChildCount++;
            node->DrawFlags |= 0xD;
            this.menuNodes.Add((nint)node);
            foreach (var eventType in new[] { AddonEventType.MouseOver, AddonEventType.MouseOut, AddonEventType.MouseClick })
            {
                if (this.addonEventManager.AddEvent((nint)addon, (nint)node, eventType, this.OnMenuEvent) is { } handle)
                    this.events.Add(handle);
            }
        }

        var weeklyBingo = (AddonWeeklyBingo*)addon;
        for (var index = 0; index < CellCount; index++)
        {
            var button = weeklyBingo->DutySlotList[index].DutyButton;
            var node = button == null ? null : (AtkResNode*)button->OwnerNode;
            this.cellNodes.Add((nint)node);
            if (node != null && this.addonEventManager.AddEvent((nint)addon, (nint)node, AddonEventType.ButtonClick, this.OnCellClick) is { } handle)
                this.events.Add(handle);
        }

        addon->UldManager.UpdateDrawNodeList();
        addon->UpdateCollisionNodeList(false);
        this.menuAddon = (nint)addon;
    }

    private void UpdateMenu()
    {
        for (var index = 0; index < this.menuNodes.Count; index++)
            SetText((AtkResNode*)this.menuNodes[index], this.menu[index].Label());
    }

    private void OnMenuEvent(AddonEventType type, AddonEventData data)
    {
        var index = this.menuNodes.IndexOf(data.NodeTargetPointer);
        if (index < 0)
            return;

        var node = (AtkResNode*)data.NodeTargetPointer;
        var text = (AtkTextNode*)node;
        var addon = (AtkUnitBase*)data.AddonPointer;
        var row = this.menu[index];
        switch (type)
        {
            case AddonEventType.MouseOver:
                text->TextColor = MenuHoverColor;
                node->DrawFlags |= 0x1;
                this.addonEventManager.SetCursor(AddonCursorType.Clickable);
                AtkStage.Instance()->TooltipManager.ShowTooltip(addon->Id, node, row.Tooltip());
                break;
            case AddonEventType.MouseOut:
                text->TextColor = MenuTextColor;
                node->DrawFlags |= 0x1;
                this.addonEventManager.ResetCursor();
                AtkStage.Instance()->TooltipManager.HideTooltip(addon->Id);
                break;
            case AddonEventType.MouseClick:
                row.OnClick();
                UIGlobals.PlaySoundEffect(1);
                break;
        }
    }

    private void DestroyMenu()
    {
        foreach (var handle in this.events)
            this.addonEventManager.RemoveEvent(handle);
        this.events.Clear();
        this.cellNodes.Clear();

        var addon = (AtkUnitBase*)this.menuAddon;
        foreach (var address in this.menuNodes)
        {
            var node = (AtkResNode*)address;
            var parent = node->ParentNode;
            if (node->NextSiblingNode != null)
                node->NextSiblingNode->PrevSiblingNode = node->PrevSiblingNode;
            else if (parent != null && parent->ChildNode == node)
                parent->ChildNode = node->PrevSiblingNode;
            if (node->PrevSiblingNode != null)
                node->PrevSiblingNode->NextSiblingNode = node->NextSiblingNode;
            if (parent != null && parent->ChildCount > 0)
                parent->ChildCount--;
            node->ParentNode = node->PrevSiblingNode = node->NextSiblingNode = null;
            node->Destroy(true);
        }

        if (addon != null && this.menuNodes.Count > 0)
        {
            addon->UldManager.UpdateDrawNodeList();
            addon->UpdateCollisionNodeList(false);
        }
        this.menuNodes.Clear();
        this.menuAddon = 0;
    }

    private static void SetVisible(AtkResNode* node, bool visible)
    {
        if (node != null && node->IsVisible() != visible)
            node->ToggleVisibility(visible);
    }

    private static void SetText(AtkResNode* node, string text)
    {
        if (node == null || node->Type != NodeType.Text)
            return;

        var textNode = (AtkTextNode*)node;
        if (textNode->NodeText.ToString() != text.Replace('\n', '\r'))
            textNode->SetText(text);
    }

    private static string UnlockName(UnlockKey key)
        => Unlocks.Definitions.First(unlock => unlock.Key == key).DisplayName;

    private static string DifficultyLabel(BingoDifficulty difficulty)
        => difficulty switch
        {
            BingoDifficulty.Easy => "Easy - lots of checks you can do right away",
            BingoDifficulty.Medium => "Medium - a mix",
            BingoDifficulty.Hard => "Hard - exactly one check you can start with",
            _ => difficulty.ToString(),
        };

    private sealed record MenuRow(Func<string> Label, Func<string> Tooltip, Action OnClick);

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
                label.Contains("Change one Bingo Square", StringComparison.OrdinalIgnoreCase))
        {
            receive.PreventOriginal();
            this.ReplaceOneIncomplete();
        }
        else if (label.Contains("Shuffle", StringComparison.OrdinalIgnoreCase))
        {
            receive.PreventOriginal();
            this.plugin.ShuffleIncomplete(this.session.Difficulty);
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
                label.Contains("Change one Bingo Square", StringComparison.OrdinalIgnoreCase))
            {
                receive.PreventOriginal();
                this.ReplaceOneIncomplete();
            }
            else if (label.Contains("Shuffle", StringComparison.OrdinalIgnoreCase))
            {
                receive.PreventOriginal();
                this.plugin.ShuffleIncomplete(this.session.Difficulty);
            }

            return;
        }

        // Let the game create its native second-chance window. Its option events are
        // intercepted above and routed to the randomized board instead of Khloe's book.
    }

    private void ReplaceOneIncomplete()
    {
        var index = this.session.Cells.ToList().FindIndex(cell => !cell.IsComplete);
        if (index >= 0)
            this.session.ReplaceIncompleteCell(index);
    }

    private void WriteValues(AtkValue* values, int count)
    {
        if (values == null || count <= DutyValueStart + CellCount - 1 || this.session.Cells.Count < CellCount)
            return;

        for (var index = 0; index < CellCount; index++)
        {
            var cell = this.session.Cells[index];
            values[CompletionValueStart + index].SetBool(cell.IsComplete);
            values[DutyValueStart + index].SetUInt((uint)(index + 1));
            SetString(values + DescriptionValueStart + index, this.BuildCellTooltip(cell));
        }

        if (count > 40)
            SetString(values + 40, "Complete a task to receive an Unlock.");

        if (count > SecondChanceValueIndex)
            values[SecondChanceValueIndex].SetUInt((uint)this.session.SecondChancePoints);
    }

    private byte* Pin(string text)
    {
        if (!this.pinnedStrings.TryGetValue(text, out var pointer))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            pointer = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            Marshal.WriteByte(pointer, bytes.Length, 0);
            this.pinnedStrings[text] = pointer;
        }

        return (byte*)pointer;
    }

    private void SetString(AtkValue* value, string text)
    {
        value->Type = AtkValueType.String;
        value->String = this.Pin(text);
    }

    private void WriteBonusInfoValues(AtkValue* values, int count)
    {
        if (values == null || count < 6)
            return;

        SetString(values, $"Second Chance Points: {this.session.SecondChancePoints}/{MaxSecondChancePoints}");
        SetString(values + 1, "Change one Bingo Square (1 Point)");
        SetString(values + 2, "Shuffle incomplete Bingo Squares (2 Points)");
        SetString(values + 4, "Replace one incomplete Bingo Square. Its Unlock remains available.");
        SetString(values + 5, "Shuffle all incomplete Bingo Squares while keeping completed Bingo Squares and Unlocks.");
    }

    private static void RewriteBonusInfoText(AtkResNode* node, int points)
    {
        if (node == null)
            return;

        if ((ushort)node->Type == 3)
        {
            var text = (AtkTextNode*)node;
            var current = text->NodeText.ToString();
            if (current.StartsWith("※Second Chance points can be carried over", StringComparison.OrdinalIgnoreCase))
            {
                node->ToggleVisibility(false);
                return;
            }
            else if (current.Contains("Second Chance Points:", StringComparison.OrdinalIgnoreCase))
                text->SetText($"Second Chance Points: {points}/{MaxSecondChancePoints}");
            else if (current.StartsWith("Retry", StringComparison.OrdinalIgnoreCase))
                text->SetText("Change one Bingo Square (1 Point)");
            else if (current.StartsWith("Shuffle", StringComparison.OrdinalIgnoreCase))
                text->SetText("Shuffle incomplete Bingo Squares (2 Points)");
            else if (current.StartsWith("Restores the status", StringComparison.OrdinalIgnoreCase))
                text->SetText("Replace one incomplete Bingo Square. Its Unlock remains available.");
            else if (current.StartsWith("Changes the location", StringComparison.OrdinalIgnoreCase))
                text->SetText("Replace all incomplete Bingo Squares while keeping completed Bingo Squares and Unlocks.");
            else if (current.StartsWith("Second Chance points can be earned", StringComparison.OrdinalIgnoreCase))
                text->SetText("Plugin second-chance points change or shuffle incomplete Bingo Squares.");
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
            if (current.StartsWith("Retry", StringComparison.OrdinalIgnoreCase))
                text->SetText("Change one Bingo Square (1 Point)");
            else if (current.StartsWith("Shuffle", StringComparison.OrdinalIgnoreCase))
                text->SetText("Shuffle incomplete Bingo Squares (2 Points)");
        }

        var component = (ushort)node->Type >= 1000 ? ((AtkComponentNode*)node)->Component : null;
        var firstChild = component == null ? node->ChildNode : component->UldManager.RootNode;
        for (var child = firstChild; child != null; child = child->PrevSiblingNode)
            RewriteNativeText(child);
    }

    /// <summary>Keeps the reward list panel as the frame for the plugin menu but hides Khloe's rewards.</summary>
    private static void HideRewardList(AtkUnitBase* addon)
    {
        foreach (var nodeId in RewardListRowNodeIds)
            SetVisible(addon->GetNodeById(nodeId), false);

        SetVisible(addon->GetNodeById(RewardListPanelNodeId), true);
        SetText(addon->GetNodeById(RewardListTitleNodeId), "A Randomized Reborn");
    }

}