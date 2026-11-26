using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace AetherKit.Game;

/// <summary>
/// 無人島設施：開拓穀倉的領取與派遣（參考 visland 的做法）。
/// 在穀倉視窗開著時，直接呼叫遊戲自己處理那些按鈕的函式（[遊戲函式]），不模擬點擊。
/// 持有上限 999：會超過就不領，回傳原因給按鈕顯示。
/// </summary>
public static unsafe class IslandFacilities
{
    public const string GranaryAddon = "MJIGatheringHouse";

    private const int Cap = 999;
    private const int Granaries = 2;
    private const int MaxExpeditionDays = 7;
    private const int CowriesPerDay = 50;

    /// <summary>謝爾達萊青船幣（派遣探險的花費）。</summary>
    private const uint Cowrie = 37549;

    private static MJIManager* Manager => MJIManager.Instance();

    // ── 開拓穀倉 ─────────────────────────────────────────────

    /// <summary>
    /// 兩個穀倉：能領的先領（會超過上限的那個不領），再把探險改成「稀有資源庫存最少」的兩個地點、補滿 7 天。
    /// 回傳每一步做了什麼，給 log 和按鈕提示。
    /// </summary>
    public static List<string> CollectAndDispatchGranaries()
    {
        var result = new List<string>();
        var agent = AgentMJIGatheringHouse.Instance();
        var manager = Manager;
        if (agent == null || agent->Data == null || !agent->Data->Initialized || manager == null || manager->GranariesState == null)
        {
            result.Add("穀倉資料還沒讀到");
            return result;
        }

        var state = manager->GranariesState;

        for (var i = 0; i < Granaries; i++)
        {
            var granary = GranaryAt(state, i);
            if (!HasResources(granary))
                continue;

            if (GranaryOvercap(granary) is { } full)
            {
                result.Add($"穀倉 {i + 1}：{full} 會超過 {Cap}，沒有領");
                continue;
            }

            state->CollectResources((byte)i); // [遊戲函式] 穀倉「領取」
            result.Add($"穀倉 {i + 1}：已領取");
        }

        DispatchLeastStocked(agent, state, result);
        return result;
    }

    private static MJIGranaryState* GranaryAt(MJIGranariesState* state, int index) =>
        (MJIGranaryState*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref state->Granary[index]);

    private static bool HasResources(MJIGranaryState* granary)
    {
        if (granary->RareResourceCount > 0) return true;
        foreach (var count in granary->NormalResourceCounts)
            if (count > 0) return true;
        return false;
    }

    /// <summary>領了會超過上限的第一樣東西的名稱；都不會就 null。</summary>
    private static string? GranaryOvercap(MJIGranaryState* granary)
    {
        if (granary->RareResourceCount > 0 && WouldOvercap(granary->RareResourcePouchId, granary->RareResourceCount) is { } rare)
            return rare;

        var ids = granary->NormalResourcePouchIds;
        var counts = granary->NormalResourceCounts;
        for (var i = 0; i < counts.Length; i++)
        {
            if (counts[i] > 0 && WouldOvercap(ids[i], counts[i]) is { } name)
                return name;
        }

        return null;
    }

    private static string? WouldOvercap(uint pouchRow, int adding)
    {
        if (IslandPouch.CountOfRow(pouchRow) is not { } have || have + adding <= Cap) return null;

        var item = Services.Data.GetExcelSheet<MJIItemPouch>().GetRowOrDefault(pouchRow)?.Item.RowId ?? 0;
        return IslandPouch.ItemName(item);
    }

