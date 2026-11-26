using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace AetherKit.Game;

/// <summary>
/// 無人島採集的循環路線：島上每採 11 個點，最早採的那個會重生，所以挑 11 個靠得最近的點繞圈就能一直採。
/// 挑法見 Plan：選「每分鐘採到勾選的次數」最多的一圈，勾選的點不一定全用上，不夠 11 個用附近的其他點補。
/// </summary>
public static class IslandRoute
{
    /// <summary>採完這麼多個點，第一個會重生。</summary>
    public const int CycleSize = 11;

    public readonly record struct Stop(uint Type, Vector3 Point, bool Filler);

    /// <summary>估一圈時間用：移動的平均速度（米／秒，走路、騎乘、飛行混在一起）。</summary>
    public const float TravelSpeed = 15f;

    /// <summary>估一圈時間用：每個點停下來的時間（秒，下坐騎、採集動作、再上坐騎）。</summary>
    public const float StopSeconds = 6f;

    /// <summary>
    /// 排好的一圈（照走的順序）；沒有可用的點時是空的。
    /// 不是硬塞最多勾選的點，而是挑「每分鐘採到勾選的次數」最多的一圈：
    /// 每個勾選的點當起點，取它附近最近的 k 個勾選的點（k = 1…11），剩下的用離這些點最近的任意點補到 11 個，
    /// 估一圈時間（距離 ÷ 速度 + 每點停留），算每分鐘採到幾個勾選的，取最高的。
    /// 例如勾選的種類分成兩區各 5 個點時，「一區 5 個 + 附近 6 個別的」常常比「兩區來回」快。
    /// </summary>
    public static List<Stop> Plan(IReadOnlyCollection<uint> selected)
    {
        var allowed = IslandGatherData.Points.Keys.ToHashSet();
        var wanted = selected.Where(allowed.Contains).ToHashSet();
        var chosen = Points(wanted).ToList();
        if (chosen.Count == 0) return new List<Stop>();

        var all = Points(allowed).Select(s => s with { Filler = !wanted.Contains(s.Type) }).ToList();

        List<Stop>? best = null;
        var bestRate = -1f;
        var bestLength = float.MaxValue;

        foreach (var seed in chosen)
        {
            var nearChosen = chosen.OrderBy(p => Vector3.DistanceSquared(p.Point, seed.Point)).ToList();

            // 補點只從起點附近找（一圈本來就在這附近），勾選很多種時才不會算太久
            var pool = all.OrderBy(p => Vector3.DistanceSquared(p.Point, seed.Point)).Take(40).ToList();
            for (var k = 1; k <= Math.Min(CycleSize, nearChosen.Count); k++)
            {
                var set = nearChosen.Take(k).ToList();
                Fill(set, pool);

                var tour = Tour(set);
                var length = Length(tour);
                var rate = PerMinute(tour, length);
                if (rate < bestRate - 0.001f || (Math.Abs(rate - bestRate) <= 0.001f && length >= bestLength)) continue;

                best = tour;
                bestRate = rate;
                bestLength = length;
            }
        }

        return best ?? new List<Stop>();
    }

    /// <summary>估一圈要幾秒。</summary>
    public static float Seconds(IReadOnlyList<Stop> tour, float length) => length / TravelSpeed + tour.Count * StopSeconds;

    /// <summary>估每分鐘採到幾個勾選的點。</summary>
    public static float PerMinute(IReadOnlyList<Stop> tour, float length)
    {
        var seconds = Seconds(tour, length);
        return seconds <= 0 ? 0 : tour.Count(s => !s.Filler) * 60f / seconds;
    }

    /// <summary>用離目前這些點最近的點（任何種類）補到 11 個。</summary>
    private static void Fill(List<Stop> set, List<Stop> all)
    {
        while (set.Count < CycleSize)
        {
            Stop? next = null;
            var nextDistance = float.MaxValue;
            foreach (var candidate in all)
            {
                if (set.Any(s => s.Point == candidate.Point)) continue;

                var d = set.Min(s => Vector3.DistanceSquared(s.Point, candidate.Point));
                if (d >= nextDistance) continue;
                nextDistance = d;
                next = candidate;
            }

            if (next == null) return;
            set.Add(next.Value);
        }
    }

