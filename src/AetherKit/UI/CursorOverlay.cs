using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;

namespace AetherKit.UI;

/// <summary>
/// 游標旁的提示圖示：按鈕停在指定狀態時，在滑鼠旁畫一個圓形圖示，可以加外圈轉動的動畫。
/// 畫在最上層的 draw list，不是視窗 — 不會擋到滑鼠，也不會被其他視窗蓋住。
/// </summary>
public static class CursorOverlay
{
    /// <summary>裁掉遊戲圖示自帶的方框邊，裁成圓形才乾淨。</summary>
    private const float IconCrop = 0.08f;

    /// <summary>每段弧線佔多少角度。兩段各 140°，中間留兩個 40° 的缺口。</summary>
    private const float ArcSpan = MathF.PI * 140f / 180f;

    private const int ArcSegments = 24;

    /// <summary>以下尺寸是在 28px 的圖示上看起來剛好的值，其他大小等比縮放。</summary>
    private const float BaseSize = 28f;

    private const float RingGap = 3f;
    private const float HeadWidth = 3.5f;
    private const float TailWidth = 0.6f;

    /// <summary>兩個提示並排時的間隔。</summary>
    private const float Spacing = 4f;

    public static void Draw(Plugin plugin)
    {
        var cursor = plugin.Config.Cursor;
        if (cursor.Indicators.Count == 0) return;

        // 每幀都要追按鍵狀態，才抓得到「按下的那一刻」
        var dragging = IsDraggingInGame();
        if (cursor.HideWhileDragging && dragging) return;

        var draw = ImGui.GetForegroundDrawList();
        var step = OuterSize(cursor.Size) + Spacing;
        var origin = ImGui.GetMousePos() + cursor.Offset;
        var shown = 0;

        foreach (var indicator in cursor.Indicators)
        {
            if (!IsActive(plugin, indicator)) continue;

            var center = origin + new Vector2(shown * step + OuterSize(cursor.Size) / 2f, OuterSize(cursor.Size) / 2f);
            DrawIndicator(draw, center, indicator.IconId, indicator.Animation, cursor, plugin.Config.Theme);
            shown++;
        }
    }

    /// <summary>項目存在、而且正停在指定的狀態。</summary>
    public static bool IsActive(Plugin plugin, CursorIndicator indicator)
    {
        var item = FindItem(plugin, indicator.ItemId);
        return item != null && item.StateIndex == indicator.StateIndex;
    }

    public static StatefulItem? FindItem(Plugin plugin, Guid id) => ItemDirectory.Find(plugin.Config, id);

    private const int VkLeftButton = 0x01;
    private const int VkRightButton = 0x02;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool buttonWasDown;
    private static bool draggingInGame;

    /// <summary>
    /// 按住滑鼠在遊戲畫面上（不是在 ImGui 上）通常是在轉視角，那時遊戲游標會消失，
    /// 提示還留在原地反而奇怪。
    ///
    /// 按鍵狀態要問 Windows：轉視角時遊戲會接管滑鼠，ImGui 收不到那次按下，
    /// 用 ImGui.IsMouseDown 會一直是 false。「按在哪」則在按下的那一刻判斷，
    /// 之後拖到插件視窗上也不改變。
    /// </summary>
    private static bool IsDraggingInGame()
    {
        var down = (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0
                   || (GetAsyncKeyState(VkRightButton) & 0x8000) != 0;

        if (down && !buttonWasDown)
            draggingInGame = !ImGui.GetIO().WantCaptureMouse;
        else if (!down)
            draggingInGame = false;

        buttonWasDown = down;
        return draggingInGame;
    }

    /// <summary>含外圈的整體大小。</summary>
    public static float OuterSize(float size)
    {
        var scale = size / BaseSize;
        return size + (RingGap + HeadWidth) * scale * 2f;
    }

    /// <summary>畫一個提示。設定頁的預覽也用這個，看到的就是實際的樣子。</summary>
    public static void DrawIndicator(ImDrawListPtr draw, Vector2 center, uint iconId, CursorAnimation animation,
        CursorConfig cursor, Theme theme)
    {
        var radius = cursor.Size / 2f;
        var alpha = Math.Clamp(cursor.Opacity, 0f, 1f);

        // 深色底圈：亮的場景上圖示邊緣才看得清楚
        draw.AddCircleFilled(center, radius + 1f, Paint.Color(theme.PanelBackground with { W = theme.PanelBackground.W * alpha }));

        var texture = iconId > 0 ? Icons.Get(iconId) : null;
        if (texture != null)
        {
            var half = new Vector2(radius);

            // 圓角等於邊長一半就是正圓
            draw.AddImageRounded(texture.Handle, center - half, center + half,
                new Vector2(IconCrop), new Vector2(1f - IconCrop),
                Paint.Color(new Vector4(1f, 1f, 1f, alpha)), radius);
        }

        if (animation == CursorAnimation.Ring)
            DrawRing(draw, center, cursor, theme.Accent with { W = theme.Accent.W * alpha });
    }

    /// <summary>
    /// 兩段弧線，頭粗尾細，一起繞著圖示轉。角度往正方向增加（螢幕上是順時針），
    /// 所以頭在弧線的結尾、領著尾巴跑。
    /// </summary>
    private static void DrawRing(ImDrawListPtr draw, Vector2 center, CursorConfig cursor, Vector4 color)
    {
        var scale = cursor.Size / BaseSize;
        var head = HeadWidth * scale;
        var tail = TailWidth * scale;
        var radius = cursor.Size / 2f + (RingGap + HeadWidth / 2f) * scale;

        var turns = ImGui.GetTime() * cursor.Speed;
        var rotation = (float)(turns % 1.0 * Math.PI * 2.0);
        var packed = Paint.Color(color);

        for (var arc = 0; arc < 2; arc++)
        {
            var start = rotation + arc * MathF.PI;
            var previous = Edge(center, radius, start, tail);

            for (var i = 1; i <= ArcSegments; i++)
            {
                var t = i / (float)ArcSegments;
                var current = Edge(center, radius, start + ArcSpan * t, tail + (head - tail) * t);

                // 每一小段是一個四邊形：上一個點的內外緣 → 這一個點的內外緣
                draw.AddQuadFilled(previous.Outer, current.Outer, current.Inner, previous.Inner, packed);
                previous = current;
            }

            // 頭端補一個圓，看起來是圓頭而不是被切平
            var tip = center + Direction(start + ArcSpan) * radius;
            draw.AddCircleFilled(tip, head / 2f, packed);
        }
    }

    private static (Vector2 Outer, Vector2 Inner) Edge(Vector2 center, float radius, float angle, float width)
    {
        var direction = Direction(angle);
        return (center + direction * (radius + width / 2f), center + direction * (radius - width / 2f));
    }

    private static Vector2 Direction(float angle) => new(MathF.Cos(angle), MathF.Sin(angle));
}
