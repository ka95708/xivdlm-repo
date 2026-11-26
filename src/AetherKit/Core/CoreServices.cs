using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;

namespace AetherKit.Core;

/// <summary>
/// 核心能力：所有功能共用的底層，例如對話推進、自動確認、修理、操作鎖。
/// 使用者在核心能力頁決定每項能力的模式；功能需要時自己借用、用完歸還，不需要使用者先去打開什麼。
/// 每幀在遊戲執行緒更新一次。
/// </summary>
public sealed class CoreServices : IDisposable
{
    private readonly Plugin plugin;
    private readonly TextAdvanceGate textAdvance = new();

    public CoreServices(Plugin plugin)
    {
        this.plugin = plugin;

        Talk = new TalkAdvance(this);
        Cutscene = new CutsceneSkip(this);
        Confirm = new AutoConfirm(this);
        Handin = new RequestHandin(this);
        Quest = new QuestFlow(this);
        Repair = new RepairService(this);
        Finish = new FinishService(plugin);

        All = new Capability[] { Talk, Cutscene, Confirm, Handin, Quest, Repair };
        FeatureLog.Enabled = Config.WriteLogs;

        Services.Framework.Update += OnUpdate;
    }

    public CoreConfig Config => plugin.Config.Core;

    public OperationLock Lock { get; } = new();

    public TalkAdvance Talk { get; }
    public CutsceneSkip Cutscene { get; }
    public AutoConfirm Confirm { get; }
    public RequestHandin Handin { get; }
    public QuestFlow Quest { get; }
    public RepairService Repair { get; }

    /// <summary>自動流程完成後登出或關閉遊戲（倒數、可取消）。</summary>
    public FinishService Finish { get; }

    /// <summary>核心能力頁的顯示順序。</summary>
    public IReadOnlyList<Capability> All { get; }

    /// <summary>按住暫停鍵時所有能力暫停。</summary>
    public bool Paused => Config.PauseKey != VirtualKey.NO_KEY && Services.KeyState[Config.PauseKey];

    /// <summary>TextAdvance 目前是不是被 AetherKit 暫停。</summary>
    public bool PausingTextAdvance => textAdvance.Pausing;

    /// <summary>收回某個功能的所有借用與操作鎖（模組停用、插件卸載時）。</summary>
    public void ReleaseAll(string owner)
    {
        foreach (var capability in All)
            capability.ReleaseAll(owner);

        Lock.Release(owner);
    }

    private void OnUpdate(IFramework framework)
    {
        if (!Services.ClientState.IsLoggedIn) return;

        var now = DateTime.Now;

        foreach (var capability in All)
            capability.Tick(now);

        // 修理開始後就算模式被改掉也要把流程走完、視窗關好
        Repair.Advance(now);

        Finish.Update(now);

        textAdvance.Set(All.Any(c => c.OverlapsTextAdvance && c.Active));
    }

    public void Dispose()
    {
        Services.Framework.Update -= OnUpdate;

        foreach (var capability in All)
            capability.Dispose();

        textAdvance.Dispose();
    }
}
