using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherKit.Core;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using Lumina.Excel.Sheets;
using CsGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AetherKit.Game;

/// <summary>
/// 無人島採集：照採集點座標（IslandGatherData）一個個過去採，跟自己操作一樣用 vnavmesh 走或飛，不瞬移。
///
/// 島上每採 11 個點，最早採的會重生，所以開始時挑 11 個靠得最近的點排成一圈（IslandRoute），之後一直繞這一圈。
/// 每個點：遠的上坐騎飛過去、近的走過去 → 下坐騎 → 點上真的有採集點（已經重生）才對話採集 → 等動作做完。
/// 要先自己切到「收穫」模式（不是收穫模式時原地等）。水下的點：騎飛行坐騎直接飛進水裡（遊戲會自己切成潛水），到了才下坐騎。
/// 停止：圈數到、聊天欄出現「持有數量已達到上限」、或設定的庫存目標全部到量。
/// </summary>
public sealed unsafe class IslandGather : IDisposable
{
    /// <summary>借用核心能力、拿操作鎖、寫 log 用的名字。和模組 Id 一樣。</summary>
    public const string Owner = "island-gather";

    /// <summary>收穫模式（MJIHudMode 表第 1 列）。</summary>
    private const uint GatherMode = 1;

    /// <summary>「持有數量已達到上限，無法獲得更多的。」（LogMessage 表）</summary>
    private const uint CapMessage = 4575;

    private const uint MountRoulette = 9;
    private const uint FlyingRoulette = 24;
    private const uint Dismount = 23;
    private const uint Jump = 2;

    /// <summary>移動時到這個距離內就算到了，不用走到點的正中間（採集點的對話距離夠大）。</summary>
    private const float GatherRange = 4f;

    /// <summary>離採集點這麼近就先在原地試著採，不再往前走（島上採集點的對話距離比岩石半徑大）。</summary>
    private const float FarInteract = 4.5f;

    /// <summary>圈上的點還沒重生時，改採這個距離（米）內的其他採集點。</summary>
    private const float SpareRange = 40f;

    /// <summary>點附近這個距離內的採集點物件才算是這個點的。</summary>
    private const float NodeMatchRange = 3f;

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    /// <summary>這麼久沒有更接近目的地就算卡住。</summary>
    private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(5);

    /// <summary>直線距離小於這個（米）的點：最多走 ShortHopLimit，走不到就在原地試著採。</summary>
    private const float ShortHop = 10f;

    private static readonly TimeSpan ShortHopLimit = TimeSpan.FromSeconds(6);

    /// <summary>水下的種類（全部的點都在水面以下，要潛水）：巨大貝類、茂密的海藻、珊瑚礁。</summary>
    public static readonly uint[] UnderwaterTypes = { 2013187, 2013188, 2013189 };

    private readonly Plugin plugin;
    private readonly Navmesh nav = new();
    private readonly FeatureLog log = FeatureLog.For(Owner);
    private readonly List<IDisposable> leases = new();
    private readonly Routine routine = new();

    private DateTime nextTick;
    private bool subscribed;
    private bool capReached;
    private bool capturing;
    private readonly List<(uint Item, int Count)> chatGains = new();
    private string status = "待命";

