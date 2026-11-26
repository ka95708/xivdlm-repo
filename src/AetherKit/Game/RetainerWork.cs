using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using AetherKit.Core;
using CsFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

namespace AetherKit.Game;

/// <summary>
/// 自動雇員（AutoRetainer 類功能，做法參考 DR 的 AutoRetainerWork）：
/// - 收探險並重新出發：雇員清單裡探險已完成的一個個進去，看結果 → 「重新派遣同一個探險」→ 確定 → 出來。
/// - 提領金幣：有金幣的一個個進去「Gil管理」，全部取出。
/// - 單一雇員的出售清單：價格表（RetainerPriceTable）查市場、改價、全部套用參考價；全部收回給自己。
/// - 賣東西時自動填價格：價格視窗（上架、自己點修改價格）一打開就查市場、把參考價填進價格欄。
///
/// 進出雇員、選選項、按確定、上架時填價格欄都是點遊戲視窗，對話交給核心的對話推進。
/// 查市場、改已上架的價格、收回用的是遊戲自己的函式（同 DR），列在改版檢查清單。
/// 使用者先在傳喚鈴打開雇員清單，再按清單上方的按鈕開始。
/// </summary>
public sealed unsafe class RetainerWork : IDisposable
{
    public const string Owner = "retainer-work";

    private const InventoryType MarketContainer = InventoryType.RetainerMarket;
    private const int MarketSlots = 20;

    // 雇員選項清單裡的文字：從遊戲文字表（Addon）讀，台服是
    // 2385「查看僱員探險情況　[結束]」、2403「…[結束保留中]」、912「Gil管理」、2380「出售（玩家所持物品）」。
    // 比對「包含」；後面再放 DR 用的各語言字當備用。
    private static string[] VentureCompleteTexts => Texts(new uint[] { 2385, 2403 }, "结束", "Complete", "完了");
    private static string[] GilTexts => Texts(new uint[] { 912 }, "金币管理", "Entrust or withdraw gil", "ギルの受け渡し");

    private static string[] Texts(uint[] rows, params string[] fallback) =>
        rows.Select(Addons.Text).Where(t => t.Length > 0).Concat(fallback).ToArray();

    private readonly Plugin plugin;
    private readonly Routine routine = new();
    private readonly FeatureLog log = FeatureLog.For("retainer");
    private readonly List<IDisposable> leases = new();

    /// <summary>查過的市場參考價（物品, HQ）→（別人的最低單價（已濾離群值），查的時間）；0 = 沒人在賣。</summary>
    private readonly Dictionary<(uint Item, bool Hq), Quote> priceCache = new();

    /// <summary>查過的市場價保留多久（跨次執行也用，這段時間內不重查）。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private string status = "待命";
    private bool lastOk;
    private DateTime requestedAt;

    /// <summary>兩次查市場之間至少隔這麼久。</summary>
    private static readonly TimeSpan RequestGap = TimeSpan.FromSeconds(2.5);
    /// <summary>目前開著的價格視窗已經自動填過（關掉才重設）。</summary>
    private bool sellWindowHandled;

    public RetainerWork(Plugin plugin)
    {
        this.plugin = plugin;
        Services.Framework.Update += OnUpdate;
    }

    private RetainerWorkConfig Config => plugin.Config.RetainerWork;

    private CoreServices Core => plugin.Core;

    /// <summary>模組啟用中（停用時不自動填價格、不藏出售清單）。</summary>
    public bool Active { get; set; }

    public bool Running { get; private set; }

    /// <summary>正在做的事（按鈕的名稱），停止按鈕上顯示。</summary>
    public string Job { get; private set; } = string.Empty;

    public string Status
    {
        get => status;
        private set
        {
            if (value == status) return;
            status = value;
            log.Write($"狀態 {value}");
        }
    }

    // ───────────────────────── 開始／停止 ─────────────────────────

    public bool RetainerListOpen => Addons.IsOpen("RetainerList");

    public bool SellListOpen => Addons.IsOpen("RetainerSellList");

    public void CollectVentures() => Start("收探險並重新出發", VenturesJob);

    public void WithdrawGil() => Start("提領金幣", GilJob);

    /// <summary>在某個雇員的出售清單裡：全部收回給自己（背包）。</summary>
    public void RetrieveAll() => Start("全部收回給自己", RetrieveAllJob);

    private void Start(string job, Func<IEnumerable<object?>> body)
    {
        if (Running) return;

        if (!Core.Lock.TryAcquire(Owner, $"自動雇員：{job}", () => Stop("從核心能力頁停止")))
        {
            Notify.Error("自動雇員", $"{Core.Lock.Label} 正在執行，先停止它。");
            return;
        }

        leases.Add(Core.Talk.Acquire(Owner));
        Job = job;
        Running = true;
        routine.Start(Run(body));
        log.Write($"===== 開始：{job}");
        Status = job;
    }

    private IEnumerable<object?> Run(Func<IEnumerable<object?>> body)
    {
        yield return body();
        Stop("完成");
    }

    public void Stop(string reason)
    {
        if (!Running) return;

        Running = false;
        routine.Clear();
        foreach (var lease in leases)
            lease.Dispose();
        leases.Clear();
        Core.Lock.Release(Owner);


        Status = $"已停止：{reason}";
        log.Write($"===== 停止（{Job}）：{reason}");
    }

    public void Dispose()
    {
        Stop("插件卸載");
        Services.Framework.Update -= OnUpdate;
        Services.Framework.RunOnFrameworkThread(() => SetNativeSellList(true));
    }

    /// <summary>模組停用、價格表關掉時呼叫：把遊戲原本的出售清單顯示回來。</summary>
    public void RestoreNativeSellList() => Services.Framework.RunOnFrameworkThread(() => SetNativeSellList(true));

    private void OnUpdate(IFramework framework)
    {
        if (!Services.ClientState.IsLoggedIn)
        {
            Stop("已登出");
            return;
        }

        // 模組停用：什麼都不做（停用時已經把出售清單顯示回來）
        if (!Active) return;

        // 價格表開著時把遊戲原本的出售清單藏起來：價格表蓋在上面，滑鼠移過去才不會觸發底下物品的資訊框
        SetNativeSellList(!Config.ShowPriceTable);

        if (Core.Paused) return;

        try
        {
            var now = DateTime.Now;
            if (Running) routine.Advance(now);

            // 上架新東西不受其他流程影響（只填價格、排佇列），一直看著
            WatchSellWindow(now);
        }
        catch (Exception e)
        {
            log.Write($"錯誤：{e}");
            Services.Log.Error(e, "自動雇員發生錯誤");
            Stop($"發生錯誤：{e.Message}");
        }
    }

