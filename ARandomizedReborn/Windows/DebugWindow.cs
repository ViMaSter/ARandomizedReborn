using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace ARandomizedReborn.Windows;

public class DebugWindow : Window, IDisposable
{
    private static readonly Vector4 DoneColor = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 ManualColor = new(0.95f, 0.78f, 0.35f, 1f);
    private static readonly Vector4 LockedColor = new(0.90f, 0.45f, 0.45f, 1f);

    private readonly Plugin plugin;
    private bool onlyBoardChecks;
    private bool hideCompleted;

    public DebugWindow(Plugin plugin)
        : base("A Randomized Reborn - Debug###ARandomizedRebornDebug")
    {
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var session = this.plugin.BingoSession;
        var statuses = session.GetCheckStatuses();

        ImGui.Text($"Checks: {statuses.Count} total, {Checks.FreeChecks.Count} without requirements, {Checks.GatedChecks.Count} gated.");
        ImGui.Text($"Unlocks: {Unlocks.Definitions.Count} ({Unlocks.Definitions.Count(definition => Unlocks.Get(this.plugin.Configuration, definition.Key))} granted)");

        ImGui.Checkbox("Only board checks", ref this.onlyBoardChecks);
        ImGui.SameLine();
        ImGui.Checkbox("Hide completed", ref this.hideCompleted);

        var showAllInJournal = this.plugin.Configuration.JournalShowAllChecks;
        if (ImGui.Checkbox("Show all checks in Journal (not just board)", ref showAllInJournal))
        {
            this.plugin.Configuration.JournalShowAllChecks = showAllInJournal;
            this.plugin.Configuration.Save();
        }

        if (ImGui.Button("Reset progress (keep board)"))
            this.plugin.ResetProgress();

        ImGui.Separator();

        using var table = ImRaii.Table("##debug-checks", 6,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Check", ImGuiTableColumnFlags.WidthStretch, 3);
        ImGui.TableSetupColumn("Requires", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn("On board", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Has ever triggered", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn("Force", ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.NoSort, 1);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        var sortedStatuses = statuses.OrderBy(status => status.Definition.DisplayName, StringComparer.OrdinalIgnoreCase).AsEnumerable();
        var sortSpecs = ImGui.TableGetSortSpecs();
        if (sortSpecs.SpecsCount > 0)
        {
            var spec = sortSpecs.Specs;
            var ascending = spec.SortDirection == ImGuiSortDirection.Ascending;
            sortedStatuses = spec.ColumnIndex switch
            {
                0 => ascending
                    ? statuses.OrderBy(status => status.Definition.DisplayName, StringComparer.OrdinalIgnoreCase)
                    : statuses.OrderByDescending(status => status.Definition.DisplayName, StringComparer.OrdinalIgnoreCase),
                1 => ascending
                    ? statuses.OrderBy(GetRequiresName, StringComparer.OrdinalIgnoreCase)
                    : statuses.OrderByDescending(GetRequiresName, StringComparer.OrdinalIgnoreCase),
                2 => ascending
                    ? statuses.OrderBy(status => status.IsOnBoard)
                    : statuses.OrderByDescending(status => status.IsOnBoard),
                3 => ascending
                    ? statuses.OrderBy(GetStateSortKey)
                    : statuses.OrderByDescending(GetStateSortKey),
                4 => ascending
                    ? statuses.OrderBy(status => status.HasEverTriggered)
                    : statuses.OrderByDescending(status => status.HasEverTriggered),
                _ => sortedStatuses,
            };
        }

        foreach (var status in sortedStatuses)
        {
            if (this.onlyBoardChecks && !status.IsOnBoard)
                continue;

            if (this.hideCompleted && status.IsComplete)
                continue;

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(status.Definition.DisplayName);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"{status.Definition.Id}\n{status.Definition.Description}");

            ImGui.TableNextColumn();
            if (status.Definition.RequiredUnlock is { } required)
            {
                var name = Unlocks.Definitions.First(definition => definition.Key == required).DisplayName;
                ImGui.TextColored(status.IsAttemptable ? DoneColor : LockedColor, name);
            }
            else
            {
                ImGui.TextDisabled("-");
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(status.IsOnBoard ? "yes" : "no");

            ImGui.TableNextColumn();
            if (!status.IsComplete)
                ImGui.TextDisabled(status.Definition.IsFullyAutomatic ? "not hit" : "not hit (manual only)");
            else if (status.WasManualOverride)
                ImGui.TextColored(ManualColor, "overridden");
            else
                ImGui.TextColored(DoneColor, "detected");

            ImGui.TableNextColumn();
            ImGui.TextColored(status.HasEverTriggered ? DoneColor : LockedColor, status.HasEverTriggered ? "yes" : "no");
            if (status.HasEverTriggered)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton($"Reset##ever-triggered-{status.Definition.Id}"))
                    session.ResetEverTriggered(status.Definition.Id);
            }

            ImGui.TableNextColumn();
            using (ImRaii.Disabled(status.IsComplete))
            {
                if (ImGui.SmallButton($"Complete##force-{status.Definition.Id}"))
                    this.plugin.CompleteCheckManually(status.Definition.Id);
            }
        }
    }

    private static string GetRequiresName(CheckStatus status)
        => status.Definition.RequiredUnlock is { } required
            ? Unlocks.Definitions.First(definition => definition.Key == required).DisplayName
            : string.Empty;

    /// <summary>Orders as: not hit, not hit (manual only), overridden, detected.</summary>
    private static int GetStateSortKey(CheckStatus status)
    {
        if (!status.IsComplete)
            return status.Definition.IsFullyAutomatic ? 0 : 1;

        return status.WasManualOverride ? 2 : 3;
    }
}
