// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AetherKit.Game.Workshop;

/// <summary>
/// 需求變動模式：一週 7 天中哪一天出現需求高峰，以及強／弱。
/// DayNStrong 表示第 N 天（從 1 起算）為強高峰。
/// </summary>
public enum DemandPattern : byte
{
    /// <summary>未知（需求固定視為 9）。</summary>
    Unknown = 0,
    /// <summary>第 2 天強。</summary>
    Day2Strong = 1,
    /// <summary>第 2 天弱。</summary>
    Day2Weak = 2,
    /// <summary>第 3 天強。</summary>
    Day3Strong = 3,
    /// <summary>第 3 天弱。</summary>
    Day3Weak = 4,
    /// <summary>第 4 天強。</summary>
    Day4Strong = 5,
    /// <summary>第 4 天弱。</summary>
    Day4Weak = 6,
    /// <summary>第 5 天強。</summary>
    Day5Strong = 7,
    /// <summary>第 5 天弱。</summary>
    Day5Weak = 8,
    /// <summary>第 6 天強。</summary>
    Day6Strong = 9,
    /// <summary>第 6 天弱。</summary>
    Day6Weak = 10,
    /// <summary>第 7 天強。</summary>
    Day7Strong = 11,
    /// <summary>第 7 天弱。</summary>
    Day7Weak = 12,
}

/// <summary>需求預測（predition/pred.rs、pattern.rs）。</summary>
public static class Prediction
{
    /// <summary>位元組轉模式；超出 0..12 視為未知。</summary>
    public static DemandPattern PatternFromByte(byte val) => val <= 12 ? (DemandPattern)val : DemandPattern.Unknown;

    /// <summary>位元組陣列轉模式陣列。</summary>
    public static DemandPattern[] PatternsFromBytes(ReadOnlySpan<byte> data)
    {
        var result = new DemandPattern[data.Length];
        for (var i = 0; i < data.Length; i++)
            result[i] = PatternFromByte(data[i]);
        return result;
    }

    /// <summary>真實需求表：[模式 - 1][週內第幾天（0 起算）]。</summary>
    public static readonly sbyte[][] RealDemandTable =
    {
        new sbyte[] { 16, 24, 9, 9, 9, 9, 9 },
        new sbyte[] { 13, 17, 7, 7, 7, 7, 7 },
        new sbyte[] { 9, 16, 24, 9, 9, 9, 9 },
        new sbyte[] { 9, 13, 17, 7, 7, 7, 7 },
        new sbyte[] { 9, 9, 16, 24, 9, 9, 9 },
        new sbyte[] { 9, 9, 13, 17, 7, 7, 7 },
        new sbyte[] { 9, 9, 9, 16, 24, 9, 9 },
        new sbyte[] { 9, 9, 9, 13, 17, 7, 7 },
        new sbyte[] { 9, 10, 2, 9, 16, 24, 9 },
        new sbyte[] { 9, 10, 5, 8, 13, 17, 7 },
        new sbyte[] { 9, 10, 2, 2, 9, 16, 24 },
        new sbyte[] { 9, 10, 2, 5, 8, 13, 17 },
    };

    /// <summary>依模式取得指定日的真實需求值。day 從 0 起算（0 = 週期第一天）；day ≥ 7 或未知模式回傳 9。</summary>
    public static sbyte GetDemand(DemandPattern pattern, byte day)
    {
        if (day >= 7 || pattern == DemandPattern.Unknown)
            return 9;
        var index = (byte)pattern - 1;
        return RealDemandTable[index][day];
    }

    /// <summary>依模式陣列取得指定日的需求陣列（索引同 pattern）。day 從 0 起算。</summary>
    public static sbyte[] GetDemands(IReadOnlyList<DemandPattern> pattern, byte day)
    {
        var demands = new sbyte[pattern.Count];
        for (var i = 0; i < pattern.Count; i++)
            demands[i] = GetDemand(pattern[i], day);
        return demands;
    }

