using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 收進收藏櫃：旅店收藏櫃的「放入」視窗（Cabinet）加「全部收進」按鈕，清單上的道具一件一件收進收藏櫃。
///
/// 每一件：點清單上的一項（跟滑鼠點一樣，由清單元件自己送事件）→ 確認框核對是「放入收藏櫃」的那個，
/// 名稱是套裝用到的裝備就按「否」跳過（可在選項關掉），否則按「是」→ 下一件。
/// 收進去之後染色、魔晶石、投影都會消失，所以確認框一定核對文字，不是這個確認框就停。
/// 只處理視窗目前顯示的分類；收不進去的（耐久度不足、重複）會往下一項跳過。
/// </summary>
public sealed unsafe class StoreToCabinet : LoopTweak
{
    private const string YesnoAddon = "SelectYesno";
    private const string MenuAddon = "ContextMenu";

    // 遊戲文字表：「確定要將「」放入收藏櫃嗎？……」
    private const uint StorePromptRow = 4650;

    private enum Outcome
    {
        /// <summary>點了清單但還沒看到確認框。</summary>
        None,

        /// <summary>確認框按了「否」跳過。</summary>
        Skipped,

        /// <summary>確認框按了「是」。</summary>
        Stored
    }

    private int index;
    private int countAtClick;
    private bool clicked;
    private bool doubleClick;
    private Outcome outcome;
    private HashSet<string> skipNames = new();

    public override string Id => "store-to-cabinet";
    public override string Name => "收進收藏櫃";
    public override string Description =>
        "收藏櫃的放入視窗加「全部收進」按鈕，目前分類清單上的道具一件一件收進收藏櫃（染色、魔晶石、投影會消失）。";
    public override TweakGroup Group => TweakGroup.Glamour;
    public override string Where => "旅店收藏櫃的放入視窗上方";

    protected override string Addon => "Cabinet";
    protected override string StartLabel => "全部收進";

    protected override string? CanStart(AtkUnitBase* addon)
    {
        if (!GlamourCommon.CabinetLoaded()) return "收藏櫃資料還沒讀到，等視窗載入完再按";
        if (FindList(addon) == null) return "找不到收藏櫃的清單";
        if (Storable() == 0) return "背包和兵裝庫裡沒有能收進收藏櫃的道具";

        index = 0;
        clicked = false;
        doubleClick = false;
        outcome = Outcome.None;
        skipNames = Config.CabinetSkipGearset ? GearsetNames() : new HashSet<string>();
        return null;
    }

    protected override string? Step(AtkUnitBase* addon, DateTime now)
    {
        var yesno = Addons.Get(YesnoAddon);
        if (yesno != null) return Confirm(yesno);

        if (Addons.Get(MenuAddon) != null) return "跳出了右鍵選單，跟預期的不一樣，停止";

        // 開著收藏櫃本來就是事件中，只等多出來的（例如按「是」之後遊戲還在處理）
        if (EventBusy)
        {
            Wait(TimeSpan.FromMilliseconds(200));
            return null;
        }

        var list = FindList(addon);
        if (list == null) return "找不到收藏櫃的清單，停止";

        var storable = Storable();
        if (clicked)
        {
            clicked = false;

            // 按了「是」數量卻沒少：這件收不進去（耐久度不足、已經收過），換下一項
            if (outcome == Outcome.Stored && storable >= countAtClick) index++;

            // 點了沒反應：下次改用雙擊試試
            if (outcome == Outcome.None) doubleClick = !doubleClick;
        }

        // 按「否」跳過的不算卡住；點了沒反應或收不進去才算
        if (outcome != Outcome.Skipped && !Progress(storable))
            return "收藏櫃沒有反應，停止";

        var count = list->GetItemCount();
        if (storable == 0 || index >= count)
        {
            var left = storable > 0 ? $"；還有 {storable} 件沒收（其他分類、略過的套裝裝備或耐久度不足）" : string.Empty;
            return $"完成，共收進 {Done} 件{left}";
        }

        Log.Write($"點清單第 {index} 項（{(doubleClick ? "雙擊" : "單擊")}，清單 {count} 項、可收 {storable} 件）");
        list->DispatchItemEvent(index, doubleClick ? AtkEventType.ListItemDoubleClick : AtkEventType.ListItemClick);
        clicked = true;
        countAtClick = storable;
        outcome = Outcome.None;
        Wait(TimeSpan.FromMilliseconds(600));
        return null;
    }

