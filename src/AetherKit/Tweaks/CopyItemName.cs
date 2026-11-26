using AetherKit.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.ContextMenu;
using Lumina.Excel.Sheets;

namespace AetherKit.Tweaks;

/// <summary>
/// 右鍵複製物品名稱：在背包、陸行鳥鞍囊、雇員等物品欄的右鍵選單加一項「複製物品名稱」，
/// 點了把名稱複製到剪貼簿。有投影幻化的裝備另外多一項，複製投影外觀的名稱。
/// 只是加一個 Dalamud 右鍵選單項目，不碰遊戲本身的選單。
/// </summary>
public sealed class CopyItemName : Tweak
{
    // 事件道具（任務道具等）的編號從這裡開始，名稱在另一張表
    private const uint EventItemStart = 2_000_000;

    // HQ、收藏品的編號會加上這類偏移，取餘數就是原本的物品
    private const uint ItemIdModulo = 500_000;

    public override string Id => "copy-item-name";
    public override string Name => "右鍵複製物品名稱";
    public override string Description => "物品欄的右鍵選單加「複製物品名稱」，點了把名稱複製到剪貼簿。";
    public override TweakGroup Group => TweakGroup.Interface;
    public override TweakKind Kind => TweakKind.Auto;
    public override string Where => "背包等物品欄的右鍵選單";

    protected override void OnEnable() => Services.ContextMenu.OnMenuOpened += OnMenuOpened;

    protected override void OnDisable() => Services.ContextMenu.OnMenuOpened -= OnMenuOpened;

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (args.MenuType != ContextMenuType.Inventory) return;
        if (args.Target is not MenuTargetInventory { TargetItem: { } item } || item.ItemId == 0) return;

        var name = ItemName(item.ItemId);
        if (name.Length == 0) return;

        args.AddMenuItem(Entry("複製物品名稱", name));

        if (item.GlamourId != 0 && ItemName(item.GlamourId) is { Length: > 0 } glamour)
            args.AddMenuItem(Entry("複製物品名稱（投影外觀）", glamour));
    }

    private static MenuItem Entry(string label, string text) => new()
    {
        Name = label,
        UseDefaultPrefix = true,
        OnClicked = _ =>
        {
            ImGui.SetClipboardText(text);
            Notify.Info("複製物品名稱", $"已複製：{text}");
        }
    };

    private static string ItemName(uint id)
    {
        if (id >= EventItemStart)
            return Services.Data.GetExcelSheet<EventItem>().GetRowOrDefault(id)?.Singular.ExtractText() ?? string.Empty;

        return Services.Data.GetExcelSheet<Item>().GetRowOrDefault(id % ItemIdModulo)?.Name.ExtractText() ?? string.Empty;
    }
}
