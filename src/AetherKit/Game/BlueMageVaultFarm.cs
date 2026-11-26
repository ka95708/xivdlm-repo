using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using AetherKit.Core;

namespace AetherKit.Game;

/// <summary>
/// 青魔刷教皇廳（原本的 snd-scripts/vault_BM.lua）：
/// 開場（換青魔套裝、檢查技能、掛非凡防禦）→ 每輪（修理 → 任務搜尋器選本、排隊 → 進本 → 照路線打 → 自爆 → 退本）。
///
/// 流程寫成 C# 迭代器（yield return 等待條件），讀起來跟原本的腳本一樣是一步一步往下，
/// 每幀由 Tick 推進。跟腳本不同的是每一招都等技能真的能用、真的放出去才往下，不靠固定秒數。
/// 全部透過遊戲介面：按技能跟按快捷列一樣、排本用任務搜尋器、退本點任務選單。
/// </summary>
public sealed unsafe class BlueMageVaultFarm : IDisposable
{
    /// <summary>借用核心能力、拿操作鎖、寫 log 用的名字。和模組 Id 一樣，模組停用時核心才能一起收回。</summary>
    public const string Owner = "bluemage-vault-farm";

    private const uint DutyId = 34;
    private const uint VaultTerritory = 1066;
    private const uint BlueMageJob = 36;

    private const uint MightyGuardStatus = 1719;
    private const uint MoonFluteStatus = 2498;

    private const uint SwiftcastAction = 7561;
    private const uint SprintAction = 3;

    /// <summary>確認框「確定要退出任務嗎？」（Addon 表）。</summary>
    private const uint LeaveDutyPrompt = 2545;

    private const float ArriveDistance = 3f;
    private const float EnemyRange = 30f;

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    // 教皇廳路線的三個定位點
    private static readonly Vector3 Point1 = new(-0.20f, -299.98f, 51.43f);
    private static readonly Vector3 Point2 = new(48.17f, -299.98f, -10.51f);
    private static readonly Vector3 Point3 = new(-15.51f, -300.00f, -66.54f);

    /// <summary>用到的青魔技能（青魔法書編號）。</summary>
    public static class Spell
    {
        public const int Bristle = 12;          // 怒髮衝冠
        public const int WhiteWind = 13;        // 純白微風
        public const int SelfDestruct = 21;     // 自爆
        public const int MightyGuard = 30;      // 非凡防禦
        public const int RamsVoice = 33;        // 冰凍咆哮
        public const int Bomb = 47;             // 轟雷
        public const int SonicBoom = 63;        // 音爆
        public const int JKick = 80;            // 正義飛踢
        public const int MoonFlute = 91;        // 鬥爭本能
        public const int Ultravibration = 92;   // 超振動
        public const int Tornado = 102;         // 如意大旋風
        public const int Gurgle = 122;          // 咕嚕咕嚕
        public const int Mortal = 124;          // 終有一死

        public static readonly int[] Required =
        {
            Bristle, WhiteWind, SelfDestruct, MightyGuard, RamsVoice, Bomb, SonicBoom,
            JKick, MoonFlute, Ultravibration, Tornado, Gurgle, Mortal
        };
    }

    private readonly Plugin plugin;
    private readonly Navmesh nav = new();
    private readonly FeatureLog log = FeatureLog.For(Owner);
    private readonly List<IDisposable> leases = new();

    /// <summary>執行中的流程：最上面是目前正在跑的那一段（子流程會疊上去）。</summary>
    private readonly Stack<IEnumerator<object?>> routine = new();

    /// <summary>打本那一段在堆疊裡的深度；死掉時把它以上全部收掉，直接接退本。0 = 不在打本。</summary>
    private int fightDepth;

    private Func<bool>? waitUntil;
    private DateTime waitDeadline;
    private DateTime nextTick;
    private bool subscribed;

    private string status = "待命";

    /// <summary>最近一次 Cast／Buff／Move 有沒有成功，給呼叫的流程判斷。</summary>
    private bool lastOk;

