// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
using System;
using System.Collections.Generic;

namespace AetherKit.Game.Workshop;

/// <summary>
/// 無人島工房排班求解器的對外入口（對應原版 apiv2.rs 的 APIv2，一個方法對一個方法）。
/// <para>
/// 共通約定：
/// <list type="bullet">
/// <item>配方 ID = MJICraftworksObject 列號。所有以「配方 ID」為索引的陣列（recipes、demands、pattern、歡迎度列）都必須至少涵蓋最大的配方 ID。</item>
/// <item>需求值（demands）是隱藏的「真實需求值」（例如 9 = 普通），不是 0~4 的需求等級；可由 <see cref="GetDemands"/> 從需求模式換算，
///   再自行扣掉本週先前已產出的數量（第一步 1 個、連擊步驟 2 個，乘上工房數）。</item>
/// <item>週期日以 0 起算：day 0 = 週期第一天（重置當天）。整週求解只排 day 1~6，回傳陣列第 i 格 = day i+1（即第 i+2 天，1 起算）；null 表示休息。</item>
/// <item>「workers」是同時執行同一佇列的工房數量（1~4）：每多一次連擊，每間工房各加 1 幹勁；產量也乘上此數。</item>
/// <item>所有 Batch 的 Value / Values / Cost 都是「單一工房」的值；Batches / BatchWithBatch 的 Value / Cost 為加總值（見各型別說明）。</item>
/// </list>
/// </para>
/// 本類別不是執行緒安全的；求解可能耗時數秒，請在背景執行緒呼叫，且同一實例不要並行使用。
/// </summary>
public sealed class WorkshopSolverApi
{
    /// <summary><see cref="SolveWeekPart"/> 的分塊數量（partId 取 0..29）。</summary>
    public const int WeekPartCount = 30;

    private readonly GameDataRepo repo;
    private readonly BFSolver daySolverSingle;
    private readonly AdvancedSimplifySolver<BFSolver> daySolverDual;
    private readonly MildSolver weekSolverSingle;
    private readonly MildMultiSolver<AdvancedSimplifySolver<BFSolver>> weekSolverDual;
    private readonly FavorSolver<MildSolver, BFSolver> weekSolverFavor;

    /// <summary>
    /// 初始化（對應 init_api_v2）。
    /// </summary>
    /// <param name="recipes">
    /// 配方表，recipes[i] 必須是 MJICraftworksObject 第 i 列（Id == i）；第 0 列為空白佔位（CraftTime = 0）。
    /// CraftTime == 0 的列一律不會被選用。欄位：Theme1/Theme2 = 主題（MJICraftworksObjectTheme 列號），
    /// Level = 需要的開拓等級，CraftTime = 製作小時，Value = 基礎價格，Cost = 原料成本（不考慮成本時填 0）。
    /// </param>
    /// <param name="popularityPatterns">
    /// 歡迎度模式表：popularityPatterns[p][id] = 歡迎度模式 p（MJICraftworksPopularity 列號）下配方 id 的歡迎度。
    /// 初始選用模式 0，用 <see cref="SetPattern"/> 切換。
    /// </param>
    public WorkshopSolverApi(IReadOnlyList<Recipe> recipes, IReadOnlyList<Popularity[]> popularityPatterns)
    {
        repo = new GameDataRepo(recipes, popularityPatterns);
        daySolverSingle = new BFSolver();
        daySolverDual = new AdvancedSimplifySolver<BFSolver>(new BFSolver());
        weekSolverSingle = new MildSolver();
        weekSolverDual = new MildMultiSolver<AdvancedSimplifySolver<BFSolver>>(new AdvancedSimplifySolver<BFSolver>(new BFSolver()));
        weekSolverFavor = new FavorSolver<MildSolver, BFSolver>(new MildSolver(), new BFSolver());
    }

