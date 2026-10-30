using System;
using System.IO;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;

namespace AetherKit;

/// <summary>
/// 載入與存檔。存檔有節流：編輯時只標記 dirty，停止變更 1 秒後才真正寫檔，
/// 避免像 QoLBar 那樣每個按鍵都 I/O。
/// </summary>
public class ConfigStore : IDisposable
{
    private const double FlushDelaySeconds = 1.0;

    private DateTime lastChange = DateTime.MinValue;
    private bool dirty;

    public Configuration Config { get; }

    public ConfigStore()
    {
        var loaded = Services.PluginInterface.GetPluginConfig() as Configuration ?? LoadFromBarkeep();

        // 舊版結構不相容，直接重來，不寫轉換
        Config = loaded is { Version: Configuration.CurrentVersion } ? loaded : new Configuration();
        Normalize(Config);

        Services.Framework.Update += OnUpdate;
    }

    /// <summary>
    /// 插件改名前叫 Barkeep，設定檔是同一個資料夾裡的 Barkeep.json，資料格式跟現在一樣。
    /// AetherKit 第一次載入、自己還沒有設定檔時，從那裡搬過來，並且馬上存一份新的。
    /// 舊檔案不刪，萬一要退回 Barkeep 也還在。
    /// </summary>
    private Configuration? LoadFromBarkeep()
    {
        var folder = Services.PluginInterface.ConfigFile.DirectoryName;
        if (folder == null) return null;

        var legacy = Path.Combine(folder, "Barkeep.json");
        if (!File.Exists(legacy)) return null;

        try
        {
            // 舊檔裡的 $type 記的是 Barkeep 的組件名稱，不處理型別資訊就會略過它
            var config = JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(legacy));
            if (config == null) return null;

            Services.Log.Information($"已從 {legacy} 搬移設定");
            dirty = true;
            return config;
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, $"讀取舊設定 {legacy} 失敗，改用新設定");
            return null;
        }
    }

    /// <summary>
    /// 把資料補成程式可以直接用的樣子。不變條件集中在這裡處理，
    /// 這樣模型的 getter 就不需要偷偷修改資料。
    /// </summary>
    public static void Normalize(Configuration config)
    {
        var indicators = config.Cursor.Indicators;
        if (indicators.Count > CursorConfig.MaxIndicators)
            indicators.RemoveRange(CursorConfig.MaxIndicators, indicators.Count - CursorConfig.MaxIndicators);

        foreach (var item in config.TopBar.Items)
        {
            EnsureState(item);

            if (item.Kind == ItemKind.Panel)
                item.Panel ??= new PanelConfig();

            if (item.Panel == null) continue;

            item.Panel.Columns.RemoveAll(column => column.Count == 0);

            foreach (var section in item.Panel.Sections)
            {
                foreach (var panelItem in section.Items)
                    EnsureState(panelItem);
            }
        }
    }

    private static void EnsureState(StatefulItem item)
    {
        if (item.States.Count == 0)
            item.States.Add(new ActionItem());
    }

    /// <summary>標記設定已變更，稍後自動寫檔。</summary>
    public void Touch()
    {
        dirty = true;
        lastChange = DateTime.UtcNow;
    }

    /// <summary>立刻寫檔。</summary>
    public void Flush()
    {
        if (!dirty) return;

        dirty = false;
        Services.PluginInterface.SavePluginConfig(Config);
    }

    private void OnUpdate(IFramework framework)
    {
        if (!dirty) return;
        if ((DateTime.UtcNow - lastChange).TotalSeconds < FlushDelaySeconds) return;

        Flush();
    }

    public void Dispose()
    {
        Services.Framework.Update -= OnUpdate;
        Flush();
    }
}
