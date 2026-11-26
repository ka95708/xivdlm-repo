using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AetherKit.Core;

/// <summary>
/// 一條自動確認規則：哪個視窗、符不符合、怎麼確認。
/// 由功能登記（例如 AutoFate 登記「副本配對自動進入」），登記期間自動確認這項能力就算被它借用。
/// </summary>
public sealed unsafe class ConfirmRule
{
    public ConfirmRule(string name, string addon, Func<nint, bool> matches, Func<nint, bool> confirm)
    {
        Name = name;
        Addon = addon;
        Matches = matches;
        Confirm = confirm;
    }

    /// <summary>給人看的名稱，核心能力頁會列出來。</summary>
    public string Name { get; }

    public string Addon { get; }

    /// <summary>參數是視窗位址（AtkUnitBase*）。</summary>
    public Func<nint, bool> Matches { get; }

    /// <summary>回傳 true = 已經按下去了。</summary>
    public Func<nint, bool> Confirm { get; }

    /// <summary>確認框的提示包含指定文字（遊戲文字表的一列）時按「是」。</summary>
    public static ConfirmRule YesnoWithText(string name, uint addonTextRow) => new(
        name, "SelectYesno",
        a => Addons.YesnoPrompt((AtkUnitBase*)a).Contains(Addons.Text(addonTextRow)),
        a => Addons.ClickYes((AtkUnitBase*)a));

    /// <summary>副本配對成功時按「進入」。</summary>
    public static ConfirmRule DutyCommence() => new(
        "副本配對自動進入", "ContentsFinderConfirm",
        _ => true,
        a => Addons.ClickButton((AtkUnitBase*)a, ((AddonContentsFinderConfirm*)a)->CommenceButton));
}

/// <summary>
/// 自動確認：照各功能登記的規則按掉確認視窗。
/// 規則跟著登記它的功能走，功能停用時規則一起收回；同一個視窗按過之後半秒內不再按，避免連點。
/// </summary>
public sealed unsafe class AutoConfirm : Capability
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromMilliseconds(500);

    private sealed record Registration(ConfirmRule Rule, Lease Lease);

    private readonly List<Registration> registrations = new();
    private readonly Dictionary<string, DateTime> cooldowns = new();

    public AutoConfirm(CoreServices core) : base(core)
    {
    }

    public override string Id => "confirm";
    public override string Name => "自動確認";
    public override string Description => "照各功能登記的規則自動按掉確認視窗（例如副本配對進入、採集收藏品確認）。";

    /// <summary>最近一次自動按掉的規則，給核心能力頁看「為什麼這個視窗被按掉了」。</summary>
    public string? LastConfirmed { get; private set; }

    public IEnumerable<(string Owner, string Rule)> Rules =>
        registrations.Where(r => !r.Lease.Released).Select(r => (r.Lease.Owner, r.Rule.Name));

    /// <summary>登記一條規則；回傳的物件 Dispose 就收回。</summary>
    public IDisposable Register(string owner, ConfirmRule rule)
    {
        var registration = new Registration(rule, Acquire(owner));
        registrations.Add(registration);
        return new Unregister(this, registration);
    }

    private sealed class Unregister : IDisposable
    {
        private readonly AutoConfirm owner;
        private readonly Registration registration;

        public Unregister(AutoConfirm owner, Registration registration)
        {
            this.owner = owner;
            this.registration = registration;
        }

        public void Dispose()
        {
            registration.Lease.Dispose();
            owner.registrations.Remove(registration);
        }
    }

    public override void ReleaseAll(string owner)
    {
        base.ReleaseAll(owner);
        registrations.RemoveAll(r => r.Lease.Released);
    }

    public override void ForceReleaseAll()
    {
        base.ForceReleaseAll();
        registrations.Clear();
    }

    protected override void Update(DateTime now)
    {
        registrations.RemoveAll(r => r.Lease.Released);

        foreach (var registration in registrations)
        {
            var rule = registration.Rule;
            if (cooldowns.TryGetValue(rule.Addon, out var until) && now < until) continue;

            var addon = Addons.Get(rule.Addon);
            if (addon == null) continue;

            if (!rule.Matches((nint)addon) || !rule.Confirm((nint)addon)) continue;

            cooldowns[rule.Addon] = now + Cooldown;
            LastConfirmed = $"{now:HH:mm:ss} {rule.Name}（{registration.Lease.Owner}）";
            FeatureLog.For("core").Write($"自動確認 {rule.Name}（{registration.Lease.Owner}）");
        }
    }
}
