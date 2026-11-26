using System;
using Dalamud.Interface.ImGuiNotification;

namespace AetherKit.Core;

/// <summary>
/// AetherKit 給使用者看的訊息，全部從這裡出去，不要直接 Services.Chat.Print。
///
/// 每一則都會記進「插件訊息」的紀錄（模組有開的話，來源 AetherKit），再照用途決定怎麼顯示：
/// <list type="bullet">
/// <item><see cref="Reply"/>：回應使用者打的指令 → 聊天欄（打指令的人本來就看著聊天欄）。</item>
/// <item><see cref="Info"/>：流程結束、功能本身的輸出 → 一般通知，幾秒後自己消失。</item>
/// <item><see cref="Error"/>：按了開始但條件不符、流程出錯停止 → 錯誤通知。</item>
/// <item><see cref="Attention"/>：執行中要使用者自己處理才能繼續 → 錯誤通知，不會自己消失。</item>
/// </list>
/// 只給除錯看的過程，寫 FeatureLog，不要用這裡。
/// </summary>
public static class Notify
{
    public const string Source = "AetherKit";

    /// <summary>回應指令：印在聊天欄。</summary>
    public static void Reply(string text, bool error = false)
    {
        Record(text, error);

        var line = $"[{Source}] {text}";
        if (error) Services.Chat.PrintError(line);
        else Services.Chat.Print(line);
    }

    /// <summary>一般通知（流程完成、功能輸出），幾秒後消失。</summary>
    public static void Info(string feature, string text) =>
        Show(feature, text, NotificationType.Info, false);

    /// <summary>錯誤通知（不能開始、出錯停止）。</summary>
    public static void Error(string feature, string text) =>
        Show(feature, text, NotificationType.Error, false);

    /// <summary>要使用者處理才能繼續：錯誤通知，不會自己消失。</summary>
    public static void Attention(string feature, string text) =>
        Show(feature, text, NotificationType.Warning, true);

    private static void Show(string feature, string text, NotificationType type, bool sticky)
    {
        Record($"{feature}：{text}", type is NotificationType.Error or NotificationType.Warning);

        var notification = new Notification
        {
            Title = $"{Source}：{feature}",
            Content = text,
            Type = type,
            Minimized = false,
        };

        if (sticky)
            notification.InitialDuration = TimeSpan.MaxValue; // 使用者關掉才消失

        Services.Notifications.AddNotification(notification);
    }

    private static void Record(string text, bool error)
    {
        var plugin = Plugin.Instance;
        if (plugin != null && plugin.IsEnabled(plugin.PluginLog))
            plugin.PluginLog.Record(Source, text, error);
    }
}
