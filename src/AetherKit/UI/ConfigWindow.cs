using System;
using System.Linq;
using System.Numerics;
using AetherKit.Modules;
using AetherKit.Tweaks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AetherKit.UI;

/// <summary>
/// 管理視窗：左邊一欄清單，上半是模組（旁邊的點：功能表示有沒有啟用、自動流程表示正在執行），分隔線下面是所有模組共用的頁面；
/// 右邊是選到的那一頁。工具列與面板的內容一律在編輯模式下直接在畫面上右鍵編輯。
///
/// 這個視窗用 Dalamud 原生樣式，跟其他插件的設定視窗一致；
/// 主題色票只套用在工具列、面板與右鍵選單上。
/// </summary>
public class ConfigWindow : Window
{
    private const float NavWidth = 160f;

    // 共用頁的代號。模組頁用模組自己的 Id
    private const string CorePageId = "core";
    private const string ThemePage = "theme";
    private const string HotkeysPage = "hotkeys";
    private const string TransferPage = "transfer";

    private readonly Plugin plugin;
    private readonly HotkeySettings hotkeys;
    private readonly TransferSettings transfer;
    private readonly CorePage corePage;

    /// <summary>下一次畫模組頁時要切過去的分頁（從工具列選單按「管理條件組…」時用）。</summary>
    private string? requestedTab;

