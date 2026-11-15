using System;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Lumina.Text.ReadOnly;

namespace GatherBuddy.Utility;

public readonly struct MultiString(string en, string de, string fr, string jp, string zhCn, string zhTw)
{
    public static string ParseSeStringLumina(ReadOnlySeString? luminaString)
        => luminaString?.ExtractText() ?? string.Empty;

    public readonly string English  = en;
    public readonly string German   = de;
    public readonly string French   = fr;
    public readonly string Japanese = jp;
    public readonly string ChineseSimplified  = zhCn;
    public readonly string ChineseTraditional = zhTw;

    public string this[ClientLanguage lang]
        => Name(lang);

    public override string ToString()
        => Name((ClientLanguage)4);

    public string ToWholeString()
        => $"{English}|{German}|{French}|{Japanese}|{ChineseSimplified}";

    public static MultiString FromPlaceName(IDataManager gameData, uint id)
    {
        var en = ParseSeStringLumina(gameData.GetExcelSheet<PlaceName>(ClientLanguage.English).GetRowOrDefault(id)?.Name);
        var de = ParseSeStringLumina(gameData.GetExcelSheet<PlaceName>(ClientLanguage.German).GetRowOrDefault(id)?.Name);
        var fr = ParseSeStringLumina(gameData.GetExcelSheet<PlaceName>(ClientLanguage.French).GetRowOrDefault(id)?.Name);
        var jp = ParseSeStringLumina(gameData.GetExcelSheet<PlaceName>(ClientLanguage.Japanese).GetRowOrDefault(id)?.Name);
        var zhCn = TryGetPlaceName(gameData, id, ClientLanguage.ChineseSimplified);
        var zhTw = TryGetPlaceName(gameData, id, ClientLanguage.ChineseTraditional);
        return new MultiString(en, de, fr, jp, zhCn, zhTw);
    }

    public static MultiString FromItem(IDataManager gameData, uint id)
    {
        var en = ParseSeStringLumina(gameData.GetExcelSheet<Item>(ClientLanguage.English).GetRowOrDefault(id)?.Name);
        var de = ParseSeStringLumina(gameData.GetExcelSheet<Item>(ClientLanguage.German).GetRowOrDefault(id)?.Name);
        var fr = ParseSeStringLumina(gameData.GetExcelSheet<Item>(ClientLanguage.French).GetRowOrDefault(id)?.Name);
        var jp = ParseSeStringLumina(gameData.GetExcelSheet<Item>(ClientLanguage.Japanese).GetRowOrDefault(id)?.Name);
        var zhCn = TryGetItemName(gameData, id, ClientLanguage.ChineseSimplified);
        var zhTw = TryGetItemName(gameData, id, ClientLanguage.ChineseTraditional);
        return new MultiString(en, de, fr, jp, zhCn, zhTw);
    }

    private static string TryGetPlaceName(IDataManager gameData, uint id, ClientLanguage language)
    {
        try
        {
            return ParseSeStringLumina(gameData.GetExcelSheet<PlaceName>(language).GetRowOrDefault(id)?.Name);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TryGetItemName(IDataManager gameData, uint id, ClientLanguage language)
    {
        try
        {
            return ParseSeStringLumina(gameData.GetExcelSheet<Item>(language).GetRowOrDefault(id)?.Name);
        }
        catch
        {
            return string.Empty;
        }
    }

    private string Name(ClientLanguage lang)
        => lang switch
        {
            ClientLanguage.English  => English,
            ClientLanguage.German   => German,
            ClientLanguage.Japanese => Japanese,
            ClientLanguage.French   => French,
            ClientLanguage.ChineseSimplified  => ChineseSimplified.Length > 0 ? ChineseSimplified : English,
            ClientLanguage.ChineseTraditional => ChineseTraditional.Length > 0 ? ChineseTraditional : English,
            _                       => English,
        };

    public static readonly MultiString Empty = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
}
