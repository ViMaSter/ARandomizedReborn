using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Gui.Toast;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace ARandomizedReborn;

/// <summary>
/// Local IPC (<c>ARandomizedReborn.Test</c>) that lets the end-to-end suite read and set randomizer
/// state deterministically. Calls must come from the framework thread, which is where AtkMcp runs them.
/// </summary>
public sealed class TestControl : IDisposable
{
    public const string IpcName = "ARandomizedReborn.Test";

    private const int MaxToasts = 100;

    private readonly Plugin plugin;
    private readonly Configuration configuration;
    private readonly IToastGui toastGui;
    private readonly ICallGateProvider<string, string, string> provider;
    private readonly List<JsonObject> toasts = [];
    private int nextToastId;

    public TestControl(Plugin plugin, Configuration configuration, IDalamudPluginInterface pluginInterface, IToastGui toastGui)
    {
        this.plugin = plugin;
        this.configuration = configuration;
        this.toastGui = toastGui;
        this.BuildTimeUtc = File.GetLastWriteTimeUtc(pluginInterface.AssemblyLocation.FullName);

        this.toastGui.Toast += this.OnToast;
        this.toastGui.QuestToast += this.OnQuestToast;
        this.toastGui.ErrorToast += this.OnErrorToast;

        this.provider = pluginInterface.GetIpcProvider<string, string, string>(IpcName);
        this.provider.RegisterFunc(this.Invoke);
    }

    public DateTime BuildTimeUtc { get; }

    public void Dispose()
    {
        this.provider.UnregisterFunc();
        this.toastGui.Toast -= this.OnToast;
        this.toastGui.QuestToast -= this.OnQuestToast;
        this.toastGui.ErrorToast -= this.OnErrorToast;
    }

    private string Invoke(string command, string argumentsJson)
    {
        try
        {
            var arguments = string.IsNullOrWhiteSpace(argumentsJson) ? [] : JsonNode.Parse(argumentsJson)!.AsObject();
            return this.Dispatch(command, arguments).ToJsonString();
        }
        catch (Exception exception)
        {
            return new JsonObject { ["error"] = $"{exception.GetType().Name}: {exception.Message}" }.ToJsonString();
        }
    }

    private JsonNode Dispatch(string command, JsonObject arguments) => command switch
    {
        "version" => new JsonObject
        {
            ["buildTimeUtc"] = this.BuildTimeUtc,
            ["version"] = typeof(Plugin).Assembly.GetName().Version!.ToString(),
        },
        "state" or "export_state" => this.State(),
        "checks" => this.CheckCatalog(),
        "import_state" => this.ImportState(Object(arguments, "state")),
        "new_session" => this.NewSession(arguments),
        "reset" => this.Run(() => this.plugin.ResetProgress()),
        "open_board" => this.Run(() => this.plugin.OpenBingoBoard()),
        "set_randomizer" => this.Run(() => this.plugin.SetRandomizerEnabled(Bool(arguments, "enabled"))),
        "set_points" => this.Run(() =>
        {
            this.configuration.BingoSecondChancePoints = Math.Clamp(Int(arguments, "points"), 0, BingoSession.MaxSecondChancePoints);
            this.configuration.Save();
        }),
        "set_hint_mode" => this.Run(() =>
        {
            this.configuration.BingoHintModeEnabled = Bool(arguments, "enabled");
            this.configuration.Save();
        }),
        "set_unlock" => this.Run(() => this.plugin.SetUnlockState(UnlockOf(String(arguments, "key")), Bool(arguments, "value"))),
        "set_all_unlocks" => this.Run(() =>
        {
            foreach (var definition in Unlocks.Definitions)
                this.plugin.SetUnlockState(definition.Key, Bool(arguments, "value"));
        }),
        "complete_check" => this.Run(() => this.plugin.CompleteCheckManually(String(arguments, "checkId"))),
        "set_journal_preference" => this.Run(() => this.plugin.BingoSession.SetJournalPreference(String(arguments, "checkId"), Int(arguments, "preference"))),
        "reset_check" => this.Run(() => this.ResetCheck(String(arguments, "checkId"))),
        "complete_line" => this.Run(() => this.CompleteLine(Int(arguments, "line"))),
        "set_step" => this.Run(() => this.SetStep(String(arguments, "checkId"), String(arguments, "stepId"), Int(arguments, "value"))),
        "toasts" => this.Toasts(arguments["since"]?.GetValue<int>() ?? 0),
        _ => throw new ArgumentException($"Unknown command '{command}'."),
    };

