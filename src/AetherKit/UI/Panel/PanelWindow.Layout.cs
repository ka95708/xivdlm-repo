using System;
using System.Collections.Generic;
using System.Numerics;
using AetherKit.Game;
using Dalamud.Bindings.ImGui;

namespace AetherKit.UI;

/// <summary>
/// 版面計算。這裡只算位置，不畫任何東西 —
/// 量尺寸和繪製都用同一份結果，兩邊不可能算出不同答案。
/// </summary>
public partial class PanelWindow
{
    /// <summary>職能小組在 RoleColumns 裡的位置：第幾欄、欄裡第幾個。</summary>
    private readonly record struct RoleSlot(int Column, int Index);

    /// <summary>
    /// 分區裡的一個東西：一個項目，或一個標題。座標是相對分區內容左上角。
    /// Role / Slot 只有職能小組的標題才有，拖曳排序要用。
    /// </summary>
    private readonly record struct Part(
        Vector2 Offset,
        ActionItem? Item,
        int Index,
        string? Title,
        JobRole? Role,
        Vector4? Tint,
        RoleSlot Slot = default);

    /// <summary>拖職能小組時才出現的落點：某一欄的底下，或右邊的新欄。</summary>
    private readonly record struct DropZone(Vector2 Offset, Vector2 Size, RoleSlot Slot);

    private sealed class SectionLayout
    {
        public List<Part> Parts { get; } = new();
        public List<DropZone> Zones { get; } = new();
        public float ContentHeight { get; set; }
    }

    /// <summary>Column / Index：這個分區在 PanelConfig.Columns 裡的位置，拖曳要用。</summary>
    private sealed record PlacedSection(
        PanelSection Section,
        Vector2 Offset,
        Vector2 Size,
        SectionLayout Layout,
        int Column,
        int Index);

    /// <summary>一欄在面板內容裡的橫向範圍，與最後一個分區的下緣。</summary>
    private readonly record struct PlacedColumn(float X, float Width, float Bottom);

    /// <summary>Size 是全部內容的大小（含四周內距），面板比它小就捲動。</summary>
    private sealed record Flow(List<PlacedSection> Sections, List<PlacedColumn> Columns, Vector2 Size);

    /// <summary>
    /// 把分區排進面板：一欄一欄由左到右，欄裡由上到下。
    /// 欄寬取那一欄最寬的分區；面板寬度不影響排法，放不下就左右捲動。
    /// </summary>
    private Flow LayoutColumns(PanelConfig config)
    {
        var theme = Theme;
        var placed = new List<PlacedSection>();
        var columns = new List<PlacedColumn>(config.Columns.Count);

        // 整個面板用同一個格寬（照所有分區裡最長的名稱），不同分區的格子才會對齊、一樣寬
        var itemWidth = MinItemWidth;
        foreach (var section in config.Sections)
            itemWidth = MathF.Max(itemWidth, NaturalItemWidth(section));

        var x = theme.Padding;
        var bottom = 0f;

        for (var c = 0; c < config.Columns.Count; c++)
        {
            var sections = config.Columns[c];
            var width = 0f;
            var y = theme.Padding;

            for (var i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                var box = new Vector2(
                    MathF.Max(section.Width, MinSectionWidth(section, itemWidth)),
                    MathF.Max(section.Height, theme.RowHeight));

                placed.Add(new PlacedSection(section, new Vector2(x, y), box, LayoutSection(section, box, itemWidth), c, i));

                width = MathF.Max(width, box.X);
                y += box.Y + theme.BlockGap;
            }

            var columnBottom = y - theme.BlockGap;
            columns.Add(new PlacedColumn(x, width, columnBottom));

            bottom = MathF.Max(bottom, columnBottom);
            x += width + theme.BlockGap;
        }

        var size = columns.Count == 0
            ? Vector2.Zero
            : new Vector2(x - theme.BlockGap + theme.Padding, bottom + theme.Padding);

        return new Flow(placed, columns, size);
    }

    /// <summary>
    /// 分區的內容。內容比分區高時會出現捲軸、可用寬度變窄，
    /// 所以超出的話用扣掉捲軸的寬度再排一次，最右邊那欄才不會被捲軸蓋住。
    /// natural = 放得下名稱的最小格寬（整個面板一樣）。
    /// </summary>
    private SectionLayout LayoutSection(PanelSection section, Vector2 box, float natural)
    {
        var layout = LayoutStretched(section, box.X, natural);

        return layout.ContentHeight > box.Y
            ? LayoutStretched(section, MathF.Max(natural, box.X - ImGui.GetStyle().ScrollbarSize), natural)
            : layout;
    }

