// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
namespace AetherKit.Game.Workshop;

/// <summary>
/// 簡易貓票（貓耳小員委託）求解器。
/// 先用 G 以「工房數 - 1」解出整週，再逐日用 S 為剩下 1 間工房挑選能滿足委託的佇列。
/// （原版另有未被呼叫、會輸出除錯訊息的 solve_complex，未移植。）
/// </summary>
public sealed class FavorSolver<G, S> : IGFavorSolver
    where G : IGSolver
    where S : ISolverWithBatch
{
    private readonly G gsolver;
    private readonly S solver;

    /// <summary>建立。</summary>
    public FavorSolver(G gsolver, S solver)
    {
        this.gsolver = gsolver;
        this.solver = solver;
    }

    /// <inheritdoc/>
    public Batches?[] Solve(SolverCtx ctx, DemandPattern[] pat, Favor[] favorList)
    {
        // 注意：原版減 1 後沒有加回來（u8 環繞）
        ctx.Info.Workers = (byte)(ctx.Info.Workers - 1);
        var result = gsolver.Solve(ctx, pat);
        var workers = ctx.Info.Workers;

        // 預先計算需求變動值
        var demandsSub = new sbyte[pat.Length];
        foreach (var batch in result)
        {
            if (batch.HasValue)
                batch.Value.DemandSub(demandsSub, (sbyte)workers);
        }

        var baseResult = new Batches?[6];
        for (var i = 0; i < 6; i++)
            baseResult[i] = result[i].HasValue ? Batches.FromSingleBatch(result[i]!.Value, workers) : null;

        var favors = new Favors(favorList);
        foreach (var item in result)
        {
            if (item.HasValue)
                favors.AddSeq(item.Value, workers);
        }

        for (var i = 0; i < 6; i++)
        {
            if (!baseResult[i].HasValue)
                continue;
            var item = baseResult[i]!.Value;

            // 當日需求（原版為加上變動值）
            var demands = Prediction.GetDemands(pat, (byte)(i + 1));
            for (var k = 0; k < demands.Length; k++)
                demands[k] = (sbyte)(demands[k] + demandsSub[k]);

            var set = new[] { new PresetQueue(item.First.Workers, item.First.Batch.Steps) };
            var best = solver.SolveFavorBest(ctx, set, demands, 1, favors.ToFavors());
            item.SetSecondResult(best.Batch, 1);
            // 更新需求變動表
            item.Second.Batch.DemandSub(demandsSub, 1);
            // 更新貓票表
            favors.AddSeq(item.Second.Batch, item.Second.Workers);
            baseResult[i] = item;
        }

        return baseResult;
    }
}
