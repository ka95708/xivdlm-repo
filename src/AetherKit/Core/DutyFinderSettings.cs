using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.UI;
using CsContentsFinder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder;

namespace AetherKit.Core;

/// <summary>任務搜尋器「設定」裡的選項，順序跟遊戲的設定資料一樣，不要改。</summary>
public enum DutyFinderOption
{
    Ja,
    En,
    De,
    Fr,
    LootRule,
    JoinPartyInProgress,
    UnrestrictedParty,
    LevelSync,
    MinimumIl,
    SilenceEcho,
    ExplorerMode,
    LimitedLevelingRoulette
}

/// <summary>
/// 讀寫任務搜尋器的選項，給「任務搜尋器快速設定」小功能和青魔教皇共用。
///
/// 寫入走遊戲自己「設定視窗按確定」時用的函式（特徵碼找位址，做法同 DR），遊戲會照常存檔；
/// 找不到函式（例如改版）時退回直接改值，當下有效，但不一定會存進遊戲設定。
/// </summary>
public static unsafe class DutyFinderSettings
{
    // [遊戲函式] 套用任務搜尋器設定的函式，改版後先確認（見 改版檢查清單.md）
    private const string ApplySignature = "E8 ?? ?? ?? ?? 49 8B 06 45 33 FF 49 8B CE 45 89 7E 20 FF 50 28 B0 01";

    private static readonly int OptionCount = Enum.GetValues<DutyFinderOption>().Length;

    private static nint apply;
    private static bool searched;

    /// <summary>找到遊戲的套用函式（設定會照常存檔）。false = 只能直接改值。</summary>
    public static bool UsesGameFunction
    {
        get
        {
            Find();
            return apply != 0;
        }
    }

    private static void Find()
    {
        if (searched) return;
        searched = true;

        try
        {
            apply = Services.SigScanner.ScanText(ApplySignature);
        }
        catch (Exception e)
        {
            apply = 0;
            Services.Log.Warning($"任務搜尋器設定：找不到套用函式，改為直接改值：{e.Message}");
            FeatureLog.For("core").Write($"任務搜尋器設定 找不到套用函式（{e.Message}），改為直接改值");
        }
    }

    /// <summary>語言選項在這個用戶端存不存在（台服可能沒有）。</summary>
    public static bool LanguagesAvailable
    {
        get
        {
            try
            {
                var ui = Services.GameConfig.UiConfig;
                return ui.TryGet("ContentsFinderUseLangTypeJA", out uint _) && ui.TryGet("ContentsFinderUseLangTypeEN", out uint _)
                       && ui.TryGet("ContentsFinderUseLangTypeDE", out uint _) && ui.TryGet("ContentsFinderUseLangTypeFR", out uint _);
            }
            catch
            {
                return false;
            }
        }
    }

    public static byte Get(DutyFinderOption option)
    {
        var finder = CsContentsFinder.Instance();
        return option switch
        {
            DutyFinderOption.Ja => Language("ContentsFinderUseLangTypeJA"),
            DutyFinderOption.En => Language("ContentsFinderUseLangTypeEN"),
            DutyFinderOption.De => Language("ContentsFinderUseLangTypeDE"),
            DutyFinderOption.Fr => Language("ContentsFinderUseLangTypeFR"),
            DutyFinderOption.JoinPartyInProgress => UiValue("ContentsFinderSupplyEnable"),
            DutyFinderOption.LootRule => (byte)finder->LootRules,
            DutyFinderOption.UnrestrictedParty => Flag(finder->IsUnrestrictedParty),
            DutyFinderOption.LevelSync => Flag(finder->IsLevelSync),
            DutyFinderOption.MinimumIl => Flag(finder->IsMinimalIL),
            DutyFinderOption.SilenceEcho => Flag(finder->IsSilenceEcho),
            DutyFinderOption.ExplorerMode => Flag(finder->IsExplorerMode),
            DutyFinderOption.LimitedLevelingRoulette => Flag(finder->IsLimitedLevelingRoulette),
            _ => 0
        };
    }

    public static bool IsOn(DutyFinderOption option) => Get(option) != 0;

