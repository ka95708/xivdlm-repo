// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
using System;
using System.Runtime.CompilerServices;

namespace AetherKit.Game.Workshop;

/// <summary>固定 6 格的配方狀態佇列（null 表示空）。</summary>
[InlineArray(6)]
public struct RecipeQueue
{
    private RecipeState? element0;
}

/// <summary>（工房數量, 工作佇列）。</summary>
public struct WorkerQueue
{
    /// <summary>工房數量。</summary>
    public byte Workers;
    /// <summary>工作佇列。</summary>
    public RecipeQueue Recipes;

    /// <summary>建立。</summary>
    public WorkerQueue(byte workers, RecipeQueue recipes)
    {
        Workers = workers;
        Recipes = recipes;
    }
}

/// <summary>工房模擬器（single.rs + multi.rs）。</summary>
public static class Simulator
{
    /// <summary>Rust 的 f32 → u16 轉型（飽和、NaN 為 0）。</summary>
    internal static ushort SatU16(float f)
    {
        if (float.IsNaN(f) || f <= 0f)
            return 0;
        if (f >= 65535f)
            return 65535;
        return (ushort)f;
    }

    /// <summary>模擬一個步驟的價格。dup：需求變動值（此物品今日已產出的數量）。</summary>
    public static ushort Simulate(in CraftworkInfo info, in RecipeState recipe, byte dup)
    {
        var val = (float)((float)recipe.Value * info.FactorLevel());
        val = (float)(val * info.FactorTension());
        val = MathF.Floor(val);
        val = (float)(val * recipe.Factor(dup));
        return SatU16(MathF.Floor(val));
    }

    /// <summary>模擬一連串工序（只考慮連擊）。</summary>
    public static Batch SimulateBatch(in CraftworkInfo info, ReadOnlySpan<RecipeState> recipe)
        => SimulateBatchSeq(info, recipe).Batch;

    /// <summary>模擬一連串工序，回傳工序與變動後的工房狀態（只考慮連擊）。</summary>
    public static (Batch Batch, CraftworkInfo Info) SimulateBatchSeq(in CraftworkInfo info0, ReadOnlySpan<RecipeState> recipe)
    {
        var batch = Batch.New();
        var info = info0;
        for (var i = 0; i < recipe.Length; i++)
        {
            ref readonly var r = ref recipe[i];
            var demandSub = (byte)(batch.GetProduce(r.Id) * info.Workers);
            if (i != 0)
                info = info.Next();
            var val = Simulate(info, r, demandSub);
            batch = batch.Push(r.Id, val, r.Cost, r.CraftTime);
        }
        return (batch, info);
    }

    /// <summary>
    /// 模擬同時工作的多個工房。
    /// 回傳每個佇列的結果（工房數量, 工序）與最終工房狀態。
    /// </summary>
    public static (WorkerBatch[] Batches, CraftworkInfo Info) SimulateMultiBatch(in CraftworkInfo info, ReadOnlySpan<WorkerQueue> recipes)
    {
        var result = new WorkerBatch[recipes.Length];
        var newInfo = SimulateMultiBatch(info, recipes, result);
        return (result, newInfo);
    }

    /// <summary>同上，但把結果寫入呼叫端提供的 output（長度需 ≥ recipes.Length），避免配置記憶體。</summary>
    public static CraftworkInfo SimulateMultiBatch(in CraftworkInfo info0, ReadOnlySpan<WorkerQueue> recipes, Span<WorkerBatch> output)
    {
        var n = recipes.Length;
        Span<int> stJ = n <= 16 ? stackalloc int[n] : new int[n];
        Span<byte> stBeg = n <= 16 ? stackalloc byte[n] : new byte[n];
        Span<byte> stEnd = n <= 16 ? stackalloc byte[n] : new byte[n];
        var info = info0;

        byte maxRecipe = 0;
        Span<byte> tensionAdds = stackalloc byte[24];
        for (var i = 0; i < n; i++)
        {
            ref readonly var recipe = ref recipes[i];
            output[i] = new WorkerBatch(recipe.Workers, Batch.New());
            var first = recipe.Recipes[0];
            stJ[i] = 0;
            stBeg[i] = 0;
            stEnd[i] = first.HasValue ? first.Value.CraftTime : (byte)0;

            byte time = 0;
            for (var j = 0; j < 6; j++)
            {
                var r = recipe.Recipes[j];
                if (r.HasValue)
                {
                    // 計算產量陣列所需長度
                    if (r.Value.Id > maxRecipe)
                        maxRecipe = r.Value.Id;
                    // 各小時的幹勁疊加表
                    if (j != 0)
                        tensionAdds[time] = (byte)(tensionAdds[time] + recipe.Workers);
                    time = (byte)(time + r.Value.CraftTime);
                }
            }
        }

        // 產量，用來計算需求變動值
        Span<byte> produces = stackalloc byte[maxRecipe + 1];
        produces.Clear();

        byte currentTime = 0;
        while (true)
        {
            // 找最早結束的配方
            byte firstEnd = 24;
            for (var i = 0; i < n; i++)
            {
                // 長度為 0：空配方
                if (stBeg[i] == stEnd[i])
                    continue;
                if (stEnd[i] < firstEnd)
                    firstEnd = stEnd[i];
            }
            // 目前時間已經過了，代表全部結束
            if (firstEnd <= currentTime)
                break;

            // 結算前更新幹勁
            for (int i = currentTime; i < firstEnd; i++)
                info = info.AddTension(tensionAdds[i]);

            // 計算配方價格
            for (var i = 0; i < n; i++)
            {
                if (stEnd[i] != firstEnd || stBeg[i] == stEnd[i])
                    continue;
                var recipe = recipes[i].Recipes[stJ[i]];
                if (recipe.HasValue)
                {
                    var r = recipe.Value;
                    var val = Simulate(info, r, produces[r.Id]);
                    output[i].Batch = output[i].Batch.Push(r.Id, val, r.Cost, r.CraftTime);
                }
            }

            // 更新產量狀態
            currentTime = firstEnd;
            for (var i = 0; i < n; i++)
            {
                int j = stJ[i];
                var beg = stBeg[i];
                var end = stEnd[i];
                if (end != firstEnd || end == beg)
                    continue;
                // 計算產量，更新需求值
                var cur = recipes[i].Recipes[j];
                if (cur.HasValue)
                {
                    var workers = recipes[i].Workers;
                    var id = cur.Value.Id;
                    produces[id] = (byte)(produces[id] + (j == 0 ? 1 : 2) * workers);
                }

                // 計算新狀態
                RecipeState? next = j + 1 < 6 ? recipes[i].Recipes[j + 1] : null;
                var craftTime = next.HasValue ? next.Value.CraftTime : (byte)0;
                stJ[i] = j + 1;
                stBeg[i] = end;
                stEnd[i] = (byte)(end + craftTime);
            }
        }

        return info;
    }
}
