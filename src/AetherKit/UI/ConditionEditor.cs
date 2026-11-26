using System;
using System.Numerics;
using AetherKit.Game;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace AetherKit.UI;

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

            if (!DrawEntry(plugin, set.Entries[i]))
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

    /// <summary>
    /// 一條條件：種類、反轉、移除，需要參數的再多一行。條件組、工具列開關的狀態來源共用。
    /// 回傳 false 代表使用者按了移除（removable = false 時不會有移除按鈕）。
    /// </summary>
    public static bool DrawEntry(Plugin plugin, ConditionEntry entry, bool removable = true)
    {
        var theme = plugin.Config.Theme;
        var known = ConditionEvaluator.TryTest(entry, out var value);
        var met = (known && value) != entry.Negate;
        ImGui.TextColored(met ? theme.Positive : theme.TextMuted, known ? (met ? "●" : "○") : "？");
        if (!known && ImGui.IsItemHovered()) ImGui.SetTooltip("讀不到：插件沒載入，或查詢名稱不對");
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
                entry.Text = string.Empty;
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

        var keep = true;
        if (removable)
        {
            ImGui.SameLine();
            keep = !ImGui.SmallButton("移除");
        }

        switch (entry.Kind)
        {
            case ConditionKind.Job:
                DrawJobPicker(plugin, entry);
                break;

            case ConditionKind.Territory:
                DrawTerritoryPicker(plugin, entry);
                break;

            case ConditionKind.PluginLoaded:
                DrawText(plugin, entry, "插件名稱", "例如 WrathCombo、RotationSolver（插件的內部名稱）");
                break;

            case ConditionKind.PluginQuery:
                DrawText(plugin, entry, "查詢名稱", "插件提供、回答是或否的查詢，例如 WrathCombo.GetAutoRotationState");
                break;

            case ConditionKind.AetherKit:
                DrawAetherKitPicker(plugin, entry);
                break;
        }

        return keep;
    }

    /// <summary>AetherKit 開關：照分組列出（自動流程、模組、小功能、核心能力、工具列）。</summary>
    private static void DrawAetherKitPicker(Plugin plugin, ConditionEntry entry)
    {
        ImGui.Indent();

        var current = AetherKitStates.Find(entry.Text) is { } chosen
            ? $"{chosen.Group}：{chosen.Name}"
            : entry.Text.Length == 0 ? "（未選擇）" : $"{entry.Text}（找不到）";

        ImGui.SetNextItemWidth(300);
        // 標籤要有自己的 ID：工具列選單裡已經有一個叫「開關」的選項（按鈕類型），同 ID 會吃掉點擊
        if (ImGui.BeginCombo("跟著哪一個##aetherkit-state", current, ImGuiComboFlags.HeightLarge))
        {
            string? group = null;
            foreach (var state in AetherKitStates.All())
            {
                if (state.Group != group)
                {
                    if (group != null) ImGui.Separator();
                    ImGui.TextDisabled(state.Group);
                    group = state.Group;
                }

                if (!ImGui.Selectable($"{state.Name}##{state.Key}", state.Key == entry.Text)) continue;

                entry.Text = state.Key;
                plugin.Save();
            }

            ImGui.EndCombo();
        }

        ImGui.Unindent();
    }

    private static void DrawJobPicker(Plugin plugin, ConditionEntry entry)
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

    private static void DrawTerritoryPicker(Plugin plugin, ConditionEntry entry)
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

    private static void DrawText(Plugin plugin, ConditionEntry entry, string label, string hint)
    {
        ImGui.Indent();

        var text = entry.Text;
        ImGui.SetNextItemWidth(260);
        if (ImGui.InputTextWithHint(label, hint, ref text, 128))
        {
            entry.Text = text.Trim();
            plugin.Save();
        }

        if (entry.Kind == ConditionKind.PluginLoaded && entry.Text.Length > 0 && !ConditionEvaluator.IsPluginLoaded(entry.Text))
            ImGui.TextDisabled("目前沒有載入這個名稱的插件（名稱要跟插件的內部名稱一樣）");

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
