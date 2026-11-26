using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AetherKit.Core;
using AetherKit.Game;
using AetherKit.Game.Workshop;
using AetherKit.UI;
using Dalamud.Bindings.ImGui;
using MJICraftworksObject = Lumina.Excel.Sheets.MJICraftworksObject;
using MJICraftworksPopularity = Lumina.Excel.Sheets.MJICraftworksPopularity;

namespace AetherKit.Modules;

/// <summary>
/// 開拓工房：走勢預測與整週排班，接上移植的求解器（Game/Workshop，mji-craftwork）。
/// 求解器的整週結果是第 2～7 天（第 1 天固定不排，當作休息），其中再挑一天休息；可以算本週或下週（下週還沒有走勢，全部當需求普通）；
/// 「同一天最多 2 種隊列」要跑 30 組候選，一次約一兩分鐘，所以放在背景執行緒算。
/// </summary>
public sealed partial class CraftworksModule
{
    /// <summary>island.ffxiv.cyou 的預設：開拓等級上限（限制能做的配方等級）。</summary>
    private const byte MaxRecipeLevel = 20;

    private const byte HoursPerDay = 24;

    /// <summary>算出來的一整週排班：哪一週、每天（Cycle：本週 0～6、下週 7～13）、每天的預估收益。</summary>
    private sealed record PlanResult(string WeekKey, CraftworksApplier.DayPlan[] Days, int[] Values, DateTime At);

    /// <summary>[0] 本週、[1] 下週，各留一份。</summary>
    private readonly PlanResult?[] plans = new PlanResult?[2];

    /// <summary>排班頁正在看哪一週：0 本週、1 下週。</summary>
    private int planWeek;
    private Task? solving;
    private CancellationTokenSource? cancel;
    private volatile string solveStatus = string.Empty;

    // ── 走勢 ─────────────────────────────────────────────────

    /// <summary>用本週第 1 天起連續記到的幾天推算走勢（求解器的 Predict；第 1 天沒記到就無法推算）。</summary>
    private static DemandPattern PredictPattern(uint craftId, SortedDictionary<int, byte[]> observed)
    {
        var seq = new List<(Demand Demand, DemandChange Change)>();
        for (var day = 0; observed.TryGetValue(day, out var bytes); day++)
        {
            if (craftId >= bytes.Length) break;
            var (supply, change) = WorkshopSolverApi.DecodeDemandByte(bytes[craftId]);
            seq.Add((supply, change));
        }

        return seq.Count == 0 ? DemandPattern.Unknown : WorkshopSolverApi.Predict(seq.ToArray());
    }

    private static string TrendText(uint craftId, SortedDictionary<int, byte[]> observed)
    {
        if (!observed.ContainsKey(0)) return observed.Count == 0 ? "-" : "缺第 1 天";

        var pattern = PredictPattern(craftId, observed);
        if (pattern == DemandPattern.Unknown) return "還不確定";

        // Day2Strong = 1、Day2Weak = 2、Day3Strong = 3 …
        var value = (int)pattern - 1;
        return $"第 {value / 2 + 2} 天{(value % 2 == 0 ? "強" : "弱")}";
    }

    // ── 排班頁 ───────────────────────────────────────────────

    private void DrawPlan()
    {
        if (snapshot is not { } s)
        {
            ModuleUi.Note("在無人島上才讀得到資料。");
            return;
        }

        var busy = solving is { IsCompleted: false };

        ImGui.BeginDisabled(busy);
        if (ImGui.RadioButton("本週", planWeek == 0)) planWeek = 0;
        ImGui.SameLine();
        if (ImGui.RadioButton("下週", planWeek == 1)) planWeek = 1;
        ImGui.EndDisabled();

        var observed = WeekObservations(s.WeekKey);
        if (planWeek == 1)
            ModuleUi.Note("下週的歡迎度和貓耳小員已經知道，但需求走勢要到下週才看得出來，所以每樣產品都當作需求普通來排（跟 island.ffxiv.cyou 不設走勢時一樣）。下週開始後可以改算「本週」再套用一次。");
        else if (!observed.ContainsKey(0))
            ModuleUi.Note("本週第 1 天的供需沒有記到，所有產品都當作需求普通來排，結果會比較不準。");
        else if (observed.Count < 4 && s.Day < 4)
            ModuleUi.Note($"已記到 {observed.Count} 天；第 1～4 天每天打開一次排班表，走勢會越來越準，記到新的一天再重排一次。");

        var targetKey = planWeek == 0 ? s.WeekKey : Craftworks.NextWeekKey(s.WeekKey);
        var plan = plans[planWeek];

        if (busy)
        {
            ImGui.TextUnformatted(solveStatus);
            if (ImGui.Button("取消計算")) cancel?.Cancel();
        }
        else
        {
            if (ImGui.Button(plan == null ? (planWeek == 0 ? "計算本週排班" : "計算下週排班") : "重新計算"))
                StartSolve(s, observed, planWeek);

            if (solveStatus.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(solveStatus);
            }
        }

        if (plan is not { } p) return;

        if (p.WeekKey != targetKey)
        {
            ModuleUi.Note("這份是之前那週算的，請重新計算。");
            return;
        }

        ImGui.Spacing();
        DrawPlanTable(p, s);
        ImGui.Spacing();

        var scheduleOpen = Addons.IsOpen(Craftworks.ScheduleAddon);
        ImGui.BeginDisabled(!scheduleOpen || applier.Running || busy);
        if (ImGui.Button("套用到遊戲"))
            applier.Start(p.Days);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.TextDisabled(!scheduleOpen
            ? "要先在島上打開排班表"
            : planWeek == 0
                ? $"只改第 {s.Day + 2} 天以後的日子；那幾天原本排的會先清掉"
                : "下週第 1～7 天原本排的會先清掉");
    }

