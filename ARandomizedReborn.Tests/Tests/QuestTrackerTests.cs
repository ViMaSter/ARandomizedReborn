namespace ARandomizedReborn.Tests.Tests;

// QuestTrackerManager writes the current checks into the game's own quest tracker.
[Order(5)]
public static class QuestTrackerTests
{
    private const string Tracker = "_ToDoList";

    [Test("arr-tracker", RequiresTool = "eval_csharp")]
    public static async Task TrackerListsTheCurrentBoardChecks(TestContext context)
    {
        var arr = Arr.Of(context);
        await arr.NewSessionAsync();
        if (!await context.IsAddonVisibleAsync(Tracker))
            throw new SkipException("The quest tracker is not shown in your HUD setup.");

        var names = (await arr.StateAsync())["board"]!.AsArray().Select(cell => cell!["name"]!.GetValue<string>()).ToHashSet();
        IReadOnlyList<string> tracked = [];
        Assert.True(
            await TestContext.WaitUntilAsync(async () => (tracked = await TrackedNamesAsync(context, names)).Count > 0, 5000),
            "the tracker shows at least one board check");
        Assert.True(tracked.Count <= 5, $"the tracker shows at most five checks, got {tracked.Count}");

        var shown = tracked[0];
        var checkId = (await arr.StateAsync())["board"]!.AsArray().First(cell => cell!["name"]!.GetValue<string>() == shown)!["checkId"]!.GetValue<string>();
        await arr.CompleteCheckAsync(checkId);
        Assert.True(
            await TestContext.WaitUntilAsync(async () => !(await TrackedNamesAsync(context, names)).Contains(shown), 5000),
            "a cleared check leaves the tracker");
    }

    [Test("arr-tracker", RequiresTool = "eval_csharp")]
    public static async Task HiddenChecksAreKeptOutOfTheTracker(TestContext context)
    {
        var arr = Arr.Of(context);
        await arr.NewSessionAsync();
        if (!await context.IsAddonVisibleAsync(Tracker))
            throw new SkipException("The quest tracker is not shown in your HUD setup.");

        var names = (await arr.StateAsync())["board"]!.AsArray().Select(cell => cell!["name"]!.GetValue<string>()).ToHashSet();
        IReadOnlyList<string> tracked = [];
        if (!await TestContext.WaitUntilAsync(async () => (tracked = await TrackedNamesAsync(context, names)).Count > 0, 5000))
            throw new SkipException("The tracker shows no board check to hide.");

        var shown = tracked[0];
        var checkId = (await arr.StateAsync())["board"]!.AsArray().First(cell => cell!["name"]!.GetValue<string>() == shown)!["checkId"]!.GetValue<string>();
        await arr.CallAsync("set_journal_preference", new { checkId, preference = 2 });
        Assert.True(
            await TestContext.WaitUntilAsync(async () => !(await TrackedNamesAsync(context, names)).Contains(shown), 5000),
            "a hidden check leaves the tracker");

        // Prioritising is the one preference that guarantees a slot again; plain "default" competes with the other squares.
        await arr.CallAsync("set_journal_preference", new { checkId, preference = 1 });
        Assert.True(
            await TestContext.WaitUntilAsync(async () => (await TrackedNamesAsync(context, names)).Contains(shown), 5000),
            "a prioritised check is tracked again");

        await arr.CallAsync("set_journal_preference", new { checkId, preference = 0 });
        Assert.Equal(null, (await arr.StateAsync())["journalPreferences"]![checkId], "the default preference is not stored");
    }

    private static async Task<IReadOnlyList<string>> TrackedNamesAsync(TestContext context, IReadOnlySet<string> boardNames)
    {
        var nodes = (await context.Client.CallOkAsync("find_nodes", new { addon = Tracker, type = "Text", visibleOnly = true, limit = 100 })).AsArray();
        return [.. nodes.Select(node => node!["text"]?.GetValue<string>() ?? string.Empty).Where(boardNames.Contains).Distinct()];
    }
}
