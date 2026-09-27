using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ARandomizedReborn;

public sealed record CheckStatus(CheckDefinition Definition, bool IsComplete, bool WasManualOverride, bool IsOnBoard, bool IsAttemptable, bool HasEverTriggered);

public sealed class BingoSession : IDisposable
{
    private readonly Configuration configuration;
    private readonly Action<UnlockKey, bool> applyUnlock;
    private readonly Action<string> notify;
    private CancellationTokenSource? generationCancellation;

    public BingoSession(Configuration configuration, Action<UnlockKey, bool> applyUnlock, Action<string> notify)
    {
        this.configuration = configuration;
        this.applyUnlock = applyUnlock;
        this.notify = notify;
    }

    public const int MaxSecondChancePoints = 9;

    public IReadOnlyList<BingoCell> Cells => this.configuration.BingoBoard;

    public bool HasBoard => this.configuration.BingoBoard.Count == BingoBoard.CellCount;

    public bool IsGenerating { get; private set; }

    public BingoGenerationProgress? Progress { get; private set; }

    public string? GenerationFailure { get; private set; }

    public BingoDifficulty Difficulty => this.configuration.BingoDifficulty;

    public bool HasWon => this.configuration.BingoWon;

    public int SecondChancePoints => this.configuration.BingoSecondChancePoints;

    public int[]? WinningLine => this.HasBoard ? BingoBoard.FindCompletedLine(this.Cells) : null;

    public bool ReplaceIncompleteCell(int index)
    {
        if (this.configuration.BingoSecondChancePoints <= 0 || index < 0 || index >= this.configuration.BingoBoard.Count ||
            this.configuration.BingoBoard[index].IsComplete)
            return false;

        var used = this.configuration.BingoBoard.Select(cell => cell.CheckId).ToHashSet(StringComparer.Ordinal);
        var candidates = Checks.Definitions
            .Where(check => !used.Contains(check.Id) && !this.configuration.CompletedChecks.Contains(check.Id) &&
                            (check.RequiredUnlock == null || Unlocks.Get(this.configuration, check.RequiredUnlock.Value)))
            .ToList();
        if (candidates.Count == 0)
            return false;

        var replacement = candidates[Random.Shared.Next(candidates.Count)];
        var oldCheckId = this.configuration.BingoBoard[index].CheckId;
        this.configuration.CheckStepProgress.Remove(oldCheckId);
        this.configuration.BingoBoard[index].CheckId = replacement.Id;
        this.configuration.BingoBoard[index].IsComplete = false;
        this.configuration.BingoBoard[index].ManualOverride = false;
        this.configuration.BingoSecondChancePoints--;
        this.configuration.Save();
        return true;
    }

    public async Task ShuffleIncompleteAsync(BingoDifficulty difficulty, Func<Action, Task> runOnGameThread)
    {
        if (this.configuration.BingoSecondChancePoints < 2 || this.IsGenerating)
            return;

        var result = await BingoBoard.GenerateAsync(difficulty, Environment.TickCount ^ Guid.NewGuid().GetHashCode(), null).ConfigureAwait(false);
        await runOnGameThread(() =>
        {
            if (!result.Success)
                return;

            var completedChecks = this.configuration.BingoBoard.Where(cell => cell.IsComplete).Select(cell => cell.CheckId).ToHashSet(StringComparer.Ordinal);
            var used = completedChecks.ToHashSet(StringComparer.Ordinal);
            var replacements = result.Cells
                .Where(cell => !completedChecks.Contains(cell.CheckId) && !this.configuration.CompletedChecks.Contains(cell.CheckId) && used.Add(cell.CheckId))
                .ToList();
            var replacementIndex = 0;
            foreach (var cell in this.configuration.BingoBoard.Where(cell => !cell.IsComplete))
            {
                if (replacementIndex >= replacements.Count)
                    break;
                var oldCheckId = cell.CheckId;
                var replacement = replacements[replacementIndex++];
                this.configuration.CheckStepProgress.Remove(oldCheckId);
                cell.CheckId = replacement.CheckId;
                cell.Reward = replacement.Reward;
                cell.ManualOverride = false;
            }

            this.configuration.BingoDifficulty = difficulty;
            this.configuration.BingoSecondChancePoints -= 2;
            this.configuration.Save();
        }).ConfigureAwait(false);
    }

