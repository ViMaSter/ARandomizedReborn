using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace ARandomizedReborn;

public sealed record ObjectiveDefinition(string Id, string DisplayName, string Description);

public sealed record ObjectiveState(ObjectiveDefinition Definition, bool IsComplete);

public sealed unsafe class ObjectiveTracker : IDisposable
{
    public static readonly ObjectiveDefinition PointAtBlueAlisaie = new(
        "point-blue-alisaie",
        "/point at Blue Alisaie",
        "Find Alisae, select her and type /point or use the 'Point' emote.");

    public static readonly ObjectiveDefinition PetGrahaTia = new(
        "pet-graha-tia",
        "Pet the certified best boy",
        "Find G'raha Tia, select him and type /pet or use the 'Pet' emote.");

    private readonly IGameInteropProvider gameInteropProvider;
    private readonly ITargetManager targetManager;
    private readonly IToastGui toastGui;
    private readonly IPluginLog log;
    private readonly Configuration configuration;
    private readonly Dictionary<ushort, string> emoteNames = [];
    private Hook<AgentEmote.Delegates.ExecuteEmote>? emoteHook;

    public ObjectiveTracker(
        IGameInteropProvider gameInteropProvider,
        IDataManager dataManager,
        ITargetManager targetManager,
        IToastGui toastGui,
        IPluginLog log,
        Configuration configuration)
    {
        this.gameInteropProvider = gameInteropProvider;
        this.targetManager = targetManager;
        this.toastGui = toastGui;
        this.log = log;
        this.configuration = configuration;

        foreach (var row in dataManager.GetExcelSheet<Emote>(ClientLanguage.English))
            this.emoteNames[(ushort)row.RowId] = row.Name.ToString();
    }

    public IReadOnlyList<ObjectiveState> GetStates()
        =>
        [
            new ObjectiveState(PointAtBlueAlisaie, this.configuration.PointAtBlueAlisaieComplete),
            new ObjectiveState(PetGrahaTia, this.configuration.PetGrahaTiaComplete),
        ];

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            this.EnableHooks();
            return;
        }

        this.DisableHooks();
    }

    public void Reset()
    {
        this.configuration.PointAtBlueAlisaieComplete = false;
        this.configuration.PetGrahaTiaComplete = false;
        this.configuration.Save();
    }

    public void Dispose()
    {
        this.DisableHooks();
    }

    private void EnableHooks()
    {
        if (this.emoteHook != null)
            return;

        this.emoteHook = this.gameInteropProvider.HookFromAddress<AgentEmote.Delegates.ExecuteEmote>(
            AgentEmote.MemberFunctionPointers.ExecuteEmote,
            this.OnExecuteEmote);
        this.emoteHook.Enable();
    }

    private void DisableHooks()
    {
        if (this.emoteHook == null)
            return;

        this.emoteHook.Dispose();
        this.emoteHook = null;
    }

    private void OnExecuteEmote(
        AgentEmote* agent,
        ushort emoteId,
        EmoteController.PlayEmoteOption* playEmoteOption,
        bool addToHistory,
        bool liveUpdateHistory)
    {
        this.TryUpdatePointAtBlueAlisaie(emoteId);
        this.TryUpdatePetGrahaTia(emoteId);
        this.emoteHook!.Original(agent, emoteId, playEmoteOption, addToHistory, liveUpdateHistory);
    }

    private void TryUpdatePointAtBlueAlisaie(ushort emoteId)
    {
        if (this.configuration.PointAtBlueAlisaieComplete)
            return;

        var emoteName = this.emoteNames.TryGetValue(emoteId, out var name) ? name : string.Empty;
        if (!string.Equals(emoteName, "Point", StringComparison.OrdinalIgnoreCase))
            return;

        var targetName = this.targetManager.Target?.Name.ToString();
        if (string.IsNullOrWhiteSpace(targetName) || !IsBlueAlisaie(targetName))
            return;

        this.configuration.PointAtBlueAlisaieComplete = true;
        this.configuration.Save();
        this.toastGui.ShowQuest($"Objective complete: {PointAtBlueAlisaie.DisplayName}");
        this.log.Information("Objective completed: {ObjectiveId}", PointAtBlueAlisaie.Id);
    }

    private void TryUpdatePetGrahaTia(ushort emoteId)
    {
        if (this.configuration.PetGrahaTiaComplete)
            return;

        var emoteName = this.emoteNames.TryGetValue(emoteId, out var name) ? name : string.Empty;
        if (!string.Equals(emoteName, "Pet", StringComparison.OrdinalIgnoreCase))
            return;

        var targetName = this.targetManager.Target?.Name.ToString();
        if (string.IsNullOrWhiteSpace(targetName) || !IsGrahaTia(targetName))
            return;

        this.configuration.PetGrahaTiaComplete = true;
        this.configuration.Save();
        this.toastGui.ShowQuest($"Objective complete: {PetGrahaTia.DisplayName}");
        this.log.Information("Objective completed: {ObjectiveId}", PetGrahaTia.Id);
    }

    private static bool IsBlueAlisaie(string targetName)
        => targetName.Contains("Blue Alisaie", StringComparison.OrdinalIgnoreCase) ||
           targetName.Contains("Alisaie", StringComparison.OrdinalIgnoreCase) ||
           targetName.Contains("Alisae", StringComparison.OrdinalIgnoreCase);

    private static bool IsGrahaTia(string targetName)
        => targetName.Contains("G'raha Tia", StringComparison.OrdinalIgnoreCase) ||
           targetName.Contains("Graha Tia", StringComparison.OrdinalIgnoreCase) ||
           targetName.Contains("G'raha", StringComparison.OrdinalIgnoreCase) ||
           targetName.Contains("Graha", StringComparison.OrdinalIgnoreCase);
}