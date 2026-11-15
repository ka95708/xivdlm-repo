using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private const int AetherGaugeOffset = 0x268;
        private const int AetherGaugeReadyThreshold = 200;
        private const float AetherTargetScanRadius = 25f;
        private const uint AethercannonActionId = 19700;
        private DateTime _lastAetherTarget = DateTime.MinValue;
        private readonly TimeSpan _aetherDebounce = TimeSpan.FromSeconds(2);
        
        private unsafe bool IsDiademAetherGaugeReady()
        {
            var addonPtr = Dalamud.GameGui.GetAddonByName("HWDAetherGauge");
            if (addonPtr == nint.Zero)
                return false;

            var addon = (AtkUnitBase*)(nint)addonPtr;
            if (addon == null || !addon->IsVisible)
                return false;

            int currentGauge = *(int*)((nint)addonPtr + AetherGaugeOffset);
            return currentGauge >= AetherGaugeReadyThreshold;
        }
        
        private IGameObject? FindNearbyEnemyForAether()
        {
            var player = Svc.ClientState.LocalPlayer;
            if (player == null) 
                return null;

            Vector3 pPos = player.Position;
            IGameObject? best = null;
            float bestDistSq = AetherTargetScanRadius * AetherTargetScanRadius;

            foreach (var obj in Svc.Objects)
            {
                if (obj is not IBattleNpc bnpc)
                    continue;

                if (!IsValidDiademEnemy(bnpc))
                    continue;

                float distSq = Vector3.DistanceSquared(pPos, bnpc.Position);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = bnpc;
                }
            }

            return best;
        }
        
        private bool IsValidDiademEnemy(IBattleNpc bnpc)
        {
            if (bnpc.IsDead)
                return false;

            if (!bnpc.IsTargetable)
                return false;

            if (bnpc.SubKind is 2 or 9)
                return false;

            return true;
        }
        
        private unsafe void TargetByGameObject(IGameObject gameObject)
        {
            var targetSystem = TargetSystem.Instance();
            if (targetSystem == null)
                return;
                
            targetSystem->Target = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)gameObject.Address;
        }
        
        private unsafe void TryUseAetherCannon()
        {
            if (!GatherBuddy.Config.AutoGatherConfig.DiademAutoAetherCannon)
                return;
            if (!Plugin.Functions.InTheDiadem())
                return;
            if (IsPathing)
                return;
            if (DateTime.UtcNow - _lastAetherTarget < _aetherDebounce)
                return;
            if (!IsDiademAetherGaugeReady())
                return;

            var enemy = FindNearbyEnemyForAether();
            if (enemy != null)
            {
                var enemyId = enemy.GameObjectId;
                TargetByGameObject(enemy);
                _lastAetherTarget = DateTime.UtcNow;
                Svc.Log.Debug($"[Diadem] Targeting enemy {enemy.Name} (ID: {enemyId}) at {enemy.Position}");
                
                TaskManager.DelayNext(100);
                
                TaskManager.Enqueue(() =>
                {
                    var currentTarget = Svc.Targets.Target;
                    if (currentTarget == null || currentTarget.GameObjectId != enemyId)
                    {
                        Svc.Log.Debug($"[Diadem] Target not set properly. Current: {currentTarget?.Name ?? "null"}");
                        return true;
                    }
                    
                    Svc.Log.Debug($"[Diadem] Target confirmed: {currentTarget.Name}, distance: {Vector3.Distance(Player.Position, currentTarget.Position):F1}y");
                    return true;
                });
                
                EnqueueActionWithDelay(() =>
                {
                    var currentTarget = Svc.Targets.Target;
                    if (currentTarget == null)
                    {
                        Svc.Log.Debug($"[Diadem] No target when trying to fire");
                        return;
                    }
                    
                    var amInstance = ActionManager.Instance();
                    if (amInstance == null)
                    {
                        Svc.Log.Debug($"[Diadem] ActionManager.Instance() is null");
                        return;
                    }
                    
                    var targetId = currentTarget.GameObjectId;
                    var actionStatus = amInstance->GetActionStatus(ActionType.Action, AethercannonActionId);
                    Svc.Log.Debug($"[Diadem] Firing at target ID {targetId}, action status: {actionStatus}");
                    
                    if (actionStatus == 0)
                    {
                        var result = amInstance->UseAction(ActionType.Action, AethercannonActionId, targetId);
                        Svc.Log.Debug($"[Diadem] UseAction returned: {result}");
                    }
                    else
                    {
                        Svc.Log.Debug($"[Diadem] Cannot use action, status code: {actionStatus}");
                    }
                });
                
                TaskManager.Enqueue(() => Dalamud.Conditions[ConditionFlag.Casting], 1000, "Wait for aethercannon cast start");
                TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.Casting], 5000, "Wait for aethercannon cast finish");
                TaskManager.DelayNext(500);
            }
        }
    }
}
