using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace ARandomizedReborn.Windows;

public class BingoWindow : Window, IDisposable
{
    private static readonly Vector4 CompleteColor = new(0.18f, 0.48f, 0.22f, 1f);
    private static readonly Vector4 CompleteHoverColor = new(0.24f, 0.60f, 0.28f, 1f);
    private static readonly Vector4 LockedColor = new(0.42f, 0.16f, 0.16f, 1f);
    private static readonly Vector4 LockedHoverColor = new(0.52f, 0.20f, 0.20f, 1f);
    private static readonly Vector4 WinningColor = new(0.85f, 0.70f, 0.20f, 1f);

    private readonly Plugin plugin;

    private int pendingOverrideIndex = -1;
    private BingoDifficulty pendingDifficulty = BingoDifficulty.Medium;
    private bool openOverridePopup;
    private bool openNewSessionPopup;

    public BingoWindow(Plugin plugin)
        : base("A Randomized Reborn - Bingo###ARandomizedRebornBingo")
    {
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 520),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose() { }

    public override void Draw()
    {
        var session = this.plugin.BingoSession;

        var enabled = this.plugin.Configuration.EnableRandomizer;
        if (ImGui.Checkbox("Randomizer active", ref enabled))
            this.plugin.SetRandomizerEnabled(enabled);

        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Turn this off to play normally. Your board and progress are kept and resume when you turn it back on.");

        ImGui.SameLine(0, 20 * ImGuiHelpers.GlobalScale);
        if (ImGui.Button("New session..."))
        {
            this.pendingDifficulty = session.Difficulty;
            this.openNewSessionPopup = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Debug"))
            this.plugin.ToggleDebugUi();

        ImGui.SameLine();
        if (ImGui.Button("Settings"))
            this.plugin.ToggleConfigUi();

        ImGui.Separator();

        if (session.IsGenerating)
        {
            var progress = session.Progress;
            ImGui.Text(progress?.Phase ?? "Generating...");
            ImGui.ProgressBar(progress?.Fraction ?? 0f, new Vector2(-1, 0));
            ImGui.TextWrapped("Checking that every square is reachable and that every line needs at least one unlock.");
        }
        else if (session.GenerationFailure != null)
        {
            ImGui.TextColored(LockedHoverColor, $"Generation failed: {session.GenerationFailure}");
        }

        if (!session.HasBoard)
        {
            ImGui.TextWrapped("No board yet. Hit \"New session...\" to roll one.");
            this.DrawNewSessionPopup();
            return;
        }

        ImGui.Text($"Difficulty: {session.Difficulty}");
        ImGui.SameLine(0, 20 * ImGuiHelpers.GlobalScale);
        var completed = session.Cells.Count(cell => cell.IsComplete);
        ImGui.Text($"Cleared: {completed}/{BingoBoard.CellCount}");

        if (session.HasWon)
        {
            ImGui.SameLine(0, 20 * ImGuiHelpers.GlobalScale);
            ImGui.TextColored(WinningColor, "BINGO! You win - keep going for the rest.");
        }

        ImGui.Spacing();
        this.DrawBoard();

        ImGui.Spacing();
        ImGui.TextDisabled("Click a square to manually mark it complete if automatic detection missed it.");

        this.DrawOverridePopup();
        this.DrawNewSessionPopup();
    }

    private void DrawBoard()
    {
        var session = this.plugin.BingoSession;
        var winningLine = session.HasWon ? session.WinningLine : null;

        var available = ImGui.GetContentRegionAvail().X;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var cellWidth = (available - (spacing * (BingoBoard.Size - 1))) / BingoBoard.Size;
        var cellSize = new Vector2(cellWidth, 86 * ImGuiHelpers.GlobalScale);

        for (var index = 0; index < BingoBoard.CellCount; index++)
        {
            if (index % BingoBoard.Size != 0)
                ImGui.SameLine();

            var cell = session.Cells[index];
            var definition = cell.Definition;
            var attemptable = session.IsCellAttemptable(cell);
            var isWinning = winningLine?.Contains(index) == true;

            var background = cell.IsComplete
                ? (isWinning ? WinningColor : CompleteColor)
                : attemptable ? ImGui.GetStyle().Colors[(int)ImGuiCol.Button] : LockedColor;
            var hover = cell.IsComplete
                ? CompleteHoverColor
                : attemptable ? ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered] : LockedHoverColor;

            using (ImRaii.PushColor(ImGuiCol.Button, background)
                       .Push(ImGuiCol.ButtonHovered, hover)
                       .Push(ImGuiCol.ButtonActive, hover))
            {
                var label = $"{Truncate(definition?.DisplayName ?? cell.CheckId, 44)}##bingo-cell-{index}";
                if (ImGui.Button(label, cellSize) && !cell.IsComplete)
                {
                    this.pendingOverrideIndex = index;
                    this.openOverridePopup = true;
                }
            }

            if (!ImGui.IsItemHovered())
                continue;

            var rewardName = Unlocks.Definitions.First(unlock => unlock.Key == cell.Reward).DisplayName;
            var requirement = cell.RequiredUnlock == null
                ? "No unlock required"
                : $"Requires: {Unlocks.Definitions.First(unlock => unlock.Key == cell.RequiredUnlock.Value).DisplayName}" +
                  (attemptable ? " (granted)" : " (still locked)");

            using var tooltip = ImRaii.Tooltip();
            ImGui.TextUnformatted(definition?.DisplayName ?? cell.CheckId);
            ImGui.Separator();
            ImGui.TextWrapped(definition?.Description ?? string.Empty);
            ImGui.TextUnformatted(requirement);
            ImGui.TextUnformatted($"Grants: {rewardName}");
            if (cell.IsComplete)
                ImGui.TextUnformatted(cell.ManualOverride ? "Completed (manual override)" : "Completed (auto-detected)");
            if (definition is { HasAutomaticDetection: false })
                ImGui.TextDisabled("No automatic detection - mark this one yourself.");
        }
    }

