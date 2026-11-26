using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AetherKit.Tweaks;

/// <summary>
/// 拾取後自動選下一件（Simple Tweaks 的 Loot Window Select Next Item）：
/// 拾取視窗打開時先選第一件還沒擲的；按了需求／貪婪／放棄之後自動選下一件。
/// 選項目用的是跟滑鼠點清單一樣的事件。
/// </summary>
public sealed unsafe class LootSelectNext : Tweak
{
    private const string Addon = "NeedGreed";

    // 拾取視窗按鈕的事件參數：0 需求、1 貪婪、2 放棄
    private const int NeedButton = 0;
    private const int GreedButton = 1;
    private const int PassButton = 2;

    // 按鈕的滑鼠放開事件
    private const int ButtonClick = 25;

    // 清單項目被點的事件
    private const int ListItemClick = 35;

    public override string Id => "loot-select-next";
    public override string Name => "拾取後自動選下一件";
    public override string Description => "拾取視窗打開時選第一件還沒擲的；按了需求、貪婪或放棄之後自動選下一件。";
    public override TweakGroup Group => TweakGroup.Duty;
    public override TweakKind Kind => TweakKind.Auto;
    public override string Where => "拾取視窗（需求／貪婪）";

    protected override void OnEnable()
    {
        Services.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, Addon, OnSetup);
        Services.AddonLifecycle.RegisterListener(AddonEvent.PostReceiveEvent, Addon, OnReceiveEvent);
    }

    protected override void OnDisable()
    {
        Services.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, Addon, OnSetup);
        Services.AddonLifecycle.UnregisterListener(AddonEvent.PostReceiveEvent, Addon, OnReceiveEvent);
    }

    private void OnSetup(AddonEvent type, AddonArgs args)
    {
        var addon = (AddonNeedGreed*)args.Addon.Address;
        if (addon == null) return;

        var first = Enumerable.Range(0, addon->NumItems)
            .FirstOrDefault(i => addon->Items[i].Roll == 0 && addon->Items[i].ItemId != 0, -1);
        if (first < 0) return;

        Log.Write($"拾取視窗打開，選第 {first + 1} 件");
        Select(addon, first);
    }

    private void OnReceiveEvent(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonReceiveEventArgs e || e.AtkEventType != ButtonClick) return;

        var addon = (AddonNeedGreed*)args.Addon.Address;
        if (addon == null) return;

        var button = e.EventParam;
        if (button == PassButton)
        {
            // 已經擲過的那件按放棄不算（遊戲會忽略）
            var item = addon->Items[addon->SelectedItemIndex];
            if (item.Roll != 0 || item.ItemId == 0) return;
        }
        else if (button is not (NeedButton or GreedButton))
        {
            return;
        }

        var next = addon->SelectedItemIndex + 1;
        if (next < addon->NumItems)
            Select(addon, next);
    }

    private static void Select(AddonNeedGreed* addon, int index)
    {
        var data = new AtkEventData();
        data.ListItemData.SelectedIndex = index;
        addon->AtkUnitBase.ReceiveEvent((AtkEventType)ListItemClick, 0, null, &data);
    }
}
