using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace AetherKit.UI;

/// <summary>
/// 編輯模式的互動：拖分區換位置、縮放面板與分區、右鍵選單、項目與職能小組的拖曳排序。
/// 一般模式完全不會走到這裡，只有 hover 開面板、點擊執行兩件事。
/// </summary>
public partial class PanelWindow
{
    /// <summary>這一幀每個分區在畫面上的位置，拖曳與右鍵都靠它判斷落在哪一區。</summary>
    private readonly List<(PanelSection Section, Vector2 Min, Vector2 Max)> sectionRects = new();

    private PanelSection? draggingSection;
    private bool sectionDragMoved;
    private PanelSection? menuSection;

    /// <summary>這一幀的排版結果與它在畫面上的原點（已含捲動），拖曳落點與縮到剛好都要用。</summary>
    private Flow? lastFlow;
    private Vector2 bodyOrigin;

    /// <summary>右鍵開面板選單時滑鼠在哪，新增的分區就放在那裡。</summary>
    private SectionSlot menuSlot;

    private void ResetEditing()
    {
        draggingSection = null;
        sectionDragMoved = false;
        menuSection = null;
    }

    private void HandleEditing(PanelConfig config)
    {
        HandleSectionDrag(config);
        HandleRightClick();
        DrawPanelMenu(config);
        DrawSectionMenu(config);
    }

    // ── 縮放 ────────────────────────────────────────────────

    /// <summary>把手放在角落外側：面板內部被捲動用的子視窗蓋住，放在裡面點不到。</summary>
    /// <summary>
    /// 從「看得到的大小」開始拖，而且拖不出螢幕：
    /// 設定值比螢幕大時，一拖就從螢幕邊緣開始縮，不用先把看不到的那一截拖回來。
    /// </summary>
    private void ResizePanel(PanelConfig config, Vector2 origin, Vector2 shown)
    {
        var delta = Paint.ResizeGrip("panelGrip", origin + shown + new Vector2(Theme.OutlineInset), out var fit);

        if (fit && lastFlow != null)
        {
            FitPanel(config, lastFlow);
            return;
        }

        if (delta == Vector2.Zero) return;

        SetPanelSize(config, shown + delta);
    }

    private void SetPanelSize(PanelConfig config, Vector2 size)
    {
        var max = MaxPanelSize();

        config.Width = Math.Clamp(size.X, MinPanelWidth, max.X);
        config.Height = Math.Clamp(size.Y, MinPanelHeight, max.Y);
        plugin.Save();
    }

    /// <summary>
    /// 分區的大小完全由使用者決定，內容不會反過來撐大它 — 放不下就捲動。
    /// 也不受面板寬度限制：面板放不下就左右捲。
    /// </summary>
    private void ResizeSection(PlacedSection placed, Vector2 corner)
    {
        var delta = Paint.ResizeGrip("sectionGrip", corner + new Vector2(Theme.OutlineInset), out var fit);
        var section = placed.Section;

        if (fit)
        {
            FitSection(section, placed.Layout);
            return;
        }

        if (delta == Vector2.Zero) return;

        section.Width = MathF.Max(placed.Size.X + delta.X, section.ItemWidth);
        section.Height = MathF.Max(placed.Size.Y + delta.Y, Theme.RowHeight);
        plugin.Save();
    }

    /// <summary>面板縮到剛好包住所有欄；內容比螢幕還大時就到螢幕邊緣為止，其餘捲動。</summary>
    private void FitPanel(PanelConfig config, Flow flow) => SetPanelSize(config, flow.Size);

    /// <summary>分區縮到剛好包住內容：實際用到的欄數，與內容的高度。</summary>
    private void FitSection(PanelSection section, SectionLayout layout)
    {
        var right = section.ItemWidth;
        foreach (var part in layout.Parts)
            right = MathF.Max(right, part.Offset.X + section.ItemWidth);

        section.Width = right;
        section.Height = MathF.Max(layout.ContentHeight, Theme.RowHeight);
        plugin.Save();
    }

    // ── 外框 ────────────────────────────────────────────────

    private void DrawSectionOutline(PanelSection section, Vector2 min, Vector2 max)
    {
        // 用座標判斷而不是 ImGui 的 hover：遊戲拖曳中視窗不接滑鼠，hover 一定是 false
        var hovered = Contains(min, max, ImGui.GetMousePos());
        var gameDropHere = plugin.Toolbar.GameDrag.IsDragging && !section.IsAuto && hovered;
        var dragging = ReferenceEquals(section, draggingSection) && sectionDragMoved;

        Paint.Outline(min, max, hovered || gameDropHere, dragging);
    }

    // ── 拖曳分區 ────────────────────────────────────────────

    /// <summary>
    /// 分區要放到哪：第 Column 欄的第 Index 個位置；
    /// NewColumn = 在第 Column 個位置插一個新欄（滑鼠在兩欄之間或最右邊）。
    /// </summary>
    private readonly record struct SectionSlot(int Column, int Index, bool NewColumn);

    private static bool Contains(Vector2 min, Vector2 max, Vector2 point) =>
        point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y;

