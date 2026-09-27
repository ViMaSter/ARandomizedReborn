namespace ARandomizedReborn.Tests.Tests;

// UiRestrictionManager hides whole windows while their unlock is missing.
[Order(4)]
public static class UiRestrictionTests
{
    [Test("arr-restrictions", RequiresTool = "eval_csharp")]
    public static async Task TheMapWindowStaysHiddenUntilTheMapUnlockIsGranted(TestContext context)
    {
        var arr = Arr.Of(context);
        await arr.SetUnlockAsync("Maps", true);
        await context.OpenByCommandAsync("AreaMap", "/map");

        await arr.SetUnlockAsync("Maps", false);
        Assert.True(await TestContext.WaitUntilAsync(async () => !await context.IsAddonVisibleAsync("AreaMap"), 4000), "the map is hidden while 'Maps' is locked");

        // Unlocking only stops the hiding; the window is still open behind the scenes and has to be reopened.
        await arr.SetUnlockAsync("Maps", true);
        await context.Client.CallAsync("close_addon", new { addon = "AreaMap" });
        await Task.Delay(500);
        await context.OpenByCommandAsync("AreaMap", "/map");
        Assert.Equal(true, await context.IsAddonVisibleAsync("AreaMap"), "the map opens again once 'Maps' is unlocked");
    }

    [Test("arr-restrictions", RequiresTool = "eval_csharp")]
    public static async Task MinimapIsHiddenUntilTheMapUnlockIsGranted(TestContext context)
        => await AssertHiddenWhileLockedAsync(context, "Maps", "_NaviMap");

    [Test("arr-restrictions", RequiresTool = "eval_csharp")]
    public static async Task EnemyCastBarsAreHiddenUntilTheirUnlockIsGranted(TestContext context)
        => await AssertHiddenWhileLockedAsync(context, "EnemyCastBars", "_EnemyList");

    [Test("arr-restrictions", RequiresTool = "eval_csharp")]
    public static async Task UnlockStateRoundTripsThroughTheBoard(TestContext context)
    {
        var arr = Arr.Of(context);
        await arr.NewSessionAsync();
        var state = await arr.StateAsync();
        Assert.True(state["unlocks"]!.AsObject().All(unlock => !unlock.Value!.GetValue<bool>()), "a new session locks every unlock");

        var cell = state["board"]!.AsArray().First(entry => entry!["attemptable"]!.GetValue<bool>())!;
        await arr.CompleteCheckAsync(cell["checkId"]!.GetValue<string>());
        var granted = (await arr.StateAsync())["unlocks"]!.AsObject().Where(unlock => unlock.Value!.GetValue<bool>()).Select(unlock => unlock.Key).ToList();
        Assert.Equal(cell["reward"]!.GetValue<string>(), string.Join(",", granted), "clearing a square grants exactly its reward");

        await arr.ResetCheckAsync(cell["checkId"]!.GetValue<string>());
        var after = await arr.StateAsync();
        Assert.True(after["unlocks"]!.AsObject().All(unlock => !unlock.Value!.GetValue<bool>()), "resetting the check takes the unlock back");
        Assert.Equal(false, after["board"]![cell["index"]!.GetValue<int>()]!["complete"]!.GetValue<bool>(), "the square is incomplete again");
    }

    private static async Task AssertHiddenWhileLockedAsync(TestContext context, string unlock, string addon)
    {
        var arr = Arr.Of(context);
        await arr.SetUnlockAsync(unlock, true);
        if (!await TestContext.WaitUntilAsync(() => context.IsAddonVisibleAsync(addon), 3000))
            throw new SkipException($"{addon} is not shown in your HUD setup.");

        await arr.SetUnlockAsync(unlock, false);
        Assert.True(await TestContext.WaitUntilAsync(async () => !await context.IsAddonVisibleAsync(addon), 4000), $"{addon} is hidden while '{unlock}' is locked");

        await arr.SetUnlockAsync(unlock, true);
        Assert.True(await TestContext.WaitUntilAsync(() => context.IsAddonVisibleAsync(addon), 4000), $"{addon} comes back once '{unlock}' is unlocked");
    }
}