    private JsonNode State()
    {
        var board = new JsonArray();
        for (var index = 0; index < this.configuration.BingoBoard.Count; index++)
        {
            var cell = this.configuration.BingoBoard[index];
            board.Add(new JsonObject
            {
                ["index"] = index,
                ["checkId"] = cell.CheckId,
                ["name"] = Checks.DisplayName(cell.CheckId),
                ["reward"] = cell.Reward.ToString(),
                ["requiredUnlock"] = cell.RequiredUnlock?.ToString(),
                ["complete"] = cell.IsComplete,
                ["manual"] = cell.ManualOverride,
                ["attemptable"] = this.plugin.BingoSession.IsCellAttemptable(cell),
            });
        }

        var unlocks = new JsonObject();
        foreach (var definition in Unlocks.Definitions)
            unlocks[definition.Key.ToString()] = Unlocks.Get(this.configuration, definition.Key);

        var progress = new JsonObject();
        foreach (var (checkId, steps) in this.configuration.CheckStepProgress)
        {
            var values = new JsonObject();
            foreach (var (stepId, value) in steps)
                values[stepId] = value;
            progress[checkId] = values;
        }

        var preferences = new JsonObject();
        foreach (var (checkId, preference) in this.configuration.JournalCheckPreferences)
            preferences[checkId] = preference;

        return new JsonObject
        {
            ["randomizer"] = this.configuration.EnableRandomizer,
            ["difficulty"] = this.configuration.BingoDifficulty.ToString(),
            ["generating"] = this.plugin.BingoSession.IsGenerating,
            ["hasBoard"] = this.plugin.BingoSession.HasBoard,
            ["won"] = this.configuration.BingoWon,
            ["winningLine"] = this.plugin.BingoSession.WinningLine is { } line ? new JsonArray([.. line.Select(index => JsonValue.Create(index))]) : null,
            ["points"] = this.configuration.BingoSecondChancePoints,
            ["maxPoints"] = BingoSession.MaxSecondChancePoints,
            ["hintMode"] = this.configuration.BingoHintModeEnabled,
            ["board"] = board,
            ["unlocks"] = unlocks,
            ["completed"] = new JsonArray([.. this.configuration.CompletedChecks.Select(id => JsonValue.Create(id))]),
            ["manuallyCompleted"] = new JsonArray([.. this.configuration.ManuallyCompletedChecks.Select(id => JsonValue.Create(id))]),
            ["everTriggered"] = new JsonArray([.. this.configuration.EverTriggeredChecks.Select(id => JsonValue.Create(id))]),
            ["stepProgress"] = progress,
            ["journalPreferences"] = preferences,
        };
    }

    private JsonNode CheckCatalog()
    {
        var checks = new JsonArray();
        foreach (var definition in Checks.Definitions)
        {
            var steps = new JsonArray();
            foreach (var step in definition.Steps)
            {
                steps.Add(new JsonObject
                {
                    ["id"] = step.Id,
                    ["label"] = step.Label,
                    ["kind"] = step.Kind.ToString(),
                    ["target"] = step.Target,
                    ["automatic"] = step.Automatic,
                });
            }

            checks.Add(new JsonObject
            {
                ["id"] = definition.Id,
                ["name"] = definition.DisplayName,
                ["description"] = definition.Description,
                ["requiredUnlock"] = definition.RequiredUnlock?.ToString(),
                ["area"] = definition.JournalArea,
                ["icon"] = definition.JournalIcon,
                ["steps"] = steps,
            });
        }

        return checks;
    }

    private JsonNode ImportState(JsonObject state)
    {
        this.configuration.BingoDifficulty = Enum.Parse<BingoDifficulty>(state["difficulty"]!.GetValue<string>());
        this.configuration.BingoBoard = [.. state["board"]!.AsArray().Select(cell => new BingoCell
        {
            CheckId = cell!["checkId"]!.GetValue<string>(),
            Reward = Enum.Parse<UnlockKey>(cell["reward"]!.GetValue<string>()),
            IsComplete = cell["complete"]!.GetValue<bool>(),
            ManualOverride = cell["manual"]!.GetValue<bool>(),
        })];
        this.configuration.BingoSecondChancePoints = state["points"]!.GetValue<int>();
        this.configuration.BingoWon = state["won"]!.GetValue<bool>();
        this.configuration.BingoHintModeEnabled = state["hintMode"]!.GetValue<bool>();
        this.configuration.CompletedChecks = [.. state["completed"]!.AsArray().Select(id => id!.GetValue<string>())];
        this.configuration.ManuallyCompletedChecks = [.. state["manuallyCompleted"]!.AsArray().Select(id => id!.GetValue<string>())];
        this.configuration.EverTriggeredChecks = [.. state["everTriggered"]!.AsArray().Select(id => id!.GetValue<string>())];
        this.configuration.CheckStepProgress = state["stepProgress"]!.AsObject().ToDictionary(
            entry => entry.Key,
            entry => entry.Value!.AsObject().ToDictionary(step => step.Key, step => step.Value!.GetValue<int>()));
        this.configuration.JournalCheckPreferences = state["journalPreferences"]!.AsObject().ToDictionary(entry => entry.Key, entry => entry.Value!.GetValue<int>());
        foreach (var definition in Unlocks.Definitions)
            this.plugin.SetUnlockState(definition.Key, state["unlocks"]![definition.Key.ToString()]!.GetValue<bool>());
        this.plugin.SetRandomizerEnabled(state["randomizer"]!.GetValue<bool>());
        this.configuration.Save();
        return this.State();
    }