    // ───────────────────────── 收探險、重新出發 ─────────────────────────

    private IEnumerable<object?> VenturesJob()
    {
        var handled = new HashSet<ulong>();
        var done = 0;

        while (Running)
        {
            var now = CsFramework.GetServerTime();
            var next = Retainers().FirstOrDefault(r => r.VentureId != 0 && r.VentureComplete != 0
                                                       && r.VentureComplete <= now && !handled.Contains(r.Id));
            if (next.Id == 0) break;

            handled.Add(next.Id);
            Status = $"收探險：{next.Name}";

            yield return Enter(next);
            if (!lastOk) continue;

            if (!Addons.ClickSelectString(VentureCompleteTexts))
            {
                log.Write($"{next.Name} 選項裡找不到探險結果：{string.Join(" | ", Addons.SelectStringEntries())}");
                yield return Leave();
                continue;
            }

            yield return Routine.Until(() => Addons.IsOpen("RetainerTaskResult"), 10);
            if (!Addons.IsOpen("RetainerTaskResult"))
            {
                log.Write($"{next.Name} 探險結果視窗沒有出現");
                yield return Leave();
                continue;
            }

            // 「重新派遣同一個探險」→ 確定
            yield return Routine.Delay(0.3);
            FireIfOpen("RetainerTaskResult", 14);

            yield return Routine.Until(() => Addons.IsOpen("RetainerTaskAsk"), 10);
            yield return Routine.Delay(0.3);
            FireIfOpen("RetainerTaskAsk", 12);

            yield return Routine.Until(() => !Addons.IsOpen("RetainerTaskAsk"), 5);
            if (Addons.IsOpen("RetainerTaskAsk"))
            {
                // 確定按不下去：多半是探險幣不夠
                log.Write($"{next.Name} 重新派遣確定不了（可能探險幣不夠），停止");
                FireIfOpen("RetainerTaskAsk", -1);
                yield return Leave();
                Stop("重新派遣確定不了（可能探險幣不夠）");
                yield break;
            }

            done++;
            log.Write($"{next.Name} 已收探險並重新出發");
            yield return Leave();
        }

        log.Write(done > 0
            ? $"收了 {done} 個雇員的探險並重新出發"
            : "沒有探險已完成的雇員");
    }

    // ───────────────────────── 提領金幣 ─────────────────────────

    private IEnumerable<object?> GilJob()
    {
        var total = 0L;
        var count = 0;

        foreach (var retainer in Retainers().Where(r => r.Gil > 0).ToList())
        {
            if (!Running) yield break;

            Status = $"提領金幣：{retainer.Name}";
            yield return Enter(retainer);
            if (!lastOk) continue;

            if (!Addons.ClickSelectString(GilTexts))
            {
                log.Write($"{retainer.Name} 選項裡找不到金幣管理：{string.Join(" | ", Addons.SelectStringEntries())}");
                yield return Leave();
                continue;
            }

            yield return Routine.Until(() => Addons.IsOpen("Bank"), 10);
            yield return Routine.Delay(0.3);

            var amount = WithdrawAllGil();
            if (amount > 0)
            {
                total += amount;
                count++;
                log.Write($"{retainer.Name} 取出 {amount:N0} 金幣");
            }

            yield return Routine.Until(() => !Addons.IsOpen("Bank"), 3);
            CloseIfOpen("Bank");
            yield return Leave();
        }

        log.Write(count > 0
            ? $"從 {count} 個雇員取出 {total:N0} 金幣"
            : "沒有雇員身上有金幣");
    }

    /// <summary>金幣管理視窗：輸入全部金額、確定。回傳取出多少（0 = 沒有，直接取消）。</summary>
    private static int WithdrawAllGil()
    {
        var bank = Addons.Get("Bank");
        if (bank == null) return 0;

        var amount = Addons.ValueInt(bank, 6);
        if (amount <= 0)
        {
            Addons.Fire(bank, 1, 0);
            return 0;
        }

        Addons.Fire(bank, 3, (uint)amount);
        Addons.Fire(bank, 0, 0);
        return amount;
    }

    // ───────────────────────── 存入重複物品 ─────────────────────────

    // 雇員選項「道具管理　[現使用欄位：12/175]」（Addon 2378），比對全形空白前面那段；後面是各語言備用
    private static string[] ItemTexts =>
        new[] { Addons.Text(2378).Split('　', '[')[0].Trim() }
            .Where(t => t.Length > 0)
            .Concat(new[] { "道具管理", "Entrust or withdraw items", "アイテムの受け渡し" })
            .ToArray();

    // 背包右鍵選單「交給僱員保管」（Addon 97）：整疊交給雇員，遊戲自己疊到雇員原本那一疊
    private const uint EntrustRow = 97;

    /// <summary>持有狀況的快取裡，HQ 物品的編號 = 原編號 + 這個數。</summary>
    private const uint HqOffset = 1_000_000;

    private static readonly InventoryType[] PlayerBags =
        { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 };

    private static readonly InventoryType[] RetainerPages =
    {
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7
    };

    /// <summary>
    /// 存入重複物品：背包（物品欄 1～4）裡的東西，雇員那邊已經有同一種（NQ 對 NQ、HQ 對 HQ），就交給那個雇員。
    /// 先用持有狀況的快取挑出要進哪些雇員；進去之後看雇員背包的即時資料，真的有才存（快取舊了就只是白跑一趟）。
    /// 套裝用到的裝備不存。
    /// </summary>
    public void DepositDuplicates() => Start("存入重複物品", DepositJob);

