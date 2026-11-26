using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AetherKit.Tweaks;

/// <summary>
/// 確認框預設選「是」（Simple Tweaks 的 Always Yes）：各種確認視窗一打開，就把游標移到「是」（或確定）按鈕上，
/// 用手把或數字鍵 0 確認時直接就是「是」。不會替你按，只是移游標。
/// 視窗上有「不再顯示」之類的勾選框、而且還沒勾時，可以先把游標移到勾選框上。
/// 移游標用的是遊戲視窗自己的「設定焦點」，並設定游標目標（見 改版檢查清單.md）。
/// </summary>
public sealed unsafe class AlwaysYes : Tweak
{
    /// <summary>一個確認視窗：視窗名稱、「是」按鈕節點、長按版「是」按鈕節點、勾選框節點、要等幾幀再移。</summary>
    private sealed record Rule(string Category, string Addon, uint Yes, uint? HoldYes = null, uint? CheckBox = null, int DelayTicks = -1);

    /// <summary>分類（設定裡一個勾選框），名稱照遊戲裡的叫法。</summary>
    private static readonly (string Id, string Name)[] Categories =
    {
        ("yesno", "一般確認框（是／否）"),
        ("duty", "副本配對確認"),
        ("cards", "幻卡交換"),
        ("ventures", "雇員探險"),
        ("entrust", "雇員：交給雇員重複的物品"),
        ("meld", "鑲嵌魔晶石"),
        ("extract", "精製魔晶石"),
        ("retrieve", "回收魔晶石"),
        ("dispel", "解除投影"),
        ("dye", "投影板確認"),
        ("desynth", "分解"),
        ("reduction", "精選結果"),
        ("exchange", "道具兌換確認"),
        ("lobby", "登入畫面"),
        ("blunderville", "金碟「布蘭德維爾」離開確認")
    };

    // 節點編號照 Simple Tweaks（各視窗「是」按鈕、勾選框的節點）
    private static readonly Rule[] Rules =
    {
        new("yesno", "SelectYesno", 8, HoldYes: 9, CheckBox: 4, DelayTicks: 1),
        new("duty", "ContentsFinderConfirm", 63),
        new("cards", "ShopCardDialog", 16),
        new("ventures", "RetainerTaskAsk", 40),
        new("ventures", "RetainerTaskResult", 20),
        new("entrust", "RetainerItemTransferList", 7),
        new("meld", "MateriaAttachDialog", 35, CheckBox: 39),
        new("extract", "MaterializeDialog", 13),
        new("retrieve", "MateriaRetrieveDialog", 17),
        new("dispel", "MiragePrismRemove", 15),
        new("dye", "MiragePrismMiragePlateConfirm", 6),
        new("desynth", "SalvageDialog", 24, CheckBox: 23, DelayTicks: 0),
        new("reduction", "PurifyResult", 19),
        new("exchange", "ShopExchangeItemDialog", 18),
        new("lobby", "LobbyDKTWorldList", 23, DelayTicks: 0),
        new("lobby", "LobbyDKTCheckExec", 3, DelayTicks: 0),
        new("lobby", "LobbyWKTCheck", 4)
    };

    private const string BlundervilleAddon = "FGSExitDialog";

    private string newException = string.Empty;

    public override string Id => "always-yes";
    public override string Name => "確認框預設選「是」";
    public override string Description => "各種確認視窗打開時，游標先放在「是」上，用手把或數字鍵 0 確認就是「是」（不會替你按）。";
    public override TweakGroup Group => TweakGroup.Interface;
    public override TweakKind Kind => TweakKind.Auto;

    private static string[] AddonNames => Rules.Select(r => r.Addon).Append(BlundervilleAddon).Distinct().ToArray();

