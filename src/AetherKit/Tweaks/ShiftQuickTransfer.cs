using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Keys;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// Shift＋右鍵快速轉移／賣出（取代 Simple Tweaks 的 Quick Sell Items）：按住 Shift 對物品按右鍵，
/// 不用從右鍵選單挑，直接做最可能要的那件事：
/// 1. 開著商店：出售。
/// 2. 開著雇員的出售清單（上架）：委託僱員出售物品（接著由自動雇員填價格、按確定）。
/// 3. 其他：放到「另一個」開著的背包——物品欄、陸行鳥鞍囊、雇員背包、兵裝庫之中，
///    除了物品所在的那個以外、最近點過的那一個（它的選單沒有對應選項就換下一個）。
/// 做法是等右鍵選單打開，點選單裡對應的那一項（跟自己點一樣）；選單裡沒有對應的項目就照常留著選單。
/// 選項文字從遊戲文字表讀（台服：出售、委託僱員出售物品、交給僱員保管……）。
/// </summary>
public sealed unsafe class ShiftQuickTransfer : Tweak
{
    private enum Bag
    {
        None,
        Inventory,
        Saddlebag,
        Retainer,
        Armoury
    }

    // 各種背包的遊戲視窗
    private static readonly (string Addon, Bag Bag)[] BagWindows =
    {
        ("Inventory", Bag.Inventory), ("InventoryLarge", Bag.Inventory), ("InventoryExpansion", Bag.Inventory),
        ("InventoryBuddy", Bag.Saddlebag), ("InventoryBuddy2", Bag.Saddlebag),
        ("InventoryRetainer", Bag.Retainer), ("InventoryRetainerLarge", Bag.Retainer),
        ("ArmouryBoard", Bag.Armoury)
    };

    // 右鍵選單的選項（Addon 表）：
    // 93 出售、5480 委託僱員出售物品、97 交給僱員保管、98 從僱員處取回、881 放入陸行鳥鞍囊、
    // 887 從陸行鳥鞍囊中取回、1387 放入兵裝庫、1388 放入背包
    private const uint SellRow = 93;
    private const uint RetainerSellRow = 5480;

    private static readonly Dictionary<Bag, uint[]> MoveRows = new()
    {
        [Bag.Inventory] = new uint[] { 98, 887, 1388 },
        [Bag.Saddlebag] = new uint[] { 881 },
        [Bag.Retainer] = new uint[] { 97 },
        [Bag.Armoury] = new uint[] { 1387 }
    };

    public override string Id => "shift-quick-transfer";
    public override string Name => "Shift＋右鍵快速轉移／賣出";
    public override string Description => "按住 Shift 對物品按右鍵：開著商店就出售、開著雇員出售清單就委託出售，不然就放到另一個開著的背包（物品欄、鞍囊、雇員、兵裝庫）。";
    public override TweakGroup Group => TweakGroup.Items;
    public override TweakKind Kind => TweakKind.Auto;
    public override string Where => "背包、鞍囊、雇員背包、兵裝庫裡的物品（Shift＋右鍵）";

    /// <summary>上一幀背包右鍵選單是不是開著（剛打開的那一幀才處理一次）。</summary>
    private bool menuWasOpen;

    /// <summary>
    /// 每一幀看背包的右鍵選單是不是剛打開。右鍵選單視窗會重複使用，「建立」事件只有第一次才有，
    /// 所以不能只靠事件，改成看「上一幀沒開、這一幀開了」。
    /// </summary>
    public override void Update(DateTime now)
    {
        TrackFocus();

        var agent = AgentInventoryContext.Instance();
        var menu = agent == null || !agent->AgentInterface.IsAgentActive()
            ? null
            : RaptureAtkUnitManager.Instance()->GetAddonById((ushort)agent->AgentInterface.GetAddonId());
        var open = menu != null && menu->IsVisible;

        if (open && !menuWasOpen) OnMenuOpened(menu, agent);
        menuWasOpen = open;
    }

    private void OnMenuOpened(AtkUnitBase* menu, AgentInventoryContext* agent)
    {
        if (!Services.KeyState[VirtualKey.SHIFT]) return;

        var source = BagOf(agent->TargetInventoryId);
        if (source == Bag.None)
        {
            Log.Write($"Shift＋右鍵：物品所在的 {agent->TargetInventoryId} 不是背包、鞍囊、雇員背包或兵裝庫，不處理");
            return;
        }

        var options = MenuOptions(agent);
        var name = ItemName(agent);

        // 1. 商店：出售（只有自己身上的東西能賣）
        if (Config.QuickSell && source is Bag.Inventory or Bag.Armoury or Bag.Saddlebag && Addons.IsOpen("Shop"))
        {
            if (Click(menu, agent, options, SellRow, $"出售 {name}")) return;
        }

        // 2. 雇員出售清單（上架中）：委託僱員出售物品
        if (Config.QuickSell && source != Bag.Retainer && Addons.IsOpen("RetainerSellList"))
        {
            if (Click(menu, agent, options, RetainerSellRow, $"委託雇員出售 {name}")) return;
        }

        if (!Config.QuickTransfer) return;

        // 3. 放到另一個開著的背包：最近點過的優先；那一個的選項選單裡沒有，就換下一個
        var targets = OtherBagsByRecentFocus(source);
        foreach (var target in targets)
        {
            foreach (var row in MoveRows[target])
            {
                if (Click(menu, agent, options, row, $"{name}：{source} → {target}")) return;
            }
        }

        Log.Write($"{name}：{source} → {string.Join("／", targets)} 都沒有對應的選項（{string.Join(" | ", options)}），照常留著選單");
    }

