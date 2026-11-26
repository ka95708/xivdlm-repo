using System;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 一鍵精選：精選清單上方加「全部精選」按鈕，清單裡的收藏品一件一件精選掉。
/// 每一件交給精選的 Agent 處理（跟在清單上點那一項一樣），精選動作一結束、清單少一件就馬上做下一件（不固定等待）。
/// </summary>
public sealed unsafe class AetherialReduction : LoopTweak
{
    public override string Id => "aetherial-reduction";
    public override string Name => "一鍵精選";
    public override string Description => "精選清單加「全部精選」按鈕，清單裡的東西一件一件精選。";
    public override TweakGroup Group => TweakGroup.Items;
    public override string Where => "精選清單上方";

    protected override string Addon => "PurifyItemSelector";
    protected override string StartLabel => "全部精選";

    protected override string? Step(AtkUnitBase* addon, DateTime now)
    {
        var agent = AgentPurify.Instance();
        if (agent == null) return "找不到精選清單，停止";

        var count = (int)agent->ReducibleItems.Count;

        // 精選了一件：等動作結束、清單少一件就馬上做下一件（最多等 10 秒）
        if (sentAt is { } sent)
        {
            if ((GameState.InEvent || count >= countBefore) && now - sent < DoneTimeout)
            {
                Wait(Poll);
                return null;
            }

            sentAt = null;
            Wait(TimeSpan.FromMilliseconds(100)); // 讓清單更新
            return null;
        }

        if (GameState.InEvent || GameState.InCombat || GameState.Mounted)
        {
            Wait(Poll);
            return null;
        }

        if (!Progress(count)) return "精選沒有開始，停止";
        if (count == 0) return $"精選完成，共 {Done} 件";
        if (FreeSlots() == 0) return "背包滿了，停止";

        var first = agent->ReducibleItems.First;
        if (first == null) return $"精選完成，共 {Done} 件";

        var item = InventoryManager.Instance()->GetInventorySlot(first->Inventory, first->Slot);
        if (item == null) return "找不到要精選的道具，停止";

        Log.Write($"精選 道具={item->ItemId}（剩 {count} 件）");
        agent->ReduceItem(item);
        sentAt = now;
        countBefore = count;
        Wait(Poll);
        return null;
    }

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DoneTimeout = TimeSpan.FromSeconds(10);
    private DateTime? sentAt;
    private int countBefore;

    protected override string? CanStart(AtkUnitBase* addon)
    {
        sentAt = null;
        return null;
    }
}
