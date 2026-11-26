// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
using System;
using System.Collections.Generic;

namespace AetherKit.Game.Workshop;

/// <summary>
/// Mild 全域多佇列求解器（Rust 的 MildMulitSolver）。
/// 以每天的最佳雙佇列解組成整週解，求解順序做排列組合，並估計幹勁對後續的影響。
/// 內含以（幹勁, 當日需求）為鍵的快取；輸入（配方、歡迎度、限制）改變時需呼叫 <see cref="ClearCache"/>。
/// </summary>
public sealed class MildMultiSolver<S> : IGMultiSolver
    where S : ISolverDual
{
    private readonly S solver;
    private readonly Dictionary<ulong, Batches> cache = new();

    /// <summary>以單日雙佇列求解器建立。</summary>
    public MildMultiSolver(S solver)
    {
        this.solver = solver;
    }

    /// <summary>清除快取。</summary>
    public void ClearCache() => cache.Clear();

    /// <inheritdoc/>
    public int Parts => 6 * 5;

    /// <inheritdoc/>
    public (Batches?[] Result, ushort Value) SolvePart(SolverCtx ctx, DemandPattern[] pat, int partId)
    {
        var current = new Batches?[6];
        var max = new Batches?[6];
        ushort maxVal = 0;
        var demand = new sbyte[pat.Length];
        var seq = new byte[6];

        var id1 = partId / 5; // 第一天位置
        var id2 = partId % 5; // 第二天位置
        if (id2 >= id1)
            id2 += 1;
        // 更新 seq（除錯用）
        seq[0] = (byte)(id1 + 2);
        seq[1] = (byte)(id2 + 2);

        // 更新需求變動
        var batch = AddAt(ctx, pat, demand, current, id1);
        batch.ProduceAdd(demand);
        batch = AddAt(ctx, pat, demand, current, id2);
        batch.ProduceAdd(demand);

        Dfs(ctx, pat, demand, current, 2, seq, ref max, ref maxVal);

        return (max, maxVal);
    }

    private void Dfs(SolverCtx ctx, DemandPattern[] pat, sbyte[] demandSub, Batches?[] current, byte depth, byte[] seq, ref Batches?[] max, ref ushort maxVal)
    {
        for (var i = 0; i < 6; i++)
        {
            if (current[i].HasValue)
                continue;

            var batch = AddAt(ctx, pat, demandSub, current, i);

            seq[depth] = (byte)(i + 2);
            if (depth >= 4)
            {
                // 全部求解完畢
                var (realBatch, val, cost) = CalcValue(ctx, pat, current, 0, 0);
                var valCmp = ctx.Limit.WithCost ? (ushort)(val - cost) : val;

                if (valCmp > maxVal)
                {
                    maxVal = valCmp;
                    max = realBatch;
                }
            }
            else
            {
                // 需求變動值
                batch.ProduceAdd(demandSub);

                Dfs(ctx, pat, demandSub, current, (byte)(depth + 1), seq, ref max, ref maxVal);

                // 還原需求變動值
                batch.ProduceSub(demandSub);
            }
            seq[depth] = 0;
            current[i] = null;
        }
    }

    private Batches AddAt(SolverCtx ctx, DemandPattern[] pat, sbyte[] demandSub, Batches?[] current, int i)
    {
        var workers = ctx.Info.Workers;

        // 目前幹勁
        byte tension = 0;
        for (var k = 0; k < i; k++)
        {
            var b = current[k];
            if (!b.HasValue)
                continue;
            tension = (byte)(tension + b.Value.TensionAdd());
        }

        // 目前幹勁可能為後續帶來的收益增加量
        var tensionDelta = new ushort[6];
        if (tension < ctx.Info.MaxTension)
        {
            for (var j = 0; j < tensionDelta.Length; j++)
            {
                var t = (byte)(tension + (byte)((byte)j * workers));
                t = Math.Min(t, ctx.Info.MaxTension);

                var (_, value, _) = CalcValue(ctx, pat, current, i + 1, t);
                tensionDelta[j] = value;
            }
        }

        // 需求值
        var info = ctx.Info;
        info.Tension = tension;

        var demand = Prediction.GetDemands(pat, (byte)(i + 1));
        var n = Math.Min(demand.Length, demandSub.Length);
        for (var k = 0; k < n; k++)
            demand[k] = (sbyte)(demand[k] - demandSub[k]);

        var hash = FnvHash(tension, demand);
        if (!cache.TryGetValue(hash, out var batch))
        {
            // 計算最佳
            batch = solver.SolveBestFn(ctx, demand, info.Workers,
                (ushort v, in Batches b) => (ushort)(v + tensionDelta[b.TensionAdd() / workers]));
            cache[hash] = batch;
        }

        current[i] = batch;
        return batch;
    }

    /// <summary>計算一週的總收益。</summary>
    private static (Batches?[] Result, ushort Value, ushort Cost) CalcValue(SolverCtx ctx, DemandPattern[] pat, Batches?[] current, int begin, byte tension)
    {
        var result = new Batches?[6];
        var info = ctx.Info;
        info.Tension = tension;
        ushort val = 0;
        ushort cost = 0;

        var demandSub = new sbyte[pat.Length];
        var recipes = new WorkerQueue[2];
        var output = new WorkerBatch[2];

        for (var i = begin; i < 6; i++)
        {
            var cur = current[i];
            if (!cur.HasValue)
            {
                result[i] = null;
                continue;
            }

            var batch = cur.Value;
            // 需求變動
            var demand = Prediction.GetDemands(pat, (byte)(i + 1));
            for (var q = 0; q < 2; q++)
            {
                var wb = batch.Get(q);
                var recipe = new RecipeQueue();
                for (var k = 0; k < wb.Batch.Seq; k++)
                {
                    int id = wb.Batch.Steps[k];
                    recipe[k] = ctx.Repo.State(id, (sbyte)(demand[id] - demandSub[id]));
                }
                recipes[q] = new WorkerQueue(wb.Workers, recipe);
            }

            info = Simulator.SimulateMultiBatch(info, recipes, output);

            // 更新需求變動表
            foreach (var wb in output)
                wb.Batch.DemandSub(demandSub, (sbyte)wb.Workers);

            batch.SetResult(output);

            result[i] = batch;
            val = (ushort)(val + batch.Value);
            cost = (ushort)(cost + batch.Cost);
        }

        return (result, val, cost);
    }

    private const ulong InitialState = 0xcbf29ce484222325;
    private const ulong Prime = 0x100000001b3;

    /// <summary>FNV 雜湊（與原版相同：i8 以符號延伸轉 u64）。</summary>
    internal static ulong FnvHash(byte tension, sbyte[] demands)
    {
        unchecked
        {
            var hash = (InitialState ^ tension) * Prime;
            for (var i = 0; i < demands.Length; i++)
            {
                hash ^= (ulong)(long)demands[i];
                hash *= Prime;
            }
            return hash;
        }
    }
}
