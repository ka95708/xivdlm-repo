using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherKit.Core;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using CsGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace AetherKit.Game;

/// <summary>
/// 高山茶理符（TsaiTouVounouLeve，原本的 snd-scripts/leve_TsaiTouVounou.lua）：原地重複「向格里格領「製作委託：治癒身心的茶」→ 交給阿爾德伊恩」，
/// 高山茶要自己先做好放在身上。不走路、不傳送：人要站在兩個 NPC 都能對話的位置。
///
/// 開場：換成烹調師（找烹調師的套裝）→ 確認兩個 NPC 都在對話距離內。
/// 每一次：領（和發行人對話 → 選「製作任務」→ 理符視窗開著時告訴遊戲要接哪一個 → 關視窗）
///        → 交（和委託人對話 → 有好幾個理符時選名稱對的 → 交道具視窗交給核心能力「交道具」→ 優質道具確認按「是」）。
/// 受理限額用完、道具不夠一次、或交滿設定的次數就算完成。
/// </summary>
public sealed unsafe class TsaiTouVounouLeve : IDisposable
{
    /// <summary>借用核心能力、拿操作鎖、寫 log 用的名字。和模組 Id 一樣。</summary>
    public const string Owner = "tsai-tou-vounou-leve";

    // 遊戲文字（Addon 表）
    private const uint TradecraftOption = 596;   // 「製作任務」
    private const uint CancelOption = 581;       // 「取消」
    private const uint HqPrompt = 102434;        // 「確定要交易優質道具嗎？」
    private const uint LowLevelPrompt = 606;     // 「以非推薦等級完成任務，理符的經驗值報酬會減少。仍然要領取嗎？」

    /// <summary>固定的理符：製作委託：治癒身心的茶（舊薩雷安，烹調師 88，交高山茶 ×3）。</summary>
    public const uint LeveId = 1647;

