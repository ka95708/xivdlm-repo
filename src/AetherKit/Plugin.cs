using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using AetherKit.Core;
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

    public PluginLogModule PluginLog { get; }

    public AutoFateModule AutoFate { get; }

    public TweaksModule Tweaks { get; }

    /// <summary>貼在遊戲視窗旁的按鈕列，小功能和自動流程共用（模組在 AfterWindows 登記按鈕）。</summary>
    public Tweaks.AddonBar AddonBar { get; } = new();

    public BlueMageVaultFarmModule VaultFarm { get; }
    public TsaiTouVounouLeveModule TsaiTouVounouLeve { get; }
    public IslandGatherModule IslandGather { get; }

    public RetainerWorkModule Retainers { get; }

    public HideOthersModule HideOthers { get; }

    public CraftworksModule Craftworks { get; }

    /// <summary>所有功能共用的底層能力（對話推進、自動確認、修理、操作鎖……）。</summary>
    public CoreServices Core { get; }

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
        Core = new CoreServices(this);

        configWindow = new ConfigWindow(this);
        iconBrowser = new IconBrowser(this);
        windows.AddWindow(configWindow);
        windows.AddWindow(iconBrowser);

        Toolbar = new ToolbarModule(this);
        Modules = new IModule[]
        {
            Toolbar,
            new AutoLoginModule(this),
            new OwnershipModule(this),
            PluginLog = new PluginLogModule(this),
            Tweaks = new TweaksModule(this),
            AutoFate = new AutoFateModule(this),
            VaultFarm = new BlueMageVaultFarmModule(this),
            TsaiTouVounouLeve = new TsaiTouVounouLeveModule(this),
            IslandGather = new IslandGatherModule(this),
            Retainers = new RetainerWorkModule(this),
            HideOthers = new HideOthersModule(this),
            Craftworks = new CraftworksModule(this),
            new CurrencyDisplayModule(this)
        };

        foreach (var module in Modules)
        {
            if (WantsEnabled(module))
                EnableModule(module);
        }

        Services.PluginInterface.UiBuilder.Draw += OnDraw;
        Services.PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
        Services.PluginInterface.UiBuilder.OpenMainUi += OpenConfig;

        Services.Commands.AddHandler(MainCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "開啟 AetherKit 設定視窗。／aetherkit edit 切換工具列的編輯模式，／aetherkit log 開啟插件訊息紀錄，／aetherkit fate 開始或停止自動Fate，／aetherkit vault 開始或停止青魔教皇，／aetherkit leve 開始或停止高山茶理符，／aetherkit island 開始或停止無人島採集，／aetherkit hide [on|off] 啟用或停用隱藏其他人，／aetherkit repair 修理裝備，／aetherkit tweak 名稱 [on|off] 開關小功能，／aetherkit module 模組 [on|off] 啟用或停用模組，／aetherkit core 能力 [on|off] 讓核心能力（例如 talk、cutscene）一直開著。"
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
        if (value == IsEnabled(module) || (!value && !module.CanDisable)) return;

        if (value)
        {
            EnableModule(module);
            Config.DisabledModules.Remove(module.Id);
            if (module.OffByDefault && !Config.EnabledModules.Contains(module.Id))
                Config.EnabledModules.Add(module.Id);
        }
        else
        {
            module.Disable(windows);
            enabled.Remove(module);
            Core.ReleaseAll(module.Id);

            if (!Config.DisabledModules.Contains(module.Id))
                Config.DisabledModules.Add(module.Id);
            Config.EnabledModules.Remove(module.Id);
        }

        Save();
    }

    /// <summary>載入時要不要開：不能關的一律開；預設關著的看有沒有被打開過；其他看有沒有被關掉過。</summary>
    private bool WantsEnabled(IModule module) =>
        !module.CanDisable
        || (module.OffByDefault
            ? Config.EnabledModules.Contains(module.Id)
            : !Config.DisabledModules.Contains(module.Id));

    private void EnableModule(IModule module)
    {
        module.Enable(windows);
        enabled.Add(module);
    }

    public void Save() => Store.Touch();

    public void OpenConfig() => configWindow.Toggle();

    /// <summary>打開設定視窗的核心能力頁（模組頁上的「核心能力設定」按鈕用）。</summary>
    public void OpenCorePage() => configWindow.ShowCorePage();

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

    /// <summary>執行工具列按鈕、開關。面板項目不在這裡執行（它是滑過或點一下展開）。</summary>
    public void Execute(TopBarItem item)
    {
        if (item.Kind != ItemKind.Panel)
            Press(item);
    }

    /// <summary>
    /// 按下一個有狀態的按鈕：執行目前狀態的指令，多狀態的話換下一個。
    /// 工具列按鈕、快捷鍵、面板裡的自訂項目都走這裡，行為才會一致。
    /// </summary>
    public void Press(StatefulItem item)
    {
        // 不能按（條件不成立、讀不到開關狀態）就不做，快捷鍵也一樣
        if (UI.ToolbarItemState.BlockedReason(Config, item) is { } reason)
        {
            Services.Log.Debug($"略過 {item.Current.Label}：{reason}");
            return;
        }

        if (item.IsToggle)
        {
            PressToggle(item);
            return;
        }

        Executor.Enqueue(item.Current.Command);

        if (!item.Cycles) return;

        item.Advance();
        Save();
    }

    /// <summary>
    /// 開關：開著就執行關掉的指令，關著就執行打開的指令。
    /// 有狀態來源的不自己記，等遊戲或插件真的變了，下一幀畫面就跟著變。
    /// </summary>
    private void PressToggle(StatefulItem item)
    {
        var on = UI.ToolbarItemState.IsOn(item) == true;
        Executor.Enqueue(on ? item.OffCommand : item.Current.Command);

        if (item.StateSource != null) return;

        item.IsOn = !on;
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

        // 遊戲視窗上的按鈕列：每幀清空，各模組在 AfterWindows 登記，最後一起畫
        AddonBar.Clear();

        foreach (var module in Modules)
        {
            if (IsEnabled(module))
                module.AfterWindows();
        }

        if (Services.ClientState.IsLoggedIn)
            AddonBar.Draw();
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("edit", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsEnabled(Toolbar))
            {
                Notify.Reply("工具列模組沒有啟用，請先在設定的「模組」分頁打開。", true);
                return;
            }

            Toolbar.EditMode = !Toolbar.EditMode;
            Notify.Reply(Toolbar.EditMode ? "已進入編輯模式。" : "已離開編輯模式。");
            return;
        }

        if (args.Trim().Equals("log", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsEnabled(PluginLog))
            {
                Notify.Reply("插件訊息模組沒有啟用，請先在設定裡打開。", true);
                return;
            }

            PluginLog.OpenWindow();
            return;
        }

        // 自動流程一直開著（沒有啟用／停用），直接切換開始、停止
        if (args.Trim().Equals("fate", StringComparison.OrdinalIgnoreCase))
        {
            AutoFate.Farmer.Toggle();
            return;
        }

        if (args.Trim().Equals("vault", StringComparison.OrdinalIgnoreCase))
        {
            VaultFarm.Farmer.Toggle();
            return;
        }

        if (args.Trim().Equals("leve", StringComparison.OrdinalIgnoreCase))
        {
            TsaiTouVounouLeve.Runner.Toggle();
            return;
        }

        if (args.Trim().Equals("island", StringComparison.OrdinalIgnoreCase))
        {
            IslandGather.Runner.Toggle();
            return;
        }

        if (args.Trim().Equals("repair", StringComparison.OrdinalIgnoreCase))
        {
            Core.Repair.RunNow();
            return;
        }

        var words = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 1 && words[0].Equals("hide", StringComparison.OrdinalIgnoreCase))
        {
            HideCommand(words);
            return;
        }

        if (words.Length >= 2 && words[0].Equals("module", StringComparison.OrdinalIgnoreCase))
        {
            ModuleCommand(words);
            return;
        }

        if (words.Length >= 2 && words[0].Equals("tweak", StringComparison.OrdinalIgnoreCase))
        {
            TweakCommand(words);
            return;
        }

        if (words.Length >= 2 && words[0].Equals("core", StringComparison.OrdinalIgnoreCase))
        {
            CoreCommand(words);
            return;
        }

        OpenConfig();
    }

    /// <summary>指令最後一個字是 on／off 就照著設，否則切換。回傳 null = 切換。</summary>
    private static bool? Switch(string[] words, out string name)
    {
        var last = words[^1].ToLowerInvariant();
        bool? value = last switch
        {
            "on" or "開" => true,
            "off" or "關" => false,
            _ => null
        };

        name = string.Join(' ', words.Skip(1).Take(words.Length - (value == null ? 1 : 2)));
        return value;
    }

    /// <summary>/ak module Id或名稱 [on|off]：啟用或停用模組（能停用的才行），可以放進工具列開關。</summary>
    private void ModuleCommand(string[] words)
    {
        var value = Switch(words, out var name);
        var module = Modules.FirstOrDefault(m => m.Id.Equals(name, StringComparison.OrdinalIgnoreCase) || m.Name == name);
        if (module == null || !module.CanDisable)
        {
            Notify.Reply($"找不到能開關的模組「{name}」。可用：{string.Join("、", Modules.Where(m => m.CanDisable).Select(m => $"{m.Id}（{m.Name}）"))}", true);
            return;
        }

        var on = value ?? !IsEnabled(module);
        SetEnabled(module, on);
        Notify.Reply($"{module.Name}：{(on ? "已啟用" : "已停用")}");
    }

    /// <summary>/ak hide [on|off]：啟用（開始隱藏）或停用（顯示全部）「隱藏其他人」模組。</summary>
    private void HideCommand(string[] words)
    {
        var on = (words.Length >= 2 ? Switch(words, out _) : null) ?? !IsEnabled(HideOthers);
        SetEnabled(HideOthers, on);
        Notify.Reply($"隱藏其他人：{(on ? "已啟用（開始隱藏）" : "已停用（顯示全部）")}");
    }

    /// <summary>/ak tweak 名稱 [on|off]：開關小功能，可以放進工具列按鈕或快捷鍵。</summary>
    private void TweakCommand(string[] words)
    {
        var value = Switch(words, out var name);
        var tweak = Tweaks.Find(name);
        if (tweak == null)
        {
            Notify.Reply($"找不到小功能「{name}」。名稱可以只打一部分，但要能分辨出是哪一個。", true);
            return;
        }

        var on = value ?? !tweak.Enabled;
        Tweaks.SetTweak(tweak, on);
        Notify.Reply($"{tweak.Name}：{(on ? "已開啟" : "已關閉")}");
    }

    /// <summary>
    /// /ak core 能力 [on|off]：讓核心能力「一直開著」或回到「功能需要時才開」。
    /// 例如對話推進、過場跳過平常給 AutoFate 之類借用，想自己跑劇情時打開、跑完關回去。
    /// 關 = 回到需要時才開，不是完全停用，正在借用它的功能不受影響。
    /// </summary>
    private void CoreCommand(string[] words)
    {
        var value = Switch(words, out var name);
        var capability = Core.All.FirstOrDefault(c => c.Id.Equals(name, StringComparison.OrdinalIgnoreCase) || c.Name == name)
                         ?? Core.All.SingleOrDefault(c => c.Name.Contains(name));
        if (capability == null)
        {
            Notify.Reply($"找不到核心能力「{name}」。可用：{string.Join("、", Core.All.Select(c => $"{c.Id}（{c.Name}）"))}", true);
            return;
        }

        var on = value ?? capability.Mode != CapabilityMode.Always;
        capability.Mode = on ? CapabilityMode.Always : CapabilityMode.OnLease;
        Notify.Reply($"{capability.Name}：{(on ? "一直開著" : "功能需要時才開")}");
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

        Core.Dispose();
        FeatureLog.CloseAll();

        windows.RemoveAllWindows();
        Icons.Dispose();
        Executor.Dispose();
        Store.Dispose();
    }
}
