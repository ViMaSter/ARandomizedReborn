using System;
using System.Collections.Generic;
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
        var hintMode = this.plugin.Configuration.BingoHintModeEnabled;
        if (ImGui.Checkbox("Hint mode", ref hintMode))
        {
            this.plugin.Configuration.BingoHintModeEnabled = hintMode;
            this.plugin.Configuration.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When on, squares are colored by whether their unlock is already granted. When off (default), you have to guess.");

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
        var hintMode = this.plugin.Configuration.BingoHintModeEnabled;

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var reservedBottomHeight = ImGui.GetTextLineHeightWithSpacing() + (8 * ImGuiHelpers.GlobalScale);
        var availableHeight = ImGui.GetContentRegionAvail().Y - reservedBottomHeight;

        var maxCellWidth = (availableWidth - (spacing * (BingoBoard.Size - 1))) / BingoBoard.Size;
        var maxCellHeight = (availableHeight - (spacing * (BingoBoard.Size - 1))) / BingoBoard.Size;
        var cellSide = MathF.Max(40 * ImGuiHelpers.GlobalScale, MathF.Min(maxCellWidth, maxCellHeight));
        var cellSize = new Vector2(cellSide, cellSide);
        var padding = new Vector2(8, 6) * ImGuiHelpers.GlobalScale;

        var gridWidth = (cellSide * BingoBoard.Size) + (spacing * (BingoBoard.Size - 1));
        var startX = ImGui.GetCursorPosX() + MathF.Max(0, (availableWidth - gridWidth) / 2f);

        for (var index = 0; index < BingoBoard.CellCount; index++)
        {
            if (index % BingoBoard.Size != 0)
                ImGui.SameLine();
            else
                ImGui.SetCursorPosX(startX);

            var cell = session.Cells[index];
            var definition = cell.Definition;
            var attemptable = session.IsCellAttemptable(cell);
            var isWinning = winningLine?.Contains(index) == true;
            var showLocked = hintMode && !attemptable;

            var background = cell.IsComplete
                ? (isWinning ? WinningColor : CompleteColor)
                : showLocked ? LockedColor : ImGui.GetStyle().Colors[(int)ImGuiCol.Button];
            var hover = cell.IsComplete
                ? CompleteHoverColor
                : showLocked ? LockedHoverColor : ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered];

            var buttonMin = ImGui.GetCursorScreenPos();
            bool clicked;
            using (ImRaii.PushColor(ImGuiCol.Button, background)
                       .Push(ImGuiCol.ButtonHovered, hover)
                       .Push(ImGuiCol.ButtonActive, hover))
            {
                clicked = ImGui.Button($"##bingo-cell-{index}", cellSize);
            }

            var hovered = ImGui.IsItemHovered();

            // Draw each wrapped line straight onto the draw list, centered, so it can't disturb the grid's SameLine layout.
            var label = definition?.DisplayName ?? cell.CheckId;
            var drawList = ImGui.GetWindowDrawList();
            var wrapWidth = cellSize.X - (padding.X * 2);
            var lines = WrapLabel(label, wrapWidth);
            var lineHeight = ImGui.GetTextLineHeight();
            var blockTop = buttonMin.Y + ((cellSize.Y - (lines.Count * lineHeight)) / 2f);
            var font = ImGui.GetFont();
            var fontSize = ImGui.GetFontSize();
            var textColor = ImGui.GetColorU32(ImGuiCol.Text);

            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                var line = lines[lineIndex];
                var lineWidth = ImGui.CalcTextSize(line).X;
                var linePos = new Vector2(buttonMin.X + ((cellSize.X - lineWidth) / 2f), blockTop + (lineIndex * lineHeight));
                drawList.AddText(font, fontSize, linePos, textColor, line);
            }

            if (clicked && !cell.IsComplete)
            {
                this.pendingOverrideIndex = index;
                this.openOverridePopup = true;
            }

            if (!hovered)
                continue;

            var rewardName = Unlocks.Definitions.First(unlock => unlock.Key == cell.Reward).DisplayName;
            var requirement = cell.RequiredUnlock == null
                ? "No unlock required"
                : $"Requires: {Unlocks.Definitions.First(unlock => unlock.Key == cell.RequiredUnlock.Value).DisplayName}" +
                  (hintMode ? attemptable ? " (granted)" : " (still locked)" : string.Empty);

            using var tooltip = ImRaii.Tooltip();
            ImGui.TextUnformatted(definition?.DisplayName ?? cell.CheckId);
            ImGui.Separator();
            ImGui.TextWrapped(definition?.Description ?? string.Empty);
            ImGui.TextUnformatted(requirement);
            ImGui.TextUnformatted($"Grants: {rewardName}");
            if (cell.IsComplete)
                ImGui.TextUnformatted(cell.ManualOverride ? "Completed (manual override)" : "Completed (auto-detected)");
            if (definition is { IsFullyAutomatic: false })
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

    /// <summary>Greedily word-wraps text to a pixel width using the current font's measurements.</summary>
    private static List<string> WrapLabel(string text, float wrapWidth)
    {
        var lines = new List<string>();
        var current = string.Empty;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (current.Length > 0 && ImGui.CalcTextSize(candidate).X > wrapWidth)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0)
            lines.Add(current);

        return lines.Count > 0 ? lines : [text];
    }
}
