using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json;

namespace Barkeep;

/// <summary>
/// 設定的匯入匯出。匯出成一行文字（前綴 + 壓縮後的 JSON 轉 base64），
/// 方便貼在聊天、Discord 或記事本裡，不用處理檔案路徑。
///
/// 前綴帶格式版本，設定的資料結構版本另外記在 JSON 裡：
/// 結構不相容的舊字串直接拒絕，跟載入設定檔的規則一樣，不寫轉換。
/// </summary>
public static class ConfigTransfer
{
    private const string Prefix = "BARKEEP1:";

    public static string Export(Configuration config)
    {
        var json = JsonConvert.SerializeObject(config, Formatting.None);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
            gzip.Write(Encoding.UTF8.GetBytes(json));

        return Prefix + Convert.ToBase64String(output.ToArray());
    }

    /// <summary>失敗時回傳 null，error 是給使用者看的原因。</summary>
    public static Configuration? Import(string text, out string error)
    {
        error = string.Empty;
        text = text.Trim();

        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            error = "剪貼簿裡不是 Barkeep 的設定（應該以 BARKEEP1: 開頭）。";
            return null;
        }

        Configuration? config;

        try
        {
            var bytes = Convert.FromBase64String(text[Prefix.Length..]);

            using var input = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var reader = new StreamReader(input, Encoding.UTF8);

            config = JsonConvert.DeserializeObject<Configuration>(reader.ReadToEnd());
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
            error = $"這是舊版 Barkeep 匯出的設定（版本 {config.Version}），目前版本是 {Configuration.CurrentVersion}，格式不相容。";
            return null;
        }

        ConfigStore.Normalize(config);
        return config;
    }
}
