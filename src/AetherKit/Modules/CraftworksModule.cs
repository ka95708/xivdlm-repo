using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherKit.Core;
using AetherKit.Game;
using AetherKit.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AetherKit.Modules;

/// <summary>
/// 無人島：
/// - 開拓工房：讀遊戲的歡迎度、每天的供需、幹勁、工坊、貓耳小員，每天自動記下供需，
///   用 island.ffxiv.cyou 的求解器（mji-craftwork，移植到 Game/Workshop）預測走勢、排本週或下週的班，按「套用」寫進遊戲。
/// - 開拓穀倉：穀倉視窗上方的「領取並派遣」。
/// </summary>
public sealed partial class CraftworksModule : IModule
{
    private readonly Plugin plugin;
    private readonly FeatureLog log = FeatureLog.For("craftworks");
    private readonly CraftworksApplier applier;

    private Craftworks.Snapshot? snapshot;
    private DateTime nextRead;
    private bool requested;
    private DateTime requestedAt;

    public CraftworksModule(Plugin plugin)
    {
        this.plugin = plugin;
        applier = new CraftworksApplier(log);
    }

    private CraftworksConfig Config => plugin.Config.Craftworks;

    public string Id => "island";
    public string Name => "無人島";
    public string Description => "開拓工房：自動讀歡迎度、供需、幹勁與貓耳小員，排出本週或下週的班，按「套用」寫進遊戲（算法來自 island.ffxiv.cyou）。開拓穀倉：一鍵領取並派遣。";

    private readonly FeatureLog granaryLog = FeatureLog.For("island-granary");
    private string granaryLast = string.Empty;
    private DateTime granaryBusyUntil;

    /// <summary>穀倉視窗上方的「領取並派遣」：兩個穀倉都領，再派去稀有資源庫存最少的地點、補滿 7 天。</summary>
    private void AddGranaryButton()
    {
        if (!Config.GranaryButton || !Addons.IsOpen(IslandFacilities.GranaryAddon)) return;

        plugin.AddonBar.Button(IslandFacilities.GranaryAddon, "領取並派遣", RunGranary, DateTime.Now >= granaryBusyUntil,
            granaryLast.Length == 0 ? "領取兩個穀倉，再派去稀有資源最少的地點、補滿 7 天" : $"上一次：\n{granaryLast}");
    }

    private void RunGranary()
    {
        // 送出後遊戲要一下子才更新，這段時間不給連按
        granaryBusyUntil = DateTime.Now.AddSeconds(2);

        var steps = IslandFacilities.CollectAndDispatchGranaries();
        granaryLast = string.Join("\n", steps);
        foreach (var step in steps) granaryLog.Write(step);
    }

    public void Enable(WindowSystem windows) { }

    public void Disable(WindowSystem windows) => applier.Stop();

    public void Dispose() => applier.Stop();

    public bool HasSettings(Configuration config) => true;

    /// <summary>匯出入只帶設定；每天記的供需是這台電腦自己的紀錄，留著對方的。</summary>
    public void CopySettings(Configuration from, Configuration to)
    {
        var f = from.Craftworks;
        to.Craftworks = new CraftworksConfig
        {
            Observations = to.Craftworks.Observations,
            MaxGroove = f.MaxGroove,
            WorkshopLevel = f.WorkshopLevel,
            Workshops = f.Workshops,
            IncludeFavors = f.IncludeFavors,
            QueueKinds = f.QueueKinds,
            GranaryButton = f.GranaryButton,
            Banned = new HashSet<uint>(f.Banned),
        };
    }

    // ── 每幀 ─────────────────────────────────────────────────

    public void AfterWindows()
    {
        var now = DateTime.Now;
        applier.Update(now);

        if (Services.ClientState.IsLoggedIn) AddGranaryButton();

        // 排班表關掉：下次打開再跟伺服器要一次資料
        var scheduleOpen = Addons.IsOpen(Craftworks.ScheduleAddon);
        if (!scheduleOpen) requested = false;

        if (now < nextRead) return;
        nextRead = now.AddSeconds(1);

        if (scheduleOpen && !requested)
        {
            requested = true;
            requestedAt = now;
            Craftworks.RequestData();
            log.Write("要求今天的供需與貓耳小員資料");
        }

        snapshot = Craftworks.Read();

        // 記憶體裡的供需要等這次跟伺服器要完才是今天的（換日後還留著昨天那份），所以只在排班表開著、要過資料一下子之後記
        if (snapshot != null && scheduleOpen && requested && now - requestedAt > TimeSpan.FromSeconds(2) && Craftworks.HasSupplyData(snapshot))
            Record(snapshot);
    }

