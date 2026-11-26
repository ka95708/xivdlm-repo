using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherKit.Core;
using AetherKit.Game;
using AetherKit.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace AetherKit.Modules;

/// <summary>
/// 無人島採集（IslandGather）：勾選想要的素材、設目標數量，用能採到這些素材的採集點挑 11 個排成一圈一直繞
/// （採 11 個後第一個會重生），走或飛過去採，不瞬移。需要 vnavmesh；要先自己切到收穫模式。
/// </summary>
public sealed class IslandGatherModule : IModule
{
    private static readonly Vector4 Green = new(0.5f, 0.9f, 0.5f, 1f);
    private static readonly Vector4 Red = new(1f, 0.4f, 0.4f, 1f);

    // 工房排程表開著時每秒讀一次預訂量
    private DateTime nextReservedRead;

    private readonly Plugin plugin;

    // 路線預覽：要繞的採集點沒變就不重算
    private List<IslandRoute.Stop> preview = new();
    private string previewKey = string.Empty;

    public IslandGatherModule(Plugin plugin)
    {
        this.plugin = plugin;
        Runner = new IslandGather(plugin);
    }

    public IslandGather Runner { get; }

    private IslandGatherConfig Config => plugin.Config.IslandGather;

    public string Id => IslandGather.Owner;
    public string Name => "無人島採集";
    public ModuleGroup Group => ModuleGroup.Automation;
    public bool Running => Runner.Running;

    public string Description =>
        "在無人島勾選想要的素材、設目標數量，用能採到它們的採集點挑 11 個排成一圈一直繞（採 11 個後第一個會重生），走或飛過去採，不瞬移。";

    public void Enable(WindowSystem windows) { }

    public void Disable(WindowSystem windows) => Runner.Stop("模組已停用");

    /// <summary>工房排程表開著時，每秒讀一次本週＋下週預訂要用的素材，有變就存起來。</summary>
    public void AfterWindows()
    {
        var now = DateTime.Now;
        if (now < nextReservedRead) return;
        nextReservedRead = now.AddSeconds(1);

        if (IslandWorkshop.ReadReserved() is not { } reserved) return;

        var changed = reserved.Count != Config.Reserved.Count
                      || reserved.Any(r => Config.Reserved.GetValueOrDefault(r.Key) != r.Value);
        if (!changed && now - Config.ReservedAt < TimeSpan.FromMinutes(5)) return;

        Config.Reserved = reserved;
        Config.ReservedAt = now;
        plugin.Save();
    }