    /// <summary>依已知的需求與需求變化（從週期第一天起）猜測模式；無法唯一確定時回傳 Unknown。</summary>
    public static DemandPattern Predict(ReadOnlySpan<(Demand Demand, DemandChange Change)> seq)
    {
        if (seq.Length > 7)
            return DemandPattern.Unknown;

        Span<bool> candidates = stackalloc bool[12];
        candidates.Fill(true);

        for (var i = 0; i < seq.Length; i++)
        {
            var (demand, change) = seq[i];
            if (i == 0 && demand == Demand.High)
            {
                switch (change)
                {
                    case DemandChange.Equal:
                        return DemandPattern.Day2Strong;
                    case DemandChange.Increase:
                        return DemandPattern.Day2Weak;
                    default:
                        // 2 強或 2 弱
                        for (var k = 2; k < candidates.Length; k++)
                            candidates[k] = false;
                        break;
                }
                continue;
            }

            // 篩選
            for (var j = 0; j < candidates.Length; j++)
            {
                if (!candidates[j])
                    continue;
                if (DemandUtil.DemandFromVal(RealDemandTable[j][i]) != demand)
                {
                    candidates[j] = false;
                }
                else if (i > 0)
                {
                    var delta = (sbyte)(RealDemandTable[j][i] - RealDemandTable[j][i - 1]);
                    if (DemandUtil.ChangeFromVal(delta) != change)
                        candidates[j] = false;
                }
            }

            // 剩餘可能數量
            var count = 0;
            var lastCandidate = 0;
            for (var j = 0; j < candidates.Length; j++)
            {
                if (candidates[j])
                {
                    count += 1;
                    lastCandidate = j;
                }
            }

            // 唯一可能
            if (count == 1)
                return PatternFromByte((byte)(lastCandidate + 1));
            // 沒有可能
            if (count == 0)
                return DemandPattern.Unknown;
        }

        return DemandPattern.Unknown;
    }

    /// <summary>
    /// （進階）預測物品需求，回傳所有可能模式的位元圖：第 j 位（0 起算）代表 DemandPattern 值 j+1。
    /// seq 為本週從第一天起的需求與變化；lastDemand 為上週最後一天的真實需求值（不知道則 null）。
    /// </summary>
    public static ushort PredictAdv(ReadOnlySpan<(Demand Demand, DemandChange Change)> seq, sbyte? lastDemand)
    {
        if (seq.Length > 7)
            return 0;

        ushort candidates = 0b1111_1111_1111;
        for (var i = 0; i < seq.Length; i++)
        {
            var (demand, change) = seq[i];

            for (var j = 0; j < 12; j++)
            {
                var mask = (ushort)(1 << j);
                if ((candidates & mask) == 0)
                    continue;
                var predDemand = RealDemandTable[j][i];
                var predDem = DemandUtil.DemandFromVal(predDemand);

                // 檢查需求是否符合此模式
                if (predDem != demand)
                {
                    // 第一、二天用原值；其他天可能受自己的產量影響而改變
                    if (DemandUtil.Less(predDem, demand) || i < 2)
                    {
                        candidates = (ushort)(candidates & ~mask);
                        continue;
                    }
                }

                // 前一天的需求值
                sbyte? prev = i == 0 ? lastDemand : RealDemandTable[j][i - 1];

                // 計算需求變化量
                if (prev.HasValue)
                {
                    var delta = (sbyte)(predDemand - prev.Value);
                    if (DemandUtil.ChangeFromVal(delta) != change)
                        candidates = (ushort)(candidates & ~mask);
                }
            }

            // 唯一解或無解直接回傳
            if (BitOperations.PopCount(candidates) <= 1)
                return candidates;
        }
        // 資訊不足，回傳目前的預測
        return candidates;
    }

    /// <summary>
    /// 批次預測。seqs 依「單一物品各天」優先排列，每個物品 days 筆；回傳每個物品的模式。
    /// </summary>
    public static DemandPattern[] PredictAll(ReadOnlySpan<(Demand Demand, DemandChange Change)> seqs, int days)
    {
        var result = new List<DemandPattern>();
        for (var i = 0; i < seqs.Length; i += days)
            result.Add(Predict(seqs.Slice(i, days)));
        return result.ToArray();
    }
}
