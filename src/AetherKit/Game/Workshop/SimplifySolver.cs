// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
namespace AetherKit.Game.Workshop;

/// <summary>
/// Simplify 簡易剪枝。
/// 在暴力搜尋過程中動態剪枝：工序累計滿 12 小時後計算每小時收益，
/// 若低於目前最高每小時收益的 69% 就剪掉。
/// 不保證所有解正確，只盡量保證最高的幾個解正確；69% 為實測值。
/// </summary>
public sealed class SimplifySolver : ISolverSingle
{
    /// <inheritdoc/>
    public void SolveFn(SolverCtx ctx, sbyte[] demands, byte workers, BatchCallback cb)
    {
        ushort avg = 0;
        var info = ctx.Info;
        if (workers != 0)
            info.Workers = workers;

        SolveSub(ctx, demands, Batch.New(), info, ref avg, cb);
    }

    private static void SolveSub(SolverCtx ctx, sbyte[] demands, Batch current, CraftworkInfo info, ref ushort max, BatchCallback cb)
    {
        var repo = ctx.Repo;
        var lastId = current.Last();
        var firstBatch = lastId == 0;
        ref readonly var last = ref repo.Recipe(lastId);
        var limitTime = ctx.Limit.Time;
        var remain = (byte)(limitTime - current.Time);
        var endTime = (byte)(limitTime - 4);

        // 連擊開始後就增加幹勁
        if (!firstBatch)
            info = info.Next();

        var len = repo.RecipeLen;
        for (var k = 0; k < len; k++)
        {
            ref readonly var r = ref repo.Recipe(k);
            if (r.CraftTime == 0 || remain < r.CraftTime)
                continue;
            if (!ctx.Limit.Check(r) || last.Id == r.Id)
                continue;
            // 連擊過濾
            if (!SolverUtil.BatchCheck(firstBatch, last, r))
                continue;

            int id = r.Id;
            var s = repo.State(id, demands[id]);
            var demandSub = (byte)(current.GetProduce(s.Id) * info.Workers);
            var val = Simulator.Simulate(info, s, demandSub);
            var next = current.Push(r.Id, val, r.Cost, r.CraftTime);

            if (next.Time >= 12)
            {
                var avg = (ushort)(next.Value / next.Time);

                // 平均收益不達標，剪枝
                if (avg < Simulator.SatU16((float)((float)max * 0.69f)))
                    continue;
            }

            if (next.Time > endTime)
            {
                // 工序結束
                cb(next);

                var avg = (ushort)(next.Value / limitTime);
                if (avg > max)
                    max = avg;
            }
            else
            {
                SolveSub(ctx, demands, next, info, ref max, cb);
            }
        }
    }
}
