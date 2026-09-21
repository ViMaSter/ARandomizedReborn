using System;
using System.Numerics;
using Dalamud.Game.Gui.Toast;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace ARandomizedReborn.Windows;

public class ConfigWindow : Window, IDisposable
{
    private static readonly int[] SkillLevelCaps =
    [15, 16, 17, 20, 24, 28, 32, 35, 38, 41, 44, 47, 50, 51, 53, 55, 57, 59, 61, 63,
     65, 67, 69, 71, 73, 75, 77, 79, 81, 83, 85, 87, 89, 91, 93, 95, 97, 99, 100];

    private readonly Plugin plugin;
    private readonly Configuration configuration;

    // We give this window a constant ID using ###.
    // This allows for labels to be dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(Plugin plugin) : base("A Randomized Reborn - Settings###ARandomizedRebornConfig")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse;

        Size = new Vector2(520, 620);
        SizeCondition = ImGuiCond.Always;

        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        // Flags must be added or removed before Draw() is being called, or they won't apply
        if (configuration.IsConfigWindowMovable)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
        }
    }

    public override void Draw()
    {
        var movable = configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Movable Config Window", ref movable))
        {
            configuration.IsConfigWindowMovable = movable;
            configuration.Save();
        }

        var enableRandomizer = configuration.EnableRandomizer;
        if (ImGui.Checkbox("Enable Randomizer", ref enableRandomizer))
        {
            this.plugin.SetRandomizerEnabled(enableRandomizer);
        }

        var disableControls = !configuration.EnableRandomizer;
        if (disableControls)
            ImGui.BeginDisabled();

        var highlightRed = configuration.SprintHighlightRed;
        if (ImGui.SliderInt("Sprint highlight red", ref highlightRed, 0, 255))
        {
            configuration.SprintHighlightRed = highlightRed;
            this.plugin.SprintBlocker.HighlightRed = highlightRed;
            configuration.Save();
        }

        var highlightMultiply = configuration.SprintHighlightMultiply;
        if (ImGui.SliderInt("Sprint highlight multiply", ref highlightMultiply, 0, 100))
        {
            configuration.SprintHighlightMultiply = highlightMultiply;
            this.plugin.SprintBlocker.HighlightMultiply = highlightMultiply;
            configuration.Save();
        }

        if (ImGui.Button("Restore defaults"))
        {
            configuration.SprintHighlightRed = 90;
            configuration.SprintHighlightMultiply = 30;
            this.plugin.SprintBlocker.HighlightRed = 90;
            this.plugin.SprintBlocker.HighlightMultiply = 30;
            configuration.Save();
        }

        ImGui.Text("Lock skills above level");
        for (var index = 0; index < SkillLevelCaps.Length; index++)
        {
            var level = SkillLevelCaps[index];
            if (index % 4 != 0)
                ImGui.SameLine();

            if (ImGui.RadioButton($"{level}##skill-level-{level}", configuration.SkillLevelCap == level))
            {
                configuration.SkillLevelCap = level;
                this.plugin.SprintBlocker.SkillLevelCap = level;
                configuration.Save();
            }
        }

        if (disableControls)
            ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.Text("Client output tests");

        if (ImGui.Button("Chat"))
            Plugin.ChatGui.Print("TEST MESSAGE");

        ImGui.SameLine();
        if (ImGui.Button("Log"))
            Plugin.Log.Information("TEST MESSAGE");

        ImGui.SameLine();
        if (ImGui.Button("ShowNormal"))
            Plugin.ToastGui.ShowNormal("TEST MESSAGE");

        ImGui.SameLine();
        if (ImGui.Button("ShowError"))
            Plugin.ToastGui.ShowError("TEST MESSAGE");

        ImGui.SameLine();
        if (ImGui.Button("Big display hint"))
            Plugin.ToastGui.ShowQuest("TEST MESSAGE", new QuestToastOptions { PlaySound = true });

        ImGui.SameLine();
        if (ImGui.Button("Notification"))
        {
            Plugin.NotificationManager.AddNotification(new Notification
            {
                Content = "TEST MESSAGE",
                Type = NotificationType.Info,
            });
        }

        ImGui.Text("Chat chimes");
        for (uint chimeId = 1; chimeId <= 16; chimeId++)
        {
            if (chimeId > 1 && (chimeId - 1) % 4 != 0)
                ImGui.SameLine();

            if (ImGui.Button($"Chime {chimeId}"))
                UIGlobals.PlayChatSoundEffect(chimeId);
        }
    }
}
