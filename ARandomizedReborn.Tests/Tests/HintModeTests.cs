namespace ARandomizedReborn.Tests.Tests;

// Hint mode tints squares whose unlock is still missing and spells the requirement out in the tooltip.
[Order(3)]
public static class HintModeTests
{
    private static readonly (int R, int G, int B) LockedTint = (100, 45, 45);
    private static readonly (int R, int G, int B) OpenTint = (100, 100, 100);

    [Test("arr-hint-mode", RequiresTool = "eval_csharp")]
    public static async Task LockedSquaresAreTintedOnlyWhileHintModeIsOn(TestContext context)
    {
        var arr = Arr.Of(context);
        await arr.NewSessionAsync();
        await arr.SetHintModeAsync(false);
        await BingoUi.OpenBoardAsync(context);
        var board = (await arr.StateAsync())["board"]!.AsArray();
        var locked = board.Where(cell => !cell!["attemptable"]!.GetValue<bool>()).Select(cell => cell!["index"]!.GetValue<int>()).ToList();
        if (locked.Count == 0)
            throw new SkipException("This board has no locked squares.");
        var open = board.First(cell => cell!["attemptable"]!.GetValue<bool>() && !cell["complete"]!.GetValue<bool>())!["index"]!.GetValue<int>();

        var off = await BingoUi.CellTintsAsync(context);
        Assert.True(locked.All(index => off[index] == OpenTint), $"hint mode off leaves locked squares untinted, got {off[locked[0]]}");
        // Only the square itself is compared; the rest of the book animates.
        var region = await context.NodeRegionAsync(BingoUi.Board, BingoUi.DutyNode(locked[0]));
        await context.AssertNotCoveredAsync(region, BingoUi.Board);
        var (baseline, noise) = await context.RegionBaselineAsync(region);

        await arr.SetHintModeAsync(true);
        Assert.True(
            await TestContext.WaitUntilAsync(async () => (await BingoUi.CellTintsAsync(context))[locked[0]] == LockedTint, 3000),
            "hint mode tints a locked square red");
        var on = await BingoUi.CellTintsAsync(context);
        Assert.True(locked.All(index => on[index] == LockedTint), "every locked square is tinted");
        Assert.Equal(OpenTint, on[open], "squares you can attempt keep their normal tint");
        TestContext.AssertVisiblyChanged(baseline, await context.ScreenRegionAsync(region, "hint-mode-on"), noise, "hint mode");

        await arr.SetHintModeAsync(false);
        Assert.True(
            await TestContext.WaitUntilAsync(async () => (await BingoUi.CellTintsAsync(context))[locked[0]] == OpenTint, 3000),
            "turning hint mode off restores the tint");
    }

    [Test("arr-hint-mode", RequiresTool = "eval_csharp")]
    public static async Task ClearedSquaresAreDimmedAndStickered(TestContext context)
    {
        var arr = Arr.Of(context);
        await arr.NewSessionAsync();
        await BingoUi.OpenBoardAsync(context);
        var cell = (await arr.StateAsync())["board"]!.AsArray().First(entry => entry!["attemptable"]!.GetValue<bool>())!;
        var index = cell["index"]!.GetValue<int>();

        await arr.CompleteCheckAsync(cell["checkId"]!.GetValue<string>());
        Assert.True(
            await TestContext.WaitUntilAsync(async () => (await BingoUi.CellTintsAsync(context))[index] == (65, 65, 65), 3000),
            "a cleared square is dimmed");
        Assert.Equal("1", await BingoUi.TextAsync(context, BingoUi.StickerCountNode), "the sticker counter follows the cleared squares");
        Assert.Equal(true, (await arr.StateAsync())["unlocks"]![cell["reward"]!.GetValue<string>()]!.GetValue<bool>(), "clearing a square grants its unlock");
    }
}