    private IEnumerable<object?> DepositJob()
    {
        var bag = BagItems();
        var cached = CachedRetainerItems();

        var plan = Retainers()
            .Select(r => (Retainer: r, Items: cached.TryGetValue(r.Id, out var has)
                ? bag.Where(b => has.Contains(CacheId(b.ItemId, b.Hq))).Select(b => (b.ItemId, b.Hq)).Distinct().ToList()
                : new List<(uint ItemId, bool Hq)>()))
            .Where(p => p.Items.Count > 0)
            .ToList();

        if (plan.Count == 0)
        {
            log.Write("存入重複物品：背包裡沒有雇員也有的物品（照持有狀況的快取；沒開過的雇員沒有資料）");
            Status = "沒有要存的物品";
            yield break;
        }

        log.Write($"存入重複物品：{string.Join("；", plan.Select(p => $"{p.Retainer.Name} {p.Items.Count} 種"))}");

        var total = 0;
        foreach (var (retainer, items) in plan)
        {
            if (!Running) yield break;

            Status = $"存入重複物品：{retainer.Name}";
            yield return Enter(retainer);
            if (!lastOk) continue;

            if (!Addons.ClickSelectString(ItemTexts))
            {
                log.Write($"{retainer.Name} 選項裡找不到道具管理：{string.Join(" | ", Addons.SelectStringEntries())}");
                yield return Leave();
                continue;
            }

            yield return Routine.Until(() => RetainerBagOpen() && RetainerBagLoaded(), 10);
            yield return Routine.Delay(0.5);

            if (!RetainerBagOpen() || !RetainerBagLoaded())
            {
                log.Write($"{retainer.Name} 的雇員背包沒有打開");
                yield return LeaveRetainerBag();
                continue;
            }

            var stored = 0;
            var full = false;
            foreach (var (item, hq) in items)
            {
                if (full || !Running) break;

                var name = ItemName(item) + (hq ? "（HQ）" : string.Empty);

                // 快取可能是舊的：雇員背包現在真的有才存
                if (RetainerCount(item, hq) == 0)
                {
                    log.Write($"{retainer.Name}：快取說有 {name}，但雇員背包裡其實沒有，不存");
                    continue;
                }

                while (Running && FindBagSlot(item, hq) is { } slot)
                {
                    if (!OpenItemMenu(slot.Type, slot.Slot))
                    {
                        log.Write($"{retainer.Name}：{name} 打不開右鍵選單");
                        break;
                    }

                    yield return Routine.Until(ItemMenuOpen, 3);

                    if (!ClickItemMenu(EntrustRow))
                    {
                        log.Write($"{retainer.Name}：{name} 的右鍵選單沒有「{Addons.Text(EntrustRow)}」（不能交給雇員的物品），不存");
                        CloseItemMenu();
                        break;
                    }

                    // 等那一格變了（拿走或數量變了）；沒變多半是雇員背包滿了
                    yield return Routine.Until(() => !SameSlot(slot), 3);
                    if (SameSlot(slot))
                    {
                        log.Write($"{retainer.Name}：{name} 存不進去（雇員背包可能滿了），換下一個雇員");
                        full = true;
                        break;
                    }

                    stored++;
                    log.Write($"{retainer.Name}：存入 {name} ×{slot.Quantity}");
                    yield return Routine.Delay(0.2);
                }
            }

            total += stored;
            yield return LeaveRetainerBag();
        }

        Status = total > 0 ? $"存入 {total} 疊" : "沒有存任何東西";
        log.Write($"存入重複物品完成：共 {total} 疊");
    }

    /// <summary>關掉雇員背包回到選項清單，再離開雇員。</summary>
    private IEnumerable<object?> LeaveRetainerBag()
    {
        CloseItemMenu();
        CloseIfOpen("InventoryRetainerLarge");
        CloseIfOpen("InventoryRetainer");
        yield return Routine.Until(() => Addons.IsOpen("SelectString"), 5);
        yield return Leave();
    }

    private readonly record struct BagItem(InventoryType Type, int Slot, uint ItemId, bool Hq, int Quantity);

    private static uint CacheId(uint item, bool hq) => hq ? item + HqOffset : item;

