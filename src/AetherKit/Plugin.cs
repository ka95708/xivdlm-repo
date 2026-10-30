using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using AetherKit.Game;
using AetherKit.Modules;
using AetherKit.UI;

namespace AetherKit;

/// <summary>
/// 插件本體只放所有模組共用的基礎：設定檔、指令執行、管理視窗、圖示瀏覽器、模組的開關。
/// 功能都在 Modules 底下，各自決定要加哪些視窗、聽哪些事件。
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string MainCommand = "/aetherkit";
    private const string ShortCommand = "/ak";
    private const int VkLeftButton = 0x01;

    public static Plugin Instance { get; private set; } = null!;

    public ConfigStore Store { get; }
    public Configuration Config => Store.Config;
    public CommandExecutor Executor { get; }

    public ToolbarModule Toolbar { get; }

    /// <summary>所有模組，依管理視窗裡顯示的順序。新增模組就在這裡加一行。</summary>
    public IReadOnlyList<IModule> Modules { get; }

    private readonly HashSet<IModule> enabled = new();

    private readonly WindowSystem windows = new("AetherKit");
    private readonly ConfigWindow configWindow;
    private readonly IconBrowser iconBrowser;

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

        configWindow = new ConfigWindow(this);
        iconBrowser = new IconBrowser(this);
        windows.AddWindow(configWindow);
        windows.AddWindow(iconBrowser);

        Toolbar = new ToolbarModule(this);
        Modules = new IModule[]
        {
            Toolbar,
            new AutoLoginModule(this)
        };

        foreach (var module in Modules)
        {
            if (!Config.DisabledModules.Contains(module.Id))
                EnableModule(module);
        }

        Services.PluginInterface.UiBuilder.Draw += OnDraw;
        Services.PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        Services.PluginInterface.UiBuilder.OpenMainUi += OpenConfig;

        Services.Commands.AddHandler(MainCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "開啟 AetherKit 設定視窗。／aetherkit edit 切換工具列的編輯模式。"
        });
        Services.Commands.AddHandler(ShortCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "／aetherkit 的縮寫。",
            ShowInHelp = false
        });
    }

    public bool IsEnabled(IModule module) => enabled.Contains(module);

    /// <summary>管理視窗的模組開關。狀態存進設定檔，下次載入照著開。</summary>
    public void SetEnabled(IModule module, bool value)
    {
        if (value == IsEnabled(module)) return;

        if (value)
        {
            EnableModule(module);
            Config.DisabledModules.Remove(module.Id);
        }
        else
        {
            module.Disable(windows);
            enabled.Remove(module);

            if (!Config.DisabledModules.Contains(module.Id))
                Config.DisabledModules.Add(module.Id);
        }

        Save();
    }

    private void EnableModule(IModule module)
    {
        module.Enable(windows);
        enabled.Add(module);
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
        Toolbar.Panels.IsOpen = false;
        Toolbar.Hotkeys.Capturing = null;
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
        FrameCount = ImGui.GetFrameCount();

        var leftDown = (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0
                       || ImGui.IsMouseDown(ImGuiMouseButton.Left);

        LeftClickedThisFrame = leftDown && !leftWasDown;
        leftWasDown = leftDown;

        foreach (var module in Modules)
        {
            if (IsEnabled(module))
                module.BeforeWindows();
        }

        windows.Draw();

        foreach (var module in Modules)
        {
            if (IsEnabled(module))
                module.AfterWindows();
        }
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("edit", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsEnabled(Toolbar))
            {
                Services.Chat.PrintError("[AetherKit] 工具列模組沒有啟用，請先在設定的「模組」分頁打開。");
                return;
            }

            Toolbar.EditMode = !Toolbar.EditMode;
            Services.Chat.Print(Toolbar.EditMode ? "[AetherKit] 已進入編輯模式。" : "[AetherKit] 已離開編輯模式。");
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

        foreach (var module in Modules)
        {
            if (IsEnabled(module))
                module.Disable(windows);

            module.Dispose();
        }

        windows.RemoveAllWindows();
        Icons.Dispose();
        Executor.Dispose();
        Store.Dispose();
    }
}
