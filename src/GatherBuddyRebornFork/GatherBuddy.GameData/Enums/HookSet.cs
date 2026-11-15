using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace GatherBuddy.Enums;

[JsonConverter(typeof(StringEnumConverter))]
public enum HookSet : byte
{
    Unknown    = 0,
    Precise    = 1,
    Powerful   = 2,
    Hook       = 3,
    DoubleHook = 4,
    TripleHook = 5,
    Stellar    = 6,
    None       = 255,
}

public static class HookSetExtensions
{
    public static string ToName(this HookSet value)
        => value switch
        {
            HookSet.Unknown    => "未知",
            HookSet.Precise    => "精準提鉤",
            HookSet.Powerful   => "強力提鉤",
            HookSet.Hook       => "一般提鉤",
            HookSet.DoubleHook => "雙重提鉤",
            HookSet.TripleHook => "三重提鉤",
            HookSet.Stellar    => "星極提鉤",
            HookSet.None       => "None",
            _                  => "無效",
        };
}
