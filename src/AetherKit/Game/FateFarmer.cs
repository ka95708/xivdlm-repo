using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Fate;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AetherKit.Core;
using FateState = Dalamud.Game.ClientState.Fates.FateState;
using CsCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using CsGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AetherKit.Game;

/// <summary>
/// 在目前區域循環打 FATE：挑一個 → 騎坐騎過去 → 等級同步 → 挑目標、走到射程內交給自動循環打 → 下一個。
/// 兩場之間順便修裝備、餵陸行鳥；要找 NPC 開始的 FATE 會走過去對話。
///
/// 移動只在需要時才重新尋路：目標還在射程外、vnavmesh 沒在走、或目標已經離上次的終點太遠。
/// 每幀重算路徑會讓角色走一下停一下（每次重算都會先停），這是 DR 那版最明顯的毛病。
/// </summary>
public sealed unsafe class FateFarmer : IDisposable
{
    private const uint Jump = 2;
    private const uint MountRoulette = 9;
    private const uint Dismount = 23;
    private const uint GysahlGreens = 4868;

    /// <summary>判斷「能不能攻擊」用的技能：冰結。CanUseActionOnTarget 只看目標類型，不看自己是不是法系。</summary>
    private const uint AttackCheckAction = 142;

    /// <summary>頭上有「可以開始 FATE」圖示的 NPC。</summary>
    private const uint FateStartIcon = 60093;

    private const int MaxStuck = 5;

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StuckWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(1);

    /// <summary>借用核心能力、拿操作鎖時用的名字。和模組 Id 一樣，模組停用時核心才能一起收回。</summary>
    private const string Owner = "auto-fate";

    // 坦克職業 → (姿態技能, 姿態狀態)：騎士、戰士、暗黑騎士、絕槍戰士
    private static readonly Dictionary<uint, (uint Action, uint Status)> TankStances = new()
    {
        [1] = (28, 79), [19] = (28, 79),
        [3] = (48, 91), [21] = (48, 91),
        [32] = (3629, 743),
        [37] = (16142, 1833)
    };

    private readonly Plugin plugin;
    private readonly List<IDisposable> leases = new();
    private DateTime nextTrade;
    private DateTime nextStance;
    private readonly Navmesh nav = new();
    private readonly FeatureLog log = FeatureLog.For("autofate");
    private DateTime startedAt;
    private DateTime nextSnapshot;
    private string status = "待命";
    private string statusKey = string.Empty;
    private int startGemstones;

    private DateTime nextTick;
    private DateTime nextPath;
    private DateTime nextMount;
    private DateTime nextSync;
    private DateTime nextInteract;
    private DateTime nextChocobo;

    /// <summary>該檢查陸行鳥了：開始、每次傳送完、每場 FATE 結束、復活後各檢查一次（不夠時間才叫）。</summary>
    private bool chocoboDue;

    /// <summary>騎坐騎趕路中被怪打、決定不理它（只記一次 log）。</summary>
    private bool ignoringAttackers;

    /// <summary>餵了野菜之後什麼時候檢查陸行鳥有沒有出來；MinValue = 沒在等。</summary>
    private DateTime chocoboCheckAt = DateTime.MinValue;

    /// <summary>傳送、復活後角色剛讀完，馬上用道具遊戲不接受：這個時間之前先不叫陸行鳥（原地等）。</summary>
    private DateTime chocoboNotBefore = DateTime.MinValue;

    private static readonly TimeSpan ChocoboSettle = TimeSpan.FromSeconds(3);

    /// <summary>失敗後原地隔多久再試（同一個檢查點最多 3 次）。</summary>
    private static readonly TimeSpan ChocoboRetry = TimeSpan.FromSeconds(1.5);
    private float chocoboBefore;
    private int chocoboFailures;

    private DateTime interactedAt;
    private DateTime nextTalk;
    private bool warnedNoGreens;

    // 趕路沒進展：跟 vnavmesh 狀態無關，只看位置
    private static readonly TimeSpan TravelStallAfter = TimeSpan.FromSeconds(10);
    private Vector3 travelAnchor;
    private DateTime travelSince;
    private DateTime lastTravelTick;
    private int travelFails;

    // vnavmesh 開始算這條路徑的時間；MinValue = 沒在算
    private DateTime pathfindSince = DateTime.MinValue;
    private static readonly TimeSpan PathfindTimeout = TimeSpan.FromSeconds(45);
    private DateTime groundOnlyUntil;
    private DateTime nextPathFailLog;

    private Vector3 stuckAnchor;
    private DateTime stuckSince;
    private int stuckCount;

    // 卡住傳送脫困：上一次傳送脫困的時間；這段時間內又卡住就停止
    private static readonly TimeSpan StuckTeleportWindow = TimeSpan.FromMinutes(5);
    private DateTime stuckTeleportAt = DateTime.MinValue;

    // 連續幾個 FATE 都在同一個位置判定到不了：問題在起點（站在導航網格外），不是 FATE
    private Vector3 unreachableAnchor;
    private int unreachableHere;

    private ushort fateId;

    // 打不到的目標：同一個目標這麼久沒進展就略過
    private static readonly TimeSpan UnreachableAfter = TimeSpan.FromSeconds(12);

    // 在射程內站這麼久都沒打到，就改貼近打（比判定打不到早，讓它有機會走過去）
    private static readonly TimeSpan CloseInAfter = TimeSpan.FromSeconds(6);
    private ulong closeInTarget;

    // 目標血量一直沒掉：多半是自動循環插件停了
    private static readonly TimeSpan NoDamageAfter = TimeSpan.FromSeconds(20);
    private ulong damageTarget;
    private uint damageHp;
    private DateTime damageSince;

    // 剛下坐騎這段時間內不再上坐騎，免得怪跑出範圍時上上下下
    private static readonly TimeSpan NoRemountAfterDismount = TimeSpan.FromSeconds(6);
    private DateTime noRemountUntil;
    private static readonly TimeSpan IgnoreTargetFor = TimeSpan.FromMinutes(1);
    private readonly Dictionary<ulong, DateTime> ignoredTargets = new();
    private ulong engageTarget;
    private DateTime engageSince;
    private float engageBest;
    private uint engageHp;

    // 進度不動的 FATE：在裡面這麼久進度都沒變就先換別的
    private static readonly TimeSpan FateStallAfter = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan SkipFateFor = TimeSpan.FromMinutes(15);
    private readonly Dictionary<ushort, DateTime> skippedFates = new();
    private ushort fateProgressFate;
    private byte fateProgress = byte.MaxValue;
    private DateTime fateProgressAt;

    // vnavmesh 的 IsReady 偶爾會短暫變 false，連續 3 秒都不是才算沒準備好
    private DateTime navNotReadySince = DateTime.MinValue;

    // 繳交型 FATE：正在去交道具、開始時身上有幾個、交不成功的冷卻
    private static readonly TimeSpan TurnInTimeout = TimeSpan.FromSeconds(45);
    private bool turningIn;
    private DateTime turnInStarted;
    private DateTime turnInLastTick;
    private int turnInHeld;
    private DateTime turnInCooldown;
    private bool warnedTurnInNpc;
    private int turnInProgress;

    /// <summary>
    /// 繳交型 FATE 每個道具大約推進幾 %（FATE 編號 → %），從實際繳交前後的進度量出來。
    /// 同一個 FATE 下次再出現也用得上；別人也在交時會量偏高，所以取看過的最小值。
    /// </summary>
    private readonly Dictionary<ushort, float> progressPerItem = new();

    /// <summary>開始 FATE 時對話的 NPC（FATE 編號 → 物件 ID、BaseId）。</summary>
    private readonly Dictionary<ushort, (ulong ObjectId, uint BaseId)> startedNpcs = new();
    private (ulong ObjectId, uint BaseId) lastTurnInNpc;
    private Vector3? moveDest;
    private bool pathFly;
    private bool attacking;
    private bool subscribed;

    /// <summary>一次傳送：選好的水晶、試了幾次、有沒有看到過圖畫面（看到又結束 = 傳送完成）。</summary>
    private sealed class TeleportJob
    {
        public required Aetherytes.Entry Aetheryte { get; init; }
        public DateTime Started { get; init; }
        public DateTime NextTry { get; set; }
        public int Attempts { get; set; }
        public bool SawLoading { get; set; }

        /// <summary>已經記過「傳送暫時不能用」的原因（每個傳送只記一次）。</summary>
        public string LoggedBlock { get; set; } = string.Empty;

        /// <summary>同地圖抄近路的傳送：傳完繼續去原本的 FATE，失敗就用飛的，不停止。</summary>
        public bool Shortcut { get; init; }
    }

    /// <summary>已經為這個 FATE 抄過近路（傳過一次就不再傳，免得來回）。</summary>
    private uint shortcutFate;

    private enum JobResult
    {
        Working,
        Done,
        Failed
    }

    /// <summary>多區域：換到下一個區域的傳送。</summary>
    private TeleportJob? zoneJob;

    /// <summary>自動停止後回水晶的傳送，停止之後還會繼續跑到完成。</summary>
    private TeleportJob? homeJob;

    /// <summary>從什麼時候開始沒有能打的 FATE（多區域換圖用）。</summary>
    private DateTime noFateSince = DateTime.MinValue;

    public FateFarmer(Plugin plugin)
    {
        this.plugin = plugin;
        Core.Repair.Finished += OnRepairFinished;
    }

    private AutoFateConfig Config => plugin.Config.AutoFate;

    private CoreServices Core => plugin.Core;

    public bool Running { get; private set; }

    /// <summary>
    /// 給模組頁顯示的狀態。狀態「類別」改變時寫進 log（括號裡的距離、秒數不算），
    /// 不然走路時每 0.1 秒更新一次距離，log 會被洗爆。
    /// </summary>
    public string Status
    {
        get => status;
        private set
        {
            if (value == status) return;
            status = value;

            var key = System.Text.RegularExpressions.Regex.Replace(value, @"（[^）]*）", string.Empty);
            if (key == statusKey) return;

            statusKey = key;
            log.Write($"狀態 {value}");
        }
    }


    /// <summary>開始到現在多久；沒在跑時是 null。</summary>
    public TimeSpan? Elapsed => Running ? DateTime.Now - startedAt : null;

    /// <summary>正在打（或正要去）的 FATE 名稱。</summary>
    public string CurrentFate { get; private set; } = string.Empty;

    public static int Gemstones =>
        InventoryManager.Instance()->GetInventoryItemCount(AutoFateConfig.BicolorGemstone);

    public void Toggle()
    {
        if (Running)
        {
            Stop("手動停止");
        }
        else if (homeJob != null)
        {
            // 正在回水晶時又按一次：取消傳送（完成後的登出、關閉遊戲也一起取消）
            homeJob = null;
            pendingFinish = FinishAction.None;
            Status = "已取消傳送";
        }
        else
        {
            Start();
        }
    }

    /// <summary>停止後正在傳送回水晶。</summary>
    public bool ReturningHome => homeJob != null;

    /// <summary>
    /// 執行期間借用的核心能力：對話推進（和 NPC 對話開始 FATE、繳交）、交道具（繳交型）、
    /// 修理（設定有開才借）。停止時全部歸還，使用者自己的模式設定不會被改。
    /// </summary>
    private void AcquireCore()
    {
        ReleaseLeases();
        leases.Add(Core.Talk.Acquire(Owner));
        leases.Add(Core.Handin.Acquire(Owner));

        // 一律借：執行中才打開「自動修理」也能馬上生效，要不要修由 Maintenance 看設定決定
        leases.Add(Core.Repair.Acquire(Owner));
    }

    private void ReleaseLeases()
    {
        foreach (var lease in leases)
            lease.Dispose();

        leases.Clear();
    }

    private void ReleaseCore()
    {
        ReleaseLeases();
        Core.Lock.Release(Owner);
    }

    /// <summary>執行時會借用的核心能力名稱，給模組頁顯示。</summary>
    public string BorrowedCapabilities =>
        Config.AutoRepair ? "對話推進、交道具、修理" : "對話推進、交道具";

    private void OnRepairFinished(bool ok, string? problem)
    {
        if (!Running) return;

        log.Write(ok
            ? $"修理 完成 最低耐久={RepairService.LowestDurability()}%"
            : $"修理 失敗：{problem}");
        Status = ok ? "修理完成" : "修理失敗";
    }

    public void Start()
    {
        if (Running) return;

        if (!Services.ClientState.IsLoggedIn)
        {
            Notify.Error("自動Fate", "還沒登入遊戲。");
            return;
        }

        var missing = FateDependencies.Check()
            .Where(d => d.Level == FateDependencies.Level.Required && !d.Loaded)
            .Select(d => d.Name)
            .ToList();
        if (missing.Count > 0)
        {
            Notify.Error("自動Fate", $"缺少必要插件：{string.Join("、", missing)}。");
            return;
        }

        if (!nav.Ready)
        {
            Notify.Error("自動Fate", "vnavmesh 的導航網格還沒建好，等它建完再開始。");
            return;
        }

        if (Config.StopOnGemstones && Gemstones >= Config.GemstoneLimit)
        {
            Notify.Error("自動Fate", $"雙色寶石已經有 {Gemstones} 個，達到設定的 {Config.GemstoneLimit}。");
            return;
        }

        if (!Core.Lock.TryAcquire(Owner, "自動Fate", () => Stop("從核心能力頁停止")))
        {
            Notify.Error("自動Fate", $"{Core.Lock.Label} 正在執行，先停止它再開始。");
            return;
        }

        AcquireCore();

        Running = true;
        fateId = 0;
        moveDest = null;
        attacking = false;
        warnedNoGreens = false;
        chocoboCheckAt = DateTime.MinValue;
        chocoboFailures = 0;
        nextChocobo = DateTime.MinValue;
        chocoboDue = true;
        stuckCount = 0;
        stuckSince = DateTime.MinValue;
        stuckTeleportAt = DateTime.MinValue;
        failedLandings.Clear();
        landingFate = 0;
        shortcutFate = 0;
        unreachableHere = 0;
        CurrentFate = string.Empty;
        startedAt = DateTime.Now;
        nextSnapshot = DateTime.MinValue;
        startGemstones = Gemstones;
        chocoboStanceTries = 0;
        chocoboStanceAt = DateTime.MinValue;
        homeJob = null;
        pendingFinish = FinishAction.None;
        zoneJob = null;
        noFateSince = DateTime.MinValue;
        deaths.Clear();
        deadSince = DateTime.MinValue;
        turningIn = false;
        warnedTurnInNpc = false;
        turnInCooldown = DateTime.MinValue;
        RememberWrathTargeting();

        log.Write($"===== 開始 區域={Services.ClientState.TerritoryType} 寶石={startGemstones} " +
                  $"類型=[{string.Join(",", Config.Kinds)}] 黑名單={Config.Blacklist.Count} " +
                  $"寶石停止={(Config.StopOnGemstones ? Config.GemstoneLimit : 0)} 定時停止={(Config.StopOnTimer ? Config.StopAfterMinutes : 0)} 分");
        log.Write("插件 " + string.Join(" ", FateDependencies.Check().Select(d => $"{d.Name}={(d.Loaded ? "開" : "關")}")));
        if (Config.MultiZone)
            log.Write("多區域 " + string.Join(" → ", Config.Zones.Select(Aetherytes.ZoneName)));

        Status = "開始";

        if (!subscribed)
        {
            Services.Framework.Update += OnUpdate;
            subscribed = true;
        }

        // 多區域：人不在清單裡的區域，先傳過去
        if (Config.MultiZone && Config.Zones.Count > 0 && !Config.Zones.Contains(Services.ClientState.TerritoryType))
            GoToZone(Config.Zones[0]);
    }

