using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using ClientTerritoryIntendedUse = FFXIVClientStructs.FFXIV.Client.Enums.TerritoryIntendedUse;

namespace ARandomizedReborn;

/// <summary>Watches the game for the check steps that can be detected automatically via emotes.</summary>
public sealed unsafe class ObjectiveTracker : IDisposable
{
    private const string PointAtBlueAlisaieId = "point-blue-alisaie";
    private const string PetGrahaTiaId = "pet-graha-tia";
    private const string CheerSameJobId = "cheer-same-job-player";
    private const string WaveGathererId = "wave-gatherer";

    private readonly IGameInteropProvider gameInteropProvider;
    private readonly ITargetManager targetManager;
    private readonly IObjectTable objectTable;
    private readonly CheckProgressTracker progressTracker;
    private readonly Dictionary<ushort, string> emoteNames = [];
    private Hook<AgentEmote.Delegates.ExecuteEmote>? emoteHook;

    public ObjectiveTracker(
        IGameInteropProvider gameInteropProvider,
        IDataManager dataManager,
        ITargetManager targetManager,
        IObjectTable objectTable,
        CheckProgressTracker progressTracker)
    {
        this.gameInteropProvider = gameInteropProvider;
        this.targetManager = targetManager;
        this.objectTable = objectTable;
        this.progressTracker = progressTracker;

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
        this.TryCheerSameJob(emoteId);
        this.TryWaveGatherer(emoteId);
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

        this.progressTracker.SetFlag(checkId, "done", true);
    }

    private void TryCheerSameJob(ushort emoteId)
    {
        var emoteName = this.emoteNames.TryGetValue(emoteId, out var name) ? name : string.Empty;
        if (!string.Equals(emoteName, "Cheer", StringComparison.OrdinalIgnoreCase))
            return;

        if (this.targetManager.Target is not ICharacter { ObjectKind: ObjectKind.Pc } target)
            return;

        if (this.objectTable.LocalPlayer is not ICharacter localPlayer)
            return;

        if (target.ClassJob.RowId == localPlayer.ClassJob.RowId)
            this.progressTracker.SetFlag(CheerSameJobId, "done", true);
    }

    private void TryWaveGatherer(ushort emoteId)
    {
        var emoteName = this.emoteNames.TryGetValue(emoteId, out var name) ? name : string.Empty;
        if (!string.Equals(emoteName, "Wave", StringComparison.OrdinalIgnoreCase))
            return;

        if (GameMain.Instance()->CurrentTerritoryIntendedUseId != ClientTerritoryIntendedUse.Overworld)
            return;

        if (this.targetManager.Target is not ICharacter { ObjectKind: ObjectKind.Pc } target)
            return;

        if (this.objectTable.LocalPlayer is not ICharacter localPlayer || target.Address == localPlayer.Address)
            return;

        if (target.ClassJob.RowId is >= 16 and <= 18)
            this.progressTracker.SetFlag(WaveGathererId, "done", true);
    }

    private static bool IsBlueAlisaie(string targetName)
        => targetName.Contains("Alisaie", StringComparison.OrdinalIgnoreCase) ||
           targetName.Contains("Alisae", StringComparison.OrdinalIgnoreCase);

    private static bool IsGrahaTia(string targetName)
        => targetName.Contains("G'raha", StringComparison.OrdinalIgnoreCase) ||
           targetName.Contains("Graha", StringComparison.OrdinalIgnoreCase);
}