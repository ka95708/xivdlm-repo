// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
namespace AetherKit.Game.Workshop;

/// <summary>
/// 整週排班求解器（gsolver/mod.rs 的 GSolver）。
/// 回傳 6 格：第 i 格為週期第 i+1 天（0 起算；即第 i+2 天，1 起算），null 為休息。
/// </summary>
public interface IGSolver
{
    /// <summary>求解整週（每天同一種佇列）。</summary>
    Batch?[] Solve(SolverCtx ctx, DemandPattern[] pat);
}

/// <summary>整週多工房排班求解器（GMultiSolver）。</summary>
public interface IGMultiSolver
{
    /// <summary>求解第 partId 分塊；回傳（6 格結果, 比較值）。</summary>
    (Batches?[] Result, ushort Value) SolvePart(SolverCtx ctx, DemandPattern[] pat, int partId);

    /// <summary>分塊數量。</summary>
    int Parts { get; }
}

/// <summary>IGMultiSolver 的預設方法。</summary>
public static class GMultiSolverExtensions
{
    /// <summary>依序求解所有分塊並取最佳。</summary>
    public static Batches?[] Solve(this IGMultiSolver self, SolverCtx ctx, DemandPattern[] pat)
    {
        var maxResult = new Batches?[6];
        ushort maxValue = 0;
        for (var id = 0; id < self.Parts; id++)
        {
            var (result, value) = self.SolvePart(ctx, pat, id);
            if (value > maxValue)
            {
                maxValue = value;
                maxResult = result;
            }
        }
        return maxResult;
    }
}

/// <summary>簡易貓票求解器（GFavorSolver）。</summary>
public interface IGFavorSolver
{
    /// <summary>求解整週並安排貓耳小員委託（會修改 ctx.Info.Workers）。</summary>
    Batches?[] Solve(SolverCtx ctx, DemandPattern[] pat, Favor[] favors);
}
