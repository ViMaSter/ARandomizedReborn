using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ARandomizedReborn;

public sealed record CheckStatus(CheckDefinition Definition, bool IsComplete, bool WasManualOverride, bool IsOnBoard, bool IsAttemptable);

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

    public IReadOnlyList<BingoCell> Cells => this.configuration.BingoBoard;

    public bool HasBoard => this.configuration.BingoBoard.Count == BingoBoard.CellCount;

    public bool IsGenerating { get; private set; }

    public BingoGenerationProgress? Progress { get; private set; }

    public string? GenerationFailure { get; private set; }

    public BingoDifficulty Difficulty => this.configuration.BingoDifficulty;

    public bool HasWon => this.configuration.BingoWon;

    public int[]? WinningLine => this.HasBoard ? BingoBoard.FindCompletedLine(this.Cells) : null;

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
            definition.RequiredUnlock == null || Unlocks.Get(this.configuration, definition.RequiredUnlock.Value)))
            .ToArray();
    }

    /// <summary>Generates a new board off the main thread, then locks everything and applies it.</summary>
    public async Task StartNewSessionAsync(BingoDifficulty difficulty, Func<Action, Task> runOnGameThread)
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
                Environment.TickCount ^ Guid.NewGuid().GetHashCode(),
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