    private PanelSection? HitSection(Vector2 point)
    {
        foreach (var (section, min, max) in sectionRects)
        {
            if (Contains(min, max, point))
                return section;
        }

        return null;
    }

    /// <summary>
    /// 在分區的空白處按住左鍵拖曳就能搬它。不走 ImGui 的拖放，
    /// 因為分區不是一個元件 — 它裡面的列才是，按在列上要拖的是列。
    /// 捲軸也算元件，所以按在捲軸上不會誤把分區拖走。
    /// </summary>
    private void HandleSectionDrag(PanelConfig config)
    {
        var mouse = ImGui.GetMousePos();

        if (draggingSection == null)
        {
            if (!ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
            if (ImGui.IsAnyItemHovered() || ImGui.IsAnyItemActive()) return;
            if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows)) return;

            draggingSection = HitSection(mouse);
            sectionDragMoved = false;
            return;
        }

        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {
            if (ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                sectionDragMoved = true;

            // 插入線畫在最上層，不會被分區的子視窗蓋住
            if (sectionDragMoved && lastFlow != null)
            {
                SlotAt(lastFlow, mouse, out var from, out var to);
                ImGui.GetForegroundDrawList().AddLine(from, to, Paint.Color(Theme.Accent), 3f);
            }

            return;
        }

        if (sectionDragMoved && lastFlow != null)
        {
            var slot = SlotAt(lastFlow, mouse, out _, out _);
            config.MoveSection(draggingSection, slot.Column, slot.Index, slot.NewColumn);
            plugin.Save();
        }

        draggingSection = null;
        sectionDragMoved = false;
    }

    /// <summary>
    /// 滑鼠位置對應到的落點，與要畫的插入線：
    ///   在某一欄的範圍內 → 看滑鼠在哪個分區的上半或下半，插在它前面或後面；比最後一個還低就接在最後
    ///   在兩欄之間、最左或最右 → 開一個新欄，畫直線
    /// 拖曳落點和右鍵「新增分區」都用這一份，加在哪裡跟拖到哪裡的規則一樣。
    /// </summary>
    private SectionSlot SlotAt(Flow flow, Vector2 mouse, out Vector2 from, out Vector2 to)
    {
        var theme = Theme;
        var half = theme.BlockGap / 2f;
        var origin = bodyOrigin;
        var columns = flow.Columns;

        var top = origin.Y + theme.Padding;
        var bottom = origin.Y + MathF.Max(flow.Size.Y - theme.Padding, theme.Padding + theme.RowHeight);

        for (var c = 0; c < columns.Count; c++)
        {
            var left = origin.X + columns[c].X;
            var right = left + columns[c].Width;

            // 比這一欄還左邊：插在這一欄前面當新欄
            if (mouse.X < left - half)
            {
                from = new Vector2(left - half, top);
                to = new Vector2(left - half, bottom);
                return new SectionSlot(c, 0, true);
            }

            if (mouse.X > right + half) continue;

            var count = 0;

            foreach (var placed in flow.Sections)
            {
                if (placed.Column != c) continue;

                count++;

                var sectionTop = origin.Y + placed.Offset.Y;
                if (mouse.Y >= sectionTop + placed.Size.Y / 2f) continue;

                from = new Vector2(left, sectionTop - half);
                to = new Vector2(right, sectionTop - half);
                return new SectionSlot(c, placed.Index, false);
            }

            var below = origin.Y + columns[c].Bottom + half;
            from = new Vector2(left, below);
            to = new Vector2(right, below);
            return new SectionSlot(c, count, false);
        }

        // 比所有欄都右邊（或還沒有任何欄）：最右邊開新欄
        var x = columns.Count == 0
            ? origin.X + theme.Padding
            : origin.X + columns[^1].X + columns[^1].Width + half;

        from = new Vector2(x, top);
        to = new Vector2(x, bottom);
        return new SectionSlot(columns.Count, 0, true);
    }

    // ── 拖曳項目與職能小組 ──────────────────────────────────

    /// <summary>自訂項目可以拖曳排序（可跨分區），右鍵開項目選單。</summary>
    private void DrawRowEditing(PanelSection section, int index)
    {
        var item = section.Items[index];
        var label = item.Current.Label;

        ItemDrag.Source(section.Items, index, string.IsNullOrEmpty(label) ? "項目" : label);

        if (ItemDrag.Target(section.Items, index))
            plugin.Save();

        if (ImGui.IsItemHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Right))
            ItemPopup.Open();

