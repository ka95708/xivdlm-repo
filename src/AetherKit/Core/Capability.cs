using System;
using System.Collections.Generic;
using System.Linq;

namespace AetherKit.Core;

/// <summary>
/// 功能對一項核心能力的借用。用完 Dispose 歸還；重複 Dispose 沒關係。
/// Suppress = 「這段期間不要作用」，例如某個功能要自己讀對話內容。只要有一筆 Suppress，能力就停，優先於任何人的借用。
/// </summary>
public sealed class Lease : IDisposable
{
    internal Lease(Capability capability, string owner, bool suppress)
    {
        Capability = capability;
        Owner = owner;
        Suppress = suppress;
        Since = DateTime.Now;
    }

    public Capability Capability { get; }
    public string Owner { get; }
    public bool Suppress { get; }
    public DateTime Since { get; }
    public bool Released { get; private set; }

    public void Dispose()
    {
        if (Released) return;

        Released = true;
        Capability.Remove(this);
    }
}

/// <summary>
/// 一項核心能力。使用者決定模式（關／只在接管時／常開），功能用 Acquire 借用、Dispose 歸還。
/// 能力自己每幀被呼叫一次 Tick，作用中才做事；狀態在作用與不作用之間切換時會通知子類別（例如開關 hook）。
/// </summary>
public abstract class Capability : IDisposable
{
    private readonly List<Lease> leases = new();
    private bool wasActive;

    protected Capability(CoreServices core)
    {
        Core = core;
    }

    protected CoreServices Core { get; }

    /// <summary>存進設定檔，發布後不要改。</summary>
    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract string Description { get; }

    public virtual CapabilityMode DefaultMode => CapabilityMode.OnLease;

    /// <summary>會不會和 TextAdvance 點同一批視窗。作用期間要請 TextAdvance 暫停。</summary>
    public virtual bool OverlapsTextAdvance => false;

    /// <summary>核心能力頁「目前」那一欄的補充，例如過場跳過用的是 hook 還是 Esc。</summary>
    public virtual string? Note => null;

    public CapabilityMode Mode
    {
        get => Core.Config.Modes.TryGetValue(Id, out var mode) ? mode : DefaultMode;
        set
        {
            Core.Config.Modes[Id] = value;
            Plugin.Instance.Save();
        }
    }

    public IReadOnlyList<Lease> Leases => leases;

    public bool Suppressed => leases.Any(l => l.Suppress);

    /// <summary>這一刻是否作用。暫停鍵按住、有人要求停、模式是關時都不作用。</summary>
    public bool Active => !Core.Paused && !Suppressed && Mode switch
    {
        CapabilityMode.Always => true,
        CapabilityMode.OnLease => leases.Any(l => !l.Suppress),
        _ => false
    };

    /// <summary>借用這項能力。owner 用模組或小功能的 Id，停用時才能一次收回。</summary>
    public Lease Acquire(string owner, bool suppress = false)
    {
        var lease = new Lease(this, owner, suppress);
        leases.Add(lease);
        return lease;
    }

    internal void Remove(Lease lease) => leases.Remove(lease);

    /// <summary>收回某個擁有者的所有借用（模組停用、插件卸載時）。</summary>
    public virtual void ReleaseAll(string owner)
    {
        foreach (var lease in leases.Where(l => l.Owner == owner).ToList())
            lease.Dispose();
    }

    /// <summary>核心能力頁的「強制歸還」：借用的功能當掉、卡住時給使用者用。</summary>
    public virtual void ForceReleaseAll()
    {
        foreach (var lease in leases.ToList())
            lease.Dispose();
    }

    internal void Tick(DateTime now)
    {
        var active = Active;
        if (active != wasActive)
        {
            wasActive = active;
            FeatureLog.For("core").Write($"{Name} {(active ? "啟動" : "停止")}");
            if (active) OnActivated();
            else OnDeactivated();
        }

        if (active) Update(now);
    }

    protected virtual void OnActivated() { }

    protected virtual void OnDeactivated() { }

    /// <summary>作用中時每幀呼叫（遊戲執行緒）。</summary>
    protected virtual void Update(DateTime now) { }

    /// <summary>核心能力頁裡這項能力的設定；沒有設定就不覆寫。</summary>
    public virtual bool HasSettings => false;

    public virtual void DrawSettings() { }

    public virtual void Dispose() { }
}
