using System.Text.Json.Nodes;

namespace ARandomizedReborn.Tests;

// The native windows BingoManager takes over, addressed the way a player reaches them.
public static class BingoUi
{
    public const string Board = "WeeklyBingo";
    public const string BonusInfo = "WeeklyBingoBonusInfo";
    public const string Confirm = "SelectYesno";
    public const string DeadlineNode = "8";
    public const string MessageNode = "34";
    public const string StickerCountNode = "60";
    public const string StickerMaxNode = "61";
    public const string WindowTitleNode = "129>3";
    public const uint SecondChanceButtonNodeId = 33;
    public const uint CancelSelectionNodeId = 28;
    public const uint ChangeOneNodeId = 4;
    public const uint ShuffleNodeId = 5;
    public const string PointsNode = "3";

    // The 16 duty buttons sit under the duty container as consecutive node ids, in board order.
    public static string DutyNode(int index) => (12 + index).ToString();

    public static async Task OpenBoardAsync(TestContext context)
    {
        if (await context.IsAddonVisibleAsync(Board))
            return;
        await Arr.Of(context).CallAsync("open_board");
        if (!await TestContext.WaitUntilAsync(() => context.IsAddonVisibleAsync(Board), 6000))
            throw new SkipException("The bingo board did not open; carry a Wondrous Tails journal in your key items.");
        context.TrackOpenedAddon(Board);
        await Task.Delay(700);
    }

    // The 16 duty slots in board order, as the board renders them.
    public static async Task<string> TextAsync(TestContext context, string node, string addon = Board)
        => (await context.NodeAsync(addon, node))["text"]?.GetValue<string>() ?? string.Empty;

    // The icon each square shows; BingoManager loads the check's journal icon into the native duty image.
    public static async Task<IReadOnlyList<uint>> CellIconsAsync(TestContext context)
    {
        var icons = await context.EvalAsync("""
            var bingo = (AddonWeeklyBingo*)ctx.Addon("WeeklyBingo");
            var result = new List<uint>();
            for (var i = 0; i < 16; i++)
            {
                var image = bingo->DutySlotList[i].DutyImage;
                var parts = image == null ? null : image->PartsList;
                var asset = parts == null ? null : parts->Parts[image->PartId].UldAsset;
                result.Add(asset == null || asset->AtkTexture.Resource == null ? 0u : asset->AtkTexture.Resource->IconId);
            }
            return result;
            """) ?? throw new AssertionException("could not read the duty slot icons");
        return [.. icons.AsArray().Select(icon => icon!.GetValue<uint>())];
    }

    public static Task ClickNodeAsync(TestContext context, string addon, uint nodeId)
        => context.Client.CallOkAsync("send_event", new { addon, node = nodeId.ToString(), eventType = "ButtonClick" });

    // Squares are clicked through the listener BingoManager registered with IAddonEventManager, not the native one (param 0).
    public static async Task ClickPluginButtonAsync(TestContext context, string node)
    {
        var events = (await context.NodeAsync(Board, node))["events"]!.AsArray();
        var click = events.FirstOrDefault(entry => entry!["type"]!.GetValue<string>() == "ButtonClick" && entry["param"]!.GetValue<int>() != 0)
                    ?? throw new AssertionException($"node {node} has no plugin ButtonClick listener");
        await context.Client.CallOkAsync("send_event", new { addon = Board, node, eventType = "ButtonClick", eventParam = click["param"]!.GetValue<int>() });
    }

    public static async Task OpenBonusInfoAsync(TestContext context)
    {
        await OpenBoardAsync(context);
        if (await context.IsAddonVisibleAsync(BonusInfo))
            return;
        await ClickNodeAsync(context, Board, SecondChanceButtonNodeId);
        Assert.True(await TestContext.WaitUntilAsync(() => context.IsAddonVisibleAsync(BonusInfo), 4000), "the second chance button opens its window");
        context.TrackOpenedAddon(BonusInfo);
        await Task.Delay(300);
    }

    // Clicking the dialog's own buttons; a hand-made FireCallback on a closing SelectYesno crashes the game.
    public static async Task AnswerConfirmAsync(TestContext context, bool yes)
    {
        Assert.True(await TestContext.WaitUntilAsync(() => context.IsAddonVisibleAsync(Confirm), 4000), "confirmation dialog shown");
        var label = yes ? "Yes" : "No";
        var button = (await context.Client.CallOkAsync("find_nodes", new { addon = Confirm, text = label, visibleOnly = true })).AsArray()
            .FirstOrDefault(node => node!["type"]!.GetValue<string>().StartsWith("Component:Button", StringComparison.Ordinal))
            ?? throw new AssertionException($"the dialog has no visible '{label}' button");
        await context.Client.CallOkAsync("send_event", new { addon = Confirm, node = button["path"]!.GetValue<string>(), eventType = "ButtonClick" });
        Assert.True(await TestContext.WaitUntilAsync(async () => !await context.IsAddonVisibleAsync(Confirm), 4000), "confirmation dialog closed");
        await Task.Delay(400);
    }

    public static async Task<string> ConfirmTextAsync(TestContext context)
    {
        Assert.True(await TestContext.WaitUntilAsync(() => context.IsAddonVisibleAsync(Confirm), 4000), "confirmation dialog shown");
        var texts = (await context.Client.CallOkAsync("find_nodes", new { addon = Confirm, type = "Text", visibleOnly = true })).AsArray();
        return string.Join("\n", texts.Select(text => text!["text"]?.GetValue<string>() ?? string.Empty));
    }

    // Multiply colours of the duty icons; BingoManager tints locked squares red and completed ones dark.
    public static async Task<IReadOnlyList<(int R, int G, int B)>> CellTintsAsync(TestContext context)
    {
        var tints = await context.EvalAsync("""
            var bingo = (AddonWeeklyBingo*)ctx.Addon("WeeklyBingo");
            var result = new List<object>();
            for (var i = 0; i < 16; i++)
            {
                var image = (AtkResNode*)bingo->DutySlotList[i].DutyImage;
                result.Add(image == null ? new { r = 0, g = 0, b = 0 } : new { r = (int)image->MultiplyRed, g = (int)image->MultiplyGreen, b = (int)image->MultiplyBlue });
            }
            return result;
            """) ?? throw new AssertionException("could not read the duty slot tints");
        return [.. tints.AsArray().Select(tint => (tint!["r"]!.GetValue<int>(), tint["g"]!.GetValue<int>(), tint["b"]!.GetValue<int>()))];
    }

    public static async Task<JsonNode> BonusInfoButtonAsync(TestContext context, uint nodeId)
        => await context.Client.CallOkAsync("get_addon_tree", new { addon = BonusInfo, node = nodeId.ToString(), maxDepth = 3 });
}
