using System;
using System.Numerics;
using Barkeep.Game;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace Barkeep.UI;

/// <summary>
/// 條件組編輯器。條件組是跨快捷列共用的，所以放在總管視窗自己的分頁，
/// 不塞進某一條快捷列的設定裡。
/// </summary>
public class ConditionEditor
{
    private static readonly ConditionKind[] Kinds = Enum.GetValues<ConditionKind>();

    private readonly Plugin plugin;
    private ConditionSet? selected;

    private Theme Theme => plugin.Config.Theme;

    public ConditionEditor(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Select(ConditionSet? set)
    {
        if (set != null)
            selected = set;
    }

    public void Draw()
    {
        if (!ImGui.BeginTable("conditions", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
            return;

        ImGui.TableSetupColumn("list", ImGuiTableColumnFlags.WidthFixed, 180f);
        ImGui.TableSetupColumn("editor", ImGuiTableColumnFlags.WidthStretch);

        ImGui.TableNextColumn();
        if (ImGui.BeginChild("setList", Vector2.Zero))
            DrawList();
        ImGui.EndChild();

        ImGui.TableNextColumn();
        if (ImGui.BeginChild("setEditor", Vector2.Zero))
            DrawEditor();
        ImGui.EndChild();

        ImGui.EndTable();
    }

    private void DrawList()
    {
        if (ImGui.Button("新增條件組"))
        {
            var set = new ConditionSet();
            plugin.Config.ConditionSets.Add(set);
            plugin.Save();
            selected = set;
        }

        ImGui.Spacing();

        foreach (var set in plugin.Config.ConditionSets)
        {
            ImGui.PushID(set.Id.ToString());

            var met = ConditionEvaluator.IsVisible(plugin.Config, set.Id);
            ImGui.TextColored(met ? Theme.Positive : Theme.TextMuted, met ? "●" : "○");
            ImGui.SameLine();

            if (ImGui.Selectable(set.Name, ReferenceEquals(set, selected)))
                selected = set;

            ImGui.PopID();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("● = 目前成立");
    }

    private void DrawEditor()
    {
        if (selected == null)
        {
            ImGui.TextDisabled("從左邊選一個條件組，或新增一個。");
            return;
        }

        var set = selected;

        var name = set.Name;
        if (ImGui.InputText("名稱", ref name, 128))
        {
            set.Name = name;
            plugin.Save();
        }

        var requireAll = set.RequireAll ? 0 : 1;
        if (ImGui.Combo("組合方式", ref requireAll, "全部成立 (AND)\0任一成立 (OR)\0"))
        {
            set.RequireAll = requireAll == 0;
            plugin.Save();
        }

        ImGui.Separator();

        for (var i = 0; i < set.Entries.Count; i++)
        {
            ImGui.PushID(i);

            if (!DrawEntry(set.Entries[i]))
            {
                set.Entries.RemoveAt(i);
                plugin.Save();
                ImGui.PopID();
                break;
            }

            ImGui.PopID();
            ImGui.Separator();
        }

        if (ImGui.Button("新增條件"))
        {
            set.Entries.Add(new ConditionEntry());
            plugin.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("刪除這個條件組"))
        {
            // 用到這組的地方要解除綁定，不然會指向不存在的組
            if (plugin.Config.TopBar.ConditionSetId == set.Id)
                plugin.Config.TopBar.ConditionSetId = Guid.Empty;

            plugin.Config.ConditionSets.Remove(set);
            selected = null;
            plugin.Save();
        }
    }

    /// <summary>回傳 false 代表使用者按了移除。</summary>
    private bool DrawEntry(ConditionEntry entry)
    {
        var met = ConditionEvaluator.Test(entry) != entry.Negate;
        ImGui.TextColored(met ? Theme.Positive : Theme.TextMuted, met ? "●" : "○");
        ImGui.SameLine();

        var kind = Array.IndexOf(Kinds, entry.Kind);
        ImGui.SetNextItemWidth(160);

        if (ImGui.BeginCombo("##kind", ConditionEvaluator.Describe(entry.Kind)))
        {
            for (var i = 0; i < Kinds.Length; i++)
            {
                if (!ImGui.Selectable(ConditionEvaluator.Describe(Kinds[i]), i == kind)) continue;

                entry.Kind = Kinds[i];
                entry.Arg = 0;
                plugin.Save();
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();

        var negate = entry.Negate;
        if (ImGui.Checkbox("反轉", ref negate))
        {
            entry.Negate = negate;
            plugin.Save();
        }

        ImGui.SameLine();
        var keep = !ImGui.SmallButton("移除");

        switch (entry.Kind)
        {
            case ConditionKind.Job:
                DrawJobPicker(entry);
                break;

            case ConditionKind.Territory:
                DrawTerritoryPicker(entry);
                break;
        }

        return keep;
    }

    private void DrawJobPicker(ConditionEntry entry)
    {
        var sheet = Services.Data.GetExcelSheet<ClassJob>();
        if (sheet == null) return;

        var current = sheet.GetRowOrDefault(entry.Arg)?.Name.ExtractText() ?? "（未選擇）";

        ImGui.Indent();
        ImGui.SetNextItemWidth(220);

        if (ImGui.BeginCombo("職業", current))
        {
            foreach (var job in sheet)
            {
                if (job.RowId == 0) continue;

                var label = job.Name.ExtractText();
                if (string.IsNullOrEmpty(label)) continue;

                if (!ImGui.Selectable(label, job.RowId == entry.Arg)) continue;

                entry.Arg = job.RowId;
                plugin.Save();
            }

            ImGui.EndCombo();
        }

        ImGui.Unindent();
    }

    private void DrawTerritoryPicker(ConditionEntry entry)
    {
        ImGui.Indent();

        var id = (int)entry.Arg;
        ImGui.SetNextItemWidth(120);

        if (ImGui.InputInt("地圖 ID", ref id))
        {
            entry.Arg = (uint)Math.Max(0, id);
            plugin.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("用目前所在地"))
        {
            entry.Arg = Services.ClientState.TerritoryType;
            plugin.Save();
        }

        ImGui.SameLine();
        ImGui.TextDisabled(TerritoryName(entry.Arg));
        ImGui.Unindent();
    }

    private static string TerritoryName(uint id)
    {
        if (id == 0) return "（未選擇）";

        var row = Services.Data.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(id);
        var place = row?.PlaceName.ValueNullable?.Name.ExtractText();

        return string.IsNullOrEmpty(place) ? $"#{id}" : place;
    }
}