    /// <summary>站的位置離 NPC 超過這個距離就不開始（遊戲的對話距離大約 6 米）。</summary>
    private const float InteractDistance = 6f;

    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);

    private readonly Plugin plugin;
    private readonly FeatureLog log = FeatureLog.For(Owner);
    private readonly List<IDisposable> leases = new();
    private readonly Routine routine = new();

    private DateTime nextTick;
    private bool subscribed;
    private string status = "待命";

    public TsaiTouVounouLeve(Plugin plugin)
    {
        this.plugin = plugin;
    }

    private TsaiTouVounouLeveConfig Config => plugin.Config.TsaiTouVounouLeve;

    private CoreServices Core => plugin.Core;

    public bool Running { get; private set; }

    public int Completed { get; private set; }

    public DateTime StartedAt { get; private set; }

    /// <summary>這次在跑的理符。</summary>
    public LeveData.Info? Leve { get; private set; }

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

    // ───────────────────────── 遊戲狀態 ─────────────────────────

    /// <summary>剩下的理符受理限額。</summary>
    public static int Allowances => QuestManager.Instance()->NumLeveAllowances;

    /// <summary>這個理符已經領了、還沒交。</summary>
    public static bool Accepted(uint leveId) => QuestManager.Instance()->GetLeveQuestById((ushort)leveId) != null;

    /// <summary>身上這個道具的數量（普通＋優質）。</summary>
    public static int ItemCount(uint itemId)
    {
        var inventory = InventoryManager.Instance();
        return inventory->GetInventoryItemCount(itemId) + inventory->GetInventoryItemCount(itemId, true);
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
            Notify.Error("高山茶理符", "還沒登入遊戲。");
            return;
        }

        if (Services.Condition[ConditionFlag.BoundByDuty])
        {
            Notify.Error("高山茶理符", "請在副本外開始。");
            return;
        }

        var leve = LeveData.Get(LeveId);
        if (leve == null)
        {
            Notify.Error("高山茶理符", $"讀不到理符 #{LeveId}。");
            return;
        }

        if (!Core.Lock.TryAcquire(Owner, "高山茶理符", () => Stop("從核心能力頁停止")))
        {
            Notify.Error("高山茶理符", $"{Core.Lock.Label} 正在執行，先停止它再開始。");
            return;
        }

        leases.Add(Core.Talk.Acquire(Owner));
        leases.Add(Core.Handin.Acquire(Owner));
        leases.Add(Core.Quest.Acquire(Owner));
        leases.Add(Core.Confirm.Register(Owner, ConfirmRule.YesnoWithText("高山茶理符：交優質道具", HqPrompt)));
        leases.Add(Core.Confirm.Register(Owner, ConfirmRule.YesnoWithText("高山茶理符：非推薦等級仍領取", LowLevelPrompt)));

        Leve = leve;
        Running = true;
        Completed = 0;
        StartedAt = DateTime.Now;
        routine.Start(Main(leve));

        log.Write($"===== 開始 理符={leve.Name} #{leve.Id} 職業={leve.JobName} Lv{leve.Level} " +
                  $"道具={leve.ItemName}×{leve.ItemCount}（身上 {ItemCount(leve.ItemId)}） " +
                  $"發行人={leve.IssuerName} 委託人={leve.ClientName} 限額={Allowances} 次數={(Config.Rounds > 0 ? Config.Rounds : "不限")}");
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
        log.Write($"===== 停止：{reason} 交了 {Completed} 次 執行 {DateTime.Now - StartedAt:hh\\:mm\\:ss}");
    }

    /// <summary>正常做完：停止，再照設定登出或關閉遊戲。</summary>
    private void Finish(string reason)
    {
        var after = Config.AfterFinish; // Stop 會把它改回不做事，先記下來
        Stop(reason);
        Core.Finish.Schedule(after, "高山茶理符");
        Notify.Info("高山茶理符", $"完成：{reason}（交了 {Completed} 次）");
    }

    /// <summary>出錯停下來：使用者要知道，跳錯誤通知。</summary>
    private void Fail(string reason)
    {
        Stop(reason);
        Notify.Error("高山茶理符", $"停止：{reason}（交了 {Completed} 次）");
    }

    public void Dispose()
    {
        Stop("插件卸載");
        if (subscribed) Services.Framework.Update -= OnUpdate;
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

        if (Core.Paused) return;

        try
        {
            if (routine.Advance(now) && Running) Stop("流程結束");
        }
        catch (Exception e)
        {
            log.Write($"錯誤：{e}");
            Services.Log.Error(e, "高山茶理符發生錯誤");
            Fail($"發生錯誤：{e.Message}");
        }
    }

    // ───────────────────────── 主流程 ─────────────────────────

    private IEnumerable<object?> Main(LeveData.Info leve)
    {
        yield return Prepare(leve);
        if (!Running) yield break;

        while (Running)
        {
            if (Config.Rounds > 0 && Completed >= Config.Rounds)
            {
                Finish($"交滿 {Completed} 次");
                yield break;
            }

            var have = ItemCount(leve.ItemId);
            if (have < leve.ItemCount)
            {
                Finish($"{leve.ItemName} 不夠交一次（身上 {have}，要 {leve.ItemCount}）");
                yield break;
            }

            if (!Accepted(leve.Id))
            {
                if (Allowances <= 0)
                {
                    Finish("理符受理限額用完");
                    yield break;
                }

                yield return Accept(leve);
                if (!Running) yield break;
            }

            yield return TurnIn(leve);
            if (!Running) yield break;

            Completed++;
            log.Write($"第 {Completed} 次完成 身上 {leve.ItemName}={ItemCount(leve.ItemId)} 限額={Allowances}");
        }
    }

    /// <summary>開場一次：換職業、到那張地圖、走到發行人旁邊。</summary>
    private IEnumerable<object?> Prepare(LeveData.Info leve)
    {
        Status = "等待角色就緒";
        yield return Routine.Until(() => GameState.Idle, 60);

        if (Job != leve.Job)
        {
            var index = FindGearset(leve.Job);
            if (index < 0)
            {
                Fail($"找不到{leve.JobName}的套裝");
                yield break;
            }

            Status = $"換成{leve.JobName}";
            plugin.Executor.Enqueue($"/gearset change {index + 1}");
            yield return Routine.Until(() => Job == leve.Job, 10);
            if (Job != leve.Job)
            {
                Fail($"換套裝後還不是{leve.JobName}");
                yield break;
            }

            yield return Routine.Delay(1);
        }

        var level = Services.Objects.LocalPlayer?.Level ?? 0;
        if (level < leve.Level)
        {
            Fail($"{leve.JobName} {level} 級，這個理符要 {leve.Level} 級");
            yield break;
        }

        // 不走路：兩個 NPC 都要在對話距離內
        var far = new[] { (leve.IssuerNpc, leve.IssuerName), (leve.ClientNpc, leve.ClientName) }
            .Select(n => (n.Item2, Distance: NpcDistance(n.Item1)))
            .Where(n => n.Distance > InteractDistance)
            .ToList();
        if (far.Count > 0)
        {
            log.Write("離 NPC 太遠：" + string.Join("、", far.Select(n => float.IsPositiveInfinity(n.Distance) ? $"{n.Item1}（附近沒有）" : $"{n.Item1} {n.Distance:0.0} 米")));
            Fail($"請站到 {leve.IssuerName} 和 {leve.ClientName} 都能對話的位置再開始（{string.Join("、", far.Select(n => n.Item1))} 太遠）");
        }
    }

    /// <summary>向發行人領理符。最多試 3 次。</summary>
    private IEnumerable<object?> Accept(LeveData.Info leve)
    {
        for (var attempt = 1; attempt <= 3 && Running; attempt++)
        {
            Status = $"向 {leve.IssuerName} 領理符";
            yield return Routine.Until(() => GameState.Idle, 10);

            if (!Interact(leve.IssuerNpc))
            {
                Fail($"附近找不到 {leve.IssuerName}");
                yield break;
            }

            yield return Routine.Until(() => Addons.IsOpen("SelectString") || Addons.IsOpen("GuildLeve"), 10);

            if (Addons.IsOpen("SelectString") && !Addons.ClickSelectString(Addons.Text(TradecraftOption)))
            {
                log.Write($"選單沒有「{Addons.Text(TradecraftOption)}」：{string.Join(" / ", Addons.SelectStringEntries())}");
                CloseMenus();
                yield return Routine.Until(() => GameState.Idle, 5);
                continue;
            }

            yield return Routine.Until(() => Addons.IsOpen("GuildLeve"), 5);
            if (!Addons.IsOpen("GuildLeve"))
            {
                log.Write($"理符視窗沒有打開（第 {attempt} 次）");
                CloseMenus();
                yield return Routine.Until(() => GameState.Idle, 5);
                continue;
            }

            yield return Routine.Delay(0.5);

            var before = Allowances;
            SendAccept(leve.Id);
            yield return Routine.Until(() => Accepted(leve.Id), 5);

            // 關掉理符視窗，回到選單後選「取消」結束對話
            CloseLeveWindow();
            yield return Routine.Until(() => Addons.IsOpen("SelectString") || GameState.Idle, 3);
            Addons.ClickSelectString(Addons.Text(CancelOption));
            yield return Routine.Until(() => GameState.Idle, 5);

            if (Accepted(leve.Id))
            {
                log.Write($"領了 {leve.Name} 限額 {before}→{Allowances}");
                yield break;
            }

            log.Write($"沒有領到（第 {attempt} 次）");
            yield return Routine.Delay(1);
        }

        if (Running) Fail($"向 {leve.IssuerName} 領不到「{leve.Name}」");
    }

    /// <summary>交給委託人。最多試 3 次。</summary>
    private IEnumerable<object?> TurnIn(LeveData.Info leve)
    {
        for (var attempt = 1; attempt <= 3 && Running; attempt++)
        {
            Status = $"交給 {leve.ClientName}";
            yield return Routine.Until(() => GameState.Idle, 10);

            var before = ItemCount(leve.ItemId);
            if (!Interact(leve.ClientNpc))
            {
                Fail($"附近找不到 {leve.ClientName}");
                yield break;
            }

            // 對話、交道具、確認都交給核心能力；這裡只處理「交哪一個理符」的選單，等到理符消失
            var deadline = DateTime.Now.AddSeconds(30);
            var picked = false;
            while (Accepted(leve.Id) && DateTime.Now < deadline && Running)
            {
                if (!picked && Addons.IsOpen("SelectIconString"))
                {
                    picked = Addons.ClickSelectIconString(leve.Name);
                    if (!picked)
                    {
                        log.Write($"交理符選單沒有「{leve.Name}」：{string.Join(" / ", Addons.SelectIconStringEntries())}");
                        break;
                    }
                }

                yield return Routine.Delay(0.2);
            }

            yield return Routine.Until(() => GameState.Idle, 10);

            if (!Accepted(leve.Id))
            {
                log.Write($"交出 {leve.Name}：{leve.ItemName} {before}→{ItemCount(leve.ItemId)}");
                yield break;
            }

            log.Write($"沒有交出去（第 {attempt} 次）");
            CloseMenus();
            yield return Routine.Until(() => GameState.Idle, 5);
            yield return Routine.Delay(1);
        }

        if (Running) Fail($"交不出「{leve.Name}」");
    }

    // ───────────────────────── 碰遊戲的部分 ─────────────────────────

    private static uint Job => Services.Objects.LocalPlayer?.ClassJob.RowId ?? 0;

    /// <summary>離這個 NPC 多遠；附近沒有就是無限大。</summary>
    private static float NpcDistance(uint npcId)
    {
        var player = Services.Objects.LocalPlayer;
        var npc = Services.Objects.FirstOrDefault(o => ((CsGameObject*)o.Address)->BaseId == npcId);
        return player == null || npc == null ? float.PositiveInfinity : Vector3.Distance(player.Position, npc.Position);
    }

    /// <summary>選中這個 NPC 並對話（跟按確認鍵一樣）。附近找不到回傳 false。</summary>
    private static bool Interact(uint npcId)
    {
        var npc = Services.Objects.FirstOrDefault(o => o.IsTargetable && ((CsGameObject*)o.Address)->BaseId == npcId);
        if (npc == null) return false;

        Services.Targets.Target = npc;
        TargetSystem.Instance()->InteractWithObject((CsGameObject*)npc.Address);
        return true;
    }

    /// <summary>
    /// 理符視窗開著時告訴遊戲要接哪一個（同 DR：AgentLeveQuest 事件 [3, 理符編號]）。
    /// 視窗清單是虛擬化的、項目沒有固定位置可以點，這是遊戲自己在清單點一項時走的路。
    /// </summary>
    private static void SendAccept(uint leveId) =>
        Addons.SendAgentEvent(AgentId.LeveQuest, 0, 3, leveId); // [遊戲函式]

    private static void CloseLeveWindow()
    {
        var guildLeve = Addons.Get("GuildLeve");
        if (guildLeve != null) guildLeve->Close(true);
    }

    /// <summary>出錯時把可能卡著的選單、理符視窗都關掉。</summary>
    private static void CloseMenus()
    {
        CloseLeveWindow();

        foreach (var name in new[] { "SelectIconString", "SelectString" })
        {
            var addon = Addons.Get(name);
            if (addon != null) Addons.Fire(addon, -1);
        }
    }

    /// <summary>這個職業的第一個套裝；沒有回傳 -1。</summary>
    private static int FindGearset(uint job)
    {
        var module = RaptureGearsetModule.Instance();
        for (var i = 0; i < 100; i++)
        {
            if (!module->IsValidGearset(i)) continue;

            var entry = module->GetGearset(i);
            if (entry != null && entry->ClassJob == job) return i;
        }

        return -1;
    }
}
