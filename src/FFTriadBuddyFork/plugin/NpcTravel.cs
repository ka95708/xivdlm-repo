using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc.Exceptions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using System;
using System.Linq;
using System.Numerics;

namespace TriadBuddyPlugin
{
    /// <summary>
    /// 前往幻卡 NPC（fork 新增）。做法參考 GatherBuddyReborn 的自動採集移動：
    ///   1. 不在 NPC 的區域 → 傳送到該區域離 NPC 最近、已登記的以太之光（遊戲內建傳送）。
    ///      區域裡只有城內以太之光 → 先傳送到主城以太之光，再用 Lifestream 搭城內傳送。
    ///   2. 距離夠遠就上坐騎（指定坐騎或坐騎隨機），這個區域風脈都解完就用飛的。
    ///   3. 用 vnavmesh 前往 NPC，到了 3 米內就停下、降落、下坐騎，並選取 NPC。
    /// 隨時可以按停止；進入戰鬥、等太久、少了需要的插件都會停下來並說明。
    /// </summary>
    public sealed unsafe class NpcTravel : IDisposable
    {
        private enum Step
        {
            Idle,
            Teleporting,
            Aethernet,
            WaitNavReady,
            Mounting,
            Moving,
            Dismounting,
        }

        private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(3);
        private const float ArriveRange = 3.0f;
        private const float MountMinDistance = 30.0f;

        // 一般動作「坐騎隨機」（GatherBuddyReborn 用的編號）
        private const uint MountRouletteActionId = 24;

        private Step step = Step.Idle;
        private DateTime stepStarted;
        private DateTime nextCheck;
        private bool actionIssued;
        private bool mountTried;

        private GameNpcInfo? target;
        private string targetName = string.Empty;
        private Vector3 destination;
        private string aethernetName = string.Empty;
        private uint aethernetTerritory;

        public string Status { get; private set; } = string.Empty;
        public bool Running => step != Step.Idle;
        public int TargetNpcId => target?.npcId ?? -1;

        /// <summary>最後一次前往的 NPC，停止後還要在那個 NPC 底下顯示結果。</summary>
        public int LastNpcId { get; private set; } = -1;

        public void Dispose() => Stop(null);

        // ─────────────────────────────────────────────────────────

        public void Start(GameNpcInfo npc, string name)
        {
            Stop(null);
            LastNpcId = npc.npcId;
            mountTried = false;

            if (npc.WorldPos == null || npc.TerritoryId == 0)
            {
                Fail($"沒有 {name} 的位置資料");
                return;
            }

            if (Service.condition[ConditionFlag.InCombat])
            {
                Fail("戰鬥中不能移動");
                return;
            }

            target = npc;
            targetName = name;
            destination = npc.WorldPos.Value;

            if (Service.clientState.TerritoryType == npc.TerritoryId)
            {
                Enter(Step.WaitNavReady, "準備導航…");
                return;
            }

            if (!StartTeleport(npc))
            {
                target = null;
            }
        }

        public void Stop(string? reason)
        {
            if (step is Step.Moving or Step.Mounting)
            {
                Vnav.Stop();
            }

            if (step == Step.Aethernet && actionIssued)
            {
                TryIpc(() => Service.pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction());
            }

            step = Step.Idle;
            actionIssued = false;
            target = null;
            if (reason != null)
            {
                Status = reason;
            }
        }

        private void Enter(Step next, string status, double settleSeconds = 0.5)
        {
            step = next;
            stepStarted = DateTime.Now;
            nextCheck = DateTime.Now + TimeSpan.FromSeconds(settleSeconds);
            actionIssued = false;
            Status = status;
        }

        private void Fail(string reason)
        {
            Stop(null);
            Status = $"前往失敗：{reason}";
            Service.chatGui.PrintError($"[TriadBuddy] 前往 NPC 失敗：{reason}");
        }

        // ─────────────────────────────────────────────────────────
        // 傳送
        // ─────────────────────────────────────────────────────────

