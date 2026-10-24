using System;
using System.Collections.Generic;
using System.Numerics;
using Barkeep.Game;
using Dalamud.Bindings.ImGui;

namespace Barkeep.UI;

/// <summary>
/// 面板項目的右鍵選單：屬性直接在這裡改，跟著游標開在項目旁邊而不是另開視窗 —
/// 就地編輯的重點就是看著結果改。外觀跟其他右鍵選單一致。
///
/// 項目可以有多個狀態（開關、循環），跟工具列按鈕一樣。選單編輯的是「目前狀態」，
/// 用 ◀ ▶ 切換要改哪一個 — 切換時畫面上的項目也跟著變，看得到每個狀態長怎樣。
/// 狀態列與欄位是公開的，工具列卡片的選單（TopBarMenu）也用同一份。
/// </summary>
public static class ItemPopup
{
    private const string Id = "itemPopup";
    private const float FieldWidth = 220f;

    public static void Open() => ImGui.OpenPopup(Id);

    /// <summary>回傳 true 代表有改動。</summary>
    public static bool Draw(Plugin plugin, PanelItem item, List<PanelItem> list, int index)
    {
        if (!Menu.Begin(Id)) return false;

        var changed = false;
        var state = item.Current;

        Menu.Header(string.IsNullOrEmpty(state.Label) ? "項目" : state.Label);

        changed |= DrawStates(item);

        Menu.Section(item.Cycles ? $"狀態 {item.StateIndex + 1} 設定" : "項目設定");
        changed |= DrawFields(plugin, state);

        Menu.Separator();
        changed |= DrawHotkey(plugin, item, "快捷鍵");

        Menu.Separator();

        if (Menu.Danger("刪除這個項目") && index >= 0 && index < list.Count)
        {
            list.RemoveAt(index);
            changed = true;
        }

        Menu.End();
        return changed;
    }

    /// <summary>
    /// 快捷鍵：按一下按鈕，再直接按想用的鍵；選單不用關，按完就看到結果。
    /// 工具列卡片與面板項目共用，跟別的項目撞到同一組鍵時當場提醒。
    /// </summary>
    public static bool DrawHotkey(Plugin plugin, StatefulItem item, string label)
    {
        var changed = false;
        var hotkey = item.Hotkey;
        var capturing = ReferenceEquals(plugin.Hotkeys.Capturing, hotkey);

        ImGui.TextUnformatted(label);
        ImGui.SameLine();

        var text = capturing ? "按下想用的鍵…（Esc 清除）" : HotkeyManager.Describe(hotkey);
        if (ImGui.SmallButton($"{text}##hotkey"))
            plugin.Hotkeys.Capturing = capturing ? null : hotkey;

        if (!hotkey.IsSet) return false;

        ImGui.SameLine();
        if (ImGui.SmallButton("清除##hotkey"))
        {
            hotkey.Clear();
            changed = true;
        }

        var blocks = hotkey.BlocksGame;
        if (ImGui.Checkbox("攔截此按鍵，不傳給遊戲", ref blocks))
        {
            hotkey.BlocksGame = blocks;
            changed = true;
        }

        foreach (var other in ItemDirectory.All(plugin.Config))
        {
            if (ReferenceEquals(other.Item, item) || !other.Item.Hotkey.SameAs(hotkey)) continue;

            var name = string.IsNullOrEmpty(other.Item.Current.Label) ? "（未命名）" : other.Item.Current.Label;
            ImGui.TextColored(plugin.Config.Theme.Danger, $"跟「{other.Location} › {name}」是同一組鍵，會一起觸發");
        }

        return changed;
    }

    /// <summary>狀態列：只有一個狀態時只給「新增狀態」，多個時多了切換與移除。</summary>
    public static bool DrawStates(StatefulItem item)
    {
        Menu.Section("狀態（兩個以上就是開關／循環）");

        var changed = false;

        if (item.Cycles)
        {
            if (ImGui.ArrowButton("##prev", ImGuiDir.Left))
            {
                item.StateIndex = (item.StateIndex + item.States.Count - 1) % item.States.Count;
                changed = true;
            }

            ImGui.SameLine();
            ImGui.TextUnformatted($"{item.StateIndex + 1} / {item.States.Count}");
            ImGui.SameLine();

            if (ImGui.ArrowButton("##next", ImGuiDir.Right))
            {
                item.Advance();
                changed = true;
            }

            ImGui.SameLine();
        }

        if (ImGui.SmallButton("＋ 新增狀態"))
        {
            // 複製目前的狀態當起點，通常只差圖示、灰階與指令
            item.States.Insert(item.StateIndex + 1, item.Current.Clone());
            item.StateIndex++;
            changed = true;
        }

        if (item.Cycles)
        {
            ImGui.SameLine();

            if (ImGui.SmallButton("移除這個狀態"))
            {
                item.States.RemoveAt(item.StateIndex);
                item.StateIndex = Math.Clamp(item.StateIndex, 0, item.States.Count - 1);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// 名稱、副標、圖示、灰階、指令。工具列卡片只有一行字所以不給副標；
    /// 展開面板的卡片不執行東西所以不給指令。
    /// </summary>
    public static bool DrawFields(Plugin plugin, ActionItem state, bool subLabel = true, bool command = true)
    {
        var changed = false;

        ImGui.SetNextItemWidth(FieldWidth);

        var label = state.Label;
        if (ImGui.InputText("名稱", ref label, 128))
        {
            state.Label = label;
            changed = true;
        }

        if (subLabel)
        {
            ImGui.SetNextItemWidth(FieldWidth);

            var sub = state.SubLabel;
            if (ImGui.InputText("副標", ref sub, 128))
            {
                state.SubLabel = sub;
                changed = true;
            }
        }

        ImGui.SetNextItemWidth(FieldWidth - 70);

        var icon = (int)state.IconId;
        if (ImGui.InputInt("##icon", ref icon, 0))
        {
            state.IconId = (uint)Math.Max(0, icon);
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("瀏覽…"))
            plugin.OpenIconBrowser(state);

        ImGui.SameLine();
        ImGui.TextUnformatted("圖示");

        var grayscale = state.Grayscale;
        if (ImGui.Checkbox("灰階", ref grayscale))
        {
            state.Grayscale = grayscale;
            changed = true;
        }

        if (!command)
            return changed;

        var text = state.Command;
        if (ImGui.InputTextMultiline("指令", ref text, 4096, new Vector2(FieldWidth, 70)))
        {
            state.Command = text;
            changed = true;
        }

        // 打的當下就標出來，不要等按下去沒反應才發現
        var theme = plugin.Config.Theme;
        var invalid = CommandExecutor.InvalidLines(text);

        if (invalid.Count > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, theme.Danger);
            ImGui.TextUnformatted("這幾行不是 / 開頭，執行時會略過：");

            foreach (var line in invalid)
                ImGui.BulletText(line);

            ImGui.PopStyleColor();
        }

        ImGui.PushStyleColor(ImGuiCol.Text, theme.TextSecondary);
        ImGui.TextUnformatted("每行一個指令，都要 / 開頭，// 開頭是註解\n要說話請用 /s、/p 這類指令\n按一次執行目前狀態的指令，然後換下一個");
        ImGui.PopStyleColor();

        return changed;
    }
}
