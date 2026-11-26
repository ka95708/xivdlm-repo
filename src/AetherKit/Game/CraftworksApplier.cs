using System;
using System.Collections.Generic;
using System.Linq;
using AetherKit.Core;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace AetherKit.Game;

/// <summary>
/// 把排好的班寫進遊戲的工房排班（參考 visland 的做法）。排班視窗（MJICraftSchedule）要開著。
/// 每一天：切到那天（SetDisplayedCycle）→ 那天原本有排就先清掉（排班代理事件 6）→ 每間工坊照順序呼叫 ScheduleCraft。
/// 休息日：改排班代理的 NewRestCycles 再送事件 5（同 visland），只改還沒到的日子（本週 0～6、下週 7～13 位元），已經過去的維持原樣。
/// 已經開始或過去的日子不動。
/// </summary>
public sealed unsafe class CraftworksApplier
{
    /// <summary>一天的排班：第幾天（本週 0～6、下週 7～13）、休息嗎、每間工坊依序做的產品（MJICraftworksObject 列號）。</summary>
    public sealed record DayPlan(int Cycle, bool Rest, IReadOnlyList<IReadOnlyList<uint>> Workshops);

    // 排班代理的事件編號（同 visland）
    private const ulong ClearDisplayedCycleEvent = 6;
    private const ulong ApplyRestCyclesEvent = 5;

    private readonly Routine routine = new();
    private readonly FeatureLog log;

    public CraftworksApplier(FeatureLog log) => this.log = log;

    public bool Running => routine.Running;

    public string Status { get; private set; } = string.Empty;

    public void Start(IReadOnlyList<DayPlan> plan)
    {
        if (Running) return;
        routine.Start(Apply(plan));
    }

    public void Stop() => routine.Clear();

    /// <summary>每幀推進（遊戲執行緒）。</summary>
    public void Update(DateTime now)
    {
        if (!routine.Running) return;

        try
        {
            routine.Advance(now);
        }
        catch (Exception e)
        {
            routine.Clear();
            Status = $"套用失敗：{e.Message}";
            log.Write($"套用 錯誤 {e}");
        }
    }

    private IEnumerable<object?> Apply(IReadOnlyList<DayPlan> plan)
    {
        if (!AgentReady())
        {
            Status = "排班表視窗沒開，沒有套用";
            yield break;
        }

        var today = CurrentDay();
        var days = plan.Where(d => d.Cycle > today && d.Cycle < Craftworks.DaysPerWeek * 2).OrderBy(d => d.Cycle).ToList();
        if (days.Count == 0)
        {
            Status = "沒有可以套用的日子（今天以前的不能改）";
            yield break;
        }

        var original = DisplayedCycle();
        log.Write($"套用 開始：{string.Join("、", days.Select(d => Craftworks.DayLabel(d.Cycle)))}");

        // 1. 要改成休息的日子先清空
        foreach (var day in days.Where(d => d.Rest))
        {
            Status = $"清空{Craftworks.DayLabel(day.Cycle)}";
            yield return ShowCycle(day.Cycle);
            if (HasEntries()) yield return ClearCycle();
        }

        // 2. 休息日
        var mask = RestMask(days, today);
        if (mask != CurrentRestCycles())
        {
            Status = "設定休息日";
            SetRestCycles(mask);
            log.Write($"套用 休息日 0x{mask:X}");
            yield return Routine.Delay(0.5);
        }

        // 3. 上班的日子：清掉原本的再排
        foreach (var day in days.Where(d => !d.Rest))
        {
            Status = $"排{Craftworks.DayLabel(day.Cycle)}";
            yield return ShowCycle(day.Cycle);
            if (HasEntries()) yield return ClearCycle();

            for (var w = 0; w < day.Workshops.Count; w++)
            {
                var hour = 0;
                foreach (var craft in day.Workshops[w])
                {
                    Schedule(craft, hour, day.Cycle, w);
                    hour += Craftworks.Object(craft)?.Time ?? 0;
                    yield return null;
                }
            }

            log.Write($"套用 {Craftworks.DayLabel(day.Cycle)}：" + string.Join(" ／ ",
                day.Workshops.Select((q, i) => $"{i + 1}號 " + string.Join("→", q.Select(Craftworks.ShortName)))));
            yield return Routine.Delay(0.3);
        }

        // 4. 切回原本看的那天，讓畫面重新整理
        yield return ShowCycle(original);
        RefreshUi();
        Status = $"已套用{string.Join("、", days.Select(d => Craftworks.DayLabel(d.Cycle)))}";
        log.Write("套用 完成");
    }

