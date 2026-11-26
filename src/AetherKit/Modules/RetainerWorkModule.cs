using System;
using System.Collections.Generic;
using AetherKit.Core;
using AetherKit.Game;
using AetherKit.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AetherKit.Modules;

/// <summary>
/// 自動雇員：
/// - 雇員清單上方：「收探險並重新出發」「提領金幣」「存入重複物品」（全部雇員一個個進去做）。
/// - 單一雇員的出售清單：蓋在上面的價格表（全部改價、全部收回、每一件的改／收），上方只有開關價格表的按鈕。
/// - 價格視窗（上架、自己點修改價格）一打開就查市場、把參考價填進價格欄並按確定。
/// </summary>
public sealed class RetainerWorkModule : IModule
{
    private readonly Plugin plugin;
    private readonly RetainerPriceTable priceTable;

    public RetainerWorkModule(Plugin plugin)
    {
        this.plugin = plugin;
        Work = new RetainerWork(plugin);
        priceTable = new RetainerPriceTable(plugin, Work);
    }

    public RetainerWork Work { get; }

    private RetainerWorkConfig Config => plugin.Config.RetainerWork;

    public string Id => RetainerWork.Owner;
    public string Name => "自動雇員";

    // 放在「功能」：按鈕掛在遊戲的雇員視窗上、賣東西時自動填價格，開著就自己運作（不是按開始才跑的長流程），
    // 所以要有啟用開關；停用時不自動填價格、不蓋價格表。
    public bool Running => Work.Running;

    public string Description =>
        "雇員清單上一次收完探險並重新出發、提領全部金幣、把背包裡雇員也有的東西存回去；單一雇員的出售清單換成價格表，可以改價、收回；上架時自動填好價格。";

    public void Enable(WindowSystem windows) => Work.Active = true;

    public void Disable(WindowSystem windows)
    {
        Work.Stop("模組已停用");
        Work.Active = false;
        Work.RestoreNativeSellList();
    }

    /// <summary>雇員清單、出售清單上的按鈕（共用的按鈕列），以及出售清單上的價格表。</summary>
    public void AfterWindows()
    {
        var bar = plugin.AddonBar;

        // 價格表執行中也顯示，看得到進度
        priceTable.Draw();

        if (Work.Running)
        {
            if (Work.RetainerListOpen) bar.Button("RetainerList", $"停止（{Work.Job}）", () => Work.Stop("手動停止"));
            if (Work.SellListOpen) bar.Button("RetainerSellList", $"停止（{Work.Job}）", () => Work.Stop("手動停止"));
            return;
        }

        if (Work.RetainerListOpen)
        {
            bar.Button("RetainerList", "收探險並重新出發", Work.CollectVentures, tooltip: "探險已完成的雇員一個個進去收，並重新派遣同一個探險");
            bar.Button("RetainerList", "提領金幣", Work.WithdrawGil, tooltip: "有金幣的雇員一個個進去，全部取出");
            bar.Button("RetainerList", "存入重複物品", Work.DepositDuplicates, tooltip: "背包裡的東西，哪個雇員已經有同一種就交給那個雇員（照持有狀況；進去後確認雇員真的有才存，套裝裝備不存）");
        }

        if (Work.SellListOpen)
        {
            bar.Button("RetainerSellList", Config.ShowPriceTable ? "收起價格表" : "價格表", () =>
            {
                Config.ShowPriceTable = !Config.ShowPriceTable;
                plugin.Save();
            });
            bar.Button("RetainerSellList", "關閉", Work.CloseSellList, tooltip: "關閉這個雇員的出售清單");
        }
    }

    /// <summary>頁首：（執行中才有的停止）→ 狀態 → 用法 → 需要的東西（版面規則見 ModuleUi）。</summary>
    public void DrawHeader()
    {
        if (Work.Running && ModuleUi.Action("停止"))
            Work.Stop("手動停止");

        ModuleUi.Status(Work.Status);
        ModuleUi.Detail("用法", "在傳喚鈴打開雇員清單，按清單上方的按鈕；改價、收回在單一雇員的出售清單上的價格表");
        ModuleUi.Borrows("對話推進");
    }

    public IEnumerable<ModuleTab> SettingsTabs
    {
        get { yield return new ModuleTab("設定", DrawSettings); }
    }

    private void DrawSettings()
    {
        ModuleUi.Section("改價");

        var undercut = Config.Undercut;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("比別人低多少（金幣）", ref undercut))
        {
            Config.Undercut = Math.Clamp(undercut, 0, 1_000_000);
            plugin.Save();
        }

        ModuleUi.Note("參考價 = 別人（同品質、不含自己雇員）的最低價，濾掉明顯過低的離群值，再減這個數字；0 = 一樣價錢。");
        ModuleUi.Note("價格表的「全部改價」「改」、上架時自動填的價格都用參考價。");
    }

    public bool HasSettings(Configuration config) => true;

    public void CopySettings(Configuration from, Configuration to)
    {
        var f = from.RetainerWork;
        to.RetainerWork = new RetainerWorkConfig
        {
            Undercut = f.Undercut,
            ShowPriceTable = f.ShowPriceTable,
        };
    }

    public void Dispose() => Work.Dispose();
}
