using System;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 仙人微彩：彩票視窗一打開就自動玩完。照期望值最高的策略翻 3 格、選線、確認、離開。
///
/// 操作都是送彩票視窗自己的 callback：翻格 (1, 格子)、選線 (2, 線)、離開 (-1)。
/// 第一次算最佳解大約要零點幾秒，放到背景算，算完下一幀再按，不會卡畫面。
/// </summary>
public sealed unsafe class MiniCactpot : Tweak
{
    private const string Addon = "LotteryDaily";

    private static readonly TimeSpan StepDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan ConfirmExitWindow = TimeSpan.FromSeconds(3);

    private DateTime nextStep;
    private DateTime confirmExitUntil;
    private Task<int>? thinking;
    private string thinkingFor = string.Empty;

    public override string Id => "mini-cactpot";
    public override string Name => "仙人微彩";
    public override string Description => "打開仙人微彩就自動玩：照期望值最高的策略翻格、選線、確認、離開。";
    public override TweakGroup Group => TweakGroup.GoldSaucer;
    public override TweakKind Kind => TweakKind.Auto;
    public override string Where => "金碟遊樂場的仙人微彩視窗";

    protected override void OnDisable()
    {
        thinking = null;
        confirmExitUntil = DateTime.MinValue;
    }

    public override void Update(DateTime now)
    {
        // 離開後遊戲會再問一次，短時間內按「是」
        if (now < confirmExitUntil)
        {
            var yesno = Addons.Get("SelectYesno");
            if (yesno != null && Addons.ClickYes(yesno)) confirmExitUntil = DateTime.MinValue;
        }

        if (now < nextStep) return;

        var unit = Addons.Get(Addon);
        if (unit == null)
        {
            thinking = null;
            return;
        }

        var addon = (AddonLotteryDaily*)unit;
        var board = new int[9];
        var revealed = 0;
        for (var i = 0; i < 9; i++)
        {
            board[i] = addon->GameNumbers[i];
            if (board[i] != 0) revealed++;
        }

        // 選完線，全部數字都翻開了：結算完畢，離開
        if (revealed == 9)
        {
            Addons.Fire(unit, -1);
            unit->Close(true);
            confirmExitUntil = now + ConfirmExitWindow;
            nextStep = now + StepDelay;
            return;
        }

        if (revealed >= CactpotSolver.RevealedBeforeLine)
        {
            var (line, expected) = CactpotSolver.BestLine(board);
            // [改記憶體] 視窗上選中的線（見 改版檢查清單.md）
            addon->UnkNumber3D4 = line;
            Addons.Fire(unit, 2, line);
            Notify.Info("仙人微彩", $"選第 {line + 1} 條線，期望約 {expected:0} MGP。");
            Log.Write($"盤面 {string.Join(",", board)} 選第 {line + 1} 條線，期望 {expected:0}");
            nextStep = now + StepDelay;
            return;
        }

        Reveal(unit, board, now);
    }

    /// <summary>背景算下一格該翻哪格，算完（而且盤面沒變）才按。</summary>
    private void Reveal(AtkUnitBase* unit, int[] board, DateTime now)
    {
        var key = string.Join(",", board);

        if (thinking == null || thinkingFor != key)
        {
            var copy = (int[])board.Clone();
            thinking = Task.Run(() => CactpotSolver.BestCell(copy));
            thinkingFor = key;
            return;
        }

        if (!thinking.IsCompleted) return;

        var cell = thinking.Result;
        thinking = null;
        if (cell < 0) return;

        Log.Write($"盤面 {string.Join(",", board)} 翻第 {cell} 格");
        Addons.Fire(unit, 1, (uint)cell);
        nextStep = now + StepDelay;
    }
}
