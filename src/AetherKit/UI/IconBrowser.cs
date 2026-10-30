using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AetherKit.UI;

/// <summary>
/// 圖示瀏覽器。遊戲圖示有數萬張，所以只向貼圖服務要「畫面上看得到」的那幾列，
/// 捲到哪才載到哪，不然開一次就把記憶體吃光。
/// </summary>
public class IconBrowser : Window
{
    private readonly record struct Range(string Group, string Label, int Start, int End);

    // 範圍參考 QoLBar 的整理（UI/IconBrowserUI.cs），那是目前最完整的一份。
    // 範圍只是大概，裡面不是每個 ID 都有圖 — 實際有哪些由 ExistingIcons 掃描決定
    private static readonly Range[] Presets =
    {
        new("常用", "系統", 0, 100),
        new("常用", "職業", 62000, 62600),
        new("常用", "套裝", 62800, 62900),
        new("常用", "巨集", 66000, 66400),
        new("常用", "部隊徽章", 90000, 100000),

        new("介面", "介面", 60000, 61000),
        new("介面", "介面 2", 61390, 62000),
        new("介面", "標記", 61200, 61250),
        new("介面", "標記 2", 61290, 61390),
        new("介面", "地圖標記", 63900, 64000),
        new("介面", "郵票", 64500, 64550),
        new("介面", "貨幣", 65000, 65900),
        new("介面", "成就／狩獵筆記", 62900, 63200),
        new("介面", "釣魚", 65900, 66000),
        new("介面", "標籤", 66400, 66500),
        new("介面", "時尚檢查", 67000, 68000),
        new("介面", "任務", 70960, 71450),
        new("介面", "青魔介面", 72000, 72500),
        new("介面", "資格任務", 80000, 80200),
        new("介面", "神兵利器", 80730, 81000),
        new("介面", "雜項介面", 82000, 82100),
        new("介面", "部隊階級", 83000, 84000),

        new("動作", "技能", 100, 4000),
        new("動作", "特性", 5100, 8000),
        new("動作", "時尚動作", 8000, 9000),
        new("動作", "PvP", 9000, 10000),
        // 台服的情感動作圖示在 64000 段；國際服新版移到的 246000 段在台服是空的
        new("動作", "情感動作", 64000, 64500),
        new("動作", "活動", 19600, 19800),
        new("動作", "坐騎動作", 19800, 20000),

        new("坐騎寵物", "坐騎", 4000, 4400),
        new("坐騎寵物", "迷你怪", 4400, 5100),
        new("坐騎寵物", "坐騎 2", 59000, 59400),
        new("坐騎寵物", "迷你怪道具", 59400, 60000),
        new("坐騎寵物", "坐騎圖鑑", 68000, 68400),
        new("坐騎寵物", "迷你怪圖鑑", 68400, 69000),

        new("道具", "一般道具", 20000, 30000),
        new("道具", "房屋", 50000, 54000),
        new("道具", "時尚道具", 58000, 59000),
        new("道具", "裝備", 30000, 50000),
        new("道具", "腰帶", 54000, 54225),
        new("道具", "花卉", 54225, 54400),
        new("道具", "特殊裝備", 54400, 58000),
        new("道具", "眼鏡", 200000, 210000),

        new("地圖", "世界地圖", 62620, 62800),
        new("地圖", "區域地圖", 63200, 63875),
        new("地圖", "開場標誌", 61000, 61100),

        new("其他", "狀態效果", 10000, 19600),
        new("其他", "九宮幻卡", 87000, 90000),
        new("其他", "拍照模式", 230000, 230850),
        new("其他", "拍照職業", 230850, 231000),
        new("其他", "陸行鳥競賽", 180000, 180060),
        new("其他", "麻將", 76000, 76200),
        new("其他", "冒險者名牌", 190000, 200000)
    };

    private const float IconSize = 44f;

    private readonly Plugin plugin;

    /// <summary>要把圖示套用到哪：顯示用的名稱，與讀寫圖示 ID 的方法。</summary>
    private (string Label, Func<uint> Get, Action<uint> Set)? target;
    private int rangeIndex;
    private int customStart;
    private int customEnd = 200;
    private bool useCustom;
    private string search = string.Empty;

