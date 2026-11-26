using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 「在某個遊戲視窗上按一下，把清單裡的東西一個個處理完」這類小功能的共用部分：
/// 按鈕列上的開始／停止按鈕、操作鎖、每一步之間的等待、算處理了幾個、卡住就停。
///
/// 子類別只要寫 Step：看一眼視窗，做一步（或什麼都不做、等一下），處理完回傳結束訊息。
/// 執行中視窗被關掉就停。
/// </summary>
public abstract unsafe class LoopTweak : Tweak
{
    /// <summary>連續幾次送出後數量都沒變就算卡住。</summary>
    private const int MaxNoProgress = 3;

    private DateTime nextStep;
    private int lastCount = -1;
    private int noProgress;

    /// <summary>放按鈕、執行期間必須開著的遊戲視窗。</summary>
    protected abstract string Addon { get; }

    /// <summary>開始按鈕上的字，例如「全部分解」。</summary>
    protected abstract string StartLabel { get; }

    /// <summary>數量單位（「件」「種」）。</summary>
    protected virtual string Unit => "件";

    public override TweakKind Kind => TweakKind.Button;

    protected bool Running { get; private set; }

    protected int Done { get; private set; }

    /// <summary>做一步。回傳 null = 還沒做完；回傳字串 = 做完了（或該停了），字串會印在聊天欄。</summary>
    protected abstract string? Step(AtkUnitBase* addon, DateTime now);

    /// <summary>按開始時檢查能不能開始；不能就回傳原因。</summary>
    protected virtual string? CanStart(AtkUnitBase* addon) => null;

    /// <summary>按鈕列的「選項」裡要放的設定；沒有就 null。</summary>
    protected virtual Action? QuickOptions => null;

    protected override void OnDisable() => Finish(null);

    /// <summary>下一步至少等這麼久。</summary>
    protected void Wait(TimeSpan delay) => nextStep = DateTime.Now + delay;

    /// <summary>開始時就有的「事件中」旗標。站在收藏櫃、投影台、交換 NPC 前開視窗，本來就算事件中。</summary>
    private HashSet<int> eventBaseline = new();

    /// <summary>
    /// 遊戲正在處理事件（例如按了確認、分解動作中），要等一下。只看開始之後多出來的事件狀態，
    /// 不然在收藏櫃、投影台前開的視窗會一直以為在事件中、永遠等下去。
    /// </summary>
    protected bool EventBusy => GameState.InEventBeyond(eventBaseline);

    /// <summary>
    /// 每次要送出動作前，把清單現在的數量給這裡：比上次少就算處理了幾個；
    /// 連續幾次都沒變回傳 false（多半是背包滿了或遊戲不讓做）。
    /// </summary>
    protected bool Progress(int count)
    {
        if (lastCount >= 0)
        {
            if (count < lastCount)
            {
                Done += lastCount - count;
                noProgress = 0;
            }
            else if (++noProgress >= MaxNoProgress)
            {
                Log.Write($"連續 {MaxNoProgress} 次數量沒變（{count}），算卡住");
                return false;
            }
        }

        lastCount = count;
        return true;
    }

    /// <summary>不用清單數量判斷進度的子類別（例如同一疊分解好幾次，清單件數不會變）自己算完成數。</summary>
    protected void CountDone(int count = 1) => Done += count;

    /// <summary>背包（四頁）還有幾格空的。</summary>
    protected static uint FreeSlots() => InventoryManager.Instance()->GetEmptySlotsInBag();

    public override void Update(DateTime now)
    {
        if (!Running || now < nextStep) return;

        var addon = Addons.Get(Addon);
        if (addon == null)
        {
            Finish("視窗關掉了，停止");
            return;
        }

        if (Step(addon, now) is { } message)
            Finish(message);
    }

    public override void AddButtons(AddonBar bar)
    {
        if (Running)
            bar.Button(Addon, $"停止（已完成 {Done} {Unit}）", () => Finish("已停止"));
        else
            bar.Button(Addon, StartLabel, Start, tooltip: Description);

        if (QuickOptions is { } options)
            bar.Options(Addon, Name, options);
    }

    private void Start()
    {
        var addon = Addons.Get(Addon);
        if (addon == null) return;

        eventBaseline = GameState.ActiveEventFlags();

        if (CanStart(addon) is { } reason)
        {
            Log.Write($"不能開始：{reason}");
            Notify.Error(Name, $"{reason}");
            return;
        }

        if (!Core.Lock.TryAcquire(Owner, Name, () => Finish("已停止")))
        {
            Notify.Error(Name, $"{Core.Lock.Label} 正在執行，先停止它。");
            return;
        }

        Log.Write(eventBaseline.Count > 0 ? $"開始（開始時的事件旗標：{string.Join(",", eventBaseline)}）" : "開始");
        Running = true;
        Done = 0;
        lastCount = -1;
        noProgress = 0;
        nextStep = DateTime.MinValue;
    }

    protected void Finish(string? message)
    {
        if (!Running) return;

        Running = false;
        Core.Lock.Release(Owner);
        Log.Write($"結束：{message ?? "小功能被關閉"}（完成 {Done} {Unit}）");
        if (message != null) Notify.Info(Name, message);
    }
}