    /// <summary>今天的供需還沒記就記下來（一天記第一次讀到的；只留本週和上週）。</summary>
    private void Record(Craftworks.Snapshot s)
    {
        if (!Config.Observations.TryGetValue(s.WeekKey, out var week))
            Config.Observations[s.WeekKey] = week = new Dictionary<int, string>();

        if (week.ContainsKey(s.Day)) return;

        week[s.Day] = Convert.ToHexString(s.SupplyDemand);
        foreach (var old in Config.Observations.Keys.OrderByDescending(k => k, StringComparer.Ordinal).Skip(2).ToList())
            Config.Observations.Remove(old);

        plugin.Save();
        log.Write($"記錄 {s.WeekKey} 第 {s.Day + 1} 天的供需");
    }

    /// <summary>本週記到的每一天：第幾天 → 每個產品的供需位元組。</summary>
    private SortedDictionary<int, byte[]> WeekObservations(string weekKey)
    {
        var result = new SortedDictionary<int, byte[]>();
        if (!Config.Observations.TryGetValue(weekKey, out var week)) return result;

        foreach (var (day, hex) in week)
        {
            try { result[day] = Convert.FromHexString(hex); }
            catch (FormatException) { }
        }

        return result;
    }

    // ── 頁面 ─────────────────────────────────────────────────

    public void DrawHeader()
    {
        if (snapshot is not { } s)
        {
            ModuleUi.Status("不在無人島（在島上打開排班表就會讀資料）");
            return;
        }

        var recorded = WeekObservations(s.WeekKey).Keys.Select(d => d + 1).ToList();
        ModuleUi.Status(applier.Running || applier.Status.Length > 0 ? applier.Status : $"本週第 {s.Day + 1} 天");
        ModuleUi.Detail("幹勁", $"{s.Groove} / {MaxGroove(s)}");
        ModuleUi.Detail("工坊", $"{Workshops(s)} 間，等級 {WorkshopLevel(s)}");
        ModuleUi.Detail("已記錄供需", recorded.Count == 0 ? "還沒有（在島上打開排班表就會記）" : $"第 {string.Join("、", recorded)} 天");
    }

    public IEnumerable<ModuleTab> SettingsTabs
    {
        get
        {
            yield return new ModuleTab("設定", DrawSettings);
            yield return new ModuleTab("需求與趨勢", DrawDemand);
            yield return new ModuleTab("排班", DrawPlan);
        }
    }

    private int MaxGroove(Craftworks.Snapshot s) => Config.MaxGroove > 0 ? Config.MaxGroove : s.MaxGroove;
    private int Workshops(Craftworks.Snapshot s) => Config.Workshops > 0 ? Config.Workshops : s.Workshops;
    private int WorkshopLevel(Craftworks.Snapshot s) => Config.WorkshopLevel > 0 ? Config.WorkshopLevel : s.WorkshopLevel;

    private void DrawSettings()
    {
        ModuleUi.Section("開拓工房");
        ModuleUi.Note("0 = 讀遊戲（在無人島時才讀得到）。跟遊戲不一樣時再自己填。");

        IntSetting("幹勁上限", Config.MaxGroove, 0, 45, v => Config.MaxGroove = v, snapshot?.MaxGroove);
        IntSetting("工坊數量", Config.Workshops, 0, 4, v => Config.Workshops = v, snapshot?.Workshops);
        IntSetting("工坊等級", Config.WorkshopLevel, 0, 5, v => Config.WorkshopLevel = v, snapshot?.WorkshopLevel);

        ModuleUi.Section("工房排班");

        var favors = Config.IncludeFavors;
        if (ImGui.Checkbox("把貓耳小員本週的需求排進去", ref favors))
        {
            Config.IncludeFavors = favors;
            plugin.Save();
        }

        var kinds = Config.QueueKinds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderInt("同一天最多幾種隊列", ref kinds, 1, 2))
        {
            Config.QueueKinds = Math.Clamp(kinds, 1, 2);
            plugin.Save();
        }

        ModuleUi.Note("1 = 每間工坊做一樣的（算得快）；2 = 可以分成兩種隊列（例如 3 間做一種、1 間做另一種），收益通常高一點、算比較久。");

        ModuleUi.Section("開拓穀倉");