    /// <summary>
    /// 格子撐滿分區寬度：先看用最小格寬塞得下幾欄，再把剩下的寬度平均分給每一欄，右邊不留一塊空白。
    /// 套裝列表照職能小組分欄（不是照寬度），維持最小格寬。
    /// </summary>
    private SectionLayout LayoutStretched(PanelSection section, float width, float natural)
    {
        var gap = Theme.Gap;
        var columns = FixedColumns(section) is > 0 and var fixedCount
            ? fixedCount
            : Math.Max(1, (int)((width + gap) / (natural + gap)));
        section.MinItemWidth = natural;

        section.ItemWidth = section.HasGroups
            ? natural
            : MathF.Max(natural, (width - gap * (columns - 1)) / columns);

        return LayoutSection(section, width);
    }

    private SectionLayout LayoutSection(PanelSection section, float width)
    {
        var layout = new SectionLayout();
        var columns = ColumnsFor(section, width);

        switch (section.Source)
        {
            case SectionSource.Manual:
                // 顯示的是每個項目目前的狀態；Part.Index 對回 section.Items
                // 設了「不能按時藏起來」的不排；indices 記每一格對回 section.Items 的第幾個
                var faces = new List<ActionItem>(section.Items.Count);
                var indices = new List<int>(section.Items.Count);

                for (var i = 0; i < section.Items.Count; i++)
                {
                    if (ToolbarItemState.IsHidden(plugin.Config, section.Items[i], EditMode)) continue;

                    faces.Add(ToolbarItemState.Face(section.Items[i]));
                    indices.Add(i);
                }

                LayoutGrid(layout, section.Title, faces, columns, section.ItemWidth, width, indices);
                break;

            case SectionSource.Gearsets:
                LayoutGroups(layout, section, PanelContent.Get(section.Source), columns);
                break;

            default:
                var rows = PanelContent.Get(section.Source);
                var items = new List<ActionItem>(rows.Count);

                foreach (var row in rows)
                    items.Add(row.Item);

                LayoutGrid(layout, section.Title, items, columns, section.ItemWidth, width);
                break;
        }

        return layout;
    }

    /// <summary>分區指定的欄數；0 = 自動。套裝列表照職能小組分欄，一律 0。</summary>
    private static int FixedColumns(PanelSection section) => section.HasGroups ? 0 : section.ColumnCount;

    /// <summary>分區最窄多寬：指定欄數時要放得下那幾欄，自動時放得下一欄。</summary>
    private float MinSectionWidth(PanelSection section, float natural)
    {
        var columns = Math.Max(1, FixedColumns(section));
        return natural * columns + Theme.Gap * (columns - 1);
    }

    /// <summary>每一格多寬：剛好放得下分區裡最長的名稱（含圖示、副標），太長的截斷、滑過看全名。</summary>
    private float NaturalItemWidth(PanelSection section)
    {
        var widest = 0f;

        if (section.Source == SectionSource.Manual)
        {
            foreach (var item in section.Items)
                widest = MathF.Max(widest, Paint.ItemWidth(item.Current));
        }
        else
        {
            foreach (var row in PanelContent.Get(section.Source))
                widest = MathF.Max(widest, Paint.ItemWidth(row.Item));
        }

        return Math.Clamp(widest, MinItemWidth, MaxItemWidth);
    }

    private const float MinItemWidth = 80f;
    private const float MaxItemWidth = 320f;

    /// <summary>欄數由寬度推出來，欄間距也算進去，所以剛好塞得下才多一欄。</summary>
    private int ColumnsFor(PanelSection section, float width)
    {
        var gap = Theme.Gap;
        return Math.Max(1, (int)((width + gap) / (section.ItemWidth + gap)));
    }

    /// <summary>沒有小組的分區：一般的格線，欄數由寬度決定。</summary>
    private void LayoutGrid(SectionLayout layout, string title, IReadOnlyList<ActionItem> items,
        int columns, float itemWidth, float width, IReadOnlyList<int>? indices = null)
    {
        var theme = Theme;
        var y = 0f;

        if (!string.IsNullOrEmpty(title))
        {
            layout.Parts.Add(new Part(Vector2.Zero, null, -1, title, null, null));
            y += Paint.TitleHeight;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var offset = new Vector2(
                i % columns * (itemWidth + theme.Gap),
                y + i / columns * (theme.RowHeight + theme.Gap));

            layout.Parts.Add(new Part(offset, items[i], indices?[i] ?? i, null, null, null));
        }

        var rowCount = (items.Count + columns - 1) / columns;
        layout.ContentHeight = y + rowCount * theme.RowHeight + Math.Max(0, rowCount - 1) * theme.Gap;
    }

