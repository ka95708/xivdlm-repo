using System;

namespace GatherBuddy.Enums;

public enum SpearfishSpeed : ushort
{
    Unknown       = 0,
    SuperSlow     = 100,
    ExtremelySlow = 150,
    VerySlow      = 200,
    Slow          = 250,
    Average       = 300,
    Fast          = 350,
    VeryFast      = 400,
    ExtremelyFast = 450,
    SuperFast     = 500,
    HyperFast     = 550,
    LynFast       = 600,

    None = ushort.MaxValue,
}

public static class SpearFishSpeedExtensions
{
    public static string ToName(this SpearfishSpeed speed)
        => speed switch
        {
            SpearfishSpeed.Unknown       => "未知速度",
            SpearfishSpeed.SuperSlow     => "極慢",
            SpearfishSpeed.ExtremelySlow => "非常慢",
            SpearfishSpeed.VerySlow      => "很慢",
            SpearfishSpeed.Slow          => "慢",
            SpearfishSpeed.Average       => "中等",
            SpearfishSpeed.Fast          => "快",
            SpearfishSpeed.VeryFast      => "很快",
            SpearfishSpeed.ExtremelyFast => "非常快",
            SpearfishSpeed.SuperFast     => "極快",
            SpearfishSpeed.HyperFast     => "超快",
            SpearfishSpeed.LynFast       => "超高速",
            SpearfishSpeed.None          => "無速度資訊",
            _                            => $"{(ushort)speed}",
        };
}