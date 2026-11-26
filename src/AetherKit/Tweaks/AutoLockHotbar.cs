using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 自動鎖定快捷列（Simple Tweaks 的 Auto Lock Action Bars）：進入戰鬥、換地圖時把快捷列鎖上（可選戰鬥結束解鎖），
/// 也可以一起鎖十字快捷列。用的是快捷列視窗上鎖頭按鈕的 callback，跟點鎖頭一樣。
/// </summary>
public sealed unsafe class AutoLockHotbar : Tweak
{
    public override string Id => "auto-lock-hotbar";
    public override string Name => "自動鎖定快捷列";
    public override string Description => "進入戰鬥、換地圖時自動鎖上快捷列，免得不小心把技能拖走。";
    public override TweakGroup Group => TweakGroup.Interface;
    public override TweakKind Kind => TweakKind.Auto;

    protected override void OnEnable()
    {
        Services.Condition.ConditionChange += OnConditionChange;
        Services.ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    protected override void OnDisable()
    {
        Services.Condition.ConditionChange -= OnConditionChange;
        Services.ClientState.TerritoryChanged -= OnTerritoryChanged;
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (flag != ConditionFlag.InCombat) return;

        if (value && Config.HotbarLockCombatStart) SetLock(true, "進入戰鬥");
        if (!value && Config.HotbarUnlockCombatEnd) SetLock(false, "戰鬥結束");
    }

    private void OnTerritoryChanged(ushort territory)
    {
        if (Config.HotbarLockZoneChange) SetLock(true, "換地圖");
    }

    private void SetLock(bool locked, string reason)
    {
        // 快捷列視窗鎖頭的 callback（跟點鎖頭一樣），十字快捷列另外一組值
        if (Addons.Get("_ActionBar") is var bar && bar != null)
            Addons.Fire(bar, 10, 3, 51u, 0u, locked);

        if (Config.HotbarLockCross && Addons.Get("_ActionCross") is var cross && cross != null)
            Addons.Fire(cross, 10, 4, 62u, 10u, locked);

        Log.Write($"{reason}：{(locked ? "鎖定" : "解鎖")}快捷列");
    }

    public override bool HasSettings => true;

    public override void DrawSettings()
    {
        var start = Config.HotbarLockCombatStart;
        if (ImGui.Checkbox("進入戰鬥時鎖定", ref start))
        {
            Config.HotbarLockCombatStart = start;
            Save();
        }

        var end = Config.HotbarUnlockCombatEnd;
        if (ImGui.Checkbox("戰鬥結束時解鎖", ref end))
        {
            Config.HotbarUnlockCombatEnd = end;
            Save();
        }

        var zone = Config.HotbarLockZoneChange;
        if (ImGui.Checkbox("換地圖時鎖定", ref zone))
        {
            Config.HotbarLockZoneChange = zone;
            Save();
        }

        var cross = Config.HotbarLockCross;
        if (ImGui.Checkbox("十字快捷列也一起", ref cross))
        {
            Config.HotbarLockCross = cross;
            Save();
        }
    }
}
