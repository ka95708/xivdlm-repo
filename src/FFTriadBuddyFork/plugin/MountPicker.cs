using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;

namespace TriadBuddyPlugin
{
    /// <summary>選坐騎的下拉選單：第一項是坐騎隨機，下面列出已取得的坐騎（fork 新增）。</summary>
    internal static unsafe class MountPicker
    {
        private static readonly List<(uint Id, string Name)> owned = new();
        private static string filter = string.Empty;

        public static bool Draw(string label, uint current, out uint selected)
        {
            selected = current;
            string preview = current == 0 ? "坐騎隨機" : GetName(current);

            if (!ImGui.BeginCombo(label, preview))
            {
                return false;
            }

            if (ImGui.IsWindowAppearing())
            {
                RefreshOwned();
                filter = string.Empty;
            }

            bool changed = false;
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##mountFilter", "搜尋", ref filter, 32);

            if (ImGui.Selectable("坐騎隨機", current == 0))
            {
                selected = 0;
                changed = current != 0;
            }

            foreach (var (id, name) in owned)
            {
                if (filter.Length > 0 && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ImGui.Selectable($"{name}##mount{id}", current == id))
                {
                    selected = id;
                    changed = current != id;
                }
            }

            ImGui.EndCombo();
            return changed;
        }

        private static void RefreshOwned()
        {
            owned.Clear();
            var player = PlayerState.Instance();
            if (player == null)
            {
                return;
            }

            foreach (var row in Service.dataManager.GetExcelSheet<Mount>())
            {
                if (row.RowId == 0)
                {
                    continue;
                }

                var name = row.Singular.ExtractText();
                if (name.Length > 0 && player->IsMountUnlocked(row.RowId))
                {
                    owned.Add((row.RowId, name));
                }
            }

            owned.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        }

        private static string GetName(uint id) =>
            Service.dataManager.GetExcelSheet<Mount>().GetRowOrDefault(id)?.Singular.ExtractText() is { Length: > 0 } name ? name : $"#{id}";
    }
}
