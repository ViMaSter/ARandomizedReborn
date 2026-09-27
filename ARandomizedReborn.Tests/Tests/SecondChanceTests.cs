using System.Text.Json.Nodes;

namespace ARandomizedReborn.Tests.Tests;

// Second Chance Points: what the window promises, what it costs, and what happens when you cannot pay.
[Order(2)]
public static class SecondChanceTests
{
    [Test("arr-second-chance", RequiresTool = "eval_csharp")]
    public static async Task WindowShowsThePointBalanceAndWhatEachButtonCosts(TestContext context)
    {
        var arr = Arr.Of(context);
        await arr.SetPointsAsync(5);
        await BingoUi.OpenBonusInfoAsync(context);

        Assert.Equal("Second Chance Points: 5/9", await BingoUi.TextAsync(context, BingoUi.PointsNode, BingoUi.BonusInfo), "point balance");
        Assert.Equal("Change one Bingo Square (1 Point)", await BingoUi.TextAsync(context, "4>2", BingoUi.BonusInfo), "change-one title");
        Assert.Contains(await BingoUi.TextAsync(context, "4>3", BingoUi.BonusInfo), "Click an incomplete Bingo Square to replace it.", "change-one description");
        Assert.Equal("Shuffle incomplete Bingo Squares (2 Points)", await BingoUi.TextAsync(context, "5>2", BingoUi.BonusInfo), "shuffle title");
        Assert.Contains(await BingoUi.TextAsync(context, "5>3", BingoUi.BonusInfo), "Replace all incomplete Bingo Squares while keeping", "shuffle description");

        // The window is rewritten every frame, so a balance change shows up without reopening it.
        await arr.SetPointsAsync(2);
        Assert.True(
            await TestContext.WaitUntilAsync(async () => await BingoUi.TextAsync(context, BingoUi.PointsNode, BingoUi.BonusInfo) == "Second Chance Points: 2/9", 2000),
            "the balance follows the current points");
    }

    [Test("arr-second-chance", RequiresTool = "eval_csharp")]
    public static async Task WithoutPointsBothActionsWarnAndChangeNothing(TestContext context)
    {
        var arr = Arr.Of(context);
        await arr.SetPointsAsync(0);
        await BingoUi.OpenBonusInfoAsync(context);
        var before = (await arr.StateAsync())["board"]!.ToJsonString();

        var mark = await arr.ToastMarkAsync();
        await BingoUi.ClickNodeAsync(context, BingoUi.BonusInfo, BingoUi.ChangeOneNodeId);
        await arr.AssertToastAsync(mark, "Not enough Second Chance Points (1 needed).", "change one without points");
        Assert.Contains(await BingoUi.TextAsync(context, BingoUi.MessageNode), "Complete a square", "the board did not enter square selection");

        mark = await arr.ToastMarkAsync();
        await BingoUi.ClickNodeAsync(context, BingoUi.BonusInfo, BingoUi.ShuffleNodeId);
        await arr.AssertToastAsync(mark, "Not enough Second Chance Points (2 needed).", "shuffle without points");
        Assert.Equal(false, await context.IsAddonVisibleAsync(BingoUi.Confirm), "no confirmation is asked for an action you cannot pay for");

        var after = await arr.StateAsync();
        Assert.Equal(0, after["points"]!.GetValue<int>(), "points stay at zero");
        Assert.Equal(before, after["board"]!.ToJsonString(), "the board is untouched");
    }

