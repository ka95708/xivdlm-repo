using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 幻化這組小功能共用的資料：收藏櫃收得下哪些道具、套裝用到哪些裝備、
/// 投影台裡有什麼、右鍵選單裡找某個選項。全部只讀遊戲資料，不做任何動作。
/// </summary>
internal static unsafe class GlamourCommon
{
    /// <summary>HQ（+1000000）、收藏品（+500000）的編號都取這個餘數就是原本的道具編號。</summary>
    private const uint BaseIdModulo = 500_000;

    /// <summary>右鍵選單（ContextMenu）：第 0 個值是選項數，選項文字從第 7 個開始。</summary>
    private const int MenuCountIndex = 0;
    private const int MenuFirstTextIndex = 7;

    /// <summary>HQ 道具名稱後面的圖示字元。</summary>
    public const char HqGlyph = '';

    /// <summary>背包四頁。</summary>
    public static readonly InventoryType[] Bags =
    {
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4
    };

    /// <summary>兵裝庫各欄（不含靈魂水晶）。</summary>
    public static readonly InventoryType[] Armoury =
    {
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody,
        InventoryType.ArmoryHands, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings
    };

    private static Dictionary<uint, uint>? cabinetRows;

    /// <summary>道具編號 → 收藏櫃表（Cabinet）的列編號；不在表裡就是收藏櫃收不下。</summary>
    public static Dictionary<uint, uint> CabinetRows => cabinetRows ??= BuildCabinetRows();

    public static uint BaseId(uint itemId) => itemId % BaseIdModulo;

    private static Dictionary<uint, uint> BuildCabinetRows()
    {
        var rows = new Dictionary<uint, uint>();
        foreach (var row in Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Cabinet>())
        {
            if (row.Item.RowId != 0)
                rows[row.Item.RowId] = row.RowId;
        }

        return rows;
    }

    /// <summary>收藏櫃資料讀到了沒（這次登入要打開過收藏櫃才有）。</summary>
    public static bool CabinetLoaded()
    {
        var ui = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
        return ui != null && ui->Cabinet.IsCabinetLoaded();
    }

    /// <summary>這件道具已經收在收藏櫃裡了嗎。</summary>
    public static bool InCabinet(uint row)
    {
        var ui = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
        return ui != null && ui->Cabinet.IsItemInCabinet((int)row);
    }

    /// <summary>所有套裝（套裝列表）用到的裝備，存原本的道具編號（不分 HQ）。</summary>
    public static HashSet<uint> GearsetItemIds()
    {
        var result = new HashSet<uint>();
        var module = RaptureGearsetModule.Instance();
        if (module == null) return result;

        foreach (ref var entry in module->Entries)
        {
            if (!entry.Flags.HasFlag(RaptureGearsetModule.GearsetFlag.Exists)) continue;

            foreach (ref var item in entry.Items)
            {
                if (item.ItemId != 0)
                    result.Add(BaseId(item.ItemId));
            }
        }

        return result;
    }

    /// <summary>道具名稱（遊戲語言）；找不到是空字串。</summary>
    public static string ItemName(uint itemId) =>
        Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(BaseId(itemId))?.Name.ExtractText() ?? string.Empty;

    /// <summary>投影台資料讀到了沒；讀到了才給 MirageManager。</summary>
    public static MirageManager* PrismBox()
    {
        var mirage = MirageManager.Instance();
        return mirage != null && mirage->PrismBoxRequested && mirage->PrismBoxLoaded ? mirage : null;
    }

    /// <summary>右鍵選單裡文字等於（或包含）text 的選項位置；沒有是 -1。</summary>
    public static int FindMenuEntry(AtkUnitBase* menu, string text)
    {
        if (menu == null || string.IsNullOrEmpty(text)) return -1;

        var count = menu->AtkValuesCount > MenuCountIndex ? (int)menu->AtkValues[MenuCountIndex].UInt : 0;
        for (var i = 0; i < count; i++)
        {
            var entry = Addons.ValueText(menu, MenuFirstTextIndex + i);
            if (entry.Contains(text, StringComparison.Ordinal)) return i;
        }

        return -1;
    }

    /// <summary>點右鍵選單第 index 個選項，跟滑鼠點下去送的 callback 一樣。</summary>
    public static void ClickMenuEntry(AtkUnitBase* menu, int index) => Addons.Fire(menu, 0, index, 0u, 0, 0);
}

/// <summary>
/// 「從投影台把某些幻影取出來」的共用部分：每一步找下一個要取的，用投影台「取出」同一個動作取出一件。
/// 取出的道具會回到背包，背包滿了就停。
/// </summary>
public abstract unsafe class DresserTakeOutTweak : LoopTweak
{
    public override TweakGroup Group => TweakGroup.Glamour;
    public override string Where => "投影台視窗上方";

    protected override string Addon => "MiragePrismPrismBox";

    /// <summary>現在投影台裡要取出的格子（PrismBoxItemIds 的位置），照順序。</summary>
    protected abstract List<int> Targets(MirageManager* mirage);

    protected override string? CanStart(AtkUnitBase* addon)
    {
        var mirage = GlamourCommon.PrismBox();
        if (mirage == null) return "投影台資料還沒讀到，等視窗載入完再按";
        if (Targets(mirage).Count == 0) return "沒有要取出的";
        return null;
    }

    protected override string? Step(AtkUnitBase* addon, DateTime now)
    {
        if (EventBusy)
        {
            Wait(TimeSpan.FromMilliseconds(200));
            return null;
        }

        var mirage = GlamourCommon.PrismBox();
        if (mirage == null)
        {
            Wait(TimeSpan.FromMilliseconds(500));
            return null;
        }

        var targets = Targets(mirage);
        if (targets.Count == 0) return $"完成，共取出 {Done} 件";
        if (!Progress(targets.Count)) return "取出沒有進行，停止";
        if (FreeSlots() == 0) return $"背包滿了，停止（已取出 {Done} 件）";

        var index = targets[0];
        Log.Write($"取出 第 {index} 格 道具={mirage->PrismBoxItemIds[index]}（剩 {targets.Count} 件）");
        mirage->RestorePrismBoxItem((uint)index);
        Wait(TimeSpan.FromMilliseconds(800));
        return null;
    }
}
