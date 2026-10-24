using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Barkeep.UI;

/// <summary>
/// 照主題畫東西。顏色與尺寸一律從 Theme 拿，這個檔案以外不應該出現寫死的顏色。
///
/// ImGui 只負責「這塊矩形有沒有被點到」，看得見的一律自己畫。
/// </summary>
public static class Paint
{
    /// <summary>副標比主標小一點。</summary>
    private const float SubLabelScale = 0.85f;

    private const float GripSize = 14f;
    private const float GripArm = 10f;

    private static Theme Theme => Plugin.Instance.Config.Theme;

    public static uint Color(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    /// <summary>
    /// 這次放開左鍵是不是拖曳的結尾。編輯模式下左鍵點擊照常執行、拖曳則是排序，
    /// 拖完放回原處時按鈕也會回報「點了」，要靠這個分開。
    /// GetMouseDragDelta 只有移動超過拖曳門檻才不是零，放開的那一幀也還拿得到。
    /// </summary>
    public static bool WasDragged() =>
        ImGui.GetMouseDragDelta(ImGuiMouseButton.Left) != Vector2.Zero;

    /// <summary>圓角半透明底 + 細描邊，面板和工具列共用。</summary>
    public static void Surface(Vector2 min, Vector2 max, Vector4 background)
    {
        var draw = ImGui.GetWindowDrawList();
        var theme = Theme;

        draw.AddRectFilled(min, max, Color(background), theme.Rounding);

        if (theme.BorderWidth > 0)
            draw.AddRect(min, max, Color(theme.Border), theme.Rounding, ImDrawFlags.None, theme.BorderWidth);
    }

    /// <summary>
    /// 一個可點的項目：圖示在左、主標在右，有副標就疊在主標下面。
    /// 工具列和面板用的都是這個。tint 是底色（例如職能色），null 就用主題預設。
    /// </summary>
    public static bool Item(string id, ActionItem item, Vector2 size, ItemStyle style,
        Vector4? tint = null, bool enabled = true)
    {
        var theme = Theme;

        var clicked = ImGui.InvisibleButton(id, size) && enabled;

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var draw = ImGui.GetWindowDrawList();

        // 底色先畫，滑過與按下再疊一層上去，這樣染色的列也看得出互動狀態
        draw.AddRectFilled(min, max, Color(tint ?? theme.RowBackground), theme.Rounding);

        if (enabled && (hovered || active))
            draw.AddRectFilled(min, max, Color(active ? theme.RowActive : theme.RowHover), theme.Rounding);

        var cursor = min.X + theme.Padding;
        var showIcon = style != ItemStyle.TextOnly && item.IconId > 0;
        var showText = style != ItemStyle.IconOnly || item.IconId == 0;

        if (showIcon)
        {
            var texture = enabled && !item.Grayscale ? Icons.Get(item.IconId) : Icons.GetGrayscale(item.IconId);

            if (texture != null)
            {
                var iconMin = new Vector2(cursor, min.Y + (size.Y - theme.IconSize) / 2f);
                draw.AddImageRounded(texture.Handle, iconMin, iconMin + new Vector2(theme.IconSize),
                    Vector2.Zero, Vector2.One, Color(Vector4.One), theme.Rounding * 0.5f);
            }

            cursor += theme.IconSize + theme.Gap;
        }

        var truncated = showText && DrawLabels(draw, item, min, max, cursor, enabled);

        // 看不到全名就用 tooltip 補回來，截斷才不會真的損失資訊
        if (hovered && truncated)
            ImGui.SetTooltip(string.IsNullOrEmpty(item.SubLabel) ? item.Label : $"{item.Label}\n{item.SubLabel}");

        return clicked;
    }

    /// <summary>回傳 true 代表有字被截斷。</summary>
    private static bool DrawLabels(ImDrawListPtr draw, ActionItem item, Vector2 min, Vector2 max,
        float left, bool enabled)
    {
        var theme = Theme;
        var primary = Color(enabled ? theme.TextPrimary : theme.TextSecondary);
        var lineHeight = ImGui.GetTextLineHeight();
        var height = max.Y - min.Y;
        var room = max.X - theme.Padding - left;

        var cut = Truncate(item.Label, room, 1f, out var label);

        if (string.IsNullOrEmpty(item.SubLabel))
        {
            draw.AddText(new Vector2(left, min.Y + (height - lineHeight) / 2f), primary, label);
            return cut;
        }

        cut |= Truncate(item.SubLabel, room, SubLabelScale, out var sub);

        // 兩行：主標在上、副標在下，整組垂直置中
        var top = min.Y + (height - lineHeight * (1f + SubLabelScale)) / 2f;

        draw.AddText(new Vector2(left, top), primary, label);

        // SetWindowFontScale 不會影響已經在進行中的 AddText，要縮小就得明確指定字型與字級
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * SubLabelScale,
            new Vector2(left, top + lineHeight), Color(theme.TextSecondary), sub);

        return cut;
    }

