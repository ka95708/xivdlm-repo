using System.Numerics;
using AetherKit.Game;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AetherKit.UI;

/// <summary>插件訊息紀錄：被收起來的插件訊息都在這裡看，錯誤是紅色的。</summary>
public class PluginLogWindow : Window
{
    private static readonly Vector4 ErrorColor = new(1f, 0.45f, 0.45f, 1f);
    private const string AllSources = "（全部插件）";

    private readonly PluginLog log;
    private string source = AllSources;
    private bool errorsOnly;
    private int seenVersion = -1;

    public PluginLogWindow(PluginLog log) : base("插件訊息##AetherKitPluginLog")
    {
        this.log = log;
        Size = new Vector2(640, 360);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void OnOpen() => log.UnreadErrors = 0;

    public override void Draw()
    {
        log.UnreadErrors = 0;

        DrawFilters();
        ImGui.Separator();
        DrawEntries();
    }

    private void DrawFilters()
    {
        ImGui.SetNextItemWidth(180);
        if (ImGui.BeginCombo("##source", source))
        {
            if (ImGui.Selectable(AllSources, source == AllSources))
                source = AllSources;

            foreach (var name in log.Sources)
            {
                if (ImGui.Selectable(name, source == name))
                    source = name;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.Checkbox("只看錯誤", ref errorsOnly);

        ImGui.SameLine();
        if (ImGui.Button("清除"))
            log.Clear();

        ImGui.SameLine();
        ImGui.TextDisabled($"共 {log.Entries.Count} 則（最多留 500 則，關遊戲就清空）");
    }

    private void DrawEntries()
    {
        // BeginChild 不管回傳什麼都要配一個 EndChild
        if (!ImGui.BeginChild("##entries"))
        {
            ImGui.EndChild();
            return;
        }

        // 原本就在最下面的話，有新訊息時跟著捲下去；往上翻著看時不打擾
        var follow = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4;

        if (ImGui.BeginTable("##log", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("時間", ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn("插件", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("訊息", ImGuiTableColumnFlags.WidthStretch);

            for (var i = 0; i < log.Entries.Count; i++)
            {
                var entry = log.Entries[i];
                if (errorsOnly && !entry.IsError) continue;
                if (source != AllSources && entry.Source != source) continue;

                ImGui.TableNextRow();
                if (entry.IsError) ImGui.PushStyleColor(ImGuiCol.Text, ErrorColor);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.Time.ToString("HH:mm:ss"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(entry.Source);
                ImGui.TableNextColumn();
                ImGui.PushTextWrapPos();
                ImGui.TextUnformatted(entry.Text);
                ImGui.PopTextWrapPos();

                if (entry.IsError) ImGui.PopStyleColor();

                ImGui.PushID(i);
                if (ImGui.BeginPopupContextItem("##copy"))
                {
                    if (ImGui.Selectable("複製這則訊息"))
                        ImGui.SetClipboardText($"[{entry.Time:HH:mm:ss}] [{entry.Source}] {entry.Text}");

                    ImGui.EndPopup();
                }

                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        if (follow && seenVersion != log.Version)
            ImGui.SetScrollHereY(1f);

        seenVersion = log.Version;
        ImGui.EndChild();
    }
}
