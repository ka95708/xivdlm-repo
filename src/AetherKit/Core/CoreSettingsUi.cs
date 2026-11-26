using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using AetherKit.Game;

namespace AetherKit.Core;

/// <summary>核心能力頁裡各能力的設定小區塊。放在一起，能力本身只管行為。</summary>
public static class CoreSettingsUi
{
    private static readonly (VirtualKey Key, string Label)[] PauseKeys =
    {
        (VirtualKey.NO_KEY, "不使用"),
        (VirtualKey.SHIFT, "Shift"),
        (VirtualKey.CONTROL, "Ctrl"),
        (VirtualKey.MENU, "Alt")
    };

    public static void PauseKey(CoreServices core)
    {
        var current = Array.Find(PauseKeys, k => k.Key == core.Config.PauseKey).Label ?? "不使用";

        ImGui.SetNextItemWidth(160);
        if (ImGui.BeginCombo("暫停鍵（按住時所有核心能力暫停）", current))
        {
            foreach (var (key, label) in PauseKeys)
            {
                if (!ImGui.Selectable(label, key == core.Config.PauseKey)) continue;

                core.Config.PauseKey = key;
                Plugin.Instance.Save();
            }

            ImGui.EndCombo();
        }
    }

    /// <summary>所有功能共用的 log 開關與資料夾。</summary>
    public static void Logs(CoreServices core)
    {
        var write = core.Config.WriteLogs;
        if (ImGui.Checkbox("把各功能的執行過程寫進 log 檔", ref write))
        {
            core.Config.WriteLogs = write;
            FeatureLog.Enabled = write;
            Plugin.Instance.Save();
        }

        ImGui.SameLine();
        if (ImGui.SmallButton("開啟 log 資料夾")) FeatureLog.OpenFolder();

        ImGui.TextDisabled("每個功能各自一個檔（檔名開頭是功能的英文名，例如 autofate-日期.log），每天一個、留 7 天。出問題時把那個功能當天的檔給我。");
    }

    public static void RepairThreshold(CoreServices core)
    {
        var percent = core.Config.RepairBelowPercent;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("耐久低於這個就修", ref percent, 1, 99, "%d%%"))
        {
            core.Config.RepairBelowPercent = percent;
            Plugin.Instance.Save();
        }

        ImGui.TextDisabled($"目前最低耐久：{RepairService.LowestDurability()}%　手動修理：/ak repair");
    }

    /// <summary>過場跳過：不跳的地圖清單（想看劇情的地方）。</summary>
    public static void CutsceneZones(CoreServices core)
    {
        var zones = core.Config.CutsceneKeepZones;
        var here = Services.ClientState.TerritoryType;

        ImGui.TextUnformatted("這些地圖不跳過過場：");

        if (zones.Count == 0)
            ImGui.TextDisabled("（沒有，全部都跳）");

        for (var i = 0; i < zones.Count; i++)
        {
            ImGui.PushID(i);
            if (ImGui.SmallButton("移除"))
            {
                zones.RemoveAt(i);
                Plugin.Instance.Save();
                ImGui.PopID();
                break;
            }

            ImGui.SameLine();
            ImGui.TextUnformatted(Aetherytes.ZoneName(zones[i]));
            ImGui.PopID();
        }

        if (here != 0 && !zones.Contains(here) && ImGui.Button($"加入目前地圖（{Aetherytes.ZoneName(here)}）"))
        {
            zones.Add(here);
            Plugin.Instance.Save();
        }
    }
}