    // ── 工具列卡片 ───────────────────────────────────────────

    /// <summary>
    /// 工具列的一張卡片：淡底 + 細框，圖示在左、名稱在右，只有一行。
    /// 跟面板的列分開畫 — 卡片疊在工具列的底上，要自己看得出邊界。
    /// opensPanel = 會展開面板的卡片，右邊多一個 ▾，跟直接執行的按鈕分得出來。
    /// open = 它的面板正開著，保持標亮，滑鼠移到面板上時還看得出是從哪張卡片開的。
    /// </summary>
    public static bool Chip(string id, ActionItem item, ItemStyle style, bool opensPanel, bool open)
    {
        var theme = Theme;
        var size = new Vector2(ChipWidth(item, style, opensPanel), theme.BarHeight);

        var clicked = ImGui.InvisibleButton(id, size);

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        var draw = ImGui.GetWindowDrawList();
        var rounding = theme.Rounding * 0.75f;

        draw.AddRectFilled(min, max, Color(open ? theme.ChipOpen : theme.ChipBackground), rounding);

        if (hovered || active)
            draw.AddRectFilled(min, max, Color(active ? theme.RowActive : theme.RowHover), rounding);

        if (theme.BorderWidth > 0)
            draw.AddRect(min, max, Color(open ? theme.Accent : theme.ChipBorder), rounding, ImDrawFlags.None, theme.BorderWidth);

        var cursor = min.X + ChipPadding;

        if (ShowsIcon(item, style))
        {
            var texture = item.Grayscale ? Icons.GetGrayscale(item.IconId) : Icons.Get(item.IconId);

            if (texture != null)
            {
                var iconMin = new Vector2(cursor, min.Y + (size.Y - theme.BarIconSize) / 2f);
                draw.AddImageRounded(texture.Handle, iconMin, iconMin + new Vector2(theme.BarIconSize),
                    Vector2.Zero, Vector2.One, Color(Vector4.One), rounding * 0.5f);
            }

            cursor += theme.BarIconSize + theme.Gap;
        }

        if (ShowsText(item, style))
        {
            var y = min.Y + (size.Y - ImGui.GetTextLineHeight()) / 2f;
            draw.AddText(new Vector2(cursor, y),
                Color(item.Grayscale ? theme.TextSecondary : theme.TextPrimary), item.Label);
        }

        if (opensPanel)
            Chevron(draw, new Vector2(max.X - ChipPadding - ChevronSize / 2f, (min.Y + max.Y) / 2f),
                Color(open ? theme.Accent : theme.TextSecondary));

        return clicked;
    }

    private const float ChevronSize = 7f;

    /// <summary>朝下的小三角，中心在 center。</summary>
    private static void Chevron(ImDrawListPtr draw, Vector2 center, uint color)
    {
        var half = ChevronSize / 2f;
        var height = ChevronSize * 0.6f;

        draw.AddTriangleFilled(
            center + new Vector2(-half, -height / 2f),
            center + new Vector2(half, -height / 2f),
            center + new Vector2(0, height / 2f),
            color);
    }

    private static float ChipPadding => Theme.Gap * 1.5f;

    private static bool ShowsIcon(ActionItem item, ItemStyle style) =>
        style != ItemStyle.TextOnly && item.IconId > 0;

    private static bool ShowsText(ActionItem item, ItemStyle style) =>
        style != ItemStyle.IconOnly || item.IconId == 0;

    /// <summary>卡片剛好包住圖示與文字，不截斷 — 工具列的字本來就短。</summary>
    public static float ChipWidth(ActionItem item, ItemStyle style, bool opensPanel)
    {
        var theme = Theme;
        var width = ChipPadding * 2 + (opensPanel ? ChevronSize + theme.Gap : 0);
        var showText = ShowsText(item, style);

        if (ShowsIcon(item, style))
            width += theme.BarIconSize + (showText ? theme.Gap : 0);

        if (showText)
            width += ImGui.CalcTextSize(item.Label).X;

        return MathF.Max(width, theme.BarHeight);
    }

