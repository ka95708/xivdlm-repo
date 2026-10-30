using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using AetherKit.Modules;
using Newtonsoft.Json;

namespace AetherKit;

/// <summary>匯入讀到的內容：設定，與它包含哪幾部分（模組的 Id，或 ThemePart）。</summary>
public sealed record ImportedSettings(Configuration Config, IReadOnlyList<string> Parts);

/// <summary>
/// 設定的匯入匯出。匯出成一行文字（前綴 + 壓縮後的 JSON 轉 base64），
/// 方便貼在聊天、Discord 或記事本裡，不用處理檔案路徑。
///
/// 匯出的單位是「部分」：每個模組各一部分（由模組自己決定要帶哪些設定），再加上主題。
/// 字串裡記著它包含哪幾部分，匯入時只列出真的有的，使用者再勾要套用哪些。
///
/// 前綴帶格式版本，設定的資料結構版本另外記在 JSON 裡：
/// 結構不相容的舊字串直接拒絕，跟載入設定檔的規則一樣，不寫轉換。
/// </summary>
public static class ConfigTransfer
{
    public const string ThemePart = "theme";

    private const string Prefix = "AETHERKIT2:";

    /// <summary>還沒分部分以前的格式：整份設定。改名前（Barkeep）的也一樣，照樣接受。</summary>
    private static readonly string[] WholeConfigPrefixes = { "AETHERKIT1:", "BARKEEP1:" };

    /// <summary>字串裡實際的內容。</summary>
    private sealed class Package
    {
        public List<string> Parts { get; set; } = new();
        public Configuration Config { get; set; } = new();
    }

    /// <summary>只帶勾選的部分：從目前的設定抽出來，放進一份新的設定裡再匯出。</summary>
    public static string Export(Configuration config, IEnumerable<IModule> modules, ICollection<string> parts)
    {
        var package = new Package { Parts = parts.ToList() };

        foreach (var module in modules)
        {
            if (parts.Contains(module.Id))
                module.CopySettings(config, package.Config);
        }

        if (parts.Contains(ThemePart))
            package.Config.Theme = config.Theme;

        return Prefix + Compress(JsonConvert.SerializeObject(package, Formatting.None));
    }

    /// <summary>失敗時回傳 null，error 是給使用者看的原因。</summary>
    public static ImportedSettings? Import(string text, IEnumerable<IModule> modules, out string error)
    {
        error = string.Empty;
        text = text.Trim();

        var whole = WholeConfigPrefixes.FirstOrDefault(p => text.StartsWith(p, StringComparison.Ordinal));
        var prefix = text.StartsWith(Prefix, StringComparison.Ordinal) ? Prefix : whole;

        if (prefix == null)
        {
            error = "剪貼簿裡不是 AetherKit 的設定（應該以 AETHERKIT 開頭）。";
            return null;
        }

        Configuration? config;
        List<string> parts;

        try
        {
            var json = Decompress(text[prefix.Length..]);

            if (whole != null)
            {
                // 舊格式是整份設定，當成「每個模組 + 主題」
                config = JsonConvert.DeserializeObject<Configuration>(json);
                parts = modules.Select(m => m.Id).Append(ThemePart).ToList();
            }
            else
            {
                var package = JsonConvert.DeserializeObject<Package>(json);
                config = package?.Config;
                parts = package?.Parts ?? new List<string>();
            }
        }
        catch (Exception e)
        {
            Services.Log.Warning(e, "匯入設定失敗");
            error = "設定字串不完整或已損壞，請重新複製一次。";
            return null;
        }

        if (config == null)
        {
            error = "設定字串是空的。";
            return null;
        }

        if (config.Version != Configuration.CurrentVersion)
        {
            error = $"這是舊版 AetherKit 匯出的設定（版本 {config.Version}），目前版本是 {Configuration.CurrentVersion}，格式不相容。";
            return null;
        }

        ConfigStore.Normalize(config);
        return new ImportedSettings(config, parts);
    }

    private static string Compress(string json)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
            gzip.Write(Encoding.UTF8.GetBytes(json));

        return Convert.ToBase64String(output.ToArray());
    }

    private static string Decompress(string base64)
    {
        using var input = new GZipStream(new MemoryStream(Convert.FromBase64String(base64)), CompressionMode.Decompress);
        using var reader = new StreamReader(input, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
