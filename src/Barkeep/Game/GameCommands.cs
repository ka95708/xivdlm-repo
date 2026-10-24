using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using HotbarSlotType = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlotType;

namespace Barkeep.Game;

/// <summary>
/// 把遊戲裡的東西換成文字指令。拖曳接收和自動分區都用這一份，
/// 兩邊產生的指令才會一致。
/// </summary>
public static unsafe class GameCommands
{
    /// <summary>做不出指令的種類回傳空字串。</summary>
    public static string For(HotbarSlotType type, uint id, string name)
    {
        // 裝備組優先用名稱：編號會因為重新排序而跑掉，名稱不會
        if (type == HotbarSlotType.GearSet)
            return Gearset(id);

        if (string.IsNullOrEmpty(name))
            return string.Empty;

        return type switch
        {
            HotbarSlotType.Action
                or HotbarSlotType.CraftAction
                or HotbarSlotType.PetAction
                or HotbarSlotType.BuddyAction
                or HotbarSlotType.BgcArmyAction => $"/ac \"{name}\"",

            HotbarSlotType.GeneralAction => $"/gaction \"{name}\"",
            HotbarSlotType.Item or HotbarSlotType.EventItem => $"/item \"{name}\"",
            HotbarSlotType.Mount => $"/mount \"{name}\"",
            HotbarSlotType.Companion => $"/minion \"{name}\"",
            HotbarSlotType.Ornament => $"/fashion \"{name}\"",
            HotbarSlotType.Emote => Emote(id),

            _ => string.Empty
        };
    }

    /// <summary>
    /// 裝備組要用它真正的名字。熱鍵列的顯示名稱後面會黏上平均裝等
    /// （像「騎士 740」），拿去下指令會比對不到。
    /// </summary>
    public static string Gearset(uint id)
    {
        var module = RaptureGearsetModule.Instance();

        if (module != null && module->IsValidGearset((int)id))
        {
            var entry = module->GetGearset((int)id);
            var name = entry != null ? entry->NameString : string.Empty;

            if (!string.IsNullOrWhiteSpace(name))
                return GearsetByName(name);
        }

        // 取不到名字才退回編號，但編號會隨排序改變
        return $"/gearset change {id + 1}";
    }

    public static string GearsetByName(string name) => $"/gearset change \"{name}\"";

    public static string Emote(uint emoteId)
    {
        var row = Services.Data.GetExcelSheet<Emote>()?.GetRowOrDefault(emoteId);
        return row is { } emote ? Emote(emote) : string.Empty;
    }

    /// <summary>
    /// 情感動作沒有通用指令，每個都有自己的 /指令。
    /// 從 Excel 的 TextCommand 拿，所以會自動是這個客戶端的在地化寫法。
    /// </summary>
    public static string Emote(Emote emote)
    {
        var text = emote.TextCommand.ValueNullable;
        if (text is not { } command) return string.Empty;

        foreach (var candidate in new[] { command.Command, command.ShortCommand, command.Alias })
        {
            var value = candidate.ExtractText();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }
}
