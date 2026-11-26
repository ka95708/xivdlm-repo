using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.Exd;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using AetherKit.UI;

namespace AetherKit.Tweaks;

/// <summary>
/// 拾取視窗標示（Simple Tweaks 的 Enhanced Loot Window）：在拾取視窗每件物品的圖示上標出
/// - 叉叉：拿不到（獨佔物品身上已經有、或坐騎／寵物等已經學過），擲了也沒用；
/// - 鎖頭：已經學過／解鎖過（例如樂譜、幻卡）。
/// Simple Tweaks 是在遊戲視窗裡加節點；這裡畫在拾取視窗上面（只畫在圖示上，不擋名稱），
/// 捲到看不到的那幾件不畫。
/// </summary>
public sealed unsafe class LootWindowMarks : Tweak
{
    private const string Addon = "NeedGreed";

    // 拾取視窗的物品清單節點
    private const uint ListNode = 6;

    // 叉叉圖示（遊戲內的圖示）
    private const uint CrossIcon = 61502;

    // 一般物品 ID 都比這小；HQ 加 1000000
    private const uint HqOffset = 1_000_000;

    // 物品分類：寵物（81）、坐騎等的道具（63 + 排序分類 175）
    private const uint MinionCategory = 81;
    private const uint OtherCategory = 63;
    private const uint MountSortCategory = 175;

    private static readonly InventoryType[] OwnedContainers =
    {
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.EquippedItems, InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead,
        InventoryType.ArmoryBody, InventoryType.ArmoryHands, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets,
        InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
        InventoryType.ArmorySoulCrystal, InventoryType.SaddleBag1, InventoryType.SaddleBag2
    };

    private enum Mark
    {
        None,
        Unobtainable,
        Unlocked
    }

    private readonly Dictionary<uint, Mark> marks = new();

    public override string Id => "loot-window-marks";
    public override string Name => "拾取視窗標示";
    public override string Description => "拾取視窗裡拿不到的物品標叉叉（獨佔物品已經有、坐騎寵物已經學過），已經學過的標鎖頭。";
    public override TweakGroup Group => TweakGroup.Duty;
    public override TweakKind Kind => TweakKind.Auto;
    public override string Where => "拾取視窗每件物品的圖示上";

    /// <summary>遊戲執行緒：視窗開著時算每件的標記（持有數、是否解鎖要問遊戲）。</summary>
    public override void Update(DateTime now)
    {
        var addon = (AddonNeedGreed*)AetherKit.Core.Addons.Get(Addon);
        if (addon == null)
        {
            marks.Clear();
            return;
        }

        for (var i = 0; i < addon->NumItems; i++)
        {
            var id = addon->Items[i].ItemId;
            if (id != 0 && !marks.ContainsKey(id)) marks[id] = Evaluate(id);
        }
    }

    private static Mark Evaluate(uint lootItemId)
    {
        var id = lootItemId > HqOffset ? lootItemId - HqOffset : lootItemId;
        if (Services.Data.GetExcelSheet<Item>().GetRowOrDefault(id) is not { } item) return Mark.None;

        // 獨佔、而且不是用了會學到東西的道具：身上已經有就拿不到
        if (item.IsUnique && item.ItemAction.RowId == 0 && Owned(lootItemId)) return Mark.Unobtainable;

        var unlocked = Unlocked(id);

        // 寵物、坐騎道具已經學過：拿不到（擲了也會被丟掉）
        if (unlocked && (item.ItemUICategory.RowId == MinionCategory
                         || (item.ItemUICategory.RowId == OtherCategory && item.ItemSortCategory.RowId == MountSortCategory)))
            return Mark.Unobtainable;

        return unlocked ? Mark.Unlocked : Mark.None;
    }

    private static bool Owned(uint itemId)
    {
        var manager = InventoryManager.Instance();
        foreach (var container in OwnedContainers)
        {
            if (manager->GetItemCountInContainer(itemId, container, false, 0) > 0) return true;
        }

        return false;
    }

    /// <summary>用了會學到東西的道具（樂譜、幻卡、坐騎……）是否已經學過（問遊戲，只讀）。</summary>
    private static bool Unlocked(uint itemId)
    {
        var row = ExdModule.GetItemRowById(itemId);
        return row != null && UIState.Instance()->IsItemActionUnlocked(row) == 1;
    }

    /// <summary>畫在拾取視窗每件物品的圖示上；捲到清單外面的不畫。</summary>
    public override void Draw()
    {
        var addon = (AddonNeedGreed*)AetherKit.Core.Addons.Get(Addon);
        if (addon == null || marks.Count == 0) return;

        var listNode = (AtkComponentNode*)addon->AtkUnitBase.GetNodeById(ListNode);
        if (listNode == null || listNode->Component == null) return;

        var list = (AtkComponentList*)listNode->Component;
        var scale = addon->AtkUnitBase.Scale;
        var top = listNode->AtkResNode.ScreenY;
        var bottom = top + listNode->AtkResNode.Height * scale;
        var draw = ImGui.GetBackgroundDrawList();

        for (var i = 0; i < addon->NumItems && i < list->GetItemCount(); i++)
        {
            if (!marks.TryGetValue(addon->Items[i].ItemId, out var mark) || mark == Mark.None) continue;

            var renderer = list->GetItemRenderer(i);
            if (renderer == null || renderer->OwnerNode == null || !renderer->OwnerNode->AtkResNode.IsVisible()) continue;

            var row = renderer->OwnerNode->AtkResNode;
            var origin = new Vector2(row.ScreenX, row.ScreenY);
            if (origin.Y < top - 1 || origin.Y + 40 * scale > bottom + 1) continue;

            if (mark == Mark.Unobtainable)
            {
                // 叉叉蓋在物品圖示上
                var texture = Icons.Get(CrossIcon);
                if (texture != null)
                {
                    var min = origin + new Vector2(14, 14) * scale;
                    draw.AddImage(texture.Handle, min, min + new Vector2(40) * scale);
                }
            }
            else
            {
                // 鎖頭放在物品圖示右下角
                var size = 18f * scale;
                var pos = origin + new Vector2(36, 34) * scale;
                draw.AddText(UiBuilder.IconFont, size, pos + new Vector2(1), 0xC0000000, FontAwesomeIcon.Lock.ToIconString());
                draw.AddText(UiBuilder.IconFont, size, pos, 0xE6FFFFFF, FontAwesomeIcon.Lock.ToIconString());
            }
        }
    }
}
