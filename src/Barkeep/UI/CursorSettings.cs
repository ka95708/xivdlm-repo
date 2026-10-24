using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Barkeep.UI;

/// <summary>
/// 管理視窗的「游標提示」分頁。選一個現有的開關項目（工具列按鈕或面板裡的自訂項目）、
/// 它在哪個狀態時顯示、游標旁用什麼圖示。項目本身的設定完全不受影響 — 設定項目時不需要知道這裡的事。
/// </summary>
public class CursorSettings
{
    private readonly Plugin plugin;

    public CursorSettings(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private CursorConfig Cursor => plugin.Config.Cursor;

    public void Draw()
    {
        ImGui.TextDisabled("工具列按鈕或面板裡的自訂項目停在指定狀態時，在滑鼠旁顯示圖示。最多兩個。");

        var indicators = Cursor.Indicators;

        for (var i = 0; i < indicators.Count; i++)
        {
            ImGui.PushID(i);
            var removed = DrawIndicator(indicators[i], i);
            ImGui.PopID();

            if (!removed) continue;

            indicators.RemoveAt(i);
            plugin.Save();
            break;
        }

        if (indicators.Count < CursorConfig.MaxIndicators)
        {
            ImGui.Spacing();
            if (ImGui.Button("＋ 新增游標提示"))
                Add();
        }

        DrawShared();
    }

    /// <summary>可以選的項目：有兩個以上狀態的才有「開著／關著」可言。</summary>
    private IEnumerable<(StatefulItem Item, string Location)> Toggles() =>
        ItemDirectory.Pressable(plugin.Config)
            .Where(entry => entry.Item.Cycles)
            .Select(entry => (entry.Item, entry.Location));

    /// <summary>新的提示預設跟第一個開關項目，圖示先借它「開」那個狀態的，之後再換。</summary>
    private void Add()
    {
        var indicator = new CursorIndicator();
        var first = Toggles().Select(entry => entry.Item).FirstOrDefault();

        if (first != null)
        {
            indicator.ItemId = first.Id;
            indicator.StateIndex = Math.Min(1, first.States.Count - 1);
            indicator.IconId = first.States[indicator.StateIndex].IconId;
        }

        Cursor.Indicators.Add(indicator);
        plugin.Save();
    }

    /// <summary>回傳 true 代表按了移除。</summary>
    private bool DrawIndicator(CursorIndicator indicator, int index)
    {
        Paint.SettingsHeading($"提示 {index + 1}");

        DrawPreview(indicator);
        ImGui.SameLine();

        ImGui.BeginGroup();

        var item = CursorOverlay.FindItem(plugin, indicator.ItemId);

        DrawItemCombo(indicator, item);
        DrawStateCombo(indicator, item);
        DrawIcon(indicator, index);
        DrawAnimation(indicator);

        var removed = ImGui.SmallButton("移除");

        ImGui.EndGroup();
        return removed;
    }

    /// <summary>預覽一直在動，照實際的大小、透明度與轉速畫。</summary>
    private void DrawPreview(CursorIndicator indicator)
    {
        var outer = CursorOverlay.OuterSize(Cursor.Size);
        var box = new Vector2(MathF.Max(outer, 48f) + 8f);
        var min = ImGui.GetCursorScreenPos();

        ImGui.Dummy(box);

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(min, min + box, ImGui.GetColorU32(ImGuiCol.FrameBg), 4f);
        CursorOverlay.DrawIndicator(draw, min + box / 2f, indicator.IconId, indicator.Animation, Cursor,
            plugin.Config.Theme);
    }

    /// <summary>用所有狀態的名稱來認，例如「自動戰鬥 開 / 關」。</summary>
    private static string ItemName(StatefulItem item) =>
        string.Join(" / ", item.States.Select(state => string.IsNullOrEmpty(state.Label) ? "（未命名）" : state.Label));

    /// <summary>選單依位置分組：工具列一組，每個面板的每個自訂區塊各一組。</summary>
    private void DrawItemCombo(CursorIndicator indicator, StatefulItem? current)
    {
        var preview = current != null ? ItemName(current)
            : indicator.ItemId == Guid.Empty ? "（選一個開關項目）" : "（項目已不存在）";

        ImGui.SetNextItemWidth(260);

        if (!ImGui.BeginCombo("項目", preview)) return;

        var location = string.Empty;

        foreach (var (item, where) in Toggles())
        {
            if (where != location)
            {
                location = where;
                ImGui.Separator();
                ImGui.TextDisabled(where);
            }

            ImGui.PushID(item.Id.ToString());

            if (ImGui.Selectable($"　{ItemName(item)}", item.Id == indicator.ItemId))
            {
                indicator.ItemId = item.Id;
                indicator.StateIndex = Math.Clamp(indicator.StateIndex, 0, item.States.Count - 1);
                plugin.Save();
            }

            ImGui.PopID();
        }

        if (location.Length == 0)
            ImGui.TextDisabled("工具列和面板裡都還沒有兩個狀態以上的項目。");

        ImGui.EndCombo();
    }

    private void DrawStateCombo(CursorIndicator indicator, StatefulItem? item)
    {
        if (item == null) return;

        string StateName(int i) =>
            $"狀態 {i + 1}：{(string.IsNullOrEmpty(item.States[i].Label) ? "（未命名）" : item.States[i].Label)}";

        var preview = indicator.StateIndex < item.States.Count
            ? StateName(indicator.StateIndex)
            : "（這個狀態已被移除）";

        ImGui.SetNextItemWidth(260);

        if (!ImGui.BeginCombo("顯示時機", preview)) return;

        for (var i = 0; i < item.States.Count; i++)
        {
            if (!ImGui.Selectable(StateName(i), i == indicator.StateIndex)) continue;

            indicator.StateIndex = i;
            plugin.Save();
        }

        ImGui.EndCombo();
    }

    private void DrawIcon(CursorIndicator indicator, int index)
    {
        ImGui.SetNextItemWidth(120);

        var icon = (int)indicator.IconId;
        if (ImGui.InputInt("##icon", ref icon, 0))
        {
            indicator.IconId = (uint)Math.Max(0, icon);
            plugin.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("瀏覽…"))
            plugin.OpenIconBrowser($"游標提示 {index + 1}", () => indicator.IconId, id => indicator.IconId = id);

        ImGui.SameLine();
        ImGui.TextUnformatted("圖示");
    }

    private void DrawAnimation(CursorIndicator indicator)
    {
        ImGui.TextUnformatted("動畫");
        ImGui.SameLine();

        if (ImGui.RadioButton("外圈轉動", indicator.Animation == CursorAnimation.Ring))
        {
            indicator.Animation = CursorAnimation.Ring;
            plugin.Save();
        }

        ImGui.SameLine();

        if (ImGui.RadioButton("無", indicator.Animation == CursorAnimation.None))
        {
            indicator.Animation = CursorAnimation.None;
            plugin.Save();
        }
    }

    private void DrawShared()
    {
        Paint.SettingsHeading("外觀（兩個共用）");

        var cursor = Cursor;

        var size = cursor.Size;
        if (ImGui.SliderFloat("大小", ref size, 16f, 64f, "%.0f"))
        {
            cursor.Size = size;
            plugin.Save();
        }

        var offset = cursor.Offset;
        if (ImGui.DragFloat2("離游標", ref offset, 0.5f, -80f, 80f, "%.0f"))
        {
            cursor.Offset = offset;
            plugin.Save();
        }

        var opacity = cursor.Opacity;
        if (ImGui.SliderFloat("不透明度", ref opacity, 0.2f, 1f, "%.2f"))
        {
            cursor.Opacity = opacity;
            plugin.Save();
        }

        var speed = cursor.Speed;
        if (ImGui.SliderFloat("轉速（圈／秒）", ref speed, 0.1f, 3f, "%.1f"))
        {
            cursor.Speed = speed;
            plugin.Save();
        }

        var hide = cursor.HideWhileDragging;
        if (ImGui.Checkbox("轉視角時隱藏（按住滑鼠在遊戲畫面上）", ref hide))
        {
            cursor.HideWhileDragging = hide;
            plugin.Save();
        }
    }
}
