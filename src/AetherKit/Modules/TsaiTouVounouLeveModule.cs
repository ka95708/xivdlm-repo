using System;
using System.Collections.Generic;
using AetherKit.Core;
using AetherKit.Game;
using AetherKit.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AetherKit.Modules;

/// <summary>
/// 高山茶理符（TsaiTouVounouLeve）：原地重複領、交「製作委託：治癒身心的茶」（取代 snd-scripts/leve_TsaiTouVounou.lua）。
/// 高山茶要先做好放在身上；人站在發行人和委託人都能對話的位置，不會走路。
/// </summary>
public sealed class TsaiTouVounouLeveModule : IModule
{
    private readonly Plugin plugin;

    public TsaiTouVounouLeveModule(Plugin plugin)
    {
        this.plugin = plugin;
        Runner = new TsaiTouVounouLeve(plugin);
    }

    public TsaiTouVounouLeve Runner { get; }

    private TsaiTouVounouLeveConfig Config => plugin.Config.TsaiTouVounouLeve;

    public string Id => TsaiTouVounouLeve.Owner;
    public string Name => "高山茶理符";
    public ModuleGroup Group => ModuleGroup.Automation;
    public bool Running => Runner.Running;

    public string Description =>
        "舊薩雷安的「製作委託：治癒身心的茶」原地重複領、交，直到受理限額或高山茶用完。高山茶要先做好放在身上。";

    public void Enable(WindowSystem windows) { }

    public void Disable(WindowSystem windows) => Runner.Stop("模組已停用");

    /// <summary>頁首：控制列 → 狀態 → 用法 → 需要的東西（版面規則見 ModuleUi）。</summary>
    public void DrawHeader()
    {
        if (ModuleUi.Action(Runner.Running ? "停止" : "開始"))
            Runner.Toggle();
        ModuleUi.Command("/aetherkit leve");

        ModuleUi.Status(Runner.Status);
        ModuleUi.FinishPending(plugin.Core, "高山茶理符");

        var leve = LeveData.Get(TsaiTouVounouLeve.LeveId);
        if (leve != null && Services.ClientState.IsLoggedIn)
        {
            ModuleUi.Detail("身上", $"{leve.ItemName} {TsaiTouVounouLeve.ItemCount(leve.ItemId)} 個（一次交 {leve.ItemCount} 個）");
            ModuleUi.Detail("受理限額", $"{TsaiTouVounouLeve.Allowances}");
        }

        if (Runner.Running)
        {
            ModuleUi.Detail("進度", Config.Rounds > 0 ? $"交了 {Runner.Completed} / {Config.Rounds} 次" : $"交了 {Runner.Completed} 次");
            ModuleUi.Detail("已執行", $"{DateTime.Now - Runner.StartedAt:hh\\:mm\\:ss}");
        }

        if (leve != null)
            ModuleUi.Detail("用法", $"在{Aetherytes.ZoneName(leve.Territory)}站到 {leve.IssuerName} 和 {leve.ClientName} 都能對話的位置再按開始（不會走路）");

        ModuleUi.Borrows("對話推進、交道具、任務接受／完成、自動確認（交優質道具）");
    }

    public IEnumerable<ModuleTab> SettingsTabs
    {
        get { yield return new ModuleTab("設定", DrawSettings); }
    }

    private void DrawSettings()
    {
        ModuleUi.Section("次數");

        var rounds = Config.Rounds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("最多交幾次", ref rounds))
        {
            Config.Rounds = Math.Clamp(rounds, 0, 9999);
            plugin.Save();
        }

        ModuleUi.Note("0 = 不限，一直交到受理限額或高山茶用完。");

        var after = Config.AfterFinish;
        if (ModuleUi.FinishActionCombo("做完之後", ref after))
        {
            Config.AfterFinish = after;
            plugin.Save();
        }

        ModuleUi.Note($"交滿次數、限額用完、高山茶用完都算做完。倒數 {FinishService.CountdownSeconds} 秒，可以在上面取消；手動停止、出錯不會登出或關閉。");
    }

    public bool HasSettings(Configuration config) => true;

    public void CopySettings(Configuration from, Configuration to)
    {
        var f = from.TsaiTouVounouLeve;
        to.TsaiTouVounouLeve = new TsaiTouVounouLeveConfig
        {
            Rounds = f.Rounds,
            AfterFinish = f.AfterFinish,
        };
    }

    public void Dispose() => Runner.Dispose();
}
