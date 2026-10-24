using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Barkeep.UI;

/// <summary>
/// 管理視窗：只放畫面上不好做的事 — 條件組、快捷鍵總覽、游標提示、主題色票與匯入匯出。
/// 工具列與面板的內容一律在編輯模式下直接在畫面上右鍵編輯。
///
/// 這個視窗用 Dalamud 原生樣式，跟其他插件的設定視窗一致；
/// 主題色票只套用在工具列、面板與右鍵選單上。
/// </summary>
public class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private readonly ConditionEditor conditions;
    private readonly HotkeySettings hotkeys;
    private readonly CursorSettings cursor;
    private readonly TransferSettings transfer;

    private bool jumpToConditions;

    public ConfigWindow(Plugin plugin) : base("Barkeep 設定###BarkeepConfig")
    {
        this.plugin = plugin;
        conditions = new ConditionEditor(plugin);
        hotkeys = new HotkeySettings(plugin);
        cursor = new CursorSettings(plugin);
        transfer = new TransferSettings(plugin);
        Size = new Vector2(720, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 360),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public void OpenConditions(ConditionSet? set)
    {
        conditions.Select(set);
        jumpToConditions = true;
        IsOpen = true;
    }

    public override void Draw()
    {
        var edit = plugin.EditMode;
        if (ImGui.Checkbox("編輯模式", ref edit))
            plugin.EditMode = edit;

        ImGui.SameLine();
        ImGui.TextDisabled("（滑過看怎麼編輯）");

        if (ImGui.IsItemHovered())
            DrawEditHelp();

        ImGui.Separator();

        if (!ImGui.BeginTabBar("main")) return;

        var conditionFlags = jumpToConditions ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        jumpToConditions = false;

        if (ImGui.BeginTabItem("條件組", conditionFlags))
        {
            conditions.Draw();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("快捷鍵"))
        {
            hotkeys.Draw();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("游標提示"))
        {
            cursor.Draw();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("主題"))
        {
            DrawThemeTab();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("匯入匯出"))
        {
            transfer.Draw();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
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
