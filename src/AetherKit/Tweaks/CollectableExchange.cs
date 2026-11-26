using System;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 收藏品一鍵交：收藏品交易視窗上方加「全部交換」按鈕，目前職業分頁裡的收藏品一件一件按「交換」。
/// 遊戲跳出確認框（例如票據快滿了）就停下來，讓玩家自己決定。
/// </summary>
public sealed unsafe class CollectableExchange : LoopTweak
{
    // 收藏品清單（列表元件）與「交換」按鈕的節點編號
    private const uint ListNode = 31;
    private const uint ExchangeButton = 51;

    public override string Id => "collectable-exchange";
    public override string Name => "收藏品一鍵交";
    public override string Description => "收藏品交易視窗加「全部交換」按鈕，目前分頁的收藏品一件一件交換。";
    public override TweakGroup Group => TweakGroup.Items;
    public override string Where => "收藏品交易視窗上方";

    protected override string Addon => "CollectablesShop";
    protected override string StartLabel => "全部交換";

    protected override string? Step(AtkUnitBase* addon, DateTime now)
    {
        if (Addons.IsOpen("SelectYesno")) return "遊戲跳出確認框（可能是票據快滿了），停止，請自己決定";

        if (EventBusy)
        {
            Wait(Poll);
            return null;
        }

        var component = addon->GetComponentNodeById(ListNode);
        var list = component == null ? null : component->GetAsAtkComponentList();
        if (list == null) return "找不到收藏品清單，停止";

        var count = list->ListLength;

        // 按了交換：清單少一件就馬上交下一件（最多等 3 秒）
        if (sentAt is { } sent)
        {
            if (count >= countBefore && now - sent < DoneTimeout)
            {
                Wait(Poll);
                return null;
            }

            sentAt = null;
            Wait(TimeSpan.FromMilliseconds(100)); // 讓清單、按鈕更新
            return null;
        }

        if (count <= 0) return $"交換完成，共 {Done} 件";
        if (!Progress(count)) return "交換沒有進行（按鈕可能不能按），停止";

        Log.Write($"按交換（清單 {count} 件） 按鈕可按={Addons.ClickButton(addon, ExchangeButton)}");
        sentAt = now;
        countBefore = count;
        Wait(Poll);
        return null;
    }

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DoneTimeout = TimeSpan.FromSeconds(3);
    private DateTime? sentAt;
    private int countBefore;

    protected override string? CanStart(AtkUnitBase* addon)
    {
        sentAt = null;
        return null;
    }
}
