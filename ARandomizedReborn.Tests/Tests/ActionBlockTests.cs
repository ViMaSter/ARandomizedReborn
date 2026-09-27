namespace ARandomizedReborn.Tests.Tests;

// SprintBlocker tints the hotbar slots of actions that are still locked.
[Order(8)]
public static class ActionBlockTests
{
    private static readonly string[] ActionBars = ["_ActionBar", .. Enumerable.Range(1, 9).Select(index => $"_ActionBar{index:00}")];
    private static readonly string[] BlockedUnlocks = ["Sprint", "TeleportReturn", "Mounts"];

    [Test("arr-actions", RequiresTool = "eval_csharp")]
    public static async Task LockedActionsAreTintedOnTheHotbar(TestContext context)
    {
        var arr = Arr.Of(context);
        foreach (var unlock in BlockedUnlocks)
            await arr.SetUnlockAsync(unlock, false);

        var highlighted = 0;
        if (!await TestContext.WaitUntilAsync(async () => (highlighted = await HighlightedIconsAsync(context)) > 0, 4000))
            throw new SkipException("No locked action (sprint, teleport, return, mount) is on a visible hotbar.");

        foreach (var unlock in BlockedUnlocks)
            await arr.SetUnlockAsync(unlock, true);
        Assert.True(
            await TestContext.WaitUntilAsync(async () => await HighlightedIconsAsync(context) < highlighted, 4000),
            "unlocking the actions removes their hotbar tint");

        foreach (var unlock in BlockedUnlocks)
            await arr.SetUnlockAsync(unlock, false);
        Assert.True(
            await TestContext.WaitUntilAsync(async () => await HighlightedIconsAsync(context) == highlighted, 4000),
            "locking them again tints the same slots");
    }

    // SprintBlocker.SetIconHighlight dims locked slots to multiply 30 and adds red.
    private static async Task<int> HighlightedIconsAsync(TestContext context)
    {
        var count = 0;
        foreach (var bar in ActionBars)
        {
            if (!await context.IsAddonVisibleAsync(bar))
                continue;
            var icons = (await context.Client.CallOkAsync("find_nodes", new { addon = bar, type = "Component:Icon", visibleOnly = true, limit = 20 })).AsArray();
            foreach (var icon in icons)
            {
                var node = await context.NodeAsync(bar, icon!["path"]!.GetValue<string>());
                if (node["multiply"]!.ToJsonString() == "[30,30,30]")
                    count++;
            }
        }

        return count;
    }
}