    /// <summary>背包（物品欄 1～4）裡的物品，套裝用到的裝備不算。</summary>
    private static List<BagItem> BagItems()
    {
        var result = new List<BagItem>();
        var inventory = InventoryManager.Instance();
        if (inventory == null) return result;

        var inSets = AetherKit.Tweaks.GlamourCommon.GearsetItemIds();
        foreach (var type in PlayerBags)
        {
            var container = inventory->GetInventoryContainer(type);
            if (container == null) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0 || inSets.Contains(slot->ItemId)) continue;

                result.Add(new BagItem(type, i, slot->ItemId, slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality), (int)slot->Quantity));
            }
        }

        return result;
    }

    /// <summary>持有狀況的快取：每個雇員（RetainerId）背包裡有哪些物品（HQ 用 +1,000,000 的編號）。</summary>
    private static Dictionary<ulong, HashSet<uint>> CachedRetainerItems()
    {
        var result = new Dictionary<ulong, HashSet<uint>>();
        var finder = FFXIVClientStructs.FFXIV.Client.UI.Misc.ItemFinderModule.Instance();
        if (finder == null) return result;

        foreach (var entry in finder->RetainerInventories)
        {
            var retainer = entry.Item2.Value;
            if (retainer == null) continue;

            var items = new HashSet<uint>();
            var length = Math.Min(retainer->ItemIds.Length, retainer->ItemCount.Length);
            for (var i = 0; i < length; i++)
            {
                if (retainer->ItemIds[i] != 0 && retainer->ItemCount[i] > 0)
                    items.Add(retainer->ItemIds[i]);
            }

            result[entry.Item1] = items;
        }

        return result;
    }

    private static BagItem? FindBagSlot(uint item, bool hq)
    {
        foreach (var b in BagItems())
        {
            if (b.ItemId == item && b.Hq == hq) return b;
        }

        return null;
    }

    /// <summary>那一格還是同一個物品、同樣數量（= 還沒存進去）。</summary>
    private static bool SameSlot(BagItem before)
    {
        var slot = InventoryManager.Instance()->GetInventoryContainer(before.Type)->GetInventorySlot(before.Slot);
        return slot != null && slot->ItemId == before.ItemId && slot->Quantity == before.Quantity;
    }

    private static int RetainerCount(uint item, bool hq)
    {
        var inventory = InventoryManager.Instance();
        var total = 0;
        foreach (var page in RetainerPages)
            total += inventory->GetItemCountInContainer(item, page, hq);

        return total;
    }

    private static bool RetainerBagOpen() => Addons.IsOpen("InventoryRetainer") || Addons.IsOpen("InventoryRetainerLarge");

    private static bool RetainerBagLoaded()
    {
        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.RetainerPage1);
        return container != null && container->IsLoaded;
    }

    /// <summary>對背包某一格開右鍵選單（跟自己按右鍵一樣的選單），掛在開著的物品欄視窗上。</summary>
    private static bool OpenItemMenu(InventoryType type, int slot)
    {
        var host = Addons.Get("InventoryExpansion");
        if (host == null) host = Addons.Get("InventoryLarge");
        if (host == null) host = Addons.Get("Inventory");

        var agent = AgentInventoryContext.Instance();
        if (agent == null) return false;

        // [遊戲函式] 開背包格子的右鍵選單（見 改版檢查清單.md）
        agent->OpenForItemSlot(type, slot, 0, host == null ? 0u : host->Id);
        return true;
    }

    private static AtkUnitBase* ItemMenu()
    {
        var agent = AgentInventoryContext.Instance();
        if (agent == null || !agent->AgentInterface.IsAgentActive()) return null;

        var menu = RaptureAtkUnitManager.Instance()->GetAddonById((ushort)agent->AgentInterface.GetAddonId());
        return menu != null && menu->IsVisible ? menu : null;
    }

    private static bool ItemMenuOpen() => ItemMenu() != null;

    /// <summary>點右鍵選單裡文字是 Addon 表那一列的選項（跟自己點一樣），點完收起選單。</summary>
    private static bool ClickItemMenu(uint row)
    {
        var menu = ItemMenu();
        var agent = AgentInventoryContext.Instance();
        if (menu == null || agent == null) return false;

        var text = Addons.Text(row);
        var index = AetherKit.Tweaks.ShiftQuickTransfer.MenuOptions(agent).FindIndex(o => o == text);
        if (text.Length == 0 || index < 0) return false;

        Addons.Fire(menu, 0, index, 0u, 0, 0);
        agent->AgentInterface.Hide();
        return true;
    }

    private static void CloseItemMenu()
    {
        var agent = AgentInventoryContext.Instance();
        if (agent != null && agent->AgentInterface.IsAgentActive()) agent->AgentInterface.Hide();
    }

    // ───────────────────────── 改價 ─────────────────────────

    /// <summary>
    /// 市場報價：別人的最低價（原始）、參考價（濾掉離群值後最低）、查的時間、別人上架的前幾筆（單價、數量）。
    /// 0 = 沒人在賣。
    /// </summary>
    public readonly record struct Quote(uint Lowest, uint Reference, DateTime At, IReadOnlyList<(uint Price, uint Quantity)> Listings);

    /// <summary>
    /// 全部收回給自己：出售清單上的每一件依序收回背包（直接呼叫遊戲的收回函式，同 DR）。
    /// 背包沒空位就停。
    /// </summary>
    private IEnumerable<object?> RetrieveAllJob()
    {
        foreach (var row in MarketRows())
        {
            if (!Running) yield break;

            yield return RetrieveRow(row.Slot, row.Item, row.Hq);
        }

        log.Write("全部收回完成");
    }

    /// <summary>價格表某一行的「收」：只收回這一件。</summary>
    public void RetrieveOne(int slot, uint item, bool hq) => Start("收回", () => RetrieveRow(slot, item, hq));

    /// <summary>收回一件給自己；背包滿了或收不回來就停止整個流程。</summary>
    private IEnumerable<object?> RetrieveRow(int slot, uint item, bool hq)
    {
        var name = ItemName(item) + (hq ? "（HQ）" : "");
        if (FreeBagSlots() == 0)
        {
            Stop("背包滿了");
            yield break;
        }

        Status = $"收回：{name}";
        RetrieveToInventory(slot);
        yield return Routine.Until(() => !SlotHasItem(slot), 3);

        if (SlotHasItem(slot))
        {
            log.Write($"{name} 收不回來（第 {slot} 格），停止");
            Stop($"{name} 收不回來");
            yield break;
        }

        log.Write($"收回 {name}");
        yield return Routine.Delay(0.3);
    }

    /// <summary>價格表的「清除快取」：查過的市場價全部丟掉，之後改價會重查。</summary>
    /// <summary>出售清單上方的「關閉」：關掉遊戲的出售清單（跟按視窗的關閉一樣）。</summary>
    public void CloseSellList() => Services.Framework.RunOnFrameworkThread(() => FireIfOpen("RetainerSellList", -1));

    public void ClearCache()
    {
        priceCache.Clear();
        log.Write("清除市場價快取");
    }

    /// <summary>
    /// 查一件物品的市場價，結果放進快取。快取還有效就不查（force = 一定重查）。
    /// 查市場太密集伺服器會直接回空的（實測一次成功、一次空白交替），所以兩次查詢之間至少隔 RequestGap；
    /// 回來是空的再查一次確認，才當作真的沒人在賣。
    /// </summary>
    private IEnumerable<object?> QueryMarket(uint item, bool hq, string name, bool force)
    {
        if (!force && CachedQuote(item, hq) != null) yield break;

        Status = $"查市場：{name}";
        var received = false;
        for (var attempt = 1; attempt <= 2 && !received; attempt++)
        {
            var wait = RequestGap - (DateTime.Now - requestedAt);
            if (wait > TimeSpan.Zero) yield return Routine.Delay(wait.TotalSeconds);

            RequestMarket(item);
            requestedAt = DateTime.Now;
            yield return Routine.Delay(0.3);
            yield return Routine.Until(() => MarketReady(item), 8);

            received = MarketReady(item) && MarketListingCount() > 0;
            if (!received) log.Write($"{name} 市場資料是空的（第 {attempt} 次）");
        }

        if (received)
        {
            var (lowest, reference) = MarketPrices(item, hq, name);
            priceCache[(item, hq)] = new Quote(lowest, reference, DateTime.Now, OtherListings(item, hq));
        }
        else
        {
            log.Write($"{name} 市場上沒有人在賣");
            priceCache[(item, hq)] = new Quote(0, 0, DateTime.Now, Array.Empty<(uint, uint)>());
        }
    }

    /// <summary>
    /// 改出售清單第 slot 格的價格（直接呼叫遊戲的改價函式，同 DR），改完讀回來確認。
    /// 價格表手動改、點欄位套用、自動改價都走這裡。只在遊戲執行緒呼叫。
    /// </summary>
    public void ApplyPrice(int slot, uint item, bool hq, uint before, uint price, string reason)
    {
        SetPrice(slot, price);

        var after = ReadPrice(slot);
        var name = ItemName(item) + (hq ? "（HQ）" : "");
        var retainer = ActiveRetainerName();

        if (after != price)
        {
            Warn($"{retainer} {name} 改成 {price:N0} 但讀回來是 {after:N0}，可能沒改成功");
            return;
        }

        log.Write($"{retainer} {name} {before:N0} → {price:N0}（{reason}）");
    }

    /// <summary>
    /// 價格表的「全部改價」：每一件都改成參考價（沒查過或過期的順便查）。
    /// 沒有任何保護機制，價格自己在價格表上看最低價、商店價判斷。
    /// </summary>
    public void AdjustAll() => Start("全部改價", AdjustAllJob);

    private IEnumerable<object?> AdjustAllJob()
    {
        foreach (var row in MarketRows())
        {
            if (!Running) yield break;

            yield return AdjustRow(row.Slot, row.Item, row.Hq);
        }
    }

    /// <summary>價格表某一行的「改」：只改這一件（沒查過或過期的順便查）。</summary>
    public void AdjustOne(int slot, uint item, bool hq) => Start("改價", () => AdjustRow(slot, item, hq));

    private IEnumerable<object?> AdjustRow(int slot, uint item, bool hq)
    {
        var name = ItemName(item) + (hq ? "（HQ）" : "");
        yield return QueryMarket(item, hq, name, false);

        if (CachedQuote(item, hq) is not { Reference: > 0 } quote) yield break;

        var current = ReadPrice(slot);
        var price = Undercut(quote.Reference);
        if (price == current) yield break;

        ApplyPrice(slot, item, hq, current, price, $"參考價（市場參考 {quote.Reference:N0}）");
        yield return Routine.Delay(0.2);
    }

    // ── 上架新東西：先用高價上架、排隊查價改價 ──

    /// <summary>目前開著的價格視窗已經處理過（關掉才重設）。</summary>
    private DateTime? confirmAt;

    /// <summary>剛用高價上架、還在等它出現在出售清單的（物品、HQ、上架時間）。</summary>
    private readonly List<(uint Item, bool Hq, DateTime At)> justListed = new();

    /// <summary>等著查價改價的新上架物品。</summary>
    private readonly Queue<(uint Item, bool Hq)> adjustQueue = new();

    /// <summary>排隊中（還沒改好）的件數，價格表顯示用。</summary>
    public int QueuedCount => adjustQueue.Count + justListed.Count;

    /// <summary>
    /// 上架新東西（價格表開著時）：價格視窗一打開就填最高價、按確定，馬上可以上架下一件；
    /// 上架後在出售清單找到那一件，排進佇列，背景一件件查市場、改成參考價（沒人在賣就留著高價，自己改）。
    /// 價格表收起來時不做任何事，照遊戲原本的方式。
    /// </summary>
    private void WatchSellWindow(DateTime now)
    {
        if (!Config.ShowPriceTable)
        {
            sellWindowHandled = false;
            confirmAt = null;
            return;
        }

        if (!Addons.IsOpen("RetainerSell"))
        {
            sellWindowHandled = false;
            confirmAt = null;
        }
        else if (!sellWindowHandled)
        {
            sellWindowHandled = true;
            if (ReadSellWindow() is { Item: > 0 } window)
            {
                SetAskingPrice(NoMarketPrice);
                confirmAt = now + TimeSpan.FromMilliseconds(200);
                justListed.Add((window.Item, window.Hq, now));
                log.Write($"上架 {window.Name}{(window.Hq ? "（HQ）" : "")}：先用最高價上架，排隊查價");
            }
            else
            {
                log.Write("價格視窗讀不到物品，不處理");
            }
        }
        else if (confirmAt is { } at && now >= at)
        {
            confirmAt = null;
            ClickSellButton(true);
        }

        // 剛上架的出現在出售清單了：排進佇列（5 秒都沒出現就放棄，例如自己按了取消）
        for (var i = justListed.Count - 1; i >= 0; i--)
        {
            var (item, hq, listedAt) = justListed[i];
            if (MarketRows().Any(r => r.Item == item && r.Hq == hq && IsPlaceholder(r.Price, r.Quantity)))
            {
                adjustQueue.Enqueue((item, hq));
                justListed.RemoveAt(i);
            }
            else if (now - listedAt > TimeSpan.FromSeconds(5))
            {
                justListed.RemoveAt(i);
            }
        }

        if (adjustQueue.Count > 0 && !Running && !Core.Lock.Busy)
            Start("新上架查價改價", AdjustQueueJob);
    }

    /// <summary>佇列裡的新上架物品一件件查市場、改成參考價；查價中又上架的會接著處理。</summary>
    private IEnumerable<object?> AdjustQueueJob()
    {
        while (Running && adjustQueue.Count > 0)
        {
            var (item, hq) = adjustQueue.Dequeue();
            var name = ItemName(item) + (hq ? "（HQ）" : "");
            yield return QueryMarket(item, hq, name, false);

            // 找還是高價的那一件（玩家可能已經自己改了）
            var row = MarketRows().FirstOrDefault(r => r.Item == item && r.Hq == hq && IsPlaceholder(r.Price, r.Quantity));
            if (row.Item == 0)
            {
                log.Write($"{name} 已經不是上架時的高價（可能自己改了），不動");
                continue;
            }

            if (CachedQuote(item, hq) is not { Reference: > 0 } quote)
            {
                log.Write($"{name} 市場上沒有別人在賣，留著上架時的高價，請到價格表自己改");
                continue;
            }

            ApplyPrice(row.Slot, item, hq, row.Price, Undercut(quote.Reference), $"新上架（市場參考 {quote.Reference:N0}）");
            yield return Routine.Delay(0.2);
        }
    }

    /// <summary>需要注意的情況（例如改完讀回來不對）：只記 log，不寫聊天欄。</summary>
    private void Warn(string message) => log.Write($"注意：{message}");

    // ───────────────────────── 進出雇員 ─────────────────────────

    /// <summary>從雇員清單點進這個雇員，等到選項清單出來（中間的對話交給對話推進）。</summary>
    private IEnumerable<object?> Enter(RetainerInfo retainer)
    {
        lastOk = false;
        yield return Routine.Until(() => Addons.IsOpen("RetainerList"), 10);
        if (!Addons.IsOpen("RetainerList"))
        {
            Stop("雇員清單沒有開著（請先在傳喚鈴打開雇員清單）");
            yield break;
        }

        FireIfOpen("RetainerList", 2, (int)retainer.Index, 0, 0);
        yield return Routine.Until(() => Addons.IsOpen("SelectString") && HasActiveRetainer(), 15);

        lastOk = Addons.IsOpen("SelectString") && HasActiveRetainer();
        if (!lastOk) log.Write($"進不去 {retainer.Name}（選項清單沒有出現）");
        else yield return Routine.Delay(0.3);
    }

    /// <summary>離開雇員回到雇員清單：選項清單按取消；途中跳出確認框記下文字後按「是」。</summary>
    private IEnumerable<object?> Leave()
    {
        lastOk = false;
        var deadline = DateTime.Now.AddSeconds(15);

        while (DateTime.Now < deadline)
        {
            // 回到清單：清單開著、選項清單和對話都沒了（遊戲不一定會清掉「目前的雇員」，所以不看它）
            if (Addons.IsOpen("RetainerList") && !Addons.IsOpen("SelectString") && !Addons.IsOpen("Talk"))
            {
                lastOk = true;
                yield return Routine.Delay(0.3);
                yield break;
            }

            if (AnswerYesno() || FireIfOpen("SelectString", -1))
            {
                yield return Routine.Delay(0.5);
                continue;
            }

            yield return null;
        }

        log.Write("回不到雇員清單");
    }

    // ───────────────────────── 遊戲操作（迭代器裡不能用指標，集中在這裡） ─────────────────────────

    private readonly record struct RetainerInfo(uint Index, ulong Id, string Name, ushort VentureId, uint VentureComplete, uint Gil, byte MarketItems);

    /// <summary>已僱用、可用的雇員，Index 是雇員清單上的順序。</summary>
    private static List<RetainerInfo> Retainers()
    {
        var list = new List<RetainerInfo>();
        var manager = RetainerManager.Instance();
        if (manager == null) return list;

        for (uint i = 0; i < manager->GetRetainerCount(); i++)
        {
            var r = manager->GetRetainerBySortedIndex(i);
            if (r == null || r->RetainerId == 0 || !r->Available) continue;

            list.Add(new RetainerInfo(i, r->RetainerId, r->NameString, r->VentureId, r->VentureComplete, r->Gil, r->MarketItemCount));
        }

        return list;
    }

    private static HashSet<ulong> OwnRetainerIds()
    {
        var ids = new HashSet<ulong>();
        var manager = RetainerManager.Instance();
        if (manager == null) return ids;

        for (uint i = 0; i < manager->GetRetainerCount(); i++)
        {
            var r = manager->GetRetainerBySortedIndex(i);
            if (r != null && r->RetainerId != 0) ids.Add(r->RetainerId);
        }

        return ids;
    }

    private static bool HasActiveRetainer()
    {
        var manager = RetainerManager.Instance();
        return manager != null && manager->GetActiveRetainer() != null;
    }

    private static string ActiveRetainerName()
    {
        var manager = RetainerManager.Instance();
        var active = manager == null ? null : manager->GetActiveRetainer();
        return active == null ? "雇員" : active->NameString;
    }

    private static bool FireIfOpen(string addonName, params object[] values)
    {
        var addon = Addons.Get(addonName);
        if (addon == null) return false;

        Addons.Fire(addon, values);
        return true;
    }

    private static void CloseIfOpen(string addonName)
    {
        var addon = Addons.Get(addonName);
        if (addon != null) addon->Close(true);
    }

    private bool AnswerYesno()
    {
        var yesno = Addons.Get("SelectYesno");
        if (yesno == null) return false;

        log.Write($"確認框按是：{Addons.YesnoPrompt(yesno)}");
        Addons.ClickYes(yesno);
        return true;
    }


    /// <summary>
    /// 出售清單上的每一件：格子編號、物品、HQ、現在的單價、數量（讀遊戲資料，不改）。
    /// 順序照遊戲出售清單畫面上的順序（從視窗資料裡的物品名稱排）；讀不到畫面順序時照格子順序。
    /// </summary>
    public static List<(int Slot, uint Item, bool Hq, uint Price, int Quantity)> MarketRows()
    {
        var rows = new List<(int Slot, uint Item, bool Hq, uint Price, int Quantity)>();
        var manager = InventoryManager.Instance();
        var container = manager->GetInventoryContainer(MarketContainer);
        if (container == null || !container->IsLoaded) return rows;

        for (var i = 0; i < MarketSlots && i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if (item == null || item->ItemId == 0) continue;

            rows.Add((i, item->ItemId, item->IsHighQuality(), (uint)manager->GetRetainerMarketPrice((short)i), item->Quantity));
        }

        // 照畫面順序排：畫面上依序出現的每一件，配給還沒配過、同物品同品質的格子
        var shown = SellListOrder();
        if (shown.Count == 0) return rows;

        var ordered = new List<(int Slot, uint Item, bool Hq, uint Price, int Quantity)>();
        var left = new List<(int Slot, uint Item, bool Hq, uint Price, int Quantity)>(rows);
        foreach (var (item, hq) in shown)
        {
            var index = left.FindIndex(r => r.Item == item && r.Hq == hq);
            if (index < 0) continue;

            ordered.Add(left[index]);
            left.RemoveAt(index);
        }

        ordered.AddRange(left);
        return ordered;
    }

    /// <summary>出售清單視窗資料裡由上到下的物品（名稱對回物品 ID、HQ）。</summary>
    private static List<(uint Item, bool Hq)> SellListOrder()
    {
        var order = new List<(uint, bool)>();
        var list = Addons.Get("RetainerSellList");
        if (list == null) return order;

        for (var i = 0; i < list->AtkValuesCount; i++)
        {
            var value = list->AtkValues[i];
            if (value.Type is not (FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String
                or FFXIVClientStructs.FFXIV.Component.GUI.ValueType.ManagedString
                or FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String8)) continue;
            if (value.String.Value == null) continue;

            var text = Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue;
            var hq = text.Contains(HqGlyph);
            var name = new string(text.Where(c => c != HqGlyph && !char.IsControl(c)).ToArray()).Trim();

            var id = ItemIdByName(name);
            if (id != 0) order.Add((id, hq));
        }

        return order;
    }

    private static uint ReadPrice(int slot) => (uint)InventoryManager.Instance()->GetRetainerMarketPrice((short)slot);

    // [遊戲函式] 改出售價格：InventoryManager.SetRetainerMarketPrice（同 DR，見 改版檢查清單.md）
    private static void SetPrice(int slot, uint price) =>
        InventoryManager.Instance()->SetRetainerMarketPrice((short)slot, price);

    /// <summary>快取裡還沒過期的市場報價；沒有或過期回傳 null。</summary>
    public Quote? CachedQuote(uint item, bool hq) =>
        priceCache.TryGetValue((item, hq), out var quote) && DateTime.Now - quote.At < CacheTtl ? quote : null;

    /// <summary>照設定的壓價金額，從參考價算出要掛的價格（價格表的「參考價」欄顯示這個）。</summary>
    public uint Undercut(uint reference) =>
        reference > (uint)Math.Max(0, Config.Undercut) ? reference - (uint)Math.Max(0, Config.Undercut) : 1u;

    // ── 價格視窗（上架、自己點修改價格時，點遊戲視窗填價格） ──

    /// <summary>價格視窗上的物品（從名稱對回物品資料）、HQ、視窗裡現在的價格。</summary>
    private static (uint Item, bool Hq, string Name, uint Price)? ReadSellWindow()
    {
        var addon = (AddonRetainerSell*)Addons.Get("RetainerSell");
        if (addon == null || addon->ItemName == null || addon->AskingPrice == null) return null;

        // 名稱裡夾著顏色之類的控制碼，要當遊戲文字解析，不能直接轉字串
        var raw = Dalamud.Game.Text.SeStringHandling.SeString.Parse(addon->ItemName->NodeText.AsSpan()).TextValue;
        var hq = raw.Contains(HqGlyph);
        var name = new string(raw.Where(c => c != HqGlyph && !char.IsControl(c)).ToArray()).Trim();

        return (ItemIdByName(name), hq, name, (uint)Math.Max(0, addon->AskingPrice->Value));
    }

    /// <summary>在價格視窗的價格欄填入價格（跟玩家輸入一樣，會連帶更新總價、手續費）。</summary>
    private static void SetAskingPrice(uint price)
    {
        var addon = (AddonRetainerSell*)Addons.Get("RetainerSell");
        if (addon == null || addon->AskingPrice == null) return;

        addon->AskingPrice->InnerSetValue((int)price, true, false);
    }

    /// <summary>價格視窗按確定（true）或取消（false）。</summary>
    private static void ClickSellButton(bool confirm)
    {
        var addon = (AddonRetainerSell*)Addons.Get("RetainerSell");
        if (addon == null) return;

        Addons.ClickButton(&addon->AtkUnitBase, confirm ? addon->Confirm : addon->Cancel);
    }

    private const char HqGlyph = '';

    private static Dictionary<string, uint>? itemsByName;

    /// <summary>物品名稱 → ID（只看能在市場賣的）。對不到回傳 0。</summary>
    private static uint ItemIdByName(string name)
    {
        if (itemsByName == null)
        {
            itemsByName = new Dictionary<string, uint>();
            foreach (var row in Services.Data.GetExcelSheet<Item>())
            {
                if (row.ItemSearchCategory.RowId == 0) continue;

                var itemName = row.Name.ExtractText();
                if (itemName.Length > 0) itemsByName.TryAdd(itemName, row.RowId);
            }
        }

        return itemsByName.TryGetValue(name, out var id) ? id : 0;
    }

    // [遊戲函式]／[改記憶體] 查市場：設好 InfoProxyItemSearch 要查的物品再 RequestData（同 DR，見 改版檢查清單.md）
    private static void RequestMarket(uint item)
    {
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null) return;

        proxy->EndRequest();
        proxy->ClearListData();
        proxy->EntryCount = 0;
        proxy->SearchItemId = item;
        proxy->RequestData();
    }

    /// <summary>市場資料到齊：筆數跟伺服器說的一樣；完全沒人賣時等 2.5 秒沒有新資料也算到齊。</summary>
    private bool MarketReady(uint item)
    {
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null || proxy->SearchItemId != item) return false;

        var received = 0;
        foreach (var listing in proxy->Listings)
        {
            if (listing.ItemId == item && listing.UnitPrice != 0) received++;
        }

        if (proxy->ListingCount > 0) return received >= proxy->ListingCount;

        return DateTime.Now - requestedAt > TimeSpan.FromSeconds(2.5);
    }

    private static int MarketListingCount()
    {
        var proxy = InfoProxyItemSearch.Instance();
        return proxy == null ? 0 : (int)proxy->ListingCount;
    }

    /// <summary>
    /// 別人（不含自己的雇員、不含展示用的）同品質、每個賣家各取最便宜一筆，由低到高。
    /// 市場資料一次就有全部上架的（不只最低一筆），所以這裡拿得到每個賣家的價格。
    /// 自己的雇員用雇員 ID 排除，另外再用市場資料附的賣家名稱比對雇員名稱（雙重確認）。
    /// </summary>
    private static List<uint> OtherSellerPrices(uint item, bool hq, out int ownCount)
    {
        ownCount = 0;
        var bySeller = new Dictionary<ulong, uint>();
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null) return new List<uint>();

        var own = OwnRetainerIds();
        var ownNames = OwnRetainerNames();
        foreach (var info in proxy->PlayerRetainers)
        {
            if (info.RetainerId != 0 && ownNames.Contains(info.Name.ToString()))
                own.Add(info.RetainerId);
        }

        foreach (var listing in proxy->Listings)
        {
            if (listing.ItemId != item || listing.UnitPrice == 0 || listing.IsMannequin || listing.IsHqItem != hq) continue;

            if (own.Contains(listing.SellingRetainerContentId))
            {
                ownCount++;
                continue;
            }

            var seller = listing.SellingRetainerContentId;
            if (!bySeller.TryGetValue(seller, out var price) || listing.UnitPrice < price)
                bySeller[seller] = listing.UnitPrice;
        }

        return bySeller.Values.OrderBy(p => p).ToList();
    }

    /// <summary>別人（不含自己雇員、不含展示用）同品質上架的前 10 筆：單價、數量，由低到高（價格表的提示用）。</summary>
    private static List<(uint Price, uint Quantity)> OtherListings(uint item, bool hq)
    {
        var list = new List<(uint, uint)>();
        var proxy = InfoProxyItemSearch.Instance();
        if (proxy == null) return list;

        var own = OwnRetainerIds();
        var ownNames = OwnRetainerNames();
        foreach (var info in proxy->PlayerRetainers)
        {
            if (info.RetainerId != 0 && ownNames.Contains(info.Name.ToString()))
                own.Add(info.RetainerId);
        }

        foreach (var listing in proxy->Listings)
        {
            if (listing.ItemId != item || listing.UnitPrice == 0 || listing.IsMannequin || listing.IsHqItem != hq) continue;
            if (own.Contains(listing.SellingRetainerContentId)) continue;

            list.Add((listing.UnitPrice, listing.Quantity));
        }

        return list.OrderBy(l => l.Item1).Take(10).ToList();
    }

    private static HashSet<string> OwnRetainerNames()
    {
        var names = new HashSet<string>();
        var manager = RetainerManager.Instance();
        if (manager == null) return names;

        for (uint i = 0; i < manager->GetRetainerCount(); i++)
        {
            var r = manager->GetRetainerBySortedIndex(i);
            if (r != null && r->RetainerId != 0) names.Add(r->NameString);
        }

        return names;
    }

    /// <summary>
    /// 要跟的價格：最便宜的幾個賣家裡，濾掉明顯過低的離群值後最低的那個；沒人在賣回傳 0。
    /// 離群值 = 比「最便宜 5 個賣家的中位數（偶數取偏低那個）」還低超過設定百分比，
    /// 例如別人都賣 1 萬、有人賣 100。離群值只能是少數：最多略過不到一半的賣家，
    /// 不足 3 家時不判斷（「一次最多降價」還是會擋）。
    /// </summary>
    private (uint Lowest, uint Reference) MarketPrices(uint item, bool hq, string name)
    {
        var prices = OtherSellerPrices(item, hq, out var ownCount);
        var own = ownCount > 0 ? $"（已排除自己雇員的 {ownCount} 筆）" : string.Empty;
        if (prices.Count == 0)
        {
            log.Write($"{name} 市場上沒有別人在賣{own}");
            return (0, 0);
        }

        var cheapest = prices.Take(5).ToList();
        log.Write($"{name} 市場（各賣家最低，前 {cheapest.Count} 家）：{string.Join(", ", cheapest.Select(p => p.ToString("N0")))}，共 {prices.Count} 家{own}");

        if (cheapest.Count < 3) return (prices[0], prices[0]);

        var median = cheapest[(cheapest.Count - 1) / 2];
        var floor = median * (100L - OutlierPercent) / 100;
        var maxIgnored = (cheapest.Count - 1) / 2;
        var ignored = prices.TakeWhile(p => p < floor).Take(maxIgnored).ToList();

        if (ignored.Count > 0)
            log.Write($"{name} 略過過低的 {ignored.Count} 筆（{string.Join(", ", ignored.Select(p => p.ToString("N0")))}），比中位數 {median:N0} 低超過 {OutlierPercent}%");

        return (prices[0], prices[ignored.Count]);
    }

    public static string ItemName(uint item) =>
        Services.Data.GetExcelSheet<Item>().GetRowOrDefault(item)?.Name.ExtractText() ?? $"#{item}";

    /// <summary>商店價：NPC 收購價（賣給 NPC 商店拿到的錢，HQ 多 10%）；不能賣給商店回傳 0。</summary>
    public static uint ShopPrice(uint item, bool hq)
    {
        var price = Services.Data.GetExcelSheet<Item>().GetRowOrDefault(item)?.PriceLow ?? 0;
        return hq ? (uint)Math.Ceiling(price * 1.1) : price;
    }

    private static HashSet<uint>? npcSoldItems;

    /// <summary>NPC 販售價（跟 NPC 商店買的價格）；NPC 不賣這個東西回傳 0。</summary>
    public static uint NpcSalePrice(uint item)
    {
        if (npcSoldItems == null)
        {
            npcSoldItems = new HashSet<uint>();
            foreach (var row in Services.Data.GetSubrowExcelSheet<GilShopItem>())
            foreach (var sub in row)
                npcSoldItems.Add(sub.Item.RowId);
        }

        return npcSoldItems.Contains(item) ? Services.Data.GetExcelSheet<Item>().GetRowOrDefault(item)?.PriceMid ?? 0 : 0;
    }

    /// <summary>判斷離群值用：比最便宜幾家的中位數低超過這麼多 % 的不跟。</summary>
    private const int OutlierPercent = 50;

    /// <summary>上架時市場上沒人在賣：先用這個金額上架（遊戲上限），之後在價格表自己改。</summary>
    private const uint NoMarketPrice = 999_999_999;

    /// <summary>
    /// 是不是上架時填的高價：遊戲限制「單價 × 數量」最多 999,999,999，所以一疊 52 個會變成單價 19,230,769。
    /// 單價到了 999,999,999 ÷ 數量（無條件捨去）就算。
    /// </summary>
    public static bool IsPlaceholder(uint price, int quantity) => price >= NoMarketPrice / (uint)Math.Max(quantity, 1);

    private static uint FreeBagSlots() => InventoryManager.Instance()->GetEmptySlotsInBag();

    // [改記憶體] 藏起／顯示遊戲的出售清單（整個視窗的根節點），價格表蓋在上面時用（見 改版檢查清單.md）
    private static void SetNativeSellList(bool visible)
    {
        var list = Addons.Get("RetainerSellList");
        if (list == null || list->RootNode == null) return;

        if (list->RootNode->IsVisible() != visible)
            list->RootNode->ToggleVisibility(visible);
    }

    private static bool SlotHasItem(int slot)
    {
        var container = InventoryManager.Instance()->GetInventoryContainer(MarketContainer);
        if (container == null || !container->IsLoaded || slot >= container->Size) return false;

        var item = container->GetInventorySlot(slot);
        return item != null && item->ItemId != 0;
    }

    // [遊戲函式] 收回給自己：InventoryManager.MoveFromRetainerMarketToPlayerInventory（同 DR，見 改版檢查清單.md）
    private static void RetrieveToInventory(int slot)
    {
        var manager = InventoryManager.Instance();
        var container = manager->GetInventoryContainer(MarketContainer);
        if (container == null || slot >= container->Size) return;

        var item = container->GetInventorySlot(slot);
        if (item == null || item->ItemId == 0) return;

        manager->MoveFromRetainerMarketToPlayerInventory(MarketContainer, (ushort)slot, (uint)item->Quantity);
    }
}