    /// <summary>確認框：核對是「放入收藏櫃」，套裝用到的按「否」，其他按「是」。</summary>
    private string? Confirm(AtkUnitBase* yesno)
    {
        var prompt = PromptText(yesno);
        if (!prompt.Contains(PromptTail(), StringComparison.Ordinal))
            return "跳出了別的確認框，停止，請自己決定";

        var name = NameInPrompt(prompt);
        if (IsGearsetItem(prompt, name))
        {
            Log.Write($"略過套裝用到的「{name}」");
            Addons.ClickButton(yesno, ((AddonSelectYesno*)yesno)->NoButton);
            outcome = Outcome.Skipped;
            index++;
        }
        else
        {
            Log.Write($"收進「{name}」");
            Addons.ClickYes(yesno);
            outcome = Outcome.Stored;
        }

        Wait(TimeSpan.FromMilliseconds(800));
        return null;
    }

    /// <summary>確認框的純文字（道具名稱是連結，去掉裡面的格式碼）。</summary>
    private static string PromptText(AtkUnitBase* yesno)
    {
        var text = ((AddonSelectYesno*)yesno)->PromptText;
        if (text == null) return string.Empty;

        return new Lumina.Text.ReadOnly.ReadOnlySeString(text->NodeText.AsSpan().ToArray()).ExtractText();
    }

    /// <summary>
    /// 是不是套裝用到的裝備。名稱對不上時再看整段文字有沒有包含套裝裝備的名稱，
    /// 寧可多跳過也不要把套裝裝備收進去。
    /// </summary>
    private bool IsGearsetItem(string prompt, string name)
    {
        if (skipNames.Count == 0) return false;
        if (skipNames.Contains(name)) return true;

        foreach (var skip in skipNames)
        {
            if (prompt.Contains(skip, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>確認框文字「」後面那段（「放入收藏櫃嗎？」），拿來認出這個確認框。</summary>
    private static string PromptTail()
    {
        var text = Addons.Text(StorePromptRow);
        var cut = text.LastIndexOf('」');
        var tail = cut >= 0 ? text[(cut + 1)..] : text;
        return string.IsNullOrWhiteSpace(tail) ? "收藏櫃" : tail.Trim();
    }

    /// <summary>確認框文字裡「」中間的道具名稱（去掉 HQ 圖示）。</summary>
    private static string NameInPrompt(string prompt)
    {
        var start = prompt.IndexOf('「');
        var end = prompt.IndexOf('」', start + 1);
        if (start < 0 || end < 0) return string.Empty;

        return prompt[(start + 1)..end].Replace(GlamourCommon.HqGlyph.ToString(), string.Empty).Trim();
    }

    /// <summary>視窗裡顯示中、項目最多的清單元件（收藏櫃視窗只有一個道具清單）。</summary>
    private static AtkComponentList* FindList(AtkUnitBase* addon)
    {
        AtkComponentList* best = null;
        var uld = addon->UldManager;

        for (var i = 0; i < uld.NodeListCount; i++)
        {
            var node = uld.NodeList[i];
            if (node == null || (int)node->Type < 1000 || !node->IsVisible()) continue;

            var component = ((AtkComponentNode*)node)->Component;
            if (component == null || component->GetComponentType() != ComponentType.List) continue;

            var list = (AtkComponentList*)component;
            if (best == null || list->GetItemCount() > best->GetItemCount()) best = list;
        }

        return best;
    }

    /// <summary>背包和兵裝庫裡收藏櫃收得下、而且還沒收過的道具件數。</summary>
    private static int Storable()
    {
        var rows = GlamourCommon.CabinetRows;
        var manager = InventoryManager.Instance();
        var count = 0;

        foreach (var types in new[] { GlamourCommon.Bags, GlamourCommon.Armoury })
        {
            foreach (var type in types)
            {
                var container = manager->GetInventoryContainer(type);
                if (container == null || !container->IsLoaded) continue;

                for (var i = 0; i < container->Size; i++)
                {
                    var slot = container->GetInventorySlot(i);
                    if (slot == null || slot->ItemId == 0) continue;

                    if (rows.TryGetValue(GlamourCommon.BaseId(slot->ItemId), out var row) && !GlamourCommon.InCabinet(row))
                        count++;
                }
            }
        }

        return count;
    }

    /// <summary>套裝用到的裝備名稱。</summary>
    private static HashSet<string> GearsetNames()
    {
        var names = new HashSet<string>();
        foreach (var id in GlamourCommon.GearsetItemIds())
        {
            var name = GlamourCommon.ItemName(id);
            if (name.Length > 0) names.Add(name);
        }

        return names;
    }

    protected override Action QuickOptions => DrawSettings;

    public override bool HasSettings => true;

    public override void DrawSettings()
    {
        var skip = Config.CabinetSkipGearset;
        if (ImGui.Checkbox("略過套裝裡用到的裝備", ref skip))
        {
            Config.CabinetSkipGearset = skip;
            Save();
        }
    }
}