    protected override void OnEnable() =>
        Services.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonNames, OnSetup);

    protected override void OnDisable() =>
        Services.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, AddonNames, OnSetup);

    private bool CategoryOn(string id) => !Config.AlwaysYesOff.Contains(id);

    private void OnSetup(AddonEvent type, AddonArgs args)
    {
        var name = args.AddonName;

        if (name == BlundervilleAddon)
        {
            if (CategoryOn("blunderville")) FocusComponentNode((AtkUnitBase*)args.Addon.Address, 10, 6);
            return;
        }

        var rule = Rules.FirstOrDefault(r => r.Addon == name);
        if (rule == null || !CategoryOn(rule.Category)) return;

        if (rule.Category == "yesno" && IsException((AtkUnitBase*)args.Addon.Address)) return;

        if (rule.DelayTicks < 0)
        {
            FocusYes((AtkUnitBase*)args.Addon.Address, rule);
            return;
        }

        // 有些視窗剛建好時按鈕還沒準備好，等一兩幀再移
        Services.Framework.RunOnTick(() =>
        {
            var addon = AetherKit.Core.Addons.Get(rule.Addon);
            if (addon != null) FocusYes(addon, rule);
        }, delayTicks: rule.DelayTicks);
    }

    /// <summary>一般確認框：提示文字包含例外清單裡任一段文字的，不動。</summary>
    private bool IsException(AtkUnitBase* addon)
    {
        if (Config.AlwaysYesExceptions.Count == 0 || addon == null) return false;

        var prompt = AetherKit.Core.Addons.YesnoPrompt(addon).ReplaceLineEndings(string.Empty);
        return prompt.Length > 0 && Config.AlwaysYesExceptions.Any(e => prompt.Contains(e));
    }

    private void FocusYes(AtkUnitBase* addon, Rule rule)
    {
        if (addon == null || addon->UldManager.LoadedState != AtkLoadState.Loaded) return;

        var yes = addon->UldManager.SearchNodeById(rule.Yes);
        if (yes == null) return;

        // 勾選框：看得到、旁邊有文字、而且（設定要求時）還沒勾，就先移到勾選框
        var checkBox = rule.CheckBox is { } boxId ? (AtkComponentNode*)addon->UldManager.SearchNodeById(boxId) : null;
        if (Config.AlwaysYesSelectCheckBox && checkBox != null && checkBox->AtkResNode.IsVisible() && checkBox->Component != null
            && checkBox->Component->UldManager.LoadedState == AtkLoadState.Loaded)
        {
            var ticked = checkBox->Component->UldManager.SearchNodeById(3);
            var label = (AtkTextNode*)checkBox->Component->UldManager.SearchNodeById(2);
            var hasLabel = label != null && !string.IsNullOrWhiteSpace(label->NodeText.ToString());
            var isTicked = ticked != null && ticked->IsVisible();

            if (hasLabel && !(Config.AlwaysYesIgnoreTicked && isTicked))
            {
                Focus(addon, &checkBox->AtkResNode, 5);
                return;
            }
        }

        // 長按版的「是」（要按住的確認框）：一般按鈕看不到時用它
        var hold = rule.HoldYes is { } holdId ? addon->UldManager.SearchNodeById(holdId) : null;
        if (hold != null && !yes->IsVisible())
            Focus(addon, hold, 7);
        else
            Focus(addon, yes, 4);
    }

    /// <summary>把游標放到某個按鈕元件裡的節點上。</summary>
    private static void Focus(AtkUnitBase* addon, AtkResNode* componentNode, uint innerNode)
    {
        var component = ((AtkComponentNode*)componentNode)->Component;
        if (component == null || component->UldManager.LoadedState != AtkLoadState.Loaded) return;

        var target = component->UldManager.SearchNodeById(innerNode);
        if (target == null) return;

        // [改記憶體] 設定視窗焦點與游標目標（同 Simple Tweaks，見 改版檢查清單.md）
        addon->SetFocusNode(target);
        addon->CursorTarget = target;
    }

    private static void FocusComponentNode(AtkUnitBase* addon, uint buttonId, uint innerNode)
    {
        if (addon == null) return;

        var button = addon->UldManager.SearchNodeById(buttonId);
        if (button != null) Focus(addon, button, innerNode);
    }

    public override bool HasSettings => true;

    public override void DrawSettings()
    {
        ImGui.TextDisabled("哪些視窗要預設選「是」：");
        foreach (var (id, name) in Categories)
        {
            var on = CategoryOn(id);
            if (!ImGui.Checkbox($"{name}##ay-{id}", ref on)) continue;

            Config.AlwaysYesOff.Remove(id);
            if (!on) Config.AlwaysYesOff.Add(id);
            Save();
        }

        ImGui.Spacing();
        var box = Config.AlwaysYesSelectCheckBox;
        if (ImGui.Checkbox("視窗上有勾選框時，先把游標放在勾選框上", ref box))
        {
            Config.AlwaysYesSelectCheckBox = box;
            Save();
        }

        if (Config.AlwaysYesSelectCheckBox)
        {
            ImGui.Indent();
            var ignore = Config.AlwaysYesIgnoreTicked;
            if (ImGui.Checkbox("已經勾了就直接放在「是」上", ref ignore))
            {
                Config.AlwaysYesIgnoreTicked = ignore;
                Save();
            }

            ImGui.Unindent();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("一般確認框的例外：提示文字包含下面任一段的，不動游標。");

        for (var i = 0; i < Config.AlwaysYesExceptions.Count; i++)
        {
            ImGui.PushID($"ay-ex-{i}");
            ImGui.SetNextItemWidth(300);
            var text = Config.AlwaysYesExceptions[i];
            if (ImGui.InputText("##text", ref text, 200))
            {
                Config.AlwaysYesExceptions[i] = text;
                Save();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("刪除"))
            {
                Config.AlwaysYesExceptions.RemoveAt(i);
                Save();
                ImGui.PopID();
                break;
            }

            ImGui.PopID();
        }

        ImGui.SetNextItemWidth(300);
        ImGui.InputTextWithHint("##ay-new", "新增例外文字……", ref newException, 200);
        ImGui.SameLine();
        if (ImGui.SmallButton("新增##ay-add") && newException.Trim().Length > 0)
        {
            Config.AlwaysYesExceptions.Add(newException.Trim());
            newException = string.Empty;
            Save();
        }
    }
}
