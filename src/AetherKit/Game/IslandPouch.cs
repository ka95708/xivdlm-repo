using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace AetherKit.Game;

/// <summary>
/// 無人島的庫存（無人島背包），只讀。
/// 數量用遊戲自己查庫存的函式（AgentMJIPouch.GetPouchItemCount，跟背包視窗顯示的一樣），
/// 不用打開背包視窗；道具清單從 MJIItemPouch 表讀（第 1 類 = 素材）。
/// </summary>
public static unsafe class IslandPouch
{
    public readonly record struct Entry(uint ItemId, int Count);

    private const uint MaterialCategory = 1;

    /// <summary>素材那一頁的每一項和數量；讀不到回傳 null。</summary>
    public static List<Entry>? Materials()
    {
        var agent = Agent;
        if (agent == null) return null;

        return Services.Data.GetExcelSheet<MJIItemPouch>()
            .Where(r => r.Category.RowId == MaterialCategory && r.Item.RowId != 0)
            .Select(r => new Entry(r.Item.RowId, agent->GetPouchItemCount(r.RowId))) // [遊戲函式]
            .ToList();
    }

    /// <summary>
    /// 素材那一頁裡採集採得到的道具（照背包的順序），不用在島上也讀得到。
    /// 只能從倉庫探索拿到的（香薺、石榴石原石…）不在 MJIGatheringItem 表裡，不列。
    /// </summary>
    public static List<uint> MaterialItems()
    {
        var gatherable = Services.Data.GetExcelSheet<MJIGatheringItem>().Select(r => r.Item.RowId).ToHashSet();
        return Services.Data.GetExcelSheet<MJIItemPouch>()
            .Where(r => r.Category.RowId == MaterialCategory && gatherable.Contains(r.Item.RowId))
            .Select(r => r.Item.RowId)
            .ToList();
    }

    /// <summary>每種道具的數量（素材、種子、作物、牧場產物全部）；讀不到回傳 null。</summary>
    public static Dictionary<uint, int>? Counts()
    {
        var agent = Agent;
        if (agent == null) return null;

        var counts = new Dictionary<uint, int>();
        foreach (var row in Services.Data.GetExcelSheet<MJIItemPouch>())
        {
            if (row.Item.RowId == 0) continue;
            counts[row.Item.RowId] = agent->GetPouchItemCount(row.RowId); // [遊戲函式]
        }

        return counts;
    }

    /// <summary>MJIItemPouch 第 row 列的數量（穀倉資料用的是列號不是道具 ID）；讀不到回傳 null。</summary>
    public static int? CountOfRow(uint row)
    {
        var agent = Agent;
        return agent == null ? null : agent->GetPouchItemCount(row); // [遊戲函式]
    }

    public static string ItemName(uint itemId) =>
        Services.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() is { Length: > 0 } name
            ? name
            : $"#{itemId}";

    private static AgentMJIPouch* Agent =>
        IslandGather.OnIsland ? (AgentMJIPouch*)AgentModule.Instance()->GetAgentByInternalId(AgentId.MJIPouch) : null;
}
