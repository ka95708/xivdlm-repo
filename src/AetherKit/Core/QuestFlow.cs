using System;

namespace AetherKit.Core;

/// <summary>
/// 任務接受與完成：任務說明視窗按「接受」，任務完成視窗按「完成」。
/// 有可選報酬時遊戲會讓「完成」按鈕維持灰色，這裡不替你選報酬（之後再做），
/// 而是提示一次，讓你自己選完再按。
/// </summary>
public sealed unsafe class QuestFlow : Capability
{
    // 兩個視窗的按鈕節點編號
    private const uint AcceptButton = 44;
    private const uint CompleteButton = 37;

    private static readonly TimeSpan ClickInterval = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan RewardHintAfter = TimeSpan.FromSeconds(2);

    private DateTime nextClick;
    private DateTime resultSince;
    private bool hinted;

    public QuestFlow(CoreServices core) : base(core)
    {
    }

    public override string Id => "quest";
    public override string Name => "任務接受／完成";
    public override string Description => "自動按任務的「接受」與「完成」。有可選報酬時會提示你自己選。";
    public override CapabilityMode DefaultMode => CapabilityMode.OnLease;
    public override bool OverlapsTextAdvance => true;

    protected override void Update(DateTime now)
    {
        if (now < nextClick) return;

        var accept = Addons.Get("JournalAccept");
        if (accept != null && Addons.ClickButton(accept, AcceptButton))
        {
            nextClick = now + ClickInterval;
            return;
        }

        var result = Addons.Get("JournalResult");
        if (result == null)
        {
            resultSince = DateTime.MinValue;
            hinted = false;
            return;
        }

        if (resultSince == DateTime.MinValue) resultSince = now;

        if (Addons.ClickButton(result, CompleteButton))
        {
            nextClick = now + ClickInterval;
            return;
        }

        if (!hinted && now - resultSince > RewardHintAfter)
        {
            hinted = true;
            Notify.Attention("任務", "這個任務有可選報酬，請自己選好再按「完成」。");
        }
    }
}
