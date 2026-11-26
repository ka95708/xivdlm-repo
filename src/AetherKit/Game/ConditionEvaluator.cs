using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;

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
            // 讀不到（例如插件沒載入、查詢不存在）當作不成立
            var met = (TryTest(entry, out var value) && value) != entry.Negate;

            if (set.RequireAll && !met) return false;
            if (!set.RequireAll && met) return true;
        }

        return set.RequireAll;
    }

    /// <summary>讀不到時當作不成立。</summary>
    public static bool Test(ConditionEntry entry) => TryTest(entry, out var value) && value;

    /// <summary>
    /// 這一條現在成不成立（還沒套用反轉）。回傳 false = 讀不到：插件提供的查詢不存在或出錯（通常是插件沒載入）。
    /// </summary>
    public static bool TryTest(ConditionEntry entry, out bool value)
    {
        if (entry.Kind == ConditionKind.PluginQuery)
            return TryQuery(entry.Text, out value);

        if (entry.Kind == ConditionKind.AetherKit)
            return AetherKitStates.TryRead(entry.Text, out value);

        value = TestGame(entry);
        return true;
    }

    private static bool TestGame(ConditionEntry entry) => entry.Kind switch
    {
        ConditionKind.PluginLoaded => IsPluginLoaded(entry.Text),

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

    // ── 插件 ──────────────────────────────────────────────

    /// <summary>已載入插件的內部名稱。每幀都會問，所以半秒才跟 Dalamud 重拿一次清單。</summary>
    private static HashSet<string> loadedPlugins = new(StringComparer.OrdinalIgnoreCase);

    private static DateTime loadedPluginsAt = DateTime.MinValue;

    public static bool IsPluginLoaded(string internalName)
    {
        if (string.IsNullOrWhiteSpace(internalName)) return false;

        var now = DateTime.Now;
        if (now - loadedPluginsAt > TimeSpan.FromSeconds(0.5))
        {
            loadedPluginsAt = now;
            loadedPlugins = Services.PluginInterface.InstalledPlugins
                .Where(p => p.IsLoaded)
                .Select(p => p.InternalName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return loadedPlugins.Contains(internalName.Trim());
    }

    private static readonly Dictionary<string, ICallGateSubscriber<bool>> queries = new();

    /// <summary>
    /// 呼叫插件提供的查詢（不帶參數、回答是或否的 IPC），例如 WrathCombo.GetAutoRotationState。
    /// 插件沒載入、名稱打錯、回答的不是是或否，都算讀不到。
    /// </summary>
    public static bool TryQuery(string name, out bool value)
    {
        value = false;
        name = name.Trim();
        if (name.Length == 0) return false;

        try
        {
            if (!queries.TryGetValue(name, out var subscriber))
            {
                subscriber = Services.PluginInterface.GetIpcSubscriber<bool>(name);
                queries[name] = subscriber;
            }

            value = subscriber.InvokeFunc();
            return true;
        }
        catch
        {
            return false;
        }
    }

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
        ConditionKind.PluginLoaded => "插件已載入",
        ConditionKind.PluginQuery => "插件查詢",
        ConditionKind.AetherKit => "AetherKit 開關",
        _ => kind.ToString()
    };
}
