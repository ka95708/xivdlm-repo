using System;
using System.Collections.Generic;
using AetherKit.Core;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AetherKit.Game;

/// <summary>
/// 精製魔晶石視窗開著時，把精煉度滿的裝備一件一件精製（「一鍵精製魔晶石」小功能、自動 FATE 共用）。
/// 每一件：點清單裡精煉度滿的那一項 → 確認框按「是」→ 等動作結束、精煉度歸零 → 下一件。
/// 目前的分類做完了就切到下一個分類（視窗事件 1），所有分類都看過還沒有就結束。全部用視窗操作。
/// </summary>
public sealed unsafe class MaterializeRunner
{
    public const string Addon = "Materialize";
    public const uint OpenAction = 14; // 一般動作「精製魔晶石」
    private const string DialogAddon = "MaterializeDialog";

    // 精煉度滿 = 10000
    private const int FullSpiritbond = 10000;

    // 精製視窗的分類數（裝備中、主手副手、頭身手、腿腳、耳頸、腕戒指）；切不過去的當作沒有
    private const int MaxCategories = 6;
    private const int MaxNoProgress = 3;

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    private static readonly InventoryType[] Containers =
    {
        InventoryType.EquippedItems,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
        InventoryType.ArmoryHands, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings
    };

    private readonly HashSet<int> emptyCategories = new();
    private int switchingTo = -1;
    private DateTime switchDeadline;
    private DateTime awaitDialogUntil;
    private DateTime awaitDoneUntil;
    private int countBefore;
    private int noProgress;

    /// <summary>這一輪精製了幾件。</summary>
    public int Done { get; private set; }

    /// <summary>下一次呼叫 Step 的時間。</summary>
    public DateTime NextStep { get; private set; }

    public void Reset()
    {
        emptyCategories.Clear();
        switchingTo = -1;
        awaitDialogUntil = DateTime.MinValue;
        awaitDoneUntil = DateTime.MinValue;
        noProgress = 0;
        Done = 0;
        NextStep = DateTime.MinValue;
    }

    /// <summary>推進一步。回傳 null = 還在做；回傳文字 = 結束（完成或停止的原因）。</summary>
    public string? Step(AtkUnitBase* addon, DateTime now, FeatureLog log)
    {
        if (now < NextStep) return null;
        NextStep = now + Poll;

        var dialog = Addons.Get(DialogAddon);
        if (dialog != null)
        {
            // 確認框一出來就按「是」，接著等這件精製完（精煉度滿的少一件）
            Addons.Fire(dialog, 0);
            awaitDialogUntil = DateTime.MinValue;
            awaitDoneUntil = now + TimeSpan.FromSeconds(10);
            countBefore = FullItems();
            return null;
        }

        // 點了裝備，等確認框出來；一直沒出來算沒進展
        if (awaitDialogUntil != DateTime.MinValue)
        {
            if (now < awaitDialogUntil) return null;

            awaitDialogUntil = DateTime.MinValue;
            if (++noProgress >= MaxNoProgress) return "精製一直沒有成功，停止";
        }

        // 按了「是」，等精製動作結束、裝備精煉度歸零
        if (awaitDoneUntil != DateTime.MinValue)
        {
            var done = FullItems() < countBefore;
            if (now < awaitDoneUntil && (GameState.InEvent || !done)) return null;

            awaitDoneUntil = DateTime.MinValue;
            if (done)
            {
                Done++;
                noProgress = 0;
                emptyCategories.Clear(); // 精製成功後換分類的紀錄重來
            }
            else if (++noProgress >= MaxNoProgress)
            {
                return "精製一直沒有成功，停止";
            }

            NextStep = now + TimeSpan.FromMilliseconds(100); // 讓清單更新
            return null;
        }

        if (GameState.InEvent || GameState.InCombat || GameState.Mounted) return null;

        var count = FullItems();
        if (count == 0) return $"精製完成，共 {Done} 件";
        if (InventoryManager.Instance()->GetEmptySlotsInBag() == 0) return "背包滿了，停止";

        var agent = AgentMaterialize.Instance();
        if (agent == null) return "讀不到精製視窗的資料，停止";

        // 切了分類，等清單換好
        if (switchingTo >= 0)
        {
            if (agent->Category != switchingTo)
            {
                if (now < switchDeadline) return null;

                log.Write($"切不到分類 {switchingTo}，跳過");
                emptyCategories.Add(switchingTo);
                switchingTo = -1;
                return null;
            }

            switchingTo = -1;
            NextStep = now + TimeSpan.FromMilliseconds(300);
            return null;
        }

        var index = FullIndexInList(agent);
        if (index < 0)
        {
            // 這個分類沒有了：換下一個；全部分類都看過就結束
            emptyCategories.Add(agent->Category);
            if (emptyCategories.Count >= MaxCategories)
                return $"所有分類都看過了，還有 {count} 件精煉度滿的裝備不在清單裡，停止";

            var next = (agent->Category + 1) % MaxCategories;
            while (emptyCategories.Contains(next)) next = (next + 1) % MaxCategories;

            log.Write($"分類 {agent->Category} 沒有精煉度滿的裝備，切到分類 {next}");
            Addons.Fire(addon, 1, next);
            switchingTo = next;
            switchDeadline = now + TimeSpan.FromSeconds(2);
            NextStep = now + TimeSpan.FromMilliseconds(300);
            return null;
        }

        log.Write($"精製 分類 {agent->Category} 清單第 {index + 1} 項（精煉度滿 {count} 件）");
        Addons.Fire(addon, 2, index);
        awaitDialogUntil = now + TimeSpan.FromSeconds(2);
        return null;
    }

    /// <summary>目前分類的清單（照畫面順序）裡第一件精煉度滿的位置；沒有 -1。</summary>
    private static int FullIndexInList(AgentMaterialize* agent)
    {
        for (var i = 0; i < agent->ItemCount && i < agent->ItemsSorted.Length; i++)
        {
            var entry = agent->ItemsSorted[i].Value;
            if (entry != null && entry->Item != null && entry->Item->SpiritbondOrCollectability >= FullSpiritbond)
                return i;
        }

        return -1;
    }

    /// <summary>身上和兵裝庫精煉度滿的裝備數。</summary>
    public static int FullItems()
    {
        var manager = InventoryManager.Instance();
        var count = 0;

        foreach (var type in Containers)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId != 0 && slot->SpiritbondOrCollectability >= FullSpiritbond)
                    count++;
            }
        }

        return count;
    }
}
