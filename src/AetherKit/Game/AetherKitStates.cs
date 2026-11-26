using System;
using System.Collections.Generic;
using System.Linq;
using AetherKit.Core;
using AetherKit.Modules;

namespace AetherKit.Game;

/// <summary>
/// AetherKit 自己的開關狀態，給工具列開關型按鈕的「狀態來源」、條件組讀。
/// 每一項有固定的代號（存進設定檔）、顯示名稱、讀法，以及打開／關掉的指令（選了就幫忙填進按鈕）。
/// 代號：fate／vault／leve／island／retainers（自動流程正在跑）、module:Id（模組啟用）、
/// tweak:Id（小功能開著）、core:Id（核心能力一直開著）、edit（工具列編輯模式）。
/// </summary>
public static class AetherKitStates
{
    /// <summary>一個開關。OnCommand／OffCommand 空白 = 沒有對應指令（只能讀）。</summary>
    public readonly record struct State(string Key, string Group, string Name, Func<bool> Read, string OnCommand, string OffCommand);

    /// <summary>自動流程：模組 Id → 切換指令（同一個指令開始、停止）。</summary>
    private static readonly (string ModuleId, string Key, string Command)[] Runners =
    {
        ("auto-fate", "fate", "/aetherkit fate"),
        (BlueMageVaultFarm.Owner, "vault", "/aetherkit vault"),
        (TsaiTouVounouLeve.Owner, "leve", "/aetherkit leve"),
        (IslandGather.Owner, "island", "/aetherkit island"),
        (RetainerWork.Owner, "retainers", ""),
    };

    public static IEnumerable<State> All()
    {
        var plugin = Plugin.Instance;

        foreach (var (moduleId, key, command) in Runners)
        {
            if (plugin.Modules.FirstOrDefault(m => m.Id == moduleId) is not { } module) continue;
            yield return new State(key, "自動流程", $"{module.Name} 執行中", () => module.Running, command, command);
        }

        foreach (var module in plugin.Modules.Where(m => m.CanDisable))
        {
            var command = $"/aetherkit module {module.Id}";
            yield return new State($"module:{module.Id}", "模組", $"{module.Name} 啟用", () => plugin.IsEnabled(module),
                command + " on", command + " off");
        }

        foreach (var tweak in plugin.Tweaks.All)
        {
            var command = $"/aetherkit tweak {tweak.Id}";
            yield return new State($"tweak:{tweak.Id}", "小功能", tweak.Name, () => tweak.Enabled, command + " on", command + " off");
        }

        foreach (var capability in plugin.Core.All)
        {
            var command = $"/aetherkit core {capability.Id}";
            yield return new State($"core:{capability.Id}", "核心能力", $"{capability.Name} 一直開著",
                () => capability.Mode == CapabilityMode.Always, command + " on", command + " off");
        }

        yield return new State("edit", "工具列", "編輯模式", () => plugin.Toolbar.EditMode, "/aetherkit edit", "/aetherkit edit");
    }

    /// <summary>模組、小功能、核心能力載入後就不會變，第一次用時建好查表（工具列每幀都會問）。</summary>
    private static Dictionary<string, State>? byKey;

    public static State? Find(string key)
    {
        byKey ??= All().ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        return byKey.TryGetValue(key.Trim(), out var state) ? state : null;
    }

    /// <summary>讀一個開關；代號不存在（例如小功能改名、移除）回 false = 讀不到。</summary>
    public static bool TryRead(string key, out bool value)
    {
        value = false;
        if (Find(key) is not { } state) return false;

        value = state.Read();
        return true;
    }
}
