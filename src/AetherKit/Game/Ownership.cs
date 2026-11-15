using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;

namespace AetherKit.Game;

/// <summary>
/// 某個地方放了幾個。Seen = 資料是什麼時候看到的；null = 即時的，或不知道。
/// OnHand = 身上的（背包、裝備中、兵裝庫），也就是遊戲資訊框右上角「持有數量」算的部分。
/// </summary>
public readonly record struct OwnershipEntry(string Label, int Normal, int Hq, DateTime? Seen, bool OnHand = false)
{
    public int Total => Normal + Hq;
}

/// <summary>
/// 算一個物品放在哪裡、有幾個。資料跟遊戲右鍵「查看持有狀況」是同一份：
/// 身上的容器直接讀，雇員、鞍囊、投影台讀遊戲的 ItemFinderModule 快取
/// （遊戲自己會在打開它們時更新，而且存在本機，重開遊戲也還在）。
///
/// 快取不記時間，所以這裡另外記每個來源最後一次被打開的時間，顯示時才知道資料新不新。
/// 不呼叫遊戲的 SearchForItem — 台服呼叫它會讓遊戲當掉，而且讀快取就夠了。
/// </summary>
public sealed unsafe class Ownership
{
    /// <summary>HQ 物品在快取裡的 ID = 原 ID + 這個數。</summary>
    private const uint HqOffset = 1_000_000;

    /// <summary>同一個來源最多多久記一次時間，免得每秒都存檔。</summary>
    private static readonly TimeSpan SeenInterval = TimeSpan.FromMinutes(1);

    private static readonly InventoryType[] Bags =
    {
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.Crystals,
    };

    private static readonly InventoryType[] Armory =
    {
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
        InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs,
        InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist, InventoryType.ArmoryRings, InventoryType.ArmorySoulCrystal,
    };

    private static readonly InventoryType[] SaddleBags =
    {
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    };

    private readonly Plugin plugin;
    private Dictionary<uint, uint>? cabinetRows;
    private DateTime nextTrack;

