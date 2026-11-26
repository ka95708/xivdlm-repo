using System;
using System.Linq;
using Dalamud.Bindings.ImGui;
using AetherKit.Core;

namespace AetherKit.UI;

/// <summary>
/// 核心能力頁：最上面是操作鎖（現在誰在操作角色）與 TextAdvance 的狀態，
/// 中間是每項能力的模式與借用者，下面是各能力的設定。
/// </summary>
public sealed class CorePage
{
    private static readonly string[] ModeLabels = { "關", "只在接管時", "常開" };

    private readonly CoreServices core;

    public CorePage(CoreServices core)
    {
        this.core = core;
    }

    public void Draw()
    {
        DrawLock();
        ImGui.Spacing();
        DrawTable();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawSettings();
    }

    private void DrawLock()
    {
        var @lock = core.Lock;
        if (@lock.Busy)
        {
            var minutes = (int)(DateTime.Now - @lock.Since).TotalMinutes;
            ImGui.TextUnformatted($"操作中：{@lock.Label}（已執行 {minutes} 分鐘）");
            ImGui.SameLine();
            if (ImGui.SmallButton("停止")) @lock.RequestStop();
            ImGui.SameLine();
            if (ImGui.SmallButton("強制清除")) @lock.ForceClear();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("流程沒有正常結束、鎖一直沒放掉時才用。");
        }
        else
        {
            ImGui.TextDisabled("操作中：沒有");
        }

        ImGui.TextDisabled(core.PausingTextAdvance
            ? "TextAdvance：AetherKit 的對話類能力作用中，已請它暫停"
            : "TextAdvance：沒有暫停它");

        if (core.Paused)
        {
            ImGui.SameLine();
            ImGui.TextUnformatted("　（暫停鍵按住中）");
        }
    }

    private void DrawTable()
    {
        if (!ImGui.BeginTable("capabilities", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH)) return;

        ImGui.TableSetupColumn("能力", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("模式", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("目前", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, 80);
        ImGui.TableHeadersRow();

        foreach (var capability in core.All)
        {
            ImGui.PushID(capability.Id);
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(capability.Name);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(capability.Description);

            ImGui.TableNextColumn();
            var mode = (int)capability.Mode;
            ImGui.SetNextItemWidth(-1);
            if (ImGui.Combo("##mode", ref mode, ModeLabels, ModeLabels.Length))
                capability.Mode = (CapabilityMode)mode;

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Describe(capability));

            ImGui.TableNextColumn();
            if (capability.Leases.Count > 0 && ImGui.SmallButton("強制歸還"))
                capability.ForceReleaseAll();

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private static string Describe(Capability capability)
    {
        var owners = capability.Leases.Where(l => !l.Suppress).Select(l => l.Owner).Distinct().ToList();
        var suppressors = capability.Leases.Where(l => l.Suppress).Select(l => l.Owner).Distinct().ToList();

        var text = capability.Active ? "作用中" : "閒置";
        if (owners.Count > 0) text += $"，借用：{string.Join("、", owners)}";
        if (suppressors.Count > 0) text += $"，被要求暫停：{string.Join("、", suppressors)}";
        if (capability.Note != null) text += $"（{capability.Note}）";

        return text;
    }

    private void DrawSettings()
    {
        CoreSettingsUi.PauseKey(core);
        CoreSettingsUi.Logs(core);

        foreach (var capability in core.All)
        {
            if (!capability.HasSettings && capability is not AutoConfirm) continue;

            if (!ImGui.CollapsingHeader($"{capability.Name}設定##{capability.Id}")) continue;

            ImGui.Indent();
            if (capability is AutoConfirm confirm) DrawRules(confirm);
            else capability.DrawSettings();
            ImGui.Unindent();
        }
    }

    private static void DrawRules(AutoConfirm confirm)
    {
        var rules = confirm.Rules.ToList();
        if (rules.Count == 0)
            ImGui.TextDisabled("目前沒有功能登記確認規則。");

        foreach (var (owner, rule) in rules)
            ImGui.TextUnformatted($"{rule} ← {owner}");

        if (confirm.LastConfirmed != null)
            ImGui.TextDisabled($"最近一次：{confirm.LastConfirmed}");
    }
}
