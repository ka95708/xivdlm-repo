using Dalamud;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MgAl2O4.Utils;
using System;

namespace TriadBuddyPlugin
{
    public class Plugin : IDalamudPlugin
    {
        public string Name => "Triad Buddy";

        private readonly WindowSystem windowSystem = new("TriadBuddy");

        private readonly PluginWindowStatus statusWindow;
        private readonly CommandInfo statusCommand;

        private readonly UIReaderTriadGame uiReaderGame;
        private readonly UIReaderTriadPrep uiReaderPrep;
        private readonly UIReaderTriadCardList uiReaderCardList;
        private readonly UIReaderTriadDeckEdit uiReaderDeckEdit;
        private readonly StatTracker statTracker;
        private readonly GameDataLoader dataLoader;
        private readonly UIReaderScheduler uiReaderScheduler;
        private readonly PluginOverlays overlays;
        private readonly AutoTriad autoTriad;
        private readonly NpcTravel npcTravel = new();
        private readonly Localization locManager;

        public static Localization? CurrentLocManager;
        private string[] supportedLangCodes = { "de", "en", "es", "fr", "ja", "ko", "zh" };

        [PluginService] internal static IDalamudPluginInterface pluginInterface { get; private set; } = null!;

        public Plugin()
        {
            pluginInterface.Create<Service>();
#if DEBUG
            MgAl2O4.Utils.Logger.logger = Service.logger;
#endif // DEBUG

            Service.plugin = this;
            Service.pluginInterface = pluginInterface;
            Service.pluginConfig = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

            // prep utils
            var myAssemblyName = GetType().Assembly.GetName().Name;
            locManager = new Localization($"{myAssemblyName}.assets.loc.", "", true);            // res stream format: TriadBuddy.assets.loc.en.json
            locManager.SetupWithLangCode(pluginInterface.UiLanguage);
            CurrentLocManager = locManager;

            dataLoader = new GameDataLoader();
            dataLoader.StartAsyncWork();

            SolverUtils.CreateSolvers();
            if (Service.pluginConfig != null && Service.pluginConfig.CanUseProfileReader && SolverUtils.solverPreGameDecks != null)
            {
                SolverUtils.solverPreGameDecks.profileGS = new UnsafeReaderProfileGS();
            }

            statTracker = new StatTracker();

            // prep data scrapers
            uiReaderGame = new UIReaderTriadGame();
            uiReaderGame.OnUIStateChanged += (state) => { if (state != null) { SolverUtils.solverGame?.UpdateGame(state); } };

            uiReaderPrep = new UIReaderTriadPrep();
            uiReaderPrep.shouldScanDeckData = (SolverUtils.solverPreGameDecks?.profileGS == null) || SolverUtils.solverPreGameDecks.profileGS.HasErrors;
            uiReaderPrep.OnUIStateChanged += (state) => SolverUtils.solverPreGameDecks?.UpdateDecks(state);

            uiReaderCardList = new UIReaderTriadCardList();
            uiReaderDeckEdit = new UIReaderTriadDeckEdit();

            var uiReaderMatchResults = new UIReaderTriadResults();
            uiReaderMatchResults.OnUpdated += (state) => { if (SolverUtils.solverGame != null) { statTracker.OnMatchFinished(SolverUtils.solverGame, state); } };

            uiReaderScheduler = new UIReaderScheduler(Service.gameGui);
            uiReaderScheduler.AddObservedAddon(uiReaderGame);
            uiReaderScheduler.AddObservedAddon(uiReaderPrep.uiReaderMatchRequest);
            uiReaderScheduler.AddObservedAddon(uiReaderPrep.uiReaderDeckSelect);
            uiReaderScheduler.AddObservedAddon(uiReaderCardList);
            uiReaderScheduler.AddObservedAddon(uiReaderDeckEdit);
            uiReaderScheduler.AddObservedAddon(uiReaderMatchResults);

            autoTriad = new AutoTriad(uiReaderGame, uiReaderPrep, uiReaderMatchResults);

            var memReaderTriadFunc = new UnsafeReaderTriadCards();
            GameCardDB.Get().memReader = memReaderTriadFunc;
            GameNpcDB.Get().memReader = memReaderTriadFunc;

            uiReaderDeckEdit.unsafeDeck = new UnsafeReaderTriadDeck();

            // prep UI
            overlays = new PluginOverlays(uiReaderGame, uiReaderPrep);
            statusWindow = new PluginWindowStatus(uiReaderGame, uiReaderPrep, new PluginPanelAuto(autoTriad), new PluginPanelCollection(npcTravel));
            windowSystem.AddWindow(statusWindow);

            var npcStatsWindow = new PluginWindowNpcStats(statTracker);
            var deckOptimizerWindow = new PluginWindowDeckOptimize(uiReaderDeckEdit);
            var deckEvalWindow = new PluginWindowDeckEval(uiReaderPrep, deckOptimizerWindow, npcStatsWindow);
            deckOptimizerWindow.OnConfigRequested += () => OnOpenConfig();
            windowSystem.AddWindow(deckEvalWindow);
            windowSystem.AddWindow(deckOptimizerWindow);
            windowSystem.AddWindow(npcStatsWindow);

            windowSystem.AddWindow(new PluginWindowCardInfo(uiReaderCardList));
            windowSystem.AddWindow(new PluginWindowCardSearch(uiReaderCardList, npcStatsWindow));
            windowSystem.AddWindow(new PluginWindowDeckSearch(uiReaderDeckEdit));

            // prep plugin hooks
            statusCommand = new(OnCommand) { HelpMessage = string.Format(Localization.Localize("Cmd_Status", "Show state of {0} plugin"), Name) + AutoCommandHelp };
            Service.commandManager.AddHandler("/triadbuddy", statusCommand);

            pluginInterface.LanguageChanged += OnLanguageChanged;
            pluginInterface.UiBuilder.Draw += OnDraw;
            pluginInterface.UiBuilder.OpenConfigUi += OnOpenConfig;
            pluginInterface.UiBuilder.OpenMainUi += () => OnCommand("", "");

            Service.framework.Update += Framework_Update;
        }