    private void DrawPlanTable(PlanResult p, Craftworks.Snapshot s)
    {
        const ImGuiTableFlags flags = ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable("##craftworks-plan", 3, flags)) return;

        ImGui.TableSetupColumn("日子");
        ImGui.TableSetupColumn("預估收益");
        ImGui.TableSetupColumn("排班（每間工坊）");
        ImGui.TableHeadersRow();

        var total = 0;
        for (var i = 0; i < p.Days.Length; i++)
        {
            var day = p.Days[i];
            var past = day.Cycle <= s.Day;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            Text($"{Craftworks.DayLabel(day.Cycle)}{(past ? "（已過）" : "")}", past);
            ImGui.TableNextColumn();
            Text(day.Rest ? "-" : p.Values[i].ToString("N0"), past);
            ImGui.TableNextColumn();

            if (day.Rest)
            {
                Text("休息", past);
                continue;
            }

            total += p.Values[i];

            // 同樣隊列的工坊合在一起：先一行「×3 間」，下面每個產品一行（開始時間、名稱、時長）
            foreach (var group in day.Workshops.GroupBy(q => string.Join(",", q)))
            {
                Text($"×{group.Count()} 間", past);

                var hour = 0;
                foreach (var craft in group.First())
                {
                    var time = Craftworks.Object(craft)?.Time ?? 0;
                    Text($"    {hour,2}:00  {Craftworks.ShortName(craft)}（{time}h）", past);
                    hour += time;
                }
            }
        }

