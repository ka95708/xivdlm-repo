using System;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace AetherKit.Core;

/// <summary>
/// 修理（自修）：最低耐久低於門檻時，用一般技能「修理」開視窗 → 全部修理 → 確認 → 等修好 → 關視窗。
///
/// 借用它的功能（例如 AutoFate）在自己方便的時候呼叫 TryStart，再等 Busy 結束；
/// 模式設成「常開」時，閒下來（沒戰鬥、沒坐騎、沒別的流程在跑）也會自己修。
/// 修理視窗沒開起來（多半是沒有暗物質）或修完還是太低，就 10 分鐘內不再自動修，免得一直卡在這裡。
/// </summary>
public sealed unsafe class RepairService : Capability
{
    private const uint RepairAction = 6;
    private const string LockOwner = "core-repair";

    private static readonly TimeSpan GiveUpFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AutoCheckInterval = TimeSpan.FromSeconds(5);

    private enum Step
    {
        None,
        Opening,
        Confirming,
        Waiting
    }

    private Step step;
    private DateTime stepAt;
    private DateTime gaveUpUntil;
    private DateTime nextAutoCheck;
    private bool manual;

    public RepairService(CoreServices core) : base(core)
    {
    }

    public override string Id => "repair";
    public override string Name => "修理";
    public override string Description => "耐久低於門檻時自己修理（需要暗物質）。設成常開時，閒下來就會自己修。";

    public bool Busy => step != Step.None;

    /// <summary>目前在做哪一步，給借用的功能顯示狀態。</summary>
    public string Status => step switch
    {
        Step.Opening => "修理：開修理視窗",
        Step.Confirming => "修理：確認",
        Step.Waiting => "修理：等待完成",
        _ => "閒置"
    };

    /// <summary>修完（true）或失敗（false，附原因）。</summary>
    public event Action<bool, string?>? Finished;

    public bool NeedsRepair => LowestDurability() < Core.Config.RepairBelowPercent;

    /// <summary>
    /// 需要修就開始修。回傳 true = 正在修（剛開始或本來就在修），呼叫的功能這段時間先別做別的。
    /// </summary>
    public bool TryStart(DateTime now)
    {
        if (Busy) return true;
        if (!Active || now < gaveUpUntil || !NeedsRepair || !GameState.Idle) return false;

        Begin(now, false);
        return true;
    }

    /// <summary>/ak repair：不管門檻和模式，只要有裝備沒滿就修。</summary>
    public void RunNow()
    {
        if (Busy) return;

        if (LowestDurability() >= 100)
        {
            Notify.Reply("裝備耐久都是滿的，不用修理。");
            return;
        }

        if (!GameState.Idle)
        {
            Notify.Reply("現在不能修理（戰鬥、坐騎、詠唱或事件中）。", true);
            return;
        }

        Begin(DateTime.Now, true);
    }

    private void Begin(DateTime now, bool byCommand)
    {
        manual = byCommand;
        ActionManager.Instance()->UseAction(ActionType.GeneralAction, RepairAction);
        step = Step.Opening;
        stepAt = now;
    }

    protected override void Update(DateTime now)
    {
        if (!Busy && Mode == CapabilityMode.Always && now >= nextAutoCheck)
        {
            nextAutoCheck = now + AutoCheckInterval;

            // 平常自動修：別的流程在跑時不插隊，修理期間自己拿操作鎖
            if (!Core.Lock.Busy && TryStart(now))
                Core.Lock.TryAcquire(LockOwner, "修理", Abort);
        }
    }

    /// <summary>不管作用與否都要推進：修到一半模式被改掉也要把視窗關好。</summary>
    internal void Advance(DateTime now)
    {
        if (!Busy) return;

        var elapsed = now - stepAt;
        var addon = Addons.Get("Repair");

        switch (step)
        {
            case Step.Opening:
                if (addon != null)
                {
                    Addons.Fire(addon, 0); // 全部修理
                    step = Step.Confirming;
                    stepAt = now;
                }
                else if (elapsed > TimeSpan.FromSeconds(5))
                {
                    Finish(false, "修理視窗沒開起來，可能身上沒有暗物質");
                }

                return;

            case Step.Confirming:
                var yesno = Addons.Get("SelectYesno");
                if (yesno != null)
                {
                    Addons.ClickYes(yesno);
                    step = Step.Waiting;
                    stepAt = now;
                }
                else if (elapsed > TimeSpan.FromSeconds(3))
                {
                    // 有些情況不會跳確認框，直接等結果
                    step = Step.Waiting;
                    stepAt = now;
                }

                return;

            case Step.Waiting:
                if (LowestDurability() >= 99 || elapsed > TimeSpan.FromSeconds(10))
                {
                    if (addon != null) Addons.Fire(addon, -1);

                    var lowest = LowestDurability();
                    var ok = manual ? lowest >= 99 : lowest >= Core.Config.RepairBelowPercent;
                    Finish(ok, ok ? null : "修理後耐久還是太低，可能暗物質等級不夠");
                }

                return;
        }
    }

    private void Abort()
    {
        if (!Busy) return;

        var addon = Addons.Get("Repair");
        if (addon != null) Addons.Fire(addon, -1);
        Finish(false, "已停止");
    }

    private void Finish(bool ok, string? problem)
    {
        step = Step.None;
        Core.Lock.Release(LockOwner);

        if (!ok && problem != "已停止")
        {
            gaveUpUntil = DateTime.Now + GiveUpFor;
            Notify.Error("修理", $"{problem}，{GiveUpFor.TotalMinutes:0} 分鐘內不再自動修理。");
        }

        FeatureLog.For("core").Write($"修理 {(ok ? "完成" : $"失敗：{problem}")}");
        Finished?.Invoke(ok, problem);
    }

    /// <summary>身上裝備最低的耐久百分比。</summary>
    public static int LowestDurability()
    {
        var container = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        if (container == null || !container->IsLoaded) return 100;

        var lowest = 100;
        for (var i = 0; i < container->Size; i++)
        {
            var item = container->GetInventorySlot(i);
            if (item == null || item->ItemId == 0) continue;

            lowest = Math.Min(lowest, item->GetConditionPercentage());
        }

        return lowest;
    }

    public override bool HasSettings => true;

    public override void DrawSettings() => CoreSettingsUi.RepairThreshold(Core);
}
