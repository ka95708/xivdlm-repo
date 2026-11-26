using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ImGuiNotification;

namespace AetherKit.Game;

/// <summary>一則插件訊息。Source 是訊息開頭 [ ] 裡的插件名稱，沒有標示就是 Unknown。</summary>
public readonly record struct PluginLogEntry(DateTime Time, string Source, string Text, bool IsError);

/// <summary>
/// 把插件印到聊天欄的訊息攔下來，收進記憶體裡的紀錄，聊天欄就不顯示。
///
/// 怎麼分辨是插件印的：插件呼叫 IChatGui.Print 時，Dalamud 先排進佇列，
/// 下一幀在 ChatGui.UpdateQueue 裡一則一則送進遊戲的聊天處理，聊天事件就是在那時觸發的。
/// 遊戲自己的訊息不經過那裡，所以看呼叫堆疊裡有沒有 UpdateQueue 就分得出來，
/// 不用猜訊息頻道或格式。聊天訊息不多，查一次堆疊的成本可以接受。
///
/// 錯誤 = Dalamud 的 PrintError，固定用 Urgent 頻道。
/// </summary>
public sealed class PluginLog : IDisposable
{
    public const string Unknown = "（未標示）";

    /// <summary>最多留幾則，超過就丟掉最舊的。只在記憶體裡，不存檔。</summary>
    private const int Capacity = 500;

    private readonly Plugin plugin;
    private readonly List<PluginLogEntry> entries = new();
    private readonly SortedSet<string> sources = new(StringComparer.OrdinalIgnoreCase);
    private bool listening;

    public PluginLog(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private PluginLogConfig Config => plugin.Config.PluginLog;

    public IReadOnlyList<PluginLogEntry> Entries => entries;

    /// <summary>看過的來源，給設定頁和紀錄視窗的篩選用。</summary>
    public IReadOnlyCollection<string> Sources => sources;

    /// <summary>紀錄視窗沒開著的時候進來的錯誤數，打開視窗就歸零。</summary>
    public int UnreadErrors { get; set; }

    /// <summary>每加一則就加一，紀錄視窗靠它知道有新訊息、要不要捲到最下面。</summary>
    public int Version { get; private set; }

    public void Start()
    {
        if (listening) return;
        Services.Chat.ChatMessage += OnChatMessage;
        listening = true;
    }

    public void Stop()
    {
        if (!listening) return;
        Services.Chat.ChatMessage -= OnChatMessage;
        listening = false;
    }

    public void Clear()
    {
        entries.Clear();
        UnreadErrors = 0;
        Version++;
    }

    private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (isHandled || !FromPluginQueue()) return;

        // AetherKit 自己印到聊天欄的只有指令回應，已經由 Notify 記過，照樣顯示
        if (message.TextValue.TrimStart().StartsWith($"[{Core.Notify.Source}]")) return;

        try
        {
            var (source, text) = Split(message.TextValue);
            var error = type == XivChatType.Urgent;

            Add(new PluginLogEntry(DateTime.Now, source, text, error));

            if (error)
                OnError(source, text);

            if (!Config.ShowInChat.Contains(source))
                isHandled = true;
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "插件訊息：處理聊天訊息失敗");
        }
    }

    private static bool FromPluginQueue()
    {
        var frames = new StackTrace(false).GetFrames();

        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            if (method?.Name == "UpdateQueue" && method.DeclaringType?.FullName == "Dalamud.Game.Gui.ChatGui")
                return true;
        }

        return false;
    }

    /// <summary>直接記一則（AetherKit 自己的訊息由 Notify 記，不從聊天欄攔）。通知由 Notify 自己跳，這裡不再跳。</summary>
    public void Record(string source, string text, bool error)
    {
        Add(new PluginLogEntry(DateTime.Now, source, text, error));
        if (error && !plugin.PluginLog.WindowOpen)
            UnreadErrors++;
    }

    private void Add(PluginLogEntry entry)
    {
        if (entries.Count >= Capacity)
            entries.RemoveAt(0);

        entries.Add(entry);
        sources.Add(entry.Source);
        Version++;
    }

    private void OnError(string source, string text)
    {
        if (!plugin.PluginLog.WindowOpen)
            UnreadErrors++;

        if (!Config.NotifyErrors) return;

        Services.Notifications.AddNotification(new Notification
        {
            Title = $"{source} 錯誤",
            Content = text,
            Type = NotificationType.Error,
            Minimized = false,
        });
    }

    /// <summary>「[TriadBuddy] 找不到 NPC」→（"TriadBuddy", "找不到 NPC"）。</summary>
    private static (string Source, string Text) Split(string message)
    {
        var text = message.TrimStart();

        foreach (var (open, close) in new[] { ('[', ']'), ('【', '】') })
        {
            if (text.Length == 0 || text[0] != open) continue;

            var end = text.IndexOf(close);
            if (end <= 1 || end > 40) continue;

            return (text[1..end].Trim(), text[(end + 1)..].Trim());
        }

        return (Unknown, text);
    }

    public void Dispose() => Stop();
}
