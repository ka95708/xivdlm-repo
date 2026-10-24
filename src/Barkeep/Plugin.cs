using System;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Barkeep.Game;
using Barkeep.UI;

namespace Barkeep;

public sealed class Plugin : IDalamudPlugin
{
    private const string MainCommand = "/barkeep";
    private const string ShortCommand = "/bk";
    private const int VkLeftButton = 0x01;

    public static Plugin Instance { get; private set; } = null!;

    public ConfigStore Store { get; }
    public Configuration Config => Store.Config;
    public CommandExecutor Executor { get; }
    public HotkeyManager Hotkeys { get; }
    public GameDragDrop GameDrag { get; } = new();
    public PanelWindow Panels { get; }

    private readonly WindowSystem windows = new("Barkeep");
    private readonly ConfigWindow configWindow;
    private readonly IconBrowser iconBrowser;
    private readonly TopBarWindow topBar;

    /// <summary>
    /// 編輯模式：面板釘住不關、顯示外框與縮放把手、右鍵開編輯選單、可以拖曳排版。
    /// 按鈕照常執行 — 編輯操作都在右鍵與拖曳上，左鍵單擊不拿來編輯，改完可以直接點來測。
    /// 刻意不存進設定檔 — 存了的話，重開遊戲面板會一直釘在畫面上。
    /// </summary>
    public bool EditMode { get; set; }

    /// <summary>ImGui 的狀態只能在繪製執行緒問，快照起來給遊戲執行緒用。</summary>
    public int FrameCount { get; private set; }

    /// <summary>
    /// 這一幀剛按下左鍵。同時看 Win32 和 ImGui：點在遊戲畫面上時
    /// ImGui 不一定收得到那次點擊，但 Win32 一定看得到。
    /// </summary>
    public bool LeftClickedThisFrame { get; private set; }

    private bool leftWasDown;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Services>();
        Instance = this;

        Store = new ConfigStore();
        Executor = new CommandExecutor();
        Hotkeys = new HotkeyManager(this);

        configWindow = new ConfigWindow(this);
        iconBrowser = new IconBrowser(this);
        topBar = new TopBarWindow(this);
        Panels = new PanelWindow(this);

        windows.AddWindow(configWindow);
        windows.AddWindow(iconBrowser);
        windows.AddWindow(topBar);
        windows.AddWindow(Panels);

        Services.PluginInterface.UiBuilder.Draw += OnDraw;
        Services.PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        Services.PluginInterface.UiBuilder.OpenMainUi += OpenConfig;

        Services.Commands.AddHandler(MainCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "開啟 Barkeep 設定視窗。／barkeep edit 切換編輯模式。"
        });
        Services.Commands.AddHandler(ShortCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "／barkeep 的縮寫。",
            ShowInHelp = false
        });
    }

    public void Save() => Store.Touch();

    public void OpenConfig() => configWindow.Toggle();

    public void OpenConditions(ConditionSet? set) => configWindow.OpenConditions(set);

    /// <summary>
    /// 匯入取代了部分設定之後：開著的面板與正在設定的快捷鍵指著的是舊物件，先收掉，
    /// 下一幀畫面就會照新的設定重畫。
    /// </summary>
    public void AfterImport()
    {
        Panels.IsOpen = false;
        Hotkeys.Capturing = null;
        Save();
    }

    public void OpenIconBrowser(ActionItem item) => iconBrowser.OpenFor(item);

    public void OpenIconBrowser(string label, Func<uint> get, Action<uint> set) =>
        iconBrowser.OpenFor(label, get, set);

    /// <summary>執行工具列按鈕。面板項目不在這裡執行（它是滑過展開）。</summary>
    public void Execute(TopBarItem item)
    {
        if (item.Kind == ItemKind.Button)
            Press(item);
    }

    /// <summary>
    /// 按下一個有狀態的按鈕：執行目前狀態的指令，多狀態的話換下一個。
    /// 工具列按鈕、快捷鍵、面板裡的自訂項目都走這裡，行為才會一致。
    /// </summary>
    public void Press(StatefulItem item)
    {
        Executor.Enqueue(item.Current.Command);

        if (!item.Cycles) return;

        item.Advance();
        Save();
    }

    private void OnDraw()
    {
        var io = ImGui.GetIO();
        Hotkeys.ImGuiWantsKeyboard = io.WantCaptureKeyboard || io.WantTextInput;
        FrameCount = ImGui.GetFrameCount();

        var leftDown = (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0
                       || ImGui.IsMouseDown(ImGuiMouseButton.Left);

        LeftClickedThisFrame = leftDown && !leftWasDown;
        leftWasDown = leftDown;

        GameDrag.Update(this);
        windows.Draw();
        Panels.Tick();

        if (Services.ClientState.IsLoggedIn)
            CursorOverlay.Draw(this);
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("edit", StringComparison.OrdinalIgnoreCase))
        {
            EditMode = !EditMode;
            Services.Chat.Print(EditMode ? "[Barkeep] 已進入編輯模式。" : "[Barkeep] 已離開編輯模式。");
            return;
        }

        OpenConfig();
    }

    public void Dispose()
    {
        Services.Commands.RemoveHandler(MainCommand);
        Services.Commands.RemoveHandler(ShortCommand);

        Services.PluginInterface.UiBuilder.Draw -= OnDraw;
        Services.PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        Services.PluginInterface.UiBuilder.OpenMainUi -= OpenConfig;

        windows.RemoveAllWindows();
        Icons.Dispose();
        Hotkeys.Dispose();
        Executor.Dispose();
        Store.Dispose();
    }
}