    /// <summary>整圈長度（最後一個回到第一個）。</summary>
    public static float Length(List<Stop> tour)
    {
        var total = 0f;
        for (var i = 0; i < tour.Count; i++)
            total += Vector3.Distance(tour[i].Point, tour[(i + 1) % tour.Count].Point);
        return total;
    }

    /// <summary>從離 position 最近的點開始走（圈本身不變）。</summary>
    public static List<Stop> StartNear(List<Stop> tour, Vector3 position)
    {
        if (tour.Count == 0) return tour;

        var start = tour.IndexOf(tour.OrderBy(s => Vector3.DistanceSquared(s.Point, position)).First());
        return tour.Skip(start).Concat(tour.Take(start)).ToList();
    }

    private static IEnumerable<Stop> Points(IEnumerable<uint> types) =>
        types.Where(IslandGatherData.Points.ContainsKey)
            .SelectMany(t => IslandGatherData.Points[t].Select(p => new Stop(t, p, false)));

    /// <summary>先「每次走最近的」排一圈，再用 2-opt 把交叉的線拉開。</summary>
    private static List<Stop> Tour(List<Stop> points)
    {
        var left = new List<Stop>(points);
        var tour = new List<Stop> { left[0] };
        left.RemoveAt(0);
        while (left.Count > 0)
        {
            var last = tour[^1].Point;
            var next = left.OrderBy(p => Vector3.DistanceSquared(p.Point, last)).First();
            tour.Add(next);
            left.Remove(next);
        }

        var improved = true;
        while (improved)
        {
            improved = false;
            for (var i = 0; i < tour.Count - 1; i++)
            {
                for (var j = i + 2; j < tour.Count; j++)
                {
                    var a = tour[i].Point;
                    var b = tour[i + 1].Point;
                    var c = tour[j].Point;
                    var d = tour[(j + 1) % tour.Count].Point;
                    if (Vector3.Distance(a, c) + Vector3.Distance(b, d) + 0.01f >= Vector3.Distance(a, b) + Vector3.Distance(c, d)) continue;

                    tour.Reverse(i + 1, j - i);
                    improved = true;
                }
            }
        }

        return tour;
    }

    // ───────────────────────── 地圖預覽 ─────────────────────────

    // 地圖預覽的縮放與位置（地圖圖片上的比例座標，0～1）
    private static float zoom = 1f;
    private static Vector2 center = new(0.5f, 0.5f);

