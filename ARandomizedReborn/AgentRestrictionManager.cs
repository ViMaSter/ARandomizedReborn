using System;
using Dalamud.Game.Agent;
using Dalamud.Game.Agent.AgentArgTypes;
using Dalamud.Plugin.Services;

namespace ARandomizedReborn;

public sealed class AgentRestrictionManager : IDisposable
{
    private readonly IAgentLifecycle agentLifecycle;
    private readonly IToastGui toastGui;
    private readonly Configuration configuration;
    private bool enabled;

    public AgentRestrictionManager(IAgentLifecycle agentLifecycle, IToastGui toastGui, Configuration configuration)
    {
        this.agentLifecycle = agentLifecycle;
        this.toastGui = toastGui;
        this.configuration = configuration;
    }

    public void SetEnabled(bool enabled)
    {
        if (this.enabled == enabled)
            return;

        this.enabled = enabled;
        if (enabled)
        {
            this.agentLifecycle.RegisterListener(AgentEvent.PreClassJobChange, this.OnClassJobChange);
            return;
        }

        this.agentLifecycle.UnregisterListener(AgentEvent.PreClassJobChange, this.OnClassJobChange);
    }

    public void Dispose()
    {
        this.SetEnabled(false);
    }

    private void OnClassJobChange(AgentEvent type, AgentArgs args)
    {
        if (args is not AgentClassJobChangeArgs classJobChangeArgs)
            return;

        if (!this.configuration.UnlockCrafters && IsCrafterClassJob(classJobChangeArgs.ClassJobId))
        {
            args.PreventOriginal();
            this.toastGui.ShowError("Locked: crafters");
            return;
        }

        if (!this.configuration.UnlockGatherers && IsGathererClassJob(classJobChangeArgs.ClassJobId))
        {
            args.PreventOriginal();
            this.toastGui.ShowError("Locked: gatherers");
        }
    }

    private static bool IsCrafterClassJob(byte classJobId)
        => classJobId is >= 8 and <= 15;

    private static bool IsGathererClassJob(byte classJobId)
        => classJobId is >= 16 and <= 18;
}