    /// <summary>頁首：控制列 → 狀態 → 用法 → 需要的東西（版面規則見 ModuleUi）。</summary>
    public void DrawHeader()
    {
        if (ModuleUi.Action(Runner.Running ? "停止" : "開始"))
            Runner.Toggle();
        ModuleUi.Command("/aetherkit island");

        ModuleUi.Status(Runner.Status);
        ModuleUi.FinishPending(plugin.Core, "無人島採集");

        if (Runner.Running && Runner.Exploring)
        {
            ModuleUi.Detail("補齊對應表", $"第 {Math.Min(Runner.RouteIndex + 1, Runner.RouteLength)} / {Runner.RouteLength} 種");
        }
        else if (Runner.Running)
        {
            var loop = Config.Loops > 0 ? $"第 {Runner.Loop + 1} / {Config.Loops} 圈" : $"第 {Runner.Loop + 1} 圈";
            ModuleUi.Detail("進度", $"{loop}，第 {Math.Min(Runner.RouteIndex + 1, Runner.RouteLength)} / {Runner.RouteLength} 個點");
        }

        if (Runner.Running)
        {
            ModuleUi.Detail("已採", $"{Runner.Gathered} 次");
            ModuleUi.Detail("已執行", $"{DateTime.Now - Runner.StartedAt:hh\\:mm\\:ss}");
        }

        if (Runner.TargetsReached(out var detail) || detail.Length > 0)
            ModuleUi.Detail("目標", detail);

        ModuleUi.Detail("用法", $"在無人島切到「{IslandGather.GatherModeName}」模式再按開始；路線在「路線」分頁先看");
        ModuleUi.Dependencies(new[]
        {
            new ModuleUi.Dependency("vnavmesh", "走路、飛行", ModuleUi.DependencyLevel.Required,
                Services.PluginInterface.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName == "vnavmesh"))
        });
    }

    public IEnumerable<ModuleTab> SettingsTabs
    {
        get
        {
            yield return new ModuleTab("設定", DrawSettings);
            yield return new ModuleTab("路線", DrawRoute);
        }
    }

    private void DrawSettings()
    {
        DrawMaterials();

        ModuleUi.Section("移動");

        var mount = Config.MountDistance;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("超過幾米才上坐騎", ref mount))
        {
            Config.MountDistance = Math.Clamp(mount, 0, 500);
            plugin.Save();
        }

        ModuleUi.Note("近的點直接走，省掉上下坐騎的時間。水下的點一定會騎飛行坐騎飛進水裡。");

        ModuleUi.Section("停止");

        var loops = Config.Loops;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("繞幾圈", ref loops))
        {
            Config.Loops = Math.Clamp(loops, 0, 999);
            plugin.Save();
        }

        ModuleUi.Note($"一圈 = {IslandRoute.CycleSize} 個點；0 = 一直繞。");

        var cap = Config.StopAtCap;
        if (ImGui.Checkbox("採到持有數量上限就停", ref cap))
        {
            Config.StopAtCap = cap;
            plugin.Save();
        }

        var after = Config.AfterFinish;
        if (ModuleUi.FinishActionCombo("做完之後", ref after))
        {
            Config.AfterFinish = after;
            plugin.Save();
        }

        ModuleUi.Note($"繞完圈數、到上限、勾選的素材全部到目標都算做完。倒數 {FinishService.CountdownSeconds} 秒，可以在上面取消；手動停止、出錯不會登出或關閉。");
    }

    /// <summary>
    /// 素材清單：勾選想要的、目前庫存、目標數量、在哪幾種採集點採得到（照對應表）。
    /// 上面是「補齊對應表」：還沒記錄過的採集點各去採一個。
    /// </summary>
    private void DrawMaterials()
    {
        ModuleUi.Section("想要的素材");

        var missing = Runner.MissingTypes();
        if (missing.Count > 0)
        {
            if (Runner.Running) ImGui.BeginDisabled();
            if (ImGui.Button($"補齊對應表（還差 {missing.Count} 種採集點）"))
                Services.Framework.RunOnFrameworkThread(Runner.StartExplore);
            if (Runner.Running) ImGui.EndDisabled();

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("還沒記錄過的採集點各去採一個（排成最短的一趟），記下會採到哪些素材：\n" +
                                 string.Join("、", missing.Select(IslandGather.TypeName)));

            ModuleUi.Note("「在哪裡採」是實際採的時候記下來的；要工具或開拓等級才會出的素材，採到時才會補上。");
        }

        ModuleUi.Note("勾選想要的素材；設了目標的到量後不再為它繞路，勾選的全部都設了目標、也都到量就停。");
        ModuleUi.Note(Config.ReservedAt == DateTime.MinValue
            ? "「預訂」= 工房本週＋下週排程要用掉的數量：打開無人島工房的排程表就會讀到。庫存不夠用時顯示紅字。"
            : $"「預訂」= 工房本週＋下週排程要用掉的數量（{Config.ReservedAt:MM/dd HH:mm} 打開排程表時讀到的，排程改了再打開一次就會更新）。庫存不夠用時顯示紅字。");

        var counts = IslandPouch.Counts();
        if (counts == null) ModuleUi.Note("要在無人島上才看得到庫存。");

        if (ImGui.SmallButton("全不勾"))
        {
            Config.Items.Clear();
            plugin.Save();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("清除全部目標"))
        {
            Config.Targets.Clear();
            plugin.Save();
        }

        // 庫存不夠工房預訂用的：勾起來、目標設成預訂量
        var shortItems = counts == null
            ? new List<uint>()
            : IslandPouch.MaterialItems().Where(i => counts.GetValueOrDefault(i) < Config.Reserved.GetValueOrDefault(i)).ToList();

        ImGui.SameLine();
        if (shortItems.Count == 0) ImGui.BeginDisabled();
        if (ImGui.SmallButton($"把不足的設成目標（{shortItems.Count} 項）"))
        {
            foreach (var item in shortItems)
            {
                Config.Items.Add(item);
                Config.Targets[item] = Math.Max(Config.Targets.GetValueOrDefault(item), Config.Reserved[item]);
            }

            plugin.Save();
        }

        if (shortItems.Count == 0) ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(shortItems.Count == 0
                ? "沒有庫存不夠工房預訂用的素材（要在島上、而且讀過工房排程表）。"
                : "庫存比工房本週＋下週預訂量少的素材：勾起來，目標設成預訂量（原本的目標比較高就不動）。\n" +
                  string.Join("、", shortItems.Select(i => IslandPouch.ItemName(i).Replace("無人島", string.Empty))));

        // 不給表格自己的捲軸（跟整頁的捲軸疊在一起很難用），整頁一起捲；點表頭排序
        if (!ImGui.BeginTable("##island-materials", 6,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Sortable))
            return;

        ImGui.TableSetupColumn("採");
        ImGui.TableSetupColumn("素材", ImGuiTableColumnFlags.DefaultSort);
        ImGui.TableSetupColumn("庫存", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("預訂", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("目標", ImGuiTableColumnFlags.PreferSortDescending);
        ImGui.TableSetupColumn("在哪裡採", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var item in SortedMaterials(counts))
        {
            ImGui.PushID((int)item);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var on = Config.Items.Contains(item);
            if (ImGui.Checkbox("##on", ref on))
            {
                if (on) Config.Items.Add(item);
                else Config.Items.Remove(item);
                plugin.Save();
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(IslandPouch.ItemName(item).Replace("無人島", string.Empty));

            // 庫存：不夠工房預訂用的紅字；到目標綠字
            ImGui.TableNextColumn();
            var target = Config.Targets.GetValueOrDefault(item);
            var reserved = Config.Reserved.GetValueOrDefault(item);
            var count = counts?.GetValueOrDefault(item);
            var text = count?.ToString() ?? "—";
            if (count != null && count < reserved) ImGui.TextColored(Red, text);
            else if (count != null && target > 0 && count >= target) ImGui.TextColored(Green, text);
            else ImGui.TextUnformatted(text);

            if (count != null && count < reserved && ImGui.IsItemHovered())
                ImGui.SetTooltip($"工房本週＋下週預訂要用 {reserved} 個，還差 {reserved - count}");

            ImGui.TableNextColumn();
            if (reserved > 0) ImGui.TextUnformatted(reserved.ToString());
            else ImGui.TextDisabled("—");

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("##target", ref target, 0))
            {
                target = Math.Clamp(target, 0, 9999);
                if (target > 0) Config.Targets[item] = target;
                else Config.Targets.Remove(item);
                plugin.Save();
            }

            ImGui.TableNextColumn();
            var sources = IslandGatherData.Points.Keys.Where(t => Runner.YieldsOf(t).Contains(item)).Select(IslandGather.TypeName).ToList();
            if (sources.Count > 0) ImGui.TextUnformatted(string.Join("、", sources));
            else ImGui.TextDisabled("還不知道");

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// 照表頭點選的欄位排序素材（第一次是背包的順序）。數字一樣時維持背包的順序。
    /// 欄位：0 勾選、1 素材（背包順序）、2 庫存、3 預訂、4 目標、5 在哪裡採。
    /// </summary>
    private List<uint> SortedMaterials(Dictionary<uint, int>? counts)
    {
        var items = IslandPouch.MaterialItems();
        var order = items.Select((item, index) => (item, index)).ToDictionary(p => p.item, p => p.index);

        var specs = ImGui.TableGetSortSpecs();
        if (specs.SpecsCount == 0) return items;

        var column = specs.Specs.ColumnIndex;
        var descending = specs.Specs.SortDirection == ImGuiSortDirection.Descending;

        Func<uint, IComparable> key = column switch
        {
            0 => i => Config.Items.Contains(i) ? 1 : 0,
            2 => i => counts?.GetValueOrDefault(i) ?? 0,
            3 => i => Config.Reserved.GetValueOrDefault(i),
            4 => i => Config.Targets.GetValueOrDefault(i),
            5 => i => string.Join("、", IslandGatherData.Points.Keys.Where(t => Runner.YieldsOf(t).Contains(i)).Select(IslandGather.TypeName)),
            _ => i => order[i]
        };

        var sorted = descending
            ? items.OrderByDescending(key).ThenBy(i => order[i])
            : items.OrderBy(key).ThenBy(i => order[i]);
        return sorted.ToList();
    }

    /// <summary>地圖上畫出要繞的那一圈：執行中畫正在繞的，沒在跑時照目前的勾選算一圈給你看。</summary>
    private void DrawRoute()
    {
        List<IslandRoute.Stop> tour;
        if (Runner.Running && !Runner.Exploring)
        {
            tour = Runner.Tour.ToList();
        }
        else
        {
            var types = Runner.WantedTypes(out var unknown);
            var key = string.Join(",", types.OrderBy(t => t));
            if (key != previewKey)
            {
                previewKey = key;
                preview = IslandRoute.Plan(types);
            }

            tour = preview;

            if (unknown.Count > 0)
                ModuleUi.Note($"還不知道在哪裡採（不算進路線）：{string.Join("、", unknown.Select(IslandPouch.ItemName))}。到「設定」分頁按「補齊對應表」。");

            if (types.Count > 0)
                ModuleUi.Note($"會採到勾選素材的採集點：{string.Join("、", types.Select(IslandGather.TypeName))}");
        }

        if (tour.Count == 0)
        {
            ModuleUi.Note("在「設定」分頁勾選想要的素材，這裡就會畫出路線。");
            return;
        }

        var fillers = tour.Count(s => s.Filler);
        var length = IslandRoute.Length(tour);
        ImGui.TextUnformatted($"一圈 {tour.Count} 個點，約 {length:0} 米{(fillers > 0 ? $"（其中 {fillers} 個是補上的）" : string.Empty)}");
        ImGui.TextUnformatted($"估計一圈約 {IslandRoute.Seconds(tour, length):0} 秒，每分鐘採到想要的 {IslandRoute.PerMinute(tour, length):0.0} 次");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"粗估：移動平均每秒 {IslandRoute.TravelSpeed:0} 米，每個點停 {IslandRoute.StopSeconds:0} 秒（下坐騎、採集、上坐騎）。");

        ModuleUi.Note("黃點是會採到想要素材的、藍點是補上的，數字是走的順序（開始時從離你最近的點開始）；綠圈是你的位置。滑鼠移到點上看是哪一種。");

        var size = Math.Min(ImGui.GetContentRegionAvail().X, 520 * ImGuiHelpers.GlobalScale);
        IslandRoute.DrawPreview(tour, size);

        ImGui.Spacing();
        for (var i = 0; i < tour.Count; i++)
        {
            var stop = tour[i];
            ImGui.TextDisabled($"{i + 1,2}. {IslandGather.TypeName(stop.Type)}{(stop.Filler ? "（補上的）" : string.Empty)}  ({stop.Point.X:0}, {stop.Point.Y:0}, {stop.Point.Z:0})");
        }
    }

    public bool HasSettings(Configuration config) => true;

    public void CopySettings(Configuration from, Configuration to)
    {
        var f = from.IslandGather;
        to.IslandGather = new IslandGatherConfig
        {
            Items = new HashSet<uint>(f.Items),
            Targets = new Dictionary<uint, int>(f.Targets),
            Yields = f.Yields.ToDictionary(y => y.Key, y => new HashSet<uint>(y.Value)),
            Loops = f.Loops,
            StopAtCap = f.StopAtCap,
            MountDistance = f.MountDistance,
            AfterFinish = f.AfterFinish,
        };
    }

    public void Dispose() => Runner.Dispose();
}
