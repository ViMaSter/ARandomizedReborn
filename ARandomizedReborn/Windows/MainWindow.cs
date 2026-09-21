using System;
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
        if (disableControls)
            ImGui.BeginDisabled();

        ImGui.Text("Unlocks");
        foreach (var unlock in plugin.GetUnlockStates())
        {
            var unlocked = unlock.IsUnlocked;
            if (ImGui.Checkbox(unlock.Definition.DisplayName, ref unlocked))
                plugin.SetUnlockState(unlock.Definition.Key, unlocked);

            ImGui.TextWrapped(unlock.Definition.Description);
        }

        if (disableControls)
            ImGui.EndDisabled();

        ImGui.Spacing();

        ImGui.Separator();
        ImGui.Text("Checks");
        foreach (var status in plugin.BingoSession.GetCheckStatuses())
        {
            var complete = status.IsComplete;
            ImGui.BeginDisabled();
            ImGui.Checkbox($"{status.Definition.DisplayName}##check-{status.Definition.Id}", ref complete);
            ImGui.EndDisabled();
            ImGui.TextWrapped(status.Definition.Description);
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
}
