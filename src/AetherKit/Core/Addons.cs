using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;
using Lumina.Excel.Sheets;

namespace AetherKit.Core;

/// <summary>
/// 操作遊戲視窗的共用小工具：找視窗、按按鈕、送 callback、點對話框、讀視窗裡的文字。
/// 全部只是模擬玩家在視窗上的操作，不送封包、不改記憶體。
/// </summary>
public static unsafe class Addons
{
    private static readonly Dictionary<uint, string> addonText = new();

    /// <summary>名稱對應的視窗；沒開、沒顯示或還沒準備好都回傳 null。</summary>
    public static AtkUnitBase* Get(string name)
    {
        var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name, 1);
        return addon != null && addon->IsVisible && addon->IsReady ? addon : null;
    }

    public static bool IsOpen(string name) => Get(name) != null;

    /// <summary>做一個跟滑鼠點擊一樣的事件，Listener 是這個視窗本身。</summary>
    public static AtkEvent NewEvent(AtkUnitBase* addon, byte flags = 0) => new()
    {
        Listener = (AtkEventListener*)addon,
        Target = &AtkStage.Instance()->AtkEventTarget,
        State = new AtkEventState { StateFlags = (AtkEventStateFlags)flags }
    };

    /// <summary>按下介面上的按鈕：把按鈕自己身上掛的事件送給它所在的介面，跟滑鼠點下去一樣。</summary>
    public static bool ClickButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (addon == null || button == null || !button->IsEnabled || !button->AtkResNode->IsVisible()) return false;

        var node = button->AtkComponentBase.OwnerNode;
        if (node == null) return false;

        var evt = node->AtkResNode.AtkEventManager.Event;
        if (evt == null) return false;

        var data = new AtkEventData();
        addon->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt, &data);
        return true;
    }

    /// <summary>用節點編號找按鈕再按（沒有具名欄位的視窗用）。</summary>
    public static bool ClickButton(AtkUnitBase* addon, uint nodeId)
    {
        if (addon == null) return false;

        var node = addon->GetNodeById(nodeId);
        if (node == null) return false;

        return ClickButton(addon, node->GetAsAtkComponentButton());
    }

    /// <summary>點一下對話框（Talk），跟玩家點畫面推進對話一樣。</summary>
    public static void ClickTalk(AtkUnitBase* talk)
    {
        var evt = NewEvent(talk);
        var data = new AtkEventData();
        talk->ReceiveEvent(AtkEventType.MouseClick, 0, &evt, &data);
    }

    /// <summary>送 callback，等同 /callback。值可以是 int、uint 或 bool。</summary>
    public static void Fire(AtkUnitBase* addon, params object[] values)
    {
        var atkValues = stackalloc AtkValue[values.Length];
        SetValues(atkValues, values);

        addon->FireCallback((uint)values.Length, atkValues, true);
    }

    /// <summary>
    /// 送事件給遊戲的 Agent（視窗背後的邏輯），跟在視窗上點選項目時遊戲自己送的一樣。
    /// 有些清單型視窗（例如分解清單）點一項就是送這個，沒有可以直接按的按鈕。
    /// </summary>
    public static bool SendAgentEvent(AgentId agentId, ulong eventKind, params object[] values)
    {
        var agent = AgentModule.Instance()->GetAgentByInternalId(agentId);
        if (agent == null) return false;

        var result = new AtkValue();
        var atkValues = stackalloc AtkValue[values.Length];
        SetValues(atkValues, values);

        agent->ReceiveEvent(&result, atkValues, (uint)values.Length, eventKind);
        return true;
    }

    /// <summary>視窗資料（AtkValues）第 index 個值的字串內容；不是字串就是空字串。</summary>
    public static string ValueText(AtkUnitBase* addon, int index)
    {
        if (addon == null || index < 0 || index >= addon->AtkValuesCount) return string.Empty;

        var value = addon->AtkValues[index];
        if (value.Type is not (ValueType.String or ValueType.ManagedString or ValueType.String8)) return string.Empty;

        var text = value.String.Value;
        return text == null ? string.Empty : System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)text) ?? string.Empty;
    }

    /// <summary>視窗資料（AtkValues）第 index 個值的整數；超出範圍是 0。</summary>
    public static int ValueInt(AtkUnitBase* addon, int index) =>
        addon == null || index < 0 || index >= addon->AtkValuesCount ? 0 : addon->AtkValues[index].Int;

    /// <summary>
    /// 遊戲文字表（Addon 表）的一列，去掉換行後面的部分。用來比對視窗上的提示，
    /// 不用自己寫死各語言的字串。
    /// </summary>
    public static string Text(uint row)
    {
        if (addonText.TryGetValue(row, out var cached)) return cached;

        var text = Services.Data.GetExcelSheet<Addon>().GetRowOrDefault(row)?.Text.ExtractText() ?? string.Empty;
        var firstLine = text.Split('\n')[0].Trim();
        addonText[row] = firstLine;
        return firstLine;
    }

    /// <summary>視窗裡有沒有哪個文字節點包含這段文字。</summary>
    public static bool ContainsText(AtkUnitBase* addon, string text)
    {
        if (addon == null || string.IsNullOrEmpty(text)) return false;

        var uld = addon->UldManager;
        for (var i = 0; i < uld.NodeListCount; i++)
        {
            var node = uld.NodeList[i];
            if (node == null || node->Type != NodeType.Text) continue;

            var value = ((AtkTextNode*)node)->NodeText.ToString();
            if (value.Contains(text)) return true;
        }

        return false;
    }

    /// <summary>選項清單（SelectString，例如雇員的「金幣管理」「出售玩家所持物品」那種）目前的各項文字；沒開是空清單。</summary>
    public static List<string> SelectStringEntries()
    {
        var list = new List<string>();
        var addon = (AddonSelectString*)Get("SelectString");
        if (addon == null) return list;

        var menu = addon->PopupMenu.PopupMenu;
        for (var i = 0; i < menu.EntryCount; i++)
        {
            var text = menu.EntryNames[i].Value;
            list.Add(text == null ? string.Empty : System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)text) ?? string.Empty);
        }

        return list;
    }

    /// <summary>在選項清單裡點第一個包含任一段文字的選項（跟玩家點一樣送 callback）。找不到回傳 false。</summary>
    public static bool ClickSelectString(params string[] texts)
    {
        var addon = Get("SelectString");
        if (addon == null) return false;

        var entries = SelectStringEntries();
        for (var i = 0; i < entries.Count; i++)
        {
            foreach (var text in texts)
            {
                if (!entries[i].Contains(text)) continue;

                Fire(addon, i);
                return true;
            }
        }

        return false;
    }

    /// <summary>int、uint、bool 轉成遊戲的 AtkValue；其他型別當 0。</summary>
    private static void SetValues(AtkValue* atkValues, object[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            switch (values[i])
            {
                case int v:
                    atkValues[i].SetInt(v);
                    break;
                case uint v:
                    atkValues[i].SetUInt(v);
                    break;
                case bool v:
                    atkValues[i].SetBool(v);
                    break;
                default:
                    atkValues[i].SetInt(0);
                    break;
            }
        }
    }

    /// <summary>有圖示的選項清單（SelectIconString，例如 NPC 列出要交哪個理符）目前的各項文字；沒開是空清單。</summary>
    public static List<string> SelectIconStringEntries()
    {
        var list = new List<string>();
        var addon = (AddonSelectIconString*)Get("SelectIconString");
        if (addon == null) return list;

        var menu = addon->PopupMenu.PopupMenu;
        for (var i = 0; i < menu.EntryCount; i++)
        {
            var text = menu.EntryNames[i].Value;
            list.Add(text == null ? string.Empty : System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)text) ?? string.Empty);
        }

        return list;
    }

    /// <summary>在有圖示的選項清單裡點第一個包含這段文字的選項。找不到回傳 false。</summary>
    public static bool ClickSelectIconString(string text)
    {
        var addon = Get("SelectIconString");
        if (addon == null) return false;

        var entries = SelectIconStringEntries();
        for (var i = 0; i < entries.Count; i++)
        {
            if (!entries[i].Contains(text)) continue;

            Fire(addon, i);
            return true;
        }

        return false;
    }

    /// <summary>確認框（SelectYesno）的提示文字。</summary>
    public static string YesnoPrompt(AtkUnitBase* addon)
    {
        var yesno = (FFXIVClientStructs.FFXIV.Client.UI.AddonSelectYesno*)addon;
        return yesno->PromptText == null ? string.Empty : yesno->PromptText->NodeText.ToString();
    }

    /// <summary>按確認框的「是」。</summary>
    public static bool ClickYes(AtkUnitBase* addon)
    {
        var yesno = (FFXIVClientStructs.FFXIV.Client.UI.AddonSelectYesno*)addon;
        return ClickButton(addon, yesno->YesButton);
    }
}
