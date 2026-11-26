using System.Collections.Generic;
using System.Linq;

namespace AetherKit.Tweaks;

/// <summary>
/// 仙人微彩的最佳解：3×3 格子放 1～9 各一次，開局翻開 1 格，玩家再翻 3 格，最後選一條線，
/// 依線上三數的和拿 MGP。這裡用期望值硬算：
/// 翻格時挑「之後照最佳策略玩，期望 MGP 最高」的那格；選線時挑期望 MGP 最高的線。
/// 盤面跟翻開順序無關，算過的盤面記起來，整局算一次不到一秒。
/// </summary>
public static class CactpotSolver
{
    /// <summary>線上三數的和 → MGP。</summary>
    private static readonly Dictionary<int, int> Payout = new()
    {
        [6] = 10000, [7] = 36, [8] = 720, [9] = 360, [10] = 80, [11] = 252, [12] = 108, [13] = 72, [14] = 54,
        [15] = 180, [16] = 72, [17] = 180, [18] = 119, [19] = 36, [20] = 306, [21] = 1080, [22] = 144,
        [23] = 1800, [24] = 3600
    };

    /// <summary>
    /// 八條線，順序和遊戲視窗上的選線按鈕一樣（第幾個就送第幾號）：
    /// 左上斜線、三條直行（左到右）、右上斜線、三條橫列（上到下）。格子編號是由左到右、由上到下 0～8。
    /// </summary>
    public static readonly int[][] Lines =
    {
        new[] { 0, 4, 8 },
        new[] { 0, 3, 6 }, new[] { 1, 4, 7 }, new[] { 2, 5, 8 },
        new[] { 2, 4, 6 },
        new[] { 0, 1, 2 }, new[] { 3, 4, 5 }, new[] { 6, 7, 8 }
    };

    /// <summary>開到第幾格時該選線（開局 1 格 + 玩家翻 3 格）。</summary>
    public const int RevealedBeforeLine = 4;

    /// <summary>下一格該翻哪格；board 是 9 格的數字，0 = 還沒翻開。</summary>
    public static int BestCell(int[] board)
    {
        var memo = new Dictionary<long, double>();
        var best = -1;
        var bestValue = double.MinValue;

        for (var cell = 0; cell < 9; cell++)
        {
            if (board[cell] != 0) continue;

            var value = RevealValue(board, cell, memo);
            if (value > bestValue)
            {
                bestValue = value;
                best = cell;
            }
        }

        return best;
    }

    /// <summary>該選哪條線（Lines 的索引）與它的期望 MGP。</summary>
    public static (int Line, double Expected) BestLine(int[] board)
    {
        var unused = Unused(board);
        var best = 0;
        var bestValue = double.MinValue;

        for (var line = 0; line < Lines.Length; line++)
        {
            var value = LineExpectation(board, Lines[line], unused);
            if (value > bestValue)
            {
                bestValue = value;
                best = line;
            }
        }

        return (best, bestValue);
    }

    /// <summary>翻開 cell 之後的期望值：對每個可能翻出的數字取平均。</summary>
    private static double RevealValue(int[] board, int cell, Dictionary<long, double> memo)
    {
        var unused = Unused(board);
        var total = 0.0;

        foreach (var number in unused)
        {
            board[cell] = number;
            total += Value(board, memo);
            board[cell] = 0;
        }

        return total / unused.Count;
    }

    /// <summary>這個盤面照最佳策略玩下去的期望 MGP。</summary>
    private static double Value(int[] board, Dictionary<long, double> memo)
    {
        var key = Key(board);
        if (memo.TryGetValue(key, out var cached)) return cached;

        double result;
        if (board.Count(n => n != 0) >= RevealedBeforeLine)
        {
            result = BestLine(board).Expected;
        }
        else
        {
            result = double.MinValue;
            for (var cell = 0; cell < 9; cell++)
            {
                if (board[cell] != 0) continue;
                result = System.Math.Max(result, RevealValue(board, cell, memo));
            }
        }

        memo[key] = result;
        return result;
    }

    /// <summary>一條線的期望 MGP：線上還沒翻開的格子，依序填入沒用過的數字，所有排列平均。</summary>
    private static double LineExpectation(int[] board, int[] line, List<int> unused)
    {
        var known = 0;
        var hidden = 0;
        foreach (var cell in line)
        {
            if (board[cell] == 0) hidden++;
            else known += board[cell];
        }

        var total = 0.0;
        var count = 0;
        Fill(hidden, known, unused, new bool[unused.Count], ref total, ref count);
        return count == 0 ? 0 : total / count;
    }

    private static void Fill(int remaining, int sum, List<int> unused, bool[] taken, ref double total, ref int count)
    {
        if (remaining == 0)
        {
            total += Payout[sum];
            count++;
            return;
        }

        for (var i = 0; i < unused.Count; i++)
        {
            if (taken[i]) continue;

            taken[i] = true;
            Fill(remaining - 1, sum + unused[i], unused, taken, ref total, ref count);
            taken[i] = false;
        }
    }

    private static List<int> Unused(int[] board) =>
        Enumerable.Range(1, 9).Where(n => !board.Contains(n)).ToList();

    private static long Key(int[] board)
    {
        long key = 0;
        foreach (var n in board) key = key * 10 + n;
        return key;
    }
}