    public IslandGather(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private IslandGatherConfig Config => plugin.Config.IslandGather;

    private CoreServices Core => plugin.Core;

    public bool Running { get; private set; }

    public int Gathered { get; private set; }

    public int Loop { get; private set; }

    public int RouteIndex { get; private set; }

    public int RouteLength { get; private set; }

    public DateTime StartedAt { get; private set; }

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

    /// <summary>採集點種類的名稱（EObjName 表）。</summary>
    public static string TypeName(uint eobj) =>
        Services.Data.GetExcelSheet<EObjName>().GetRowOrDefault(eobj)?.Singular.ExtractText() is { Length: > 0 } name ? name : $"#{eobj}";

    public static bool OnIsland => Services.ClientState.TerritoryType == IslandGatherData.Territory;

    /// <summary>目前是不是收穫模式。</summary>
    public static bool InGatherMode => MJIManager.Instance() != null && MJIManager.Instance()->CurrentMode == GatherMode;

    public static string GatherModeName =>
        Services.Data.GetExcelSheet<MJIHudMode>().GetRowOrDefault(GatherMode)?.Name.ExtractText() ?? "收穫";

    // ───────────────────────── 開始／停止 ─────────────────────────

    public void Toggle()
    {
        if (Running) Stop("手動停止");
        else Start();
    }

    public void Start()
    {
        if (Running) return;

        if (!OnIsland)
        {
            Notify.Error("無人島採集", "請先到無人島再開始。");
            return;
        }

        if (Config.Items.Count == 0)
        {
            Notify.Error("無人島採集", "沒有勾選想要的素材。");
            return;
        }

        if (TargetsReached(out _))
        {
            Notify.Error("無人島採集", "勾選的素材都已經到目標數量了。");
            return;
        }

        var types = WantedTypes(out var unknown);
        if (types.Count == 0)
        {
            Notify.Error("無人島採集", $"還不知道{string.Join("、", unknown.Select(IslandPouch.ItemName))}在哪裡採，先按「補齊對應表」。");
            return;
        }

        if (!Begin()) return;

        Exploring = false;
        routine.Start(Main(types));
        log.Write($"===== 開始 素材={string.Join("、", Config.Items.Select(IslandPouch.ItemName))} " +
                  $"採集點={string.Join("、", types.Select(TypeName))} 圈數={(Config.Loops > 0 ? Config.Loops : "不限")} 到上限停={Config.StopAtCap}" +
                  (unknown.Count > 0 ? $" 還不知道在哪裡採={string.Join("、", unknown.Select(IslandPouch.ItemName))}" : string.Empty));
        Status = "開始";
    }

    /// <summary>補齊對應表：還沒記錄過的每種採集點各去採一個（排成最短的一趟），記下會採到什麼。</summary>
    public void StartExplore()
    {
        if (Running) return;

        if (!OnIsland)
        {
            Notify.Error("無人島採集", "請先到無人島再開始。");
            return;
        }

        var missing = MissingTypes();
        if (missing.Count == 0)
        {
            Notify.Error("無人島採集", "每種採集點都記錄過了。");
            return;
        }

        if (!Begin()) return;

        Exploring = true;
        routine.Start(Explore(missing));
        log.Write($"===== 補齊對應表 {missing.Count} 種：{string.Join("、", missing.Select(TypeName))}");
        Status = "補齊對應表";
    }

    /// <summary>拿操作鎖、重設計數、開始聽每幀更新和聊天欄。失敗回傳 false。</summary>
    private bool Begin()
    {
        if (!Core.Lock.TryAcquire(Owner, "無人島採集", () => Stop("從核心能力頁停止")))
        {
            Notify.Error("無人島採集", $"{Core.Lock.Label} 正在執行，先停止它再開始。");
            return false;
        }

        Running = true;
        Gathered = 0;
        Loop = 0;
        RouteIndex = 0;
        RouteLength = 0;
        Tour = Array.Empty<IslandRoute.Stop>();
        capReached = false;
        StartedAt = DateTime.Now;

        if (!subscribed)
        {
            Services.Framework.Update += OnUpdate;
            Services.Chat.ChatMessage += OnChat;
            subscribed = true;
        }

        return true;
    }

    public void Stop(string reason)
    {
        if (!Running) return;

        Running = false;
        nav.Stop();
        routine.Clear();

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
        log.Write($"===== 停止：{reason} 採了 {Gathered} 次 執行 {DateTime.Now - StartedAt:hh\\:mm\\:ss}");
    }

    private void Finish(string reason)
    {
        var after = Config.AfterFinish; // Stop 會把它改回不做事，先記下來
        Stop(reason);
        Core.Finish.Schedule(after, "無人島採集");
        Notify.Info("無人島採集", $"完成：{reason}（採了 {Gathered} 次）");
    }

    private void Fail(string reason)
    {
        Stop(reason);
        Notify.Error("無人島採集", $"停止：{reason}（採了 {Gathered} 次）");
    }

    public void Dispose()
    {
        Stop("插件卸載");
        if (!subscribed) return;

        Services.Framework.Update -= OnUpdate;
        Services.Chat.ChatMessage -= OnChat;
    }

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

        if (!OnIsland && !GameState.BetweenAreas)
        {
            Stop("離開無人島");
            return;
        }

        if (Core.Paused) return;

        try
        {
            if (routine.Advance(now) && Running) Stop("流程結束");
        }
        catch (Exception e)
        {
            log.Write($"錯誤：{e}");
            Services.Log.Error(e, "無人島採集發生錯誤");
            Fail($"發生錯誤：{e.Message}");
        }
    }