    private static Dictionary<int, (uint Action, string Name)>? spells;

    public BlueMageVaultFarm(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private BlueMageVaultFarmConfig Config => plugin.Config.BlueMageVaultFarm;

    private CoreServices Core => plugin.Core;

    public bool Running { get; private set; }

    public int Completed { get; private set; }

    public DateTime StartedAt { get; private set; }

    public TimeSpan? LastRound { get; private set; }

    private DateTime roundStarted;

    public string Status
    {
        get => status;
        private set
        {
            if (value == status) return;
            status = value;
            log.Write($"狀態 {value}");
        }
    }

    // ───────────────────────── 開始／停止 ─────────────────────────

    public void Toggle()
    {
        if (Running) Stop("手動停止");
        else Start();
    }

    public void Start()
    {
        if (Running) return;

        if (!Services.ClientState.IsLoggedIn || Services.Objects.LocalPlayer == null)
        {
            Notify.Error("青魔教皇", "還沒登入遊戲。");
            return;
        }

        if (Services.Condition[ConditionFlag.BoundByDuty])
        {
            Notify.Error("青魔教皇", "請在副本外開始。");
            return;
        }

        if (!Core.Lock.TryAcquire(Owner, "青魔教皇", () => Stop("從核心能力頁停止")))
        {
            Notify.Error("青魔教皇", $"{Core.Lock.Label} 正在執行，先停止它再開始。");
            return;
        }

        leases.Add(Core.Confirm.Register(Owner, ConfirmRule.DutyCommence()));
        leases.Add(Core.Cutscene.Acquire(Owner));
        leases.Add(Core.Repair.Acquire(Owner));

        Running = true;
        Completed = 0;
        LastRound = null;
        StartedAt = DateTime.Now;
        fightDepth = 0;
        waitUntil = null;
        routine.Clear();
        routine.Push(Main().GetEnumerator());

        log.Write($"===== 開始 輪數={Config.Loops} 套裝={Config.Gearset} 修理={Config.AutoRepair}");
        Status = "開始";

        if (!subscribed)
        {
            Services.Framework.Update += OnUpdate;
            subscribed = true;
        }
    }

    public void Stop(string reason)
    {
        if (!Running) return;

        Running = false;
        nav.Stop();
        routine.Clear();
        waitUntil = null;

        foreach (var lease in leases)
            lease.Dispose();
        leases.Clear();
        Core.Lock.Release(Owner);

        // 做完之後的動作只用一次：這次結束（不管是做完、手動停止還是出錯）就改回不做事
        if (Config.AfterFinish != FinishAction.None)
        {
            Config.AfterFinish = FinishAction.None;
            plugin.Save();
        }

        Status = $"已停止：{reason}";
        log.Write($"===== 停止：{reason} 完成 {Completed} 輪 執行 {DateTime.Now - StartedAt:hh\\:mm\\:ss}");
        Notify.Info("青魔教皇", $"停止：{reason}（完成 {Completed} 輪）");
    }

    public void Dispose()
    {
        Stop("插件卸載");
        if (subscribed) Services.Framework.Update -= OnUpdate;
    }

    // ───────────────────────── 每幀推進 ─────────────────────────

    private void OnUpdate(IFramework framework)
    {
        if (!Running) return;

        var now = DateTime.Now;
        if (now < nextTick) return;
        nextTick = now + TickInterval;

        if (!Services.ClientState.IsLoggedIn)
        {
            Stop("已登出");
            return;
        }

        if (Core.Paused) return;

        try
        {
            Advance(now);
        }
        catch (Exception e)
        {
            log.Write($"錯誤：{e}");
            Services.Log.Error(e, "青魔教皇發生錯誤");
            Stop($"發生錯誤：{e.Message}");
        }
    }

    private void Advance(DateTime now)
    {
        // 打本途中死掉（被怪打死，或自爆比預期早）：收掉打本那段，直接接退本
        if (fightDepth > 0 && Services.Objects.LocalPlayer is { IsDead: true })
        {
            log.Write("打本途中已經倒地，直接退本");
            nav.Stop();
            while (routine.Count >= fightDepth) routine.Pop();
            fightDepth = 0;
            waitUntil = null;
        }

        if (waitUntil != null)
        {
            if (!waitUntil() && now < waitDeadline) return;
            waitUntil = null;
        }

        // 一次推進到下一個等待為止
        while (routine.Count > 0 && Running)
        {
            var top = routine.Peek();
            var more = top.MoveNext();

            // 流程裡呼叫了 Stop：堆疊已經清掉
            if (!Running) return;

            if (!more)
            {
                routine.Pop();
                if (routine.Count < fightDepth) fightDepth = 0;
                continue;
            }

            switch (top.Current)
            {
                case null:
                    return;
                case Wait wait:
                    waitUntil = wait.Until;
                    waitDeadline = now + wait.Timeout;
                    if (waitUntil()) { waitUntil = null; continue; }
                    return;
                case Fight fight:
                    routine.Push(fight.Routine.GetEnumerator());
                    fightDepth = routine.Count;
                    continue;
                case IEnumerable<object?> sub:
                    routine.Push(sub.GetEnumerator());
                    continue;
            }
        }

        if (routine.Count == 0 && Running)
            Stop("流程結束");
    }

    /// <summary>等到條件成立或超時（超時也往下走，呼叫的流程自己再檢查一次條件）。</summary>
    private sealed record Wait(Func<bool> Until, TimeSpan Timeout);

    /// <summary>打本那一段：倒地時整段收掉。</summary>
    private sealed record Fight(IEnumerable<object?> Routine);

    private static Wait Until(Func<bool> condition, double seconds) => new(condition, TimeSpan.FromSeconds(seconds));

    private static Wait Delay(double seconds) => new(() => false, TimeSpan.FromSeconds(seconds));

    // ───────────────────────── 主流程 ─────────────────────────

    private IEnumerable<object?> Main()
    {
        yield return Prepare();
        if (!Running) yield break;

        while (Running && Completed < Config.Loops)
        {
            roundStarted = DateTime.Now;
            log.Write($"===== 第 {Completed + 1} / {Config.Loops} 輪");

            yield return Repair();
            yield return MightyGuard();

            yield return Queue();
            if (!lastOk) yield break;

            yield return Enter();
            if (!Running) yield break;

            yield return new Fight(Rotation());
            yield return Leave();
            if (!Running) yield break;

            Completed++;
            LastRound = DateTime.Now - roundStarted;
            log.Write($"第 {Completed} 輪完成，用時 {LastRound:mm\\:ss}");
        }

        if (!Running) yield break;

        var after = Config.AfterFinish; // Stop 會把它改回不做事，先記下來
        Stop($"完成 {Completed} 輪");
        Core.Finish.Schedule(after, "青魔教皇");
    }

    /// <summary>開場一次：換青魔套裝、檢查技能組。</summary>
    private IEnumerable<object?> Prepare()
    {
        Status = "等待角色就緒";
        yield return Until(PlayerReady, 120);

        if (Job != BlueMageJob)
        {
            var index = FindGearset(Config.Gearset);
            if (index < 0)
            {
                Stop(string.IsNullOrWhiteSpace(Config.Gearset)
                    ? "目前不是青魔道士，也沒有設定要換的套裝"
                    : $"找不到名稱是「{Config.Gearset}」的套裝");
                yield break;
            }

            Status = "換青魔套裝";
            EquipGearset(index);
            yield return Until(() => Job == BlueMageJob, 5);
            if (Job != BlueMageJob)
            {
                Stop("換套裝後還不是青魔道士");
                yield break;
            }

            yield return Delay(1);
        }

        var missing = MissingSpells(true);
        if (missing.Count > 0)
        {
            Stop($"目前的青魔技能組缺少：{string.Join("、", missing)}");
            yield break;
        }
    }

    private IEnumerable<object?> Repair()
    {
        if (!Config.AutoRepair || !Core.Repair.NeedsRepair) yield break;

        Status = "修理";
        log.Write($"耐久最低 {RepairService.LowestDurability()}%，修理");
        yield return Until(() => GameState.Idle, 15);
        if (!Core.Repair.TryStart(DateTime.Now))
        {
            log.Write("修理沒有開始（可能沒有暗物質或核心能力的修理被關掉），略過");
            yield break;
        }

        yield return Until(() => !Core.Repair.Busy, 30);
        log.Write($"修理結束 最低耐久={RepairService.LowestDurability()}%");
    }

    /// <summary>非凡防禦是姿態技，再按一次會取消，所以每次放完都要等夠久才判斷有沒有上身。</summary>
    private IEnumerable<object?> MightyGuard()
    {
        if (HasStatus(MightyGuardStatus)) yield break;

        Status = "施放非凡防禦";
        yield return Buff(Spell.MightyGuard, MightyGuardStatus, 3);
        if (!lastOk) log.Write("非凡防禦上不去，繼續");
    }

    /// <summary>打開任務搜尋器選好教皇廳、確認選項、按參加。</summary>
    private IEnumerable<object?> Queue()
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            lastOk = false;
            Status = "任務搜尋器選本";
            yield return Until(() => GameState.Idle, 30);

            // 選項只在不對時才改（通常只有第一輪）；排隊函式直接帶副本編號，不用開任務搜尋器視窗。
            // 跟原本腳本一樣直接設好任務搜尋器的選項：解除限制、等級同步開，最低裝等、無視增益、探索模式關。
            // 設了沒生效（例如被遊戲改回去）才退回提示使用者自己改。
            if (!SettingsOk(out var before))
            {
                log.Write($"任務搜尋器選項不符（{before}），直接設好");
                ApplyDutySettings();
                yield return Delay(0.3);
            }

            if (!SettingsOk(out var problem))
            {
                Status = $"請在任務搜尋器的「設定」{problem}";
                log.Write($"任務搜尋器設定不符：{problem}");
                Notify.Attention("青魔教皇", $"請在任務搜尋器的「設定」{problem}，勾好會自動繼續。");
                yield return Until(() => SettingsOk(out _), 600);
                if (!SettingsOk(out _))
                {
                    Stop("任務搜尋器設定一直沒有改好");
                    yield break;
                }
            }

            // 跟原本腳本一樣直接呼叫遊戲的排隊函式；前兩次都不行，最後一次才開視窗選本、按「參加」
            Status = "排隊";
            var useButton = attempt == 3;
            if (useButton)
            {
                OpenDuty();
                yield return Until(() => Addons.IsOpen("ContentsFinder"), 5);
                yield return Delay(0.5);
            }

            log.Write($"排隊（第 {attempt} 次，{(useButton ? "按參加" : "排隊函式")}）");
            if (useButton) ClickJoin();
            else QueueDuty();

            yield return Until(() => QueueState != ContentsFinderQueueInfo.QueueStates.None, 5);
            if (QueueState != ContentsFinderQueueInfo.QueueStates.None)
            {
                lastOk = true;
                yield break;
            }

            log.Write("沒有進入排隊");
            yield return Delay(2);
        }

