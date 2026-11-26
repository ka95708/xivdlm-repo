using System;

namespace AetherKit.Core;

/// <summary>
/// 對話推進：和 NPC 對話時自動點過一般對話。跟 TextAdvance 一樣是點對話框，不 hook 遊戲腳本。
/// 只在「事件中」作用，其他時候出現的對話框不碰；有選項的對話（SelectString）不在這裡處理，由各功能自己決定。
/// </summary>
public sealed unsafe class TalkAdvance : Capability
{
    private static readonly TimeSpan ClickInterval = TimeSpan.FromMilliseconds(150);

    private DateTime nextClick;

    public TalkAdvance(CoreServices core) : base(core)
    {
    }

    public override string Id => "talk";
    public override string Name => "對話推進";
    public override string Description => "和 NPC 對話時自動點過一般對話。有選項的對話不會自動選。";
    public override bool OverlapsTextAdvance => true;

    protected override void Update(DateTime now)
    {
        if (now < nextClick || !GameState.InEvent) return;

        var talk = Addons.Get("Talk");
        if (talk == null) return;

        Addons.ClickTalk(talk);
        nextClick = now + ClickInterval;
    }
}