    /// <summary>
    /// 停止。automatic = 程式自己停的（寶石到量、時間到、卡住、出錯）：照設定傳送（指定的地點，或這張地圖最近的以太之光）。
    /// 手動停止、死亡、登出、從核心能力頁停止、插件卸載都不傳。
    /// </summary>
    public void Stop(string reason, bool automatic = false, bool finished = false)
    {
        if (!Running) return;

        Running = false;
        nav.Stop();
        StopAttacking();
        EndMaterialize(null);
        RestoreWrathTargeting();
        ReleaseCore();

        fateId = 0;
        moveDest = null;
        zoneJob = null;
        CurrentFate = string.Empty;
        Status = $"已停止：{reason}";

        var gained = Services.ClientState.IsLoggedIn ? Gemstones - startGemstones : 0;
        log.Write($"===== 停止：{reason} 執行 {DateTime.Now - startedAt:hh\\:mm\\:ss} 寶石增加 {gained}");

        // 做完或手動停止：一般通知；程式自己因為出錯、卡住停下：錯誤通知
        if (automatic && !finished) Notify.Error("自動Fate", $"停止：{reason}");
        else Notify.Info("自動Fate", $"停止：{reason}");

        if (finished)
            pendingFinish = Config.AfterFinish;

        // 做完之後的動作只用一次：這次結束（不管是做完、手動停止還是出錯）就改回不做事
        if (Config.AfterFinish != FinishAction.None)
        {
            Config.AfterFinish = FinishAction.None;
            plugin.Save();
        }

        if (automatic && Config.TeleportOnStop)
            StartHomeTeleport();

        // 不用回水晶（或回不了）就直接排；要回水晶的等傳送完再排
        if (homeJob == null)
            ScheduleFinish();
    }

    /// <summary>達到目標停止後要做的事（登出、關閉遊戲），回水晶完才排。</summary>
    private FinishAction pendingFinish;

    private void ScheduleFinish()
    {
        if (pendingFinish == FinishAction.None) return;

        Core.Finish.Schedule(pendingFinish, "自動Fate");
        pendingFinish = FinishAction.None;
    }

    private void StartHomeTeleport()
    {
        var player = Services.Objects.LocalPlayer;
        if (player == null) return;

        var territory = Services.ClientState.TerritoryType;
        Aetherytes.Entry? target = null;

        // 有指定就傳指定的（大水晶或住宅）；清單裡找不到改回這張地圖最近的
        if (Config.StopAetheryte != 0)
        {
            target = Aetherytes.Unlocked().Concat(Aetherytes.Housing())
                .Where(a => a.Id == Config.StopAetheryte && a.Sub == Config.StopAetheryteSub)
                .Cast<Aetherytes.Entry?>()
                .FirstOrDefault();
            if (target == null)
                log.Write($"回水晶 指定的傳送點 #{Config.StopAetheryte}/{Config.StopAetheryteSub} 不在傳送清單裡，改傳這張地圖最近的");
        }

        target ??= Aetherytes.Nearest(territory, player.Position);
        if (target is not { } crystal)
        {
            log.Write($"回水晶 略過：{Aetherytes.ZoneName(territory)} 沒有解鎖的以太之光");
            return;
        }

        if (crystal.Territory == territory && crystal.Position is { } pos && Aetherytes.Distance2D(pos, player.Position) < 30f)
        {
            log.Write($"回水晶 略過：已經在 {crystal.Name} 旁邊");
            return;
        }

        homeJob = new TeleportJob { Aetheryte = crystal, Started = DateTime.Now };
        Status += $"（傳送回 {crystal.Name}）";
        log.Write($"回水晶 傳送到 {crystal.Name}");
    }

    // ── 死亡 ─────────────────────────────────────────────────

    /// <summary>等多久才按「返回」：給隊友或路人拉人的時間，有人拉就接受，不用跑回來。</summary>
    private static readonly TimeSpan ReviveWait = TimeSpan.FromSeconds(5);

    /// <summary>這段時間內死這麼多次就停：多半是打不過，一直死一直回來沒有意義。</summary>
    private static readonly TimeSpan DeathWindow = TimeSpan.FromMinutes(30);
    private const int MaxDeaths = 3;

    private readonly List<DateTime> deaths = new();
    private DateTime deadSince = DateTime.MinValue;
    private uint deathZone;
    private Vector3 deathPosition;

    /// <summary>
    /// 死掉了：停下手上的事，等幾秒（有人拉就接受復活），再按確認框的「是」返回。
    /// 死亡時出現的確認框只有「返回返回點」和「接受復活」兩種，按「是」都對。
    /// </summary>
    private void HandleDeath(IPlayerCharacter player, DateTime now)
    {
        if (deadSince == DateTime.MinValue)
        {
            deadSince = now;
            deathZone = Services.ClientState.TerritoryType;
            deathPosition = player.Position;

            // 記在哪場 FATE 死的（跨次執行保留），在「略過的 FATE」分頁看次數、決定要不要加黑名單
            if (fateId != 0)
            {
                Config.FateDeaths[fateId] = Config.FateDeaths.GetValueOrDefault(fateId) + 1;
                plugin.Save();
                log.Write($"死亡 FATE={CurrentFate} #{fateId} 累計死亡 {Config.FateDeaths[fateId]} 次");
            }

            nav.Stop();
            StopAttacking();
            moveDest = null;
            fateId = 0;
            CurrentFate = string.Empty;

            deaths.RemoveAll(t => now - t > DeathWindow);
            deaths.Add(now);
            log.Write($"死亡 區域={deathZone} 位置=({deathPosition.X:0},{deathPosition.Y:0},{deathPosition.Z:0}) " +
                      $"{DeathWindow.TotalMinutes:0} 分鐘內第 {deaths.Count} 次");
            log.Write($"死亡 前 {ThreatWindow.TotalSeconds:0} 秒：{ThreatSummary(now)}");

            if (deaths.Count >= MaxDeaths)
            {
                deadSince = DateTime.MinValue;
                Stop($"{DeathWindow.TotalMinutes:0} 分鐘內死亡 {deaths.Count} 次");
                return;
            }
        }

        Status = "死亡，等待復活";
        if (now - deadSince < ReviveWait) return;

        var yesno = Addons.Get("SelectYesno");
        if (yesno != null && Addons.ClickYes(yesno))
            log.Write($"死亡 按下確認：{Addons.YesnoPrompt(yesno).Replace('\n', ' ')}");
    }

    /// <summary>
    /// 復活後第一次：被拉起來就在原地，直接繼續；回到返回點（通常在別的地圖）就傳回死掉的區域，
    /// 用離死亡位置最近的水晶。要傳送就回 true（這一輪先不做別的）。
    /// </summary>
    private bool AfterRevive()
    {
        deadSince = DateTime.MinValue;
        var territory = Services.ClientState.TerritoryType;

        if (territory == deathZone)
        {
            log.Write("復活 在同一個區域，繼續");
            chocoboDue = true;
            chocoboNotBefore = DateTime.Now + ChocoboSettle;
            return false;
        }

        log.Write($"復活 在 {Aetherytes.ZoneName(territory)}，傳回 {Aetherytes.ZoneName(deathZone)}");
        GoToZone(deathZone, deathPosition);
        return true;
    }

    /// <summary>多區域或死亡後：傳送到 territory 的水晶；給了 near 就挑離那裡最近的。</summary>
    private void GoToZone(uint territory, Vector3? near = null)
    {
        if (Aetherytes.Nearest(territory, near) is not { } crystal)
        {
            Stop($"{Aetherytes.ZoneName(territory)} 沒有解鎖的以太之光，傳不過去", automatic: true);
            return;
        }

        nav.Stop();
        moveDest = null;
        fateId = 0;
        noFateSince = DateTime.MinValue;
        zoneJob = new TeleportJob { Aetheryte = crystal, Started = DateTime.Now };
        log.Write($"換區域 → {Aetherytes.ZoneName(territory)}（{crystal.Name}）");
    }

    /// <summary>
    /// 推進一次傳送：等詠唱、等戰鬥結束、下坐騎，然後送出傳送；看到過圖畫面又結束就算完成。
    /// 送出後 10 秒還沒開始過圖就再試，最多 3 次；整個超過 90 秒算失敗（多半是錢不夠或被打斷）。
    /// </summary>
    private JobResult RunTeleport(TeleportJob job, DateTime now)
    {
        var condition = Services.Condition;
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            job.SawLoading = true;
            return JobResult.Working;
        }

        var player = Services.Objects.LocalPlayer;
        if (player == null) return JobResult.Working;

        if (job.SawLoading) return JobResult.Done;
        if (now - job.Started > TimeSpan.FromSeconds(90) || job.Attempts > 3) return JobResult.Failed;
        if (player.IsDead) return JobResult.Failed;

        if (player.IsCasting || condition[ConditionFlag.InCombat] || now < job.NextTry) return JobResult.Working;

        // 騎著坐騎（連飛行中）也能直接傳送，不用先下來：以前一律先下坐騎，飛在水面、屋頂上空時下不來，傳送就一直送不出去。
        // 只有遊戲說現在不能傳送時，才先下坐騎試試
        if (condition[ConditionFlag.Mounted] && TeleportBlocked() != null)
        {
            DismountNow(now);
            return JobResult.Working;
        }

        nav.Stop();
        moveDest = null;

        // 送出前先問遊戲：有確認框開著（例如問要不要用傳送券）、或傳送技能現在不能用（5 = 傳送）時硬送，
        // 會跳「無法發動傳送，其他傳送正在進行」。先等，原因記一次
        // 狀態碼的意思沒全部實測過：擋太久（20 秒）就照送，免得判斷錯了連換區域都傳不出去
        var block = TeleportBlocked();
        if (block != null && now - job.Started < TimeSpan.FromSeconds(20))
        {
            if (job.LoggedBlock != block)
            {
                job.LoggedBlock = block;
                log.Write($"傳送到 {job.Aetheryte.Name} 先等：{block}");
            }

            job.NextTry = now + TimeSpan.FromSeconds(1);
            return JobResult.Working;
        }

