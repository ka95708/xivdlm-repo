using AetherKit.Game;
using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace AetherKit.UI;

/// <summary>
/// 工具列的兩個右鍵選單，外觀跟面板的選單一樣：
///   卡片選單 — 編輯模式下在卡片上右鍵。上半跟面板項目共用（狀態、名稱、圖示…），
///              下半是工具列才有的設定（類型、顯示方式、快捷鍵）。
///   工具列選單 — 其他地方右鍵。編輯模式下多了新增與顯示條件。
/// </summary>
public static class TopBarMenu
{
    public const string ChipId = "chipMenu";
    public const string BarId = "barMenu";

    private static readonly string[] Styles = { "圖示", "文字", "都有" };

    /// <summary>回傳 true 代表有改動。added = 從「工具列 ▸」新增的項目，呼叫端接著幫它開卡片選單。</summary>
    public static bool DrawChip(Plugin plugin, TopBarItem item, List<TopBarItem> items, out TopBarItem? added)
    {
        added = null;
        if (!Menu.Begin(ChipId)) return false;

        var changed = false;
        var isButton = item.Kind == ItemKind.Button;
        var isToggle = item.Kind == ItemKind.Toggle;
        var name = item.Current.Label;

        Menu.Header(string.IsNullOrEmpty(name) ? "工具列項目" : name);

        // 面板只用得到一個狀態，展開面板的卡片不需要開關
        if (isButton)
            changed |= ItemPopup.DrawStates(item);

        Menu.Section(isButton && item.Cycles ? $"狀態 {item.StateIndex + 1} 設定" : "外觀");
        changed |= ItemPopup.DrawFields(plugin, item.Current, subLabel: false, command: isButton);

        if (isToggle)
            changed |= DrawToggle(plugin, item);

        Menu.Section("工具列設定");
        changed |= DrawKind(item);
        changed |= DrawStyle(item);

        if (item.Kind != ItemKind.Panel)
            changed |= DrawEnableCondition(plugin, item);

        // 面板卡片的快捷鍵是開關它的面板
        changed |= ItemPopup.DrawHotkey(plugin, item, isButton ? "快捷鍵" : "快捷鍵（開關面板）");

        Menu.Separator();

        // 編輯模式下卡片幾乎佔滿整條，工具列選單很難點到空白處叫出來，
        // 所以在每張卡片的選單裡也放一份，收在子選單裡不佔版面
        if (ImGui.BeginMenu("工具列"))
        {
            added = DrawBarActions(plugin, items.IndexOf(item));
            ImGui.EndMenu();
        }

        Menu.Separator();

        if (Menu.Item("複製這個項目"))
        {
            // 放在右邊，快捷鍵不複製（免得兩個一起觸發）
            var index = items.IndexOf(item);
            items.Insert(index + 1, item.Duplicate());
            changed = true;
        }

        if (Menu.Danger("刪除這個項目"))
        {
            // 面板存在項目裡面，刪項目就一起刪掉，不會留下孤兒
            items.Remove(item);
            changed = true;
        }

        Menu.End();
        return changed;
    }