    /// <summary>
    /// 以原版 wasm 的扁平格式初始化（對應 init_repo / init_api_v2 的參數）。
    /// </summary>
    /// <param name="recipes">每 7 個一組：id, theme1, theme2, level, time, value, cost；第 n 組必須是 id n。</param>
    /// <param name="popPattern">歡迎度模式的二維表攤平成一維（每列 popPatternRow 個，值為 <see cref="Popularity"/> 數值）。</param>
    /// <param name="popPatternRow">每列長度。</param>
    /// <returns>新的求解器。</returns>
    public static WorkshopSolverApi FromRaw(ReadOnlySpan<ushort> recipes, ReadOnlySpan<byte> popPattern, int popPatternRow)
    {
        var recp = new List<Recipe>();
        for (var i = 0; i + 7 <= recipes.Length; i += 7)
            recp.Add(Recipe.FromArray(recipes.Slice(i, 7)));

        var pops = new List<Popularity[]>();
        for (var i = 0; i < popPattern.Length; i += popPatternRow)
        {
            var len = Math.Min(popPatternRow, popPattern.Length - i);
            var row = new Popularity[len];
            for (var c = 0; c < len; c++)
                row[c] = PopularityUtil.FromByte(popPattern[i + c]);
            pops.Add(row);
        }
        return new WorkshopSolverApi(recp, pops);
    }

    /// <summary>目前資料庫（唯讀用途）。</summary>
    public GameDataRepo Repo => repo;

    /// <summary>
    /// 設定本週的歡迎度模式（對應 set_pattern）。
    /// </summary>
    /// <param name="pattern">由封包解析出的歡迎度模式 ID，即 popularityPatterns 的索引（MJICraftworksPopularity 列號）。影響之後所有模擬與求解。</param>
    public void SetPattern(int pattern) => repo.SetPopularPattern(pattern);

    /// <summary>
    /// 模擬一天的排班表，多個佇列同時進行（對應 simulate）。
    /// </summary>
    /// <param name="state">工房狀態：Tension = 當天開始時的幹勁，MaxTension、Level 生效；Workers 不影響多佇列模擬（幹勁依各佇列的工房數計算）。</param>
    /// <param name="seq">各佇列：（工房數量, 最多 6 步的配方 ID，0 = 空）。</param>
    /// <param name="demands">當天各配方的真實需求值，索引 = 配方 ID。</param>
    /// <returns>與 seq 一一對應：每個佇列「單一工房」的模擬結果（Batch.Cost = 成本、Batch.Values[k] = 第 k 步單價、Batch.Value = 合計）。</returns>
    public Batch[] Simulate(CraftworkInfo state, IReadOnlyList<PresetQueue> seq, sbyte[] demands)
    {
        var recp = new WorkerQueue[seq.Count];
        for (var i = 0; i < seq.Count; i++)
        {
            var arr = new RecipeQueue();
            for (var j = 0; j < 6; j++)
            {
                int id = seq[i].Steps[j];
                arr[j] = id == 0 ? null : repo.State(id, demands[id]);
            }
            recp[i] = new WorkerQueue(seq[i].Workers, arr);
        }

        var (batches, _) = Simulator.SimulateMultiBatch(state, recp);
        var ret = new Batch[batches.Length];
        for (var i = 0; i < batches.Length; i++)
            ret[i] = batches[i].Batch;
        return ret;
    }

    /// <summary>
    /// 在已設定部分工房的情況下，搜尋剩下工房（同一佇列）的最佳解（對應 solve_day_with_batch）。
    /// </summary>
    /// <param name="state">工房狀態：Tension = 當天開始時的幹勁；MaxTension、Level 生效。</param>
    /// <param name="level">開拓等級；只使用 Recipe.Level ≤ level 的配方。</param>
    /// <param name="banList">禁用的配方 ID（可為 null）。</param>
    /// <param name="set">已設定的佇列（工房數量, 6 步配方 ID）。</param>
    /// <param name="demands">當天真實需求值，索引 = 配方 ID。</param>
    /// <param name="worker">要搜尋的這個新佇列由幾間工房執行。</param>
    /// <param name="time">每天可用工時（一般 24）。</param>
    /// <param name="withCost">排序時是否扣除成本。</param>
    /// <returns>
    /// 最多 100 個解，由好到壞排序。每個元素：Batch = 新佇列（單一工房）的工序與單價；
    /// Value / Cost = 已設定佇列在一起模擬後的總價 / 總成本（已乘工房數）；
    /// CmpValue = 排序值 = 新佇列值 × worker + 已設定值（withCost 時都先扣成本，u16 環繞）。
    /// </returns>
    public BatchWithBatch[] SolveDayWithBatch(CraftworkInfo state, byte level, byte[]? banList, IReadOnlyList<PresetQueue> set, sbyte[] demands, byte worker, byte time, bool withCost)
    {
        var ctx = new SolverCtx(repo, state, new SolveLimit(level, banList, time, withCost));
        return daySolverSingle.Solve(ctx, ToArray(set), demands, worker);
    }

