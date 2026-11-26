using System;
using AetherKit.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AetherKit.Tweaks;

/// <summary>
/// 一鍵精製魔晶石：精製魔晶石視窗上方加「全部精製」按鈕，精煉度滿的裝備一件一件精製，會自動切換分類。
/// 實際流程在 <see cref="MaterializeRunner"/>（自動 FATE 也用它）。
/// </summary>
public sealed unsafe class Materialize : LoopTweak
{
    private readonly MaterializeRunner runner = new();

    public override string Id => "materialize";
    public override string Name => "一鍵精製魔晶石";
    public override string Description => "精製魔晶石視窗加「全部精製」按鈕，精煉度滿的裝備一件一件精製，會自動切換分類，全部做完。";
    public override TweakGroup Group => TweakGroup.Items;
    public override string Where => "精製魔晶石視窗上方";

    protected override string Addon => MaterializeRunner.Addon;
    protected override string StartLabel => "全部精製";

    protected override string? CanStart(AtkUnitBase* addon)
    {
        runner.Reset();
        return null;
    }

    protected override string? Step(AtkUnitBase* addon, DateTime now)
    {
        var before = runner.Done;
        var result = runner.Step(addon, now, Log);
        if (runner.Done > before) CountDone(runner.Done - before);
        Wait(runner.NextStep - now);
        return result;
    }
}
