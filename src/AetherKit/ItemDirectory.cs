using System.Collections.Generic;

namespace AetherKit;

/// <summary>工具列與面板裡的一個項目，與它在哪（設定頁顯示用）。</summary>
public readonly record struct ItemEntry(StatefulItem Item, string Location, PanelConfig? OpensPanel);

/// <summary>
/// 列出所有存在設定裡的項目：工具列上的卡片，與每個面板裡自訂區塊的項目。
/// 快捷鍵、游標提示、總覽頁都從這裡找，「有哪些項目」只有一份答案。
///
/// 套裝列表與情感動作的內容是每次從遊戲讀的，不存在設定裡，所以不在這份清單上。
/// </summary>
public static class ItemDirectory
{
    public static IEnumerable<ItemEntry> All(Configuration config)
    {
        var bar = config.TopBar.Items;

        foreach (var item in bar)
            yield return new ItemEntry(item, "工具列", item.Kind == ItemKind.Panel ? item.Panel : null);

        foreach (var opener in bar)
        {
            if (opener.Kind != ItemKind.Panel || opener.Panel == null) continue;

            var panelName = string.IsNullOrEmpty(opener.Current.Label) ? "面板" : opener.Current.Label;

            foreach (var section in opener.Panel.Sections)
            {
                var sectionName = string.IsNullOrEmpty(section.Title) ? "自訂區塊" : section.Title;

                foreach (var item in section.Items)
                    yield return new ItemEntry(item, $"{panelName} › {sectionName}", null);
            }
        }
    }

    /// <summary>會被按的項目（展開面板的卡片不算）。</summary>
    public static IEnumerable<ItemEntry> Pressable(Configuration config)
    {
        foreach (var entry in All(config))
        {
            if (entry.OpensPanel == null)
                yield return entry;
        }
    }

    public static StatefulItem? Find(Configuration config, System.Guid id)
    {
        foreach (var entry in All(config))
        {
            if (entry.Item.Id == id)
                return entry.Item;
        }

        return null;
    }
}
