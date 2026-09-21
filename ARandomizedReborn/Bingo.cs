using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ARandomizedReborn;

public enum BingoDifficulty
{
    Easy,
    Medium,
    Hard,
}

/// <summary>A single square of the board: a check to complete and the unlock it grants.</summary>
[Serializable]
public sealed class BingoCell
{
    public string CheckId { get; set; } = string.Empty;
    public UnlockKey Reward { get; set; }
    public bool IsComplete { get; set; }
    public bool ManualOverride { get; set; }

    public CheckDefinition? Definition => Checks.Find(this.CheckId);

    public UnlockKey? RequiredUnlock => this.Definition?.RequiredUnlock;
}

public sealed record BingoGenerationProgress(string Phase, int Attempt, int MaxAttempts)
{
    public float Fraction => this.MaxAttempts <= 0 ? 0f : Math.Clamp(this.Attempt / (float)this.MaxAttempts, 0f, 1f);
}

public sealed record BingoGenerationResult(bool Success, IReadOnlyList<BingoCell> Cells, int Attempts, string? Failure);

public static class BingoBoard
{
    public const int Size = 4;
    public const int CellCount = Size * Size;

    private const int MaxLayoutShuffles = 500;

    public static IReadOnlyList<int[]> Lines { get; } = BuildLines();

    public static int FreeCheckTarget(BingoDifficulty difficulty)
        => difficulty switch
        {
            BingoDifficulty.Easy => 8,
            BingoDifficulty.Medium => 4,
            BingoDifficulty.Hard => 1,
            _ => 4,
        };

    /// <summary>
    /// Builds a board that is guaranteed to be logically completable: cells are picked in a valid
    /// completion order, so every gated check has its required unlock handed out by an earlier cell.
    /// Placement is then shuffled until every row, column and diagonal contains at least one gated check.
    /// </summary>
    public static Task<BingoGenerationResult> GenerateAsync(
        BingoDifficulty difficulty,
        int seed,
        IProgress<BingoGenerationProgress>? progress,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Generate(difficulty, seed, progress, cancellationToken), cancellationToken);

    public static bool IsLineComplete(IReadOnlyList<BingoCell> cells, int[] line)
        => line.All(index => cells[index].IsComplete);

    public static int[]? FindCompletedLine(IReadOnlyList<BingoCell> cells)
    {
        if (cells.Count != CellCount)
            return null;

        return Lines.FirstOrDefault(line => IsLineComplete(cells, line));
    }

    private static BingoGenerationResult Generate(
        BingoDifficulty difficulty,
        int seed,
        IProgress<BingoGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 2000;

        if (Unlocks.Definitions.Count != CellCount)
        {
            return new BingoGenerationResult(
                false,
                [],
                0,
                $"Expected exactly {CellCount} unlocks but found {Unlocks.Definitions.Count}.");
        }

        var freeTarget = FreeCheckTarget(difficulty);
        string? lastFailure = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt % 25 == 1)
                progress?.Report(new BingoGenerationProgress("Building a solvable chain", attempt, maxAttempts));

            var random = new Random(HashCode.Combine(seed, attempt));
            var ordered = TryBuildChain(random, freeTarget, out var chainFailure);
            if (ordered == null)
            {
                lastFailure = chainFailure;
                continue;
            }

            var layout = TryLayout(random, ordered, cancellationToken);
            if (layout == null)
            {
                lastFailure = "Could not place the checks so that every line contains a gated check.";
                continue;
            }

            progress?.Report(new BingoGenerationProgress("Verifying board logic", maxAttempts, maxAttempts));

            if (!IsSolvable(layout))
            {
                lastFailure = "Generated board failed the solvability verification.";
                continue;
            }

