using System;
using AetherKit.Game;
using Dalamud.Bindings.ImGui;

namespace AetherKit.UI;

/// <summary>
/// 自動登入的設定：選角色、等待秒數，以及目前跑到哪一步。
/// 角色清單來自角色選擇畫面 — 插件看過的角色才會出現在這裡。
/// </summary>
public class AutoLoginSettings
{
    private readonly Plugin plugin;
    private readonly AutoLogin login;

    public AutoLoginSettings(Plugin plugin, AutoLogin login)
    {
        this.plugin = plugin;
        this.login = login;
    }

    private AutoLoginConfig Config => plugin.Config.AutoLogin;

    public void Draw()
    {
        DrawCharacter();

        Paint.SettingsHeading("時機");

        var delay = Config.DelaySeconds;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("每個畫面出現後等幾秒再按", ref delay, 0f, 5f, "%.1f"))
        {
            Config.DelaySeconds = delay;
            plugin.Save();
        }

        Paint.SettingsHeading("狀態");
        ImGui.TextUnformatted(login.Status);

        ImGui.Spacing();
        ImGui.TextDisabled("・開遊戲時按住 Shift，這次就不自動登入。");
        ImGui.TextDisabled("・每次開遊戲只登入一次：登出回到標題畫面不會再自動登入。");
        ImGui.TextDisabled("・出現錯誤訊息、等太久或角色不能登入時會停下來，並在聊天欄說明。");
    }

    private void DrawCharacter()
    {
        Paint.SettingsHeading("登入的角色");

        if (Config.Characters.Count == 0)
        {
            ImGui.TextDisabled("還沒有看過任何角色。到一次角色選擇畫面，那裡的角色就會出現在這裡；");
            ImGui.TextDisabled("有多個伺服器的話，每個伺服器分頁都點過一次。");
            return;
        }

        var selected = Config.Characters.Find(c => c.ContentId == Config.CharacterId);
        var preview = selected != null ? Describe(selected) : "（不自動登入）";

        ImGui.SetNextItemWidth(260);

        if (ImGui.BeginCombo("角色", preview))
        {
            if (ImGui.Selectable("（不自動登入）", Config.CharacterId == 0))
            {
                Config.CharacterId = 0;
                plugin.Save();
            }

            foreach (var character in Config.Characters)
            {
                ImGui.PushID(character.ContentId.ToString());

                if (ImGui.Selectable(Describe(character), character.ContentId == Config.CharacterId))
                {
                    Config.CharacterId = character.ContentId;
                    plugin.Save();
                }

                ImGui.PopID();
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();

        // 角色刪了、改名了，清單裡的舊資料可以清掉，下次到角色選擇畫面會重新記
        if (ImGui.SmallButton("清除清單"))
        {
            Config.Characters.RemoveAll(c => c.ContentId != Config.CharacterId);
            plugin.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("只保留目前選的角色，其他的清掉。下次到角色選擇畫面會重新記下來。");
    }

    private static string Describe(KnownCharacter character) =>
        string.IsNullOrEmpty(character.HomeWorld)
            ? character.Name
            : $"{character.Name} @ {character.HomeWorld}";
}
