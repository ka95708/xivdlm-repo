using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace AetherKit.Game;

/// <summary>
/// 開拓工房的遊戲資料：產品表（遊戲表）、本週歡迎度、今天的供需、幹勁、工坊、貓耳小員（記憶體，只讀）。
/// 供需每個產品一個位元組：高 4 位元是供給（0 嚴重不足～4 嚴重過剩），低 4 位元是需求變動（0 大幅增加～4 大幅降低），
/// 編號跟求解器（mji-craftwork）的 Demand／DemandChange 一樣。
/// </summary>
public static unsafe class Craftworks
{
    public const string ScheduleAddon = "MJICraftSchedule";
    public const int DaysPerWeek = 7;

    /// <summary>一個產品（MJICraftworksObject 一列）。</summary>
    public sealed record CraftObject(uint Id, uint ItemId, string Name, byte Theme1, byte Theme2, byte Level, byte Time, ushort Value);

    /// <summary>貓耳小員本週的一項需求：產品、要幾個、已經交了＋排了幾個。</summary>
    public readonly record struct Favor(uint CraftId, int Done);

    /// <summary>某一刻讀到的工房狀態。</summary>
    public sealed record Snapshot(
        string WeekKey,
        int Day,
        uint Popularity,
        uint NextPopularity,
        byte[] SupplyDemand,
        int Groove,
        int MaxGroove,
        int Rank,
        int Workshops,
        int WorkshopLevel,
        uint RestCycles,
        Favor[] Favors,
        Favor[] NextFavors);

    private static List<CraftObject>? objects;

    /// <summary>全部產品（有道具的列），照列號排。</summary>
    public static IReadOnlyList<CraftObject> Objects => objects ??= LoadObjects();

    private static List<CraftObject> LoadObjects()
    {
        var items = Services.Data.GetExcelSheet<Item>();
        return Services.Data.GetExcelSheet<MJICraftworksObject>()
            .Where(r => r.Item.RowId != 0)
            .Select(r => new CraftObject(
                r.RowId,
                r.Item.RowId,
                items.GetRowOrDefault(r.Item.RowId)?.Name.ExtractText() ?? $"#{r.RowId}",
                (byte)r.Theme[0].RowId,
                (byte)r.Theme[1].RowId,
                (byte)r.LevelReq,
                (byte)r.CraftingTime,
                r.Value))
            .ToList();
    }

    /// <summary>列號 → 產品；找不到 null。</summary>
    public static CraftObject? Object(uint id) => Objects.FirstOrDefault(o => o.Id == id);

    /// <summary>產品名稱（遊戲裡的全名）。</summary>
    public static string ShortName(uint id) => Object(id)?.Name ?? $"#{id}";

    public static string ThemeName(byte theme) =>
        theme == 0 ? string.Empty : Services.Data.GetExcelSheet<MJICraftworksObjectTheme>().GetRowOrDefault(theme)?.Name.ExtractText() ?? $"#{theme}";

    /// <summary>歡迎度型態（MJICraftworksPopularity 列號）下某個產品的歡迎度：1 人氣火爆、2 略受瞩目、3 普普通通、4 無人問津、0 未知。</summary>
    public static byte PopularityOf(uint row, uint craftId)
    {
        if (Services.Data.GetExcelSheet<MJICraftworksPopularity>().GetRowOrDefault(row) is not { } pop) return 0;
        return craftId < pop.Popularity.Count ? (byte)pop.Popularity[(int)craftId].RowId : (byte)0;
    }

    /// <summary>歡迎度的比率（%），從遊戲表讀：140／120／100／80。</summary>
    public static int PopularityRatio(byte popularity) =>
        Services.Data.GetExcelSheet<MJICraftworksPopularityType>().GetRowOrDefault(popularity)?.Ratio ?? 0;

    /// <summary>供給的比率（%）：0→160 … 4→60。</summary>
    public static int SupplyRatio(int supply) =>
        Services.Data.GetExcelSheet<MJICraftworksSupplyDefine>().GetRowOrDefault((uint)supply)?.Ratio ?? 0;

    public static int Supply(byte packed) => (packed & 0xF0) >> 4;
    public static int DemandShift(byte packed) => packed & 0x0F;

