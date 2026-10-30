using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Barkeep.UI;

/// <summary>
/// 由工具列項目展開的面板。同一時間只會有一個，所以用一個視窗輪流顯示不同內容。
///
/// 兩層，各自固定大小、各自捲動：
///   面板 — 有寬高，分區放在固定的欄裡（欄由左到右、欄裡由上到下），放不下就上下左右捲動
///   分區 — 有寬高，內容超出就自己捲動，不會反過來撐大分區
///
/// 這個檔案管視窗與繪製；版面怎麼算在 PanelWindow.Layout.cs，
/// 編輯模式的互動在 PanelWindow.Edit.cs。
/// </summary>
public partial class PanelWindow : Window
{
    private const float MinPanelWidth = 160f;
    private const float MinPanelHeight = 80f;

    private readonly Plugin plugin;

    private PanelConfig? panel;
    private Vector2 openerMin;
    private Vector2 openerMax;
    private int lastHoveredFrame = -10;

    public PanelWindow(Plugin plugin) : base("Barkeep##Panel", ImGuiWindowFlags.None, true)
    {
        this.plugin = plugin;
        IsOpen = false;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
    }

    private Theme Theme => plugin.Config.Theme;

    private bool EditMode => plugin.EditMode;

    public void Open(PanelConfig target, Vector2 itemMin, Vector2 itemMax)
    {
        if (!ReferenceEquals(target, panel))
            ResetEditing();

        panel = target;
        openerMin = itemMin;
        openerMax = itemMax;
        lastHoveredFrame = plugin.FrameCount;
        pinnedByHotkey = false;
        IsOpen = true;
    }

    /// <summary>
    /// 快捷鍵按下時要開關的面板。快捷鍵在遊戲迴圈裡偵測，那時還不知道卡片在畫面上的位置，
    /// 所以先記下來，等工具列畫到那張卡片時再用它的位置開（TakeToggleRequest）。
    /// 遊戲迴圈和繪製都在同一條主執行緒上，不用鎖。
    /// </summary>
    public void RequestToggle(PanelConfig target) => toggleRequest = target;

    private PanelConfig? toggleRequest;

    /// <summary>
    /// 用快捷鍵開的面板，滑鼠不一定在旁邊，不能照「滑鼠離開就收」的規則 —
    /// 一開就會被收掉。所以先釘住，等滑鼠進來過一次再恢復平常的規則；
    /// 再按一次快捷鍵、或點到插件以外的地方也會收起來。
    /// </summary>
    private bool pinnedByHotkey;

    /// <summary>工具列畫到這張卡片時呼叫。有它的開關請求就處理，回傳 true 代表處理了。</summary>
    public bool TakeToggleRequest(PanelConfig target, Vector2 itemMin, Vector2 itemMax)
    {
        if (!ReferenceEquals(toggleRequest, target)) return false;

        toggleRequest = null;

        if (IsShowing(target))
        {
            IsOpen = false;
            pinnedByHotkey = false;
            return true;
        }

        Open(target, itemMin, itemMax);
        pinnedByHotkey = true;

        // Open 會當成「剛被滑過」，那樣下一次 Tick 就解除釘住了；滑鼠其實還沒進來過
        lastHoveredFrame = -10;
        return true;
    }

    /// <summary>
    /// 滑鼠還在工具列上（可能只是在項目之間的留白），不要關。
    /// 沒有這個的話，從按鈕滑到旁邊的空隙就會把面板收掉。
    /// </summary>
    public void KeepAlive() => lastHoveredFrame = plugin.FrameCount;

    /// <summary>這個面板現在是不是開著，工具列要把對應的卡片標亮。</summary>
    public bool IsShowing(PanelConfig target) => IsOpen && ReferenceEquals(target, panel);

    /// <summary>編輯模式釘住不關，否則滑鼠離開就收。</summary>
    public void Tick()
    {
        // 工具列沒畫出來（例如被顯示條件藏起來）就沒人接，丟掉，免得之後突然打開
        toggleRequest = null;

        if (!IsOpen || EditMode) return;

        if (pinnedByHotkey)
        {
            // 點到插件以外的地方就收；滑鼠進來過之後改回平常的規則
            if (plugin.LeftClickedThisFrame && !ImGui.GetIO().WantCaptureMouse)
            {
                IsOpen = false;
                pinnedByHotkey = false;
            }
            else if (plugin.FrameCount - lastHoveredFrame <= 1)
            {
                pinnedByHotkey = false;
            }

            return;
        }

        if (plugin.FrameCount - lastHoveredFrame > 1)
            IsOpen = false;
    }

