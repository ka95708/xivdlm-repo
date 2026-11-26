using AetherKit.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui.Dtr;
using AetherKit.UI;

namespace AetherKit.Tweaks;

/// <summary>
/// 周圍玩家（DR 的 AutoCountPlayers）：
/// - 伺服器資訊列顯示周圍人數，有人選你為目標時後面多「被 n 人選中」；滑鼠移上去列出名單，點一下開關完整清單視窗。
/// - 有人選你為目標時，從你身上畫紅線連到他，他那一端標職業圖示和名字；你頭上標「被 n 人選為目標」。
/// - 新的人選你為目標時在聊天欄提示一次。
/// 戰鬥中資訊列那一格藏起來（PvP 例外）。只讀遊戲的物件表，不做任何操作。
/// </summary>
public sealed class NearbyPlayers : Tweak
{
    private sealed record Row(IPlayerCharacter Player, string Name, string World, string Job, uint JobId, float Distance, bool TargetingMe);

    private const int MinRange = 5;
    private const int MaxRange = 200;
    private const string DtrTitle = "AetherKit-NearbyPlayers";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    private static readonly Vector4 Orange = new(1f, 0.6f, 0.3f, 1f);
    private static readonly uint LineColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.15f, 0.15f, 0.85f));
    private static readonly uint DotColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.85f, 0.85f, 0.95f));
    private static readonly uint OrangeColor = ImGui.ColorConvertFloat4ToU32(Orange);
    private static readonly uint OutlineColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.15f, 0.05f, 1f));

    private IDtrBarEntry? entry;
    private volatile bool windowOpen;
    private List<Row> rows = new();
    private DateTime nextRefresh;
    private HashSet<ulong> watchers = new();

    public override string Id => "nearby-players";
    public override string Name => "周圍玩家";
    public override string Description => "伺服器資訊列顯示周圍人數；有人選你為目標時畫線標出是誰並在聊天欄提示，點資訊列可以開完整清單。";
    public override TweakGroup Group => TweakGroup.Interface;
    public override TweakKind Kind => TweakKind.Auto;
    public override string Where => "伺服器資訊列（畫面右上），點一下開清單視窗";

    private int ListRange => Math.Clamp(Config.NearbyPlayersRange, MinRange, MaxRange);

    protected override void OnDisable()
    {
        windowOpen = false;
        rows = new List<Row>();
        watchers.Clear();
        Services.Framework.RunOnFrameworkThread(() =>
        {
            entry?.Remove();
            entry = null;
        });
    }

    // ───────────────────── 遊戲執行緒：收集、資訊列、聊天提示 ─────────────────────

    public override void Update(DateTime now)
    {
        if (now < nextRefresh) return;
        nextRefresh = now + RefreshInterval;

        var collected = Collect();
        rows = collected;

        UpdateEntry(collected);
        NotifyNewWatchers(collected);
    }

    private void UpdateEntry(List<Row> list)
    {
        if (entry == null)
        {
            entry = Services.DtrBar.Get(DtrTitle);
            entry.OnClick = _ => windowOpen = !windowOpen;
        }

        // 戰鬥中藏起來（PvP 例外），跟 DR 一樣
        entry.Shown = !AetherKit.Core.GameState.InCombat || Services.ClientState.IsPvP;

        var watching = list.Count(r => r.TargetingMe);
        entry.Text = watching > 0 ? $"周圍 {list.Count} 人（被 {watching} 人選中）" : $"周圍 {list.Count} 人";

        var lines = list.Take(15).Select(r => $"{(r.TargetingMe ? "★ " : "")}{r.Name}@{r.World}  {r.Job}  {r.Distance:0} 米");
        var more = list.Count > 15 ? $"\n……還有 {list.Count - 15} 人" : string.Empty;
        entry.Tooltip = list.Count == 0
            ? $"{ListRange} 米內沒有其他玩家\n點一下開清單"
            : $"{ListRange} 米內的玩家（★ = 選你為目標）\n{string.Join("\n", lines)}{more}\n點一下開清單";
    }

    private void NotifyNewWatchers(List<Row> list)
    {
        var now = list.Where(r => r.TargetingMe).ToList();
        var ids = now.Select(r => r.Player.GameObjectId).ToHashSet();

        if (Config.NearbyPlayersChat)
        {
            foreach (var row in now.Where(r => !watchers.Contains(r.Player.GameObjectId)))
            {
                Notify.Info("周圍玩家", $"{row.Name}@{row.World}（{row.Job}）選你為目標");
                Log.Write($"被選為目標：{row.Name}@{row.World} {row.Job} 距離 {row.Distance:0.0}");
            }
        }

        watchers = ids;
    }

    private List<Row> Collect()
    {
        var list = new List<Row>();
        var me = Services.Objects.LocalPlayer;
        if (me == null) return list;

        foreach (var obj in Services.Objects)
        {
            if (obj is not IPlayerCharacter player || player.GameObjectId == me.GameObjectId) continue;

            var distance = Vector3.Distance(me.Position, player.Position);
            if (distance > ListRange) continue;

            var job = player.ClassJob.IsValid ? player.ClassJob.Value.Abbreviation.ExtractText() : "?";
            var world = player.HomeWorld.IsValid ? player.HomeWorld.Value.Name.ExtractText() : string.Empty;
            list.Add(new Row(player, player.Name.TextValue, world, job, player.ClassJob.RowId, distance, player.TargetObjectId == me.GameObjectId));
        }

        list.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return list;
    }

    // ───────────────────── 繪製：連線、清單視窗 ─────────────────────

    public override void Draw()
    {
        var current = rows;

        if (Config.NearbyPlayersLines)
            DrawWatcherLines(current);

        if (windowOpen)
            DrawWindow(current);
    }

    /// <summary>從自己畫紅線到每個選你為目標的人，他那一端標職業圖示和名字；自己頭上標人數。</summary>
    private static void DrawWatcherLines(List<Row> list)
    {
        var watchingRows = list.Where(r => r.TargetingMe).ToList();
        if (watchingRows.Count == 0) return;

        var me = Services.Objects.LocalPlayer;
        if (me == null) return;

        var draw = ImGui.GetForegroundDrawList();
        var viewport = ImGui.GetMainViewport();

        if (!Services.GameGui.WorldToScreen(me.Position, out var start))
            start = new Vector2(viewport.Pos.X + viewport.Size.X / 2, viewport.Pos.Y + viewport.Size.Y);

        foreach (var row in watchingRows)
        {
            if (!Services.GameGui.WorldToScreen(row.Player.Position, out var end)) continue;

            draw.AddLine(start, end, LineColor, 5f);
            draw.AddCircleFilled(start, 7f, DotColor);
            draw.AddCircleFilled(end, 7f, DotColor);

            // 對方那一端：職業圖示 + 名字
            var labelPos = end + new Vector2(12f, -12f);
            var icon = Icons.Get(62100 + row.JobId);
            if (icon != null)
            {
                draw.AddImage(icon.Handle, labelPos, labelPos + new Vector2(24f));
                labelPos += new Vector2(28f, 0);
            }

            OutlinedText(draw, labelPos + new Vector2(0, 4f), row.Name);
        }

        // 自己頭上：被幾個人選為目標
        if (Services.GameGui.WorldToScreen(me.Position + new Vector3(0, 2.4f, 0), out var head))
        {
            var text = $"被 {watchingRows.Count} 人選為目標";
            OutlinedText(draw, head - new Vector2(ImGui.CalcTextSize(text).X / 2, 0), text);
        }
    }

    private static void OutlinedText(ImDrawListPtr draw, Vector2 pos, string text)
    {
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        {
            if (dx != 0 || dy != 0)
                draw.AddText(pos + new Vector2(dx, dy), OutlineColor, text);
        }

        draw.AddText(pos, OrangeColor, text);
    }

    private void DrawWindow(List<Row> list)
    {
        var open = true;
        ImGui.SetNextWindowSize(new Vector2(380, 300), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("周圍玩家###aetherkit-nearby-players", ref open))
            DrawContent(list);

        ImGui.End();

        // 關掉視窗只是收起清單，資訊列和連線照常
        if (!open) windowOpen = false;
    }

    private void DrawContent(List<Row> list)
    {
        var watching = list.Count(r => r.TargetingMe);

        ImGui.TextUnformatted($"{ListRange} 米內共 {list.Count} 人");
        if (watching > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Orange, $"（{watching} 人選你為目標）");
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        var range = ListRange;
        if (ImGui.SliderInt("##range", ref range, MinRange, MaxRange, "%d 米"))
        {
            Config.NearbyPlayersRange = range;
            Save();
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY
                                      | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##players", 4, flags)) return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("名稱", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("伺服器", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableSetupColumn("職業", ImGuiTableColumnFlags.WidthFixed, 40);
        ImGui.TableSetupColumn("距離", ImGuiTableColumnFlags.WidthFixed, 50);
        ImGui.TableHeadersRow();

        foreach (var row in list)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();

            if (row.TargetingMe) ImGui.PushStyleColor(ImGuiCol.Text, Orange);
            if (ImGui.Selectable($"{row.Name}##{row.Player.GameObjectId}", false, ImGuiSelectableFlags.SpanAllColumns))
            {
                var player = row.Player;
                Services.Framework.RunOnFrameworkThread(() => Services.Targets.Target = player);
            }

            if (row.TargetingMe) ImGui.PopStyleColor();
            if (row.TargetingMe && ImGui.IsItemHovered()) ImGui.SetTooltip("正在選你為目標");

            ImGui.TableNextColumn();
            ImGui.TextDisabled(row.World);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Job);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{row.Distance:0.0}");
        }

        ImGui.EndTable();
    }

    // ───────────────────── 設定 ─────────────────────

    public override bool HasSettings => true;

    public override void DrawSettings()
    {
        var range = ListRange;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("統計幾米內的玩家", ref range, MinRange, MaxRange, "%d 米"))
        {
            Config.NearbyPlayersRange = range;
            Save();
        }

        var lines = Config.NearbyPlayersLines;
        if (ImGui.Checkbox("有人選我為目標時畫線標出是誰", ref lines))
        {
            Config.NearbyPlayersLines = lines;
            Save();
        }

        var chat = Config.NearbyPlayersChat;
        if (ImGui.Checkbox("有人選我為目標時在聊天欄提示", ref chat))
        {
            Config.NearbyPlayersChat = chat;
            Save();
        }

        if (ImGui.Button(windowOpen ? "收起清單視窗" : "打開清單視窗"))
            windowOpen = !windowOpen;

        ImGui.SameLine();
        ImGui.TextDisabled("也可以點伺服器資訊列上的「周圍 n 人」");
    }
}
