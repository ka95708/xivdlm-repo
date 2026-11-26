using System;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 肖像更新確認：換裝備或存套裝後，遊戲跳出「要不要更新肖像」的預覽視窗（BannerPreview）就自動按更新。
/// 只是在核心的自動確認登記一條規則；按下去送的是預覽視窗自己的 callback 0，跟玩家按「更新」一樣。
/// </summary>
public sealed unsafe class PortraitUpdateConfirm : Tweak
{
    private const string Addon = "BannerPreview";

    private IDisposable? rule;

    public override string Id => "portrait-update-confirm";
    public override string Name => "肖像更新確認";
    public override string Description => "套裝連動的肖像要更新時，自動按「更新」。";
    public override TweakGroup Group => TweakGroup.Portrait;
    public override TweakKind Kind => TweakKind.Auto;
    public override string Where => "換裝備或儲存套裝後跳出的肖像更新視窗";

    protected override void OnEnable() =>
        rule = Core.Confirm.Register(Owner, new ConfirmRule("肖像更新確認", Addon, _ => true, Confirm));

    protected override void OnDisable()
    {
        rule?.Dispose();
        rule = null;
    }

    private bool Confirm(nint address)
    {
        Addons.Fire((AtkUnitBase*)address, 0);
        if (Config.PortraitUpdateNotify) Notify.Info("肖像更新確認", "已自動更新肖像。");
        return true;
    }

    public override bool HasSettings => true;

    public override void DrawSettings()
    {
        var notify = Config.PortraitUpdateNotify;
        if (ImGui.Checkbox("更新後在聊天欄提示", ref notify))
        {
            Config.PortraitUpdateNotify = notify;
            Save();
        }
    }
}