        var granary = Config.GranaryButton;
        if (ImGui.Checkbox("穀倉視窗上方加「領取並派遣」", ref granary))
        {
            Config.GranaryButton = granary;
            plugin.Save();
        }

        ModuleUi.Note("按一下：兩個穀倉能領的先領（會超過 999 的不領），再派去稀有資源庫存最少的兩個地點、補滿 7 天（青船幣不夠就派付得起的天數）。");

        ModuleUi.Section("算法");
        ModuleUi.Note("排班算法移植自 island.ffxiv.cyou（mji-craftwork，Apache License 2.0）。");
    }

    private void IntSetting(string label, int value, int min, int max, Action<int> set, int? game)
    {
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt(label, ref value))
        {
            set(Math.Clamp(value, min, max));
            plugin.Save();
        }

        if (game is { } g)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"（遊戲：{g}）");
        }
    }

    // ── 需求與趨勢 ───────────────────────────────────────────

    private static readonly string[] SupplyNames = { "嚴重不足", "不足", "充足", "過剩", "嚴重過剩" };
    private static readonly string[] ShiftNames = { "大幅增加", "增加", "不變", "減少", "大幅減少" };
    private static readonly string[] PopularityNames = { "未知", "人氣火爆", "略受矚目", "普普通通", "無人問津" };

    private void DrawDemand()
    {
        if (snapshot is not { } s)
        {
            ModuleUi.Note("在無人島上才讀得到。");
            return;
        }

        var observed = WeekObservations(s.WeekKey);
        ModuleUi.Note("供給、需求變動是今天的；「走勢」用本週記到的幾天推算（第 1～4 天記越多越準）。點表頭可以排序。");

        const ImGuiTableFlags flags = ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Sortable;
        if (!ImGui.BeginTable("##craftworks-demand", 8, flags)) return;

        ImGui.TableSetupColumn("產品");
        ImGui.TableSetupColumn("時間");
        ImGui.TableSetupColumn("價格");
        ImGui.TableSetupColumn("本週歡迎度");
        ImGui.TableSetupColumn("下週歡迎度");
        ImGui.TableSetupColumn("今天供給");
        ImGui.TableSetupColumn("需求變動");
        ImGui.TableSetupColumn("走勢");
        ImGui.TableHeadersRow();

        var rows = Craftworks.Objects.Where(o => o.Id < s.SupplyDemand.Length).ToList();
        rows = SortDemandRows(rows, s);

        foreach (var o in rows)
        {
            var packed = s.SupplyDemand[o.Id];
            var pop = Craftworks.PopularityOf(s.Popularity, o.Id);
            var next = Craftworks.PopularityOf(s.NextPopularity, o.Id);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Craftworks.ShortName(o.Id));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join("、", new[] { Craftworks.ThemeName(o.Theme1), Craftworks.ThemeName(o.Theme2) }.Where(t => t.Length > 0)));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{o.Time}h");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(o.Value.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(PopularityNames[Math.Min(pop, (byte)4)]);
            ImGui.TableNextColumn();
            ImGui.TextDisabled(PopularityNames[Math.Min(next, (byte)4)]);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(SupplyNames[Math.Min(Craftworks.Supply(packed), 4)]);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(ShiftNames[Math.Min(Craftworks.DemandShift(packed), 4)]);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(TrendText(o.Id, observed));
        }

        ImGui.EndTable();
    }

    private static int demandSortColumn = -1;
    private static bool demandSortDescending;

    private static List<Craftworks.CraftObject> SortDemandRows(List<Craftworks.CraftObject> rows, Craftworks.Snapshot s)
    {
        var specs = ImGui.TableGetSortSpecs();
        if (specs.SpecsCount > 0)
        {
            demandSortColumn = specs.Specs.ColumnIndex;
            demandSortDescending = specs.Specs.SortDirection == ImGuiSortDirection.Descending;
        }

        Func<Craftworks.CraftObject, int> key = demandSortColumn switch
        {
            1 => o => o.Time,
            2 => o => o.Value,
            3 => o => Craftworks.PopularityOf(s.Popularity, o.Id),
            4 => o => Craftworks.PopularityOf(s.NextPopularity, o.Id),
            5 => o => Craftworks.Supply(s.SupplyDemand[o.Id]),
            6 => o => Craftworks.DemandShift(s.SupplyDemand[o.Id]),
            _ => o => (int)o.Id,
        };

        return (demandSortDescending ? rows.OrderByDescending(key) : rows.OrderBy(key)).ToList();
    }
}