    /// <summary>
    /// 搜尋單日至多兩種佇列的最佳組合（對應 solve_day_dual）。
    /// </summary>
    /// <param name="state">工房狀態：Tension = 當天開始時的幹勁；MaxTension、Level 生效。</param>
    /// <param name="level">開拓等級；只使用 Recipe.Level ≤ level 的配方。</param>
    /// <param name="banList">禁用的配方 ID（可為 null）。</param>
    /// <param name="demands">當天真實需求值，索引 = 配方 ID。</param>
    /// <param name="worker">工房總數。</param>
    /// <param name="time">每天可用工時（一般 24）。</param>
    /// <param name="withCost">排序時是否扣除成本。</param>
    /// <returns>
    /// 最多 100 個解，由好到壞排序。每個元素：First / Second = （工房數, 單一工房工序）；Second.Workers == 0 表示只有一種佇列
    /// （原版輸出時會略過 Workers 為 0 的項目）。Value / Cost = 全部工房總價 / 總成本；CmpValue = 排序值。
    /// </returns>
    public Batches[] SolveDayDual(CraftworkInfo state, byte level, byte[]? banList, sbyte[] demands, byte worker, byte time, bool withCost)
    {
        var ctx = new SolverCtx(repo, state, new SolveLimit(level, banList, time, withCost));
        return daySolverDual.Solve(ctx, demands, worker);
    }

    /// <summary>
    /// 在已設定部分工房的情況下，搜尋貓耳小員委託優先的最佳解（對應 solve_day_with_favor）。
    /// </summary>
    /// <param name="state">工房狀態：Tension = 當天開始時的幹勁；MaxTension、Level 生效。</param>
    /// <param name="level">開拓等級。</param>
    /// <param name="banList">禁用的配方 ID（可為 null）。</param>
    /// <param name="set">已設定的佇列（工房數量, 6 步配方 ID）。</param>
    /// <param name="demands">當天真實需求值，索引 = 配方 ID。</param>
    /// <param name="favors">委託（配方 ID, 尚需數量）；只看前 3 項。</param>
    /// <param name="worker">要搜尋的這個新佇列由幾間工房執行。</param>
    /// <param name="time">每天可用工時。</param>
    /// <param name="withCost">排序時是否扣除成本。</param>
    /// <returns>
    /// 同 <see cref="SolveDayWithBatch"/>，但 CmpValue = (委託滿足分數 &lt;&lt; 10) + 收益值（u16 環繞），
    /// 委託滿足分數 = 4 × Σmin(產量, 需求) + 剛好填滿的項數；產量只計新佇列（× worker）。
    /// </returns>
    public BatchWithBatch[] SolveDayWithFavor(CraftworkInfo state, byte level, byte[]? banList, IReadOnlyList<PresetQueue> set, sbyte[] demands, Favor[] favors, byte worker, byte time, bool withCost)
    {
        var ctx = new SolverCtx(repo, state, new SolveLimit(level, banList, time, withCost));
        return daySolverSingle.SolveFavor(ctx, ToArray(set), demands, worker, favors);
    }

