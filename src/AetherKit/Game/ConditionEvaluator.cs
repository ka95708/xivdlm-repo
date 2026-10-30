using System;
using Dalamud.Game.ClientState.Conditions;

namespace AetherKit.Game;

/// <summary>
/// 判斷一條快捷列現在該不該出現。
///
/// 每種情境在遊戲裡常常對應好幾個旗標（例如騎乘有 Mounted 和 Mounted2），
/// 所以這裡一律收斂成一個語意，使用者只要選「騎乘中」就好。
/// </summary>
public static class ConditionEvaluator
{
    // 職業 ID 的分段：8–15 是生產職，16–18 是採集職，其餘有職業的都算戰鬥職
    private const uint CrafterFirst = 8;
    private const uint CrafterLast = 15;
    private const uint GathererFirst = 16;
    private const uint GathererLast = 18;

    public static bool IsVisible(Configuration config, Guid conditionSetId)
    {
        var set = config.FindConditionSet(conditionSetId);

        // 沒指定條件組、或組是空的，就永遠顯示
        if (set == null || set.Entries.Count == 0) return true;

        foreach (var entry in set.Entries)
        {
            var met = Test(entry) != entry.Negate;

            if (set.RequireAll && !met) return false;
            if (!set.RequireAll && met) return true;
        }

        return set.RequireAll;
    }

    public static bool Test(ConditionEntry entry) => entry.Kind switch
    {
        ConditionKind.Job => CurrentJob() == entry.Arg,
        ConditionKind.CombatJob => IsCombatJob(CurrentJob()),
        ConditionKind.CrafterJob => CurrentJob() is >= CrafterFirst and <= CrafterLast,
        ConditionKind.GathererJob => CurrentJob() is >= GathererFirst and <= GathererLast,
        ConditionKind.Territory => Services.ClientState.TerritoryType == entry.Arg,

        ConditionKind.InCombat => Services.Condition[ConditionFlag.InCombat],

        ConditionKind.InDuty => Services.Condition[ConditionFlag.BoundByDuty]
                                || Services.Condition[ConditionFlag.BoundByDuty56]
                                || Services.Condition[ConditionFlag.BoundByDuty95],

        // RidingPillion 是坐別人的雙人坐騎，語意上也算騎乘中
        ConditionKind.Mounted => Services.Condition[ConditionFlag.Mounted]
                                 || Services.Condition[ConditionFlag.RidingPillion],

        ConditionKind.Crafting => Services.Condition[ConditionFlag.Crafting]
                                  || Services.Condition[ConditionFlag.PreparingToCraft]
                                  || Services.Condition[ConditionFlag.ExecutingCraftingAction],

        ConditionKind.Gathering => Services.Condition[ConditionFlag.Gathering]
                                   || Services.Condition[ConditionFlag.ExecutingGatheringAction]
                                   || Services.Condition[ConditionFlag.Fishing],

        ConditionKind.InCutscene => Services.Condition[ConditionFlag.OccupiedInCutSceneEvent]
                                    || Services.Condition[ConditionFlag.WatchingCutscene]
                                    || Services.Condition[ConditionFlag.WatchingCutscene78],

        ConditionKind.InPvP => Services.ClientState.IsPvP,

        _ => false
    };

    /// <summary>IClientState.LocalPlayer 已棄用，職業改從 IPlayerState 拿。</summary>
    public static uint CurrentJob() =>
        Services.PlayerState.IsLoaded ? Services.PlayerState.ClassJob.RowId : 0;

    private static bool IsCombatJob(uint job) =>
        job != 0 && job is < CrafterFirst or > GathererLast;

    public static string Describe(ConditionKind kind) => kind switch
    {
        ConditionKind.Job => "指定職業",
        ConditionKind.CombatJob => "戰鬥職",
        ConditionKind.CrafterJob => "生產職",
        ConditionKind.GathererJob => "採集職",
        ConditionKind.Territory => "指定地圖",
        ConditionKind.InCombat => "戰鬥中",
        ConditionKind.InDuty => "任務中",
        ConditionKind.Mounted => "騎乘中",
        ConditionKind.Crafting => "製作中",
        ConditionKind.Gathering => "採集中",
        ConditionKind.InCutscene => "過場動畫",
        ConditionKind.InPvP => "PvP",
        _ => kind.ToString()
    };
}