    public IconBrowser(Plugin plugin) : base("AetherKit 圖示瀏覽器###AetherKitIcons")
    {
        this.plugin = plugin;
        Size = new Vector2(560, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void OpenFor(ActionItem item) =>
        OpenFor(item.Label, () => item.IconId, id => item.IconId = id);

    /// <summary>不是項目的東西（例如游標提示）也能挑圖示。</summary>
    public void OpenFor(string label, Func<uint> get, Action<uint> set)
    {
        target = (label, get, set);
        IsOpen = true;
    }

    public override void Draw()
    {
        if (target == null)
        {
            ImGui.TextDisabled("先在右鍵選單或設定視窗按「瀏覽…」。");
            return;
        }

        DrawHeader();
        ImGui.Separator();

        var (start, end) = CurrentRange();
        var ids = ExistingIcons(start, end);

        if (ids == null)
            ImGui.TextDisabled("找圖示中…");
        else if (ids.Length == 0)
            ImGui.TextDisabled("這個範圍在這個客戶端沒有圖示。");
        else
            DrawGrid(ids);
    }

    private void DrawHeader()
    {
        var (label, get, set) = target!.Value;
        ImGui.TextUnformatted($"套用到：{(string.IsNullOrEmpty(label) ? "（未命名）" : label)}");

        var current = (int)get();
        if (ImGui.InputInt("目前圖示 ID", ref current))
        {
            set((uint)Math.Max(0, current));
            plugin.Save();
        }

        ImGui.SameLine();
        PreviewIcon(get(), 32f);

        if (ImGui.InputTextWithHint("##search", "直接輸入 ID 跳過去", ref search, 16)
            && int.TryParse(search, out var jump) && jump > 0)
        {
            useCustom = true;
            customStart = Math.Max(0, jump - 50);
            customEnd = jump + 450;
        }

        ImGui.Checkbox("自訂範圍", ref useCustom);

        if (useCustom)
        {
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("起##s", ref customStart, 0))
                customStart = Math.Max(0, customStart);

            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("迄##e", ref customEnd, 0))
                customEnd = Math.Clamp(customEnd, customStart + 1, customStart + 5000);
        }
        else
        {
            var preset = Presets[rangeIndex];

            if (ImGui.BeginCombo("範圍", $"{preset.Group} / {preset.Label}"))
            {
                var group = string.Empty;

                for (var i = 0; i < Presets.Length; i++)
                {
                    // 同群組的排在一起，群組換了就插一條分隔線當標題
                    if (Presets[i].Group != group)
                    {
                        group = Presets[i].Group;
                        ImGui.Separator();
                        ImGui.TextDisabled(group);
                    }

                    if (ImGui.Selectable($"　{Presets[i].Label}", i == rangeIndex))
                        rangeIndex = i;
                }

                ImGui.EndCombo();
            }
        }
    }

    private (int Start, int End) CurrentRange() =>
        useCustom ? (customStart, Math.Max(customStart + 1, customEnd)) : (Presets[rangeIndex].Start, Presets[rangeIndex].End);

    /// <summary>
    /// 每個範圍實際有圖的 ID。第一次打開某個範圍時在背景掃一次，之後就用快取：
    /// 一個 ID 查一次檔案很快，但大範圍有上萬個，不該卡住畫面。
    /// 回傳 null 代表還在掃。
    /// </summary>
    private uint[]? ExistingIcons(int start, int end)
    {
        if (existing.TryGetValue((start, end), out var ids))
            return ids;

        if (scanning.Add((start, end)))
        {
            Task.Run(() =>
            {
                var found = new List<uint>();

                for (var id = (uint)start; id < end; id++)
                {
                    if (Services.Data.FileExists($"ui/icon/{id / 1000 * 1000:000000}/{id:000000}.tex"))
                        found.Add(id);
                }

                existing[(start, end)] = found.ToArray();
            });
        }

        return null;
    }

    private readonly ConcurrentDictionary<(int Start, int End), uint[]> existing = new();
    private readonly HashSet<(int Start, int End)> scanning = new();

    /// <summary>只排有圖的 ID，所以格線裡不會有空洞。</summary>
    private void DrawGrid(uint[] ids)
    {
        if (!ImGui.BeginChild("icons", new Vector2(0, 0), false))
        {
            ImGui.EndChild();
            return;
        }

        var spacing = ImGui.GetStyle().ItemSpacing;
        var step = IconSize + spacing.X;
        var avail = ImGui.GetContentRegionAvail().X;
        var perRow = Math.Max(1, (int)((avail + spacing.X) / step));
        var total = ids.Length;
        var rows = (total + perRow - 1) / perRow;
        var rowHeight = IconSize + spacing.Y;

        // 手動虛擬化：先撐出完整高度，再只畫捲軸看得到的那幾列
        var scroll = ImGui.GetScrollY();
        var viewHeight = ImGui.GetWindowHeight();
        var firstRow = Math.Max(0, (int)(scroll / rowHeight) - 1);
        var lastRow = Math.Min(rows - 1, (int)((scroll + viewHeight) / rowHeight) + 1);

        ImGui.Dummy(new Vector2(0, firstRow * rowHeight));

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var col = 0; col < perRow; col++)
            {
                var index = row * perRow + col;
                if (index >= total) break;

                if (col > 0)
                    ImGui.SameLine();

                DrawIconButton(ids[index]);
            }
        }

        var remaining = rows - 1 - lastRow;
        if (remaining > 0)
            ImGui.Dummy(new Vector2(0, remaining * rowHeight));

        ImGui.EndChild();
    }

    /// <summary>單純畫一張圖，不吃點擊。</summary>
    private static void PreviewIcon(uint id, float size)
    {
        var texture = Icons.Get(id);
        var pos = ImGui.GetCursorScreenPos();
        ImGui.Dummy(new Vector2(size));

        if (texture != null)
            ImGui.GetWindowDrawList().AddImage(texture.Handle, pos, pos + new Vector2(size));
    }

    private void DrawIconButton(uint id)
    {
        var size = new Vector2(IconSize);

        // 貼圖可能還在載入，先佔好位置，載好了就會畫出來
        var texture = Icons.Get(id);
        var pos = ImGui.GetCursorScreenPos();

        if (ImGui.InvisibleButton($"icon{id}", size))
        {
            target!.Value.Set(id);
            plugin.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.GetWindowDrawList().AddRectFilled(pos, pos + size,
                Paint.Color(plugin.Config.Theme.RowHover), plugin.Config.Theme.Rounding);

        if (texture != null)
            ImGui.GetWindowDrawList().AddImage(texture.Handle, pos, pos + size);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(id.ToString());
    }

}