    /// <summary>
    /// 已解鎖的探險地點照「稀有資源的庫存」由少到多排，兩個穀倉派最少的兩個（不重複）。
    /// 已經在派其中一個的穀倉不換，只補天數；剩不到 7 天就補到 7 天，青船幣不夠就派付得起的天數。
    /// </summary>
    private static void DispatchLeastStocked(AgentMJIGatheringHouse* agent, MJIGranariesState* state, List<string> result)
    {
        var counts = IslandPouch.Counts();
        var expeditions = new List<(byte Id, string Name, uint Rare, int Stock)>();

        // 直接用遊戲那份資料的指標問「解鎖了沒」，不要傳複本
        var list = &agent->Data->Expeditions;
        for (var e = list->First; e < list->Last; e++)
        {
            if (!agent->IsExpeditionUnlocked(e)) continue; // [遊戲函式] 只讀

            var stock = counts != null && counts.TryGetValue(e->RareItemId, out var have) ? have : 0;
            expeditions.Add((e->ExpeditionId, e->Name.ToString(), e->RareItemId, stock));
        }

        if (expeditions.Count == 0)
        {
            result.Add("沒有解鎖的探險地點");
            return;
        }

        var wanted = expeditions.OrderBy(e => e.Stock).Take(Granaries).ToList();

        // 穀倉已經派了想要的其中一個就留著，另一個穀倉派剩下的
        var plan = new (byte Id, string Name, int Stock)?[Granaries];
        var left = wanted.ToList();
        for (var i = 0; i < Granaries; i++)
        {
            var current = GranaryAt(state, i)->ActiveExpeditionId;
            var keep = left.FindIndex(e => e.Id == current);
            if (keep < 0) continue;

            plan[i] = (left[keep].Id, left[keep].Name, left[keep].Stock);
            left.RemoveAt(keep);
        }

        for (var i = 0; i < Granaries; i++)
        {
            if (plan[i] != null || left.Count == 0) continue;

            plan[i] = (left[0].Id, left[0].Name, left[0].Stock);
            left.RemoveAt(0);
        }

        for (var i = 0; i < Granaries; i++)
        {
            if (plan[i] is not { } target) continue;
            result.Add(Dispatch(agent, state, (byte)i, target.Id, target.Name, target.Stock));
        }
    }

    private static string Dispatch(AgentMJIGatheringHouse* agent, MJIGranariesState* state, byte index, byte expedition, string name, int stock)
    {
        var granary = GranaryAt(state, index);
        var curExpedition = granary->ActiveExpeditionId;
        var curDays = granary->RemainingDays;

        var affordable = InventoryManager.Instance()->GetInventoryItemCount(Cowrie) / CowriesPerDay;
        var days = (byte)MaxExpeditionDays;

        var confirm = Confirmation(curExpedition, curDays, expedition, days);
        if (confirm == AgentMJIGatheringHouse.Confirmation.None)
            return $"穀倉 {index + 1}：{name}（庫存 {stock}）已經派滿 {curDays} 天";

        // 同一個地點是補天數，只付多出來的；換地點從頭算
        var cost = curExpedition == expedition ? days - curDays : days;
        if (cost > affordable)
        {
            if (affordable <= 0)
                return $"穀倉 {index + 1}：青船幣不夠，沒有派遣";

            days = (byte)(curExpedition == expedition ? curDays + affordable : affordable);
            confirm = Confirmation(curExpedition, curDays, expedition, days);
            if (confirm == AgentMJIGatheringHouse.Confirmation.None)
                return $"穀倉 {index + 1}：青船幣不夠補天數";
        }

        // 照 visland：先把穀倉視窗「選擇探險」時遊戲會填的欄位填好，再呼叫確認
        agent->CurGranaryIndex = index; // [改記憶體]
        agent->CurActiveExpeditionId = curExpedition; // [改記憶體]
        agent->CurActiveDays = curDays; // [改記憶體]
        agent->CurSelectedExpeditionId = expedition; // [改記憶體]
        agent->CurHoveredExpeditionId = expedition; // [改記憶體]
        agent->CurSelectedDays = days; // [改記憶體]
        agent->CurExpeditionName.SetString(name); // [改記憶體]
        agent->ConfirmType = confirm; // [改記憶體]
        agent->GranariesState->SelectExpeditionCommit(index, expedition, days); // [遊戲函式] 穀倉「派遣」確認

        var action = curExpedition == expedition ? "延長" : curExpedition == 0 ? "派遣" : "改派";
        return $"穀倉 {index + 1}：{action} {name}（庫存 {stock}）{days} 天";
    }

    /// <summary>跟穀倉視窗確認框一樣的分類：開始、換地點、換地點並延長、延長；不用改是 None。</summary>
    private static AgentMJIGatheringHouse.Confirmation Confirmation(byte curExpedition, byte curDays, byte newExpedition, byte newDays)
    {
        if (curExpedition == newExpedition && curDays >= newDays) return AgentMJIGatheringHouse.Confirmation.None;
        if (curExpedition == 0 || curDays == 0) return AgentMJIGatheringHouse.Confirmation.Start;
        if (curExpedition != newExpedition)
            return newDays > curDays ? AgentMJIGatheringHouse.Confirmation.ChangeExtend : AgentMJIGatheringHouse.Confirmation.Change;
        return AgentMJIGatheringHouse.Confirmation.Extend;
    }
}