        Stop("排不進任務");
    }

    /// <summary>等配對（自動確認會按「進入」）、過圖、角色與 vnavmesh 就緒。</summary>
    private IEnumerable<object?> Enter()
    {
        Status = "等待進本";
        yield return Until(InVault, Config.QueueTimeoutSeconds);
        if (!InVault())
        {
            Stop("等太久沒有進本");
            yield break;
        }

        Status = "進本讀取中";
        yield return Until(() => !GameState.BetweenAreas && PlayerReady(), 60);

        Status = "等待 vnavmesh";
        yield return Until(() => nav.Ready, 120);
        if (!nav.Ready) log.Write("vnavmesh 等太久還沒準備好，照樣繼續");

        yield return Delay(0.5);
    }

    // ───────────────────────── 打本 ─────────────────────────

    private IEnumerable<object?> Rotation()
    {
        roundGil = Gil;
        Status = "鬥爭本能";
        yield return Buff(Spell.MoonFlute, MoonFluteStatus, 4);
        if (!lastOk)
        {
            log.Write("鬥爭本能上不去，這輪直接退本");
            yield break;
        }

        // 第一個定位點：飛踢進場、一輪範圍技
        Status = "前往第一個點";
        yield return Move(Point1);

        Status = "第一波";
        TargetNearest();
        yield return Cast(Spell.JKick);
        yield return Cast(Spell.Gurgle);
        yield return Cast(Spell.Mortal);
        TargetNearest();
        yield return Cast(Spell.Bomb);
        yield return Cast(Spell.Tornado);

        // 往第二點走，2 秒後看還有沒有沒死的，有就停下來用音爆補
        Status = "前往第二個點";
        yield return Cleanup(Point2);

        Status = "第二波";
        yield return Cast(Spell.RamsVoice);
        yield return Delay(0.3);
        yield return Cast(Spell.Ultravibration);
        yield return Cast(Spell.WhiteWind);
        yield return Cast(SprintAction, "衝刺");

        Status = "前往第三個點";
        yield return Move(Point3);

        // 結算：怒髮衝冠 → 即刻詠唱 → 自爆
        Status = "自爆";
        yield return Cast(Spell.Bristle);
        yield return Cast(SwiftcastAction, "即刻詠唱");
        yield return SelfDestruct();
    }

    /// <summary>邊走邊看：先往 dest 走 2 秒，附近還有戰鬥中的怪就停下來用音爆打，打完再走完。</summary>
    private IEnumerable<object?> Cleanup(Vector3 dest)
    {
        yield return Until(() => nav.Ready, 30);
        nav.MoveCloseTo(dest, false, 1f);
        yield return Delay(2);

        if (Leftovers().Count > 0)
        {
            nav.Stop();
            log.Write($"殘血補刀：還有 {Leftovers().Count} 隻");

            for (var round = 0; round < 6; round++)
            {
                var target = Leftovers().OrderBy(Distance).FirstOrDefault();
                if (target == null) break;

                Services.Targets.Target = target;
                yield return Cast(Spell.SonicBoom);
                yield return Until(() => target.IsDead || target.CurrentHp == 0, 1.5);
            }
        }

        yield return Move(dest);
    }

    private IEnumerable<object?> SelfDestruct()
    {
        var (action, _) = SpellAction(Spell.SelfDestruct);
        var deadline = DateTime.Now.AddSeconds(15);

        while (DateTime.Now < deadline)
        {
            var player = Services.Objects.LocalPlayer;
            if (player == null || player.IsDead) yield break;

            if (Ready(ActionType.Action, action))
                UseAction(ActionType.Action, action);

            yield return null;
        }

        log.Write("自爆 15 秒都沒有倒地，直接退本");
    }

    // ───────────────────────── 退本 ─────────────────────────

    /// <summary>
    /// 關掉倒地後跳出的確認框（按「否」，只關視窗）→ 開任務選單 → 退出任務 → 確認「是」。
    /// 只對「確定要退出任務嗎？」按「是」，其他確認框一律按「否」，不會按錯框。
    /// </summary>
    private IEnumerable<object?> Leave()
    {
        nav.Stop();

        // 跟原本腳本一樣：倒地後等「即將回到開始地點」的確認框跳出來才退本（最多 8 秒），
        // 這段時間剛好讓怪死掉、金幣入帳。沒倒地（例如這輪沒打）就不用等。
        if (Services.Objects.LocalPlayer is { IsDead: true })
        {
            Status = "等待倒地確認框";
            yield return Until(() => Addons.IsOpen("SelectYesno"), 8);
        }

        log.Write($"這輪金幣 +{(long)Gil - roundGil}");
        Status = "退本";

        // 確認框看文字決定按「是」或「否」，不會按錯
        var leavePrompt = Addons.Text(LeaveDutyPrompt);
        var deadline = DateTime.Now.AddSeconds(60);
        var nextMenu = DateTime.MinValue;

        while (InVault() && DateTime.Now < deadline)
        {
            if (AnswerYesno(leavePrompt))
            {
                yield return Delay(0.5);
                continue;
            }

            if (ClickLeaveInMenu())
            {
                yield return Until(() => Addons.IsOpen("SelectYesno"), 2);
                continue;
            }

            if (DateTime.Now >= nextMenu)
            {
                nextMenu = DateTime.Now.AddSeconds(3);
                plugin.Executor.Enqueue("/dutyfinder");
            }

            yield return Delay(0.3);
        }

        if (InVault())
        {
            Stop("退不出副本");
            yield break;
        }

        Status = "出本讀取中";
        yield return Until(() => !GameState.BetweenAreas && PlayerReady(), 60);
        yield return Delay(0.3);
    }

    // ───────────────────────── 技能 ─────────────────────────

    private IEnumerable<object?> Cast(int spell)
    {
        var (action, name) = SpellAction(spell);
        return Cast(action, name);
    }

    /// <summary>
    /// 放一招：等它能用（不在詠唱、沒有動作鎖、冷卻好了）就按，跟按快捷列一樣；
    /// 有詠唱的等詠唱結束。6 秒內都不能用就記下來、跳過這招。
    /// </summary>
    private IEnumerable<object?> Cast(uint action, string name, ActionType type = ActionType.Action)
    {
        lastOk = false;
        var deadline = DateTime.Now.AddSeconds(6);

        while (true)
        {
            if (Ready(type, action) && UseAction(type, action))
                break;

            if (DateTime.Now >= deadline)
            {
                var status = ActionStatus(type, action);
                log.Write($"{name} 6 秒內都不能用（狀態 {status}），跳過");
                yield break;
            }

            yield return null;
        }

        lastOk = true;
        log.Write($"放 {name}");

        // 有詠唱的：等詠唱開始再等結束；瞬發的第一段直接超時
        yield return Until(() => Services.Objects.LocalPlayer?.IsCasting == true, 0.5);
        yield return Until(() => Services.Objects.LocalPlayer?.IsCasting != true, 5);
    }

    /// <summary>放到身上有這個狀態為止。放完至少等 2.5 秒才判斷失敗（狀態要一下才掛上去）。</summary>
    private IEnumerable<object?> Buff(int spell, uint statusId, int tries)
    {
        for (var i = 0; i < tries; i++)
        {
            if (HasStatus(statusId)) { lastOk = true; yield break; }

            yield return Cast(spell);
            yield return Until(() => HasStatus(statusId), 2.5);
            if (HasStatus(statusId)) { lastOk = true; yield break; }

            log.Write($"{SpellAction(spell).Name} 放了但狀態沒上（第 {i + 1} 次）");
            yield return Delay(1);
        }

        lastOk = HasStatus(statusId);
    }

    // ───────────────────────── 遊戲操作（迭代器裡不能用指標，集中在這裡） ─────────────────────────

    private static void EquipGearset(int index) => RaptureGearsetModule.Instance()->EquipGearset(index, 0);

    /// <summary>任務搜尋器選教皇廳，跟點副本連結一樣。</summary>
    private static void OpenDuty() => AgentContentsFinder.Instance()->OpenRegularDuty(DutyId, false);

    /// <summary>
    /// 排教皇廳：直接呼叫遊戲的排隊函式（原本腳本透過 SND 的 QueueDuty 也是這樣做），
    /// 用的是任務搜尋器目前的選項（解除限制等），不需要視窗。
    /// </summary>
    // [遊戲函式] ContentsFinderQueueInfo.QueueDuties（見 改版檢查清單.md）
    private static void QueueDuty()
    {
        var info = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder.Instance()->GetQueueInfo();
        var id = DutyId;
        info->QueueDuties(&id, 1, 0);
    }

    /// <summary>任務搜尋器的「參加」（排隊函式失敗時的備用）。</summary>
    private static void ClickJoin()
    {
        var finder = Addons.Get("ContentsFinder");
        if (finder != null) Addons.Fire(finder, 12, 0);
    }

    private static bool UseAction(ActionType type, uint action) =>
        ActionManager.Instance()->UseAction(type, action);

    private static uint ActionStatus(ActionType type, uint action) =>
        ActionManager.Instance()->GetActionStatus(type, action);

    /// <summary>有確認框就處理：是退出任務的按「是」，其他按「否」。回傳 true = 有確認框。</summary>
    private bool AnswerYesno(string leavePrompt)
    {
        var yesno = Addons.Get("SelectYesno");
        if (yesno == null) return false;

        var prompt = Addons.YesnoPrompt(yesno);
        if (leavePrompt.Length > 0 && prompt.Contains(leavePrompt))
        {
            log.Write("確認退出任務");
            Addons.ClickYes(yesno);
        }
        else
        {
            log.Write($"關掉確認框：{System.Text.RegularExpressions.Regex.Replace(prompt, @"[\p{C}�]", "")}");
            Addons.ClickButton(yesno, ((AddonSelectYesno*)yesno)->NoButton);
        }

        return true;
    }

    /// <summary>任務選單開著就按「退出任務」。</summary>
    private bool ClickLeaveInMenu()
    {
        var menu = Addons.Get("ContentsFinderMenu");
        if (menu == null) return false;

        log.Write("任務選單按退出");
        Addons.Fire(menu, 0, 1);
        return true;
    }

    private static bool Ready(ActionType type, uint action)
    {
        var player = Services.Objects.LocalPlayer;
        if (player == null || player.IsDead || player.IsCasting) return false;

        var manager = ActionManager.Instance();
        return manager->AnimationLock <= 0 && manager->GetActionStatus(type, action) == 0;
    }

    /// <summary>青魔法書編號 → 技能 ID 與名稱（從遊戲資料表查）。</summary>
    public static (uint Action, string Name) SpellAction(int number)
    {
        if (spells == null)
        {
            var transient = Services.Data.GetExcelSheet<AozActionTransient>();
            spells = new Dictionary<int, (uint, string)>();
            foreach (var row in Services.Data.GetExcelSheet<AozAction>())
            {
                if (transient.GetRowOrDefault(row.RowId) is not { } t || t.Number == 0) continue;
                spells[t.Number] = (row.Action.RowId, row.Action.ValueNullable?.Name.ExtractText() ?? $"#{row.Action.RowId}");
            }
        }

        return spells.TryGetValue(number, out var spell) ? spell : (0, $"No.{number}");
    }

    /// <summary>目前青魔技能組裡缺的必備技能名稱。讀不到技能組（全部都對不上）時記 log、不擋。</summary>
    public List<string> MissingSpells(bool writeLog = false)
    {
        var manager = ActionManager.Instance();
        var active = new HashSet<uint>();
        for (var slot = 0; slot < 24; slot++)
            active.Add(manager->GetActiveBlueMageActionInSlot(slot));

        var missing = Spell.Required
            .Select(SpellAction)
            .Where(s => !active.Contains(s.Action))
            .Select(s => s.Name)
            .ToList();

        if (missing.Count == Spell.Required.Length)
        {
            if (writeLog) log.Write($"技能組讀到的值都對不上，可能讀法不對，先不擋：{string.Join(",", active)}");
            return new List<string>();
        }

        return missing;
    }

    // ───────────────────────── 移動與目標 ─────────────────────────

    /// <summary>用 vnavmesh 走到 dest，離 3 米內才算到；沒到就重走，最多 3 次。</summary>
    private IEnumerable<object?> Move(Vector3 dest)
    {
        lastOk = false;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            yield return Until(() => nav.Ready, 30);
            nav.MoveCloseTo(dest, false, 1f);
            yield return Until(() => nav.Busy, 2);
            yield return Until(() => !nav.Busy, 60);
            if (nav.Busy) nav.Stop();

            var distance = DistanceTo(dest);
            if (distance <= ArriveDistance)
            {
                lastOk = true;
                yield break;
            }

            log.Write($"沒走到定位點（還差 {distance:F1} 米），重走（{attempt}/3）");
            yield return Delay(1);
        }
    }

    private static void TargetNearest()
    {
        var target = Enemies().OrderBy(Distance).FirstOrDefault();
        if (target != null) Services.Targets.Target = target;
    }

    private static IEnumerable<IBattleNpc> Enemies() =>
        Services.Objects.OfType<IBattleNpc>()
            .Where(n => n.BattleNpcKind == BattleNpcSubKind.Enemy && n.IsTargetable && !n.IsDead && n.CurrentHp > 0
                        && Distance(n) <= EnemyRange);

    /// <summary>還在跟我們打的怪。</summary>
    private static List<IBattleNpc> Leftovers() =>
        Enemies().Where(n => (n.StatusFlags & StatusFlags.InCombat) != 0).ToList();

    private static float Distance(IGameObject obj) => DistanceTo(obj.Position);

    private static float DistanceTo(Vector3 p)
    {
        var player = Services.Objects.LocalPlayer;
        return player == null ? float.MaxValue : Vector3.Distance(player.Position, p);
    }

    // ───────────────────────── 狀態判斷 ─────────────────────────

    /// <summary>進本開打時的金幣，算這輪賺多少用。</summary>
    private uint roundGil;

    private static uint Gil => InventoryManager.Instance()->GetGil();

    private static uint Job => Services.Objects.LocalPlayer?.ClassJob.RowId ?? 0;

    private static bool PlayerReady()
    {
        var player = Services.Objects.LocalPlayer;
        return player != null && player.CurrentHp > 0 && !GameState.BetweenAreas && !GameState.InCutscene;
    }

    private static bool HasStatus(uint id) =>
        Services.Objects.LocalPlayer?.StatusList.Any(s => s.StatusId == id) == true;

    private static bool InVault() =>
        Services.ClientState.TerritoryType == VaultTerritory && Services.Condition[ConditionFlag.BoundByDuty];

    private static ContentsFinderQueueInfo.QueueStates QueueState =>
        FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder.Instance()->QueueInfo.QueueState;

    /// <summary>設好任務搜尋器的選項（跟原本腳本一樣），用核心共用的任務搜尋器設定。</summary>
    private void ApplyDutySettings()
    {
        DutyFinderSettings.Set(new Dictionary<DutyFinderOption, byte>
        {
            [DutyFinderOption.UnrestrictedParty] = 1,
            [DutyFinderOption.LevelSync] = 1,
            [DutyFinderOption.MinimumIl] = 0,
            [DutyFinderOption.SilenceEcho] = 0,
            [DutyFinderOption.ExplorerMode] = 0,
        });
        log.Write(DutyFinderSettings.UsesGameFunction ? "已用遊戲的套用函式設好" : "找不到套用函式，已直接改值");
    }

    private static bool SettingsOk(out string problem)
    {
        var finder = FFXIVClientStructs.FFXIV.Client.Game.UI.ContentsFinder.Instance();
        var wrong = new List<string>();
        if (!finder->IsUnrestrictedParty) wrong.Add("勾選「解除限制」");
        if (!finder->IsLevelSync) wrong.Add("勾選「等級同步」");
        if (finder->IsMinimalIL) wrong.Add("取消「最低裝等」");
        if (finder->IsSilenceEcho) wrong.Add("取消「無視增益效果」");
        if (finder->IsExplorerMode) wrong.Add("取消「探索模式」");
        problem = string.Join("、", wrong);
        return wrong.Count == 0;
    }

    private static int FindGearset(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return -1;

        var module = RaptureGearsetModule.Instance();
        var partial = -1;
        for (var i = 0; i < 100; i++)
        {
            if (!module->IsValidGearset(i)) continue;

            var entry = module->GetGearset(i);
            if (entry == null) continue;

            var gearsetName = entry->NameString;
            if (gearsetName == name) return i;
            if (partial < 0 && gearsetName.Contains(name)) partial = i;
        }

        return partial;
    }
}
