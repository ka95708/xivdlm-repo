using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherKit.Core;
using AetherKit.Game;
using AetherKit.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AetherKit.Modules;

/// <summary>
/// 青魔教皇（BlueMageVaultFarm）：青魔道士單人解除限制刷聖教中樞伊修加爾德教皇廳（取代 snd-scripts/vault_BM.lua）。
/// 需要 vnavmesh 走位；排本、進本、退本都透過任務搜尋器。
/// </summary>
public sealed class BlueMageVaultFarmModule : IModule
{
    private readonly Plugin plugin;

    public BlueMageVaultFarmModule(Plugin plugin)
    {
        this.plugin = plugin;
        Farmer = new BlueMageVaultFarm(plugin);
    }

    public BlueMageVaultFarm Farmer { get; }

    private BlueMageVaultFarmConfig Config => plugin.Config.BlueMageVaultFarm;

    public string Id => BlueMageVaultFarm.Owner;
    public string Name => "青魔教皇";
    public ModuleGroup Group => ModuleGroup.Automation;
    public bool Running => Farmer.Running;

    public string Description =>
        "青魔道士單人（解除限制）循環刷聖教中樞伊修加爾德教皇廳：排本 → 走位放技能 → 自爆 → 退本。需要 vnavmesh。";

    public void Enable(WindowSystem windows) { }

    public void Disable(WindowSystem windows) => Farmer.Stop("模組已停用");

    /// <summary>頁首：控制列 → 狀態 → 需要的東西（版面規則見 ModuleUi）。</summary>
    public void DrawHeader()
    {
        if (ModuleUi.Action(Farmer.Running ? "停止" : "開始"))
            Farmer.Toggle();
        ModuleUi.Command("/aetherkit vault");

        ModuleUi.Status(Farmer.Status);
        ModuleUi.FinishPending(plugin.Core, "青魔教皇");

        if (Farmer.Running)
        {
            ModuleUi.Detail("進度", $"第 {Math.Min(Farmer.Completed + 1, Config.Loops)} / {Config.Loops} 輪");
            ModuleUi.Detail("已執行", $"{DateTime.Now - Farmer.StartedAt:hh\\:mm\\:ss}");
            if (Farmer.LastRound is { } last) ModuleUi.Detail("上一輪", $"{last:mm\\:ss}");
        }

        ModuleUi.Borrows("自動確認（副本配對進入）、過場跳過、修理");
        ModuleUi.Dependencies(new[]
        {
            new ModuleUi.Dependency("vnavmesh", "走位移動", ModuleUi.DependencyLevel.Required,
                Services.PluginInterface.InstalledPlugins.Any(p => p.IsLoaded && p.InternalName == "vnavmesh"))
        });
    }

    public IEnumerable<ModuleTab> SettingsTabs
    {
        get
        {
            yield return new ModuleTab("設定", DrawSettings);
            yield return new ModuleTab("技能", DrawSpells);
        }
    }

    private void DrawSettings()
    {
        Paint.SettingsHeading("次數");

        var loops = Config.Loops;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("輪數", ref loops))
        {
            Config.Loops = Math.Clamp(loops, 1, 9999);
            plugin.Save();
        }

        var after = Config.AfterFinish;
        if (ModuleUi.FinishActionCombo("跑完之後", ref after))
        {
            Config.AfterFinish = after;
            plugin.Save();
        }

        ImGui.TextDisabled($"倒數 {FinishService.CountdownSeconds} 秒，可以在上面取消。手動停止、出錯不會登出或關閉。");

        Paint.SettingsHeading("開場");

        var gearset = Config.Gearset;
        ImGui.SetNextItemWidth(200);
        if (ImGui.InputText("青魔套裝名稱", ref gearset, 64))
        {
            Config.Gearset = gearset.Trim();
            plugin.Save();
        }

        ImGui.TextDisabled("開始時不是青魔道士就換這個套裝（名稱完全相同優先，否則取第一個包含它的）。留空 = 不換。");

        Paint.SettingsHeading("任務搜尋器");

        ImGui.TextDisabled("排本前自動設好：解除限制、等級同步開，最低裝等、無視增益效果、探索模式關。萬一設不上，會停在選本提示你到任務搜尋器的「設定」改好，改好就自動繼續。");

        var timeout = Config.QueueTimeoutSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("排隊最多等（秒）", ref timeout))
        {
            Config.QueueTimeoutSeconds = Math.Clamp(timeout, 30, 3600);
            plugin.Save();
        }

        Paint.SettingsHeading("修理");

        var repair = Config.AutoRepair;
        if (ImGui.Checkbox("每輪排本前檢查耐久，低於門檻就修理（需要暗物質）", ref repair))
        {
            Config.AutoRepair = repair;
            plugin.Save();
        }

        ImGui.TextDisabled("門檻在核心能力頁的「修理」設定。");
    }

    /// <summary>必備技能清單；是青魔道士時標出目前技能組裡有沒有。</summary>
    private void DrawSpells()
    {
        ImGui.TextDisabled("開始前會檢查目前的青魔技能組，缺任何一招就不會開始。");

        var isBlue = Services.Objects.LocalPlayer?.ClassJob.RowId == 36;
        var missing = isBlue ? new HashSet<string>(Farmer.MissingSpells()) : null;
        if (!isBlue) ImGui.TextDisabled("（目前不是青魔道士，換成青魔道士後會顯示技能組裡有沒有）");

        var green = new Vector4(0.5f, 0.9f, 0.5f, 1f);
        var red = new Vector4(1f, 0.4f, 0.4f, 1f);

        foreach (var number in BlueMageVaultFarm.Spell.Required)
        {
            var (_, name) = BlueMageVaultFarm.SpellAction(number);
            var label = $"No.{number,-3} {name}";

            if (missing == null) ImGui.TextUnformatted(label);
            else if (missing.Contains(name)) ImGui.TextColored(red, $"{label}（技能組裡沒有）");
            else ImGui.TextColored(green, label);
        }
    }

    public bool HasSettings(Configuration config) => true;

    public void CopySettings(Configuration from, Configuration to)
    {
        var f = from.BlueMageVaultFarm;
        to.BlueMageVaultFarm = new BlueMageVaultFarmConfig
        {
            Loops = f.Loops,
            AfterFinish = f.AfterFinish,
            Gearset = f.Gearset,
            AutoRepair = f.AutoRepair,
            QueueTimeoutSeconds = f.QueueTimeoutSeconds,
        };
    }

    public void Dispose() => Farmer.Dispose();
}