        private void OnLanguageChanged(string langCode)
        {
            // check if resource is available, will cause exception if trying to load empty json
            if (Array.Find(supportedLangCodes, x => x == langCode) != null)
            {
                locManager.SetupWithLangCode(langCode);
            }
            else
            {
                locManager.SetupWithFallbacks();
            }

            statusCommand.HelpMessage = string.Format(Localization.Localize("Cmd_Status", "Show state of {0} plugin"), Name) + AutoCommandHelp;
        }

        private const string AutoCommandHelp = "\n/triadbuddy auto → 自動對戰分頁\n/triadbuddy cards → 卡片收集分頁";

        public void Dispose()
        {
            Service.commandManager.RemoveHandler("/triadbuddy");
            Service.framework.Update -= Framework_Update;
            windowSystem.RemoveAllWindows();
            autoTriad.Dispose();
            npcTravel.Dispose();
        }

        private void OnCommand(string command, string args)
        {
            var tab = args.Trim().ToLowerInvariant() switch
            {
                "auto" => PluginWindowStatus.Tab.Auto,
                "cards" => PluginWindowStatus.Tab.Collection,
                "config" => PluginWindowStatus.Tab.Config,
                _ => PluginWindowStatus.Tab.Status,
            };

            statusWindow.Toggle(tab);
        }

        private void OnDraw()
        {
            windowSystem.Draw();
            overlays.OnDraw();
        }

        private void OnOpenConfig()
        {
            statusWindow.OpenTab(PluginWindowStatus.Tab.Config);
        }

        private void Framework_Update(IFramework framework)
        {
            try
            {
                if (dataLoader.IsDataReady)
                {
                    float deltaSeconds = (float)framework.UpdateDelta.TotalSeconds;
                    uiReaderScheduler.Update(deltaSeconds);
                    autoTriad.Update();
                    npcTravel.Update();
                }
            }
            catch (Exception ex)
            {
                Service.logger.Error(ex, "state update failed");
            }
        }
    }
}
