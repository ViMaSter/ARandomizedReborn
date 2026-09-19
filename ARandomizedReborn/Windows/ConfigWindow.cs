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
    private readonly Configuration configuration;

    // We give this window a constant ID using ###.
    // This allows for labels to be dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(Plugin plugin) : base("A Wonderful Configuration Window###With a constant ID")
    {
        Flags = ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse;

        Size = new Vector2(420, 420);
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
        // Can't ref a property, so use a local copy
        var configValue = configuration.SomePropertyToBeSavedAndWithADefault;
        if (ImGui.Checkbox("Random Config Bool", ref configValue))
        {
            configuration.SomePropertyToBeSavedAndWithADefault = configValue;
            // Can save immediately on change if you don't want to provide a "Save and Close" button
            configuration.Save();
        }

        var movable = configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Movable Config Window", ref movable))
        {
            configuration.IsConfigWindowMovable = movable;
            configuration.Save();
        }

        ImGui.Separator();
        ImGui.Text("Client output tests");

        if (ImGui.Button("Chat"))
            Plugin.ChatGui.Print("TEST MESSAGE");

        ImGui.SameLine();
        if (ImGui.Button("Log"))
            Plugin.Log.Information("TEST MESSAGE");

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