    [Test("arr-second-chance", RequiresTool = "eval_csharp")]
    public static async Task ChangeOneReplacesTheSelectedSquareForOnePoint(TestContext context)
    {
        var arr = Arr.Of(context);
        await arr.NewSessionAsync();
        await arr.SetPointsAsync(5);
        await BingoUi.OpenBonusInfoAsync(context);
        var before = (await arr.StateAsync())["board"]!.AsArray();
        var target = before.First(cell => !cell!["complete"]!.GetValue<bool>())!;
        var index = target["index"]!.GetValue<int>();

        await BingoUi.ClickNodeAsync(context, BingoUi.BonusInfo, BingoUi.ChangeOneNodeId);
        Assert.True(
            await TestContext.WaitUntilAsync(async () => (await BingoUi.TextAsync(context, BingoUi.MessageNode)).Contains("Click an incomplete square", StringComparison.Ordinal), 3000),
            "the board asks for a square to replace");

        await BingoUi.ClickPluginButtonAsync(context, BingoUi.DutyNode(index));
        Assert.True(
            await TestContext.WaitUntilAsync(async () => (await arr.StateAsync())["board"]![index]!["checkId"]!.GetValue<string>() != target["checkId"]!.GetValue<string>(), 3000),
            "the selected square is replaced");

        var after = await arr.StateAsync();
        Assert.Equal(4, after["points"]!.GetValue<int>(), "replacing one square costs one point");
        Assert.Equal(false, after["board"]![index]!["complete"]!.GetValue<bool>(), "the new square starts incomplete");
        Assert.Equal(
            string.Join(",", before.Where(cell => cell!["index"]!.GetValue<int>() != index).Select(cell => cell!["checkId"]!.GetValue<string>())),
            string.Join(",", after["board"]!.AsArray().Where(cell => cell!["index"]!.GetValue<int>() != index).Select(cell => cell!["checkId"]!.GetValue<string>())),
            "no other square changes");
        Assert.Contains(await BingoUi.TextAsync(context, BingoUi.MessageNode), "Complete a square", "the board leaves square selection");
    }

    [Test("arr-second-chance", RequiresTool = "eval_csharp")]
    public static async Task CancellingSquareSelectionCostsNothing(TestContext context)
    {
        var arr = Arr.Of(context);
        await arr.SetPointsAsync(3);
        await BingoUi.OpenBonusInfoAsync(context);
        var before = (await arr.StateAsync())["board"]!.ToJsonString();

        await BingoUi.ClickNodeAsync(context, BingoUi.BonusInfo, BingoUi.ChangeOneNodeId);
        Assert.True(
            await TestContext.WaitUntilAsync(async () => (await BingoUi.TextAsync(context, BingoUi.MessageNode)).Contains("Click an incomplete square", StringComparison.Ordinal), 3000),
            "square selection started");
        await BingoUi.ClickNodeAsync(context, BingoUi.Board, BingoUi.CancelSelectionNodeId);

        var after = await arr.StateAsync();
        Assert.Equal(3, after["points"]!.GetValue<int>(), "cancelling keeps the points");
        Assert.Equal(before, after["board"]!.ToJsonString(), "cancelling keeps the board");
    }

    [Test("arr-second-chance", RequiresTool = "eval_csharp")]
    public static async Task ShuffleAsksBeforeSpendingTwoPointsAndKeepsClearedSquares(TestContext context)
    {
        var arr = Arr.Of(context);
        await arr.NewSessionAsync();
        await arr.SetPointsAsync(4);
        var cleared = (await arr.StateAsync())["board"]!.AsArray().First(cell => cell!["attemptable"]!.GetValue<bool>())!["checkId"]!.GetValue<string>();
        await arr.CompleteCheckAsync(cleared);
        await BingoUi.OpenBonusInfoAsync(context);
        var before = (await arr.StateAsync())["board"]!.AsArray().Select(cell => cell!["checkId"]!.GetValue<string>()).ToList();

        await BingoUi.ClickNodeAsync(context, BingoUi.BonusInfo, BingoUi.ShuffleNodeId);
        var question = await BingoUi.ConfirmTextAsync(context);
        Assert.Contains(question, "costs 2 Second Chance Points (4 left)", "the dialog states the price and the balance");
        await BingoUi.AnswerConfirmAsync(context, yes: false);
        Assert.Equal(4, (await arr.StateAsync())["points"]!.GetValue<int>(), "declining costs nothing");

        await BingoUi.OpenBonusInfoAsync(context);
        await BingoUi.ClickNodeAsync(context, BingoUi.BonusInfo, BingoUi.ShuffleNodeId);
        await BingoUi.AnswerConfirmAsync(context, yes: true);
        Assert.True(
            await TestContext.WaitUntilAsync(async () => (await arr.StateAsync())["points"]!.GetValue<int>() == 2, 8000),
            "shuffling costs two points");

        var after = (await arr.StateAsync())["board"]!.AsArray();
        var clearedCell = after.First(cell => cell!["complete"]!.GetValue<bool>())!;
        Assert.Equal(cleared, clearedCell["checkId"]!.GetValue<string>(), "the cleared square survives the shuffle");
        var unchanged = after.Count(cell => cell!["checkId"]!.GetValue<string>() == before[cell["index"]!.GetValue<int>()]);
        Assert.True(unchanged < before.Count, "incomplete squares are replaced");
    }
}