    /// <summary>
    /// 把字截到放得下為止，尾巴補省略號。ImGui 沒有 text-overflow，
    /// 固定欄寬又一定會遇到長名字，所以得自己量。回傳 true 代表有截斷。
    /// </summary>
    public static bool Truncate(string text, float maxWidth, float scale, out string result)
    {
        result = text;
        if (string.IsNullOrEmpty(text)) return false;
        if (ImGui.CalcTextSize(text).X * scale <= maxWidth) return false;

        const string ellipsis = "…";
        var ellipsisWidth = ImGui.CalcTextSize(ellipsis).X * scale;

        // 由長到短試，中文一個字就是一個 char
        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length];
            if (ImGui.CalcTextSize(candidate).X * scale + ellipsisWidth > maxWidth) continue;

            result = candidate + ellipsis;
            return true;
        }

        result = ellipsis;
        return true;
    }

    /// <summary>標題字（分區標題、職能小組標題）。只畫字、不佔版面 — 位置由版面計算決定。</summary>
    public static void TitleText(Vector2 pos, string title)
    {
        if (string.IsNullOrEmpty(title)) return;

        ImGui.GetWindowDrawList().AddText(pos + new Vector2(Theme.Padding, 0), Color(Theme.TextSecondary), title);
    }

    /// <summary>職能小組標題：字底下一條細線，跟遊戲職業頁一樣。</summary>
    public static void GroupTitle(Vector2 pos, string title, float width)
    {
        TitleText(pos, title);

        var y = pos.Y + ImGui.GetTextLineHeight() + Theme.Gap * 0.5f;
        ImGui.GetWindowDrawList().AddLine(new Vector2(pos.X, y), new Vector2(pos.X + width, y),
            Color(Theme.Border), 1f);
    }

    public static float TitleHeight => ImGui.GetTextLineHeight() + Theme.Gap;

    /// <summary>淡色提示字。不畫框，只告訴使用者這裡可以做什麼。</summary>
    public static void Hint(Vector2 pos, string text) =>
        ImGui.GetWindowDrawList().AddText(pos, Color(Theme.TextMuted), text);

    /// <summary>編輯模式下的外框。畫在內容外面一點，版面才不會因為進出編輯模式而移動。</summary>
    public static void Outline(Vector2 min, Vector2 max, bool highlighted, bool accent = false)
    {
        var theme = Theme;
        var inset = new Vector2(theme.OutlineInset);
        var color = accent ? theme.Accent : highlighted ? theme.OutlineActive : theme.Outline;

        ImGui.GetWindowDrawList().AddRect(min - inset, max + inset, Color(color), theme.Rounding);
    }

    /// <summary>
    /// 右下角的縮放把手，面板和分區共用同一個樣子。
    /// 點擊範圍往角落外面多延伸一點：裡面被捲動用的子視窗蓋住，
    /// 範圍全放在裡面的話滑鼠點不到。回傳這一幀拖了多少；fit = 連點兩下，要縮到剛好包住內容。
    /// </summary>
    public static Vector2 ResizeGrip(string id, Vector2 corner, out bool fit)
    {
        var theme = Theme;

        ImGui.SetCursorScreenPos(corner - new Vector2(GripArm));
        ImGui.InvisibleButton(id, new Vector2(GripSize));

        var active = ImGui.IsItemActive();
        var hovered = ImGui.IsItemHovered();
        var color = Color(active ? theme.TextPrimary : hovered ? theme.OutlineActive : theme.Outline);

        var draw = ImGui.GetWindowDrawList();
        draw.AddLine(corner - new Vector2(GripArm, 0), corner, color, 2f);
        draw.AddLine(corner - new Vector2(0, GripArm), corner, color, 2f);

        if (hovered || active)
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNwse);

        if (hovered)
            ImGui.SetTooltip("拖曳調整大小，連點兩下縮到剛好");

        fit = hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);

        return active && ImGui.IsMouseDragging(ImGuiMouseButton.Left)
            ? ImGui.GetIO().MouseDelta
            : Vector2.Zero;
    }

    /// <summary>
    /// 設定視窗裡的小節標題。設定視窗用的是 Dalamud 原生樣式（跟其他插件一致），
    /// 但小節的寫法統一走這裡，不再各處自己 TextDisabled。
    /// </summary>
    public static void SettingsHeading(string text)
    {
        ImGui.Spacing();
        ImGui.TextColored(Theme.TextSecondary, text);
        ImGui.Separator();
    }
}
