using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using HotbarSlotType = FFXIVClientStructs.FFXIV.Client.UI.Misc.RaptureHotbarModule.HotbarSlotType;

namespace Barkeep.Game;

/// <summary>
/// 接住從遊戲裡拖出來的技能、道具等等。
///
/// 遊戲的拖放跑在它自己的 AtkUnit 系統裡，ImGui 收不到那個 payload，
/// 所以三樣東西分三個地方拿：
///   種類 ← AtkDragDropInterface.DragDropType
///   ID   ← AtkDragDropManager.PayloadContainer.Int2
///   圖示 ← 被拖的那個 AtkComponentDragDrop 自己的 GetIconId()
///
/// 放開滑鼠的那一幀 ImGui 不一定收得到事件，所以落點是自己用座標找的：
/// 畫面每一幀把可以放的位置登記進來，拖曳結束時比對滑鼠在哪一塊上。
/// </summary>
public unsafe class GameDragDrop
{
    /// <summary>放開滑鼠後還能接住的幀數。</summary>
    private const int GraceFrames = 3;

    private HotbarSlotType payloadType;
    private uint payloadId;
    private int payloadIcon;
    private int idleFrames = GraceFrames + 1;
    private bool wasDragging;

    /// <summary>
    /// 上一幀登記的可放置位置，依登記順序比對，先登記的先命中 —
    /// 所以列要比它所在的分區先登記。Index == List.Count 代表「加到最後面」。
    /// </summary>
    private readonly List<(List<PanelItem> List, int Index, Vector2 Min, Vector2 Max)> targets = new();

    /// <summary>遊戲正拖著某個認得出來的東西，用來顯示落點提示。</summary>
    public bool IsDragging { get; private set; }

    public void RegisterTarget(List<PanelItem> list, int index, Vector2 min, Vector2 max)
        => targets.Add((list, index, min, max));

    /// <summary>每幀在繪製開頭呼叫一次。</summary>
    public void Update(Plugin plugin)
    {
        var stage = AtkStage.Instance();
        var dragging = stage != null && stage->DragDropManager.IsDragging;

        // 拖曳剛結束：用上一幀登記的位置判斷落點
        if (!dragging && wasDragging)
            DropAtMouse(plugin);

        wasDragging = dragging;
        targets.Clear();

        if (dragging)
        {
            IsDragging = true;
            idleFrames = 0;
            Capture(stage);
            return;
        }

        IsDragging = false;

        if (idleFrames <= GraceFrames)
            idleFrames++;
        else
            payloadType = HotbarSlotType.Empty;
    }

    private bool CanDrop => payloadType != HotbarSlotType.Empty && idleFrames <= GraceFrames;

    private void Capture(AtkStage* stage)
    {
        var manager = &stage->DragDropManager;

        var source = manager->DragDrop1 != null ? manager->DragDrop1 : manager->DragDrop2;
        if (source == null) return;

        var mapped = Map(source->DragDropType);
        if (mapped == HotbarSlotType.Empty) return;

        payloadType = mapped;
        payloadId = (uint)manager->PayloadContainer.Int2;
        payloadIcon = manager->DragDropS != null ? manager->DragDropS->GetIconId() : 0;
    }

    private void DropAtMouse(Plugin plugin)
    {
        if (!CanDrop) return;

        var mouse = ImGui.GetIO().MousePos;

        foreach (var target in targets)
        {
            if (mouse.X < target.Min.X || mouse.X > target.Max.X) continue;
            if (mouse.Y < target.Min.Y || mouse.Y > target.Max.Y) continue;
            if (target.Index < 0 || target.Index > target.List.Count) continue;
            if (!TryTake(out var item)) return;

            if (target.Index == target.List.Count)
                target.List.Add(PanelItem.From(item));
            else
                target.List[target.Index] = PanelItem.From(item);

            plugin.Save();
            return;
        }
    }

    /// <summary>把拖著的東西轉成一個項目。名稱或指令查不出來的話就不接。</summary>
    private bool TryTake(out ActionItem item)
    {
        item = null!;

        var module = RaptureHotbarModule.Instance();
        if (module == null) return false;

        // GetDisplayNameForSlot / GetIconIdForSlot 實際上是查表用的，
        // 隨便給一個 slot 當 this 就能呼叫，這裡借暫存槽。
        var slot = &module->ScratchSlot;
        var name = slot->GetDisplayNameForSlot(payloadType, payloadId).ToString() ?? string.Empty;
        var command = GameCommands.For(payloadType, payloadId, name);

        if (string.IsNullOrEmpty(command))
        {
            Services.Log.Information($"略過拖曳: {payloadType} #{payloadId} 名稱='{name}' 做不出指令");
            Discard();
            return false;
        }

        // 被拖的元件自己回報的圖示最準，查表的只當後備
        var icon = payloadIcon > 0 ? payloadIcon : slot->GetIconIdForSlot(payloadType, payloadId);

        item = new ActionItem
        {
            Label = name,
            SubLabel = command,
            IconId = icon > 0 ? (uint)icon : 0,
            Command = command,
            Source = new GameSource { Type = (int)payloadType, Id = payloadId }
        };

        Discard();
        return true;
    }

    private void Discard()
    {
        payloadType = HotbarSlotType.Empty;
        idleFrames = GraceFrames + 1;
    }

    /// <summary>
    /// 從介面拖和從熱鍵列拖是兩組不同的 DragDropType，但指的是同樣的東西，
    /// 統一換算成 HotbarSlotType。只列做得出文字指令的種類，其餘回 Empty
    /// 讓它們根本接不住，而不是放一個按了沒反應的項目上去。
    /// </summary>
    private static HotbarSlotType Map(DragDropType type) => type switch
    {
        DragDropType.Action or DragDropType.ActionBar_Action => HotbarSlotType.Action,
        DragDropType.Item or DragDropType.ActionBar_Item => HotbarSlotType.Item,
        DragDropType.EventItem or DragDropType.ActionBar_EventItem => HotbarSlotType.EventItem,
        DragDropType.Emote or DragDropType.ActionBar_Emote => HotbarSlotType.Emote,
        DragDropType.CraftingAction or DragDropType.ActionBar_CraftingAction => HotbarSlotType.CraftAction,
        DragDropType.GeneralAction or DragDropType.ActionBar_GeneralAction => HotbarSlotType.GeneralAction,
        DragDropType.BuddyAction or DragDropType.ActionBar_BuddyAction => HotbarSlotType.BuddyAction,
        DragDropType.Companion or DragDropType.ActionBar_Companion => HotbarSlotType.Companion,
        DragDropType.GearSet or DragDropType.ActionBar_GearSet => HotbarSlotType.GearSet,
        DragDropType.PetAction or DragDropType.ActionBar_PetAction => HotbarSlotType.PetAction,
        DragDropType.Mount or DragDropType.ActionBar_Mount => HotbarSlotType.Mount,
        DragDropType.BgcArmyAction => HotbarSlotType.BgcArmyAction,
        DragDropType.Ornament => HotbarSlotType.Ornament,
        _ => HotbarSlotType.Empty
    };
}
