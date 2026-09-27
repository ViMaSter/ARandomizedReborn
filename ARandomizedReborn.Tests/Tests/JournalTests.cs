using System.Text.Json.Nodes;

namespace ARandomizedReborn.Tests.Tests;

// JournalManager replaces the quest journal with the board's checks; ids are faked from 0xF000 up.
[Order(6)]
public static class JournalTests
{
    private const string Journal = "Journal";
    private const string Detail = "JournalDetail";
    private const uint FirstFakeQuestId = 0xF000;

    [Test("arr-journal", RequiresTool = "eval_csharp")]
    public static async Task JournalListsBoardChecksInsteadOfQuests(TestContext context)
    {
        var arr = Arr.Of(context);
        await arr.NewSessionAsync();
        var list = await OpenListAsync(context);
        var names = (await arr.StateAsync())["board"]!.AsArray().Select(cell => cell!["name"]!.GetValue<string>()).ToHashSet();
        var known = (await arr.CallAsync("checks")).AsArray().Select(check => check!["name"]!.GetValue<string>()).ToHashSet();

        var rows = (await context.NodeAsync(Journal, list))["treeListItems"]!.AsArray();
        var leaves = rows.Where(row => (row!["uints"]![0]!.GetValue<uint>() >> 16) >= FirstFakeQuestId && (row["uints"]![0]!.GetValue<uint>() >> 16) < 0xFFFD).ToList();
        Assert.True(leaves.Count > 0, "the journal shows randomizer rows");
        Assert.True(leaves.All(row => known.Contains(row!["strings"]![0]!.GetValue<string>())), "every randomizer row is a check");
        Assert.True(leaves.Any(row => names.Contains(row!["strings"]![0]!.GetValue<string>())), "the current board's checks are listed");
        Assert.True(rows.Any(row => row!["uints"]![0]!.GetValue<uint>() == 0xFFFF0002), "checks are grouped under area headers");
    }

    [Test("arr-journal", RequiresTool = "eval_csharp")]
    public static async Task SelectingACheckShowsItsStepsAsObjectives(TestContext context)
    {
        var arr = Arr.Of(context);
        await arr.NewSessionAsync();
        var list = await OpenListAsync(context);
        var board = (await arr.StateAsync())["board"]!.AsArray();
        var catalog = (await arr.CallAsync("checks")).AsArray().ToDictionary(check => check!["name"]!.GetValue<string>(), check => check!);

        var rows = (await context.NodeAsync(Journal, list))["treeListItems"]!.AsArray();
        var row = rows.First(entry => board.Any(cell => cell!["name"]!.GetValue<string>() == entry!["strings"]![0]!.GetValue<string>()))!;
        var name = row["strings"]![0]!.GetValue<string>();
        foreach (var eventType in new[] { "ListItemClick", "ListItemHighlight" })
            await context.Client.CallOkAsync("send_event", new { addon = Journal, node = list, eventType, listItem = row["index"]!.GetValue<int>() });

        Assert.True(
            await TestContext.WaitUntilAsync(async () => (await context.Client.CallOkAsync("find_nodes", new { addon = Detail, text = name })).AsArray().Count > 0, 4000),
            $"selecting '{name}' opens its detail page");
        context.TrackOpenedAddon(Detail);

        var detailTexts = (await context.Client.CallOkAsync("find_nodes", new { addon = Detail, type = "Text", limit = 200 })).AsArray()
            .Select(node => node!["text"]?.GetValue<string>() ?? string.Empty).ToList();
        foreach (var step in catalog[name]["steps"]!.AsArray())
        {
            var label = step!["label"]!.GetValue<string>();
            Assert.True(detailTexts.Any(text => text.Contains(label, StringComparison.Ordinal)), $"'{name}' lists its step '{label}'");
        }
    }

    private static async Task<string> OpenListAsync(TestContext context)
    {
        await context.OpenByCommandAsync(Journal, "/journal");
        var list = (await context.Client.CallOkAsync("find_nodes", new { addon = Journal, type = "Component:TreeList" })).AsArray().FirstOrDefault()
                   ?? throw new AssertionException("the journal has no tree list");
        await Task.Delay(300);
        return list["path"]!.GetValue<string>();
    }
}