    /// <summary>
    /// 這一週的代號：本週第 1 天的日期（UTC，遊戲每天在 08:00 UTC 換日）。
    /// 用今天的日期往回推目前是第幾天，不用自己算週二。
    /// </summary>
    public static string WeekKey(int currentDay) =>
        (DateTime.UtcNow.AddHours(-8).Date - TimeSpan.FromDays(currentDay)).ToString("yyyy-MM-dd");

    /// <summary>讀目前的工房狀態；不在無人島或資料還沒好回傳 null。</summary>
    public static Snapshot? Read()
    {
        if (!IslandGather.OnIsland) return null;

        var manager = MJIManager.Instance();
        if (manager == null) return null;

        var island = &manager->IslandState;
        var day = manager->CurrentCycleDay;

        var packed = manager->SupplyAndDemandShifts.ToArray();

        // 工坊：蓋好（有位置、不在施工）的才算；等級取最低的那間
        int count = 0, level = int.MaxValue;
        for (var i = 0; i < 4; i++) // 最多 4 間工坊
        {
            if (island->Workshops.PlaceId[i] == 0 || island->Workshops.UnderConstruction[i] != 0) continue;
            count++;
            level = Math.Min(level, island->Workshops.BuildingLevel[i]);
        }

        if (count == 0) level = 0;

        // 幹勁上限：照已完成的地標數查表（0 個 10、1 個 15 … 5 個 45）
        var landmarks = 0;
        for (var i = 0; i < island->LandmarkIds.Length; i++)
            if (island->LandmarkIds[i] != 0 && island->LandmarkUnderConstruction[i] == 0)
                landmarks++;
        var maxGroove = Services.Data.GetExcelSheet<MJICraftworksTension>().GetRowOrDefault((uint)landmarks)?.Unknown0 ?? 0;

        // 休息日：排班視窗開著時用它那份（含還沒存的修改），不然用遊戲記的
        var rest = 0u;
        var agent = AgentMJICraftSchedule.Instance();
        if (agent != null && agent->IsAgentActive() && agent->Data != null)
            rest = agent->Data->RestCycles;
        else
            foreach (var cycle in manager->CraftworksRestDays)
                if (cycle < 32) rest |= 1u << cycle;

        return new Snapshot(
            WeekKey(day), day,
            manager->CurrentPopularity, manager->NextPopularity,
            packed,
            (int)manager->CurrentGroove, (int)maxGroove,
            island->CurrentRank,
            count, level,
            rest,
            ReadFavors(manager, 3),
            ReadFavors(manager, 6));
    }

    /// <summary>貓耳小員：每週 3 項，第 0～2 格上週、3～5 格本週、6～8 格下週；數量 = 已交＋已排。</summary>
    private static Favor[] ReadFavors(MJIManager* manager, int first)
    {
        var favors = new List<Favor>();
        if (manager->FavorState == null) return favors.ToArray();

        var state = manager->FavorState;
        for (var i = first; i < first + 3; i++)
        {
            var id = state->CraftObjectIds[i];
            if (id != 0) favors.Add(new Favor(id, state->NumDelivered[i] + state->NumScheduled[i]));
        }

        return favors.ToArray();
    }

    /// <summary>下週的代號（本週第 1 天＋7 天）。</summary>
    public static string NextWeekKey(string weekKey) =>
        DateTime.TryParse(weekKey, out var start) ? start.AddDays(DaysPerWeek).ToString("yyyy-MM-dd") : weekKey;

    /// <summary>排班表上的第幾天（0～6 本週、7～13 下週）怎麼念。</summary>
    public static string DayLabel(int cycle) => cycle < DaysPerWeek ? $"第 {cycle + 1} 天" : $"下週第 {cycle - DaysPerWeek + 1} 天";

    /// <summary>供需資料讀到了沒（全部是 0 代表還沒跟伺服器要過）。</summary>
    public static bool HasSupplyData(Snapshot snapshot) => snapshot.SupplyDemand.Any(b => b != 0);

    /// <summary>請伺服器送今天的供需和貓耳小員資料（排班視窗打開「需求」時遊戲自己也會要）。</summary>
    public static void RequestData()
    {
        var manager = MJIManager.Instance();
        if (manager == null) return;

        manager->RequestDemandFull(); // [遊戲函式] 要今天的供需
        manager->RequestFavorData(); // [遊戲函式] 要貓耳小員的需求
    }
}
