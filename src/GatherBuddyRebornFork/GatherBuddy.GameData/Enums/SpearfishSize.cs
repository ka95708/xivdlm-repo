using System;

namespace GatherBuddy.Enums;

public enum SpearfishSize : byte
{
    Unknown = 0,
    Small   = 1,
    Average = 2,
    Large   = 3,
    None    = 255,
}

public static class SpearFishSizeExtensions
{
    public static string ToName(this SpearfishSize size)
        => size switch
        {
            SpearfishSize.Unknown => "未知尺寸",
            SpearfishSize.Small   => "小型",
            SpearfishSize.Average => "中等",
            SpearfishSize.Large   => "大型",
            SpearfishSize.None    => "無尺寸資訊",
            _                     => throw new ArgumentOutOfRangeException(nameof(size), size, null),
        };
}