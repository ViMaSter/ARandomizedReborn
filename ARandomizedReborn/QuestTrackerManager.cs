using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace ARandomizedReborn;

public sealed unsafe class QuestTrackerManager : IDisposable
{
    private const int BlueQuestIcon = 71205;
    private const int CompleteQuestIcon = 71203;
    private const int QuestTextStart = 9;
    private const int QuestTextSlots = 60;
    private const int MaxChecks = 5;
    private const int MaxObjectives = 9;

    private readonly IFramework framework;
    private readonly BingoSession session;
    private readonly CheckProgressTracker progress;
    private readonly ushort completedColor;
    private readonly Dictionary<int, int> originalNumbers = [];
    private readonly Dictionary<int, byte[]> originalTexts = [];
    private bool enabled;
    private bool applied;
    private DateTime nextUpdateUtc;
    private nint numberArrayAddress;
    private nint stringArrayAddress;
    private string? projectedTitle;
    private int projectedCount = -1;
    private readonly IPluginLog log;

    public QuestTrackerManager(IFramework framework, IDataManager dataManager, BingoSession session, CheckProgressTracker progress, IPluginLog log)
    {
        this.framework = framework;
        this.session = session;
        this.progress = progress;
        this.log = log;
        this.completedColor = (ushort)dataManager.GetExcelSheet<UIColor>(ClientLanguage.English)
            .Where(row => row.RowId <= ushort.MaxValue)
            .OrderBy(row => Math.Abs((int)((row.Dark >> 24) & 0xff) - 150) +
                Math.Abs((int)((row.Dark >> 16) & 0xff) - 150) +
                Math.Abs((int)((row.Dark >> 8) & 0xff) - 150) +
                ((row.Dark & 0xff) == 255 ? 0 : 1000))
            .First().RowId;
    }

    public void SetEnabled(bool enabled)
    {
        if (this.enabled == enabled)
            return;

        this.enabled = enabled;
        if (enabled)
            this.framework.Update += this.OnFrameworkUpdate;
        else
        {
            this.framework.Update -= this.OnFrameworkUpdate;
            this.Restore();
        }
    }

    public void Dispose() => this.SetEnabled(false);

