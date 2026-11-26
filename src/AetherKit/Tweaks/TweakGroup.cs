namespace AetherKit.Tweaks;

/// <summary>小功能清單的分組：依遊戲裡的系統分，開著哪個系統的視窗就去哪一組找。順序就是清單上的順序。</summary>
public enum TweakGroup
{
    Items,
    Glamour,
    GoldSaucer,
    Gathering,
    Portrait,
    Duty,
    Interface
}

/// <summary>小功能怎麼運作，清單上會標出來，開了之後才知道去哪裡找它。</summary>
public enum TweakKind
{
    /// <summary>在遊戲視窗上方多一排按鈕，按了才做。</summary>
    Button,

    /// <summary>開著就自己做。</summary>
    Auto,

    /// <summary>另外開一個 AetherKit 視窗。</summary>
    Window
}

public static class TweakGroupNames
{
    public static string Name(this TweakGroup group) => group switch
    {
        TweakGroup.Items => "物品",
        TweakGroup.Glamour => "幻化",
        TweakGroup.GoldSaucer => "金碟與每週",
        TweakGroup.Gathering => "採集與製作",
        TweakGroup.Portrait => "肖像",
        TweakGroup.Duty => "副本與戰利品",
        _ => "介面與其他"
    };

    public static string Name(this TweakKind kind) => kind switch
    {
        TweakKind.Button => "按鈕",
        TweakKind.Auto => "自動",
        _ => "視窗"
    };
}
