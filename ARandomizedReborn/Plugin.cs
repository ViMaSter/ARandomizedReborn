using Dalamud.Game.Command;
using Dalamud.Game.Chat;
using Dalamud.IoC;
using Dalamud.Hooking;
using Dalamud.Plugin;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using Dalamud.Game.Text;
using Dalamud.Utility;
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

    private const string CommandName = "/pmycommand";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("ARandomizedReborn");
    public SprintBlocker SprintBlocker { get; init; }
    public ObjectiveTracker ObjectiveTracker { get; init; }
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private Hook<TargetSystem.Delegates.InteractWithObject> InteractionHook { get; init; }
    private readonly Dictionary<ushort, string> emoteNames = [];
    private readonly Dictionary<nint, ushort> targetedEmotes = [];

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        foreach (var row in DataManager.GetExcelSheet<Emote>())
            emoteNames[(ushort)row.RowId] = row.Name.ToString();

        SprintBlocker = new SprintBlocker(GameInteropProvider, DataManager, Log, ToastGui, Framework, GameGui)
        {
            IsEnabled = Configuration.EnableRandomizer,
            IsBlocking = Configuration.DisableSprint,
            SkillLevelCap = Configuration.SkillLevelCap,
            HighlightRed = Configuration.SprintHighlightRed,
            HighlightMultiply = Configuration.SprintHighlightMultiply,
        };

        ObjectiveTracker = new ObjectiveTracker(GameInteropProvider, DataManager, TargetManager, ToastGui, Log, Configuration);
        ObjectiveTracker.SetEnabled(Configuration.EnableRandomizer);

        // You might normally want to embed resources and load them from the manifest stream
        var goatImagePath = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, "goat.png");

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this, goatImagePath);

        InteractionHook = GameInteropProvider.HookFromAddress<TargetSystem.Delegates.InteractWithObject>(
            TargetSystem.MemberFunctionPointers.InteractWithObject,
            OnInteractWithObject);
        InteractionHook.Enable();

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "A useful message to display in /xlhelp"
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

        // Add a simple message to the log with level set to information
        // Use /xllog to open the log window in-game
        // Example Output: 00:57:54.959 | INF | [SamplePlugin] ===A cool log message from Sample Plugin===
        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");
    }

    public void Dispose()
    {
        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        ChatGui.ChatMessageHandled -= OnChatMessage;
        Framework.Update -= OnFrameworkUpdate;
        InteractionHook.Dispose();
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        SprintBlocker.Dispose();
        ObjectiveTracker.Dispose();

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

    private unsafe ulong OnInteractWithObject(TargetSystem* targetSystem, GameObject* gameObject, bool checkLineOfSight)
    {
        var interactedObject = gameObject == null ? null : ObjectTable.CreateObjectReference((nint)gameObject);
        if (interactedObject?.ObjectKind == DalamudObjectKind.EventNpc)
        {
            var target = interactedObject.Name.ToString();
            if (!string.IsNullOrWhiteSpace(target))
                ToastGui.ShowQuest($"Interacting with {target}");
        }

        return InteractionHook.Original(targetSystem, gameObject, checkLineOfSight);
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();

    public void SetRandomizerEnabled(bool enabled)
    {
        Configuration.EnableRandomizer = enabled;
        SprintBlocker.IsEnabled = enabled;
        ObjectiveTracker.SetEnabled(enabled);
        Configuration.Save();
    }
}
