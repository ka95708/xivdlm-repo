using Dalamud.Game;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace TriadBuddyPlugin
{
    internal class Service
    {
        public static Plugin plugin = null!;
        public static IDalamudPluginInterface pluginInterface = null!;
        public static Configuration pluginConfig = null!;

        [PluginService]
        public static IDataManager dataManager { get; private set; } = null!;

        [PluginService]
        public static ICommandManager commandManager { get; private set; } = null!;

        [PluginService]
        public static ISigScanner sigScanner { get; private set; } = null!;

        [PluginService]
        public static IFramework framework { get; private set; } = null!;

        [PluginService]
        public static IGameGui gameGui { get; private set; } = null!;

        [PluginService]
        public static ITextureProvider textureProvider { get; private set; } = null!;

        [PluginService]
        public static IPluginLog logger { get; private set; } = null!;

        [PluginService]
        public static IChatGui chatGui { get; private set; } = null!;

        [PluginService]
        public static ICondition condition { get; private set; } = null!;

        [PluginService]
        public static IAddonLifecycle addonLifecycle { get; private set; } = null!;

        [PluginService]
        public static IGameInteropProvider gameInterop { get; private set; } = null!;

        [PluginService]
        public static IClientState clientState { get; private set; } = null!;

        [PluginService]
        public static IObjectTable objectTable { get; private set; } = null!;

        [PluginService]
        public static Dalamud.Game.ClientState.Objects.ITargetManager targetManager { get; private set; } = null!;
    }
}