    public void Dispose()
    {
        this.generationCancellation?.Cancel();
        this.generationCancellation?.Dispose();
        this.generationCancellation = null;
    }

    public bool IsCellAttemptable(BingoCell cell)
        => cell.RequiredUnlock == null || Unlocks.Get(this.configuration, cell.RequiredUnlock.Value);

    public IReadOnlyList<CheckStatus> GetCheckStatuses()
    {
        var onBoard = this.configuration.BingoBoard
            .Select(cell => cell.CheckId)
            .ToHashSet(StringComparer.Ordinal);

        return Checks.Definitions.Select(definition => new CheckStatus(
            definition,
            this.configuration.CompletedChecks.Contains(definition.Id),
            this.configuration.ManuallyCompletedChecks.Contains(definition.Id),
            onBoard.Contains(definition.Id),
            definition.RequiredUnlock == null || Unlocks.Get(this.configuration, definition.RequiredUnlock.Value),
            this.configuration.EverTriggeredChecks.Contains(definition.Id)))
            .OrderBy(status => status.Definition.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<CheckDefinition> GetNextChecks(CheckProgressTracker progress, int count)
    {
        return this.Cells.Select((cell, index) => (cell, index))
            .Where(entry => !entry.cell.IsComplete && entry.cell.Definition != null && this.GetJournalPreference(entry.cell.CheckId) != 2)
            .OrderByDescending(entry => this.GetJournalPreference(entry.cell.CheckId) == 1)
            .ThenBy(entry => UnlockDepth(entry.cell, []))
            .ThenBy(entry => entry.cell.Definition!.Steps.Count(step => !progress.IsStepSatisfied(entry.cell.CheckId, step)))
            .ThenBy(entry => BingoBoard.Lines.Where(line => line.Contains(entry.index))
                .Min(line => line.Count(index => !this.Cells[index].IsComplete)))
            .ThenBy(entry => BingoBoard.Lines.Select((line, index) => (line, index))
                .Where(line => line.line.Contains(entry.index))
                .OrderBy(line => line.line.Count(index => !this.Cells[index].IsComplete))
                .First().index)
            .ThenBy(entry => entry.index)
            .ThenBy(entry => entry.cell.Definition!.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .Select(entry => entry.cell.Definition!)
            .ToArray();

        int UnlockDepth(BingoCell cell, HashSet<UnlockKey> visited)
        {
            if (cell.RequiredUnlock is not { } required || Unlocks.Get(this.configuration, required))
                return 0;

            if (!visited.Add(required))
                return int.MaxValue / 2;

            var prerequisite = this.Cells.FirstOrDefault(other => other.Reward == required);
            return prerequisite == null ? int.MaxValue / 2 : 1 + UnlockDepth(prerequisite, visited);
        }
    }

    /// <summary>0 = default sorting, 1 = prioritized in the task list, 2 = hidden from the task list.</summary>
    public int GetJournalPreference(string checkId)
        => this.configuration.JournalCheckPreferences.GetValueOrDefault(checkId);

    public void SetJournalPreference(string checkId, int preference)
    {
        if (preference is < 0 or > 2 || preference == this.GetJournalPreference(checkId))
            return;

        if (preference == 0)
            this.configuration.JournalCheckPreferences.Remove(checkId);
        else
            this.configuration.JournalCheckPreferences[checkId] = preference;
        this.configuration.Save();
    }

    /// <summary>Generates a new board off the main thread, then locks everything and applies it.</summary>
    public async Task StartNewSessionAsync(BingoDifficulty difficulty, Func<Action, Task> runOnGameThread, int? seed = null)
    {
        if (this.IsGenerating)
            return;

        this.generationCancellation?.Cancel();
        this.generationCancellation?.Dispose();
        this.generationCancellation = new CancellationTokenSource();

        this.IsGenerating = true;
        this.GenerationFailure = null;
        this.Progress = new BingoGenerationProgress("Starting", 0, 1);

        try
        {
            var progress = new Progress<BingoGenerationProgress>(value => this.Progress = value);
            var result = await BingoBoard.GenerateAsync(
                difficulty,
                seed ?? (Environment.TickCount ^ Guid.NewGuid().GetHashCode()),
                progress,
                this.generationCancellation.Token).ConfigureAwait(false);

            await runOnGameThread(() =>
            {
                if (!result.Success)
                {
                    this.GenerationFailure = result.Failure;
                    this.notify($"Board generation failed: {result.Failure}");
                    return;
                }

                this.ApplyBoard(difficulty, result.Cells);
                this.notify($"New {difficulty} board generated after {result.Attempts} attempt(s).");
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by another generation request.
        }
        finally
        {
            this.IsGenerating = false;
            this.Progress = null;
        }
    }

    public bool TryCompleteCheck(string checkId, bool manualOverride)
    {
        if (Checks.Find(checkId) == null)
            return false;

        var alreadyComplete = !this.configuration.CompletedChecks.Add(checkId);
        if (manualOverride)
            this.configuration.ManuallyCompletedChecks.Add(checkId);
        else if (!alreadyComplete)
            this.configuration.EverTriggeredChecks.Add(checkId);

        var cell = this.configuration.BingoBoard.FirstOrDefault(entry => entry.CheckId == checkId);
        if (cell == null)
        {
            if (!alreadyComplete)
                this.configuration.Save();

            return !alreadyComplete;
        }

        if (cell.IsComplete)
        {
            this.configuration.Save();
            return false;
        }

        cell.IsComplete = true;
        cell.ManualOverride = manualOverride;

        Unlocks.Set(this.configuration, cell.Reward, true);
        this.applyUnlock(cell.Reward, true);

        var unlockName = Unlocks.Definitions.First(definition => definition.Key == cell.Reward).DisplayName;
        this.notify($"{Checks.DisplayName(checkId)} cleared - {unlockName}");

        this.EvaluateWin();
        this.configuration.Save();
        return true;
    }

    public void ResetEverTriggered(string checkId)
    {
        if (this.configuration.EverTriggeredChecks.Remove(checkId))
            this.configuration.Save();
    }

    public void ResetProgressOnly()
    {
        foreach (var cell in this.configuration.BingoBoard)
        {
            cell.IsComplete = false;
            cell.ManualOverride = false;
        }

        this.configuration.CompletedChecks.Clear();
        this.configuration.ManuallyCompletedChecks.Clear();
        this.configuration.CheckStepProgress.Clear();
        this.configuration.BingoWon = false;
        this.LockEverything();
        this.configuration.Save();
    }

    private void ApplyBoard(BingoDifficulty difficulty, IReadOnlyList<BingoCell> cells)
    {
        this.configuration.BingoDifficulty = difficulty;
        this.configuration.BingoBoard = [.. cells];
        this.configuration.CompletedChecks.Clear();
        this.configuration.ManuallyCompletedChecks.Clear();
        this.configuration.CheckStepProgress.Clear();
        this.configuration.BingoWon = false;
        this.configuration.BingoSecondChancePoints = MaxSecondChancePoints;
        this.LockEverything();
        this.configuration.Save();
    }

    private void LockEverything()
    {
        foreach (var definition in Unlocks.Definitions)
        {
            Unlocks.Set(this.configuration, definition.Key, false);
            this.applyUnlock(definition.Key, false);
        }
    }

    private void EvaluateWin()
    {
        if (this.configuration.BingoWon)
            return;

        if (BingoBoard.FindCompletedLine(this.configuration.BingoBoard) == null)
            return;

        this.configuration.BingoWon = true;
        this.notify("BINGO! You completed a line. Tracking continues for the remaining squares.");
    }
}