        if (ItemPopup.Draw(plugin, item, section.Items, index))
            plugin.Save();
    }

    /// <summary>
    /// 職能小組標題：編輯模式下是可以拖的把手。
    /// 拖到別組的標題上 = 排到它前面（可以跨欄）；拖到某欄底下的空白 = 接在那欄最後；
    /// 拖到右邊的空白 = 開新欄。後兩個落點只在拖曳時出現，見 DrawDropZones。
    /// 字由呼叫端畫，這裡只負責底色與拖放。
    /// </summary>
    private void DrawRoleTitle(PanelSection section, JobRole role, RoleSlot slot, Vector2 pos)
    {
        ImGui.SetCursorScreenPos(pos);
        ImGui.InvisibleButton($"role{(int)role}", new Vector2(section.ItemWidth, Paint.TitleHeight));

        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
        {
            ImGui.GetWindowDrawList().AddRectFilled(ImGui.GetItemRectMin(), ImGui.GetItemRectMax(),
                Paint.Color(Theme.RowHover), Theme.Rounding);
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        RoleDrag.Source(section, role, RoleName(role));

        if (RoleDrag.Target(section, slot.Column, slot.Index))
            plugin.Save();
    }

    /// <summary>拖曳職能小組時，每欄底下與右邊新欄畫出淡框，告訴使用者可以放在哪。</summary>
    private void DrawDropZones(PanelSection section, SectionLayout layout, Vector2 origin)
    {
        var draw = ImGui.GetWindowDrawList();
        var id = 0;

        foreach (var zone in layout.Zones)
        {
            var min = origin + zone.Offset;

            ImGui.SetCursorScreenPos(min);
            ImGui.InvisibleButton($"zone{id++}", zone.Size);

            var hovered = ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
            draw.AddRect(min, min + zone.Size, Paint.Color(hovered ? Theme.Accent : Theme.Outline), Theme.Rounding);

            if (RoleDrag.Target(section, zone.Slot.Column, zone.Slot.Index))
                plugin.Save();
        }
    }

    // ── 右鍵 ────────────────────────────────────────────────

    /// <summary>
    /// 右鍵在分區上開分區選單，在面板空白處開面板選單。
    /// 按在列上的右鍵由列自己處理（開項目選單），這裡不搶。
    /// </summary>
    private void HandleRightClick()
    {
        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Right)) return;
        if (!ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByPopup)) return;
        if (ImGui.IsAnyItemHovered()) return;

        var mouse = ImGui.GetMousePos();
        menuSection = HitSection(mouse);

        if (menuSection == null && lastFlow != null)
            menuSlot = SlotAt(lastFlow, mouse, out _, out _);

        ImGui.OpenPopup(menuSection == null ? "panelMenu" : "sectionMenu");
    }

    private void DrawPanelMenu(PanelConfig config)
    {
        if (!Menu.Begin("panelMenu")) return;

        Menu.Header("面板");
        Menu.Section("新增分區");

        if (Menu.Primary("＋ 自訂區塊"))
            AddSection(config, SectionSource.Manual);

        if (Menu.Primary("＋ 套裝列表"))
            AddSection(config, SectionSource.Gearsets);

        if (Menu.Primary("＋ 情感動作"))
            AddSection(config, SectionSource.EmoteFavorites);

        Menu.End();
    }

    /// <summary>放在按右鍵的位置：欄裡就插在那裡，欄與欄之間就開新欄，規則跟拖曳一樣。</summary>
    private void AddSection(PanelConfig config, SectionSource source)
    {
        config.MoveSection(new PanelSection { Source = source }, menuSlot.Column, menuSlot.Index, menuSlot.NewColumn);
        plugin.Save();
    }

    /// <summary>
    /// 分區選單分成三段：最上面是「往裡面加東西」，中間是這一區自己的設定，
    /// 最下面是刪除。動作和設定混在一起時，很難一眼找到要的東西。
    /// </summary>
    private void DrawSectionMenu(PanelConfig config)
    {
        if (!Menu.Begin("sectionMenu")) return;

        var section = menuSection;

        if (section == null)
        {
            ImGui.CloseCurrentPopup();
            Menu.End();
            return;
        }

        Menu.Header(SectionName(section));

        if (!section.IsAuto)
        {
            if (Menu.Primary("＋ 新增項目"))
            {
                section.Items.Add(PanelItem.From(new ActionItem { Label = "新項目" }));
                plugin.Save();
            }

            Menu.Separator();
        }

        Menu.Section("分區設定");

        if (!section.HasGroups)
        {
            ImGui.SetNextItemWidth(150);

            var title = section.Title;
            if (ImGui.InputText("標題", ref title, 64))
            {
                section.Title = title;
                plugin.Save();
            }
        }

        ImGui.SetNextItemWidth(150);

        var itemWidth = section.ItemWidth;
        if (ImGui.SliderFloat("項目寬度", ref itemWidth, 80f, 400f, "%.0f"))
        {
            section.ItemWidth = itemWidth;
            section.Width = MathF.Max(section.Width, itemWidth);
            plugin.Save();
        }

        if (section.HasGroups && section.RoleColumns.Count > 0 && Menu.Item("職能擺法恢復預設"))
        {
            section.RoleColumns.Clear();
            plugin.Save();
        }

        Menu.Separator();

        if (Menu.Danger("刪除這個分區"))
        {
            config.RemoveSection(section);
            menuSection = null;
            plugin.Save();
        }

        Menu.End();
    }

    private static string SectionName(PanelSection section) => section.Source switch
    {
        SectionSource.Gearsets => "套裝列表",
        SectionSource.EmoteFavorites => "情感動作",
        _ => string.IsNullOrEmpty(section.Title) ? "自訂區塊" : $"自訂區塊 · {section.Title}"
    };
}
