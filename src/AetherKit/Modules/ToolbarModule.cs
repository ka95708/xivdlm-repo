using System.Collections.Generic;
using AetherKit.Game;
using AetherKit.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AetherKit.Modules;

/// <summary>
/// 工具列模組：畫面上一條工具列、滑過展開的面板、編輯模式、快捷鍵、從遊戲拖曳進面板，
/// 以及跟著工具列項目狀態變化的游標提示、決定工具列何時顯示的條件組。
/// 這些都是作用在工具列與面板的項目上，拆成不同模組反而不好用。
///
/// 物件在插件載入時就建好，啟用與否只決定要不要加視窗、聽按鍵；
/// 其他地方拿 plugin.Toolbar 的東西永遠不會是 null。
/// </summary>
public sealed class ToolbarModule : IModule
{
    private readonly Plugin plugin;
    private readonly TopBarWindow topBar;
    private readonly CursorSettings cursorSettings;

    public ToolbarModule(Plugin plugin)
    {
        this.plugin = plugin;
        Hotkeys = new HotkeyManager(plugin);
        Panels = new PanelWindow(plugin);
        Conditions = new ConditionEditor(plugin);
        topBar = new TopBarWindow(plugin);
        cursorSettings = new CursorSettings(plugin);
    }

    public string Id => "toolbar";
    public string Name => "工具列";
    public string Description => "畫面上一條工具列，滑過展開面板；換套裝、做情感動作、執行指令，可設開關狀態、快捷鍵與游標提示。";

    public HotkeyManager Hotkeys { get; }
    public GameDragDrop GameDrag { get; } = new();
    public PanelWindow Panels { get; }
    public ConditionEditor Conditions { get; }

    /// <summary>
    /// 編輯模式：面板釘住不關、顯示外框與縮放把手、右鍵開編輯選單、可以拖曳排版。
    /// 按鈕照常執行 — 編輯操作都在右鍵與拖曳上，左鍵單擊不拿來編輯，改完可以直接點來測。
    /// 刻意不存進設定檔 — 存了的話，重開遊戲面板會一直釘在畫面上。
    /// </summary>
    public bool EditMode { get; set; }

    public void Enable(WindowSystem windows)
    {
        windows.AddWindow(topBar);
        windows.AddWindow(Panels);
        Hotkeys.Start();
    }

    public void Disable(WindowSystem windows)
    {
        Panels.IsOpen = false;
        windows.RemoveWindow(topBar);
        windows.RemoveWindow(Panels);
        Hotkeys.Stop();
        EditMode = false;
    }

    /// <summary>
    /// 視窗畫之前：拖曳狀態要先更新，視窗才知道這一幀要不要讓滑鼠穿過去；
    /// ImGui 是否在打字只能在繪製執行緒問，快照起來給在遊戲迴圈裡跑的快捷鍵用。
    /// </summary>
    public void BeforeWindows()
    {
        var io = ImGui.GetIO();
        Hotkeys.ImGuiWantsKeyboard = io.WantCaptureKeyboard || io.WantTextInput;
        GameDrag.Update(plugin);
    }

    /// <summary>
    /// 視窗畫完之後：面板要不要收起來，等所有視窗都回報過「有沒有被滑過」才判斷；
    /// 游標提示畫在最上層，不會被視窗蓋住。
    /// </summary>
    public void AfterWindows()
    {
        Panels.Tick();

        if (Services.ClientState.IsLoggedIn)
            CursorOverlay.Draw(plugin);
    }

    public void DrawHeader()
    {
        ImGui.SameLine();

        var edit = EditMode;
        if (ImGui.Checkbox("編輯模式", ref edit))
            EditMode = edit;

        ImGui.SameLine();
        ImGui.TextDisabled("(?)");

        if (ImGui.IsItemHovered())
            DrawEditHelp();
    }

    public IEnumerable<ModuleTab> SettingsTabs
    {
        get
        {
            yield return new ModuleTab(CursorTab, cursorSettings.Draw);
            yield return new ModuleTab(ConditionsTab, Conditions.Draw);
        }
    }

    public const string CursorTab = "游標提示";
    public const string ConditionsTab = "條件組";

    public bool HasSettings(Configuration config) =>
        config.TopBar.Items.Count > 0 || config.ConditionSets.Count > 0 || config.Cursor.Indicators.Count > 0;

    /// <summary>
    /// 工具列、條件組、游標提示一起搬：游標提示跟著工具列的項目、工具列用條件組決定何時顯示，
    /// 拆開搬會有一邊找不到另一邊。
    /// </summary>
    public void CopySettings(Configuration from, Configuration to)
    {
        to.TopBar = from.TopBar;
        to.ConditionSets = from.ConditionSets;
        to.Cursor = from.Cursor;
    }

    /// <summary>編輯模式下能做什麼。全部都在畫面上做，這裡只是說明。</summary>
    private static void DrawEditHelp()
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted("編輯模式下面板釘住不關，直接在畫面上排版。左鍵點擊照常執行，改完可以直接測：");
        ImGui.Spacing();
        ImGui.TextUnformatted("工具列");
        ImGui.BulletText("卡片上右鍵：名稱、圖示、狀態、類型、顯示方式、快捷鍵、刪除");
        ImGui.BulletText("卡片選單的「工具列 ▸」：在左邊／右邊新增、顯示條件（空白處右鍵也可以）");
        ImGui.BulletText("左鍵拖曳卡片：調整順序；右鍵拖曳：移動整條");
        ImGui.Spacing();
        ImGui.TextUnformatted("面板");
        ImGui.BulletText("空白處右鍵：新增分區（加在按右鍵的位置）");
        ImGui.BulletText("分區內右鍵：新增項目、標題、項目寬度、刪除");
        ImGui.BulletText("項目上右鍵：名稱、圖示、灰階、指令、開關狀態、快捷鍵");
        ImGui.BulletText("拖分區空白處：換位置；拖到欄與欄之間或最右邊開新欄");
        ImGui.BulletText("拖職能標題：調整套裝列表的職能擺法（可換欄）");
        ImGui.BulletText("拖右下角：調整面板或分區大小，連點兩下縮到剛好");
        ImGui.EndTooltip();
    }

    public void Dispose() => Hotkeys.Stop();
}
