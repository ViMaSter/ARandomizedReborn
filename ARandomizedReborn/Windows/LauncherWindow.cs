using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace ARandomizedReborn.Windows;

/// <summary>Floating open-world button, similar to a duty action, that opens the bingo board.</summary>
public class LauncherWindow : Window
{
    private const uint IconId = 60071;
    private const float ButtonSize = 44f;
    private const float ClickDragThreshold = 3f;

    private readonly Plugin plugin;
    private Vector2? pressPosition;

    public LauncherWindow(Plugin plugin)
        : base("A Randomized Reborn Launcher###ARandomizedRebornLauncher",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDocking)
    {
        this.plugin = plugin;

        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        AllowPinning = false;
        AllowClickthrough = false;
        Position = new Vector2(100, 300);
        PositionCondition = ImGuiCond.FirstUseEver;
    }

    public override bool DrawConditions()
    {
        if (Plugin.ObjectTable.LocalPlayer == null || Plugin.GameGui.GameUiHidden)
            return false;

        if (Plugin.ClientState.IsPvP)
            return false;

        var condition = Plugin.Condition;
        return !condition[ConditionFlag.BoundByDuty] && !condition[ConditionFlag.BoundByDuty56] &&
               !condition[ConditionFlag.BoundByDuty95] && !condition[ConditionFlag.OccupiedInCutSceneEvent] &&
               !condition[ConditionFlag.WatchingCutscene] && !condition[ConditionFlag.WatchingCutscene78] &&
               !condition[ConditionFlag.BetweenAreas] && !condition[ConditionFlag.BetweenAreas51];
    }

    public override void PreDraw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4, 4));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
    }

    public override void PostDraw() => ImGui.PopStyleVar(2);

    public override void Draw()
    {
        var inCombat = Plugin.Condition[ConditionFlag.InCombat];
        var size = new Vector2(ButtonSize * ImGuiHelpers.GlobalScale);
        var texture = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(IconId)).GetWrapOrEmpty();
        var tint = inCombat ? new Vector4(0.4f, 0.4f, 0.4f, 0.8f) : Vector4.One;

        // A plain image is not an interactive item, so holding the mouse on it moves the window natively.
        ImGui.Image(texture.Handle, size, Vector2.Zero, Vector2.One, tint);
        var hovered = ImGui.IsItemHovered();
        var mousePosition = ImGui.GetMousePos();

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            this.pressPosition = mousePosition;

        if (this.pressPosition is { } pressed && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            this.pressPosition = null;
            if (hovered && !inCombat && Vector2.Distance(pressed, mousePosition) < ClickDragThreshold * ImGuiHelpers.GlobalScale)
                plugin.OpenBingoBoard();
        }

        if (hovered && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            ImGui.SetTooltip(inCombat ? "A Randomized Reborn\nUnavailable in combat." : "A Randomized Reborn\nClick to open. Hold and drag to move.");
    }
}
