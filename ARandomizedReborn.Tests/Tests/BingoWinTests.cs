namespace ARandomizedReborn.Tests.Tests;

// Completing a row, column or diagonal wins the board and is announced on it.
[Order(7)]
public static class BingoWinTests
{
    [Test("arr-win", RequiresTool = "eval_csharp")]
    public static async Task CompletingALineWinsTheBoardAndAnnouncesIt(TestContext context)
    {
        var arr = Arr.Of(context);
        await arr.NewSessionAsync();
        await BingoUi.OpenBoardAsync(context);
        Assert.Equal(false, (await arr.StateAsync())["won"]!.GetValue<bool>(), "a new board is not won");
        var (baseline, noise) = await context.BaselineAsync(BingoUi.Board);

        var mark = await arr.ToastMarkAsync();
        await arr.CompleteLineAsync(0);
        var state = await arr.StateAsync();
        Assert.Equal(true, state["won"]!.GetValue<bool>(), "completing a line wins the board");
        Assert.Equal(4, state["winningLine"]!.AsArray().Count, "the winning line has four squares");
        await arr.AssertToastAsync(mark, "BINGO", "win announcement");

        Assert.True(
            await TestContext.WaitUntilAsync(async () => (await BingoUi.TextAsync(context, BingoUi.MessageNode)).Contains("BINGO", StringComparison.Ordinal), 4000),
            "the board message announces the win");
        // Clearing a square hands out its unlock, which can let the tracker clear further squares on its own.
        var cleared = state["board"]!.AsArray().Count(cell => cell!["complete"]!.GetValue<bool>());
        Assert.True(cleared >= 4, "at least the line's four squares are cleared");
        Assert.Equal(cleared.ToString(), await BingoUi.TextAsync(context, BingoUi.StickerCountNode), "the sticker counter matches the cleared squares");
        Assert.Equal($"Medium  -  Cleared {cleared}/16", await BingoUi.TextAsync(context, BingoUi.DeadlineNode), "the header counts the cleared squares");
        TestContext.AssertVisiblyChanged(baseline, await context.ScreenshotAsync(BingoUi.Board, "bingo"), noise, "bingo");

        await arr.NewSessionAsync();
        Assert.Equal(false, (await arr.StateAsync())["won"]!.GetValue<bool>(), "a new session clears the win");
        Assert.True(
            await TestContext.WaitUntilAsync(async () => await BingoUi.TextAsync(context, BingoUi.StickerCountNode) == "0", 4000),
            "a new session resets the cleared counter");
    }
}
