using System;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 採集到收藏品時，遊戲問「確定要將下列道具收為收藏品嗎？」就自動按「是」。
/// 只是在核心的自動確認登記一條規則，提示文字從遊戲文字表讀，換語言也能用。
/// </summary>
public sealed class CollectableConfirm : Tweak
{
    // 「確定要將下列道具收為收藏品嗎？」
    private const uint PromptRow = 1463;

    private IDisposable? rule;

    public override string Id => "collectable-confirm";
    public override string Name => "自動確認採集收藏品";
    public override string Description => "採集收藏品時，「確定要將下列道具收為收藏品嗎？」自動按「是」。";
    public override TweakGroup Group => TweakGroup.Gathering;
    public override TweakKind Kind => TweakKind.Auto;

    protected override void OnEnable() =>
        rule = Core.Confirm.Register(Owner, ConfirmRule.YesnoWithText("採集收藏品確認", PromptRow));

    protected override void OnDisable()
    {
        rule?.Dispose();
        rule = null;
    }
}
