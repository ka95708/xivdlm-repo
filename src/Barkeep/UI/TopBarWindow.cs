using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Barkeep.UI;

/// <summary>
/// 工具列：橫向一排卡片，右鍵拖曳移動位置。
/// 編輯模式下直接在這裡編輯 — 卡片上右鍵改設定、空白處右鍵新增、左鍵拖卡片排順序。
/// </summary>
public class TopBarWindow : Window
{
    private readonly Plugin plugin;

    private bool rightHeld;
    private bool rightMoved;

    private const string ChipPayload = "BARKEEP_CHIP";
    private int dragIndex = -1;

    /// <summary>這一幀每張卡片的位置，右鍵判斷按在哪張卡片、新增要插在哪都靠它。</summary>
    private readonly List<(TopBarItem Item, Vector2 Min, Vector2 Max)> chipRects = new();

    private TopBarItem? menuItem;
    private TopBarItem? pendingChipMenu;
    private int insertAt;

    public TopBarWindow(Plugin plugin) : base("Barkeep##TopBar", ImGuiWindowFlags.None, true)
    {
        this.plugin = plugin;
        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
    }

    private Theme Theme => plugin.Config.Theme;
    private TopBarConfig Bar => plugin.Config.TopBar;

    public override bool DrawConditions() =>
        Services.ClientState.IsLoggedIn
        && Barkeep.Game.ConditionEvaluator.IsVisible(plugin.Config, Bar.ConditionSetId);

    public override void PreDraw()
    {
        Flags = ImGuiWindowFlags.NoTitleBar
                | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoScrollWithMouse
                | ImGuiWindowFlags.AlwaysAutoResize
                | ImGuiWindowFlags.NoFocusOnAppearing
                | ImGuiWindowFlags.NoNav
                | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.NoMove;

        Position = Bar.Position;
        PositionCondition = ImGuiCond.Always;

        // 卡片貼齊整條的邊，不留內距
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Theme.Gap, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
    }

    public override void PostDraw() => ImGui.PopStyleVar(2);

    public override void Draw()
    {
        // draw list 是依序疊上去的，整條的底必須先畫，所以寬度要自己算，不能等卡片排完再量
        var start = ImGui.GetCursorScreenPos();
        Paint.Surface(start, start + new Vector2(ContentWidth(), Theme.BarHeight), Theme.BarBackground);

        chipRects.Clear();

        for (var i = 0; i < Bar.Items.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine();

            DrawItem(Bar.Items[i], i);
        }

        // 沒有項目時還是要有東西讓滑鼠右鍵點得到，不然整排就找不回來了
        if (Bar.Items.Count == 0)
            Paint.Chip("empty", EmptyChip, ItemStyle.TextOnly, false, false);

        // 項目之間與四周都有留白，滑過那些地方不該讓面板收起來
        // 按住卡片時也算滑過，理由同面板（見 PanelWindow.Draw）
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows
                                  | ImGuiHoveredFlags.AllowWhenBlockedByPopup
                                  | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
            plugin.Panels.KeepAlive();

        HandleRightButton();
    }

    private float ContentWidth()
    {
        if (Bar.Items.Count == 0)
            return Paint.ChipWidth(EmptyChip, ItemStyle.TextOnly, false);

        var width = Theme.Gap * (Bar.Items.Count - 1);

        foreach (var item in Bar.Items)
            width += Paint.ChipWidth(item.Current, item.Style, item.Kind == ItemKind.Panel);

        return width;
    }

    private static readonly ActionItem EmptyChip = new() { Label = "Barkeep（右鍵）" };

    private void DrawItem(TopBarItem item, int index)
    {
        var open = item.Panel != null && plugin.Panels.IsShowing(item.Panel);

        ImGui.PushID(item.Id.ToString());
        var clicked = Paint.Chip("item", item.Current, item.Style, item.Kind == ItemKind.Panel, open);

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var hovered = ImGui.IsItemHovered();

        chipRects.Add((item, min, max));

        if (plugin.EditMode)
            DragChip(item, index);

        ImGui.PopID();

        if (item.Kind == ItemKind.Panel)
        {
            if (item.Panel == null) return;

            // 快捷鍵按下的開關請求，要等畫到這張卡片才知道面板該貼在哪
            if (plugin.Panels.TakeToggleRequest(item.Panel, min, max)) return;

            // hover 就展開，不做延遲；面板自己會把間隙納入命中範圍
            if (hovered)
                plugin.Panels.Open(item.Panel, min, max);

            return;
        }

        // 編輯模式下也照常執行，改完直接點就能測；拖曳排序放開時不算點擊
        if (!clicked || Paint.WasDragged()) return;

        Services.Log.Debug($"點擊工具列：{item.Current.Label}（編輯模式={plugin.EditMode}）");
        plugin.Execute(item);
    }

