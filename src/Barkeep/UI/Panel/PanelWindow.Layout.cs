using System;
using System.Collections.Generic;
using System.Numerics;
using Barkeep.Game;
using Dalamud.Bindings.ImGui;

namespace Barkeep.UI;

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
                    MathF.Max(section.Width, section.ItemWidth),
                    MathF.Max(section.Height, theme.RowHeight));

                placed.Add(new PlacedSection(section, new Vector2(x, y), box, LayoutSection(section, box), c, i));

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
    /// </summary>
    private SectionLayout LayoutSection(PanelSection section, Vector2 box)
    {
        var layout = LayoutSection(section, box.X);

        return layout.ContentHeight > box.Y
            ? LayoutSection(section, MathF.Max(section.ItemWidth, box.X - ImGui.GetStyle().ScrollbarSize))
            : layout;
    }

    private SectionLayout LayoutSection(PanelSection section, float width)
    {
        var layout = new SectionLayout();
        var columns = ColumnsFor(section, width);

        switch (section.Source)
        {
            case SectionSource.Manual:
                // 顯示的是每個項目目前的狀態；Part.Index 對回 section.Items
                var faces = new List<ActionItem>(section.Items.Count);

                foreach (var item in section.Items)
                    faces.Add(item.Current);

                LayoutGrid(layout, section.Title, faces, columns, section.ItemWidth, width);
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

    /// <summary>欄數由寬度推出來，欄間距也算進去，所以剛好塞得下才多一欄。</summary>
    private int ColumnsFor(PanelSection section, float width)
    {
        var gap = Theme.Gap;
        return Math.Max(1, (int)((width + gap) / (section.ItemWidth + gap)));
    }

    /// <summary>沒有小組的分區：一般的格線，欄數由寬度決定。</summary>
    private void LayoutGrid(SectionLayout layout, string title, IReadOnlyList<ActionItem> items,
        int columns, float itemWidth, float width)
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

            layout.Parts.Add(new Part(offset, items[i], i, null, null, null));
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