    private void DrawOverridePopup()
    {
        if (this.openOverridePopup)
        {
            ImGui.OpenPopup("Manual override##bingo-override");
            this.openOverridePopup = false;
        }

        var open = true;
        using var popup = ImRaii.PopupModal("Manual override##bingo-override", ref open, ImGuiWindowFlags.AlwaysAutoResize);
        if (!popup.Success)
            return;

        var session = this.plugin.BingoSession;
        if (this.pendingOverrideIndex < 0 || this.pendingOverrideIndex >= session.Cells.Count)
        {
            ImGui.CloseCurrentPopup();
            return;
        }

        var cell = session.Cells[this.pendingOverrideIndex];
        var rewardName = Unlocks.Definitions.First(unlock => unlock.Key == cell.Reward).DisplayName;

        ImGui.TextWrapped($"If the detection didn't work, this would complete \"{Checks.DisplayName(cell.CheckId)}\" and grant \"{rewardName}\".");
        ImGui.TextWrapped("Are you sure?");
        ImGui.Separator();

        if (ImGui.Button("Yes, mark it complete", new Vector2(220 * ImGuiHelpers.GlobalScale, 0)))
        {
            this.plugin.CompleteCheckManually(cell.CheckId);
            this.pendingOverrideIndex = -1;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120 * ImGuiHelpers.GlobalScale, 0)))
        {
            this.pendingOverrideIndex = -1;
            ImGui.CloseCurrentPopup();
        }
    }

    private void DrawNewSessionPopup()
    {
        if (this.openNewSessionPopup)
        {
            ImGui.OpenPopup("New session##bingo-new-session");
            this.openNewSessionPopup = false;
        }

        var open = true;
        using var popup = ImRaii.PopupModal("New session##bingo-new-session", ref open, ImGuiWindowFlags.AlwaysAutoResize);
        if (!popup.Success)
            return;

        ImGui.TextWrapped("Starting a new session locks every unlock again and shuffles the board.");
        ImGui.Separator();

        foreach (var difficulty in Enum.GetValues<BingoDifficulty>())
        {
            if (ImGui.RadioButton(DifficultyLabel(difficulty), this.pendingDifficulty == difficulty))
                this.pendingDifficulty = difficulty;

            ImGui.SameLine();
            ImGui.TextDisabled($"({BingoBoard.FreeCheckTarget(difficulty)} free)");
        }

        ImGui.Separator();
        if (ImGui.Button("Generate", new Vector2(160 * ImGuiHelpers.GlobalScale, 0)))
        {
            this.plugin.StartNewSession(this.pendingDifficulty);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", new Vector2(120 * ImGuiHelpers.GlobalScale, 0)))
            ImGui.CloseCurrentPopup();
    }

    private static string DifficultyLabel(BingoDifficulty difficulty)
        => difficulty switch
        {
            BingoDifficulty.Easy => "Easy - lots of checks you can do right away",
            BingoDifficulty.Medium => "Medium - a mix",
            BingoDifficulty.Hard => "Hard - exactly one check you can start with",
            _ => difficulty.ToString(),
        };

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength - 1), "…");
}
