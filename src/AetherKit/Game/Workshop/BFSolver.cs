// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
namespace AetherKit.Game.Workshop;

/// <summary>
/// Brute Force 暴力搜尋。
/// 對解空間做完整走訪，得到所有情況的收益，結果一定正確。
/// </summary>
public sealed class BFSolver : ISolverSingle, ISolverWithBatch
{
    /// <inheritdoc/>
    public void SolveFn(SolverCtx ctx, sbyte[] demands, byte workers, BatchCallback cb)
    {
        var info = ctx.Info;
        if (workers != 0)
            info.Workers = workers;

        SolveSub(ctx, demands, Batch.New(), info, cb);
    }

    private static void SolveSub(SolverCtx ctx, sbyte[] demands, Batch current, CraftworkInfo info, BatchCallback cb)
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

            if (next.Time > endTime)
            {
                // 工序結束
                cb(next);
            }
            else
            {
                SolveSub(ctx, demands, next, info, cb);
            }
        }
    }

    /// <inheritdoc/>
    public void SolveFn(SolverCtx ctx, PresetQueue[] set, sbyte[] demands, byte workers, QueueFilter filter, BatchWithBatchCallback cb)
    {
        var repo = ctx.Repo;
        // 準備計算狀態
        var recipes = new WorkerQueue[set.Length + 1];
        for (var n = 0; n < set.Length; n++)
        {
            var arr = new RecipeQueue();
            for (var i = 0; i < 6; i++)
            {
                int id = set[n].Steps[i];
                arr[i] = id == 0 ? null : repo.State(id, demands[id]);
            }
            recipes[n] = new WorkerQueue(set[n].Workers, arr);
        }
        recipes[set.Length] = new WorkerQueue(workers, new RecipeQueue());

        var output = new WorkerBatch[recipes.Length];
        var len = repo.RecipeLen;
        var limitTime = ctx.Limit.Time;
        var pos = 0;
        byte sumTime = 0;
        while (true)
        {
            // 要修改的佇列
            ref var recipe = ref recipes[set.Length].Recipes;
            var slot = recipe[pos];
            int currId = slot.HasValue ? slot.Value.Id : 0;

            sumTime = (byte)(sumTime - repo.Recipe(currId).CraftTime);

            while (currId < len)
            {
                currId += 1;
                if (currId >= len)
                    break;

                ref readonly var r = ref repo.Recipe(currId);

                var prev = pos == 0 ? null : recipe[pos - 1];
                int lastIdx = prev.HasValue ? prev.Value.Id : 0;
                ref readonly var last = ref repo.Recipe(lastIdx);

                // 時間過濾
                if (r.CraftTime == 0 || (byte)(sumTime + r.CraftTime) > limitTime)
                    continue;
                if (!ctx.Limit.Check(r) || last.Id == r.Id)
                    continue;
                // 連擊過濾
                if (!SolverUtil.BatchCheck(pos == 0, last, r))
                    continue;

                sumTime = (byte)(sumTime + r.CraftTime);
                recipe[pos] = repo.State(currId, demands[currId]);
                break;
            }

            // 已到尾端：本層結束，回上一層
            if (currId >= len)
            {
                if (pos == 0)
                    break; // 完全結束
                recipe[pos] = null;
                pos -= 1;
                continue;
            }

            // 快速判斷還能不能插入新步驟
            if ((byte)(sumTime + 4) <= limitTime && pos < 6 - 1)
            {
                pos += 1;
                continue;
            }

            if (!filter(recipe))
                continue;

            // 不能再插入，直接計算
            Simulator.SimulateMultiBatch(ctx.Info, recipes, output);
            var b2b = BatchWithBatch.FromBatch(output[recipes.Length - 1].Batch);
            for (var i = 0; i < recipes.Length - 1; i++)
            {
                var w = output[i].Workers;
                b2b.Value = (ushort)(b2b.Value + (ushort)(output[i].Batch.Value * w));
                b2b.Cost = (ushort)(b2b.Cost + (ushort)(output[i].Batch.Cost * w));
            }

            cb(b2b);
        }
    }
}
