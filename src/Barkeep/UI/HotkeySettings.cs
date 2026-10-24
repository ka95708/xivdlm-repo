using System.Linq;
using Barkeep.Game;
using Dalamud.Bindings.ImGui;

namespace Barkeep.UI;

/// <summary>
/// 管理視窗的「快捷鍵」分頁：所有設了快捷鍵的項目一覽。快捷鍵平常在項目的右鍵選單裡設，
/// 這裡是用來看「哪些鍵已經用掉、有沒有撞到」，也可以直接重設或清除。
/// </summary>
public class HotkeySettings
{
    private readonly Plugin plugin;

    public HotkeySettings(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        ImGui.TextDisabled("快捷鍵在編輯模式下，於工具列卡片或面板項目上按右鍵設定。這裡列出所有已設定的。");

        var entries = ItemDirectory.All(plugin.Config).Where(entry => entry.Item.Hotkey.IsSet).ToList();

        if (entries.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("還沒有設定任何快捷鍵。");
            return;
        }

        if (!ImGui.BeginTable("hotkeys", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
            return;

        ImGui.TableSetupColumn("位置", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("項目", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("快捷鍵", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, 90f);
        ImGui.TableHeadersRow();

        var theme = plugin.Config.Theme;

        foreach (var entry in entries)
        {
            var item = entry.Item;
            var hotkey = item.Hotkey;
            var conflict = entries.Any(other => !ReferenceEquals(other.Item, item) && other.Item.Hotkey.SameAs(hotkey));
            var capturing = ReferenceEquals(plugin.Hotkeys.Capturing, hotkey);

            ImGui.PushID(item.Id.ToString());
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(entry.Location);

            ImGui.TableNextColumn();
            var name = string.IsNullOrEmpty(item.Current.Label) ? "（未命名）" : item.Current.Label;
            ImGui.TextUnformatted(entry.OpensPanel != null ? $"{name}（開關面板）" : name);

            ImGui.TableNextColumn();
            var text = capturing ? "按下想用的鍵…" : HotkeyManager.Describe(hotkey);

            if (conflict)
                ImGui.TextColored(theme.Danger, $"{text}（重複）");
            else
                ImGui.TextUnformatted(text);

            ImGui.TableNextColumn();

            if (ImGui.SmallButton("重設"))
                plugin.Hotkeys.Capturing = capturing ? null : hotkey;

            ImGui.SameLine();

            if (ImGui.SmallButton("清除"))
            {
                hotkey.Clear();
                plugin.Save();
            }

            ImGui.PopID();
        }

        ImGui.EndTable();

        if (entries.Any(entry => entries.Any(other => !ReferenceEquals(other.Item, entry.Item) && other.Item.Hotkey.SameAs(entry.Item.Hotkey))))
        {
            ImGui.Spacing();
            ImGui.TextColored(theme.Danger, "標「重複」的是同一組鍵，按下時會一起觸發。");
        }
    }
}
