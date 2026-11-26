using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace AetherKit.Game;

/// <summary>
/// 用 Fate 表的地圖圖示分 FATE 類型。遊戲另外的 Rule 欄位分不出頭目和一般打怪，
/// 圖示才是玩家在地圖上看到的分類。帝國主題的 FATE（術士大隊、魔導兵器）有自己一組圖示。
/// </summary>
public static class FateKinds
{
    public static readonly IReadOnlyList<(FateKind Kind, string Label, string Hint)> All = new[]
    {
        (FateKind.Slay, "打倒敵人", "打一群小怪"),
        (FateKind.Boss, "討伐頭目", "打一隻大怪"),
        (FateKind.Defend, "防衛", "守住據點或 NPC，打來襲的怪"),
        (FateKind.Escort, "護送", "要跟著 NPC 移動，目前只會在中心附近等怪"),
        (FateKind.Collect, "收集繳交", "打怪收道具、夠了去交給 NPC（需要 TextAdvance）"),
        (FateKind.Special, "特殊大型", "法夫納、帕祖祖、幸福兔這類，強度高或玩法特殊"),
        (FateKind.Other, "其他", "季節活動、建設類等"),
    };

    public static FateKind Of(Fate fate) => fate.MapIcon switch
    {
        60501 or 60801 => FateKind.Slay,
        60502 or 60802 => FateKind.Boss,
        60503 or 60803 => FateKind.Collect,
        60504 or 60804 => FateKind.Defend,
        60505 => FateKind.Escort,
        60958 or 60506 => FateKind.Special,
        _ => FateKind.Other
    };

    public static string Label(FateKind kind)
    {
        foreach (var entry in All)
        {
            if (entry.Kind == kind)
                return entry.Label;
        }

        return kind.ToString();
    }
}