    public override void PreDraw()
    {
        Flags = ImGuiWindowFlags.NoTitleBar
                | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoScrollWithMouse
                | ImGuiWindowFlags.AlwaysAutoResize
                | ImGuiWindowFlags.NoFocusOnAppearing
                | ImGuiWindowFlags.NoNav
                | ImGuiWindowFlags.NoBackground
                | ImGuiWindowFlags.NoMove
                | plugin.GameDrag.PassThrough;

        Position = AnchoredPosition();
        PositionCondition = ImGuiCond.Always;

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Theme.Gap));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
    }

    public override void PostDraw() => ImGui.PopStyleVar(2);

    /// <summary>
    /// 貼齊工具列按鈕左緣、開在正下方。太寬就往左推回畫面內；
    /// 太高不往上推（會蓋住工具列），而是由 ShownSize 縮短。
    /// </summary>
    private Vector2 AnchoredPosition()
    {
        var position = new Vector2(openerMin.X, openerMax.Y);
        if (panel == null) return position;

        var viewport = ImGui.GetMainViewport();
        var min = viewport.WorkPos.X;
        var max = viewport.WorkPos.X + viewport.WorkSize.X - ShownSize(panel).X - EdgeMargin;

        return position with { X = Math.Clamp(position.X, min, Math.Max(min, max)) };
    }

    /// <summary>右下角的縮放把手畫在面板外側，要留這麼多空間，把手才不會掉到螢幕外。</summary>
    private const float EdgeMargin = 16f;

    /// <summary>從按鈕下方到螢幕右下角放得下多大。</summary>
    private Vector2 MaxPanelSize()
    {
        var viewport = ImGui.GetMainViewport();
        var bottom = viewport.WorkPos.Y + viewport.WorkSize.Y;
        var top = openerMax.Y + Theme.Bridge;

        return new Vector2(
            MathF.Max(MinPanelWidth, viewport.WorkSize.X - EdgeMargin),
            MathF.Max(MinPanelHeight, bottom - top - EdgeMargin));
    }

    /// <summary>
    /// 實際顯示的大小：設定值，但不超出螢幕。超出的部分捲動看。
    /// 設定值本身不動 — 換到大螢幕或把工具列往上移，面板就回到原本的大小。
    /// 編輯模式下右下角的把手因此永遠看得到，不會因為拉太大而再也改不回來。
    /// </summary>
    private Vector2 ShownSize(PanelConfig config) =>
        Vector2.Min(new Vector2(config.Width, config.Height), MaxPanelSize());

    public override void Draw()
    {
        if (panel == null)
        {
            IsOpen = false;
            return;
        }

        // AllowWhenBlockedByActiveItem 不能少：按住列的那一刻，那一列變成「作用中」，
        // 沒有這個旗標的話 ImGui 會說視窗沒被滑過，面板在按下時就收起來，
        // 列等不到放開滑鼠，點擊永遠不會成立
        // 遊戲拖曳中視窗不接滑鼠（見 GameDragDrop.PassThrough），hover 一定是 false，改看游標位置
        var windowMin = ImGui.GetWindowPos();
        if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows
                                  | ImGuiHoveredFlags.AllowWhenBlockedByPopup
                                  | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
            || plugin.GameDrag.IsOver(windowMin, windowMin + ImGui.GetWindowSize()))
            lastHoveredFrame = plugin.FrameCount;

        // 透明橋接：佔住按鈕與面板之間的間隙，hover 才不會中斷，但不畫任何東西
        ImGui.Dummy(new Vector2(1, Theme.Bridge));

        var origin = ImGui.GetCursorScreenPos();
        var size = ShownSize(panel);

        Paint.Surface(origin, origin + size, Theme.PanelBackground);

        // 欄的位置固定、不隨面板寬度換行，所以放不下時要能左右捲
        if (ImGui.BeginChild("body", size, false,
                ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.HorizontalScrollbar | plugin.GameDrag.PassThrough))
            DrawBody(panel);

        ImGui.EndChild();

        if (EditMode)
            ResizePanel(panel, origin, size);
    }

    /// <summary>分區一欄一欄排。位置每一幀照欄的清單重算，所以換位置只要改清單。</summary>
    private void DrawBody(PanelConfig config)
    {
        var theme = Theme;
        var origin = ImGui.GetCursorScreenPos();
        var flow = LayoutColumns(config);
        lastFlow = flow;
        bodyOrigin = origin;

        sectionRects.Clear();

        foreach (var placed in flow.Sections)
        {
            var min = origin + placed.Offset;

            ImGui.PushID(placed.Section.Id.ToString());
            DrawSection(placed, min);
            ImGui.PopID();

            sectionRects.Add((placed.Section, min, min + placed.Size));
        }

        if (config.Columns.Count == 0 && EditMode)
            Paint.Hint(origin + new Vector2(theme.Padding), "在這裡右鍵新增分區");

        // 絕對定位的內容不會自己撐開捲動範圍，在右下角補一個元件把寬高都撐出來
        ImGui.SetCursorScreenPos(origin + Vector2.Max(Vector2.Zero, flow.Size - Vector2.One));
        ImGui.Dummy(Vector2.One);

        if (EditMode)
            HandleEditing(config);
    }

    /// <summary>一個分區：外框在外面畫，內容放在自己的子視窗裡，超出就自己捲動。</summary>
    private void DrawSection(PlacedSection placed, Vector2 min)
    {
        var theme = Theme;
        var section = placed.Section;
        var layout = placed.Layout;
        var max = min + placed.Size;

        if (EditMode)
            DrawSectionOutline(section, min, max);

        ImGui.SetCursorScreenPos(min);

        if (ImGui.BeginChild("section", placed.Size, false, ImGuiWindowFlags.NoBackground | plugin.GameDrag.PassThrough))
        {
            var origin = ImGui.GetCursorScreenPos();
            var rowSize = new Vector2(section.ItemWidth, theme.RowHeight);
            var id = 0;

            foreach (var part in layout.Parts)
            {
                var pos = origin + part.Offset;

                if (part.Item == null)
                {
                    DrawTitle(section, part, pos);
                    continue;
                }

                ImGui.SetCursorScreenPos(pos);
                ImGui.PushID(id++);

                DrawRow(section, part, rowSize);

                if (!section.IsAuto)
                {
                    // 看得到的列才算落點，捲出去的不算
                    if (ImGui.IsItemVisible())
                    {
                        plugin.GameDrag.RegisterTarget(section.Items, part.Index,
                            ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
                    }

                    if (EditMode)
                        DrawRowEditing(section, part.Index);
                }

                ImGui.PopID();
            }

            if (EditMode && RoleDrag.IsDragging(section))
                DrawDropZones(section, layout, origin);

            ImGui.SetCursorScreenPos(origin + new Vector2(0, MathF.Max(0, layout.ContentHeight - 1)));
            ImGui.Dummy(Vector2.One);

            if (!section.IsAuto)
            {
                // 分區的空白處 = 加到最後。要在列之後登記，列才會優先被命中
                var windowMin = ImGui.GetWindowPos();
                plugin.GameDrag.RegisterTarget(section.Items, section.Items.Count, windowMin, windowMin + placed.Size);

                if (EditMode && section.Items.Count == 0)
                    Paint.Hint(origin + new Vector2(theme.Padding, theme.Padding), "右鍵新增項目");
            }
        }

        ImGui.EndChild();

        if (EditMode)
            ResizeSection(placed, max);
    }

    private void DrawTitle(PanelSection section, Part part, Vector2 pos)
    {
        var title = part.Title ?? string.Empty;

        if (part.Role is not { } role)
        {
            Paint.TitleText(pos, title);
            return;
        }

        // 編輯模式下職能小組的標題可以拖曳換位置、換欄
        if (EditMode)
            DrawRoleTitle(section, role, part.Slot, pos);

        Paint.GroupTitle(pos, title, section.ItemWidth);
    }

    /// <summary>
    /// 點了不關面板：常常是連續做幾個情感動作，或換完套裝順手再按別的。
    /// 自訂區塊的項目跟工具列按鈕一樣，可以有開關／循環狀態；
    /// 套裝與情感動作是讀遊戲的，直接呼叫遊戲函式執行。
    /// </summary>
    private void DrawRow(PanelSection section, Part part, Vector2 size)
    {
        var item = part.Item!;
        if (!Paint.Item("row", item, size, ItemStyle.IconAndText, part.Tint)) return;

        // 編輯模式下也照常執行，改完直接點就能測；拖曳排序放開時不算點擊
        if (Paint.WasDragged()) return;

        Services.Log.Debug($"點擊：{item.Label}（編輯模式={EditMode}，來源={item.Source?.Type}/{item.Source?.Id}）");

        if (section.IsAuto)
            plugin.Executor.Run(item, direct: true);
        else if (part.Index >= 0 && part.Index < section.Items.Count)
            plugin.Press(section.Items[part.Index]);
    }
}