        var sent = Aetherytes.Teleport(job.Aetheryte.Id, job.Aetheryte.Sub);
        log.Write($"送出傳送 {job.Aetheryte.Name}（第 {job.Attempts + 1} 次）結果={sent}");
        job.Attempts++;
        job.NextTry = now + TimeSpan.FromSeconds(10);
        return JobResult.Working;
    }

    /// <summary>傳送現在送不出去的原因；可以送就回傳 null。</summary>
    private static string? TeleportBlocked()
    {
        var yesno = Addons.Get("SelectYesno");
        if (yesno != null) return $"確認框還開著：{Addons.YesnoPrompt(yesno)}";

        var status = ActionManager.Instance()->GetActionStatus(ActionType.Action, TeleportAction);
        return status == 0 ? null : $"傳送技能不能用（狀態碼 {status}）";
    }

    private const uint TeleportAction = 5;

    public void Dispose()
    {
        Stop("插件卸載");
        ReleaseCore();
        Core.Repair.Finished -= OnRepairFinished;

        if (subscribed)
        {
            Services.Framework.Update -= OnUpdate;
            subscribed = false;
        }

    }

    private void OnUpdate(IFramework framework)
    {
        if (!Running && homeJob == null) return;

        var now = DateTime.Now;
        if (now < nextTick) return;
        nextTick = now + TickInterval;

        if (!Running)
        {
            RunHome(now);
            return;
        }

        try
        {
            Tick(now);
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "AutoFate tick failed");
            log.Write($"錯誤 {e}");
            Stop($"發生錯誤：{e.Message}", automatic: true);
        }
    }

    /// <summary>停止後回水晶。登出就作罷。</summary>
    private void RunHome(DateTime now)
    {
        var job = homeJob!;
        if (!Services.ClientState.IsLoggedIn)
        {
            homeJob = null;
            return;
        }

        try
        {
            switch (RunTeleport(job, now))
            {
                case JobResult.Done:
                    log.Write($"回水晶 完成：{job.Aetheryte.Name}");
                    homeJob = null;
                    break;

                case JobResult.Failed:
                    log.Write($"回水晶 失敗：{job.Aetheryte.Name}（試了 {job.Attempts} 次）");
                    Notify.Error("自動Fate", $"傳送回 {job.Aetheryte.Name} 失敗。");
                    homeJob = null;
                    break;
            }
        }
        catch (Exception e)
        {
            log.Write($"錯誤 回水晶 {e}");
            homeJob = null;
        }

        if (homeJob == null)
            ScheduleFinish();
    }

    /// <summary>
    /// 陸行鳥真的在身邊（不是只看剩餘時間：死掉後陸行鳥會被送走、但剩餘時間還在；騎坐騎時剩餘時間也暫停）。
    /// </summary>
    private static bool ChocoboOut(IPlayerCharacter player) =>
        Services.Objects.Any(o => o is IBattleNpc { BattleNpcKind: BattleNpcSubKind.Chocobo } npc && npc.OwnerId == player.EntityId);

    /// <summary>陸行鳥戰術：上次送出指令的時間、連續沒改成功的次數（3 次就這次執行不再試）。</summary>
    private DateTime chocoboStanceAt = DateTime.MinValue;
    private int chocoboStanceTries;

    private const int ChocoboStanceMaxTries = 3;
    private static readonly TimeSpan ChocoboStanceRetry = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 陸行鳥在身邊時，看它現在的戰術（CompanionInfo.ActiveCommand，跟 BuddyAction 表同編號：4 自由、5 防護、6 進攻、7 治療），
    /// 跟設定不一樣就用遊戲指令 /cac「治療戰術」改（跟自己打一樣；戰術名稱從遊戲文字表讀）。
    /// 重開遊戲後陸行鳥還在、但戰術回到自由，也會在這裡改回來。只在非戰鬥、沒騎坐騎時檢查。
    /// </summary>
    private void EnsureChocoboStance(IPlayerCharacter player, DateTime now)
    {
        if (Config.ChocoboStance == 0 || !ChocoboOut(player)) return;

        var current = (uint)UIState.Instance()->Buddy.CompanionInfo.ActiveCommand;
        if (current == Config.ChocoboStance)
        {
            chocoboStanceTries = 0;
            return;
        }

        if (now < chocoboStanceAt || chocoboStanceTries > ChocoboStanceMaxTries) return;

        var sheet = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.BuddyAction>();
        var was = sheet.GetRowOrDefault(current)?.Name.ExtractText() is { Length: > 0 } n ? n : $"#{current}";

        // 最後一次送出後等過了還是沒變：記一次，這次執行不再改
        if (chocoboStanceTries == ChocoboStanceMaxTries)
        {
            chocoboStanceTries++;
            log.Write($"陸行鳥 戰術 試了 {ChocoboStanceMaxTries} 次還是 {was}，這次執行不再改");
            return;
        }

        var name = sheet.GetRowOrDefault(Config.ChocoboStance)?.Name.ExtractText() ?? string.Empty;
        if (name.Length == 0) return;

        plugin.Executor.Enqueue($"/cac \"{name}\"");
        chocoboStanceAt = now + ChocoboStanceRetry;
        chocoboStanceTries++;
        log.Write($"陸行鳥 戰術：{was} → {name}（第 {chocoboStanceTries} 次）");
    }

    /// <summary>最近打過我的怪：名稱 → （同時最多幾隻、最後看到的時間）。死的時候寫進 log，知道是被什麼打死的。</summary>
    private readonly Dictionary<string, (int Max, DateTime Seen)> threats = new();

    /// <summary>最近每秒一筆的 HP%（死的時候看掉血速度）。</summary>
    private readonly List<(DateTime At, int Hp)> hpTrail = new();

    private DateTime nextThreatSample;

    private static readonly TimeSpan ThreatWindow = TimeSpan.FromSeconds(20);

    /// <summary>每秒記一次：哪些怪正在打我、HP 剩多少。</summary>
    private void TrackThreats(IPlayerCharacter player, DateTime now)
    {
        if (now < nextThreatSample) return;
        nextThreatSample = now + TimeSpan.FromSeconds(1);

        foreach (var group in Attackers(player).GroupBy(e => e.Name.TextValue))
        {
            var count = group.Count();
            threats[group.Key] = (threats.TryGetValue(group.Key, out var old) && now - old.Seen < ThreatWindow ? Math.Max(old.Max, count) : count, now);
        }

        hpTrail.Add((now, player.MaxHp == 0 ? 0 : (int)(player.CurrentHp * 100 / player.MaxHp)));
        hpTrail.RemoveAll(h => now - h.At > ThreatWindow);
    }

    /// <summary>死前一段時間打我的怪（最多同時幾隻）、HP 變化。</summary>
    private string ThreatSummary(DateTime now)
    {
        var recent = threats.Where(t => now - t.Value.Seen <= ThreatWindow)
            .OrderByDescending(t => t.Value.Max)
            .Select(t => $"{t.Key}×{t.Value.Max}")
            .ToList();

        var hp = hpTrail.Count == 0 ? "無" : string.Join("→", hpTrail.Where((_, i) => i % 3 == 0 || i == hpTrail.Count - 1).Select(h => $"{h.Hp}%"));
        threats.Clear();
        hpTrail.Clear();

        return $"打我的怪={(recent.Count == 0 ? "沒記到" : string.Join("、", recent))} HP={hp} 陸行鳥剩餘={UIState.Instance()->Buddy.CompanionInfo.TimeLeft:0}s";
    }

    private void Tick(DateTime now)
    {
        if (!Services.ClientState.IsLoggedIn)
        {
            Stop("已登出");
            return;
        }

        var player = Services.Objects.LocalPlayer;
        if (player == null) return;

        var condition = Services.Condition;
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]
            || condition[ConditionFlag.OccupiedInCutSceneEvent] || condition[ConditionFlag.WatchingCutscene]
            || condition[ConditionFlag.BoundByDuty])
        {
            if (zoneJob != null && (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]))
                zoneJob.SawLoading = true;

            Status = "等待（過圖、過場或副本中）";
            return;
        }

        if (player.IsDead)
        {
            HandleDeath(player, now);
            return;
        }

        TrackThreats(player, now);

        // 騎坐騎時陸行鳥會暫時消失、下坐騎後才回來：剛下坐騎的幾秒內不要因為「不在身邊」就餵野菜
        if (condition[ConditionFlag.Mounted] && now + ChocoboSettle > chocoboNotBefore)
            chocoboNotBefore = now + ChocoboSettle;

        if (deadSince != DateTime.MinValue && AfterRevive())
            return;

        if (now >= nextSnapshot)
        {
            Snapshot(player);
            nextSnapshot = now + SnapshotInterval;
        }

        RefuseTrade(now);
        ConfirmFateStart(now);

        // 修理由核心能力負責，修理中這裡什麼都不做
        if (Core.Repair.Busy)
        {
            Status = Core.Repair.Status;
            return;
        }

        // 精製魔晶石中：做完、被打或出錯才回來找 FATE
        if (materializing)
        {
            AdvanceMaterialize(now);
            return;
        }

        var inCombat = condition[ConditionFlag.InCombat];

        if (FinishReason(now) is { } reason)
        {
            if (!inCombat)
            {
                Stop(reason, automatic: true, finished: true);
                return;
            }

            // 該停了但還在戰鬥：打完手上的怪再停，不要站著被打
            var attackers = Attackers(player);
            if (attackers.Count > 0)
            {
                Status = $"{reason}，打完身上的怪就停";
                Fight(player, Pick(player, attackers), now);
                return;
            }

            // 還在戰鬥狀態但沒有怪在打我：等戰鬥狀態解除，不要再去接新的 FATE
            Status = $"{reason}，等戰鬥結束就停";
            return;
        }

        if (zoneJob != null)
        {
            // 傳送要先脫離戰鬥：身上有怪就先打掉
            var attackers = inCombat ? Attackers(player) : new List<IBattleNpc>();
            if (attackers.Count > 0)
            {
                Status = "換區域前先處理身上的怪";
                Fight(player, Pick(player, attackers), now);
                return;
            }

            switch (RunTeleport(zoneJob, now))
            {
                case JobResult.Working:
                    Status = $"傳送到 {zoneJob.Aetheryte.Name}";
                    return;

                case JobResult.Done:
                    log.Write(zoneJob.Shortcut
                        ? $"抄近路 完成：{zoneJob.Aetheryte.Name}"
                        : $"換區域 完成：{Aetherytes.ZoneName(Services.ClientState.TerritoryType)}");
                    chocoboDue = true;
                    chocoboNotBefore = DateTime.Now + ChocoboSettle;
                    zoneJob = null;
                    noFateSince = DateTime.MinValue;
                    return;

                case JobResult.Failed:
                    var name = zoneJob.Aetheryte.Name;
                    if (zoneJob.Shortcut)
                    {
                        log.Write($"抄近路 傳送到 {name} 失敗，改用移動的");
                        zoneJob = null;
                        return;
                    }

                    zoneJob = null;
                    Stop($"傳送到 {name} 失敗", automatic: true);
                    return;
            }
        }

        // IsReady 偶爾會閃一下 false（一晚 log 裡出現幾十次，每次一兩秒），連續 3 秒都沒準備好才停下來等
        if (nav.Ready)
        {
            navNotReadySince = DateTime.MinValue;
        }
        else
        {
            if (navNotReadySince == DateTime.MinValue) navNotReadySince = now;
            if (now - navNotReadySince >= TimeSpan.FromSeconds(3))
            {
                Status = "等待 vnavmesh 導航網格";
                return;
            }
        }

        if (CheckStuck(player, now)) return;

        // 優先目標（迷失少女這類）一出現就先打，不管在不在 FATE 範圍裡
        var priority = PriorityTarget(player);
        if (priority != null)
        {
            Status = $"優先目標：{priority.Name}";
            Fight(player, priority, now);
            return;
        }

        var fate = TargetFate(player);

        // 被野外菁英怪（名牌上有怪物等級標記、不屬於任何 FATE）纏上：不打，往反方向跑／飛遠甩開，
        // 不然它會跟到 FATE 裡，打很久甚至被打死。跑太久甩不掉才放棄、照常打。
        if (inCombat && Config.FleeElites && FleeElites(player, now)) return;

        // 路上被怪纏上：還在地上走就先打掉（不然上不了坐騎）；已經騎著坐騎在趕路就不理它，
        // 戰鬥中也能繼續騎、繼續飛，到了 FATE 下坐騎後怪跟過來再打
        if (inCombat)
        {
            var attackers = Attackers(player);
            if (attackers.Count > 0 && (fate == null || fate.State != FateState.Running || !InFate(player, fate)))
            {
                if (condition[ConditionFlag.Mounted] && moveDest != null)
                {
                    if (!ignoringAttackers)
                    {
                        ignoringAttackers = true;
                        log.Write($"趕路中被 {attackers.Count} 隻怪打，騎著坐騎不停下來（{string.Join("、", attackers.Select(a => a.Name.TextValue))}）");
                    }
                }
                else
                {
                    ignoringAttackers = false;
                    Status = "處理身上的怪";
                    Fight(player, Pick(player, attackers), now);
                    return;
                }
            }
            else
            {
                ignoringAttackers = false;
            }
        }
        else
        {
            ignoringAttackers = false;
        }

        if (fate == null)
        {
            CurrentFate = string.Empty;

            if (!inCombat && Maintenance(player, now)) return;

            if (moveDest != null)
            {
                nav.Stop();
                moveDest = null;
            }

            if (NextZone() is { } next && !inCombat)
            {
                if (noFateSince == DateTime.MinValue) noFateSince = now;

                var left = TimeSpan.FromSeconds(Config.ZoneWaitSeconds) - (now - noFateSince);
                if (left <= TimeSpan.Zero)
                {
                    GoToZone(next);
                    return;
                }

                Status = $"等待臨危受命出現（{left.TotalSeconds:0} 秒後換到 {Aetherytes.ZoneName(next)}）";
                return;
            }

            Status = "等待臨危受命出現";
            return;
        }

        noFateSince = DateTime.MinValue;
        CurrentFate = fate.Name.TextValue;

        // 出發前（還沒上坐騎時）先把修理、陸行鳥處理掉。要找 NPC 開始的 FATE 也一樣
        if (!inCombat && !Services.Condition[ConditionFlag.Mounted] && moveDest == null
            && !InFate(player, fate) && Maintenance(player, now))
            return;

        if (fate.State == FateState.Preparation)
        {
            StartAtNpc(player, fate, now);
            return;
        }

        if (InFate(player, fate))
        {
            DoFate(player, fate, now);
            return;
        }

        if (Shortcut(player, fate, fate.Position)) return;

        // 直接飛往 FATE 中心的話，進圈時停在哪就在哪下坐騎，底下常是水面、屋頂、陡坡下不來；
        // 先找好圈內能降落的地面點，飛到那裡再下
        if (FateLanding(fate, now) is { } landing)
            Travel(player, landing, 1.5f, now, "前往 FATE");
        else
            Travel(player, fate.Position, Math.Max(5f, fate.Radius * 0.5f), now, "前往 FATE");
    }

    /// <summary>FATE 圈內（中心 60% 半徑內）最靠近中心、能下坐騎的地面點；每個 FATE 找一次，找不到過 5 秒再找。</summary>
    private Vector3? FateLanding(IFate fate, DateTime now)
    {
        // 找好的點試過降不了：重找（跳過降不了的點）
        if (landingFate == fate.FateId && landingPoint is { } cached && FailedLanding(cached))
            landingPoint = null;

        if (landingFate == fate.FateId && (landingPoint != null || now < landingRetryAt))
            return landingPoint;

        var first = landingFate != fate.FateId;
        landingFate = fate.FateId;
        landingPoint = null;
        landingRetryAt = now + TimeSpan.FromSeconds(5);
        if (!nav.Ready) return null;

        // 由近到遠找，找到的點還要在圈內夠裡面，不然一降落就出圈
        var inner = fate.Radius * 0.6f;
        // 先找四周也能降落的點（中心、再往外兩圈 8 個方向），都沒有才接受從中心找、可能在可降落區邊上的點
        var candidates = new List<Vector3?>();
        foreach (var ring in new[] { 0f, inner * 0.33f, inner * 0.66f })
        {
            var directions = ring == 0f ? 1 : 8;
            for (var i = 0; i < directions; i++)
            {
                var angle = i * MathF.PI / 4f;
                candidates.Add(RoomyLanding(fate.Position + new Vector3(MathF.Cos(angle) * ring, 0, MathF.Sin(angle) * ring), 3f));
            }
        }

        foreach (var extent in new[] { 5f, inner * 0.5f, inner })
            candidates.Add(nav.LandingPoint(fate.Position, Math.Max(3f, extent)));

        foreach (var candidate in candidates)
        {
            if (candidate is { } p && Distance2D(p, fate.Position) <= inner && !FailedLanding(p))
            {
                landingPoint = p;
                log.Write($"FATE #{fate.FateId} 降落點=({p.X:0},{p.Y:0},{p.Z:0}) 離中心 {Distance2D(p, fate.Position):0} 米");
                return p;
            }
        }

        if (first) log.Write($"FATE #{fate.FateId} 圈內找不到能降落的點，直接飛往中心");
        return null;
    }

    private ushort landingFate;
    private Vector3? landingPoint;
    private DateTime landingRetryAt;

    /// <summary>
    /// 同地圖抄近路：有已解鎖的以太之光離目的地比現在近很多（省下 ShortcutSaving 以上），先傳過去再走。
    /// 每個 FATE 只傳一次；戰鬥中、已經在 FATE 附近就不傳。回傳 true = 開始傳送了。
    /// </summary>
    private bool Shortcut(IPlayerCharacter player, IFate fate, Vector3 dest)
    {
        if (!Config.ShortcutTeleport || shortcutFate == fate.FateId) return false;
        if (Services.Condition[ConditionFlag.InCombat]) return false;

        var territory = Services.ClientState.TerritoryType;
        var nearest = Aetherytes.Nearest(territory, dest);
        if (nearest is not { Position: { } crystalPos } crystal)
        {
            ShortcutSkipped(fate, nearest == null
                ? "這張地圖沒有已解鎖的以太之光"
                : $"讀不到 {nearest.Value.Name} 的座標（這張地圖已解鎖 {Aetherytes.Unlocked().Count(a => a.Territory == territory)} 個）");
            return false;
        }

        var here = Aetherytes.Distance2D(player.Position, dest);
        var there = Aetherytes.Distance2D(crystalPos, dest);
        if (here - there < Config.ShortcutSaving)
        {
            ShortcutSkipped(fate, $"最近的 {crystal.Name} 離目的地 {there:0} 米，現在 {here:0} 米，只省 {here - there:0} 米（要 {Config.ShortcutSaving:0}）");
            return false;
        }

        // 已經在那個水晶旁邊（剛傳過來）就不用再傳
        if (Aetherytes.Distance2D(player.Position, crystalPos) < 50f) return false;

        shortcutFate = fate.FateId;
        nav.Stop();
        moveDest = null;
        zoneJob = new TeleportJob { Aetheryte = crystal, Started = DateTime.Now, Shortcut = true };
        log.Write($"抄近路 #{fate.FateId} {fate.Name.TextValue}：傳送到 {crystal.Name}（離目的地 {there:0} 米，現在 {here:0} 米）");
        return true;
    }

    /// <summary>沒抄近路的原因，每個 FATE 只記一次（Shortcut 每幀都會跑）。</summary>
    private void ShortcutSkipped(IFate fate, string reason)
    {
        if (shortcutLoggedFate == fate.FateId) return;
        shortcutLoggedFate = fate.FateId;
        log.Write($"不抄近路 #{fate.FateId}：{reason}");
    }

    private ushort shortcutLoggedFate;

    // ── 挑 FATE ────────────────────────────────────────────────

    /// <summary>沿用正在打的 FATE；它結束了才挑下一個。</summary>
    private IFate? TargetFate(IPlayerCharacter player)
    {
        if (fateId != 0)
        {
            var current = Services.Fates.FirstOrDefault(f => f.FateId == fateId);
            if (current != null && Active(current))
            {
                // 還在路上（沒進 FATE）時冒出有加成的 FATE：改去那個。已經在打的就打完，不半途丟下
                if (Config.PreferBonus && !current.HasBonus && !InFate(player, current) && !turningIn
                    && Services.Fates.Where(Eligible).Where(f => f.HasBonus).OrderBy(f => Score(player, f)).FirstOrDefault() is { } bonus)
                {
                    log.Write($"改去有加成的 FATE #{bonus.FateId} {bonus.Name.TextValue}（原本要去 #{current.FateId} {current.Name.TextValue}）");
                    fateId = bonus.FateId;
                    nav.Stop();
                    moveDest = null;
                    return bonus;
                }

                return current;
            }

            log.Write(current == null
                ? $"FATE 結束 #{fateId}（已消失） 寶石={Gemstones}"
                : $"FATE 結束 #{fateId} 狀態={current.State} 進度={current.Progress}% 寶石={Gemstones}");

            fateId = 0;
            moveDest = null;
            turningIn = false;
            chocoboDue = true;
            killWatch = 0;
            dropWaitUntil = DateTime.MinValue;
            turnInAwaitDrop = DateTime.MinValue;
            warnedTurnInNpc = false;
        }

        var next = Services.Fates
            .Where(Eligible)
            .OrderBy(f => Score(player, f))
            .FirstOrDefault();

        if (next != null)
        {
            fateId = next.FateId;
            var kind = next.GameData.ValueNullable is { } data ? FateKinds.Label(FateKinds.Of(data)) : "?";
            log.Write($"選定 FATE #{next.FateId} {next.Name.TextValue} 類型={kind} 狀態={next.State} " +
                      $"進度={next.Progress}% 剩餘={Remaining(next)} 距離={Vector3.Distance(player.Position, next.Position):0} " +
                      $"加成={next.HasBonus} 候選={Services.Fates.Count(Eligible)}/{Services.Fates.Length} 有加成的候選={Services.Fates.Where(Eligible).Count(f => f.HasBonus)} 全區有加成={Services.Fates.Count(f => f.HasBonus)}");
        }

        return next;
    }

    private bool Active(IFate fate) =>
        fate.State == FateState.Running || (fate.State == FateState.Preparation && Config.StartNpcFates);

    private bool Eligible(IFate fate)
    {
        if (!Active(fate)) return false;
        if (Config.Blacklist.Contains(fate.FateId)) return false;
        if (skippedFates.TryGetValue(fate.FateId, out var until) && DateTime.Now < until) return false;
        if (fate.GameData.ValueNullable is not { } data) return false;
        if (!Config.Kinds.Contains(FateKinds.Of(data))) return false;

        // 繳交型要靠核心的對話推進與交道具，使用者把其中一個關掉就不去，免得收了一堆道具交不出去
        if (data.EventItem.RowId != 0
            && (Core.Handin.Mode == CapabilityMode.Off || Core.Talk.Mode == CapabilityMode.Off)) return false;

        if (fate.State == FateState.Running)
        {
            if (fate.Progress > Config.MaxProgress) return false;
            if (fate.Duration > 0 && fate.TimeRemaining < Config.MinSecondsLeft) return false;
        }

        return true;
    }

    private float Score(IPlayerCharacter player, IFate fate)
    {
        // 有加成的一律排在前面（再遠也先去），同樣有／沒有加成的才比距離。
        // 原本只是「算近 300 米」，地圖大一點就輸給近的普通 FATE
        var score = Vector3.Distance(player.Position, fate.Position);
        if (Config.PreferBonus && fate.HasBonus)
            score -= 100000f;

        return score;
    }

    private static bool InFate(IPlayerCharacter player, IFate fate)
    {
        var manager = FateManager.Instance();
        if (manager->CurrentFate != null && manager->CurrentFate->FateId == fate.FateId)
            return true;

        return Distance2D(player.Position, fate.Position) < fate.Radius * 0.9f;
    }

    // ── 移動 ───────────────────────────────────────────────────

    /// <summary>騎坐騎（夠遠的話）往 dest 走，到 range 以內 vnavmesh 會自己停。</summary>
    /// <summary>開始原地等戰鬥狀態解除（好上坐騎）的時間；MinValue = 沒在等。</summary>
    private DateTime mountWaitSince = DateTime.MinValue;

    private static readonly TimeSpan MountCombatWait = TimeSpan.FromSeconds(8);

    private void Travel(IPlayerCharacter player, Vector3 dest, float range, DateTime now, string label)
    {
        var distance = Vector3.Distance(player.Position, dest);
        var mounted = Services.Condition[ConditionFlag.Mounted];

        if (player.IsCasting)
        {
            Status = "等待詠唱結束";
            return;
        }

        if (!mounted && Config.UseMount && distance > Config.MountDistance && now >= nextMount
            && (now >= noRemountUntil || distance > Config.MountDistance * 3)
            && !Services.Condition[ConditionFlag.InCombat] && CanUse(ActionType.GeneralAction, MountRoulette))
        {
            nav.Stop();
            moveDest = null;
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, MountRoulette);
            nextMount = now + TimeSpan.FromSeconds(3);
            Status = "上坐騎";
            return;
        }

        // 該上坐騎、但剛打完還在戰鬥狀態（沒有怪在打我，只是旗標還沒消）：原地等它消掉再上坐騎飛，
        // 不然會先用走的出發，等戰鬥狀態消了才上坐騎改飛。最多等 MountCombatWait，太久就照走
        if (!mounted && Config.UseMount && distance > Config.MountDistance && Services.Condition[ConditionFlag.InCombat]
            && Attackers(player).Count == 0)
        {
            if (mountWaitSince == DateTime.MinValue) mountWaitSince = now;
            if (now - mountWaitSince < MountCombatWait)
            {
                if (nav.Busy) nav.Stop();
                moveDest = null;
                Status = "等戰鬥狀態解除再上坐騎";
                return;
            }
        }
        else
        {
            mountWaitSince = DateTime.MinValue;
        }

        if (TravelStalled(player, distance <= range + 2f, now)) return;

        var fly = mounted && Control.CanFly && now >= groundOnlyUntil;

        // 剛上坐騎、走的還是地面路線：改走飛行路線；終點換了也要重走
        var restart = moveDest is { } current && (fly != pathFly || Vector3.Distance(current, dest) > 3f);

        if ((!nav.Busy || restart) && now >= nextPath)
        {
            if (restart) nav.Stop();

            if (nav.MoveCloseTo(dest, fly, range))
            {
                moveDest = dest;
                pathFly = fly;
            }
            else if (now >= nextPathFailLog)
            {
                log.Write($"尋路失敗 位置=({player.Position.X:0},{player.Position.Y:0},{player.Position.Z:0}) 終點={dest} 飛行={fly}");
                nextPathFailLog = now + TimeSpan.FromSeconds(30);
            }

            nextPath = now + TimeSpan.FromSeconds(2);
        }

        Status = $"{label}（{distance:0} 米）";
    }

    /// <summary>
    /// 趕路時位置一直沒變，不管 vnavmesh 說自己在不在走（尋路失敗時它根本不會開始走，
    /// CheckStuck 就看不到）。依序：跳一下重找路 → 改走地面路線 → 這個 FATE 略過 15 分鐘。
    /// 回傳 true = 這一輪處理了，先不做別的。
    /// </summary>
    private bool TravelStalled(IPlayerCharacter player, bool arrived, DateTime now)
    {
        // 中間去打怪、對話之類沒在趕路，回來時重新計時
        var resumed = now - lastTravelTick > TimeSpan.FromSeconds(1);
        lastTravelTick = now;

        if (resumed || arrived || Vector3.Distance(player.Position, travelAnchor) > 2f)
        {
            if (!resumed) travelFails = 0;
            travelAnchor = player.Position;
            travelSince = now;
            return false;
        }

        // vnavmesh 還在算路徑（遠的飛行路線常要算十幾秒）：沒在動是正常的，不算沒進展。
        // 以前會在這時停掉重算、第二次就改走地面，所以遠的地點常常變成用走的。算太久（PathfindTimeout）才當作沒進展
        if (nav.Busy && !nav.Moving)
        {
            if (pathfindSince == DateTime.MinValue) pathfindSince = now;
            if (now - pathfindSince < PathfindTimeout)
            {
                travelSince = now;
                return false;
            }
        }
        else
        {
            pathfindSince = DateTime.MinValue;
        }

        if (now - travelSince < TravelStallAfter) return false;

        if (pathfindSince != DateTime.MinValue)
            log.Write($"vnavmesh 算路徑超過 {PathfindTimeout.TotalSeconds:0} 秒還沒算完");
        pathfindSince = DateTime.MinValue;

        travelFails++;
        travelSince = now;
        log.Write($"趕路沒進展 第 {travelFails} 次 位置=({player.Position.X:0},{player.Position.Y:0},{player.Position.Z:0}) " +
                  $"終點={moveDest} 尋路={nav.Busy} 坐騎={Services.Condition[ConditionFlag.Mounted]} 狀態={Status}");

        nav.Stop();
        moveDest = null;
        nextPath = now + TimeSpan.FromSeconds(1);

        switch (travelFails)
        {
            case 1:
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, Jump);
                Status = "趕路沒進展，跳一下重新尋路";
                break;
            case 2:
                groundOnlyUntil = now + TimeSpan.FromMinutes(1);
                Status = "趕路沒進展，改走地面路線";
                break;
            default:
                travelFails = 0;
                if (fateId != 0)
                {
                    skippedFates[fateId] = now + SkipFateFor;
                    log.Write($"到不了，略過 {SkipFateFor.TotalMinutes:0} 分鐘：#{fateId}");
                    fateId = 0;
                }

                if (Vector3.Distance(player.Position, unreachableAnchor) < 3f)
                    unreachableHere++;
                else
                {
                    unreachableAnchor = player.Position;
                    unreachableHere = 1;
                }

                if (unreachableHere >= 2)
                {
                    unreachableHere = 0;
                    EscapeByTeleport(player, now, "連續幾個 FATE 都從同一個位置到不了（可能站在導航網格外）");
                    return true;
                }

                Status = "到不了，換別的 FATE";
                break;
        }

        return true;
    }

    /// <summary>
    /// vnavmesh 在走、角色卻幾乎沒動：停下來跳一下再重新尋路。連續卡住太多次就放棄。
    /// 回傳 true = 這一輪處理了卡住，先不做別的。
    /// </summary>
    private bool CheckStuck(IPlayerCharacter player, DateTime now)
    {
        if (!nav.Moving || player.IsCasting)
        {
            stuckSince = DateTime.MinValue;
            return false;
        }

        if (stuckSince == DateTime.MinValue || Vector3.Distance(player.Position, stuckAnchor) > 1.5f)
        {
            if (stuckSince != DateTime.MinValue) stuckCount = 0;

            stuckAnchor = player.Position;
            stuckSince = now;
            return false;
        }

        if (now - stuckSince < StuckWindow) return false;

        stuckCount++;
        stuckSince = DateTime.MinValue;
        log.Write($"卡住 第 {stuckCount} 次 位置=({player.Position.X:0},{player.Position.Y:0},{player.Position.Z:0}) 終點={moveDest} 狀態={Status}");

        if (stuckCount >= MaxStuck)
        {
            stuckCount = 0;
            EscapeByTeleport(player, now, "移動一直卡住");
            return true;
        }

        nav.Stop();
        moveDest = null;
        ActionManager.Instance()->UseAction(ActionType.GeneralAction, Jump);
        nextPath = now + TimeSpan.FromSeconds(1);
        Status = $"卡住了，重新尋路（{stuckCount}/{MaxStuck}）";
        return true;
    }

    // ── 精製魔晶石 ─────────────────────────────────────────────

    private readonly MaterializeRunner materializer = new();
    private bool materializing;
    private DateTime materializeStartedAt;
    private DateTime materializeRetryAt;

    /// <summary>用一般動作開精製魔晶石視窗，之後交給 MaterializeRunner 一件一件做。</summary>
    private void StartMaterialize(DateTime now)
    {
        nav.Stop();
        moveDest = null;
        StopAttacking();

        materializer.Reset();
        materializing = true;
        materializeStartedAt = now;
        log.Write($"精製魔晶石 開始 精煉度滿={MaterializeRunner.FullItems()} 件");
        Status = "精製魔晶石：開視窗";

        if (Addons.Get(MaterializeRunner.Addon) == null)
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, MaterializeRunner.OpenAction);
    }

    private void AdvanceMaterialize(DateTime now)
    {
        // 被怪打：先關視窗去打，等一下再回來精製
        if (Services.Condition[ConditionFlag.InCombat])
        {
            EndMaterialize("被怪打，先打怪", TimeSpan.FromSeconds(30));
            return;
        }

        var addon = Addons.Get(MaterializeRunner.Addon);
        if (addon == null)
        {
            if (now - materializeStartedAt > TimeSpan.FromSeconds(5))
                EndMaterialize("精製魔晶石視窗沒開起來", TimeSpan.FromMinutes(10));
            else
                Status = "精製魔晶石：開視窗";
            return;
        }

        Status = $"精製魔晶石（已完成 {materializer.Done} 件）";
        if (materializer.Step(addon, now, log) is not { } result) return;

        // 全部做完就不用等；中途停下（背包滿、一直沒成功、有的不在清單裡）10 分鐘內不再試
        var finished = MaterializeRunner.FullItems() == 0;
        EndMaterialize(result, finished ? TimeSpan.Zero : TimeSpan.FromMinutes(10));
    }

    /// <summary>結束精製、關掉視窗。reason null = 自動 FATE 停止時順便收掉，不寫 log。</summary>
    private void EndMaterialize(string? reason, TimeSpan retryAfter = default)
    {
        if (!materializing) return;

        materializing = false;
        materializeRetryAt = DateTime.Now + retryAfter;

        var addon = Addons.Get(MaterializeRunner.Addon);
        if (addon != null) Addons.Fire(addon, -1);

        if (reason != null)
        {
            log.Write($"精製魔晶石 結束：{reason}（完成 {materializer.Done} 件）");
            Status = $"精製魔晶石：{reason}";
        }
    }

    /// <summary>
    /// 傳送到這張地圖最近的水晶脫困，卡住時在打的 FATE 先略過一陣子，傳完照常繼續。
    /// 傳送脫困後沒多久又卡住：真的走不了，停止。
    /// </summary>
    private void EscapeByTeleport(IPlayerCharacter player, DateTime now, string reason)
    {
        nav.Stop();
        moveDest = null;

        if (now - stuckTeleportAt < StuckTeleportWindow)
        {
            Stop($"{reason}（傳送脫困後又卡住）", automatic: true);
            return;
        }

        if (fateId != 0)
        {
            skippedFates[fateId] = now + SkipFateFor;
            log.Write($"卡住的 FATE 略過 {SkipFateFor.TotalMinutes:0} 分鐘：#{fateId}");
            fateId = 0;
        }

        stuckTeleportAt = now;
        StopAttacking();
        log.Write($"{reason}，傳送到最近的水晶脫困後繼續");
        GoToZone(Services.ClientState.TerritoryType, player.Position);
    }

    // ── 找 NPC 開始的 FATE ─────────────────────────────────────

    /// <summary>對話前要站穩多久（剛下坐騎還在滑時對話會沒反應）。</summary>
    private static readonly TimeSpan TalkSettle = TimeSpan.FromMilliseconds(300);

    private Vector3 talkSettlePos;
    private DateTime talkSettleSince = DateTime.MinValue;

    private void StartAtNpc(IPlayerCharacter player, IFate fate, DateTime now)
    {
        var npc = FateNpc(fate.FateId);
        if (npc == null)
        {
            // NPC 在物件表的範圍外看不到，先往 FATE 那邊走（遠的話先傳送抄近路）
            if (Shortcut(player, fate, fate.Position)) return;
            Travel(player, fate.Position, 5f, now, "前往要對話開始的 FATE");
            return;
        }

        var distance = Vector3.Distance(player.Position, npc.Position);
        if (distance > 5f)
        {
            Travel(player, npc.Position, 3f, now, $"前往 {npc.Name}");
            return;
        }

        if (moveDest != null)
        {
            nav.Stop();
            moveDest = null;
        }

        if (Services.Condition[ConditionFlag.Mounted])
        {
            DismountNow(now);
            Status = "下坐騎";
            talkSettleSince = DateTime.MinValue;
            lastMountedAt = now;
            return;
        }

        Status = $"和 {npc.Name} 對話開始 FATE";

        // 剛下坐騎時角色還在動作中，位置雖然不動了，對話還是沒反應（實測下來 1 秒內對話被吃掉，3 秒後重試才成功）
        if (now - lastMountedAt < DismountTalkDelay) return;

        // 剛下坐騎、還在滑行時對話會沒反應，要等重試（3 秒）反而更慢：
        // 位置連續 TalkSettle 沒動、也不在跳躍／下坐騎中，才對話
        if (Vector3.Distance(player.Position, talkSettlePos) > 0.05f || Services.Condition[ConditionFlag.Jumping]
                                                                    || Services.Condition[ConditionFlag.MountOrOrnamentTransition])
        {
            talkSettlePos = player.Position;
            talkSettleSince = now;
            return;
        }

        if (talkSettleSince == DateTime.MinValue) talkSettleSince = now;
        if (now - talkSettleSince < TalkSettle) return;

        if (now < nextInteract || Services.Condition[ConditionFlag.OccupiedInQuestEvent]
                               || Services.Condition[ConditionFlag.OccupiedInEvent])
            return;

        Services.Targets.Target = npc;
        TargetSystem.Instance()->InteractWithObject((CsGameObject*)npc.Address);
        log.Write($"對話 {npc.Name} 開始 FATE #{fate.FateId}（BaseId={((CsGameObject*)npc.Address)->BaseId}）" +
                  (lastMountedAt != DateTime.MinValue ? $" 下坐騎後 {(now - lastMountedAt).TotalSeconds:0.0} 秒" : ""));

        // 記住是誰開始的：繳交型 FATE 收道具的通常就是同一個 NPC
        startedNpcs[fate.FateId] = (npc.GameObjectId, ((CsGameObject*)npc.Address)->BaseId);
        interactedAt = now;

        // 對話成功的話馬上就會進入事件中（上面會擋住不再對話），沒反應才會走到重試，不用等太久
        nextInteract = now + TimeSpan.FromSeconds(1.5);
    }

    private DateTime lastMountedAt = DateTime.MinValue;
    private static readonly TimeSpan DismountTalkDelay = TimeSpan.FromSeconds(1);

    private static IGameObject? FateNpc(ushort id)
    {
        foreach (var obj in Services.Objects)
        {
            var native = (CsGameObject*)obj.Address;
            if (native->FateId == id && native->NamePlateIconId == FateStartIcon)
                return obj;
        }

        return null;
    }

    /// <summary>
    /// 自己剛和 FATE 的 NPC 對話後：點掉對話文字，跳出的確認框按「是」。只在對話後的幾秒內才動，
    /// 別的對話或確認框（交易、組隊邀請之類）不會被誤按。每點一次對話就把時限往後延，對話再長也不會中斷。
    /// </summary>
    private void ConfirmFateStart(DateTime now)
    {
        if (now - interactedAt > ConfirmWindow) return;

        var talk = Addons.Get("Talk");
        if (talk != null)
        {
            // 核心的對話推進在作用時交給它點，兩邊一起點會連跳好幾句
            if (Core.Talk.Active)
            {
                interactedAt = now;
                return;
            }

            // 使用者把對話推進關掉（或按著暫停鍵）：開始 FATE 的這段對話還是要過，自己點
            if (now < nextTalk) return;

            Addons.ClickTalk(talk);
            nextTalk = now + TimeSpan.FromMilliseconds(300);
            interactedAt = now;
            return;
        }

        var yesno = Addons.Get("SelectYesno");
        if (yesno == null) return;

        if (Addons.ClickYes(yesno))
        {
            interactedAt = DateTime.MinValue;
            log.Write("對話 確認框按「是」");
        }
    }

    // ── FATE 裡面 ──────────────────────────────────────────────

    private void DoFate(IPlayerCharacter player, IFate fate, DateTime now)
    {
        // 一進圈就算到了，但這時多半還在圈邊的半空中：正飛往圈內降落點的話，飛到了再下坐騎
        if (Services.Condition[ConditionFlag.Mounted] && landingFate == fate.FateId && landingPoint is { } spot
            && moveDest == spot && nav.Busy && Vector3.Distance(player.Position, spot) > 3f)
        {
            Status = "飛往 FATE 裡能降落的地方";
            return;
        }

        // 從趕路切過來：停掉往 FATE 中心（或降落點）的路線
        if (moveDest == fate.Position || (moveDest != null && moveDest == landingPoint))
        {
            nav.Stop();
            moveDest = null;
        }

        if (Services.Condition[ConditionFlag.Mounted])
        {
            DismountNow(now);
            Status = "下坐騎";
            return;
        }

        LevelSync(player, fate, now);

        if (fate.GameData.ValueNullable is { } data && data.EventItem.RowId != 0 && Collect(player, fate, data.EventItem.RowId, now))
            return;

        // 繳交型的進度只有交了才會動：身上有道具就是有在進展，不算卡住
        // （實測撿了 22 個一直交不出去，進度 0% 三分鐘就被當成卡住略過，道具全帶走了）
        var collecting = fate.GameData.ValueNullable is { } collectData && collectData.EventItem.RowId != 0
                         && CountItem(collectData.EventItem.RowId) > 0;

        // FATE 進度一直沒動（怪都打不到、或根本打不完）：這個 FATE 先略過一陣子，換下一個
        if (fate.FateId != fateProgressFate || fate.Progress != fateProgress || collecting)
        {
            fateProgressFate = fate.FateId;
            fateProgress = fate.Progress;
            fateProgressAt = now;
        }
        else if (now - fateProgressAt > FateStallAfter && !Services.Condition[ConditionFlag.InCombat])
        {
            skippedFates[fate.FateId] = now + SkipFateFor;
            log.Write($"FATE 進度 {FateStallAfter.TotalMinutes:0} 分鐘沒動，略過 {SkipFateFor.TotalMinutes:0} 分鐘：#{fate.FateId} {fate.Name.TextValue} 進度={fate.Progress}%");
            fateId = 0;
            nav.Stop();
            moveDest = null;
            return;
        }

        // 野外菁英怪不當目標（要甩開的；放棄甩開後才打）
        var enemies = Enemies()
            .Where(e => (FateOf(e) == fate.FateId || e.TargetObjectId == player.GameObjectId) && !Ignored(e, player)
                        && (!Config.FleeElites || now < fleeGiveUpUntil || !IsFieldElite(e)))
            .ToList();

        if (enemies.Count == 0)
        {
            // 空檔順便叫陸行鳥、修裝備
            if (!Services.Condition[ConditionFlag.InCombat] && Maintenance(player, now)) return;

            Status = "等待怪物出現";

            // 站得太外圍就往中間靠，怪通常從中間刷
            if (Distance2D(player.Position, fate.Position) > fate.Radius * 0.6f && !nav.Busy && now >= nextPath)
            {
                nav.MoveCloseTo(fate.Position, false, fate.Radius * 0.3f);
                nextPath = now + TimeSpan.FromSeconds(2);
            }

            return;
        }

        var target = Pick(player, enemies);
        Status = $"戰鬥：{target.Name}";
        Fight(player, target, now);
    }

    // ── 繳交型 FATE ────────────────────────────────────────────

    /// <summary>
    /// 繳交型：平常照樣打怪（怪會掉 FATE 道具），身上道具夠了、或 FATE 快結束時，
    /// 走去找收道具的 NPC 對話，繳交視窗交給 TextAdvance。
    /// 回傳 true = 這一輪在處理繳交，先不打怪。
    /// </summary>
    /// <summary>正在打的這場 FATE 的怪；牠死了（或不見了）= 剛打死一隻。</summary>
    private ulong killWatch;

    /// <summary>剛打死一隻：這個時間之前等掉的道具進背包；dropWaitHeld = 打死時身上有幾個。</summary>
    private DateTime dropWaitUntil = DateTime.MinValue;

    /// <summary>先往 NPC 走、等剛打死那隻的道具：這個時間之前要進來；MinValue = 沒在等。</summary>
    private DateTime turnInAwaitDrop = DateTime.MinValue;
    private int dropWaitHeld;

    private static readonly TimeSpan DropWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 打死怪後道具要等一下才會進背包。剛打死一隻、而且再一個就夠繳交時回傳 true：
    /// 不用等它進來，直接往 NPC 走，路上道具就進來了（沒進來就回去打怪）。
    /// </summary>
    private bool ExpectingDrop(IFate fate, int held, DateTime now)
    {
        if (EngagedFateTarget(fate) is { } target)
        {
            killWatch = target.GameObjectId;
        }
        else if (killWatch != 0)
        {
            var watched = Services.Objects.SearchById(killWatch) as IBattleNpc;
            if (watched == null || watched.IsDead || watched.CurrentHp == 0)
            {
                dropWaitUntil = now + DropWait;
                dropWaitHeld = held;
            }

            killWatch = 0;
        }

        if (now >= dropWaitUntil) return false;

        // 進來了：照平常判斷要不要去交
        if (held > dropWaitHeld)
        {
            dropWaitUntil = DateTime.MinValue;
            return false;
        }

        // 只有「再一個就夠」才先出發
        return !WouldTurnIn(fate, held) && WouldTurnIn(fate, held + 1);
    }

    /// <summary>身上有 count 個時，數量條件夠不夠去繳交（設定的數量，或量過的每個進度能推滿）。</summary>
    private bool WouldTurnIn(IFate fate, int count)
    {
        if (count >= Config.TurnInAt) return true;
        return progressPerItem.TryGetValue(fate.FateId, out var rate) && rate > 0
               && count >= (int)Math.Ceiling((100 - fate.Progress) / rate);
    }

    /// <summary>目前的目標是這場 FATE 的怪、還活著、而且已經打掉血（= 正在打）。</summary>
    private static IBattleNpc? EngagedFateTarget(IFate fate) =>
        Services.Targets.Target is IBattleNpc npc && !npc.IsDead && npc.CurrentHp > 0
        && npc.CurrentHp < npc.MaxHp && FateOf(npc) == fate.FateId
            ? npc
            : null;

    private bool Collect(IPlayerCharacter player, IFate fate, uint item, DateTime now)
    {
        var held = CountItem(item);

        if (!turningIn)
        {
            var expecting = ExpectingDrop(fate, held, now);
            if ((held == 0 && !expecting) || now < turnInCooldown) return false;

            var enough = held >= Config.TurnInAt;
            var ending = fate.Progress >= 90 || (fate.Duration > 0 && fate.TimeRemaining < 60);

            // 量過每個道具推進多少：身上的已經夠把進度推滿，就不用湊到設定的數量
            var finishes = false;
            if (progressPerItem.TryGetValue(fate.FateId, out var rate) && rate > 0)
                finishes = held >= (int)Math.Ceiling((100 - fate.Progress) / rate);

            if (!enough && !ending && !finishes && !expecting) return false;

            turnInAwaitDrop = expecting && !enough && !ending && !finishes ? dropWaitUntil : DateTime.MinValue;
            turningIn = true;
            turnInStarted = now;
            turnInLastTick = now;
            turnInHeld = held;
            turnInProgress = fate.Progress;
            var why = enough ? "數量夠" : finishes ? $"足夠完成（每個約 {rate:0.#}%）" : ending ? "快結束" : "剛打死一隻，道具進來就夠（先往 NPC 走）";
            log.Write($"繳交 開始 #{fate.FateId} 道具={item} 持有={held} 進度={fate.Progress}% 剩餘={fate.TimeRemaining}s 原因={why}");
        }

        // 交出去了（身上變少）就回去打怪
        if (held < turnInHeld)
        {
            var given = turnInHeld - held;
            var gained = fate.Progress - turnInProgress;
            if (gained > 0 && fate.Progress < 100)
            {
                var measured = (float)gained / given;
                progressPerItem[fate.FateId] = progressPerItem.TryGetValue(fate.FateId, out var old) ? Math.Min(old, measured) : measured;
            }

            log.Write($"繳交 完成 交出 {given} 個 進度={turnInProgress}%→{fate.Progress}%" +
                      (progressPerItem.TryGetValue(fate.FateId, out var r) ? $" 每個約 {r:0.#}%" : ""));
            turningIn = false;

            // 交成功的對象記下來，這個 FATE 下次繳交直接找它
            if (lastTurnInNpc.ObjectId != 0)
                startedNpcs[fate.FateId] = lastTurnInNpc;

            return false;
        }

        // 先出發等道具：進來了就照平常交；時間到還沒進來（沒掉），數量又不夠，就回去打怪
        if (turnInAwaitDrop != DateTime.MinValue)
        {
            if (held > turnInHeld)
            {
                log.Write($"繳交 道具進來了 持有={held}");
                turnInHeld = held;
                turnInAwaitDrop = DateTime.MinValue;
            }
            else if (now > turnInAwaitDrop)
            {
                turnInAwaitDrop = DateTime.MinValue;
                var ending = fate.Progress >= 90 || (fate.Duration > 0 && fate.TimeRemaining < 60);
                if (!WouldTurnIn(fate, held) && !ending)
                {
                    log.Write($"繳交 取消：道具沒有進來 持有={held}，回去打怪");
                    turningIn = false;
                    return false;
                }
            }
        }

        var attackers = Attackers(player);
        var npc = TurnInNpc(fate, player);

        // 在 NPC 旁邊打跟過來的怪：這段時間不算進繳交逾時（不然打完怪逾時、回去打怪、又被一直刷的怪纏住）
        var fightingAtNpc = attackers.Count > 0 && npc != null && Vector3.Distance(player.Position, npc.Position) <= 4f;
        if (fightingAtNpc)
            turnInStarted += now - turnInLastTick;
        turnInLastTick = now;

        if (now - turnInStarted > TurnInTimeout)
        {
            log.Write($"繳交 逾時 持有={held} 進度={fate.Progress}% 交道具={Core.Handin.Active} 對話推進={Core.Talk.Active}");
            turningIn = false;
            turnInCooldown = now + TimeSpan.FromMinutes(1);
            return false;
        }

        if (npc == null)
        {
            // 找不到 NPC：先把身上的怪打掉，再往 FATE 中心找
            if (attackers.Count > 0)
            {
                Status = "繳交前先處理身上的怪";
                Fight(player, Pick(player, attackers), now);
                return true;
            }

            warnedTurnInNpc = true;

            Travel(player, fate.Position, 5f, now, "找收道具的 NPC");
            return true;
        }

        var distance = Vector3.Distance(player.Position, npc.Position);
        if (distance > 4f)
        {
            // 道具夠了就脫離戰鬥直接走：怪會一直刷，在原地打完再走會被纏住打不完。
            // 跟過來的怪到 NPC 旁邊再打
            if (attackers.Count > 0 && (attacking || Services.Targets.Target != null))
            {
                StopAttacking();
                Services.Targets.Target = null;
                log.Write($"繳交 脫離戰鬥 身上的怪={attackers.Count}，先去 NPC 旁邊");
            }

            Travel(player, npc.Position, 3f, now, $"去找 {npc.Name} 繳交（{held} 個）");
            return true;
        }

        // 到 NPC 旁邊了：跟過來的怪在這裡打掉，不然會打斷對話
        if (attackers.Count > 0)
        {
            Status = $"在 {npc.Name} 旁邊處理跟過來的怪";
            Fight(player, Pick(player, attackers), now);
            return true;
        }

        if (moveDest != null)
        {
            nav.Stop();
            moveDest = null;
        }

        // 走到了道具還沒進來：在 NPC 旁邊等一下再對話
        if (turnInAwaitDrop != DateTime.MinValue)
        {
            Status = $"在 {npc.Name} 旁邊等道具進背包";
            return true;
        }

        Status = $"繳交給 {npc.Name}（{held} 個）";

        if (now < nextInteract || Services.Condition[ConditionFlag.OccupiedInQuestEvent]
                               || Services.Condition[ConditionFlag.OccupiedInEvent])
            return true;

        Services.Targets.Target = npc;
        TargetSystem.Instance()->InteractWithObject((CsGameObject*)npc.Address);
        interactedAt = now;
        nextInteract = now + TimeSpan.FromSeconds(4);
        log.Write($"繳交 對話 {npc.Name}（BaseId={((CsGameObject*)npc.Address)->BaseId}）持有={held}");
        lastTurnInNpc = (npc.GameObjectId, ((CsGameObject*)npc.Address)->BaseId);
        return true;
    }

    /// <summary>
    /// 收道具的 NPC，依序找：
    /// 1. 開始 FATE 時對話的那個 NPC（實測「藥師的工作」收道具的就是開始時的 NPC）
    /// 2. FATE 資料記的目標 NPC／發起 NPC（比對 BaseId，不管是哪一類物件）
    /// 3. 掛著這個 FATE 編號、不能攻擊的 NPC
    /// 第一版只找「事件 NPC + FATE 編號」，實測找不到，所以放寬，找不到時把附近的候選寫進 log。
    /// </summary>
    private IGameObject? TurnInNpc(IFate fate, IPlayerCharacter player)
    {
        var context = FateManager.Instance()->GetFateById(fate.FateId);
        var objective = context != null ? context->ObjectiveNpc : 0;
        var motivation = context != null ? context->MotivationNpc : 0;
        startedNpcs.TryGetValue(fate.FateId, out var started);

        IGameObject? byStartedBase = null, byBase = null, byFateNpc = null, byFate = null;
        foreach (var obj in Services.Objects)
        {
            if (!obj.IsTargetable || obj.GameObjectId == player.GameObjectId) continue;
            if (obj is IPlayerCharacter) continue;

            // 怪物（活的能攻擊、死的屍體還能選一陣子）一律不算：
            // 實測打死的「山嶺蟻蛉」屍體不能攻擊了，被當成收道具的 NPC 對話了三次
            if (obj is IBattleNpc battle && (battle.IsDead || battle.CurrentHp == 0 || Attackable(battle))) continue;

            var native = (CsGameObject*)obj.Address;

            if (started.ObjectId != 0 && obj.GameObjectId == started.ObjectId)
                return obj;

            // 開始時對話的 NPC 換了物件（重新生成）也認得：比 FATE 資料記的 NPC 優先
            if (started.BaseId != 0 && native->BaseId == started.BaseId)
                byStartedBase ??= obj;
            else if (native->BaseId != 0 && (native->BaseId == objective || native->BaseId == motivation))
                byBase ??= obj;

            if (native->FateId == fate.FateId)
            {
                // 同樣掛著 FATE 編號的還可能是場景物件，NPC 優先
                var isNpc = native->ObjectKind is FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.EventNpc
                    or FFXIVClientStructs.FFXIV.Client.Game.Object.ObjectKind.BattleNpc;
                if (isNpc) byFateNpc ??= obj;
                else byFate ??= obj;
            }
        }

        var found = byStartedBase ?? byBase ?? byFateNpc ?? byFate;
        if (found == null && !warnedTurnInNpc)
            LogTurnInCandidates(fate, player, objective, motivation, started.BaseId);

        return found;
    }

    /// <summary>找不到收道具的 NPC 時，把 FATE 附近的 NPC 都記下來，下次看 log 就知道該怎麼認。</summary>
    private void LogTurnInCandidates(IFate fate, IPlayerCharacter player, uint objective, uint motivation, uint started)
    {
        log.Write($"繳交 找不到收道具的 NPC #{fate.FateId} 目標NPC={objective} 發起NPC={motivation} 開始時對話={started}");

        var nearby = Services.Objects
            .Where(o => o is not IPlayerCharacter && o.GameObjectId != player.GameObjectId
                        && Vector3.Distance(o.Position, fate.Position) < fate.Radius + 30f)
            .Where(o => o is not IBattleNpc b || !Attackable(b))
            .Take(15);

        foreach (var obj in nearby)
        {
            var native = (CsGameObject*)obj.Address;
            log.Write($"  候選 {obj.Name} 類型={native->ObjectKind} BaseId={native->BaseId} FATE={native->FateId} " +
                      $"可選={obj.IsTargetable} 距離={Vector3.Distance(player.Position, obj.Position):0}");
        }
    }

    /// <summary>身上某個道具的數量。FATE 道具放在「重要物品」欄，一般的數量函式不一定算得到，兩邊都看。</summary>
    private static int CountItem(uint item)
    {
        var inventory = InventoryManager.Instance();
        var count = inventory->GetInventoryItemCount(item);

        var keyItems = inventory->GetInventoryContainer(InventoryType.KeyItems);
        if (keyItems != null && keyItems->IsLoaded)
        {
            var sum = 0;
            for (var i = 0; i < keyItems->Size; i++)
            {
                var slot = keyItems->GetInventorySlot(i);
                if (slot != null && slot->ItemId == item)
                    sum += slot->Quantity;
            }

            count = Math.Max(count, sum);
        }

        return count;
    }

    private void LevelSync(IPlayerCharacter player, IFate fate, DateTime now)
    {
        if (now < nextSync) return;

        var manager = FateManager.Instance();
        var current = manager->CurrentFate;
        if (current == null || current->FateId != fate.FateId) return;

        TankStance(player, now);

        if (manager->SyncedFateId == fate.FateId) return;
        if (player.Level <= current->MaxLevel) return;

        manager->LevelSync();
        log.Write($"等級同步 #{fate.FateId} {player.Level}→{current->MaxLevel}");
        nextSync = now + TimeSpan.FromSeconds(3);
    }

    /// <summary>坦克職業進 FATE 時開防禦姿態（已經開著就不動），怪才會打你不會打旁邊的人。</summary>
    private void TankStance(IPlayerCharacter player, DateTime now)
    {
        if (!Config.TankStance || now < nextStance) return;
        if (!TankStances.TryGetValue(player.ClassJob.RowId, out var stance)) return;
        if (player.StatusList.Any(s => s.StatusId == stance.Status)) return;
        if (!CanUse(ActionType.Action, stance.Action)) return;

        ActionManager.Instance()->UseAction(ActionType.Action, stance.Action);
        log.Write($"坦克姿態 開啟 技能={stance.Action}");
        nextStance = now + TimeSpan.FromSeconds(5);
    }

    /// <summary>執行期間有人發起交易就直接拒絕：掛機時沒人能回應，交易視窗開著還會擋住其他操作。</summary>
    private void RefuseTrade(DateTime now)
    {
        if (!Config.RefuseTrade || now < nextTrade || !Addons.IsOpen("Trade")) return;

        InventoryManager.Instance()->RefuseTrade();
        log.Write("交易 已拒絕別人發起的交易");
        nextTrade = now + TimeSpan.FromSeconds(2);
    }

    // ── 戰鬥 ───────────────────────────────────────────────────

    /// <summary>
    /// 選定目標、走到射程內就停，剩下交給自動循環。
    /// 路線只在「沒在走」或「目標離終點太遠」時重算，這樣能一路走到位，不會走走停停。
    /// </summary>
    private void Fight(IPlayerCharacter player, IBattleNpc target, DateTime now)
    {
        if (Services.Condition[ConditionFlag.Mounted]
            && Vector3.Distance(player.Position, target.Position) < 15f)
        {
            // 只停自己的追怪路線；DismountNow 可能正在飛往降落點，不能每幀都停掉
            if (moveDest != null)
            {
                nav.Stop();
                moveDest = null;
            }

            DismountNow(now);
            return;
        }

        if (Services.Targets.Target?.GameObjectId != target.GameObjectId)
            Services.Targets.Target = target;

        StartAttacking();

        var reach = AttackRange(player) + target.HitboxRadius;
        var distance = Vector3.Distance(player.Position, target.Position);

        if (Unreachable(player, target, distance, now))
            return;

        CheckDamage(target, now);

        // 在射程內卻打不出去：視線被地形擋住，或站在高低差的地方。
        // 遊戲回報看不到、或站著 6 秒都沒打到，就貼到近身距離再打
        // 一旦決定貼近就記住這隻，不然走近一步計時重來，又會停在原地
        if (NoLineOfSight(player, target) || now - engageSince > CloseInAfter)
            closeInTarget = target.GameObjectId;
        if (closeInTarget == target.GameObjectId)
            reach = Math.Min(reach, 3f + target.HitboxRadius);

        if (distance <= reach)
        {
            if (moveDest != null)
            {
                nav.Stop();
                moveDest = null;
            }

            return;
        }

        var stale = moveDest is { } dest && Vector3.Distance(dest, target.Position) > 5f;
        if ((!nav.Busy || stale) && now >= nextPath)
        {
            if (stale) nav.Stop();

            // 停在比射程近一點的地方，免得剛好卡在邊緣、怪一動又出界
            if (nav.MoveCloseTo(target.Position, false, Math.Max(1f, reach - 1f)))
                moveDest = target.Position;

            nextPath = now + TimeSpan.FromMilliseconds(500);
        }
    }

    /// <summary>
    /// 盯著同一個目標太久卻沒有任何進展（沒變近、沒掉血、它也沒來打我）就當作打不到：
    /// 多半是站在 vnavmesh 走不到的地方（高台、牆上）或視線被擋。略過它一分鐘，換別隻。
    /// 回傳 true = 這次判定打不到，已經放掉目標。
    /// </summary>
    private bool Unreachable(IPlayerCharacter player, IBattleNpc target, float distance, DateTime now)
    {
        if (target.GameObjectId != engageTarget)
        {
            engageTarget = target.GameObjectId;
            engageSince = now;
            engageBest = distance;
            engageHp = target.CurrentHp;
            return false;
        }

        var closer = distance < engageBest - 1f;
        var hurt = target.CurrentHp < engageHp;
        var fightingMe = target.TargetObjectId == player.GameObjectId;

        if (closer || hurt || fightingMe)
        {
            engageSince = now;
            engageBest = Math.Min(engageBest, distance);
            engageHp = Math.Min(engageHp, target.CurrentHp);
            return false;
        }

        if (now - engageSince < UnreachableAfter) return false;

        ignoredTargets[target.GameObjectId] = now + IgnoreTargetFor;
        log.Write($"目標打不到，略過 1 分鐘：{target.Name} 距離={distance:0} HP={target.CurrentHp}/{target.MaxHp} " +
                  $"尋路={nav.Busy} 位置=({target.Position.X:0},{target.Position.Y:0},{target.Position.Z:0})");

        nav.Stop();
        moveDest = null;
        engageTarget = 0;
        Services.Targets.Target = null;
        return true;
    }

    private bool Ignored(IBattleNpc npc, IPlayerCharacter player)
    {
        if (npc.TargetObjectId == player.GameObjectId) return false;
        if (!ignoredTargets.TryGetValue(npc.GameObjectId, out var until)) return false;
        if (DateTime.Now < until) return true;

        ignoredTargets.Remove(npc.GameObjectId);
        return false;
    }

    private float AttackRange(IPlayerCharacter player)
    {
        var role = player.ClassJob.ValueNullable?.Role ?? 0;
        return role is 1 or 2 ? Config.MeleeRange : Config.RangedRange;
    }

    /// <summary>優先打打我的怪，其次是正在打的目標，最後挑最近的。</summary>
    private static IBattleNpc Pick(IPlayerCharacter player, List<IBattleNpc> enemies)
    {
        var current = Services.Targets.Target;
        if (current != null && enemies.FirstOrDefault(e => e.GameObjectId == current.GameObjectId) is { } keep)
            return keep;

        return enemies
            .OrderBy(e => e.TargetObjectId == player.GameObjectId ? 0 : 1)
            .ThenBy(e => Vector3.Distance(player.Position, e.Position))
            .First();
    }

    private IBattleNpc? PriorityTarget(IPlayerCharacter player)
    {
        if (Config.PriorityNames.Count == 0) return null;

        return Enemies()
            .Where(e => Config.PriorityNames.Contains(e.NameId)
                        && Vector3.Distance(player.Position, e.Position) <= Config.PriorityRange
                        && !Ignored(e, player))
            .OrderBy(e => Vector3.Distance(player.Position, e.Position))
            .FirstOrDefault();
    }

    // 名牌上的怪物等級標記（菁英怪、狩獵怪）：遊戲圖示 61701～61712（藍、橘紅兩套）
    private const uint FirstRankIcon = 61701;
    private const uint LastRankIcon = 61712;

    private static readonly TimeSpan FleeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FleeGiveUpFor = TimeSpan.FromMinutes(2);
    private const float FleeDistance = 80f;

    private DateTime fleeSince = DateTime.MinValue;
    private DateTime fleeGiveUpUntil = DateTime.MinValue;
    private Vector3? fleePoint;
    private DateTime fleePointAt;

    /// <summary>野外菁英怪：名牌有怪物等級標記、而且不屬於任何 FATE（FATE 的頭目不算，那是要打的）。</summary>
    private static bool IsFieldElite(IBattleNpc npc)
    {
        var icon = ((CsGameObject*)npc.Address)->NamePlateIconId;
        return icon is >= FirstRankIcon and <= LastRankIcon && FateOf(npc) == 0;
    }

    /// <summary>
    /// 有野外菁英怪在打我：往遠離它的方向跑（騎著就繼續飛，不下坐騎），直到脫離戰鬥。
    /// 回傳 true = 正在甩，這一輪別做別的。跑超過 FleeTimeout 還甩不掉就放棄，之後 2 分鐘照常打。
    /// </summary>
    private bool FleeElites(IPlayerCharacter player, DateTime now)
    {
        if (now < fleeGiveUpUntil) return false;

        var elites = Attackers(player).Where(IsFieldElite).ToList();
        if (elites.Count == 0)
        {
            if (fleeSince != DateTime.MinValue)
            {
                log.Write($"甩開菁英怪 完成（{(now - fleeSince).TotalSeconds:0} 秒）");
                fleeSince = DateTime.MinValue;
                fleePoint = null;
                nav.Stop();
                moveDest = null;
            }

            return false;
        }

        var nearest = elites.OrderBy(e => Vector3.Distance(e.Position, player.Position)).First();
        if (fleeSince == DateTime.MinValue)
        {
            fleeSince = now;
            StopAttacking();
            log.Write($"被菁英怪纏上，往反方向甩開：{string.Join("、", elites.Select(e => e.Name.TextValue))}");
        }

        if (now - fleeSince > FleeTimeout)
        {
            log.Write($"甩開菁英怪 {FleeTimeout.TotalSeconds:0} 秒還甩不掉，放棄，照常打");
            fleeGiveUpUntil = now + FleeGiveUpFor;
            fleeSince = DateTime.MinValue;
            fleePoint = null;
            return false;
        }

        // 逃跑點：從怪往自己的方向再往外 80 米；走近了或過幾秒就重算（怪會跟著動）
        if (fleePoint is not { } point || Vector3.Distance(player.Position, point) < 10f || now - fleePointAt > TimeSpan.FromSeconds(6))
        {
            var away = player.Position - nearest.Position;
            away.Y = 0;
            away = away.LengthSquared() < 0.01f ? new Vector3(1, 0, 0) : Vector3.Normalize(away);

            var target = player.Position + away * FleeDistance;
            if (!Services.Condition[ConditionFlag.InFlight])
                target = nav.LandingPoint(target, 20f) ?? target;

            fleePoint = target;
            fleePointAt = now;
        }

        Travel(player, fleePoint.Value, 5f, now, $"甩開菁英怪 {nearest.Name.TextValue}");
        return true;
    }

    private static List<IBattleNpc> Attackers(IPlayerCharacter player) =>
        Enemies().Where(e => e.TargetObjectId == player.GameObjectId).ToList();

    private static IEnumerable<IBattleNpc> Enemies()
    {
        foreach (var obj in Services.Objects)
        {
            if (obj is not IBattleNpc npc) continue;
            if (npc.BattleNpcKind != BattleNpcSubKind.Enemy) continue;
            if (npc.IsDead || npc.CurrentHp == 0 || !npc.IsTargetable) continue;

            // FATE 裡的友方 NPC（要保護的對象、求救的人）也是 Enemy 類、也掛著 FATE 編號，
            // 只看那兩個會把它們當成怪（log 裡「走投無路的再造者」就是這樣站著盯了好幾分鐘）。
            // 改問遊戲「攻擊技能能不能對它用」，跟 RSR 判斷敵人的方式一樣（142 = 冰結，只看目標類型）
            if (!Attackable(npc)) continue;

            yield return npc;
        }
    }

    // 迭代器裡不能用指標，另外包一層
    private static bool Attackable(IBattleNpc npc) =>
        ActionManager.CanUseActionOnTarget(AttackCheckAction, (CsGameObject*)npc.Address);

    private static ushort FateOf(IBattleNpc npc) => ((CsCharacter*)npc.Address)->FateId;

    /// <summary>
    /// 戰鬥中目標一直沒掉血：「打不到」的判斷在怪來打我時不會成立（它一直在打我，計時一直重來），
    /// 所以另外看血量。20 秒沒掉就當作自動循環插件停了（被別的東西關掉、暫停），重送一次開始指令並記下來。
    /// </summary>
    private void CheckDamage(IBattleNpc target, DateTime now)
    {
        if (target.GameObjectId != damageTarget || target.CurrentHp < damageHp)
        {
            damageTarget = target.GameObjectId;
            damageHp = target.CurrentHp;
            damageSince = now;
            return;
        }

        if (!Services.Condition[ConditionFlag.InCombat] || now - damageSince < NoDamageAfter) return;

        log.Write($"目標 {NoDamageAfter.TotalSeconds:0} 秒沒掉血，重送 {Config.RotationName} 開始指令：{target.Name} " +
                  $"HP={target.CurrentHp}/{target.MaxHp} 打我={target.TargetObjectId == Services.Objects.LocalPlayer?.GameObjectId}");

        attacking = false;
        StartAttacking();
        damageSince = now;
    }

    /// <summary>開始前 Wrath 原本的選目標模式（傷害、治療）；沒記到是 null。</summary>
    private (string? Damage, string? Healer) wrathTargeting;

    /// <summary>用 Wrath 時，開始前記下原本的選目標模式（透過 Wrath 的 IPC 讀，不改）。</summary>
    private void RememberWrathTargeting()
    {
        wrathTargeting = (null, null);
        if (Config.Rotation != RotationPlugin.WrathCombo) return;

        wrathTargeting = (WrathIpc.DamageTargeting, WrathIpc.HealerTargeting);
        log.Write($"Wrath 原本的選目標模式：傷害={wrathTargeting.Damage ?? "讀不到"} 治療={wrathTargeting.Healer ?? "讀不到"}");
    }

    /// <summary>
    /// 停止時把 Wrath 的選目標模式設回開始前的樣子。
    /// 「/wrath auto target …」會順便關掉自動循環，停止時剛好不影響。
    /// </summary>
    private void RestoreWrathTargeting()
    {
        var (damage, healer) = wrathTargeting;
        wrathTargeting = (null, null);
        if (damage == null && healer == null) return;

        var commands = new List<string>();
        if (damage != null) commands.Add($"/wrath auto target damage {damage}");
        if (healer != null) commands.Add($"/wrath auto target healer {healer}");

        plugin.Executor.Enqueue(string.Join("\n", commands));
        log.Write($"Wrath 選目標模式設回：{string.Join(" / ", commands)}");
    }

    private void StartAttacking()
    {
        if (attacking) return;

        attacking = true;
        plugin.Executor.Enqueue(Config.StartCommands);
        log.Write($"{Config.RotationName} 開始：{OneLine(Config.StartCommands)}");
    }

    private void StopAttacking()
    {
        if (!attacking) return;

        attacking = false;
        plugin.Executor.Enqueue(Config.StopCommands);
        log.Write($"{Config.RotationName} 停止：{OneLine(Config.StopCommands)}");
    }

    private static string OneLine(string commands) =>
        string.Join(" / ", commands.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    // ── 兩場之間的雜事 ─────────────────────────────────────────

    /// <summary>修裝備、餵陸行鳥。回傳 true = 這一輪做了事，先別移動。只在非戰鬥、沒騎坐騎時叫。</summary>
    private bool Maintenance(IPlayerCharacter player, DateTime now)
    {
        // 剛餵完野菜：等動作做完、確認陸行鳥出來了，不要馬上上坐騎把它蓋掉
        if (chocoboCheckAt != DateTime.MinValue)
        {
            if (now < chocoboCheckAt) return true;

            chocoboCheckAt = DateTime.MinValue;
            var left = UIState.Instance()->Buddy.CompanionInfo.TimeLeft;
            if (left > chocoboBefore || ChocoboOut(player))
            {
                chocoboFailures = 0;
                chocoboDue = false;
                log.Write($"陸行鳥 已叫出 剩餘={left:0}s");
            }
            else
            {
                chocoboFailures++;
                log.Write($"陸行鳥 沒有叫出來（第 {chocoboFailures} 次） 剩餘={left:0}s");
                if (chocoboFailures >= 3)
                {
                    // 這個檢查點試 3 次都不行（例如這張地圖不能叫）：等下一個檢查點（傳送後、FATE 結束後）再試
                    chocoboDue = false;
                    chocoboFailures = 0;
                }
                else
                {
                    // 原地再試，不要先上坐騎飛走（飛走之後要等下一場 FATE 結束才會再叫）
                    nextChocobo = now + ChocoboRetry;
                    Status = "叫出陸行鳥（重試）";
                    return true;
                }
            }
        }

        if (Services.Condition[ConditionFlag.Mounted] || player.IsCasting) return false;

        EnsureChocoboStance(player, now);

        // 修理交給核心：耐久低於核心設定的門檻、而且現在方便修，它就開始修（失敗後 10 分鐘內不再試）
        if (Config.AutoRepair && Core.Repair.NeedsRepair && !Core.Repair.Busy)
        {
            var lowest = RepairService.LowestDurability();
            if (Core.Repair.TryStart(now))
            {
                nav.Stop();
                moveDest = null;
                StopAttacking();
                log.Write($"修理 開始 最低耐久={lowest}%");
                Status = "修理裝備";
                return true;
            }
        }

        // 有精煉度滿的裝備：開精製魔晶石視窗全部精製（失敗後 10 分鐘內不再試）
        if (Config.AutoMaterialize && now >= materializeRetryAt && GameState.Idle && MaterializeRunner.FullItems() > 0)
        {
            StartMaterialize(now);
            return true;
        }

        // 陸行鳥只在檢查點看一次：開始、傳送完、每場 FATE 結束、復活後。時間還夠就不用叫，這個檢查點結束
        if (Config.SummonChocobo && chocoboDue && now >= nextChocobo
            && UIState.Instance()->Buddy.CompanionInfo.TimeLeft >= Config.ChocoboMinSeconds && ChocoboOut(player))
        {
            chocoboDue = false;
        }

        // 剛傳送、復活完：角色還沒就緒，等一下再叫（原地等，不要先飛走）
        if (Config.SummonChocobo && chocoboDue && now < chocoboNotBefore)
        {
            Status = "等角色就緒再叫陸行鳥";
            return true;
        }

        if (Config.SummonChocobo && chocoboDue && now >= nextChocobo)
        {
            nextChocobo = now + TimeSpan.FromSeconds(10);

            if (InventoryManager.Instance()->GetInventoryItemCount(GysahlGreens) == 0)
            {
                chocoboDue = false;
                if (!warnedNoGreens)
                {
                    Notify.Attention("自動Fate", "身上沒有基薩爾野菜，叫不出陸行鳥。");
                    warnedNoGreens = true;
                }

                return false;
            }

            if (CanUse(ActionType.Item, GysahlGreens))
            {
                chocoboBefore = UIState.Instance()->Buddy.CompanionInfo.TimeLeft;
                // 以自己為目標用野菜（目標給「無」時遊戲會拒絕）；還是不接受就改用「從背包使用道具」
                var used = ActionManager.Instance()->UseAction(ActionType.Item, GysahlGreens, player.GameObjectId, 65535);
                var how = "技能";
                if (!used)
                {
                    var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventoryContext.Instance();
                    if (agent != null)
                    {
                        agent->UseItem(GysahlGreens, (InventoryType)9999, 0, 0);
                        how = "背包使用道具";
                    }
                }

                log.Write($"陸行鳥 餵野菜（{how}）剩餘={chocoboBefore:0}s{(used ? "" : "，技能方式遊戲沒有接受")}");
                Status = "叫出陸行鳥";
                chocoboCheckAt = now + TimeSpan.FromSeconds(2);
                return true;
            }

            chocoboFailures++;
            log.Write($"陸行鳥 現在不能用野菜（狀態碼 {ActionManager.Instance()->GetActionStatus(ActionType.Item, GysahlGreens)}，第 {chocoboFailures} 次）");
            if (chocoboFailures >= 3)
            {
                chocoboDue = false;
                chocoboFailures = 0;
                return false;
            }

            nextChocobo = now + ChocoboRetry;
            return true;
        }

        return false;
    }

    /// <summary>多區域：清單裡的下一個區域。不在清單裡就從第一個開始；下一個就是自己（只有一個區域）時是 null。</summary>
    private uint? NextZone()
    {
        if (!Config.MultiZone || Config.Zones.Count == 0) return null;

        var current = Services.ClientState.TerritoryType;
        var index = Config.Zones.IndexOf(current);
        var next = Config.Zones[(index + 1) % Config.Zones.Count];

        return next == current ? null : next;
    }

    /// <summary>該停了的原因（寶石到量、時間到）；還不用停就是 null。</summary>
    private string? FinishReason(DateTime now)
    {
        if (Config.StopOnGemstones && Gemstones >= Config.GemstoneLimit)
            return $"雙色寶石已達 {Gemstones} 個";

        if (Config.StopOnTimer && now - startedAt >= TimeSpan.FromMinutes(Config.StopAfterMinutes))
            return $"已執行 {Config.StopAfterMinutes} 分鐘";

        return null;
    }

    /// <summary>每分鐘一筆的快照：事後看 log 時，就算狀態沒變也知道角色在哪、在幹嘛。</summary>
    private void Snapshot(IPlayerCharacter player)
    {
        var target = Services.Targets.Target;
        var targetText = target == null
            ? "無"
            : $"{target.Name}({Vector3.Distance(player.Position, target.Position):0}m" +
              (target is IBattleNpc npc && npc.MaxHp > 0 ? $" HP{npc.CurrentHp * 100 / npc.MaxHp}%" : string.Empty) + ")";

        var fate = fateId == 0 ? null : Services.Fates.FirstOrDefault(f => f.FateId == fateId);
        var fateText = fate == null ? "無" : $"#{fate.FateId} {fate.Name.TextValue} {fate.State} {fate.Progress}%";

        log.Write($"快照 區域={Services.ClientState.TerritoryType} 位置=({player.Position.X:0},{player.Position.Y:0},{player.Position.Z:0}) " +
                  $"HP={player.CurrentHp}/{player.MaxHp} 等級={player.Level} 狀態={Status} FATE={fateText} 目標={targetText} " +
                  $"戰鬥={Services.Condition[ConditionFlag.InCombat]} 坐騎={Services.Condition[ConditionFlag.Mounted]} " +
                  $"尋路={nav.Busy} 寶石={Gemstones}(+{Gemstones - startGemstones}) 耐久={RepairService.LowestDurability()}% " +
                  $"陸行鳥={UIState.Instance()->Buddy.CompanionInfo.TimeLeft:0}s{(ChocoboOut(player) ? "（在身邊）" : Services.Condition[ConditionFlag.Mounted] ? "（騎坐騎中）" : "（不在）")} 區域FATE={Services.Fates.Length}");
    }

    // ── 小工具 ─────────────────────────────────────────────────

    private void DismountNow(DateTime now)
    {
        // 連續在下坐騎就累計時間；中間斷過 3 秒以上算新的一輪
        if (now - lastDismountTry > TimeSpan.FromSeconds(3)) dismountSince = now;
        lastDismountTry = now;

        if (now < nextMount) return;

        // 底下不能降落（水面、陡坡、建築上）時按下坐騎沒反應，會一直懸在空中：
        // 按了 4 秒還在飛，就請 vnavmesh 找附近能降落的地面點飛過去，到了再按
        var player = Services.Objects.LocalPlayer;
        if (player != null && Services.Condition[ConditionFlag.InFlight]
            && now - dismountSince > TimeSpan.FromSeconds(4))
        {
            // 一直下不來：這個 FATE 先略過，換一個（飛去別的地方通常就能降落）。
            // 實測卡在 (250,7,-256) 附近 40 米都找不到能降落的點，懸在空中一分多鐘
            if (now - dismountSince > LandGiveUpAfter && fateId != 0)
            {
                log.Write($"下坐騎 {LandGiveUpAfter.TotalSeconds:0} 秒還下不來，略過 FATE #{fateId} {SkipFateFor.TotalMinutes:0} 分鐘");
                skippedFates[fateId] = now + SkipFateFor;
                fateId = 0;
                nav.Stop();
                moveDest = null;
                dismountSince = now;
                return;
            }

            if (!nav.Busy && now >= nextLandingSearch)
            {
                nextLandingSearch = now + TimeSpan.FromSeconds(3);

                // vnavmesh 說能降落、遊戲卻不讓降（實測 (149,36,-772) 就在腳下，按了 30 秒都下不來）：
                // 人已經在那個點上還下不來，就記成「降不了」，換別的點。最多連換 5 個近的
                var spot = NextLandingSpot(player);
                for (var i = 0; i < 5 && spot is { } near && Vector3.Distance(near, player.Position) < 3f; i++)
                {
                    failedLandings.Add(near);
                    log.Write($"降落點 ({near.X:0},{near.Y:0},{near.Z:0}) 人在上面還是下不來，記下來換別的點");
                    spot = NextLandingSpot(player);
                }

                log.Write($"飛行中無法降落 位置=({player.Position.X:0},{player.Position.Y:0},{player.Position.Z:0}) 降落點={spot?.ToString() ?? "找不到"}");
                if (spot is { } land && Vector3.Distance(land, player.Position) > 1.5f)
                {
                    nav.MoveCloseTo(land, true, 1f);
                    moveDest = null;
                }
            }

            if (nav.Busy) return;
        }

        // 飛行中按一次是降落，落地後再按一次才下坐騎，所以隔一秒重按到下來為止
        ActionManager.Instance()->UseAction(ActionType.GeneralAction, Dismount);
        nextMount = now + TimeSpan.FromSeconds(1);
        noRemountUntil = now + NoRemountAfterDismount;
    }

    private DateTime dismountSince;
    private DateTime lastDismountTry;
    private DateTime nextLandingSearch;

    // 要換好幾個點飛過去試，給久一點
    private static readonly TimeSpan LandGiveUpAfter = TimeSpan.FromSeconds(60);

    /// <summary>試過、人在上面卻下不來的降落點（vnavmesh 說能降、遊戲不讓降）。開始時清空。</summary>
    private readonly List<Vector3> failedLandings = new();

    private bool FailedLanding(Vector3 p) => failedLandings.Exists(f => Vector3.Distance(f, p) < 4f);

    /// <summary>
    /// 下一個要試的降落點：在打 FATE 就先用圈內的降落點（從自己位置找的點可能在圈外，降落後又被拉回圈內飛，一直循環）；
    /// 再來以自己為中心，由近到遠往 8 個方向找能降落的地面點；都沒有就找最近的導航網格點（可能在下面很遠）。跳過試過降不了的點。
    /// </summary>
    private Vector3? NextLandingSpot(IPlayerCharacter player)
    {
        if (fateId != 0 && landingFate == fateId && landingPoint is { } fateLand && !FailedLanding(fateLand))
            return fateLand;

        // 第一輪只要「四周也都能降落」的點：vnavmesh 找最近的點會吸到可降落區的邊上，
        // 實測邊上的點差一點點遊戲就不讓降，而且之後找的都吸到同一個邊、全被當成試過的點濾掉（一直「找不到」）。
        // 第一輪都沒有，第二輪才接受邊上的點
        foreach (var roomy in new[] { true, false })
        {
            foreach (var radius in new[] { 0f, 6f, 12f, 25f, 50f, 80f })
            {
                var directions = radius == 0f ? 1 : 8;
                for (var i = 0; i < directions; i++)
                {
                    var angle = i * MathF.PI / 4f;
                    var probe = player.Position + new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
                    var extent = Math.Max(3f, radius * 0.5f);
                    if ((roomy ? RoomyLanding(probe, extent) : nav.LandingPoint(probe, extent)) is { } p && !FailedLanding(p))
                        return p;
                }
            }
        }

        return nav.NearestPoint(player.Position, 100f, 100f) is { } n && !FailedLanding(n) ? n : null;
    }

    /// <summary>probe 附近能降落、而且前後左右 LandingMargin 米也都能降落（不在可降落區邊上）的點；沒有就 null。</summary>
    private Vector3? RoomyLanding(Vector3 probe, float halfExtent)
    {
        if (nav.LandingPoint(probe, halfExtent) is not { } p || FailedLanding(p)) return null;

        foreach (var offset in LandingOffsets)
        {
            var side = p + offset;
            // 旁邊的點要就在那裡（沒被吸到別處）、高度也差不多（不是懸崖下面）
            if (nav.LandingPoint(side, 1f) is not { } q || Distance2D(q, side) > 1f || Math.Abs(q.Y - p.Y) > 2f)
                return null;
        }

        return p;
    }

    private const float LandingMargin = 2.5f;

    private static readonly Vector3[] LandingOffsets =
    {
        new(LandingMargin, 0, 0), new(-LandingMargin, 0, 0), new(0, 0, LandingMargin), new(0, 0, -LandingMargin),
    };

    /// <summary>遊戲自己的射程／視線判定：562 = 視線被擋。用冰結只是借它的判定，跟職業無關。</summary>
    private static bool NoLineOfSight(IPlayerCharacter player, IBattleNpc target) =>
        ActionManager.GetActionInRangeOrLoS(AttackCheckAction,
            (CsGameObject*)player.Address, (CsGameObject*)target.Address) == 562;

    private static string Remaining(IFate fate) =>
        fate.State == FateState.Preparation || fate.Duration == 0 ? "未開始" : $"{fate.TimeRemaining}s";

    private static bool CanUse(ActionType type, uint id) =>
        ActionManager.Instance()->GetActionStatus(type, id) == 0;

    private static float Distance2D(Vector3 a, Vector3 b) =>
        Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));
}