    public ConfigWindow(Plugin plugin) : base("AetherKit 設定###AetherKitConfig")
    {
        this.plugin = plugin;
        hotkeys = new HotkeySettings(plugin);
        transfer = new TransferSettings(plugin);
        corePage = new CorePage(plugin.Core);
        Size = new Vector2(820, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 380),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    /// <summary>直接打開工具列頁的條件組分頁，並選好那一組。</summary>
    public void OpenConditions(ConditionSet? set)
    {
        plugin.Toolbar.Conditions.Select(set);
        Page = plugin.Toolbar.Id;
        requestedTab = ToolbarModule.ConditionsTab;
        IsOpen = true;
    }

    public void ShowCorePage()
    {
        Page = CorePageId;
        IsOpen = true;
    }

    /// <summary>目前這一頁。沒記錄或記錄的頁已經不存在時，停在第一個模組。</summary>
    private string Page
    {
        get
        {
            var page = plugin.Config.LastSettingsPage;
            return IsKnownPage(page) ? page : plugin.Modules[0].Id;
        }
        set
        {
            if (plugin.Config.LastSettingsPage == value) return;

            plugin.Config.LastSettingsPage = value;
            plugin.Save();
        }
    }

    private bool IsKnownPage(string page)
    {
        if (page is CorePageId or ThemePage or HotkeysPage or TransferPage) return true;
        if (page == TweakSearchPage || TweakGroupOf(page) != null) return true;

        foreach (var module in plugin.Modules)
        {
            if (module.Id == page)
                return true;
        }

        return false;
    }

    public override void Draw()
    {
        if (ImGui.BeginChild("nav", new Vector2(NavWidth, 0), true))
            DrawNav();

        ImGui.EndChild();
        ImGui.SameLine();

        if (ImGui.BeginChild("page", Vector2.Zero, false))
            DrawPage();

        ImGui.EndChild();
    }

    private void DrawNav()
    {
        var page = Page;

        NavGroup("自動流程", ModuleGroup.Automation, page);
        ImGui.Spacing();
        NavGroup("功能", ModuleGroup.Feature, page);
        ImGui.Spacing();
        NavTweaks(page);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        NavItem("核心能力", CorePageId, page);
        NavItem("主題", ThemePage, page);
        NavItem("快捷鍵列表", HotkeysPage, page);
        NavItem("匯入匯出", TransferPage, page);
    }

    /// <summary>一組模組：灰色小標題，下面是這組的模組。沒有模組的組不顯示。</summary>
    private void NavGroup(string title, ModuleGroup group, string page)
    {
        var first = true;
        foreach (var module in plugin.Modules)
        {
            // 小功能在側邊欄自己一區（NavTweaks），不在「功能」組裡
            if (module.Group != group || module == plugin.Tweaks) continue;

            if (first)
            {
                ImGui.TextDisabled(title);
                first = false;
            }

            NavModule(module, page);
        }
    }

    /// <summary>
    /// 模組在清單上前面有一個點。功能：亮的是啟用、灰的是停用。
    /// 自動流程沒有啟用／停用（一直開著），點改成表示「正在執行」。
    /// </summary>
    private void NavModule(IModule module, string page)
    {
        var theme = plugin.Config.Theme;
        var start = ImGui.GetCursorScreenPos();
        var height = ImGui.GetTextLineHeight();
        var dot = height * 0.3f;

        // 名稱前面留點的位置，點畫在 Selectable 上面
        if (ImGui.Selectable($"      {module.Name}##{module.Id}", page == module.Id))
            Page = module.Id;

        var lit = module.Group == ModuleGroup.Automation
            ? plugin.IsEnabled(module) && module.Running
            : plugin.IsEnabled(module);
        var color = lit ? theme.Positive : theme.TextMuted;
        ImGui.GetWindowDrawList().AddCircleFilled(
            start + new Vector2(dot + ImGui.GetStyle().FramePadding.X, height / 2f), dot, Paint.Color(color));
    }

    private void NavItem(string label, string id, string page)
    {
        if (ImGui.Selectable(label, page == id))
            Page = id;
    }

    // ── 小功能：側邊欄自己一區，第一項是搜尋，下面一個分組一項 ──

    private const string TweakSearchPage = "tweaks:search";
    private const string TweakGroupPrefix = "tweaks:";

    private static string TweakGroupPage(Tweaks.TweakGroup group) => TweakGroupPrefix + group;

    private static Tweaks.TweakGroup? TweakGroupOf(string page) =>
        page.StartsWith(TweakGroupPrefix) && Enum.TryParse<Tweaks.TweakGroup>(page[TweakGroupPrefix.Length..], out var group)
            ? group
            : null;

    /// <summary>小功能一區：「搜尋」＋每個分組一項，分組前面的點亮 = 這組有開著的小功能。</summary>
    private void NavTweaks(string page)
    {
        var tweaks = plugin.Tweaks;
        ImGui.TextDisabled("小功能");

        NavItem("      搜尋", TweakSearchPage, page);

        var theme = plugin.Config.Theme;
        foreach (var group in Enum.GetValues<Tweaks.TweakGroup>())
        {
            var items = tweaks.All.Where(t => t.Group == group).ToList();
            if (items.Count == 0) continue;

            var start = ImGui.GetCursorScreenPos();
            var height = ImGui.GetTextLineHeight();
            var dot = height * 0.3f;
            var on = items.Count(t => t.Enabled);

            if (ImGui.Selectable($"      {group.Name()}##tweaks-{group}", page == TweakGroupPage(group)))
                Page = TweakGroupPage(group);

            var color = on > 0 ? theme.Positive : theme.TextMuted;
            ImGui.GetWindowDrawList().AddCircleFilled(
                start + new Vector2(dot + ImGui.GetStyle().FramePadding.X, height / 2f), dot, Paint.Color(color));
        }
    }

    private void DrawPage()
    {
        var page = Page;

        // 舊的「小功能」整頁：改成搜尋頁
        if (page == plugin.Tweaks.Id) page = TweakSearchPage;

        if (page == TweakSearchPage)
        {
            PageHeading("小功能：搜尋", "輸入名稱或說明，找出要的小功能。");
            plugin.Tweaks.DrawSearch();
            return;
        }

        if (TweakGroupOf(page) is { } tweakGroup)
        {
            PageHeading($"小功能：{tweakGroup.Name()}", "各種掛在遊戲視窗上的小改動，各自開關。");
            plugin.Tweaks.DrawGroup(tweakGroup);
            return;
        }

        switch (page)
        {
            case CorePageId:
                PageHeading("核心能力", "自動流程和小功能共用的底層能力。平常不需要動，功能執行時會自己借用。");
                corePage.Draw();
                return;

            case ThemePage:
                PageHeading("主題", "工具列、面板與右鍵選單的顏色和尺寸。所有模組共用。");
                DrawThemeTab();
                return;

            case HotkeysPage:
                PageHeading("快捷鍵列表", "所有模組裡設了快捷鍵的項目，看哪些鍵已經用掉、有沒有重複。");
                hotkeys.Draw();
                return;

            case TransferPage:
                PageHeading("匯入匯出", "把設定複製成一行文字保存或分享；可以只匯出、只套用其中幾部分。");
                transfer.Draw();
                return;
        }

        foreach (var module in plugin.Modules)
        {
            if (module.Id == page)
                DrawModulePage(module);
        }
    }

    /// <summary>每一頁最上面：標題加一句說明，新的頁面也照這個格式。</summary>
    private static void PageHeading(string title, string description)
    {
        ImGui.TextUnformatted(title);
        ImGui.TextDisabled(description);
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>
    /// 模組頁：最上面是啟用開關與模組自己的控制項，下面是它的分頁。
    /// 停用時分頁藏起來 — 改了也看不出效果，只會讓人困惑。
    /// </summary>
    private void DrawModulePage(IModule module)
    {
        ImGui.TextUnformatted(module.Name);
        ImGui.TextDisabled(module.Description);
        ImGui.Separator();
        ImGui.Spacing();

        if (module.CanDisable)
        {
            var on = plugin.IsEnabled(module);
            if (ImGui.Checkbox("啟用", ref on))
                plugin.SetEnabled(module, on);
        }

        if (!plugin.IsEnabled(module))
        {
            ImGui.Spacing();
            ImGui.TextDisabled("停用中：不顯示、不偵測快捷鍵。設定都還在，重新啟用就恢復。");
            return;
        }

        module.DrawHeader();
        ImGui.Spacing();

        if (module.DrawPage != null)
        {
            module.DrawPage();
            return;
        }

        if (!ImGui.BeginTabBar($"tabs{module.Id}")) return;

        foreach (var tab in module.SettingsTabs)
        {
            var flags = tab.Title == requestedTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (!ImGui.BeginTabItem(tab.Title, flags)) continue;

            tab.Draw();
            ImGui.EndTabItem();
        }

        requestedTab = null;
        ImGui.EndTabBar();
    }

    // ── 主題 ────────────────────────────────────────────────

    private void DrawThemeTab()
    {
        var theme = plugin.Config.Theme;

        Paint.SettingsHeading("底色");
        ColorRow("工具列背景", () => theme.BarBackground, v => theme.BarBackground = v);
        ColorRow("面板背景", () => theme.PanelBackground, v => theme.PanelBackground = v);
        ColorRow("描邊", () => theme.Border, v => theme.Border = v);

        Paint.SettingsHeading("工具列按鈕");
        ColorRow("卡片背景", () => theme.ChipBackground, v => theme.ChipBackground = v);
        ColorRow("卡片描邊", () => theme.ChipBorder, v => theme.ChipBorder = v);
        ColorRow("面板開著", () => theme.ChipOpen, v => theme.ChipOpen = v);
        FloatRow("卡片高度", () => theme.BarHeight, v => theme.BarHeight = v, 20f, 56f);
        FloatRow("卡片圖示", () => theme.BarIconSize, v => theme.BarIconSize = v, 12f, 40f);

        Paint.SettingsHeading("項目");
        ColorRow("項目背景", () => theme.RowBackground, v => theme.RowBackground = v);
        ColorRow("項目滑過", () => theme.RowHover, v => theme.RowHover = v);
        ColorRow("項目按下", () => theme.RowActive, v => theme.RowActive = v);

        Paint.SettingsHeading("文字");
        ColorRow("主文字", () => theme.TextPrimary, v => theme.TextPrimary = v);
        ColorRow("副文字", () => theme.TextSecondary, v => theme.TextSecondary = v);
        ColorRow("提示文字", () => theme.TextMuted, v => theme.TextMuted = v);

        Paint.SettingsHeading("狀態");
        ColorRow("強調色", () => theme.Accent, v => theme.Accent = v);
        ColorRow("刪除", () => theme.Danger, v => theme.Danger = v);
        ColorRow("成立", () => theme.Positive, v => theme.Positive = v);
        ColorRow("編輯外框", () => theme.Outline, v => theme.Outline = v);
        ColorRow("編輯外框（滑過）", () => theme.OutlineActive, v => theme.OutlineActive = v);

        Paint.SettingsHeading("職能底色");
        ColorRow("坦克", () => theme.RoleTank, v => theme.RoleTank = v);
        ColorRow("治療", () => theme.RoleHealer, v => theme.RoleHealer = v);
        ColorRow("輸出", () => theme.RoleDps, v => theme.RoleDps = v);
        ColorRow("能工巧匠", () => theme.RoleCrafter, v => theme.RoleCrafter = v);
        ColorRow("大地使者", () => theme.RoleGatherer, v => theme.RoleGatherer = v);

        Paint.SettingsHeading("尺寸");
        FloatRow("圓角", () => theme.Rounding, v => theme.Rounding = v, 0f, 16f);
        FloatRow("描邊粗細", () => theme.BorderWidth, v => theme.BorderWidth = v, 0f, 4f);
        FloatRow("列高", () => theme.RowHeight, v => theme.RowHeight = v, 18f, 64f);
        FloatRow("圖示大小", () => theme.IconSize, v => theme.IconSize = v, 12f, 48f);
        FloatRow("內距", () => theme.Padding, v => theme.Padding = v, 0f, 24f);
        FloatRow("間隔", () => theme.Gap, v => theme.Gap = v, 0f, 16f);

        ImGui.Spacing();
        ImGui.Separator();

        if (ImGui.Button("恢復預設"))
        {
            plugin.Config.Theme = new Theme();
            plugin.Save();
        }
    }

    private void ColorRow(string label, Func<Vector4> get, Action<Vector4> set)
    {
        var value = get();
        if (!ImGui.ColorEdit4(label, ref value, ImGuiColorEditFlags.AlphaPreviewHalf | ImGuiColorEditFlags.NoInputs))
            return;

        set(value);
        plugin.Save();
    }

    private void FloatRow(string label, Func<float> get, Action<float> set, float min, float max)
    {
        var value = get();
        if (!ImGui.SliderFloat(label, ref value, min, max, "%.0f"))
            return;

        set(value);
        plugin.Save();
    }
}
