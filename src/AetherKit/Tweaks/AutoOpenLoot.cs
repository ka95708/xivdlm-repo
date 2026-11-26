using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 有新戰利品時打開拾取視窗（Simple Tweaks 的 Open loot window when items are added）：
/// 寶箱裡出現還沒擲的新物品，就像點畫面右邊的戰利品通知一樣打開拾取視窗；過場中先等，結束再開。
/// Simple Tweaks 是看聊天欄的「請擲骰」訊息（沒有台服的字），這裡改成直接看戰利品清單有沒有新東西。
/// </summary>
public sealed unsafe class AutoOpenLoot : Tweak
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(500);

    private readonly HashSet<(uint Chest, uint Index, uint Item)> seen = new();
    private DateTime nextCheck;
    private bool pending;

    public override string Id => "auto-open-loot";
    public override string Name => "有新戰利品時打開拾取視窗";
    public override string Description => "寶箱裡有新的戰利品要擲骰時，自動打開拾取視窗；過場中會等過場結束。";
    public override TweakGroup Group => TweakGroup.Duty;
    public override TweakKind Kind => TweakKind.Auto;

    protected override void OnEnable()
    {
        seen.Clear();
        pending = false;
    }

    public override void Update(DateTime now)
    {
        if (now < nextCheck) return;
        nextCheck = now + CheckInterval;

        var loot = Loot.Instance();
        if (loot == null) return;

        var current = new HashSet<(uint, uint, uint)>();
        foreach (var item in loot->Items)
        {
            if (item.ItemId == 0) continue;

            var key = (item.ChestObjectId, item.ChestItemIndex, item.ItemId);
            current.Add(key);

            // 新出現、還沒擲的
            if (!seen.Contains(key) && item.RollState is RollState.UpToNeed or RollState.UpToGreed or RollState.UpToPass)
                pending = true;
        }

        // 清單會清空重來，只記得目前還在的
        seen.Clear();
        seen.UnionWith(current);

        if (!pending) return;

        if (Addons.IsOpen("NeedGreed"))
        {
            pending = false;
            return;
        }

        if (GameState.InCutscene || GameState.BetweenAreas) return;

        var notification = Addons.Get("_Notification");
        if (notification == null) return;

        // 跟點畫面右邊的戰利品通知一樣
        Addons.Fire(notification, 0, 2);
        Log.Write("有新的戰利品，打開拾取視窗");
        pending = false;
    }
}