    private static bool DrawKind(TopBarItem item)
    {
        var changed = false;

        ImGui.TextUnformatted("類型");
        ImGui.SameLine();

        if (ImGui.RadioButton("滑過展開面板", item.Kind == ItemKind.Panel))
        {
            // 從按鈕改回面板時，原本的面板內容還在就沿用
            item.Kind = ItemKind.Panel;
            item.Panel ??= new PanelConfig();
            changed = true;
        }

        ImGui.SameLine();

        if (ImGui.RadioButton("按鈕", item.Kind == ItemKind.Button))
        {
            item.Kind = ItemKind.Button;
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("開關", item.Kind == ItemKind.Toggle))
        {
            item.Kind = ItemKind.Toggle;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 開關：打開的指令、關掉的指令、狀態來源。
    /// 狀態來源「自己記」= 按一次換一次；「跟著遊戲或插件」= 照那一條條件顯示，從別的地方切換也會同步。
    /// </summary>
    public static bool DrawToggle(Plugin plugin, StatefulItem item)
    {
        var changed = false;
        var theme = plugin.Config.Theme;

        Menu.Section("開關");

        var on = item.Current.Command;
        if (ItemPopup.DrawCommand(plugin, "打開的指令", ref on))
        {
            item.Current.Command = on;
            changed = true;
        }

        var off = item.OffCommand;
        if (ItemPopup.DrawCommand(plugin, "關掉的指令", ref off))
        {
            item.OffCommand = off;
            changed = true;
        }

        ItemPopup.DrawCommandHelp(plugin, "開著時按下執行關掉的指令，關著時執行打開的指令；關著時自動變灰");

        Menu.Section("狀態來源");

        if (ImGui.RadioButton("自己記", item.StateSource == null))
        {
            item.StateSource = null;
            changed = true;
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("跟著遊戲、插件或 AetherKit", item.StateSource != null) && item.StateSource == null)
        {
            item.StateSource = new ConditionEntry { Kind = ConditionKind.AetherKit };
            changed = true;
        }

        if (item.StateSource is { } source)
        {
            var before = source.Kind == ConditionKind.AetherKit ? source.Text : null;
            ConditionEditor.DrawEntry(plugin, source, removable: false);
            ImGui.TextColored(theme.TextSecondary, "這一條成立 = 開著（勾反轉就反過來）");

            if (source.Kind == ConditionKind.AetherKit && source.Text != before)
                changed |= FillAetherKitCommands(item, before, source.Text);
        }
        else
        {
            var isOn = item.IsOn;
            if (ImGui.Checkbox("現在是開著", ref isOn))
            {
                item.IsOn = isOn;
                changed = true;
            }

            ImGui.TextColored(theme.TextSecondary, "按一次換一次；從別的地方切換的話，在這裡改回來");
        }

        var state = ToolbarItemState.IsOn(item);
        ImGui.TextColored(state == true ? theme.Positive : theme.TextSecondary,
            state switch { true => "目前：開著", false => "目前：關著", _ => "目前：讀不到（按鈕會變灰、不能按）" });

        return changed;
    }

    /// <summary>
    /// 狀態來源選了 AetherKit 開關：把它的打開／關掉指令填進按鈕。
    /// 只在指令還是空的、或還是上一個開關帶進來的指令時才填，自己改過的不蓋掉。
    /// </summary>
    private static bool FillAetherKitCommands(StatefulItem item, string? oldKey, string newKey)
    {
        if (AetherKitStates.Find(newKey) is not { } state || state.OnCommand.Length == 0) return false;

        var old = oldKey == null ? null : AetherKitStates.Find(oldKey);
        bool Replaceable(string command, string? previous) => command.Trim().Length == 0 || command == previous;

        if (!Replaceable(item.Current.Command, old?.OnCommand) || !Replaceable(item.OffCommand, old?.OffCommand)) return false;

        item.Current.Command = state.OnCommand;
        item.OffCommand = state.OffCommand;
        return true;
    }

    /// <summary>可以按的條件：選一個條件組，不成立時變灰、不能按（或藏起來）。工具列卡片、面板項目共用。</summary>
    public static bool DrawEnableCondition(Plugin plugin, StatefulItem item)
    {
        var changed = false;
        var set = plugin.Config.FindConditionSet(item.EnableConditionSetId);

        ImGui.SetNextItemWidth(150);
        if (ImGui.BeginCombo("可以按的條件", set?.Name ?? "一直可以按"))
        {
            if (ImGui.Selectable("一直可以按", item.EnableConditionSetId == Guid.Empty))
            {
                item.EnableConditionSetId = Guid.Empty;
                changed = true;
            }

            foreach (var candidate in plugin.Config.ConditionSets)
            {
                if (!ImGui.Selectable(candidate.Name, candidate.Id == item.EnableConditionSetId)) continue;

                item.EnableConditionSetId = candidate.Id;
                changed = true;
            }

            ImGui.EndCombo();
        }

        if (set != null)
        {
            var hide = item.HideWhenDisabled;
            if (ImGui.Checkbox("不成立時藏起來（不然是變灰）", ref hide))
            {
                item.HideWhenDisabled = hide;
                changed = true;
            }
        }

        ImGui.TextColored(plugin.Config.Theme.TextSecondary, "條件組在工具列設定的「條件組」分頁新增，可以用「插件已載入」");
        return changed;
    }

    private static bool DrawStyle(TopBarItem item)
    {
        var changed = false;

        ImGui.TextUnformatted("顯示");

        for (var i = 0; i < Styles.Length; i++)
        {
            ImGui.SameLine();

            if (!ImGui.RadioButton(Styles[i], (int)item.Style == i)) continue;

            item.Style = (ItemStyle)i;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 工具列選單。編輯模式下可以新增（加在按右鍵的位置）、選顯示條件。
    /// 回傳新增的項目，呼叫端接著幫它開卡片選單，新增完馬上就能改名字和圖示。
    /// </summary>
    public static TopBarItem? DrawBar(Plugin plugin, int insertAt)
    {
        if (!Menu.Begin(BarId)) return null;

        TopBarItem? added = null;

        Menu.Header("工具列");

        if (plugin.Toolbar.EditMode)
        {
            added = Insert(plugin, "＋ 面板", "＋ 按鈕", insertAt);
            Menu.Separator();
            DrawCondition(plugin, plugin.Config.TopBar);
            Menu.Separator();
        }

        DrawModeAndSettings(plugin);

        Menu.End();
        return added;
    }

    /// <summary>卡片選單裡的「工具列 ▸」：新增在這張卡片左邊或右邊，其餘跟工具列選單一樣。</summary>
    private static TopBarItem? DrawBarActions(Plugin plugin, int index)
    {
        Menu.Section("在左邊新增");
        var added = Insert(plugin, "＋ 面板##left", "＋ 按鈕##left", index);

        Menu.Section("在右邊新增");
        added ??= Insert(plugin, "＋ 面板##right", "＋ 按鈕##right", index + 1);

        Menu.Separator();
        DrawCondition(plugin, plugin.Config.TopBar);
        Menu.Separator();

        DrawModeAndSettings(plugin);
        return added;
    }

    /// <summary>兩個新增選項，插在 insertAt。回傳新增的項目，沒按就是 null。</summary>
    private static TopBarItem? Insert(Plugin plugin, string panelLabel, string buttonLabel, int insertAt)
    {
        TopBarItem? added = null;

        if (Menu.Primary(panelLabel))
            added = TopBarItem.NewPanel("面板");

        if (Menu.Primary(buttonLabel))
            added = TopBarItem.NewButton("按鈕");

        if (added == null) return null;

        var items = plugin.Config.TopBar.Items;
        items.Insert(Math.Clamp(insertAt, 0, items.Count), added);
        plugin.Save();
        return added;
    }

    private static void DrawModeAndSettings(Plugin plugin)
    {
        if (Menu.Item("編輯模式", plugin.Toolbar.EditMode))
            plugin.Toolbar.EditMode = !plugin.Toolbar.EditMode;

        if (Menu.Item("開啟設定"))
            plugin.OpenConfig();
    }

    private static void DrawCondition(Plugin plugin, TopBarConfig bar)
    {
        var set = plugin.Config.FindConditionSet(bar.ConditionSetId);

        ImGui.SetNextItemWidth(150);

        if (ImGui.BeginCombo("顯示條件", set?.Name ?? "永遠顯示"))
        {
            if (ImGui.Selectable("永遠顯示", bar.ConditionSetId == Guid.Empty))
            {
                bar.ConditionSetId = Guid.Empty;
                plugin.Save();
            }

            foreach (var candidate in plugin.Config.ConditionSets)
            {
                if (!ImGui.Selectable(candidate.Name, candidate.Id == bar.ConditionSetId)) continue;

                bar.ConditionSetId = candidate.Id;
                plugin.Save();
            }

            ImGui.EndCombo();
        }

        if (Menu.Item("管理條件組…"))
            plugin.OpenConditions(set);
    }
}
