// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
using System;
using System.Collections.Generic;

namespace AetherKit.Game.Workshop;

/// <summary>
/// 一天內至多兩種佇列的工房組合（solver_multi.rs 的 Batches）。
/// Value / Cost 是所有工房加總（各佇列單價 × 工房數量）。
/// </summary>
public struct Batches
{
    /// <summary>第一種佇列（工房數量, 單一工房工序）。</summary>
    public WorkerBatch First;
    /// <summary>第二種佇列；Workers 為 0 表示沒有第二種。</summary>
    public WorkerBatch Second;
    /// <summary>總收益（全部工房）。</summary>
    public ushort Value;
    /// <summary>總成本（全部工房）。</summary>
    public ushort Cost;
    /// <summary>排序用的值。</summary>
    public ushort CmpValue;

    /// <summary>空組合。</summary>
    public static Batches New() => default;

    /// <summary>以索引 0/1 取第一/第二種佇列。</summary>
    public readonly WorkerBatch Get(int index) => index switch
    {
        0 => First,
        1 => Second,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    /// <summary>由單一佇列建立。</summary>
    public static Batches FromSingleBatch(in Batch batch, byte worker) => new()
    {
        First = new WorkerBatch(worker, batch),
        Second = new WorkerBatch(0, Batch.New()),
        Value = (ushort)(batch.Value * worker),
        Cost = (ushort)(batch.Cost * worker),
        CmpValue = 0,
    };

    /// <summary>由模擬結果建立（加總全部項目，取前兩項）。</summary>
    public static Batches FromBatch(ReadOnlySpan<WorkerBatch> batches)
    {
        ushort value = 0;
        ushort cost = 0;
        foreach (ref readonly var wb in batches)
        {
            value = (ushort)(value + (ushort)(wb.Workers * wb.Batch.Value));
            cost = (ushort)(cost + (ushort)(wb.Workers * wb.Batch.Cost));
        }
        return new Batches
        {
            First = batches[0],
            Second = batches[1],
            Value = value,
            Cost = cost,
            CmpValue = 0,
        };
    }

    /// <summary>以模擬結果更新兩種佇列並重算總值。</summary>
    public void SetResult(ReadOnlySpan<WorkerBatch> batches)
    {
        First = batches[0];
        Second = batches[1];
        UpdateValues();
    }

    private void UpdateValues()
    {
        var value = (ushort)((ushort)(First.Workers * First.Batch.Value) + (ushort)(Second.Workers * Second.Batch.Value));
        var cost = (ushort)((ushort)(First.Workers * First.Batch.Cost) + (ushort)(Second.Workers * Second.Batch.Cost));
        Value = value;
        Cost = cost;
    }

    /// <summary>設定第二種佇列並重算總值。</summary>
    public void SetSecondResult(in Batch batch, byte worker)
    {
        Second = new WorkerBatch(worker, batch);
        UpdateValues();
    }

    /// <summary>幹勁增加量。</summary>
    public readonly byte TensionAdd()
    {
        byte tension = 0;
        if (First.Workers != 0)
            tension = (byte)(tension + First.Workers * (byte)(First.Batch.Seq - 1));
        if (Second.Workers != 0)
            tension = (byte)(tension + Second.Workers * (byte)(Second.Batch.Seq - 1));
        return tension;
    }

    /// <summary>把產量加到需求變動表。</summary>
    public readonly void ProduceAdd(sbyte[] demands)
    {
        First.Batch.DemandSub(demands, (sbyte)First.Workers);
        Second.Batch.DemandSub(demands, (sbyte)Second.Workers);
    }

    /// <summary>從需求變動表扣回產量。</summary>
    public readonly void ProduceSub(sbyte[] demands)
    {
        First.Batch.DemandSub(demands, (sbyte)((sbyte)First.Workers * -1));
        Second.Batch.DemandSub(demands, (sbyte)((sbyte)Second.Workers * -1));
    }

    /// <summary>排序鍵：cmp_value*10 + 兩佇列工房數差（i8 環繞語意）。</summary>
    internal readonly uint CmpKey()
    {
        var d = (sbyte)((sbyte)First.Workers - (sbyte)Second.Workers);
        if (d < 0)
            d = (sbyte)(-d);
        return (uint)CmpValue * 10 + (uint)(int)d;
    }

    /// <inheritdoc/>
    public override readonly string ToString()
        => $"[{(CmpValue == 0 ? Value : CmpValue)}] {First.Workers}x [{string.Join(",", First.Batch.Steps.ToArray())}], {Second.Workers}x [{string.Join(",", Second.Batch.Steps.ToArray())}]";
}

/// <summary>Batches 的 Rust Ord（反向）。</summary>
internal struct BatchesOrd : IComparer<Batches>
{
    public readonly int Compare(Batches x, Batches y) => y.CmpKey().CompareTo(x.CmpKey());
}

/// <summary>Batches 回呼。</summary>
public delegate void BatchesCallback(in Batches batches);

/// <summary>單日多佇列求解器（solver_multi.rs 的 SolverDual）。</summary>
public interface ISolverDual
{
    /// <summary>列舉解。workers：工房總數。</summary>
    void SolveFn(SolverCtx ctx, sbyte[] demands, byte workers, BatchesCallback cb);
}

/// <summary>ISolverDual 的預設方法。</summary>
public static class SolverDualExtensions
{
    /// <summary>所有解（未排序）。</summary>
    public static Batches[] SolveUnordered(this ISolverDual self, SolverCtx ctx, sbyte[] demands, byte workers)
    {
        var result = new List<Batches>();
        self.SolveFn(ctx, demands, workers, (in Batches b) => result.Add(b));
        return result.ToArray();
    }

    /// <summary>前 MaxResult 名，由高到低排序。</summary>
    public static Batches[] Solve(this ISolverDual self, SolverCtx ctx, sbyte[] demands, byte workers)
    {
        var heap = new RustBinaryHeap<Batches, BatchesOrd>(ctx.Limit.MaxResult + 1);
        var withCost = ctx.Limit.WithCost;
        var max = ctx.Limit.MaxResult;
        self.SolveFn(ctx, demands, workers, (in Batches b) =>
        {
            var item = b;
            item.CmpValue = withCost ? (ushort)(item.Value - item.Cost) : item.Value;
            heap.Push(item);
            if (heap.Count > max)
                heap.Pop(out _);
        });
        return heap.IntoSortedVec();
    }

    /// <summary>唯一最佳解。</summary>
    public static Batches SolveBest(this ISolverDual self, SolverCtx ctx, sbyte[] demands, byte workers)
    {
        ushort maxVal = 0;
        var maxBatch = Batches.New();
        var withCost = ctx.Limit.WithCost;
        self.SolveFn(ctx, demands, workers, (in Batches item) =>
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
    public static Batches SolveBestFn(this ISolverDual self, SolverCtx ctx, sbyte[] demands, byte workers, SortValue<Batches> sortVal)
    {
        ushort maxVal = 0;
        var maxBatch = Batches.New();
        var withCost = ctx.Limit.WithCost;
        self.SolveFn(ctx, demands, workers, (in Batches item) =>
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
