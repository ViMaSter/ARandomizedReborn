using System.Reflection;
using ARandomizedReborn.Tests;
using FFXIVTestKit;

return await TestKitHost.RunAsync(args, new TestKitOptions
{
    SuiteName = "ARandomizedReborn test suite",
    TestAssembly = Assembly.GetExecutingAssembly(),
    ArtifactDirectoryName = "arr-tests",
    SuiteWindows = ["WeeklyBingoBonusInfo", "SelectYesno", "WeeklyBingo", "JournalDetail", "Journal", "ContentsFinder", "AreaMap"],
    Plugins = [PluginUnderTest.AtkMcp, Arr.PluginUnderTest],
    SetUpAsync = Arr.SetUpAsync,
    TearDownAsync = Arr.TearDownAsync,
});
