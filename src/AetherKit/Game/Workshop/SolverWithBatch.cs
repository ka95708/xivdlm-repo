// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
using System;
using System.Collections.Generic;

namespace AetherKit.Game.Workshop;

/// <summary>已設定好的佇列：（工房數量, 6 步配方 ID，0 表示空）。</summary>
public struct PresetQueue
{
    /// <summary>跑這個佇列的工房數量。</summary>
    public byte Workers;
    /// <summary>6 步配方 ID（MJICraftworksObject 列號），不足補 0。</summary>
    public StepArray Steps;

    /// <summary>建立。steps 最多取前 6 個，不足補 0。</summary>
    public PresetQueue(byte workers, ReadOnlySpan<byte> steps)
    {
        Workers = workers;
        Steps = StepArray.From(steps);
    }

    /// <summary>建立。</summary>
    public PresetQueue(byte workers, StepArray steps)
    {
        Workers = workers;
        Steps = steps;
    }
}

/// <summary>帶已設定排班的步驟解（solver_with_batch.rs 的 BatchWithBatch）。</summary>
public struct BatchWithBatch
{
    /// <summary>本次搜尋的佇列（單一工房的工序與價格）。</summary>
    public Batch Batch;
    /// <summary>已設定佇列的總價（已乘上各自工房數量）。</summary>
    public ushort Value;
    /// <summary>已設定佇列的總成本（已乘上各自工房數量）。</summary>
    public ushort Cost;
    /// <summary>排序用的總值。</summary>
    public ushort CmpValue;

    /// <summary>由工序建立。</summary>
    public static BatchWithBatch FromBatch(in Batch batch) => new() { Batch = batch };
}

/// <summary>BatchWithBatch 的 Rust Ord（反向）。</summary>
internal struct BatchWithBatchOrd : IComparer<BatchWithBatch>
{
    public readonly int Compare(BatchWithBatch x, BatchWithBatch y)
    {
        var a = (uint)x.CmpValue * 10 + x.Batch.Seq;
        var b = (uint)y.CmpValue * 10 + y.Batch.Seq;
        return b.CompareTo(a);
    }
}

/// <summary>BatchWithBatch 回呼。</summary>
public delegate void BatchWithBatchCallback(in BatchWithBatch item);

/// <summary>佇列過濾器。</summary>
public delegate bool QueueFilter(in RecipeQueue queue);

/// <summary>帶已設定排班的當日求解器。</summary>
public interface ISolverWithBatch
{
    /// <summary>列舉解。workers：本次搜尋佇列的工房數量。</summary>
    void SolveFn(SolverCtx ctx, PresetQueue[] set, sbyte[] demands, byte workers, QueueFilter filter, BatchWithBatchCallback cb);
}

/// <summary>ISolverWithBatch 的預設方法。</summary>
public static class SolverWithBatchExtensions
{
    private static readonly QueueFilter AcceptAll = (in RecipeQueue _) => true;

    private static ushort TotalVal(in BatchWithBatch item, bool withCost, byte workers)
        => withCost
            ? (ushort)((ushort)((ushort)(item.Batch.Value - item.Batch.Cost) * workers) + (ushort)(item.Value - item.Cost))
            : (ushort)((ushort)(item.Batch.Value * workers) + item.Value);

    /// <summary>所有解（未排序）。</summary>
    public static BatchWithBatch[] SolveUnordered(this ISolverWithBatch self, SolverCtx ctx, PresetQueue[] set, sbyte[] demands, byte workers)
    {
        var result = new List<BatchWithBatch>();
        self.SolveFn(ctx, set, demands, workers, AcceptAll, (in BatchWithBatch b) => result.Add(b));
        return result.ToArray();
    }

    /// <summary>前 MaxResult 名，由高到低排序。</summary>
    public static BatchWithBatch[] Solve(this ISolverWithBatch self, SolverCtx ctx, PresetQueue[] set, sbyte[] demands, byte workers)
    {
        var heap = new RustBinaryHeap<BatchWithBatch, BatchWithBatchOrd>(ctx.Limit.MaxResult + 1);
        var withCost = ctx.Limit.WithCost;
        var max = ctx.Limit.MaxResult;
        self.SolveFn(ctx, set, demands, workers, AcceptAll, (in BatchWithBatch b) =>
        {
            var item = b;
            item.CmpValue = TotalVal(item, withCost, workers);
            heap.Push(item);
            if (heap.Count > max)
                heap.Pop(out _);
        });
        return heap.IntoSortedVec();
    }

