using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace ARandomizedReborn;

/// <summary>Watches the game for the checks that can be detected automatically.</summary>
public sealed unsafe class ObjectiveTracker : IDisposable
{
    private const string PointAtBlueAlisaieId = "point-blue-alisaie";
    private const string PetGrahaTiaId = "pet-graha-tia";

    private readonly IGameInteropProvider gameInteropProvider;
    private readonly ITargetManager targetManager;
    private readonly IPluginLog log;
    private readonly Func<string, bool> reportCheck;
    private readonly Dictionary<ushort, string> emoteNames = [];
    private Hook<AgentEmote.Delegates.ExecuteEmote>? emoteHook;

    public ObjectiveTracker(
        IGameInteropProvider gameInteropProvider,
        IDataManager dataManager,
        ITargetManager targetManager,
        IPluginLog log,
        Func<string, bool> reportCheck)
    {
        this.gameInteropProvider = gameInteropProvider;
        this.targetManager = targetManager;
        this.log = log;
        this.reportCheck = reportCheck;

        foreach (var row in dataManager.GetExcelSheet<Emote>(ClientLanguage.English))
            this.emoteNames[(ushort)row.RowId] = row.Name.ToString();
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            this.EnableHooks();
            return;
        }

        this.DisableHooks();
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
        this.TryTargetedEmote(emoteId, "Point", IsBlueAlisaie, PointAtBlueAlisaieId);
        this.TryTargetedEmote(emoteId, "Pet", IsGrahaTia, PetGrahaTiaId);
        this.emoteHook!.Original(agent, emoteId, playEmoteOption, addToHistory, liveUpdateHistory);
    }

    private void TryTargetedEmote(ushort emoteId, string expectedEmote, Func<string, bool> targetMatches, string checkId)
    {
        var emoteName = this.emoteNames.TryGetValue(emoteId, out var name) ? name : string.Empty;
        if (!string.Equals(emoteName, expectedEmote, StringComparison.OrdinalIgnoreCase))
            return;

        var targetName = this.targetManager.Target?.Name.ToString();
        if (string.IsNullOrWhiteSpace(targetName) || !targetMatches(targetName))
            return;

        if (this.reportCheck(checkId))
            this.log.Information("Check completed: {CheckId}", checkId);
    }

    private static bool IsBlueAlisaie(string targetName)
        => targetName.Contains("Alisaie", StringComparison.OrdinalIgnoreCase) ||
           targetName.Contains("Alisae", StringComparison.OrdinalIgnoreCase);

    private static bool IsGrahaTia(string targetName)
        => targetName.Contains("G'raha", StringComparison.OrdinalIgnoreCase) ||
           targetName.Contains("Graha", StringComparison.OrdinalIgnoreCase);
}