        private bool StartTeleport(GameNpcInfo npc)
        {
            var sheet = Service.dataManager.GetExcelSheet<Aetheryte>();
            var npcPos = new Vector2(npc.WorldPos!.Value.X, npc.WorldPos!.Value.Z);

            // 1. 同區域、已登記的主以太之光，挑離 NPC 最近的
            var direct = sheet
                .Where(a => a.IsAetheryte && a.Territory.RowId == npc.TerritoryId && IsAttuned(a.RowId))
                .OrderBy(a => Distance(a, npcPos))
                .FirstOrDefault();

            if (direct.RowId != 0)
            {
                aethernetName = string.Empty;
                return Teleport(direct.RowId, $"傳送到 {direct.PlaceName.ValueNullable?.Name.ExtractText()}…");
            }

            // 2. 只有城內以太之光：先傳送到同一組的主以太之光，再搭城內傳送
            var shard = sheet
                .Where(a => !a.IsAetheryte && a.AethernetGroup != 0 && a.Territory.RowId == npc.TerritoryId)
                .OrderBy(a => Distance(a, npcPos))
                .FirstOrDefault();

            if (shard.RowId != 0)
            {
                var main = sheet.FirstOrDefault(a => a.IsAetheryte && a.AethernetGroup == shard.AethernetGroup);
                if (main.RowId == 0 || !IsAttuned(main.RowId))
                {
                    Fail("附近的以太之光還沒登記");
                    return false;
                }

                aethernetName = shard.AethernetName.ValueNullable?.Name.ExtractText() ?? string.Empty;
                aethernetTerritory = main.Territory.RowId;
                if (aethernetName.Length == 0)
                {
                    Fail("讀不到城內以太之光的名字");
                    return false;
                }

                if (Service.clientState.TerritoryType == main.Territory.RowId)
                {
                    // 已經在主城了，直接搭城內傳送
                    Enter(Step.Aethernet, $"城內傳送到 {aethernetName}…");
                    return true;
                }

                return Teleport(main.RowId, $"傳送到 {main.PlaceName.ValueNullable?.Name.ExtractText()}（之後搭城內傳送到 {aethernetName}）…");
            }

            Fail("這個區域沒有可以傳送的以太之光");
            return false;
        }

        /// <summary>照傳送清單判斷有沒有登記過（同 GatherBuddyReborn 的 Teleporter.IsAttuned）。</summary>
        private static bool IsAttuned(uint aetheryteId)
        {
            var telepo = Telepo.Instance();
            if (telepo == null)
            {
                return false;
            }

            telepo->UpdateAetheryteList();
            for (var it = telepo->TeleportList.First; it != telepo->TeleportList.Last; ++it)
            {
                if (it->AetheryteId == aetheryteId)
                {
                    return true;
                }
            }

            return false;
        }

        private bool Teleport(uint aetheryteId, string status)
        {
            var telepo = Telepo.Instance();
            if (telepo == null || !telepo->Teleport(aetheryteId, 0))
            {
                Fail("無法傳送（可能正在詠唱或被限制）");
                return false;
            }

            Enter(Step.Teleporting, status);
            return true;
        }

        private static float Distance(Aetheryte aetheryte, Vector2 npcPos)
        {
            var level = aetheryte.Level.FirstOrDefault().ValueNullable;
            if (level == null || (level.Value.X == 0 && level.Value.Z == 0))
            {
                return float.MaxValue / 2;
            }

            return Vector2.Distance(new Vector2(level.Value.X, level.Value.Z), npcPos);
        }

        // ─────────────────────────────────────────────────────────
        // 每幀更新
        // ─────────────────────────────────────────────────────────

        public void Update()
        {
            if (!Running || target == null)
            {
                return;
            }

            if (Service.condition[ConditionFlag.InCombat])
            {
                Fail("進入戰鬥");
                return;
            }

            if (DateTime.Now - stepStarted > StepTimeout)
            {
                Fail("等太久");
                return;
            }

            if (DateTime.Now < nextCheck)
            {
                return;
            }

            nextCheck = DateTime.Now + TimeSpan.FromSeconds(step == Step.Moving ? 0.1 : 0.3);

            switch (step)
            {
                case Step.Teleporting: TickTeleporting(); break;
                case Step.Aethernet: TickAethernet(); break;
                case Step.WaitNavReady: TickWaitNavReady(); break;
                case Step.Mounting: TickMounting(); break;
                case Step.Moving: TickMoving(); break;
                case Step.Dismounting: TickDismounting(); break;
            }
        }

        private static bool IsBusy =>
            Service.objectTable.LocalPlayer == null ||
            Service.condition[ConditionFlag.BetweenAreas] ||
            Service.condition[ConditionFlag.BetweenAreas51] ||
            Service.condition[ConditionFlag.Casting] ||
            Service.condition[ConditionFlag.OccupiedInCutSceneEvent];

