// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
using System;

namespace AetherKit.Game.Workshop;

/// <summary>求解器限制（solver/mod.rs）。</summary>
public struct SolveLimit
{
    /// <summary>配方最高等級（無人島開拓等級）。</summary>
    public byte Level;
    /// <summary>禁用的配方 ID。</summary>
    public byte[] BanList;
    /// <summary>結果數量上限（預設 100）。</summary>
    public int MaxResult;
    /// <summary>是否考慮成本。</summary>
    public bool WithCost;
    /// <summary>工序耗時上限（小時）。</summary>
    public byte Time;

    /// <summary>建立限制。</summary>
    public SolveLimit(byte level, byte[]? banList, byte time, bool withCost)
    {
        Level = level;
        BanList = banList ?? Array.Empty<byte>();
        MaxResult = 100;
        WithCost = withCost;
        Time = time;
    }

    /// <summary>配方是否可用。</summary>
    public readonly bool Check(in Recipe recipe)
        => recipe.Level <= Level && Array.IndexOf(BanList, recipe.Id) < 0;
}

/// <summary>求解器上下文。</summary>
public sealed class SolverCtx
{
    /// <summary>資料庫。</summary>
    public readonly GameDataRepo Repo;
    /// <summary>工房狀態（FavorSolver 會修改其中的 Workers）。</summary>
    public CraftworkInfo Info;
    /// <summary>限制。</summary>
    public SolveLimit Limit;

    /// <summary>建立上下文。</summary>
    public SolverCtx(GameDataRepo repo, CraftworkInfo info, SolveLimit limit)
    {
        Repo = repo;
        Info = info;
        Limit = limit;
    }
}

/// <summary>工序回呼。</summary>
public delegate void BatchCallback(in Batch batch);

/// <summary>自訂排序值：輸入（基本值, 項目），回傳排序值。</summary>
public delegate ushort SortValue<T>(ushort value, in T item);

/// <summary>單一工房每日排班求解器（solver_single.rs）。</summary>
public interface ISolverSingle
{
    /// <summary>列舉所有解。workers 為 0 時沿用 ctx.Info.Workers。</summary>
    void SolveFn(SolverCtx ctx, sbyte[] demands, byte workers, BatchCallback cb);
}

/// <summary>ISolverSingle 的預設方法。</summary>
public static class SolverSingleExtensions
{
    /// <summary>所有解（未排序）。</summary>
    public static Batch[] SolveUnordered(this ISolverSingle self, SolverCtx ctx, sbyte[] demands, byte workers)
    {
        var result = new System.Collections.Generic.List<Batch>();
        self.SolveFn(ctx, demands, workers, (in Batch b) => result.Add(b));
        return result.ToArray();
    }

    /// <summary>前 MaxResult 名，由高到低排序。</summary>
    public static Batch[] Solve(this ISolverSingle self, SolverCtx ctx, sbyte[] demands, byte workers)
    {
        var heap = new RustBinaryHeap<Batch, BatchOrd>(ctx.Limit.MaxResult + 1);
        var withCost = ctx.Limit.WithCost;
        var max = ctx.Limit.MaxResult;
        self.SolveFn(ctx, demands, workers, (in Batch b) =>
        {
            var item = b;
            item.SetCmpValue(withCost);
            heap.Push(item);
            if (heap.Count > max)
                heap.Pop(out _);
        });
        return heap.IntoSortedVec();
    }

    /// <summary>唯一最佳解（同分取最先找到的）。</summary>
    public static Batch SolveBest(this ISolverSingle self, SolverCtx ctx, sbyte[] demands, byte workers)
    {
        ushort maxVal = 0;
        var maxBatch = Batch.New();
        var withCost = ctx.Limit.WithCost;
        self.SolveFn(ctx, demands, workers, (in Batch item) =>
        {
            var val = withCost ? (ushort)(item.Value - item.Cost) : item.Value;
            if (maxVal < val)
            {
                maxVal = val;
                maxBatch = item;
            }
        });
        return maxBatch;
    }

    /// <summary>使用自訂排序值求唯一最佳解。</summary>
    public static Batch SolveBestFn(this ISolverSingle self, SolverCtx ctx, sbyte[] demands, byte workers, SortValue<Batch> sortVal)
    {
        ushort maxVal = 0;
        var maxBatch = Batch.New();
        var withCost = ctx.Limit.WithCost;
        self.SolveFn(ctx, demands, workers, (in Batch item) =>
        {
            var val = withCost ? (ushort)(item.Value - item.Cost) : item.Value;
            val = sortVal(val, item);
            if (maxVal < val)
            {
                maxVal = val;
                maxBatch = item;
            }
        });
        return maxBatch;
    }
}

/// <summary>共用的連擊判斷。</summary>
internal static class SolverUtil
{
    /// <summary>兩個配方能否連擊（有共同主題，或上一步沒有主題）。</summary>
    public static bool BatchCheck(bool first, in Recipe last, in Recipe r)
        => first
           || (last.Theme1 != 0 && (last.Theme1 == r.Theme1 || last.Theme1 == r.Theme2))
           || (last.Theme2 != 0 && (last.Theme2 == r.Theme1 || last.Theme2 == r.Theme2))
           || (last.Theme1 == 0 && last.Theme2 == 0);
}
