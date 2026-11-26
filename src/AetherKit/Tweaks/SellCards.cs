using System;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 一鍵賣幻卡：幻卡交換視窗上方加「全部賣出」按鈕，清單上的幻卡一種一種全數換成 MGP。
/// 每一種：點清單第一項 → 數量視窗填最大數量、確定 → 下一種。
/// </summary>
public sealed unsafe class SellCards : LoopTweak
{
    private const string DialogAddon = "ShopCardDialog";

    // 清單視窗：第 1 個值是可交換的幻卡種類數，第 204 個值是現在能不能交換
    private const int CountIndex = 1;
    private const int CanExchangeIndex = 204;

    // 數量視窗：第 6 個值是最多能交換幾張
    private const int MaxQuantityIndex = 6;

    public override string Id => "sell-cards";
    public override string Name => "一鍵賣幻卡";
    public override string Description => "幻卡交換視窗加「全部賣出」按鈕，清單上的幻卡全數換成 MGP。";
    public override TweakGroup Group => TweakGroup.GoldSaucer;
    public override string Where => "金碟遊樂場幻卡交換視窗上方";

    protected override string Addon => "TripleTriadCoinExchange";
    protected override string StartLabel => "全部賣出";
    protected override string Unit => "種";

    protected override string? Step(AtkUnitBase* addon, DateTime now)
    {
        var dialog = Addons.Get(DialogAddon);
        if (dialog != null)
        {
            var quantity = dialog->AtkValuesCount > MaxQuantityIndex ? dialog->AtkValues[MaxQuantityIndex].UInt : 1u;
            Log.Write($"賣出 {quantity} 張");
            Addons.Fire(dialog, 0, quantity);
            dialog->Close(true);
            Wait(TimeSpan.FromMilliseconds(500));
            return null;
        }

        var count = Addons.ValueInt(addon, CountIndex);
        if (count == 0) return $"賣出完成，共 {Done} 種";
        if (!Progress(count)) return "賣出沒有進行，停止";

        if (addon->AtkValuesCount > CanExchangeIndex && addon->AtkValues[CanExchangeIndex].Byte == 0)
            return "遊戲顯示目前不能交換，停止";

        Log.Write($"選第一種（剩 {count} 種）");
        Addons.Fire(addon, 0, 0, 0);
        Wait(TimeSpan.FromMilliseconds(500));
        return null;
    }
}
