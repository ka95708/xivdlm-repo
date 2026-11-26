using System.Collections.Generic;
using AetherKit.Game;
using AetherKit.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AetherKit.Modules;

/// <summary>
/// 隱藏其他人：不畫其他玩家、召喚物、陸行鳥、小寵物（照 Visibility 插件的做法）。
/// 每種可以各自設：要不要藏、隊友／好友／同公會的照樣顯示。不分地圖，啟用就藏。
/// </summary>
public sealed class HideOthersModule : IModule
{
    private readonly Plugin plugin;

    public HideOthersModule(Plugin plugin)
    {
        this.plugin = plugin;
        Hider = new HideOthers(plugin);
    }

    public HideOthers Hider { get; }

    private HideOthersConfig Config => plugin.Config.HideOthers;

    public string Id => "hide-others";
    public string Name => "隱藏其他人";
    public string Description => "不畫其他玩家、召喚物、陸行鳥、小寵物，人多時畫面清爽、比較不卡。啟用就開始藏，不分地圖；隊友、好友、同公會的可以照樣顯示。";

    /// <summary>啟用就是開始隱藏；新裝時預設關著，免得一更新畫面上的人就不見。</summary>
    public bool OffByDefault => true;

    public void Enable(WindowSystem windows) => Hider.Start();

    public void Disable(WindowSystem windows) => Hider.Stop();

    public void DrawHeader()
    {
        ModuleUi.Command("/aetherkit hide [on|off]");
        ModuleUi.Status($"隱藏中：玩家 {Hider.Count(HideOthers.Kind.Player)}、召喚物 {Hider.Count(HideOthers.Kind.Pet)}、" +
                        $"陸行鳥 {Hider.Count(HideOthers.Kind.Chocobo)}、小寵物 {Hider.Count(HideOthers.Kind.Minion)}");
    }

    public IEnumerable<ModuleTab> SettingsTabs
    {
        get { yield return new ModuleTab("設定", DrawSettings); }
    }

    private void DrawSettings()
    {
        ModuleUi.Section("要藏的");
        ModuleUi.Note("召喚物、陸行鳥、小寵物看主人是誰：主人是隊友、好友、同公會的人就照樣顯示。自己的永遠不藏。");

        if (ImGui.BeginTable("##hide-others", 5, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit))
        {
            ImGui.TableSetupColumn("種類");
            ImGui.TableSetupColumn("隱藏");
            ImGui.TableSetupColumn("顯示隊友");
            ImGui.TableSetupColumn("顯示好友");
            ImGui.TableSetupColumn("顯示公會");
            ImGui.TableHeadersRow();

            Row("玩家", Config.Players);
            Row("召喚物", Config.Pets, "召喚師、學者的召喚獸，機工士的機器人等職業叫出來的");
            Row("陸行鳥", Config.Chocobos, "其他人的陸行鳥夥伴");
            Row("小寵物", Config.Minions, "跟在身邊、不會戰鬥的那種");

            ImGui.EndTable();
        }

        ModuleUi.Note("藏起來的人還是在那裡，只是不畫出來。停用模組就全部顯示回來。");
    }

    private void Row(string label, HideRule rule, string? hint = null)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(label);
        if (hint != null && ImGui.IsItemHovered()) ImGui.SetTooltip(hint);

        Cell($"##{label}-hide", rule.Hide, v => rule.Hide = v);

        ImGui.BeginDisabled(!rule.Hide);
        Cell($"##{label}-party", rule.ShowParty, v => rule.ShowParty = v);
        Cell($"##{label}-friend", rule.ShowFriends, v => rule.ShowFriends = v);
        Cell($"##{label}-company", rule.ShowCompany, v => rule.ShowCompany = v);
        ImGui.EndDisabled();
    }

    private void Cell(string id, bool value, System.Action<bool> set)
    {
        ImGui.TableNextColumn();
        if (!ImGui.Checkbox(id, ref value)) return;

        set(value);
        plugin.Save();
    }

    public bool HasSettings(Configuration config) => true;

    public void CopySettings(Configuration from, Configuration to)
    {
        var f = from.HideOthers;
        to.HideOthers = new HideOthersConfig
        {
            Players = Copy(f.Players),
            Pets = Copy(f.Pets),
            Chocobos = Copy(f.Chocobos),
            Minions = Copy(f.Minions),
        };
    }

    private static HideRule Copy(HideRule r) => new()
    {
        Hide = r.Hide,
        ShowParty = r.ShowParty,
        ShowFriends = r.ShowFriends,
        ShowCompany = r.ShowCompany,
    };

    public void Dispose() => Hider.Dispose();
}
