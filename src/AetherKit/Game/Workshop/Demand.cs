// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
using System;

namespace AetherKit.Game.Workshop;

/// <summary>市場需求（數值與封包格式一致：0 最高、4 最低）。</summary>
public enum Demand : byte
{
    /// <summary>嚴重供不應求。</summary>
    VeryHigh = 0,
    /// <summary>供不應求。</summary>
    High = 1,
    /// <summary>供需平衡。</summary>
    Average = 2,
    /// <summary>供過於求。</summary>
    Low = 3,
    /// <summary>嚴重供過於求。</summary>
    VeryLow = 4,
}

/// <summary>需求變化量（數值與封包格式一致）。</summary>
public enum DemandChange : byte
{
    /// <summary>需求大幅增加。</summary>
    MassiveIncrease = 0,
    /// <summary>需求增加。</summary>
    Increase = 1,
    /// <summary>無變化。</summary>
    Equal = 2,
    /// <summary>需求降低。</summary>
    Decrease = 3,
    /// <summary>需求大幅降低。</summary>
    MassiveDecrease = 4,
}

/// <summary>Demand / DemandChange 的輔助函式。</summary>
public static class DemandUtil
{
    /// <summary>真實需求值上限（含）。</summary>
    public static short UpperBound(this Demand d) => d switch
    {
        Demand.VeryHigh => short.MaxValue,
        Demand.High => 17,
        Demand.Average => 9,
        Demand.Low => 1,
        _ => -7,
    };

    /// <summary>真實需求值下限（含）。</summary>
    public static short LowerBound(this Demand d) => d switch
    {
        Demand.VeryHigh => 18,
        Demand.High => 10,
        Demand.Average => 2,
        Demand.Low => -6,
        _ => -999,
    };

    /// <summary>依真實需求值對應到區間。</summary>
    public static Demand DemandFromVal(short val)
    {
        if (val >= 18) return Demand.VeryHigh;
        if (val >= 10) return Demand.High;
        if (val >= 2) return Demand.Average;
        if (val >= -6) return Demand.Low;
        return Demand.VeryLow;
    }

    /// <summary>需求係數（百分比）。</summary>
    public static byte Factor(this Demand d) => d switch
    {
        Demand.VeryHigh => 160,
        Demand.High => 130,
        Demand.Average => 100,
        Demand.Low => 80,
        _ => 60,
    };

    /// <summary>位元組轉需求；超出 0..4 時 Rust 會 panic，這裡丟例外。</summary>
    public static Demand DemandFromByte(byte val)
        => val <= 4 ? (Demand)val : throw new ArgumentOutOfRangeException(nameof(val));

    /// <summary>
    /// Rust 的 PartialOrd 是反向的：a &lt; b 代表 a 的需求等級比 b 低（列舉值較大）。
    /// </summary>
    public static bool Less(Demand a, Demand b) => (byte)b < (byte)a;

    /// <summary>真實變動值上限（含）。</summary>
    public static short UpperBound(this DemandChange c) => c switch
    {
        DemandChange.MassiveIncrease => short.MaxValue,
        DemandChange.Increase => 5,
        DemandChange.Equal => 0,
        DemandChange.Decrease => -1,
        _ => -6,
    };

    /// <summary>真實變動值下限（含）。</summary>
    public static short LowerBound(this DemandChange c) => c switch
    {
        DemandChange.MassiveIncrease => 6,
        DemandChange.Increase => 1,
        DemandChange.Equal => 0,
        DemandChange.Decrease => -5,
        _ => -999,
    };

    /// <summary>依真實變動值對應到區間。</summary>
    public static DemandChange ChangeFromVal(short val)
    {
        if (val >= 6) return DemandChange.MassiveIncrease;
        if (val >= 1) return DemandChange.Increase;
        if (val == 0) return DemandChange.Equal;
        if (val >= -5) return DemandChange.Decrease;
        return DemandChange.MassiveDecrease;
    }

    /// <summary>位元組轉需求變化；超出 0..4 時 Rust 會 panic，這裡丟例外。</summary>
    public static DemandChange ChangeFromByte(byte val)
        => val <= 4 ? (DemandChange)val : throw new ArgumentOutOfRangeException(nameof(val));

    /// <summary>解析封包格式的需求位元組：高 4 位是需求，低 4 位是需求變化。</summary>
    public static (Demand Demand, DemandChange Change) Decode(byte b)
        => (DemandFromByte((byte)(b >> 4)), ChangeFromByte((byte)(b & 0x0F)));
}