    /// <summary>編輯模式下左鍵拖卡片調整順序，放到哪張卡片上就換到那個位置。</summary>
    private void DragChip(TopBarItem item, int index)
    {
        if (ImGui.BeginDragDropSource())
        {
            dragIndex = index;
            ImGui.SetDragDropPayload(ChipPayload, ReadOnlySpan<byte>.Empty);
            ImGui.TextUnformatted(string.IsNullOrEmpty(item.Current.Label) ? "項目" : item.Current.Label);
            ImGui.EndDragDropSource();
        }

        if (!ImGui.BeginDragDropTarget()) return;

        var payload = DropHighlight.Accept(ChipPayload);
        var items = Bar.Items;

        if (!payload.IsNull && payload.IsDelivery() && dragIndex >= 0 && dragIndex < items.Count && dragIndex != index)
        {
            var moved = items[dragIndex];
            items.RemoveAt(dragIndex);
            items.Insert(Math.Clamp(index, 0, items.Count), moved);

            dragIndex = -1;
            plugin.Save();
        }

        ImGui.EndDragDropTarget();
    }

    private TopBarItem? HitChip(Vector2 point)
    {
        foreach (var (item, min, max) in chipRects)
        {
            if (point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y)
                return item;
        }

        return null;
    }

    /// <summary>新增的項目插在哪：滑鼠左邊有幾張卡片，就插在第幾個位置。</summary>
    private int InsertIndexAt(float x)
    {
        var index = 0;

        foreach (var (_, min, max) in chipRects)
        {
            if ((min.X + max.X) / 2f < x)
                index++;
        }

        return index;
    }

    /// <summary>
    /// 右鍵按住拖曳移動整條，單擊開選單：
    /// 編輯模式下按在卡片上開卡片選單，其餘開工具列選單。
    /// </summary>
    private void HandleRightButton()
    {
        var hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows
                                            | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem
                                            | ImGuiHoveredFlags.AllowWhenBlockedByPopup);

        if (!rightHeld)
        {
            if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            {
                rightHeld = true;
                rightMoved = false;
            }
        }
        else if (ImGui.IsMouseDown(ImGuiMouseButton.Right))
        {
            // 超過拖曳門檻才算在移動：手按右鍵時免不了抖一兩個像素，
            // 不設門檻的話那一點抖動就會被當成拖曳，選單打不開
            if (!rightMoved && ImGui.IsMouseDragging(ImGuiMouseButton.Right))
            {
                rightMoved = true;
                MoveBy(ImGui.GetMouseDragDelta(ImGuiMouseButton.Right));
            }
            else if (rightMoved)
            {
                MoveBy(ImGui.GetIO().MouseDelta);
            }
        }
        else
        {
            rightHeld = false;
            if (!rightMoved)
                OpenMenuAt(ImGui.GetMousePos());
        }

        DrawMenus();
    }

    private void OpenMenuAt(Vector2 mouse)
    {
        menuItem = plugin.EditMode ? HitChip(mouse) : null;

        if (menuItem != null)
        {
            ImGui.OpenPopup(TopBarMenu.ChipId);
            return;
        }

        insertAt = InsertIndexAt(mouse.X);
        ImGui.OpenPopup(TopBarMenu.BarId);
    }

    private void DrawMenus()
    {
        // 剛新增的項目：上一個選單已經關了，這一幀幫它開卡片選單
        if (pendingChipMenu != null)
        {
            menuItem = pendingChipMenu;
            pendingChipMenu = null;
            ImGui.OpenPopup(TopBarMenu.ChipId);
        }

        var added = TopBarMenu.DrawBar(plugin, insertAt);
        if (added != null)
            pendingChipMenu = added;

        if (menuItem == null) return;

        if (TopBarMenu.DrawChip(plugin, menuItem, Bar.Items, out var addedHere))
            plugin.Save();

        if (addedHere != null)
            pendingChipMenu = addedHere;
    }

    private void MoveBy(Vector2 delta)
    {
        var viewport = ImGui.GetMainViewport();
        var size = ImGui.GetWindowSize();
        var target = Bar.Position + delta;

        var min = viewport.WorkPos;
        var max = viewport.WorkPos + viewport.WorkSize - size;

        Bar.Position = new Vector2(
            Math.Clamp(target.X, min.X, Math.Max(min.X, max.X)),
            Math.Clamp(target.Y, min.Y, Math.Max(min.Y, max.Y)));

        plugin.Save();
    }
}
