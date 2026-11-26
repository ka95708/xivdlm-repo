using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AetherKit.Core;

/// <summary>
/// 各功能共用的持久 log：寫在設定資料夾的 logs 底下，每個功能一個檔、每天一個檔（例如 autofate-20261124.log），留 7 天。
/// 第一次寫才建檔，沒用到的功能不會有檔案。出問題時把那個功能當天的檔案拿出來看就好，不會跟別的功能混在一起。
///
/// 只記「發生了什麼」（開始停止、做了哪一步、為什麼停），不要每幀寫。
/// 整個插件共用一個開關（核心能力頁的 Log 設定），寫入失敗不會影響功能本身。
/// </summary>
public sealed class FeatureLog
{
    private const int KeepDays = 7;

    private static readonly Dictionary<string, FeatureLog> logs = new();
    private static readonly object registry = new();
    private static DateTime lastPrune = DateTime.MinValue;

    private readonly object gate = new();
    private readonly string name;
    private StreamWriter? writer;
    private string currentFile = string.Empty;

    private FeatureLog(string name) => this.name = name;

    /// <summary>log 資料夾。</summary>
    public static string Directory => Path.Combine(Services.PluginInterface.ConfigDirectory.FullName, "logs");

    /// <summary>整個插件寫不寫 log。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>某個功能的 log；同一個名稱拿到同一個。名稱就是檔名開頭，用英文小寫。</summary>
    public static FeatureLog For(string name)
    {
        lock (registry)
        {
            if (!logs.TryGetValue(name, out var log))
                logs[name] = log = new FeatureLog(name);
            return log;
        }
    }

    /// <summary>插件卸載時關掉所有檔案。</summary>
    public static void CloseAll()
    {
        lock (registry)
        {
            foreach (var log in logs.Values)
                log.Close();
        }
    }

    public static void OpenFolder()
    {
        System.IO.Directory.CreateDirectory(Directory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Directory) { UseShellExecute = true });
    }

    public void Write(string message)
    {
        if (!Enabled) return;

        try
        {
            lock (gate)
            {
                var now = DateTime.Now;
                var file = Path.Combine(Directory, $"{name}-{now:yyyyMMdd}.log");
                if (file != currentFile)
                    Open(file);

                writer!.WriteLine($"{now:HH:mm:ss} {message}");
            }
        }
        catch (Exception e)
        {
            // 寫 log 失敗不能讓功能跟著停
            Services.Log.Warning($"{name} log write failed: {e.Message}");
        }
    }

    private void Open(string file)
    {
        writer?.Dispose();

        System.IO.Directory.CreateDirectory(Directory);
        writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
        {
            AutoFlush = true
        };
        currentFile = file;

        Prune();
    }

    /// <summary>刪掉太舊的檔案（所有功能一起，一天最多做一次）。</summary>
    private static void Prune()
    {
        if (DateTime.Now - lastPrune < TimeSpan.FromHours(1)) return;
        lastPrune = DateTime.Now;

        var cutoff = DateTime.Now.AddDays(-KeepDays);
        foreach (var old in new DirectoryInfo(Directory).GetFiles("*.log").Where(f => f.LastWriteTime < cutoff))
        {
            try
            {
                old.Delete();
            }
            catch
            {
                // 刪不掉就留著，下次再試
            }
        }
    }

    private void Close()
    {
        lock (gate)
        {
            writer?.Dispose();
            writer = null;
            currentFile = string.Empty;
        }
    }
}
