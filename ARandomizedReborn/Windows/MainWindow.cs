using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace ARandomizedReborn.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Dictionary<string, bool> checkGroupExpanded = [];
    private bool? pendingCheckGroupToggle;

    public MainWindow(Plugin plugin)
        : base("A Randomized Reborn###ARandomizedRebornMain", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        if (ImGui.Button("Show Settings"))
        {
            plugin.ToggleConfigUi();
        }

        ImGui.SameLine();
        if (ImGui.Button("Show Bingo Board"))
        {
            plugin.ToggleBingoUi();
        }

        ImGui.Spacing();

        var enableRandomizer = plugin.Configuration.EnableRandomizer;
        if (ImGui.Checkbox("Enable Randomizer", ref enableRandomizer))
        {
            plugin.SetRandomizerEnabled(enableRandomizer);
        }

        var disableControls = !plugin.Configuration.EnableRandomizer;

        var reservedBottomHeight = 170 * ImGuiHelpers.GlobalScale;
        var listAreaHeight = MathF.Max(160 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().Y - reservedBottomHeight);
        var halfHeight = listAreaHeight / 2f;

        if (ImGui.CollapsingHeader("Unlocks", ImGuiTreeNodeFlags.DefaultOpen))
        {
            using var unlocksChild = ImRaii.Child("UnlocksChild", new Vector2(0, halfHeight), true);
            if (unlocksChild.Success)
            {
                if (disableControls)
                    ImGui.BeginDisabled();

                foreach (var unlock in plugin.GetUnlockStates())
                {
                    DrawToggleEntry(
                        $"unlock-{unlock.Definition.Key}",
                        unlock.Definition.DisplayName,
                        unlock.Definition.Description,
                        unlock.IsUnlocked,
                        value => plugin.SetUnlockState(unlock.Definition.Key, value));
                }

                if (disableControls)
                    ImGui.EndDisabled();
            }
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Checks", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // Shift-click toggles every subgroup at once instead of just this header.
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left) && ImGui.GetIO().KeyShift)
                this.pendingCheckGroupToggle = this.checkGroupExpanded.Count == 0 || this.checkGroupExpanded.Values.Any(open => !open);

            using var checksChild = ImRaii.Child("ChecksChild", new Vector2(0, halfHeight), true);
            if (checksChild.Success)
                DrawGroupedChecks(plugin);

            this.pendingCheckGroupToggle = null;
        }

        if (ImGui.Button("Reset State"))
            plugin.ResetProgress();

        ImGui.Spacing();

        // Normally a BeginChild() would have to be followed by an unconditional EndChild(),
        // ImRaii takes care of this after the scope ends.
        using (var child = ImRaii.Child("PlayerInfo", Vector2.Zero, true))
        {
            // Check if this child is drawing
            if (child.Success)
            {
                // PlayerState provides a wrapper filled with information about the player character.
                var playerState = Plugin.PlayerState;
                if (!playerState.IsLoaded)
                {
                    ImGui.Text("Our local player is currently not logged in.");
                    return;
                }
                
                if (!playerState.ClassJob.IsValid)
                {
                    ImGui.Text("Our current job is currently not valid.");
                    return;
                }
                
                ImGui.AlignTextToFramePadding();
                ImGui.Text($"Current job:");
                
                // Scaling hardcoded pixel values is important, as otherwise users with HUD scales above or below 100%
                // won't be able to see everything.
                ImGui.SameLine(120 * ImGuiHelpers.GlobalScale);
                
                // Get the icon id from a known offset + the class jobs id
                var jobIconId = 62100 + playerState.ClassJob.RowId;
                var iconTexture = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(jobIconId)).GetWrapOrEmpty();
                ImGui.Image(iconTexture.Handle, new Vector2(28, 28) * ImGuiHelpers.GlobalScale);
                
                ImGui.SameLine();
                
                // If you want to see the Macro representation of this SeString use `.ToMacroString()`
                // More info about SeStrings: https://dalamud.dev/plugin-development/sestring/
                ImGui.Text(playerState.ClassJob.Value.Abbreviation.ToString());
                
                ImGui.SameLine();
                ImGui.Text($" [Level {playerState.Level}]");
                
                // Example for querying Lumina, getting the name of our current area.
                var territoryId = Plugin.ClientState.TerritoryType;
                if (Plugin.DataManager.GetExcelSheet<TerritoryType>(ClientLanguage.English).TryGetRow(territoryId, out var territoryRow))
                {
                    ImGui.Text($"Current location:");
                    ImGui.SameLine(120 * ImGuiHelpers.GlobalScale);
                    ImGui.Text(territoryRow.PlaceName.Value.Name.ToString());
                }
                else
                {
                    ImGui.Text("Invalid territory.");
                }
            }
        }
    }

    /// <summary>A single row: checkbox for state, description shown as a tooltip on hover.</summary>
    private static void DrawToggleEntry(string id, string displayName, string description, bool value, Action<bool>? onToggle)
    {
        using var pushId = ImRaii.PushId(id);

        var localValue = value;
        if (onToggle == null)
        {
            ImGui.BeginDisabled();
            ImGui.Checkbox(displayName, ref localValue);
            ImGui.EndDisabled();
        }
        else if (ImGui.Checkbox(displayName, ref localValue))
        {
            onToggle(localValue);
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(description);
    }

    /// <summary>Checks grouped by required unlock, each group collapsible on its own.</summary>
    private void DrawGroupedChecks(Plugin plugin)
    {
        var groups = plugin.BingoSession.GetCheckStatuses()
            .GroupBy(status => status.Definition.RequiredUnlock)
            .OrderBy(group => group.Key.HasValue)
            .ThenBy(
                group => group.Key.HasValue ? Unlocks.Definitions.First(unlock => unlock.Key == group.Key.Value).DisplayName : string.Empty,
                StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var label = group.Key.HasValue
                ? Unlocks.Definitions.First(unlock => unlock.Key == group.Key.Value).DisplayName
                : "No requirement";

            using var pushId = ImRaii.PushId($"checkgroup-{group.Key?.ToString() ?? "none"}");

            if (this.pendingCheckGroupToggle.HasValue)
                ImGui.SetNextItemOpen(this.pendingCheckGroupToggle.Value, ImGuiCond.Always);

            var open = ImGui.CollapsingHeader(label, ImGuiTreeNodeFlags.DefaultOpen);
            this.checkGroupExpanded[label] = open;

            if (!open)
                continue;

            using var indent = ImRaii.PushIndent();
            foreach (var status in group)
                DrawCheckEntry(plugin, status);
        }
    }

    /// <summary>An expandable check row: overall state at a glance, per-step progress when opened.</summary>
    private static void DrawCheckEntry(Plugin plugin, CheckStatus status)
    {
        using var pushId = ImRaii.PushId($"check-{status.Definition.Id}");

        var tracker = plugin.CheckProgressTracker;
        var complete = status.IsComplete;

        var expanded = ImGui.CollapsingHeader("##expand", ImGuiTreeNodeFlags.None);
        ImGui.SameLine();

        ImGui.BeginDisabled();
        ImGui.Checkbox(status.Definition.DisplayName, ref complete);
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(status.Definition.Description);

        if (!expanded)
            return;

        using var indent = ImRaii.PushIndent();
        ImGui.TextWrapped(status.Definition.Description);

        foreach (var step in status.Definition.Steps)
            DrawStepRow(tracker, status.Definition.Id, step, complete);
    }

    private static void DrawStepRow(CheckProgressTracker tracker, string checkId, CheckStepDefinition step, bool checkComplete)
    {
        using var pushId = ImRaii.PushId(step.Id);
        var locked = checkComplete;

        if (step.Kind == ProgressStepKind.Flag)
        {
            var value = tracker.IsStepSatisfied(checkId, step);
            if (step.Automatic || locked)
            {
                ImGui.BeginDisabled();
                ImGui.Checkbox(step.Label, ref value);
                ImGui.EndDisabled();
            }
            else if (ImGui.Checkbox(step.Label, ref value))
            {
                tracker.SetFlag(checkId, step.Id, value);
            }

            if (step.Automatic)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(auto)");
            }

            return;
        }

        var current = tracker.GetValue(checkId, step.Id);
        var target = tracker.GetTarget(checkId, step);
        ImGui.Text($"{step.Label}: {Math.Min(current, target)}/{target}");

        if (step.Automatic)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(auto)");
        }
        else if (!locked)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("-"))
                tracker.AdjustCounter(checkId, step.Id, -1);

            ImGui.SameLine();
            if (ImGui.SmallButton("+"))
                tracker.AdjustCounter(checkId, step.Id, 1);
        }

        ImGui.ProgressBar(target <= 0 ? 0f : Math.Clamp(current / (float)target, 0f, 1f), new Vector2(-1, 0));
    }
}
