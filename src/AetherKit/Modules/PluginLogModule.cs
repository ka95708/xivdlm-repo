using System.Collections.Generic;
using System.Linq;
using AetherKit.Game;
using AetherKit.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AetherKit.Modules;

/// <summary>
/// 插件訊息模組：插件印到聊天欄的訊息全部收起來，改到訊息紀錄視窗看；
/// 插件印錯誤時跳通知。遊戲自己的訊息不受影響。
/// </summary>
public sealed class PluginLogModule : IModule
{
    private readonly Plugin plugin;
    private readonly PluginLog log;
    private readonly PluginLogWindow window;

    public PluginLogModule(Plugin plugin)
    {
        this.plugin = plugin;
        log = new PluginLog(plugin);
        window = new PluginLogWindow(log);
    }

    private PluginLogConfig Config => plugin.Config.PluginLog;

    public bool WindowOpen => window.IsOpen;

    public string Id => "plugin-log";
    public string Name => "插件訊息";
    public string Description => "插件印到聊天欄的訊息不再顯示，改收進訊息紀錄視窗；插件出錯時跳通知。遊戲自己的訊息不受影響。";

    public void Enable(WindowSystem windows)
    {
        windows.AddWindow(window);
        log.Start();
    }

    public void Disable(WindowSystem windows)
    {
        log.Stop();
        window.IsOpen = false;
        windows.RemoveWindow(window);
    }

    public void OpenWindow() => window.IsOpen = true;

    /// <summary>AetherKit 自己的訊息直接記進紀錄（不經過聊天欄攔截）。</summary>
    public void Record(string source, string text, bool error) => log.Record(source, text, error);

    public void DrawHeader()
    {
        var label = log.UnreadErrors > 0 ? $"開啟訊息紀錄（{log.UnreadErrors} 個新錯誤）" : "開啟訊息紀錄";
        if (ModuleUi.Action(label))
            OpenWindow();
        ModuleUi.Command("/aetherkit log");
    }

    public IEnumerable<ModuleTab> SettingsTabs
    {
        get { yield return new ModuleTab("設定", DrawSettings); }
    }

    private void DrawSettings()
    {
        Paint.SettingsHeading("錯誤");

        var notify = Config.NotifyErrors;
        if (ImGui.Checkbox("插件印錯誤訊息時跳通知", ref notify))
        {
            Config.NotifyErrors = notify;
            plugin.Save();
        }

        Paint.SettingsHeading("照樣顯示在聊天欄");
        ImGui.TextDisabled("勾選的插件，訊息還是會出現在聊天欄（紀錄視窗裡也有）。清單是這次開遊戲後印過訊息的插件。");

        var names = log.Sources.Union(Config.ShowInChat).Distinct().OrderBy(n => n).ToList();
        if (names.Count == 0)
        {
            ImGui.TextDisabled("還沒有插件印過訊息。");
            return;
        }

        foreach (var name in names)
        {
            var show = Config.ShowInChat.Contains(name);
            if (!ImGui.Checkbox($"{name}##show", ref show)) continue;

            if (show) Config.ShowInChat.Add(name);
            else Config.ShowInChat.Remove(name);

            plugin.Save();
        }
    }

    public bool HasSettings(Configuration config) => true;

    public void CopySettings(Configuration from, Configuration to) =>
        to.PluginLog = new PluginLogConfig
        {
            NotifyErrors = from.PluginLog.NotifyErrors,
            ShowInChat = new List<string>(from.PluginLog.ShowInChat),
        };

    public void Dispose()
    {
        log.Dispose();
    }
}