    private bool Click(AtkUnitBase* menu, AgentInventoryContext* agent, List<string> options, uint row, string what)
    {
        var text = Addons.Text(row);
        if (text.Length == 0) return false;

        var index = options.FindIndex(o => o == text);
        if (index < 0) return false;

        // 跟點選單那一項一樣，點完把選單收起來
        Addons.Fire(menu, 0, index, 0u, 0, 0);
        agent->AgentInterface.Hide();
        Log.Write(what);
        return true;
    }

    /// <summary>右鍵選單目前的各項文字（從背包右鍵選單的資料讀）。</summary>
    internal static List<string> MenuOptions(AgentInventoryContext* agent)
    {
        var list = new List<string>();
        for (var i = 0; i < agent->ContextItemCount; i++)
        {
            var value = agent->EventParams[agent->ContexItemStartIndex + i];
            if (value.Type is not (FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String
                or FFXIVClientStructs.FFXIV.Component.GUI.ValueType.ManagedString
                or FFXIVClientStructs.FFXIV.Component.GUI.ValueType.String8) || value.String.Value == null)
            {
                list.Add(string.Empty);
                continue;
            }

            list.Add(Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue.Trim());
        }

        return list;
    }

    private static Bag BagOf(InventoryType type) => (int)type switch
    {
        >= (int)InventoryType.Inventory1 and <= (int)InventoryType.Inventory4 => Bag.Inventory,
        (int)InventoryType.SaddleBag1 or (int)InventoryType.SaddleBag2
            or (int)InventoryType.PremiumSaddleBag1 or (int)InventoryType.PremiumSaddleBag2 => Bag.Saddlebag,
        >= (int)InventoryType.RetainerPage1 and <= (int)InventoryType.RetainerPage7 => Bag.Retainer,
        >= (int)InventoryType.ArmoryOffHand and <= (int)InventoryType.ArmoryMainHand => Bag.Armoury,
        _ => Bag.None
    };

    /// <summary>每個背包最後一次被點（變成目前選中的視窗）的順序，數字越大越近。</summary>
    private readonly Dictionary<Bag, long> lastFocus = new();
    private long focusCounter;

    /// <summary>記錄目前被選中的視窗是哪個背包（每一幀）。遊戲的繪製順序跟點的先後對不起來，所以自己記。</summary>
    private void TrackFocus()
    {
        var focused = RaptureAtkUnitManager.Instance()->AtkUnitManager.FocusedAddon;
        if (focused == null) return;

        var name = focused->NameString;
        foreach (var (addonName, bag) in BagWindows)
        {
            if (addonName != name) continue;

            if (!lastFocus.TryGetValue(bag, out var order) || order != focusCounter)
                lastFocus[bag] = ++focusCounter;
            return;
        }
    }

    /// <summary>除了來源以外、開著的背包，照最近點過的先後排（沒點過的排最後）。</summary>
    private List<Bag> OtherBagsByRecentFocus(Bag source)
    {
        var open = BagWindows
            .Where(w => w.Bag != source && Addons.IsOpen(w.Addon))
            .Select(w => w.Bag)
            .Distinct()
            .OrderByDescending(b => lastFocus.TryGetValue(b, out var order) ? order : 0)
            .ToList();

        Log.Write($"開著的其他背包（最近點過的在前）：{string.Join("、", open.Select(b => $"{b}={(lastFocus.TryGetValue(b, out var o) ? o : 0)}"))}；來源 {source}");
        return open;
    }

    private static string ItemName(AgentInventoryContext* agent)
    {
        var item = agent->TargetInventorySlot;
        if (item == null || item->ItemId == 0) return "物品";

        return Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(item->ItemId)?.Name.ExtractText() ?? $"#{item->ItemId}";
    }

    public override bool HasSettings => true;

    public override void DrawSettings()
    {
        var sell = Config.QuickSell;
        if (ImGui.Checkbox("開著商店、雇員出售清單時直接賣", ref sell))
        {
            Config.QuickSell = sell;
            Save();
        }

        var transfer = Config.QuickTransfer;
        if (ImGui.Checkbox("其他時候放到另一個開著的背包", ref transfer))
        {
            Config.QuickTransfer = transfer;
            Save();
        }

        ImGui.TextDisabled("另一個背包 = 物品欄、陸行鳥鞍囊、雇員背包、兵裝庫之中，除了物品所在的以外、最近點過的那一個。");
    }
}