    private IEnumerable<object?> ShowCycle(int cycle)
    {
        SetDisplayedCycle(cycle);
        yield return Routine.Until(() => DisplayedCycle() == cycle, 2);
        yield return Routine.Delay(0.2);
    }

    private IEnumerable<object?> ClearCycle()
    {
        Addons.SendAgentEvent(AgentId.MJICraftSchedule, ClearDisplayedCycleEvent, 0); // [遊戲函式] 清空正在看的那天
        yield return Routine.Until(() => !HasEntries(), 2);
        yield return Routine.Delay(0.2);
    }

    /// <summary>休息日遮罩：今天以前（含今天）照原本的，排到的日子照排班，其他位元保留。</summary>
    private static uint RestMask(IReadOnlyList<DayPlan> days, int today)
    {
        var mask = CurrentRestCycles();
        foreach (var day in days)
        {
            var bit = 1u << day.Cycle;
            mask = day.Rest ? mask | bit : mask & ~bit;
        }

        return mask;
    }

    // ── 遊戲（指標都在這些一般方法裡，迭代器不能用指標） ─────────────

    private static AgentMJICraftSchedule* Agent => AgentMJICraftSchedule.Instance();

    private static bool AgentReady() =>
        Agent != null && Agent->IsAgentActive() && Agent->Data != null && Addons.IsOpen(Craftworks.ScheduleAddon);

    private static int CurrentDay() => MJIManager.Instance() is var m && m != null ? m->CurrentCycleDay : 0;

    private static int DisplayedCycle() => AgentReady() ? Agent->Data->CycleDisplayed : -1;

    private static uint CurrentRestCycles() => AgentReady() ? Agent->Data->RestCycles : 0;

    private static bool HasEntries()
    {
        if (!AgentReady()) return false;

        foreach (var workshop in Agent->Data->WorkshopSchedules)
            if (workshop.NumScheduleEntries > 0) return true;
        return false;
    }

    private static void SetDisplayedCycle(int cycle)
    {
        if (AgentReady()) Agent->SetDisplayedCycle(cycle); // [遊戲函式] 排班表切到第幾天
    }

    private static void SetRestCycles(uint mask)
    {
        if (!AgentReady()) return;

        Agent->Data->NewRestCycles = mask; // [改記憶體] 新的休息日（同 visland）
        Addons.SendAgentEvent(AgentId.MJICraftSchedule, ApplyRestCyclesEvent, 0); // [遊戲函式] 確認休息日
    }

    /// <summary>排一個產品：hour = 從那天開始算第幾小時（遊戲的時段從 17 點起算，同 visland 換算）。</summary>
    private static void Schedule(uint craft, int hour, int cycle, int workshop)
    {
        var manager = MJIManager.Instance();
        if (manager == null) return;

        manager->ScheduleCraft((ushort)craft, (byte)((hour + 17) % 24), (byte)cycle, (byte)workshop); // [遊戲函式] 排班
    }

    private static void RefreshUi()
    {
        if (!AgentReady()) return;

        Agent->SetDisplayedCycle(Agent->Data->CycleDisplayed); // [遊戲函式]
        *(byte*)&Agent->Data->Flags1 |= 4; // [改記憶體] 讓排班表重新整理（同 visland）
    }
}