    /// <summary>開關切換；分配方式在三種之間輪替。最後一個勾著的語言不能取消。</summary>
    public static void Toggle(DutyFinderOption option)
    {
        if (IsLanguage(option) && IsOn(option) && LanguageCount() <= 1) return;

        var value = option == DutyFinderOption.LootRule
            ? (byte)((Get(option) + 1) % 3)
            : (byte)(IsOn(option) ? 0 : 1);

        Set(new Dictionary<DutyFinderOption, byte> { [option] = value });
    }

    /// <summary>一次設好多個選項；已經一樣的不動。回傳 true = 有改。</summary>
    public static bool Set(IReadOnlyDictionary<DutyFinderOption, byte> values)
    {
        var changed = false;
        foreach (var (option, value) in values)
        {
            if (Get(option) != value) changed = true;
        }

        if (!changed) return false;

        Find();
        if (apply != 0) ApplyWithGame(values);
        else ApplyDirectly(values);

        return true;
    }

    /// <summary>
    /// 跟設定視窗按確定一樣：前半是新值、後半是舊值，最後一格固定 1（照 DR 的做法）。
    /// </summary>
    private static void ApplyWithGame(IReadOnlyDictionary<DutyFinderOption, byte> values)
    {
        var data = new byte[27];
        for (var i = 0; i < OptionCount; i++)
        {
            var current = Get((DutyFinderOption)i);
            data[i] = current;
            data[i + OptionCount] = current;
        }

        data[26] = 1;

        foreach (var (option, value) in values)
            data[(int)option] = value;

        fixed (byte* p = data)
            ((delegate* unmanaged<byte*, UIModule*, void>)apply)(p, UIModule.Instance());
    }

    // [改記憶體] 找不到套用函式時直接改任務搜尋器選項（見 改版檢查清單.md）
    private static void ApplyDirectly(IReadOnlyDictionary<DutyFinderOption, byte> values)
    {
        var finder = CsContentsFinder.Instance();
        foreach (var (option, value) in values)
        {
            var on = value != 0;
            switch (option)
            {
                case DutyFinderOption.LootRule: finder->LootRules = (CsContentsFinder.LootRule)value; break;
                case DutyFinderOption.UnrestrictedParty: finder->IsUnrestrictedParty = on; break;
                case DutyFinderOption.LevelSync: finder->IsLevelSync = on; break;
                case DutyFinderOption.MinimumIl: finder->IsMinimalIL = on; break;
                case DutyFinderOption.SilenceEcho: finder->IsSilenceEcho = on; break;
                case DutyFinderOption.ExplorerMode: finder->IsExplorerMode = on; break;
                case DutyFinderOption.LimitedLevelingRoulette: finder->IsLimitedLevelingRoulette = on; break;
                case DutyFinderOption.JoinPartyInProgress: Services.GameConfig.UiConfig.Set("ContentsFinderSupplyEnable", (uint)value); break;
                case DutyFinderOption.Ja: Services.GameConfig.UiConfig.Set("ContentsFinderUseLangTypeJA", (uint)value); break;
                case DutyFinderOption.En: Services.GameConfig.UiConfig.Set("ContentsFinderUseLangTypeEN", (uint)value); break;
                case DutyFinderOption.De: Services.GameConfig.UiConfig.Set("ContentsFinderUseLangTypeDE", (uint)value); break;
                case DutyFinderOption.Fr: Services.GameConfig.UiConfig.Set("ContentsFinderUseLangTypeFR", (uint)value); break;
            }
        }
    }

    private static bool IsLanguage(DutyFinderOption option) => option <= DutyFinderOption.Fr;

    private static int LanguageCount() =>
        Get(DutyFinderOption.Ja) + Get(DutyFinderOption.En) + Get(DutyFinderOption.De) + Get(DutyFinderOption.Fr);

    private static byte Flag(bool value) => value ? (byte)1 : (byte)0;

    private static byte Language(string key) => LanguagesAvailable ? UiValue(key) : (byte)0;

    private static byte UiValue(string key)
    {
        try
        {
            return Services.GameConfig.UiConfig.TryGet(key, out uint value) ? (byte)value : (byte)0;
        }
        catch
        {
            return 0;
        }
    }
}