    /// <summary>採集時跳出「持有數量已達到上限」。</summary>
    private void OnChat(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
    {
        if (!Running) return;

        // 採集中：聊天欄「獲得了…」裡的道具連結就是這個採集點給的東西
        if (capturing)
        {
            foreach (var payload in message.Payloads.OfType<Dalamud.Game.Text.SeStringHandling.Payloads.ItemPayload>())
            {
                var count = System.Text.RegularExpressions.Regex.Match(message.TextValue, @"[×x](\d+)");
                chatGains.Add((payload.ItemId, count.Success ? int.Parse(count.Groups[1].Value) : 1));
            }
        }

        if (capReached) return;

        var text = Services.Data.GetExcelSheet<LogMessage>().GetRowOrDefault(CapMessage)?.Text.ExtractText();
        if (string.IsNullOrEmpty(text) || !message.TextValue.Contains(text)) return;

        capReached = true;
        log.Write($"聊天欄：{message.TextValue}");
    }

    // ───────────────────────── 主流程 ─────────────────────────

    /// <summary>這次在繞的一圈（照走的順序）。</summary>
    public IReadOnlyList<IslandRoute.Stop> Tour { get; private set; } = Array.Empty<IslandRoute.Stop>();

    /// <summary>正在跑「補齊對應表」。</summary>
    public bool Exploring { get; private set; }

    /// <summary>這個素材到目標了沒（沒設目標 = 沒到）。</summary>
    private bool Reached(uint item, Dictionary<uint, int>? counts) =>
        counts != null && Config.Targets.TryGetValue(item, out var target) && target > 0 && counts.GetValueOrDefault(item) >= target;

    /// <summary>
    /// 勾選的素材都設了目標、也全部到量。沒勾、有沒設目標的、或讀不到庫存時是 false。
    /// detail 是有設目標的那些目前幾個／目標幾個。
    /// </summary>
    public bool TargetsReached(out string detail)
    {
        detail = string.Empty;
        var counts = IslandPouch.Counts();
        if (counts == null || Config.Items.Count == 0) return false;

        var targeted = Config.Items.Where(i => Config.Targets.GetValueOrDefault(i) > 0).ToList();
        detail = string.Join("、", targeted.Select(i => $"{IslandPouch.ItemName(i)} {counts.GetValueOrDefault(i)}/{Config.Targets[i]}"));
        return targeted.Count == Config.Items.Count && targeted.All(i => Reached(i, counts));
    }

    /// <summary>
    /// 要繞的採集點種類：會採到「勾選、還沒到目標」的素材的那些（照對應表）。
    /// unknown = 勾選了、但對應表裡還沒有哪種採集點採過它的素材。
    /// </summary>
    public HashSet<uint> WantedTypes(out List<uint> unknown)
    {
        var counts = IslandPouch.Counts();
        var active = Config.Items.Where(i => !Reached(i, counts)).ToHashSet();
        unknown = active.Where(i => !IslandGatherData.Points.Keys.Any(t => YieldsOf(t).Contains(i))).ToList();
        return IslandGatherData.Points.Keys.Where(t => YieldsOf(t).Overlaps(active)).ToHashSet();
    }

    /// <summary>對應表裡還沒記錄過的採集點種類。</summary>
    public List<uint> MissingTypes() => IslandGatherData.Points.Keys.Where(t => YieldsOf(t).Count == 0).ToList();

    /// <summary>這種採集點會採到的素材：內建的對應表加上執行時新記到的。</summary>
    public HashSet<uint> YieldsOf(uint type)
    {
        var items = IslandGatherData.Yields.TryGetValue(type, out var known) ? new HashSet<uint>(known) : new HashSet<uint>();
        if (Config.Yields.TryGetValue(type, out var learned)) items.UnionWith(learned);
        return items;
    }

    private IEnumerable<object?> Main(HashSet<uint> types)
    {
        var tour = UseTour(types);

        while (Running)
        {
            if (Config.Loops > 0 && Loop >= Config.Loops)
            {
                Finish($"繞完 {Loop} 圈");
                yield break;
            }

            for (RouteIndex = 0; RouteIndex < tour.Count && Running; RouteIndex++)
            {
                var stop = tour[RouteIndex];
                yield return Visit(stop.Type, stop.Point);
                if (!Running) yield break;

                if (Config.StopAtCap && capReached)
                {
                    Finish("持有數量到上限");
                    yield break;
                }

                if (TargetsReached(out var detail))
                {
                    Finish($"勾選的素材都到目標（{detail}）");
                    yield break;
                }

                // 某個素材到目標了：不再為它繞路，用剩下的重排一圈
                var now = WantedTypes(out _);
                if (now.Count > 0 && !now.SetEquals(types))
                {
                    log.Write($"要繞的採集點變了：{string.Join("、", now.Select(TypeName))}，重排路線");
                    types = now;
                    tour = UseTour(types);
                    RouteIndex = -1;
                }
            }

            Loop++;
            log.Write($"繞完第 {Loop} 圈，採了 {Gathered} 次");
        }
    }

    /// <summary>排一圈（從離自己最近的點開始），記成目前這一圈。</summary>
    private List<IslandRoute.Stop> UseTour(HashSet<uint> types)
    {
        var tour = IslandRoute.StartNear(IslandRoute.Plan(types), Services.Objects.LocalPlayer?.Position ?? Vector3.Zero);
        Tour = tour;
        RouteLength = tour.Count;
        log.Write($"一圈 {tour.Count} 個點、約 {IslandRoute.Length(tour):0} 米：" +
                  string.Join(" → ", tour.Select(s => $"{TypeName(s.Type)}{(s.Filler ? "*" : string.Empty)}({s.Point.X:0},{s.Point.Z:0})")));
        return tour;
    }

    /// <summary>
    /// 補齊對應表：每種還沒記錄的採集點各採一個。從自己的位置開始，每次去最近的那一種；
    /// 那種最近的點還沒重生就換同種的下一個近的，最多試 3 個。
    /// </summary>
    private IEnumerable<object?> Explore(List<uint> types)
    {
        var left = new List<uint>(types);
        RouteLength = left.Count;
        RouteIndex = 0;

        while (left.Count > 0 && Running)
        {
            var here = Services.Objects.LocalPlayer?.Position ?? Vector3.Zero;
            var type = left.OrderBy(t => IslandGatherData.Points[t].Min(p => Vector3.DistanceSquared(p, here))).First();
            left.Remove(type);

            var name = TypeName(type);
            var tried = 0;
            foreach (var point in IslandGatherData.Points[type].OrderBy(p => Vector3.DistanceSquared(p, here)))
            {
                if (tried >= 3 || !Running) break;

                // 遠遠看得到還沒重生的，直接換下一個，不算一次
                if (FindNode(point) == null && NotRespawned(point)) continue;
                tried++;

                yield return WaitForGatherMode();
                if (!Running) yield break;

                yield return Travel(point, name, point.Y < 0);
                if (!Running) yield break;
                if (!lastOk) continue;

                yield return DismountNow();
                if (!Running) yield break;

                var node = FindNode(point);
                if (node == null)
                {
                    log.Write($"補齊：{name} ({point.X:0},{point.Y:0},{point.Z:0}) 還沒重生，換下一個");
                    continue;
                }

                yield return Gather(node, type, name);
                break;
            }

            RouteIndex++;
            if (!Config.Yields.ContainsKey(type)) log.Write($"補齊：{name} 這次沒記到");
        }

        if (!Running) yield break;

        var missing = MissingTypes();
        Stop(missing.Count == 0 ? "對應表補齊了" : $"對應表還差 {missing.Count} 種（{string.Join("、", missing.Select(TypeName))}），可以再按一次");
    }

    /// <summary>
    /// 到一個點：過去 → 下坐騎 → 有採集點就採。這個點還沒重生的話，改採附近別的採集點，
    /// 讓「採 11 個、最早那個重生」的計數繼續往前，不然一整圈都會撲空。
    /// </summary>
    private IEnumerable<object?> Visit(uint type, Vector3 point)
    {
        yield return WaitForGatherMode();
        if (!Running) yield break;

        var name = TypeName(type);
        justDismounted = false;

        // 遠遠就看得到這個點、但它還沒重生：不用飛過去，直接改採附近別的
        if (FindNode(point) == null && NotRespawned(point))
        {
            log.Write($"{name} ({point.X:0},{point.Y:0},{point.Z:0}) 還沒重生，不過去");
            yield return GatherSpare();
            yield break;
        }

        // 水下的點：騎著飛行坐騎直接飛進水裡就會自己潛水，到了點旁邊才下坐騎
        yield return Travel(point, name, point.Y < 0);
        if (!Running || !lastOk) yield break;

        yield return DismountNow();
        if (!Running) yield break;

        var node = FindNode(point);
        if (node == null)
        {
            log.Write($"{name} ({point.X:0},{point.Y:0},{point.Z:0}) 沒有可以採的（已經採過、還沒重生） 附近：{Nearby(point)}");
            yield return GatherSpare();
            yield break;
        }

        yield return Gather(node, type, name);
    }

    /// <summary>圈上的點還沒重生：改採附近不在圈上的採集點，讓重生的計數繼續往前。附近沒有就算了。</summary>
    private IEnumerable<object?> GatherSpare()
    {
        var spare = SpareNode();
        if (spare == null) yield break;

        var type = TypeAt(spare.Position);
        var name = type == 0 ? "附近的採集點" : TypeName(type);
        log.Write($"改採附近的 {name} ({spare.Position.X:0},{spare.Position.Y:0},{spare.Position.Z:0}) 距離 {Distance(spare.Position):0} 米");

        yield return Travel(spare.Position, name, spare.Position.Y < 0);
        if (!Running || !lastOk) yield break;

        yield return DismountNow();
        if (!Running) yield break;

        yield return Gather(spare, type, name);
    }

    /// <summary>
    /// 採一個採集點：先在原地試（島上採集點的對話距離比岩石半徑大，硬要走到中心反而會卡在岩石上），
    /// 沒反應才往前靠近再試，最多 3 次。
    /// </summary>
    private IEnumerable<object?> Gather(IGameObject node, uint type, string name)
    {
        Status = $"採集 {name}";
        yield return Routine.Until(() => !Services.Condition[ConditionFlag.Casting] && !Services.Condition[ConditionFlag.Jumping] && !GameState.Mounted, 5);

        // 剛下坐騎時角色還在動作中，這時對話遊戲不理：有下坐騎才停一下
        if (justDismounted) yield return Routine.Delay(0.6);

        // 太遠才先靠近（最多 5 秒）
        if (Distance(node.Position) > FarInteract)
            yield return StepToward(node.Position, 5);

        // 島上平常就可能有幾個「事件中」旗標：只看對話之後才出現的
        var baseline = GameState.ActiveEventFlags();
        var before = IslandPouch.Counts();
        var nodeId = node.GameObjectId;
        var started = false;
        chatGains.Clear();
        capturing = true;

        for (var attempt = 1; attempt <= 3 && !started && Running; attempt++)
        {
            Interact(node);
            yield return Routine.Until(() => Busy(baseline) || !NodeAlive(nodeId), 3);
            started = Busy(baseline) || !NodeAlive(nodeId);
            if (started) break;

            log.Write($"{name} 對話第 {attempt} 次沒反應 距離={Distance(node.Position):0.0} 坐騎={GameState.Mounted} " +
                      $"目標={Services.Targets.Target?.GameObjectId == nodeId} 旗標=[{Flags()}] 模式={CurrentMode}");

            // 沒反應：往採集點靠近一點再試
            if (attempt < 3) yield return StepToward(node.Position, 3);
        }

        yield return Routine.Until(() => !Busy(baseline), 15);

        // 採完之後採集點會淡出，給它一點時間消失
        yield return Routine.Until(() => !NodeAlive(nodeId), 2);
        if (NodeAlive(nodeId))
        {
            capturing = false;
            log.Write($"{name} 對話後還在，可能沒採到");
            yield break;
        }

        Gathered++;

        // 這種採集點會給的東西：以聊天欄「獲得了…」的道具連結為準，沒抓到再看背包數量的變化；記下來給設定頁顯示
        yield return Routine.Delay(0.4);
        capturing = false;
        var got = chatGains.Count > 0
            ? chatGains.GroupBy(g => g.Item % 1_000_000).ToDictionary(g => g.Key, g => g.Sum(x => x.Count))
            : Gains(before, IslandPouch.Counts());
        if (got.Count > 0 && type != 0)
        {
            if (!Config.Yields.TryGetValue(type, out var known)) Config.Yields[type] = known = new HashSet<uint>();
            // 不能用 Any(known.Add)：Any 碰到第一個 true 就停，後面的不會加進去
            var added = got.Keys.Count(known.Add);
            if (added > 0) plugin.Save();
        }

        log.Write($"採了 {name}（第 {Gathered} 次）" +
                  (got.Count > 0 ? "：" + string.Join("、", got.Select(g => $"{IslandPouch.ItemName(g.Key)}+{g.Value}")) : string.Empty));
    }

    /// <summary>往 target 靠近，最多 seconds 秒（不上坐騎；在水裡用潛水的路線）。</summary>
    private IEnumerable<object?> StepToward(Vector3 target, double seconds)
    {
        if (!nav.MoveCloseTo(target, InWater, 1f)) yield break;

        yield return Routine.Until(() => nav.Busy, 2);
        yield return Routine.Until(() => !nav.Busy, seconds);
        if (nav.Busy) nav.Stop();
    }

    private static bool InWater => Services.Condition[ConditionFlag.Diving] || Services.Condition[ConditionFlag.Swimming];

    /// <summary>前後兩份背包數量相比，變多的道具和多了幾個。任一份讀不到就是空的。</summary>
    private static Dictionary<uint, int> Gains(Dictionary<uint, int>? before, Dictionary<uint, int>? after)
    {
        var gains = new Dictionary<uint, int>();
        if (before == null || after == null) return gains;

        foreach (var (item, count) in after)
        {
            var diff = count - before.GetValueOrDefault(item);
            if (diff > 0) gains[item] = diff;
        }

        return gains;
    }

    /// <summary>不是收穫模式就原地等（模式要自己切）。</summary>
    private IEnumerable<object?> WaitForGatherMode()
    {
        if (InGatherMode) yield break;

        Status = $"請切換到「{GatherModeName}」模式";
        log.Write($"不是{GatherModeName}模式（目前 {CurrentMode}），等切換");
        Notify.Attention("無人島採集", $"請切換到「{GatherModeName}」模式，切好會自動繼續。");
        yield return Routine.Until(() => InGatherMode, 300);
        if (!InGatherMode) Fail($"等了 5 分鐘還不是「{GatherModeName}」模式");
    }

    private bool lastOk;

    /// <summary>這個點有按過下坐騎（採集前要等動作做完）。</summary>
    private bool justDismounted;

    /// <summary>
    /// 走或飛到點旁邊。卡住（5 秒沒有更接近）時：第 1 次跳一下重新找路，第 2、3 次先往側邊繞一段再過去；
    /// 很近的點繞不過去就在原地試著採，遠的就略過這個點。
    /// 水下的點：不管遠近都先騎上飛行坐騎，直接飛進水裡（遊戲會自己切成潛水），不在水面下坐騎。
    /// </summary>
    private IEnumerable<object?> Travel(Vector3 point, string name, bool underwater = false)
    {
        lastOk = true;
        if (Distance(point) <= GatherRange) yield break;

        if (!nav.Ready)
        {
            Status = "等待 vnavmesh";
            yield return Routine.Until(() => nav.Ready, 60);
            if (!nav.Ready)
            {
                Fail("vnavmesh 不能用");
                yield break;
            }
        }

        // 要上坐騎的話：先出發，邊走邊叫（優先用隨機飛行坐騎，一定能飛、能潛水），騎上了再改走飛行路線。
        // 邊走邊叫被打斷（2.5 秒還沒騎上、還很遠）才停下來叫
        var mount = 0u;
        if (!GameState.Mounted && (underwater || Distance(point) > Config.MountDistance))
        {
            mount = CanUse(ActionType.GeneralAction, FlyingRoulette) ? FlyingRoulette
                : CanUse(ActionType.GeneralAction, MountRoulette) ? MountRoulette : 0u;
        }

        var mountPressedAt = DateTime.MinValue;
        var mountedStanding = false;

        // 水下的點：地面路線到不了，一定要先騎上去才出發
        if (underwater && mount != 0)
        {
            mountedStanding = true;
            Status = "上坐騎";
            yield return Routine.Until(() => !Services.Condition[ConditionFlag.Casting] && !Services.Condition[ConditionFlag.Jumping], 5);
            UseGeneral(mount);
            yield return Routine.Until(() => GameState.Mounted, 4);
            if (!GameState.Mounted)
                log.Write($"{name} 在水下：上不了坐騎（游泳={Services.Condition[ConditionFlag.Swimming]} 潛水={Services.Condition[ConditionFlag.Diving]}），用游的試試");
            mount = 0;
        }

        for (var attempt = 1; attempt <= 3 && Running; attempt++)
        {
            // 騎著能飛、或已經在水裡（潛水的移動跟飛行一樣）就走立體路線
            var fly = (GameState.Mounted && (Control.CanFly || underwater)) || InWater;

            // 第 2、3 次：先往側邊（右、左輪流）繞一段，從別的方向過去
            if (attempt > 1 && Services.Objects.LocalPlayer is { } me)
            {
                var detour = Detour(me.Position, point, attempt == 2 ? 1 : -1, fly);
                Status = $"繞路前往 {name}";
                log.Write($"前往 {name} 繞路（第 {attempt} 次）經過 ({detour.X:0},{detour.Y:0},{detour.Z:0})");
                if (nav.MoveCloseTo(detour, fly, 1f))
                {
                    yield return Routine.Until(() => nav.Busy, 2);
                    yield return Routine.Until(() => !nav.Busy, 6);
                    if (nav.Busy) nav.Stop();
                }
            }

            Status = $"前往 {name}（{Distance(point):0} 米）";
            if (!nav.MoveCloseTo(point, fly, GatherRange - 1f))
            {
                yield return Routine.Delay(1);
                nav.MoveCloseTo(point, fly, GatherRange - 1f);
            }

            yield return Routine.Until(() => nav.Busy, 3);

            if (mount != 0 && !GameState.Mounted && mountPressedAt == DateTime.MinValue)
            {
                UseGeneral(mount);
                mountPressedAt = DateTime.Now;
                Status = $"邊走邊上坐騎，前往 {name}";
            }

            var pathFly = fly;

            // 卡住看的是「有沒有更接近目的地」，不是「有沒有在動」：在岩石旁邊繞圈也算卡住
            var best = Distance(point);
            var since = DateTime.Now;
            var shortHop = best < ShortHop;
            var deadline = DateTime.Now + (shortHop ? ShortHopLimit : TimeSpan.FromMinutes(2));
            var stalled = false;

            while (nav.Busy && DateTime.Now < deadline && Running)
            {
                var now = Distance(point);
                if (now <= GatherRange) break; // 夠近就停，不用走到正中間

                // vnavmesh 還在算路徑（遠的飛行路線要算比較久）：還沒開始走，不算卡住
                if (!nav.Moving)
                {
                    since = DateTime.Now;
                    yield return Routine.Delay(0.3);
                    continue;
                }

                // 騎上了：改走飛行路線
                if (!pathFly && GameState.Mounted && (Control.CanFly || underwater))
                {
                    nav.MoveCloseTo(point, true, GatherRange - 1f);
                    pathFly = true;
                    best = now;
                    since = DateTime.Now;
                    Status = $"前往 {name}（{now:0} 米）";
                }

                // 邊走邊叫沒成功（被移動打斷）、還很遠：停下來叫
                if (mount != 0 && !GameState.Mounted && !mountedStanding && mountPressedAt != DateTime.MinValue
                    && DateTime.Now - mountPressedAt > TimeSpan.FromSeconds(2.5) && (underwater || now > Config.MountDistance))
                {
                    mountedStanding = true;
                    nav.Stop();
                    log.Write($"邊走邊上坐騎沒成功，停下來叫（還有 {now:0} 米）");
                    Status = "上坐騎";
                    yield return Routine.Until(() => !Services.Condition[ConditionFlag.Casting] && !Services.Condition[ConditionFlag.Jumping], 3);
                    UseGeneral(mount);
                    yield return Routine.Until(() => GameState.Mounted, 4);

                    if (underwater && !GameState.Mounted)
                        log.Write($"{name} 在水下：上不了坐騎（游泳={Services.Condition[ConditionFlag.Swimming]} 潛水={Services.Condition[ConditionFlag.Diving]}），用游的試試");

                    pathFly = (GameState.Mounted && (Control.CanFly || underwater)) || InWater;
                    nav.MoveCloseTo(point, pathFly, GatherRange - 1f);
                    yield return Routine.Until(() => nav.Busy, 3);
                    best = Distance(point);
                    since = DateTime.Now;
                    continue;
                }

                if (now < best - 1f)
                {
                    best = now;
                    since = DateTime.Now;
                }
                else if (DateTime.Now - since > StallAfter)
                {
                    stalled = true;
                    break;
                }

                yield return Routine.Delay(0.3);
            }

            if (Distance(point) <= GatherRange + 1f)
            {
                if (nav.Busy) nav.Stop();
                yield break;
            }

            nav.Stop();
            log.Write($"前往 {name} ({point.X:0},{point.Y:0},{point.Z:0}) {(stalled ? "卡住" : "沒到")}，還差 {Distance(point):0.0} 米（第 {attempt} 次）");
            if (attempt == 1)
            {
                UseGeneral(Jump);
                yield return Routine.Delay(1);
            }
        }

        // 很近的點繞不過去：從這裡試著採（採不到會在 log 寫距離）
        if (Distance(point) <= ShortHop)
        {
            log.Write($"前往 {name} 繞不過去，在 {Distance(point):0.0} 米外直接試著採");
            yield break;
        }

        lastOk = false;
        log.Write($"略過 {name} ({point.X:0},{point.Y:0},{point.Z:0})：到不了");
    }

    /// <summary>從 from 往 to 的方向，往側邊（side = 1 右、-1 左）偏 6 米、往前 4 米的繞路點；飛的時候再高一點。</summary>
    private static Vector3 Detour(Vector3 from, Vector3 to, int side, bool fly)
    {
        var dir = new Vector2(to.X - from.X, to.Z - from.Z);
        dir = dir.LengthSquared() < 0.01f ? new Vector2(1, 0) : Vector2.Normalize(dir);
        var right = new Vector2(-dir.Y, dir.X) * side;
        var p = new Vector2(from.X, from.Z) + right * 6f + dir * 4f;
        return new Vector3(p.X, from.Y + (fly ? 4f : 0f), p.Y);
    }

    /// <summary>
    /// 下坐騎。飛行中按一次是降落、落地再按一次才下來，所以隔一秒重按。
    /// 底下不能降落（停在半空中）時，往旁邊挪一步（vnavmesh 找附近能站的地面）再按。
    /// </summary>
    private IEnumerable<object?> DismountNow()
    {
        var aside = 0;
        for (var i = 0; i < 8 && GameState.Mounted && Running; i++)
        {
            Status = "下坐騎";
            UseGeneral(Dismount);
            justDismounted = true;
            yield return Routine.Until(() => !GameState.Mounted, 1);
            if (!GameState.Mounted) break;

            // 按了兩次還在飛：這裡降落不了，往旁邊挪
            if (i % 2 == 1 && Services.Condition[ConditionFlag.InFlight] && Services.Objects.LocalPlayer is { } me)
            {
                var spot = LandingSpot(me.Position, aside++);
                log.Write($"下不了坐騎（位置 {me.Position.X:0},{me.Position.Y:0},{me.Position.Z:0}），往旁邊 ({spot.X:0},{spot.Y:0},{spot.Z:0}) 再下");
                if (nav.MoveCloseTo(spot, true, 0.5f))
                {
                    yield return Routine.Until(() => nav.Busy, 2);
                    yield return Routine.Until(() => !nav.Busy, 5);
                    if (nav.Busy) nav.Stop();
                }
            }
        }

        if (GameState.Mounted) log.Write("下不了坐騎，直接試著採");
        yield return Routine.Until(() => !GameState.BetweenAreas && !Services.Condition[ConditionFlag.Jumping], 2);
    }

    /// <summary>旁邊能降落的地面：輪流試前後左右 3 米（第二輪 6 米），請 vnavmesh 找那附近的地面點。</summary>
    private Vector3 LandingSpot(Vector3 here, int index)
    {
        var offsets = new[] { new Vector3(3, 0, 0), new Vector3(0, 0, 3), new Vector3(-3, 0, 0), new Vector3(0, 0, -3) };
        var offset = offsets[index % offsets.Length] * (1 + index / offsets.Length);
        return nav.LandingPoint(here + offset, 3f) ?? here + offset;
    }

    // ───────────────────────── 碰遊戲的部分 ─────────────────────────

    /// <summary>正在採（動作、事件中、跳）。</summary>
    private static bool Busy(HashSet<int> baseline) =>
        GameState.InEventBeyond(baseline) || Services.Objects.LocalPlayer is { IsCasting: true }
                          || Services.Condition[ConditionFlag.Jumping] || Services.Condition[ConditionFlag.Casting];

    private static float Distance(Vector3 position) =>
        Services.Objects.LocalPlayer is { } player ? Vector3.Distance(player.Position, position) : float.MaxValue;

    /// <summary>
    /// 點上還能採（可以選取）的採集點。島上的採集點物件類型是 CardStand；
    /// 它的物件編號跟座標資料裡的種類編號（名稱表的編號）不一樣，所以只看類型和位置（同 DR）。
    /// </summary>
    private static IGameObject? FindNode(Vector3 point) =>
        Services.Objects
            .Where(o => o.IsTargetable
                        && o.ObjectKind == ObjectKind.CardStand
                        && Vector3.Distance(o.Position, point) <= NodeMatchRange)
            .OrderBy(o => Vector3.Distance(o.Position, point))
            .FirstOrDefault();

    /// <summary>
    /// 圈上的點還沒重生時改採的：離自己 SpareRange 米內、能採、不在這一圈上的採集點，挑最近的。
    /// 不挑圈上的點，免得等一下輪到它時又撲空。
    /// </summary>
    private IGameObject? SpareNode() =>
        Services.Objects
            .Where(o => o.IsTargetable
                        && o.ObjectKind == ObjectKind.CardStand
                        && Distance(o.Position) <= SpareRange
                        && !Tour.Any(s => Vector3.Distance(s.Point, o.Position) <= NodeMatchRange))
            .OrderBy(o => Distance(o.Position))
            .FirstOrDefault();

    /// <summary>這個位置是哪一種採集點（座標資料裡 3 米內的點）；查不到是 0。</summary>
    private static uint TypeAt(Vector3 position)
    {
        foreach (var (type, points) in IslandGatherData.Points)
        {
            if (points.Any(p => Vector3.Distance(p, position) <= NodeMatchRange)) return type;
        }

        return 0;
    }

    /// <summary>點上看得到採集點、但還不能選（採過、還沒重生）。太遠看不到時是 false，要過去才知道。</summary>
    private static bool NotRespawned(Vector3 point) =>
        Services.Objects.Any(o => o.ObjectKind == ObjectKind.CardStand && !o.IsTargetable
                                  && Vector3.Distance(o.Position, point) <= NodeMatchRange);

    private static bool NodeAlive(ulong id) => Services.Objects.SearchById(id) is { IsTargetable: true };

    /// <summary>目前開著的條件旗標（給 log 查原因）。</summary>
    private static string Flags()
    {
        var on = new List<string>();
        foreach (ConditionFlag flag in Enum.GetValues(typeof(ConditionFlag)))
        {
            if (flag != ConditionFlag.None && Services.Condition[flag]) on.Add($"{flag}({(int)flag})");
        }

        return string.Join(" ", on);
    }

    /// <summary>點附近 10 米內的物件（種類、編號、能不能選），找不到採集點時寫進 log 查原因。</summary>
    private static string Nearby(Vector3 point)
    {
        var near = Services.Objects
            .Where(o => o.ObjectKind != ObjectKind.Player && Vector3.Distance(o.Position, point) <= 10f)
            .Select(o => $"{o.ObjectKind}#{((CsGameObject*)o.Address)->BaseId}{(o.IsTargetable ? string.Empty : "(不能選)")}@{Vector3.Distance(o.Position, point):0.0}")
            .ToList();
        return near.Count == 0 ? "沒有物件" : string.Join(" ", near);
    }

    private static void Interact(IGameObject node)
    {
        Services.Targets.Target = node;
        TargetSystem.Instance()->InteractWithObject((CsGameObject*)node.Address);
    }

    private static uint CurrentMode => MJIManager.Instance() == null ? 0 : MJIManager.Instance()->CurrentMode;

    /// <summary>按一般動作（上坐騎、下坐騎、跳），跟按快捷列一樣。</summary>
    private static void UseGeneral(uint id) => ActionManager.Instance()->UseAction(ActionType.GeneralAction, id);

    private static bool CanUse(ActionType type, uint id) =>
        ActionManager.Instance()->GetActionStatus(type, id) == 0;
}