        ImGui.EndTable();
        ImGui.TextUnformatted($"整週預估收益：{total:N0} 青船幣");
        ImGui.TextDisabled($"計算時間：{p.At:HH:mm}");
    }

    private static void Text(string text, bool dim)
    {
        if (dim) ImGui.TextDisabled(text);
        else ImGui.TextUnformatted(text);
    }

    // ── 求解 ─────────────────────────────────────────────────

    private void StartSolve(Craftworks.Snapshot s, SortedDictionary<int, byte[]> observed, int week)
    {
        // 遊戲資料在這裡（遊戲執行緒）先讀好，背景只跑求解器
        var input = BuildInput(s, observed, week);
        cancel = new CancellationTokenSource();
        var token = cancel.Token;
        solveStatus = "計算中…";

        solving = Task.Run(() =>
        {
            try
            {
                var started = DateTime.Now;
                var result = Solve(input, token);
                if (result == null) return;

                plans[input.Week] = result;
                solveStatus = $"算好了（{(DateTime.Now - started).TotalSeconds:0} 秒）";
                log.Write($"排班 {solveStatus}：" + string.Join(" ／ ", result.Days.Select((d, i) =>
                    d.Rest ? $"{Craftworks.DayLabel(d.Cycle)} 休息" : $"{Craftworks.DayLabel(d.Cycle)} {result.Values[i]}")));
            }
            catch (OperationCanceledException)
            {
                solveStatus = "已取消";
            }
            catch (Exception e)
            {
                solveStatus = $"計算失敗：{e.Message}";
                log.Write($"排班 錯誤 {e}");
            }
        }, token);
    }

    private sealed record SolveInput(
        int Week,
        string WeekKey,
        List<Recipe> Recipes,
        List<Popularity[]> Popularity,
        int PopularityRow,
        DemandPattern[] Patterns,
        CraftworkInfo Info,
        byte[] Banned,
        Favor[] Favors,
        int QueueKinds);

    private SolveInput BuildInput(Craftworks.Snapshot s, SortedDictionary<int, byte[]> observed, int week)
    {
        // 配方表：索引 = MJICraftworksObject 列號（第 0 列是空的）
        var recipes = Services.Data.GetExcelSheet<MJICraftworksObject>()
            .Select(r => new Recipe((byte)r.RowId, (byte)r.Theme[0].RowId, (byte)r.Theme[1].RowId, (byte)r.LevelReq,
                r.Item.RowId == 0 ? (byte)0 : (byte)r.CraftingTime, r.Value, 0)) // 成本 0：跟網站預設一樣不考慮材料成本
            .ToList();

        var popularity = Services.Data.GetExcelSheet<MJICraftworksPopularity>()
            .Select(r => Enumerable.Range(0, recipes.Count)
                .Select(i => i < r.Popularity.Count ? (Popularity)r.Popularity[i].RowId : Popularity.Unknown)
                .ToArray())
            .ToList();

        // 下週還看不出走勢：全部當作需求普通（Unknown = 9）
        var patterns = Enumerable.Range(0, recipes.Count)
            .Select(i => week == 0 ? PredictPattern((uint)i, observed) : DemandPattern.Unknown)
            .ToArray();

        var workshops = Math.Clamp(Workshops(s), 1, 4);
        var level = Math.Clamp(WorkshopLevel(s), 1, 5);
        var info = new CraftworkInfo(
            (byte)Math.Clamp(s.Groove, 0, 255),
            (byte)Math.Clamp(MaxGroove(s), 0, 255),
            (byte)(level - 1),
            (byte)workshops);

        // 貓耳小員：4、8 小時的要 8 個，6 小時的要 6 個（同 island.ffxiv.cyou），扣掉已經交的和排了的
        var favors = Config.IncludeFavors && workshops > 1
            ? (week == 0 ? s.Favors : s.NextFavors).Select(f =>
                {
                    var need = Craftworks.Object(f.CraftId)?.Time == 6 ? 6 : 8;
                    return new Favor((byte)f.CraftId, (byte)Math.Max(0, need - f.Done));
                })
                .Where(f => f.Num > 0)
                .Take(3)
                .ToArray()
            : Array.Empty<Favor>();

        return new SolveInput(week, week == 0 ? s.WeekKey : Craftworks.NextWeekKey(s.WeekKey),
            recipes, popularity, (int)(week == 0 ? s.Popularity : s.NextPopularity), patterns, info,
            Config.Banned.Select(b => (byte)b).ToArray(), favors, Math.Clamp(Config.QueueKinds, 1, 2));
    }

    /// <summary>背景執行緒：跑求解器，轉成每天每間工坊的隊列。</summary>
    private PlanResult? Solve(SolveInput input, CancellationToken token)
    {
        var api = new WorkshopSolverApi(input.Recipes, input.Popularity);
        api.SetPattern(input.PopularityRow);
        var workers = input.Info.Workers;

        Batches?[] days;
        if (input.Favors.Length > 0)
        {
            solveStatus = "計算中（含貓耳小員）…";
            days = api.SolveWeekFavor(input.Info, MaxRecipeLevel, input.Banned, HoursPerDay, false, input.Patterns, input.Favors);
        }
        else if (input.QueueKinds >= 2 && workers > 1)
        {
            api.SolverClearCache();
            days = new Batches?[6];
            var best = -1;
            for (var part = 0; part < WorkshopSolverApi.WeekPartCount; part++)
            {
                token.ThrowIfCancellationRequested();
                solveStatus = $"計算中 {part + 1}/{WorkshopSolverApi.WeekPartCount}…";

                var (result, value) = api.SolveWeekPart(input.Info, MaxRecipeLevel, input.Banned, HoursPerDay, false, input.Patterns, part);
                if (value <= best) continue;
                best = value;
                days = result;
            }
        }
        else
        {
            solveStatus = "計算中…";
            days = api.SolveWeekSingle(input.Info, MaxRecipeLevel, input.Banned, HoursPerDay, false, input.Patterns)
                .Select(b => b is { } batch ? new Batches { First = new WorkerBatch { Workers = workers, Batch = batch }, Value = (ushort)(batch.Value * workers) } : (Batches?)null)
                .ToArray();
        }

        token.ThrowIfCancellationRequested();

        // 第 1 天固定休息；求解器第 i 格 = 第 i+2 天。排班表上本週是 cycle 0～6、下週 7～13
        var offset = input.Week * Craftworks.DaysPerWeek;
        var dayPlans = new List<CraftworksApplier.DayPlan> { new(offset, true, Array.Empty<IReadOnlyList<uint>>()) };
        var values = new List<int> { 0 };
        for (var i = 0; i < days.Length; i++)
        {
            var cycle = offset + i + 1;
            if (days[i] is not { } day)
            {
                dayPlans.Add(new CraftworksApplier.DayPlan(cycle, true, Array.Empty<IReadOnlyList<uint>>()));
                values.Add(0);
                continue;
            }

            var queues = new List<IReadOnlyList<uint>>();
            AddQueues(queues, day.First);
            AddQueues(queues, day.Second);
            dayPlans.Add(new CraftworksApplier.DayPlan(cycle, false, queues.Take(workers).ToList()));
            values.Add(day.Value);
        }

        return new PlanResult(input.WeekKey, dayPlans.ToArray(), values.ToArray(), DateTime.Now);
    }

    private static void AddQueues(List<IReadOnlyList<uint>> queues, WorkerBatch group)
    {
        if (group.Workers == 0 || group.Batch.Seq == 0) return;

        var steps = group.Batch.StepsToArray().Take(group.Batch.Seq).Select(b => (uint)b).ToList();
        for (var w = 0; w < group.Workers; w++) queues.Add(steps);
    }
}