    public Ownership(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private ItemOwnershipConfig Config => plugin.Config.ItemOwnership;

    public static string SeenKey(string source, ulong id) => $"{source}:{id:X}";

    /// <summary>每秒看一次哪些來源正開著，開著就表示快取剛被遊戲更新，記下時間。</summary>
    public void Track()
    {
        var now = DateTime.UtcNow;
        if (now < nextTrack) return;
        nextTrack = now.AddSeconds(1);

        var inventory = InventoryManager.Instance();
        var character = Services.PlayerState.ContentId;
        if (inventory == null || character == 0) return;

        RememberRetainerNames();

        var retainers = RetainerManager.Instance();
        var active = retainers != null ? retainers->GetActiveRetainer() : null;
        if (active != null && IsLoaded(inventory, InventoryType.RetainerPage1))
            MarkSeen(SeenKey("retainer", active->RetainerId), now);

        if (IsLoaded(inventory, InventoryType.SaddleBag1))
            MarkSeen(SeenKey("saddlebag", character), now);

        var mirage = MirageManager.Instance();
        if (mirage != null && mirage->PrismBoxLoaded)
            MarkSeen(SeenKey("dresser", character), now);
    }

    private void MarkSeen(string key, DateTime now)
    {
        if (Config.LastSeen.TryGetValue(key, out var seen) && now - seen < SeenInterval)
            return;

        Config.LastSeen[key] = now;
        plugin.Save();
    }

    private static bool IsLoaded(InventoryManager* inventory, InventoryType type)
    {
        var container = inventory->GetInventoryContainer(type);
        return container != null && container->IsLoaded;
    }

    /// <summary>itemId 是不含 HQ 偏移的原始 ID。</summary>
    public List<OwnershipEntry> Find(uint itemId)
    {
        var result = new List<OwnershipEntry>();
        var inventory = InventoryManager.Instance();
        if (inventory == null) return result;

        var character = Services.PlayerState.ContentId;

        Add(result, "背包", Sum(inventory, Bags, itemId, false), Sum(inventory, Bags, itemId, true), null, true);
        Add(result, "裝備中",
            Sum(inventory, [InventoryType.EquippedItems], itemId, false),
            Sum(inventory, [InventoryType.EquippedItems], itemId, true), null, true);
        Add(result, "兵裝庫", Sum(inventory, Armory, itemId, false), Sum(inventory, Armory, itemId, true), null, true);

        var finder = ItemFinderModule.Instance();

        // 鞍囊開著就讀即時的，不然讀快取
        if (IsLoaded(inventory, InventoryType.SaddleBag1))
        {
            Add(result, "鞍囊", Sum(inventory, SaddleBags, itemId, false), Sum(inventory, SaddleBags, itemId, true), null);
        }
        else if (finder != null)
        {
            var (nq, hq) = CountIn(finder->SaddleBagItemIds, finder->SaddleBagItemCount, itemId);
            var (pnq, phq) = CountIn(finder->PremiumSaddleBagItemIds, finder->PremiumSaddleBagItemCount, itemId);
            Add(result, "鞍囊", nq + pnq, hq + phq, Seen(SeenKey("saddlebag", character)));
        }

        AddDresser(result, finder, itemId, character);
        AddCabinet(result, itemId);

        if (finder != null)
            AddRetainers(result, finder, itemId);

        return result;
    }

    private void AddDresser(List<OwnershipEntry> result, ItemFinderModule* finder, uint itemId, ulong character)
    {
        var mirage = MirageManager.Instance();

        Span<uint> ids;
        DateTime? seen;

        if (mirage != null && mirage->PrismBoxLoaded)
        {
            ids = mirage->PrismBoxItemIds;
            seen = null;
        }
        else if (finder != null)
        {
            ids = finder->GlamourDresserItemIds;
            seen = Seen(SeenKey("dresser", character));
        }
        else
        {
            return;
        }

        int nq = 0, hq = 0;
        foreach (var id in ids)
        {
            if (id == itemId) nq++;
            else if (id == itemId + HqOffset) hq++;
        }

        Add(result, "投影台", nq, hq, seen);
    }

    /// <summary>收藏櫃只記有沒有收，沒有數量。收藏櫃的資料要在這次登入打開過才讀得到。</summary>
    private void AddCabinet(List<OwnershipEntry> result, uint itemId)
    {
        cabinetRows ??= BuildCabinetRows();
        if (!cabinetRows.TryGetValue(itemId, out var row)) return;

        var ui = UIState.Instance();
        if (ui == null || !ui->Cabinet.IsCabinetLoaded()) return;

        if (ui->Cabinet.IsItemInCabinet((int)row))
            result.Add(new OwnershipEntry("收藏櫃", 1, 0, null));
    }

    private static Dictionary<uint, uint> BuildCabinetRows()
    {
        var rows = new Dictionary<uint, uint>();

        foreach (var row in Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Cabinet>())
        {
            if (row.Item.RowId != 0)
                rows[row.Item.RowId] = row.RowId;
        }

        return rows;
    }

    private void AddRetainers(List<OwnershipEntry> result, ItemFinderModule* finder, uint itemId)
    {
        var names = Config.RetainerNames;

        foreach (var entry in finder->RetainerInventories)
        {
            var retainer = entry.Item2.Value;
            if (retainer == null) continue;

            var (nq, hq) = CountIn(retainer->ItemIds, retainer->ItemCount, itemId);

            foreach (var id in retainer->EquippedItemIds)
            {
                if (id == itemId) nq++;
                else if (id == itemId + HqOffset) hq++;
            }

            // 水晶另外存：陣列照水晶 ID（2–19）排
            var crystal = (int)itemId - 2;
            if (crystal >= 0 && crystal < retainer->CrystalQuantities.Length)
                nq += retainer->CrystalQuantities[crystal];

            var label = names.TryGetValue(entry.Item1, out var name) ? name : "雇員";
            Add(result, label, nq, hq, Seen(SeenKey("retainer", entry.Item1)));
        }
    }

    private DateTime? Seen(string key) =>
        Config.LastSeen.TryGetValue(key, out var seen) ? seen : null;

    private static void Add(List<OwnershipEntry> result, string label, int nq, int hq, DateTime? seen, bool onHand = false)
    {
        if (nq + hq > 0)
            result.Add(new OwnershipEntry(label, nq, hq, seen, onHand));
    }

    private static int Sum(InventoryManager* inventory, InventoryType[] types, uint itemId, bool hq)
    {
        var total = 0;
        foreach (var type in types)
            total += inventory->GetItemCountInContainer(itemId, type, hq);

        return total;
    }

    private static (int Normal, int Hq) CountIn(Span<uint> ids, Span<ushort> counts, uint itemId)
    {
        int nq = 0, hq = 0;
        var length = Math.Min(ids.Length, counts.Length);

        for (var i = 0; i < length; i++)
        {
            if (ids[i] == itemId) nq += counts[i];
            else if (ids[i] == itemId + HqOffset) hq += counts[i];
        }

        return (nq, hq);
    }

    /// <summary>遊戲的雇員資料有內容時，把名字記進設定檔（改名也會跟著更新）。</summary>
    private void RememberRetainerNames()
    {
        var manager = RetainerManager.Instance();
        if (manager == null) return;

        var names = Config.RetainerNames;

        foreach (var retainer in manager->Retainers)
        {
            if (retainer.RetainerId == 0) continue;

            var name = retainer.NameString;
            if (string.IsNullOrEmpty(name) || names.TryGetValue(retainer.RetainerId, out var known) && known == name)
                continue;

            names[retainer.RetainerId] = name;
            plugin.Save();
        }
    }
}