    // Generation runs off-thread; poll "state" until generating is false.
    private JsonNode NewSession(JsonObject arguments)
    {
        var difficulty = Enum.Parse<BingoDifficulty>(String(arguments, "difficulty"), ignoreCase: true);
        this.plugin.StartNewSession(difficulty, arguments["seed"]?.GetValue<int>());
        return new JsonObject { ["started"] = true };
    }

    private void ResetCheck(string checkId)
    {
        this.configuration.CompletedChecks.Remove(checkId);
        this.configuration.ManuallyCompletedChecks.Remove(checkId);
        this.configuration.EverTriggeredChecks.Remove(checkId);
        this.configuration.CheckStepProgress.Remove(checkId);
        foreach (var cell in this.configuration.BingoBoard.Where(cell => cell.CheckId == checkId))
        {
            cell.IsComplete = false;
            cell.ManualOverride = false;
            this.plugin.SetUnlockState(cell.Reward, false);
        }

        this.configuration.BingoWon = BingoBoard.FindCompletedLine(this.configuration.BingoBoard) != null;
        this.configuration.Save();
    }

    private void CompleteLine(int line)
    {
        foreach (var index in BingoBoard.Lines[line])
            this.plugin.CompleteCheckManually(this.configuration.BingoBoard[index].CheckId);
    }

    private void SetStep(string checkId, string stepId, int value)
    {
        var step = Checks.Find(checkId)?.Steps.FirstOrDefault(step => step.Id == stepId)
                   ?? throw new ArgumentException($"Check '{checkId}' has no step '{stepId}'.");
        if (step.Kind == ProgressStepKind.Flag)
            this.plugin.CheckProgressTracker.SetFlag(checkId, stepId, value != 0);
        else
            this.plugin.CheckProgressTracker.AdjustCounter(checkId, stepId, value - this.plugin.CheckProgressTracker.GetValue(checkId, stepId));
    }

    private JsonNode Toasts(int since)
    {
        var entries = new JsonArray();
        foreach (var toast in this.toasts.Where(toast => toast["id"]!.GetValue<int>() > since))
            entries.Add(toast.DeepClone());
        return new JsonObject { ["lastId"] = this.nextToastId, ["toasts"] = entries };
    }

    private void OnToast(ref SeString message, ref ToastOptions options, ref bool isHandled) => this.Record("normal", message);

    private void OnQuestToast(ref SeString message, ref QuestToastOptions options, ref bool isHandled) => this.Record("quest", message);

    private void OnErrorToast(ref SeString message, ref bool isHandled) => this.Record("error", message);

    private void Record(string kind, SeString message)
    {
        this.toasts.Add(new JsonObject { ["id"] = ++this.nextToastId, ["kind"] = kind, ["message"] = message.TextValue });
        if (this.toasts.Count > MaxToasts)
            this.toasts.RemoveRange(0, this.toasts.Count - MaxToasts);
    }

    private JsonNode Run(Action action)
    {
        action();
        return new JsonObject { ["ok"] = true };
    }

    private static JsonObject Object(JsonObject arguments, string name)
        => arguments[name]?.AsObject() ?? throw new ArgumentException($"Missing argument '{name}'.");

    private static string String(JsonObject arguments, string name)
        => arguments[name]?.GetValue<string>() ?? throw new ArgumentException($"Missing argument '{name}'.");

    private static int Int(JsonObject arguments, string name)
        => arguments[name]?.GetValue<int>() ?? throw new ArgumentException($"Missing argument '{name}'.");

    private static bool Bool(JsonObject arguments, string name)
        => arguments[name]?.GetValue<bool>() ?? throw new ArgumentException($"Missing argument '{name}'.");

    private static UnlockKey UnlockOf(string key) => Enum.Parse<UnlockKey>(key, ignoreCase: true);
}
