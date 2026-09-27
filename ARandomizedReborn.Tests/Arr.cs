using System.Text.Json;
using System.Text.Json.Nodes;

namespace ARandomizedReborn.Tests;

// Drives ARandomizedReborn's TestControl IPC through AtkMcp's eval_csharp (only plugins can reach Dalamud IPC).
public sealed class Arr(TestContext context)
{
    public const string IpcName = "ARandomizedReborn.Test";

    private static readonly string SnapshotPath = Path.Combine(Path.GetTempPath(), "arr-tests-state.json");

    public static Arr Of(TestContext context) => (Arr)context.Items["arr"];

    public static PluginUnderTest PluginUnderTest { get; } = new(
        "ARandomizedReborn",
        "ARandomizedReborn.slnx",
        Path.Combine("ARandomizedReborn", "bin", "x64", "Debug", "ARandomizedReborn.dll"),
        BuildTimeAsync);

    public async Task<JsonNode> CallAsync(string command, object? arguments = null)
    {
        var json = arguments == null ? "{}" : JsonSerializer.Serialize(arguments);
        var raw = await context.EvalAsync($$"""
            var ipc = ctx.PluginInterface.GetIpcSubscriber<string, string, string>("{{IpcName}}");
            return ipc.InvokeFunc({{Literal(command)}}, {{Literal(json)}});
            """) ?? throw new AssertionException($"{command}: eval returned nothing.");
        var result = JsonNode.Parse(raw.GetValue<string>())!;
        if (result is JsonObject payload && payload["error"] is { } error)
            throw new AssertionException($"{command}: {error.GetValue<string>()}");
        return result;
    }

    public async Task<JsonObject> StateAsync() => (await this.CallAsync("state")).AsObject();

    public Task<JsonNode> SetPointsAsync(int points) => this.CallAsync("set_points", new { points });

    public Task<JsonNode> SetUnlockAsync(string key, bool value) => this.CallAsync("set_unlock", new { key, value });

    public Task<JsonNode> SetHintModeAsync(bool enabled) => this.CallAsync("set_hint_mode", new { enabled });

    public Task<JsonNode> SetRandomizerAsync(bool enabled) => this.CallAsync("set_randomizer", new { enabled });

    public Task<JsonNode> CompleteCheckAsync(string checkId) => this.CallAsync("complete_check", new { checkId });

    public Task<JsonNode> ResetCheckAsync(string checkId) => this.CallAsync("reset_check", new { checkId });

    public Task<JsonNode> SetStepAsync(string checkId, string stepId, int value) => this.CallAsync("set_step", new { checkId, stepId, value });

    public Task<JsonNode> CompleteLineAsync(int line) => this.CallAsync("complete_line", new { line });

    public async Task<JsonObject> NewSessionAsync(string difficulty = "Medium", int seed = 12345)
    {
        await this.CallAsync("new_session", new { difficulty, seed });
        Assert.True(
            await TestContext.WaitUntilAsync(async () =>
            {
                var state = await this.StateAsync();
                return !state["generating"]!.GetValue<bool>() && state["hasBoard"]!.GetValue<bool>();
            }, 20000, 250),
            "board generation finished");
        return await this.StateAsync();
    }

    // Toast ids are monotonic, so tests snapshot the id first and then read only what their action produced.
    public async Task<int> ToastMarkAsync() => (await this.CallAsync("toasts", new { since = int.MaxValue - 1 }))["lastId"]!.GetValue<int>();

    public async Task<IReadOnlyList<string>> ToastsSinceAsync(int mark)
        => [.. (await this.CallAsync("toasts", new { since = mark }))["toasts"]!.AsArray().Select(toast => toast!["message"]!.GetValue<string>())];

    public async Task AssertToastAsync(int mark, string fragment, string what)
    {
        IReadOnlyList<string> toasts = [];
        var seen = await TestContext.WaitUntilAsync(async () =>
        {
            toasts = await this.ToastsSinceAsync(mark);
            return toasts.Any(toast => toast.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }, 3000, 100);
        Assert.True(seen, $"{what}: expected a toast containing '{fragment}', got [{string.Join(" | ", toasts)}]");
    }

    public async Task AssertNoToastAsync(int mark, string fragment, string what)
    {
        var toasts = await this.ToastsSinceAsync(mark);
        Assert.True(!toasts.Any(toast => toast.Contains(fragment, StringComparison.OrdinalIgnoreCase)), $"{what}: unexpected toast containing '{fragment}'");
    }

    public static async Task SetUpAsync(TestContext context)
    {
        if (!context.Client.ToolNames.Contains("eval_csharp"))
            throw new InvalidOperationException("The randomizer suite needs eval_csharp; enable eval in /atkmcp.");

        var arr = new Arr(context);
        context.Items["arr"] = arr;
        JsonObject snapshot;
        try
        {
            snapshot = (await arr.CallAsync("export_state")).AsObject();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"ARandomizedReborn's test IPC did not answer ({exception.Message}). Is the plugin loaded and rebuilt?");
        }

        // A run that dies (or crashes the game) must not cost the player their session, so the snapshot also lives on disk.
        if (File.Exists(SnapshotPath))
        {
            var leftover = JsonNode.Parse(await File.ReadAllTextAsync(SnapshotPath))!.AsObject();
            await arr.CallAsync("import_state", new { state = leftover });
            snapshot = leftover;
            Console.WriteLine($"Restored the state left over from an interrupted run ({SnapshotPath}).");
        }
        else
        {
            await File.WriteAllTextAsync(SnapshotPath, snapshot.ToJsonString());
        }

        context.Items["arr-snapshot"] = snapshot;
        await arr.SetRandomizerAsync(true);
        await arr.NewSessionAsync();
        Console.WriteLine("Randomizer enabled, deterministic board generated (previous state is restored after the run).");
    }

    public static async Task TearDownAsync(TestContext context)
    {
        if (!context.Items.TryGetValue("arr-snapshot", out var snapshot))
            return;
        await Of(context).CallAsync("import_state", new { state = (JsonObject)snapshot });
        File.Delete(SnapshotPath);
        Console.WriteLine("Restored the randomizer state captured before the run.");
    }

    private static async Task<DateTime?> BuildTimeAsync(McpClient client)
    {
        if (!client.ToolNames.Contains("eval_csharp"))
            return null;
        try
        {
            var call = await client.CallAsync("eval_csharp", new
            {
                code = $$"""
                    var ipc = ctx.PluginInterface.GetIpcSubscriber<string, string, string>("{{IpcName}}");
                    return ipc.InvokeFunc("version", "{}");
                    """,
            });
            if (call.IsError)
                return null;
            var version = JsonNode.Parse(call.Json["result"]!.GetValue<string>())!;
            return version["buildTimeUtc"]?.GetValue<DateTime>().ToUniversalTime();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Literal(string value) => JsonValue.Create(value).ToJsonString();
}