    /// <summary>
    /// 每天所有工房使用同一佇列時，求解整週（對應 solve_week_single，Mild 演算法）。
    /// </summary>
    /// <param name="state">工房狀態：Workers = 工房數量，MaxTension、Level 生效；Tension 被忽略（從 0 開始累計整週幹勁）。</param>
    /// <param name="level">開拓等級。</param>
    /// <param name="banList">禁用的配方 ID（可為 null）。</param>
    /// <param name="time">每天可用工時。</param>
    /// <param name="withCost">比較時是否扣除成本。</param>
    /// <param name="pattern">本週各配方的需求變動模式，索引 = 配方 ID。</param>
    /// <returns>
    /// 長度 6：第 i 格 = 週期 day i+1（0 起算，即第 i+2 天）的佇列（單一工房值，總收益需 × Workers）；null = 休息（恰有一格）。
    /// </returns>
    public Batch?[] SolveWeekSingle(CraftworkInfo state, byte level, byte[]? banList, byte time, bool withCost, DemandPattern[] pattern)
    {
        var ctx = new SolverCtx(repo, state, new SolveLimit(level, banList, time, withCost));
        return weekSolverSingle.Solve(ctx, pattern);
    }

    /// <summary>
    /// 清除 <see cref="SolveWeekPart"/> 使用的快取（對應 solver_clear_cache）。
    /// 快取鍵只含（幹勁, 當日需求），開始新一輪 30 個分塊前、或配方 / 歡迎度 / 等級 / 禁用清單 / 工房狀態改變後都必須呼叫。
    /// </summary>
    public void SolverClearCache() => weekSolverDual.ClearCache();

    /// <summary>
    /// 每天至多兩種佇列時，求解整週的其中一個分塊（對應 solve_week_part）。
    /// 完整求解需對 partId = 0..<see cref="WeekPartCount"/>-1 各呼叫一次，取 Value 最大者。
    /// </summary>
    /// <param name="state">工房狀態：Workers = 工房總數，MaxTension、Level 生效；Tension 被忽略。</param>
    /// <param name="level">開拓等級。</param>
    /// <param name="banList">禁用的配方 ID（可為 null）。</param>
    /// <param name="time">每天可用工時。</param>
    /// <param name="withCost">比較時是否扣除成本。</param>
    /// <param name="pattern">本週各配方的需求變動模式，索引 = 配方 ID。</param>
    /// <param name="partId">分塊編號 0..29：先排的兩天 = (partId / 5) 與 (partId % 5，若 ≥ 前者則 +1)。</param>
    /// <returns>
    /// Days：長度 6，第 i 格 = 週期 day i+1 的組合（First/Second 為（工房數, 單一工房工序），Value/Cost 為全部工房總值）；null = 休息。
    /// Value：此分塊的比較值（整週總收益，withCost 時扣成本）；沒找到任何解時為 0 且 Days 全為 null。
    /// </returns>
    public (Batches?[] Days, ushort Value) SolveWeekPart(CraftworkInfo state, byte level, byte[]? banList, byte time, bool withCost, DemandPattern[] pattern, int partId)
    {
        var ctx = new SolverCtx(repo, state, new SolveLimit(level, banList, time, withCost));
        return weekSolverDual.SolvePart(ctx, pattern, partId);
    }

    /// <summary>
    /// 求解整週並以 1 間工房安排貓耳小員委託（對應 solve_week_favor）。
    /// 先以「Workers - 1」間工房用 Mild 解出整週，再逐日為最後 1 間工房挑選滿足委託的佇列。
    /// </summary>
    /// <param name="state">工房狀態：Workers = 工房總數（需 ≥ 2），MaxTension、Level 生效。</param>
    /// <param name="level">開拓等級。</param>
    /// <param name="banList">禁用的配方 ID（可為 null）。</param>
    /// <param name="time">每天可用工時。</param>
    /// <param name="withCost">比較時是否扣除成本。</param>
    /// <param name="pattern">本週各配方的需求變動模式，索引 = 配方 ID。</param>
    /// <param name="favors">委託（配方 ID, 需求數量），至多 3 項（超過時丟 ArgumentException，原版會 panic）。</param>
    /// <returns>
    /// 長度 6，第 i 格 = 週期 day i+1：First = （Workers-1, 主佇列），Second = （1, 委託佇列）；null = 休息。
    /// </returns>
    public Batches?[] SolveWeekFavor(CraftworkInfo state, byte level, byte[]? banList, byte time, bool withCost, DemandPattern[] pattern, Favor[] favors)
    {
        var ctx = new SolverCtx(repo, state, new SolveLimit(level, banList, time, withCost));
        return weekSolverFavor.Solve(ctx, pattern, favors);
    }