    /// <summary>
    /// 在設定頁畫無人島地圖，標出所有採集點（灰）、這一圈的點（勾選的黃、補上的藍）和連線、自己的位置。
    /// 滑鼠滾輪放大縮小（以游標為中心）、按住左鍵拖曳移動。地圖圖片和座標換算都從遊戲的 Map 表讀。
    /// </summary>
    public static void DrawPreview(List<Stop> tour, float size)
    {
        var territory = Services.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(IslandGatherData.Territory);
        if (territory?.Map.ValueNullable is not { } map)
        {
            ImGui.TextDisabled("讀不到無人島的地圖。");
            return;
        }

        if (ImGui.SmallButton("放大")) SetZoom(zoom * 1.5f, center);
        ImGui.SameLine();
        if (ImGui.SmallButton("縮小")) SetZoom(zoom / 1.5f, center);
        ImGui.SameLine();
        if (ImGui.SmallButton("重設"))
        {
            zoom = 1f;
            center = new Vector2(0.5f, 0.5f);
        }

        ImGui.SameLine();
        ImGui.TextDisabled($"{zoom:0.0} 倍（滾輪縮放、拖曳移動）");

        var id = map.Id.ExtractText();
        var texture = Services.Textures.GetFromGame($"ui/map/{id}/{id.Replace("/", string.Empty)}_m.tex").GetWrapOrEmpty();

        // 地圖放在自己的小視窗裡（內容比視窗高 1 點），滾輪才會被地圖吃掉、不會捲動整個設定頁
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.BeginChild("##island-map", new Vector2(size), false, ImGuiWindowFlags.NoScrollbar);

        var origin = ImGui.GetCursorScreenPos();
        var half = 0.5f / zoom;
        var uv0 = center - new Vector2(half);
        ImGui.Image(texture.Handle, new Vector2(size), uv0, center + new Vector2(half));
        var hovered = ImGui.IsItemHovered();

        // 滾輪：以游標為中心縮放；拖曳：移動
        if (hovered)
        {
            var wheel = ImGui.GetIO().MouseWheel;
            if (wheel != 0)
            {
                var mouseUv = uv0 + (ImGui.GetMousePos() - origin) / size / zoom;
                var newZoom = Math.Clamp(zoom * MathF.Pow(1.25f, wheel), 1f, 8f);
                SetZoom(newZoom, mouseUv - ((ImGui.GetMousePos() - origin) / size - new Vector2(0.5f)) / newZoom);
            }

            if (ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                SetZoom(zoom, center - ImGui.GetIO().MouseDelta / size / zoom);
            }
        }

        half = 0.5f / zoom;
        uv0 = center - new Vector2(half);

        var draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(origin, origin + new Vector2(size), true);

        var scale = map.SizeFactor / 100f;
        Vector2 ToScreen(Vector3 p) => origin + (new Vector2(
            ((p.X + map.OffsetX) * scale + 1024f) / 2048f,
            ((p.Z + map.OffsetY) * scale + 1024f) / 2048f) - uv0) * zoom * size;

        foreach (var (_, points) in IslandGatherData.Points)
        {
            foreach (var p in points)
                draw.AddCircleFilled(ToScreen(p), 1.5f + zoom * 0.3f, 0x60FFFFFF);
        }

        for (var i = 0; i < tour.Count; i++)
            draw.AddLine(ToScreen(tour[i].Point), ToScreen(tour[(i + 1) % tour.Count].Point), 0xFF40D0FF, 2f);

        for (var i = 0; i < tour.Count; i++)
        {
            var at = ToScreen(tour[i].Point);
            draw.AddCircleFilled(at, 4f, tour[i].Filler ? 0xFFFFA040 : 0xFF30E0FF);
            draw.AddText(at + new Vector2(5, -7), 0xFFFFFFFF, $"{i + 1}");
        }

        if (IslandGather.OnIsland && Services.Objects.LocalPlayer is { } player)
            draw.AddCircle(ToScreen(player.Position), 6f, 0xFF40FF40, 0, 2f);

        draw.PopClipRect();

        ImGui.Dummy(new Vector2(1));
        ImGui.SetScrollY(0);
        ImGui.EndChild();
        ImGui.PopStyleVar();

        // 滑鼠移到點上顯示是哪一種
        if (!hovered) return;

        var mouse = ImGui.GetMousePos();
        foreach (var stop in tour)
        {
            if (Vector2.Distance(ToScreen(stop.Point), mouse) > 6f) continue;

            ImGui.SetTooltip($"{IslandGather.TypeName(stop.Type)}{(stop.Filler ? "（補上的）" : string.Empty)}");
            break;
        }
    }

    /// <summary>設定縮放倍數和中心，中心限制在地圖範圍內（不會拖到地圖外面）。</summary>
    private static void SetZoom(float newZoom, Vector2 newCenter)
    {
        zoom = Math.Clamp(newZoom, 1f, 8f);
        var half = 0.5f / zoom;
        center = new Vector2(Math.Clamp(newCenter.X, half, 1f - half), Math.Clamp(newCenter.Y, half, 1f - half));
    }
}
