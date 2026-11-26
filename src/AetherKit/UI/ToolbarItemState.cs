using System;
using AetherKit.Game;

namespace AetherKit.UI;

/// <summary>
/// 工具列項目「現在」的狀態：開關開著沒、能不能按、為什麼不能按、畫面上該長怎樣。
/// 畫工具列、按下去、快捷鍵都問這裡，判斷才會一致。
/// </summary>
public static class ToolbarItemState
{
    /// <summary>開關現在開著嗎。null = 讀不到（狀態來源是插件查詢，但插件沒載入或名稱不對）。</summary>
    public static bool? IsOn(StatefulItem item)
    {
        if (item.StateSource is not { } source) return item.IsOn;
        if (!ConditionEvaluator.TryTest(source, out var value)) return null;

        return value != source.Negate;
    }

    /// <summary>不能按的原因；可以按就是 null。</summary>
    public static string? BlockedReason(Configuration config, StatefulItem item)
    {
        if (item is TopBarItem { Kind: ItemKind.Panel }) return null;

        if (item.EnableConditionSetId != Guid.Empty
            && config.FindConditionSet(item.EnableConditionSetId) is { } set
            && !ConditionEvaluator.IsVisible(config, set.Id))
            return $"「{set.Name}」不成立";

        if (item.IsToggle && item.StateSource is { } source && IsOn(item) == null)
            return $"讀不到狀態：{Describe(source)}（插件可能沒載入）";

        return null;
    }

    /// <summary>設了「不能按時藏起來」而且現在不能按。編輯模式下不藏，才找得到。</summary>
    public static bool IsHidden(Configuration config, StatefulItem item, bool editMode) =>
        !editMode && item.HideWhenDisabled && BlockedReason(config, item) != null;

    /// <summary>畫在工具列、面板上的樣子：開關關著時改灰階。</summary>
    public static ActionItem Face(StatefulItem item)
    {
        if (!item.IsToggle) return item.Current;

        var face = item.Current.Clone();
        face.Grayscale = IsOn(item) != true;
        return face;
    }

    public static string Describe(ConditionEntry entry)
    {
        var name = ConditionEvaluator.Describe(entry.Kind);
        var text = entry.Kind switch
        {
            ConditionKind.AetherKit => AetherKitStates.Find(entry.Text) is { } state ? $"{name} {state.Name}" : $"{name} {entry.Text}",
            ConditionKind.PluginLoaded or ConditionKind.PluginQuery when entry.Text.Length > 0 => $"{name} {entry.Text}",
            _ => name,
        };

        return entry.Negate ? $"不是「{text}」" : text;
    }
}