    private void OnFrameworkUpdate(IFramework _)
    {
        if (DateTime.UtcNow < this.nextUpdateUtc)
            return;

        this.nextUpdateUtc = DateTime.UtcNow + TimeSpan.FromMilliseconds(200);
        var stage = AtkStage.Instance();
        if (stage == null)
            return;

        var numbers = stage->GetNumberArrayData(NumberArrayType.ToDoList);
        var strings = stage->GetStringArrayData(StringArrayType.ToDoList);
        if (numbers == null || strings == null || numbers->Size < 49 || strings->Size < QuestTextStart + QuestTextSlots)
            return;

        if (this.applied && (this.numberArrayAddress != (nint)numbers || this.stringArrayAddress != (nint)strings))
        {
            this.originalNumbers.Clear();
            this.originalTexts.Clear();
            this.applied = false;
        }

        if (this.applied && this.projectedTitle != strings->StringArray[QuestTextStart].ToString())
            this.Capture(numbers, strings);

        var definitions = this.session.GetNextChecks(this.progress, MaxChecks);
        if (this.projectedCount != definitions.Count)
        {
            this.log.Information($"[QuestTracker] selected {definitions.Count}/{this.session.Cells.Count(cell => !cell.IsComplete)} unfinished board checks");
            this.projectedCount = definitions.Count;
        }
        if (definitions.Count == 0)
        {
            this.Restore();
            return;
        }

        if (!this.applied)
            this.Capture(numbers, strings);

        var changed = false;
        SetNumber(7, 1);
        SetNumber(8, definitions.Count);
        var textIndex = QuestTextStart;
        foreach (var definition in definitions)
            SetText(textIndex++, new SeStringBuilder().AddText(definition.DisplayName).Build().EncodeWithNullTerminator());

        for (var checkIndex = 0; checkIndex < definitions.Count; checkIndex++)
        {
            var definition = definitions[checkIndex];
            var steps = definition.Steps.Take(MaxObjectives).ToArray();
            var pending = steps.Count(step => !this.progress.IsStepSatisfied(definition.Id, step));
            var objectives = steps.Length > 1
                ? steps.Select(step => (Text: step.Label + (step.Kind == ProgressStepKind.Counter
                    ? $" ({this.progress.GetValue(definition.Id, step.Id)}/{this.progress.GetTarget(definition.Id, step)})"
                    : string.Empty), Done: this.progress.IsStepSatisfied(definition.Id, step))).ToArray()
                : [(Text: definition.Id == "fill-armory-category" && this.progress.GetFullestArmoryCategory() is { } armory
                    ? $"Fill {armory.Name}: {armory.Filled}/{armory.Capacity}"
                    : definition.Description, Done: false)];

            SetNumber(9 + checkIndex, steps.Length > 1 && pending == 1 ? CompleteQuestIcon : BlueQuestIcon);
            SetNumber(19 + checkIndex, objectives.Length);
            SetNumber(29 + checkIndex, 0);
            SetNumber(39 + checkIndex, 0);
            foreach (var (text, done) in objectives)
            {
                var builder = new SeStringBuilder();
                if (done)
                    builder.AddUiForeground(this.completedColor);
                builder.AddText(text);
                if (done)
                    builder.AddUiForegroundOff();
                SetText(textIndex++, builder.Build().EncodeWithNullTerminator());
            }
        }

        for (var index = definitions.Count; index < MaxChecks; index++)
        {
            SetNumber(9 + index, 0);
            SetNumber(19 + index, 0);
            SetNumber(29 + index, 0);
            SetNumber(39 + index, 0);
        }

        for (; textIndex < QuestTextStart + QuestTextSlots; textIndex++)
            SetText(textIndex, [0]);

        if (changed)
            numbers->SetValue(1, 1, force: true);

        this.applied = true;
        this.projectedTitle = definitions[0].DisplayName;

        void SetNumber(int index, int value)
        {
            if (numbers->IntArray[index] != value)
            {
                numbers->SetValue(index, value);
                changed = true;
            }
        }

        void SetText(int index, byte[] value)
        {
            if (strings->StringArray[index].AsSpan().SequenceEqual(value.AsSpan(0, value.Length - 1)))
                return;

            fixed (byte* pointer = value)
                strings->SetValue(index, pointer, managed: true);
            changed = true;
        }
    }

    private void Capture(NumberArrayData* numbers, StringArrayData* strings)
    {
        this.originalNumbers.Clear();
        this.originalTexts.Clear();
        this.numberArrayAddress = (nint)numbers;
        this.stringArrayAddress = (nint)strings;
        for (var index = 7; index <= 8; index++)
            this.originalNumbers[index] = numbers->IntArray[index];
        for (var index = 0; index < MaxChecks; index++)
        {
            foreach (var offset in new[] { 9, 19, 29, 39 })
                this.originalNumbers[offset + index] = numbers->IntArray[offset + index];
        }

        for (var index = QuestTextStart; index < QuestTextStart + QuestTextSlots; index++)
            this.originalTexts[index] = [.. strings->StringArray[index].AsSpan(), 0];
    }

    private void Restore()
    {
        if (!this.applied)
            return;

        var stage = AtkStage.Instance();
        var numbers = stage == null ? null : stage->GetNumberArrayData(NumberArrayType.ToDoList);
        var strings = stage == null ? null : stage->GetStringArrayData(StringArrayType.ToDoList);
        if (numbers != null && strings != null && (nint)numbers == this.numberArrayAddress && (nint)strings == this.stringArrayAddress)
        {
            foreach (var (index, value) in this.originalNumbers)
                numbers->SetValue(index, value);

            foreach (var (index, value) in this.originalTexts)
            {
                fixed (byte* pointer = value)
                    strings->SetValue(index, pointer, managed: true);
            }

            numbers->SetValue(1, 1);
        }

        this.originalNumbers.Clear();
        this.originalTexts.Clear();
        this.applied = false;
        this.projectedTitle = null;
    }
}