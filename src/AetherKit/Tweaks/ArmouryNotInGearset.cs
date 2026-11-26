using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 移出不在套裝的裝備：兵裝庫裡沒有被任何套裝（套裝列表）用到的裝備，一件一件放回背包。
///
/// 每一件：在兵裝庫對這件按右鍵（打開跟玩家右鍵一樣的選單）→ 選「放入背包」→ 下一件。
/// 比對時不分 HQ／NQ，同名的有一件在套裝裡就整批留著，寧可少移也不要移錯。
/// </summary>
public sealed unsafe class ArmouryNotInGearset : LoopTweak
{
    private const string MenuAddon = "ContextMenu";

    // 遊戲文字表裡兵裝庫右鍵選單的「放入背包」
    private const uint ToBagTextRow = 1388;

    private bool menuOpened;

    public override string Id => "armoury-not-in-gearset";
    public override string Name => "移出不在套裝的裝備";
    public override string Description => "兵裝庫加「移出不在套裝的」按鈕，沒有被任何套裝用到的裝備一件一件放回背包。";
    public override TweakGroup Group => TweakGroup.Glamour;
    public override string Where => "兵裝庫視窗上方";

    protected override string Addon => "ArmouryBoard";
    protected override string StartLabel => "移出不在套裝的";

    protected override string? CanStart(AtkUnitBase* addon)
    {
        menuOpened = false;
        if (GlamourCommon.GearsetItemIds().Count == 0) return "讀不到任何套裝，為了安全不動作";
        if (Candidates().Count == 0) return "兵裝庫裡沒有不在套裝的裝備";
        return null;
    }

    protected override string? Step(AtkUnitBase* addon, DateTime now)
    {
        var menu = Addons.Get(MenuAddon);
        if (menu != null)
        {
            if (!menuOpened) return "有別的右鍵選單開著，停止";

            menuOpened = false;
            var label = Addons.Text(ToBagTextRow);
            var index = GlamourCommon.FindMenuEntry(menu, label);
            if (index < 0)
            {
                menu->Close(true);
                return $"右鍵選單裡沒有「{label}」，停止";
            }

            Log.Write($"選單第 {index} 項「{label}」");
            GlamourCommon.ClickMenuEntry(menu, index);
            Wait(TimeSpan.FromMilliseconds(500));
            return null;
        }

        if (GameState.InEvent || GameState.InCombat)
        {
            Wait(TimeSpan.FromMilliseconds(200));
            return null;
        }

        var candidates = Candidates();
        if (candidates.Count == 0) return $"完成，共放回背包 {Done} 件";
        if (!Progress(candidates.Count)) return "放回背包沒有進行，停止";
        if (FreeSlots() == 0) return $"背包滿了，停止（已放回 {Done} 件）";

        var (type, slot, itemId) = candidates[0];
        Log.Write($"右鍵 {type} 第 {slot} 格 道具={itemId}（剩 {candidates.Count} 件）");
        AgentInventoryContext.Instance()->OpenForItemSlot(type, slot, 0, addon->Id);
        menuOpened = true;
        Wait(TimeSpan.FromMilliseconds(300));
        return null;
    }

    /// <summary>兵裝庫裡不在任何套裝的裝備（欄位、格子、道具編號）。</summary>
    private static List<(InventoryType Type, int Slot, uint ItemId)> Candidates()
    {
        var result = new List<(InventoryType, int, uint)>();
        var inSets = GlamourCommon.GearsetItemIds();
        if (inSets.Count == 0) return result;

        var manager = InventoryManager.Instance();
        foreach (var type in GlamourCommon.Armoury)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;

            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0) continue;

                if (!inSets.Contains(GlamourCommon.BaseId(slot->ItemId)))
                    result.Add((type, i, slot->ItemId));
            }
        }

        return result;
    }
}
