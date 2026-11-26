// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
using System;

namespace AetherKit.Game.Workshop;

/// <summary>
/// Mild 全域求解器。
/// 以每天的最佳解組成整週解：對求解順序做排列組合，配合整週需求變動，
/// 部分考慮物品需求對後續的影響；並估計當天幹勁對後續的收益影響。
/// </summary>
public sealed class MildSolver : IGSolver
{
    /// <inheritdoc/>
    public Batch?[] Solve(SolverCtx ctx, DemandPattern[] pat)
    {
        var current = new Batch?[6];
        var max = new Batch?[6];
        ushort maxVal = 0;
        var demand = new sbyte[pat.Length];
        var seq = new byte[6];
        Dfs(ctx, pat, demand, current, 0, seq, ref max, ref maxVal);
        return max;
    }

    private void Dfs(SolverCtx ctx, DemandPattern[] pat, sbyte[] demandSub, Batch?[] current, byte depth, byte[] seq, ref Batch?[] max, ref ushort maxVal)
    {
        var workers = ctx.Info.Workers;
        for (var i = 0; i < 6; i++)
        {
            if (current[i].HasValue)
                continue;

            // 目前幹勁
            byte tension = 0;
            for (var k = 0; k < i; k++)
            {
                var b = current[k];
                if (!b.HasValue)
                    continue;
                tension = (byte)(tension + (byte)((byte)(b.Value.Seq - 1) * workers));
            }

            // 目前幹勁可能為後續帶來的收益增加量
            var tensionDelta = new ushort[6];
            if (tension < ctx.Info.MaxTension)
            {
                for (var j = 0; j < tensionDelta.Length; j++)
                {
                    var t = (byte)(tension + (byte)((byte)(1 + j) * workers));
                    t = Math.Min(t, ctx.Info.MaxTension);

                    var (_, value, _) = CalcValue(ctx, pat, current, i + 1, t);
                    tensionDelta[j] = value;
                }
            }

            // 需求值（原版在這裡算了 info.tension 但沒有用到）
            var solver = new SimplifySolver();

            var demand = Prediction.GetDemands(pat, (byte)(i + 1));
            var n = Math.Min(demand.Length, demandSub.Length);
            for (var k = 0; k < n; k++)
                demand[k] = (sbyte)(demand[k] - demandSub[k]);

            // 計算最佳
            var batch = solver.SolveBestFn(ctx, demand, 0, (ushort v, in Batch b) => (ushort)(v + tensionDelta[b.Seq - 1]));
            current[i] = batch;

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
                for (var k = 0; k < 6; k++)
                {
                    int id = batch.Steps[k];
                    if (id == 0)
                        break;
                    demandSub[id] = (sbyte)(demandSub[id] + (sbyte)((k == 0 ? 1 : 2) * (sbyte)workers));
                }

                Dfs(ctx, pat, demandSub, current, (byte)(depth + 1), seq, ref max, ref maxVal);

                // 還原需求變動值
                for (var k = 0; k < 6; k++)
                {
                    int id = batch.Steps[k];
                    if (id == 0)
                        break;
                    demandSub[id] = (sbyte)(demandSub[id] - (sbyte)((k == 0 ? 1 : 2) * (sbyte)workers));
                }
            }
            seq[depth] = 0;
            current[i] = null;
        }
    }

    /// <summary>計算一週的總收益。</summary>
    private static (Batch?[] Result, ushort Value, ushort Cost) CalcValue(SolverCtx ctx, DemandPattern[] pat, Batch?[] current, int begin, byte tension)
    {
        var result = new Batch?[6];
        var info = ctx.Info;
        info.Tension = tension;
        ushort val = 0;
        ushort cost = 0;

        var demandSub = new sbyte[pat.Length];
        Span<RecipeState> recipe = stackalloc RecipeState[6];

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
            for (var k = 0; k < batch.Seq; k++)
            {
                int id = batch.Steps[k];
                recipe[k] = ctx.Repo.State(id, (sbyte)(demand[id] - demandSub[id]));
            }

            Batch nb;
            (nb, info) = Simulator.SimulateBatchSeq(info, recipe[..batch.Seq]);

            // 更新需求變動表
            for (var k = 0; k < nb.Seq; k++)
            {
                int id = nb.Steps[k];
                var produce = (sbyte)((k == 0 ? 1 : 2) * (sbyte)ctx.Info.Workers);
                demandSub[id] = (sbyte)(demandSub[id] + produce);
            }
            result[i] = nb;
            val = (ushort)(val + nb.Value);
            cost = (ushort)(cost + nb.Cost);
        }

        return (result, val, cost);
    }
}