    // ---- 需求預測（lib.rs 的 pattern_* 與 predition 模組） ----

    /// <summary>依模式取得某天的真實需求值。</summary>
    /// <param name="pattern">需求變動模式。</param>
    /// <param name="day">週期日，0 起算（0 = 週期第一天）；≥ 7 時回傳 9。</param>
    /// <returns>真實需求值（未知模式為 9）。</returns>
    public static sbyte GetDemand(DemandPattern pattern, byte day) => Prediction.GetDemand(pattern, day);

    /// <summary>依各配方的模式取得某天的真實需求值陣列（對應 pattern_demand）。</summary>
    /// <param name="patterns">各配方的需求變動模式，索引 = 配方 ID。</param>
    /// <param name="day">週期日，0 起算。</param>
    /// <returns>與 patterns 等長的真實需求值陣列，可直接作為單日求解的 demands。</returns>
    public static sbyte[] GetDemands(IReadOnlyList<DemandPattern> patterns, byte day) => Prediction.GetDemands(patterns, day);

    /// <summary>以已知的每日（需求, 變化）猜測單一物品的模式（舊版 predict）。</summary>
    /// <param name="seq">從週期第一天起的（需求等級, 需求變化），最多 7 筆。</param>
    /// <returns>唯一可能的模式；無法唯一確定時為 Unknown。</returns>
    public static DemandPattern Predict(ReadOnlySpan<(Demand Demand, DemandChange Change)> seq) => Prediction.Predict(seq);

    /// <summary>（進階）預測單一物品所有可能模式（對應 pattern_predict_adv 的單一物品）。</summary>
    /// <param name="seq">從週期第一天起的（需求等級, 需求變化），最多 7 筆。可用 <see cref="DecodeDemandByte"/> 解析封包位元組。</param>
    /// <param name="lastDemand">上週最後一天的真實需求值；不知道就傳 null（原版以 -128 表示）。</param>
    /// <returns>位元圖：第 j 位（0 起算）為 1 代表 DemandPattern 值 j+1 仍可能；只剩一位或 0 時即提早回傳。</returns>
    public static ushort PredictAdv(ReadOnlySpan<(Demand Demand, DemandChange Change)> seq, sbyte? lastDemand) => Prediction.PredictAdv(seq, lastDemand);

    /// <summary>批次預測（對應 pattern_predict）。</summary>
    /// <param name="seqs">多個物品的（需求, 變化），單一物品的各天連續排列，每個物品 days 筆。</param>
    /// <param name="days">每個物品的天數。</param>
    /// <returns>每個物品的模式。</returns>
    public static DemandPattern[] PredictAll(ReadOnlySpan<(Demand Demand, DemandChange Change)> seqs, int days) => Prediction.PredictAll(seqs, days);

    /// <summary>解析封包格式的需求位元組：高 4 位 = 需求等級，低 4 位 = 需求變化。</summary>
    /// <param name="b">封包位元組。</param>
    /// <returns>（需求等級, 需求變化）；任一半超出 0..4 時丟例外。</returns>
    public static (Demand Demand, DemandChange Change) DecodeDemandByte(byte b) => DemandUtil.Decode(b);

    private static PresetQueue[] ToArray(IReadOnlyList<PresetQueue> set)
    {
        var arr = new PresetQueue[set.Count];
        for (var i = 0; i < set.Count; i++)
            arr[i] = set[i];
        return arr;
    }
}
