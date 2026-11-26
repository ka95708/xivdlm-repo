// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
namespace AetherKit.Game.Workshop;

/// <summary>產物受歡迎程度（數值對應 MJICraftworksPopularityType 列號）。</summary>
public enum Popularity : byte
{
    /// <summary>未知（係數 0）。</summary>
    Unknown = 0,
    /// <summary>人氣火爆。</summary>
    VeryHigh = 1,
    /// <summary>略受矚目。</summary>
    High = 2,
    /// <summary>普普通通。</summary>
    Average = 3,
    /// <summary>無人問津。</summary>
    Low = 4,
}

/// <summary>Popularity 的輔助函式。</summary>
public static class PopularityUtil
{
    /// <summary>係數（百分比）。</summary>
    public static byte Factor(this Popularity p) => p switch
    {
        Popularity.VeryHigh => 140,
        Popularity.High => 120,
        Popularity.Average => 100,
        Popularity.Low => 80,
        _ => 0,
    };

    /// <summary>位元組轉歡迎度；超出 0..4 視為未知。</summary>
    public static Popularity FromByte(byte val) => val <= 4 ? (Popularity)val : Popularity.Unknown;
}
