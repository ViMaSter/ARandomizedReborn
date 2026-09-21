using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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

        if (ImGui.Button("Reset progress (keep board)"))
            this.plugin.ResetProgress();

        ImGui.Separator();

        using var table = ImRaii.Table("##debug-checks", 5,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Check", ImGuiTableColumnFlags.WidthStretch, 3);
        ImGui.TableSetupColumn("Requires", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn("On board", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthStretch, 1.5f);
        ImGui.TableSetupColumn("Force", ImGuiTableColumnFlags.WidthStretch, 1);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var status in statuses)
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
                ImGui.TextDisabled(status.Definition.HasAutomaticDetection ? "not hit" : "not hit (manual only)");
            else if (status.WasManualOverride)
                ImGui.TextColored(ManualColor, "overridden");
            else
                ImGui.TextColored(DoneColor, "detected");

            ImGui.TableNextColumn();
            using (ImRaii.Disabled(status.IsComplete))
            {
                if (ImGui.SmallButton($"Complete##force-{status.Definition.Id}"))
                    this.plugin.CompleteCheckManually(status.Definition.Id);
            }
        }
    }
}