    /// <summary>
    /// 有職能小組的分區（裝備組）：像遊戲的職業頁，小組固定擺在使用者指定的欄裡，由上到下。
    /// 不做自動流動 — 位置固定，拖曳時才知道會落在哪。
    /// 沒有裝備組的小組不顯示，整欄都沒有的欄也不佔位置；
    /// 分區太窄放不下所有欄時，多出來的欄接在最後一欄底下。
    /// </summary>
    private void LayoutGroups(SectionLayout layout, PanelSection section, IReadOnlyList<ContentRow> rows, int fit)
    {
        var theme = Theme;
        var columns = section.ResolvedRoleColumns();
        var columnStep = section.ItemWidth + theme.Gap;
        var rowStep = theme.RowHeight + theme.Gap;

        var visible = new List<(int Column, List<(JobRole Role, int Index, List<ActionItem> Members)> Groups)>();

        for (var c = 0; c < columns.Count; c++)
        {
            var groups = new List<(JobRole, int, List<ActionItem>)>();

            for (var i = 0; i < columns[c].Count; i++)
            {
                var role = columns[c][i];
                var members = new List<ActionItem>();

                foreach (var row in rows)
                {
                    if (row.Role == role)
                        members.Add(row.Item);
                }

                if (members.Count > 0)
                    groups.Add((role, i, members));
            }

            if (groups.Count > 0)
                visible.Add((c, groups));
        }

        if (visible.Count == 0) return;

        var shown = Math.Min(visible.Count, fit);
        var heights = new float[shown];
        var appendSlots = new RoleSlot[shown];

        for (var v = 0; v < visible.Count; v++)
        {
            var display = Math.Min(v, shown - 1);
            var (column, groups) = visible[v];

            foreach (var (role, index, members) in groups)
            {
                var offset = new Vector2(display * columnStep, heights[display]);
                var tint = theme.RoleColor(role);

                layout.Parts.Add(new Part(offset, null, -1, RoleName(role), role, null, new RoleSlot(column, index)));
                offset.Y += Paint.TitleHeight;

                for (var i = 0; i < members.Count; i++)
                    layout.Parts.Add(new Part(offset + new Vector2(0, i * rowStep), members[i], -1, null, null, tint));

                heights[display] = offset.Y + members.Count * rowStep - theme.Gap + theme.BlockGap;
            }

            // 放到這一欄底下 = 接在這一欄（資料上的那一欄）最後面
            appendSlots[display] = new RoleSlot(column, columns[column].Count);
        }

        var tallest = 0f;
        foreach (var height in heights)
            tallest = MathF.Max(tallest, height);

        layout.ContentHeight = MathF.Max(0, tallest - theme.BlockGap);

        // 落點：每一欄底下剩的空間，至少一列高
        var zoneBottom = MathF.Max(layout.ContentHeight, theme.RowHeight);

        for (var d = 0; d < shown; d++)
        {
            var top = MathF.Max(0, heights[d] - theme.BlockGap);
            var height = MathF.Max(zoneBottom - top, theme.RowHeight);

            layout.Zones.Add(new DropZone(new Vector2(d * columnStep, top),
                new Vector2(section.ItemWidth, height), appendSlots[d]));
        }

        // 右邊還放得下一欄的話，可以拖過去開新欄
        if (shown < fit)
        {
            layout.Zones.Add(new DropZone(new Vector2(shown * columnStep, 0),
                new Vector2(section.ItemWidth, zoneBottom), new RoleSlot(columns.Count, 0)));
        }
    }

    /// <summary>跟遊戲職業頁的分類名稱一致。</summary>
    private static string RoleName(JobRole role) => role switch
    {
        JobRole.Tank => "防護職業",
        JobRole.Healer => "治療職業",
        JobRole.Melee => "近戰職業",
        JobRole.PhysicalRanged => "遠程物理職業",
        JobRole.MagicalRanged => "遠程魔法職業",
        JobRole.Crafter => "能工巧匠",
        _ => "大地使者"
    };
}
