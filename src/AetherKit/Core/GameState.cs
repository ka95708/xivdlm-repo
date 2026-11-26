using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;

namespace AetherKit.Core;

/// <summary>核心能力共用的遊戲狀態判斷。條件旗標用數字，Dalamud 各版本的列舉名稱不太一樣。</summary>
public static class GameState
{
    // 和 NPC 互動、任務事件、過場、傳喚鈴、搬運物品等「事件中」的狀態
    private static readonly int[] EventFlags = { 30, 31, 32, 33, 35, 38, 39, 50, 58, 65, 71, 78, 9 };

    private static bool Flag(int flag) => Services.Condition[(ConditionFlag)flag];

    /// <summary>正在和 NPC 或任務事件互動。對話推進只在這時候點，其他時候出現的對話框不碰。</summary>
    public static bool InEvent
    {
        get
        {
            foreach (var flag in EventFlags)
            {
                if (Flag(flag)) return true;
            }

            return false;
        }
    }

    /// <summary>目前開著的「事件中」旗標。開著收藏櫃、雇員這類視窗時本來就有幾個，要比較前後變化時用。</summary>
    public static HashSet<int> ActiveEventFlags()
    {
        var result = new HashSet<int>();
        foreach (var flag in EventFlags)
        {
            if (Flag(flag)) result.Add(flag);
        }

        return result;
    }

    /// <summary>有沒有 baseline 以外的「事件中」旗標（例如按了確認之後遊戲還在處理）。</summary>
    public static bool InEventBeyond(HashSet<int> baseline)
    {
        foreach (var flag in EventFlags)
        {
            if (Flag(flag) && !baseline.Contains(flag)) return true;
        }

        return false;
    }

    public static bool InCutscene => Flag(35) || Flag(78);

    public static bool InCombat => Services.Condition[ConditionFlag.InCombat];

    public static bool Mounted => Services.Condition[ConditionFlag.Mounted];

    public static bool BetweenAreas => Services.Condition[ConditionFlag.BetweenAreas] || Services.Condition[ConditionFlag.BetweenAreas51];

    /// <summary>可以安心做「開視窗、按按鈕」這類事的時候：沒在戰鬥、坐騎、詠唱、過圖、事件中。</summary>
    public static bool Idle
    {
        get
        {
            var player = Services.Objects.LocalPlayer;
            return player != null && !player.IsCasting && !player.IsDead
                   && !InCombat && !Mounted && !BetweenAreas && !InEvent;
        }
    }
}
