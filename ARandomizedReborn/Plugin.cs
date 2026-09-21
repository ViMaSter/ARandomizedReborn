using Dalamud.Game.Command;
using Dalamud.Game.Chat;
using Dalamud.Game;
using Dalamud.IoC;
using Dalamud.Plugin;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using Dalamud.Game.Text;
using ARandomizedReborn.Windows;

namespace ARandomizedReborn;

public sealed unsafe class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static INotificationManager NotificationManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IAgentLifecycle AgentLifecycle { get; private set; } = null!;

    private const string CommandName = "/randomizer";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("ARandomizedReborn");
    public SprintBlocker SprintBlocker { get; init; }
    public ObjectiveTracker ObjectiveTracker { get; init; }
    public BingoSession BingoSession { get; init; }
    public InteractionRestrictionManager InteractionRestrictionManager { get; init; }
    public UiRestrictionManager UiRestrictionManager { get; init; }
    public AgentRestrictionManager AgentRestrictionManager { get; init; }
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private BingoWindow BingoWindow { get; init; }
    private DebugWindow DebugWindow { get; init; }
    private readonly Dictionary<ushort, string> emoteNames = [];
    private readonly Dictionary<nint, ushort> targetedEmotes = [];

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        foreach (var row in DataManager.GetExcelSheet<Emote>(ClientLanguage.English))
            emoteNames[(ushort)row.RowId] = row.Name.ToString();

        SprintBlocker = new SprintBlocker(GameInteropProvider, DataManager, Log, ToastGui, Framework, GameGui)
        {
            IsEnabled = Configuration.EnableRandomizer,
            IsBlocking = Configuration.DisableSprint,
            UnlockSprint = Configuration.UnlockSprint,
            UnlockTeleportReturn = Configuration.UnlockTeleportReturn,
            UnlockMounts = Configuration.UnlockMounts,
            UnlockGatherers = Configuration.UnlockGatherers,
            UnlockCrafters = Configuration.UnlockCrafters,
            SkillLevelCap = Configuration.SkillLevelCap,
            HighlightRed = Configuration.SprintHighlightRed,
            HighlightMultiply = Configuration.SprintHighlightMultiply,
        };

        ObjectiveTracker = new ObjectiveTracker(GameInteropProvider, DataManager, TargetManager, Log, ReportCheck);
        ObjectiveTracker.SetEnabled(Configuration.EnableRandomizer);

        BingoSession = new BingoSession(Configuration, ApplyUnlockState, Notify);

        InteractionRestrictionManager = new InteractionRestrictionManager(GameInteropProvider, ToastGui, Configuration);
        InteractionRestrictionManager.SetEnabled(Configuration.EnableRandomizer);

        UiRestrictionManager = new UiRestrictionManager(AddonLifecycle, DataManager, Configuration);
        UiRestrictionManager.SetEnabled(Configuration.EnableRandomizer);

        AgentRestrictionManager = new AgentRestrictionManager(AgentLifecycle, ToastGui, Configuration);
        AgentRestrictionManager.SetEnabled(Configuration.EnableRandomizer);

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);
        BingoWindow = new BingoWindow(this);
        DebugWindow = new DebugWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(BingoWindow);
        WindowSystem.AddWindow(DebugWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the A Randomized Reborn window."
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        ChatGui.ChatMessageHandled += OnChatMessage;
        Framework.Update += OnFrameworkUpdate;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
    }

    public void Dispose()
    {
        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        ChatGui.ChatMessageHandled -= OnChatMessage;
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        BingoWindow.Dispose();
        DebugWindow.Dispose();
        SprintBlocker.Dispose();
        ObjectiveTracker.Dispose();
        BingoSession.Dispose();
        InteractionRestrictionManager.Dispose();
        UiRestrictionManager.Dispose();
        AgentRestrictionManager.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        // In response to the slash command, toggle the display status of our main ui
        MainWindow.Toggle();
    }

    private void OnChatMessage(IChatMessage message)
    {
        if (message.LogKind is XivChatType.NPCDialogue or XivChatType.NPCDialogueAnnouncements)
        {
            ToastGui.ShowQuest($"{message.Sender}: {message.Message}");
        }
    }

    private unsafe void OnFrameworkUpdate(IFramework _)
    {
        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null)
            return;

        var localPlayerId = (ulong)localPlayer.GameObjectId;
        var seenObjects = new HashSet<nint>();

        foreach (var gameObject in ObjectTable.CharacterManagerObjects)
        {
            if (gameObject.ObjectKind != DalamudObjectKind.Pc)
                continue;

            if (gameObject.Address == localPlayer.Address)
                continue;

            var character = (Character*)gameObject.Address;
            var emoteController = &character->EmoteController;
            var targetId = (ulong)emoteController->Target;
            var targetMatchesLocal = targetId == localPlayerId || targetId == localPlayer.EntityId;
            var emoteId = targetMatchesLocal ? emoteController->EmoteId : (ushort)0;
            var address = gameObject.Address;
            seenObjects.Add(address);

            if (emoteId != 0 && (!this.targetedEmotes.TryGetValue(address, out var previousEmote) || previousEmote != emoteId))
            {
                var emoteName = this.emoteNames.TryGetValue(emoteId, out var name) ? name : $"#{emoteId}";
                ToastGui.ShowQuest($"{gameObject.Name} used {emoteName} on you");
                this.targetedEmotes[address] = emoteId;
            }
            else if (emoteId == 0)
            {
                this.targetedEmotes.Remove(address);
            }
        }

        foreach (var address in this.targetedEmotes.Keys.Where(address => !seenObjects.Contains(address)).ToArray())
            this.targetedEmotes.Remove(address);
    }

    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
    public void ToggleBingoUi() => BingoWindow.Toggle();
    public void ToggleDebugUi() => DebugWindow.Toggle();

    public void StartNewSession(BingoDifficulty difficulty)
        => _ = BingoSession.StartNewSessionAsync(difficulty, action => Framework.RunOnFrameworkThread(action));

    public void ResetProgress() => BingoSession.ResetProgressOnly();

    public void CompleteCheckManually(string checkId) => BingoSession.TryCompleteCheck(checkId, manualOverride: true);

    /// <summary>Only counts automatic detections while the randomizer is actually running.</summary>
    private bool ReportCheck(string checkId)
        => Configuration.EnableRandomizer && BingoSession.TryCompleteCheck(checkId, manualOverride: false);

    private void Notify(string message)
    {
        ToastGui.ShowQuest(message);
        Log.Information(message);
    }

    public void SetRandomizerEnabled(bool enabled)
    {
        Configuration.EnableRandomizer = enabled;
        SprintBlocker.IsEnabled = enabled;
        ObjectiveTracker.SetEnabled(enabled);
        InteractionRestrictionManager.SetEnabled(enabled);
        UiRestrictionManager.SetEnabled(enabled);
        AgentRestrictionManager.SetEnabled(enabled);
        Configuration.Save();
    }

    public IReadOnlyList<UnlockState> GetUnlockStates()
    {
        var states = new List<UnlockState>(Unlocks.Definitions.Count);
        foreach (var definition in Unlocks.Definitions)
            states.Add(new UnlockState(definition, Unlocks.Get(Configuration, definition.Key)));

        return states;
    }

    public void SetUnlockState(UnlockKey key, bool unlocked)
    {
        Unlocks.Set(Configuration, key, unlocked);
        this.ApplyUnlockState(key, unlocked);
        Configuration.Save();
    }

    private void ApplyUnlockState(UnlockKey key, bool unlocked)
    {
        switch (key)
        {
            case UnlockKey.Sprint:
                SprintBlocker.UnlockSprint = unlocked;
                SprintBlocker.IsBlocking = !unlocked;
                break;
            case UnlockKey.TeleportReturn:
                SprintBlocker.UnlockTeleportReturn = unlocked;
                break;
            case UnlockKey.Mounts:
                SprintBlocker.UnlockMounts = unlocked;
                break;
            case UnlockKey.Gatherers:
                SprintBlocker.UnlockGatherers = unlocked;
                break;
            case UnlockKey.Crafters:
                SprintBlocker.UnlockCrafters = unlocked;
                break;
        }
    }
}