            return new BingoGenerationResult(true, layout, attempt, null);
        }

        return new BingoGenerationResult(false, [], maxAttempts, lastFailure ?? "Board generation failed.");
    }

    private static List<BingoCell>? TryBuildChain(Random random, int freeTarget, out string? failure)
    {
        failure = null;

        var availableUnlocks = Unlocks.Definitions.Select(definition => definition.Key).ToList();
        var granted = new HashSet<UnlockKey>();
        var usedChecks = new HashSet<string>(StringComparer.Ordinal);
        var chain = new List<BingoCell>(CellCount);
        var remainingFree = freeTarget;

        for (var index = 0; index < CellCount; index++)
        {
            var remainingCells = CellCount - index;

            var freeCandidates = Checks.FreeChecks.Where(check => !usedChecks.Contains(check.Id)).ToList();
            var gatedCandidates = Checks.GatedChecks
                .Where(check => !usedChecks.Contains(check.Id) && granted.Contains(check.RequiredUnlock!.Value))
                .ToList();

            var mustPickFree = remainingFree >= remainingCells || gatedCandidates.Count == 0;
            var canPickFree = remainingFree > 0 && freeCandidates.Count > 0;

            bool pickFree;
            if (mustPickFree)
            {
                if (!canPickFree)
                {
                    failure = "Ran out of usable checks while building the unlock chain.";
                    return null;
                }

                pickFree = true;
            }
            else
            {
                pickFree = canPickFree && random.NextDouble() < remainingFree / (double)remainingCells;
            }

            var pool = pickFree ? freeCandidates : gatedCandidates;
            var check = pool[random.Next(pool.Count)];
            usedChecks.Add(check.Id);
            if (pickFree)
                remainingFree--;

            var reward = PickReward(random, availableUnlocks, usedChecks, remainingCells - 1, remainingFree);
            availableUnlocks.Remove(reward);
            granted.Add(reward);

            chain.Add(new BingoCell { CheckId = check.Id, Reward = reward });
        }

        if (remainingFree != 0)
        {
            failure = "Could not hit the requested amount of requirement-free checks.";
            return null;
        }

        return chain;
    }

    /// <summary>Prefers handing out an unlock that actually opens up unused checks, so the chain keeps going.</summary>
    private static UnlockKey PickReward(
        Random random,
        List<UnlockKey> availableUnlocks,
        HashSet<string> usedChecks,
        int remainingCells,
        int remainingFree)
    {
        var needsGatedCells = remainingCells > remainingFree;
        if (needsGatedCells)
        {
            var productive = availableUnlocks
                .Where(unlock => Checks.GatedChecks.Any(check =>
                    check.RequiredUnlock == unlock && !usedChecks.Contains(check.Id)))
                .ToList();

            if (productive.Count > 0)
                return productive[random.Next(productive.Count)];
        }

        return availableUnlocks[random.Next(availableUnlocks.Count)];
    }

    private static List<BingoCell>? TryLayout(Random random, List<BingoCell> chain, CancellationToken cancellationToken)
    {
        var positions = Enumerable.Range(0, CellCount).ToArray();

        for (var shuffle = 0; shuffle < MaxLayoutShuffles; shuffle++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Shuffle(random, positions);

            var layout = new BingoCell[CellCount];
            for (var index = 0; index < CellCount; index++)
                layout[positions[index]] = chain[index];

            if (Lines.All(line => line.Any(index => layout[index].RequiredUnlock != null)))
                return [.. layout];
        }

        return null;
    }

    /// <summary>Replays the board from a fully locked state to prove every cell is reachable.</summary>
    private static bool IsSolvable(IReadOnlyList<BingoCell> cells)
    {
        var granted = new HashSet<UnlockKey>();
        var pending = cells.ToList();

        while (pending.Count > 0)
        {
            var doable = pending
                .Where(cell => cell.RequiredUnlock == null || granted.Contains(cell.RequiredUnlock.Value))
                .ToList();

            if (doable.Count == 0)
                return false;

            foreach (var cell in doable)
            {
                granted.Add(cell.Reward);
                pending.Remove(cell);
            }
        }

        return true;
    }

    private static void Shuffle(Random random, int[] values)
    {
        for (var index = values.Length - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (values[index], values[swap]) = (values[swap], values[index]);
        }
    }

    private static List<int[]> BuildLines()
    {
        var lines = new List<int[]>();

        for (var row = 0; row < Size; row++)
            lines.Add(Enumerable.Range(0, Size).Select(column => (row * Size) + column).ToArray());

        for (var column = 0; column < Size; column++)
            lines.Add(Enumerable.Range(0, Size).Select(row => (row * Size) + column).ToArray());

        lines.Add(Enumerable.Range(0, Size).Select(index => (index * Size) + index).ToArray());
        lines.Add(Enumerable.Range(0, Size).Select(index => (index * Size) + (Size - 1 - index)).ToArray());

        return lines;
    }
}
