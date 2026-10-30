using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using HotbarSlotType = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlotType;

namespace AetherKit.Game;

/// <summary>自動分區的一列：要顯示的東西，加上它屬於哪個職能（沒有就是 null）。</summary>
public readonly record struct ContentRow(ActionItem Item, JobRole? Role);

/// <summary>
/// 自動分區的內容來源。每幀重讀 — 資料都在記憶體裡，成本可以忽略，
/// 而且遊戲那邊一改（新增裝備組、調整常用順序）就立刻反映。
///
/// 這裡只回答「有什麼」，不管顏色或排版：職能要用什麼顏色、小組怎麼排，是畫面的事。
/// </summary>
public static unsafe class PanelContent
{
    public static IReadOnlyList<ContentRow> Get(SectionSource source) => source switch
    {
        SectionSource.Gearsets => Gearsets(),
        SectionSource.EmoteFavorites => EmoteFavorites(),
        _ => Array.Empty<ContentRow>()
    };

    private static List<ContentRow> Gearsets()
    {
        var result = new List<(ContentRow Row, byte Priority)>();
        var module = RaptureGearsetModule.Instance();
        if (module == null) return new List<ContentRow>();

        var jobs = Services.Data.GetExcelSheet<ClassJob>();

        for (var i = 0; i < 100; i++)
        {
            if (!module->IsValidGearset(i)) continue;

            var entry = module->GetGearset(i);
            if (entry == null) continue;

            var name = entry->NameString;
            if (string.IsNullOrWhiteSpace(name)) continue;

            var job = jobs?.GetRowOrDefault(entry->ClassJob);
            var abbreviation = job?.Abbreviation.ExtractText() ?? string.Empty;
            var icon = module->GetClassJobIconForGearset(i);

            var item = new ActionItem
            {
                Label = name,
                SubLabel = string.IsNullOrEmpty(abbreviation)
                    ? $"IL {entry->ItemLevel}"
                    : $"{abbreviation} · IL {entry->ItemLevel}",
                IconId = (uint)Math.Max(0, icon),
                Command = GameCommands.GearsetByName(name),
                Source = new GameSource { Type = (int)HotbarSlotType.GearSet, Id = (uint)i }
            };

            var role = RoleOf(entry->ClassJob, job?.Role ?? 0, job?.PrimaryStat ?? 0);
            result.Add((new ContentRow(item, role), job?.UIPriority ?? byte.MaxValue));
        }

        // 小組裡的順序跟遊戲職業頁一樣（騎士、戰士、暗黑騎士…），同職業多組時照裝備組編號。
        // OrderBy 是穩定排序，同職業的相對順序不變
        return result.OrderBy(x => x.Priority).Select(x => x.Row).ToList();
    }

    /// <summary>主屬性是智力的遠程算魔法職業（BaseParam 4 = 智力）。</summary>
    private const byte Intelligence = 4;

    /// <summary>
    /// ClassJob.Role 只分到坦克／近戰／遠程／治療，生產採集是 0，
    /// 所以生產採集改用職業 ID 分段判斷；遠程再用主屬性分物理與魔法。
    /// </summary>
    private static JobRole RoleOf(byte classJob, byte role, byte primaryStat) => classJob switch
    {
        >= 8 and <= 15 => JobRole.Crafter,
        >= 16 and <= 18 => JobRole.Gatherer,
        _ => role switch
        {
            1 => JobRole.Tank,
            2 => JobRole.Melee,
            4 => JobRole.Healer,
            _ => primaryStat == Intelligence ? JobRole.MagicalRanged : JobRole.PhysicalRanged
        }
    };

    private static List<ContentRow> EmoteFavorites()
    {
        var result = new List<ContentRow>();
        var module = EmoteHistoryModule.Instance();
        if (module == null) return result;

        var sheet = Services.Data.GetExcelSheet<Emote>();
        if (sheet == null) return result;

        foreach (var id in module->Favorites)
        {
            if (id == 0) continue;

            if (sheet.GetRowOrDefault(id) is not { } emote) continue;

            var name = emote.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var command = GameCommands.Emote(emote);

            result.Add(new ContentRow(new ActionItem
            {
                Label = name,
                SubLabel = command,
                IconId = emote.Icon,
                Command = command,
                Source = new GameSource { Type = (int)HotbarSlotType.Emote, Id = id }
            }, null));
        }

        return result;
    }
}
