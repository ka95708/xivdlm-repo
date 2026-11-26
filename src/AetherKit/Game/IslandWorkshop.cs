using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace AetherKit.Game;

/// <summary>
/// 無人島工房排程預訂要用掉的素材（只讀）。
/// 遊戲在「排程表」開著時會算好每種素材的分配量，放在 AgentMJICraftSchedule 的 MaterialUse：
/// 三組分別是 [0] 目前這一天、[1] 本週、[2] 本週＋下週；每組 109 格，第 i 格對應 MJIItemPouch 表第 i 列。
/// 排程表沒開時這份資料不一定有，所以由模組在開著時讀、存進設定檔，之後顯示存下來的。
/// </summary>
public static unsafe class IslandWorkshop
{
    /// <summary>本週＋下週那一組。</summary>
    private const int ThisAndNextWeek = 2;

    /// <summary>排程表開著就讀出本週＋下週每種素材的預訂量（道具 ID → 數量，只列大於 0 的）；沒開回傳 null。</summary>
    public static Dictionary<uint, int>? ReadReserved()
    {
        var agent = AgentMJICraftSchedule.Instance();
        if (agent == null || !agent->IsAgentActive() || agent->Data == null) return null;

        var used = agent->Data->MaterialUse.Entries[ThisAndNextWeek].UsedAmounts;
        var rows = Services.Data.GetExcelSheet<MJIItemPouch>();
        var result = new Dictionary<uint, int>();

        for (var i = 0; i < used.Length; i++)
        {
            if (used[i] == 0) continue;
            if (rows.GetRowOrDefault((uint)i) is not { } row || row.Item.RowId == 0) continue;
            result[row.Item.RowId] = used[i];
        }

        return result;
    }
}