        private static float DistanceTo(Vector3 pos)
        {
            var player = Service.objectTable.LocalPlayer;
            return player != null ? Vector3.Distance(player.Position, pos) : float.MaxValue;
        }

        private void TickTeleporting()
        {
            if (IsBusy)
            {
                return;
            }

            uint expected = aethernetName.Length > 0 ? aethernetTerritory : target!.TerritoryId;
            if (Service.clientState.TerritoryType != expected)
            {
                // 詠唱被打斷就不會換區域
                if (DateTime.Now - stepStarted > TimeSpan.FromSeconds(15))
                {
                    Fail("傳送被中斷");
                }

                return;
            }

            // 換完區域後等一下再動作（同 GatherBuddyReborn 等 1.5 秒）
            if (aethernetName.Length > 0)
            {
                Enter(Step.Aethernet, $"城內傳送到 {aethernetName}…", 1.5);
            }
            else
            {
                Enter(Step.WaitNavReady, "準備導航…", 1.5);
            }
        }

        private void TickAethernet()
        {
            if (IsBusy)
            {
                return;
            }

            TryIpc(() => Service.pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy").InvokeFunc(), out var busy);
            if (busy)
            {
                return;
            }

            if (Service.clientState.TerritoryType == target!.TerritoryId)
            {
                Enter(Step.WaitNavReady, "準備導航…", 1.5);
                return;
            }

            if (actionIssued)
            {
                // 已經叫 Lifestream 傳了，它不忙了卻還沒到：失敗
                if (DateTime.Now - stepStarted > TimeSpan.FromSeconds(10))
                {
                    Fail($"Lifestream 沒有傳送到 {aethernetName}");
                }

                return;
            }

            if (!TryIpc(() => Service.pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.AethernetTeleport").InvokeFunc(aethernetName), out var ok))
            {
                Fail("城內傳送需要 Lifestream 插件");
                return;
            }

            if (!ok)
            {
                Fail($"Lifestream 無法城內傳送到 {aethernetName}");
                return;
            }

            actionIssued = true;
        }

        private void TickWaitNavReady()
        {
            if (IsBusy)
            {
                return;
            }

            if (!Vnav.IsReady(out var ready))
            {
                Fail("需要 vnavmesh 插件");
                return;
            }

            if (!ready)
            {
                Status = "等待 vnavmesh 載入地圖…";
                return;
            }

            float dist = DistanceTo(destination);
            if (dist <= ArriveRange)
            {
                Arrive();
                return;
            }

            // 距離夠遠就先上坐騎，上不了就用走的
            bool mounted = Service.condition[ConditionFlag.Mounted];
            if (!mountTried && Service.pluginConfig.TravelUseMount && !mounted && dist > MountMinDistance && TryMountUp())
            {
                mountTried = true;
                Enter(Step.Mounting, "上坐騎…");
                return;
            }

            bool fly = Service.pluginConfig.TravelFly && mounted && CanFlyHere();
            var dest = Vnav.NearestPoint(destination);
            if (!Vnav.PathfindAndMoveTo(dest, fly))
            {
                Fail("vnavmesh 找不到路");
                return;
            }

            Enter(Step.Moving, $"{(fly ? "飛向" : mounted ? "騎向" : "走向")} {targetName}…");
        }

        /// <summary>上坐騎：有指定且已取得的坐騎就騎它，否則用坐騎隨機（同 GatherBuddyReborn）。</summary>
        private static bool TryMountUp()
        {
            var am = ActionManager.Instance();
            uint mountId = Service.pluginConfig.TravelMountId;

            if (mountId != 0 && PlayerState.Instance()->IsMountUnlocked(mountId) && am->GetActionStatus(ActionType.Mount, mountId) == 0)
            {
                return am->UseAction(ActionType.Mount, mountId);
            }

            if (am->GetActionStatus(ActionType.GeneralAction, MountRouletteActionId) == 0)
            {
                return am->UseAction(ActionType.GeneralAction, MountRouletteActionId);
            }

            return false;
        }

        private void TickMounting()
        {
            // 上好坐騎，或等了幾秒還沒上去（例如這裡不能騎），都回去開始導航
            bool mounted = Service.condition[ConditionFlag.Mounted] && !Service.condition[ConditionFlag.Casting];
            if (mounted || DateTime.Now - stepStarted > TimeSpan.FromSeconds(5))
            {
                Enter(Step.WaitNavReady, "準備導航…", 0.2);
            }
        }

        /// <summary>這個區域的風脈泉都解完了才能飛（主城等沒有風脈的區域不能飛）。</summary>
        private static bool CanFlyHere()
        {
            var territory = Service.dataManager.GetExcelSheet<TerritoryType>().GetRowOrDefault(Service.clientState.TerritoryType);
            uint flagSet = territory?.AetherCurrentCompFlgSet.RowId ?? 0;
            return flagSet != 0 && PlayerState.Instance()->IsAetherCurrentZoneComplete(flagSet);
        }

        private void TickMoving()
        {
            float dist = DistanceTo(destination);
            if (dist <= ArriveRange)
            {
                Vnav.Stop();
                Arrive();
                return;
            }

            if (Vnav.IsBusy())
            {
                return;
            }

            // vnavmesh 停了但還沒到
            string name = targetName;
            Fail($"停在離 {name} {dist:0} 米的地方（路可能不通）");
        }

        private void Arrive()
        {
            if (Service.condition[ConditionFlag.Mounted] && Service.pluginConfig.TravelDismount)
            {
                Enter(Step.Dismounting, "下坐騎…", 0.1);
                return;
            }

            Finish();
        }

        /// <summary>下坐騎：飛行中按第一次會先降落，所以每秒按一次直到下來（同 GatherBuddyReborn）。</summary>
        private void TickDismounting()
        {
            if (!Service.condition[ConditionFlag.Mounted])
            {
                Finish();
                return;
            }

            if (DateTime.Now - stepStarted > TimeSpan.FromSeconds(6))
            {
                // 下不來（例如停在空中不能降落的地方）就算了，人已經到了
                Finish();
                return;
            }

            if (!Service.condition[ConditionFlag.Casting])
            {
                ActionManager.Instance()->UseAction(ActionType.Mount, 0);
            }

            nextCheck = DateTime.Now + TimeSpan.FromSeconds(1);
        }

        private void Finish()
        {
            var npc = Service.objectTable.FirstOrDefault(o => o.BaseId == target!.ENpcId);
            if (npc != null)
            {
                Service.targetManager.Target = npc;
            }

            string name = targetName;
            Stop(null);
            Status = $"已抵達 {name}";
            Service.chatGui.Print($"[TriadBuddy] 已抵達 {name}" + (npc != null ? "，已選取" : ""));
        }

        // ─────────────────────────────────────────────────────────
        // vnavmesh（參數格式同 GatherBuddyReborn 的 IpcSubscribers）
        // ─────────────────────────────────────────────────────────

        private static class Vnav
        {
            public static bool IsReady(out bool ready) =>
                TryIpc(() => Service.pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady").InvokeFunc(), out ready);

            public static bool PathfindAndMoveTo(Vector3 dest, bool fly) =>
                TryIpc(() => Service.pluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo").InvokeFunc(dest, fly), out var ok) && ok;

            public static bool IsBusy()
            {
                TryIpc(() => Service.pluginInterface.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress").InvokeFunc(), out var computing);
                TryIpc(() => Service.pluginInterface.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning").InvokeFunc(), out var running);
                return computing || running;
            }

            public static void Stop() =>
                TryIpc(() => Service.pluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop").InvokeAction());

            /// <summary>把 NPC 的位置修正到導航網格上最近的點（NPC 常站在網格邊緣外）。修不了就用原位置。</summary>
            public static Vector3 NearestPoint(Vector3 pos)
            {
                if (TryIpc(() => Service.pluginInterface.GetIpcSubscriber<Vector3, float, float, Vector3>("vnavmesh.Query.Mesh.NearestPoint").InvokeFunc(pos, ArriveRange, 2.5f), out var point) &&
                    point != Vector3.Zero &&
                    Vector3.Distance(point, pos) <= ArriveRange + 2.5f)
                {
                    return point;
                }

                return pos;
            }
        }

        private static bool TryIpc(System.Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (IpcNotReadyError)
            {
                return false;
            }
            catch (Exception ex)
            {
                Service.logger.Warning(ex, "ipc call failed");
                return false;
            }
        }

        private static bool TryIpc<T>(Func<T> func, out T result)
        {
            try
            {
                result = func();
                return true;
            }
            catch (IpcNotReadyError)
            {
                result = default!;
                return false;
            }
            catch (Exception ex)
            {
                Service.logger.Warning(ex, "ipc call failed");
                result = default!;
                return false;
            }
        }
    }
}