    /// <summary>唯一最佳解。</summary>
    public static BatchWithBatch SolveBest(this ISolverWithBatch self, SolverCtx ctx, PresetQueue[] set, sbyte[] demands, byte workers)
    {
        ushort maxVal = 0;
        var maxBatch = BatchWithBatch.FromBatch(Batch.New());
        var withCost = ctx.Limit.WithCost;
        self.SolveFn(ctx, set, demands, workers, AcceptAll, (in BatchWithBatch item) =>
        {
            var val = TotalVal(item, withCost, workers);
            if (maxVal < val)
            {
                maxVal = val;
                maxBatch = item;
            }
        });
        return maxBatch;
    }

    /// <summary>使用自訂排序值求唯一最佳解。</summary>
    public static BatchWithBatch SolveBestFn(this ISolverWithBatch self, SolverCtx ctx, PresetQueue[] set, sbyte[] demands, byte workers, SortValue<BatchWithBatch> sortVal)
    {
        ushort maxVal = 0;
        var maxBatch = BatchWithBatch.FromBatch(Batch.New());
        var withCost = ctx.Limit.WithCost;
        self.SolveFn(ctx, set, demands, workers, AcceptAll, (in BatchWithBatch item) =>
        {
            var val = TotalVal(item, withCost, workers);
            val = sortVal(val, item);
            if (maxVal < val)
            {
                maxVal = val;
                maxBatch = item;
            }
        });
        return maxBatch;
    }

    private static ushort FavorCmp(in BatchWithBatch item, Favor[] favors, ushort val, byte workers)
    {
        var counter = new Favors(favors);
        for (var i = 0; i < item.Batch.Seq; i++)
            counter.Add(item.Batch.Steps[i], (byte)(i == 0 ? 1 * workers : 2 * workers));
        return (ushort)((ushort)(counter.Value() << 10) + val);
    }

    /// <summary>解貓耳小員：前 MaxResult 名，由高到低排序。</summary>
    public static BatchWithBatch[] SolveFavor(this ISolverWithBatch self, SolverCtx ctx, PresetQueue[] set, sbyte[] demands, byte workers, Favor[] favors)
    {
        var heap = new RustBinaryHeap<BatchWithBatch, BatchWithBatchOrd>(ctx.Limit.MaxResult + 1);
        var withCost = ctx.Limit.WithCost;
        var max = ctx.Limit.MaxResult;
        self.SolveFn(ctx, set, demands, workers, AcceptAll, (in BatchWithBatch b) =>
        {
            var item = b;
            var val = TotalVal(item, withCost, workers);
            item.CmpValue = FavorCmp(item, favors, val, workers);
            heap.Push(item);
            if (heap.Count > max)
                heap.Pop(out _);
        });
        return heap.IntoSortedVec();
    }

    /// <summary>解貓耳小員：唯一最佳解。</summary>
    public static BatchWithBatch SolveFavorBest(this ISolverWithBatch self, SolverCtx ctx, PresetQueue[] set, sbyte[] demands, byte workers, Favor[] favors)
    {
        byte sum = 0;
        foreach (var x in favors)
            sum = (byte)(sum + (ctx.Repo.Recipe(x.Id).CraftTime > 0 ? x.Num : 0));
        if (sum == 0)
            return self.SolveBest(ctx, set, demands, workers);

        ushort maxVal = 0;
        var maxBatch = BatchWithBatch.FromBatch(Batch.New());
        var withCost = ctx.Limit.WithCost;
        self.SolveFn(ctx, set, demands, workers, (in RecipeQueue recipe) =>
        {
            for (var s = 0; s < 6; s++)
            {
                var step = recipe[s];
                if (!step.HasValue)
                    return false;
                foreach (var f in favors)
                {
                    if (f.Num > 0 && f.Id == step.Value.Id)
                        return true;
                }
            }
            return false;
        }, (in BatchWithBatch item) =>
        {
            var val = TotalVal(item, withCost, workers);
            var cmpValue = FavorCmp(item, favors, val, workers);
            if (cmpValue > maxVal)
            {
                maxBatch = item;
                maxVal = cmpValue;
            }
        });
        return maxBatch;
    }
}
