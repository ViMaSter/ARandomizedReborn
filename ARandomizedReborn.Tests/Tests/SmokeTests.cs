namespace ARandomizedReborn.Tests.Tests;

[Order(1)]
public static class SmokeTests
{
    [Test("arr-smoke", RequiresTool = "eval_csharp")]
    public static async Task TestControlReportsSessionState(TestContext context)
    {
        var state = await Arr.Of(context).StateAsync();
        Assert.Equal(true, state["randomizer"]!.GetValue<bool>(), "randomizer enabled by the fixture");
        Assert.Equal(true, state["hasBoard"]!.GetValue<bool>(), "board generated");
        Assert.Equal(16, state["board"]!.AsArray().Count, "board size");
        Assert.Equal(9, state["points"]!.GetValue<int>(), "a fresh session starts with all second chance points");
        Assert.True(state["unlocks"]!.AsObject().All(unlock => !unlock.Value!.GetValue<bool>()), "a fresh session locks everything");
    }

    [Test("arr-smoke", RequiresTool = "eval_csharp")]
    public static async Task BoardShowsTheGeneratedChecks(TestContext context)
    {
        var arr = Arr.Of(context);
        await BingoUi.OpenBoardAsync(context);
        var state = await arr.StateAsync();
        var completed = state["board"]!.AsArray().Count(cell => cell!["complete"]!.GetValue<bool>());

        Assert.Equal("A Randomized Reborn", await BingoUi.TextAsync(context, BingoUi.WindowTitleNode), "window title");
        Assert.Equal($"{state["difficulty"]}  -  Cleared {completed}/16", await BingoUi.TextAsync(context, BingoUi.DeadlineNode), "deadline line shows difficulty and progress");
        Assert.Equal(completed.ToString(), await BingoUi.TextAsync(context, BingoUi.StickerCountNode), "sticker count");
        Assert.Equal("/16", await BingoUi.TextAsync(context, BingoUi.StickerMaxNode), "sticker maximum");
        Assert.Contains(await BingoUi.TextAsync(context, BingoUi.MessageNode), "Complete a square", "board message");

        var catalog = (await arr.CallAsync("checks")).AsArray().ToDictionary(check => check!["id"]!.GetValue<string>(), check => check!["icon"]!.GetValue<uint>());
        var expected = state["board"]!.AsArray().Select(cell => catalog[cell!["checkId"]!.GetValue<string>()]).ToList();
        Assert.Equal(string.Join(",", expected), string.Join(",", await BingoUi.CellIconsAsync(context)), "every square shows its check's journal icon");
    }
}
