// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
using System;
using System.Collections.Generic;

namespace AetherKit.Game.Workshop;

/// <summary>
/// 進階簡化的單日雙佇列求解器（simplify_adv.rs）。
/// 先以最壞疊箱情況求出下限，再挑出非疊箱時理論值不低於下限的候選工序，兩兩組合模擬。
/// </summary>
public sealed class AdvancedSimplifySolver<U> : ISolverDual
    where U : ISolverSingle
{
    private readonly U solver;

    /// <summary>以單佇列求解器建立。</summary>
    public AdvancedSimplifySolver(U solver)
    {
        this.solver = solver;
    }

    /// <inheritdoc/>
    public void SolveFn(SolverCtx ctx, sbyte[] demands, byte workers, BatchesCallback cb)
    {
        var info = ctx.Info;
        info.Workers = workers;
        var withCost = ctx.Limit.WithCost;
        var repo = ctx.Repo;

        // 以最大工房數計算最壞疊箱時（各佇列組合的理論最小值）的最大值
        var maxBatch = solver.SolveBest(ctx, demands, workers);
        var maxBatchVal = withCost ? (ushort)(maxBatch.Value - maxBatch.Cost) : maxBatch.Value;

        // 計算非疊箱時的最大值（各組合的理論最大值）
        var candidates = new List<RecipeQueue>();
        solver.SolveFn(ctx, demands, 1, (in Batch b) =>
        {
            // 補償多個工房的幹勁加成（全部為 u8 運算）
            var seqM1 = (byte)(b.Seq - 1);
            var num = (byte)(100 + (byte)(seqM1 * workers));
            var den = (byte)(100 + seqM1);
            var factor = (float)((float)num / (float)den);
            var value = Simulator.SatU16((float)((float)b.Value * factor));
            value = withCost ? (ushort)(value - b.Cost) : value;
            if (value >= maxBatchVal)
            {
                var recipe = new RecipeQueue();
                for (var i = 0; i < b.Seq; i++)
                {
                    int id = b.Steps[i];
                    recipe[i] = repo.State(id, demands[id]);
                }
                candidates.Add(recipe);
            }
        });

        // 模擬計算
        var pair = new WorkerQueue[2];
        var output = new WorkerBatch[2];
        Span<RecipeState> single = stackalloc RecipeState[6];
        Span<WorkerBatch> batches = stackalloc WorkerBatch[2];
        for (var i = 0; i < candidates.Count; i++)
        {
            for (var j = i + 1; j < candidates.Count; j++)
            {
                var k = (byte)(workers - 1);
                var maxBatches = Batches.New();
                ushort maxVal = 0;
                while (k > 0)
                {
                    var other = (byte)(workers - k);
                    if (k > other)
                    {
                        pair[0] = new WorkerQueue(k, candidates[i]);
                        pair[1] = new WorkerQueue(other, candidates[j]);
                    }
                    else
                    {
                        pair[0] = new WorkerQueue(other, candidates[j]);
                        pair[1] = new WorkerQueue(k, candidates[i]);
                    }
                    Simulator.SimulateMultiBatch(ctx.Info, pair, output);
                    var batch = Batches.FromBatch(output);
                    var value = withCost ? (ushort)(batch.Value - batch.Cost) : batch.Value;
                    if (value > maxVal)
                    {
                        maxVal = value;
                        maxBatches = batch;
                    }
                    k -= 1;
                }
                cb(maxBatches);
            }

            var len = 0;
            var cand = candidates[i];
            for (var j = 0; j < 6; j++)
            {
                var r = cand[j];
                if (r.HasValue)
                {
                    single[j] = r.Value;
                    len += 1;
                }
                else
                {
                    single[j] = RecipeState.Empty();
                }
            }

            var one = Simulator.SimulateBatch(info, single[..len]);
            batches[0] = new WorkerBatch(workers, one);
            batches[1] = new WorkerBatch(0, Batch.New());
            cb(Batches.FromBatch(batches));
        }
    }
}
