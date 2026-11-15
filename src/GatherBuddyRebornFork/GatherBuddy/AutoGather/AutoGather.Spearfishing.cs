using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.AutoGather.Extensions;
using GatherBuddy.Classes;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        public unsafe void SnapshotSpearfishingInventory(IEnumerable<Fish> fishToTrack)
        {
            _spearfishingInventorySnapshot.Clear();
            var inventory = InventoryManager.Instance();
            
            foreach (var fish in fishToTrack)
            {
                var count = inventory->GetInventoryItemCount(fish.ItemId, false, false, false);
                _spearfishingInventorySnapshot[fish.ItemId] = (int)count;
            }
        }
        
        public unsafe void UpdateSpearfishingCatches()
        {
            if (_spearfishingInventorySnapshot.Count == 0)
            {
                if (_spawnRequirementsMetCache.Any(kvp => kvp.Value))
                {
                    ClearSpearfishingSessionData();
                }
                return;
            }
                
            var inventory = InventoryManager.Instance();
            
            foreach (var (fishId, previousCount) in _spearfishingInventorySnapshot)
            {
                var currentCount = (int)inventory->GetInventoryItemCount(fishId, false, false, false);
                var caught = currentCount - previousCount;
                
                if (caught > 0)
                {
                    if (!SpearfishingSessionCatches.ContainsKey(fishId))
                        SpearfishingSessionCatches[fishId] = 0;
                        
                    SpearfishingSessionCatches[fishId] += caught;
                    _spawnRequirementsMetCache.Clear();
                    var fishName = GatherBuddy.GameData.Fishes.TryGetValue(fishId, out var fish) ? fish.Name[GatherBuddy.Language] : fishId.ToString();
                    GatherBuddy.Log.Information($"[Spearfishing] Caught {caught}x {fishName}, session total: {SpearfishingSessionCatches[fishId]}");
                }
            }
            
            _spearfishingInventorySnapshot.Clear();
        }
        
        public bool AreSpawnRequirementsMet(FishingSpot shadowNode)
        {
            if (!shadowNode.IsShadowNode || shadowNode.SpawnRequirements.Count == 0)
                return true;
            
            if (!IsGathering && _spawnRequirementsMetCache.TryGetValue(shadowNode.Id, out var cached))
            {
                GatherBuddy.Log.Debug($"[Spearfishing] Using cached requirement status for shadow node {shadowNode.Id}: {cached}");
                return cached;
            }
                
            var allMet = true;
            foreach (var requirement in shadowNode.SpawnRequirements)
            {
                var caughtCount = SpearfishingSessionCatches.GetValueOrDefault(requirement.RequiredFish.ItemId, 0);
                var reqFishName = GatherBuddy.GameData.Fishes.TryGetValue(requirement.RequiredFish.ItemId, out var fish) ? fish.Name[GatherBuddy.Language] : requirement.RequiredFish.ItemId.ToString();
                GatherBuddy.Log.Debug($"[Spearfishing] Requirement check: {reqFishName} - caught {caughtCount}/{requirement.Count}");
                if (caughtCount < requirement.Count)
                {
                    allMet = false;
                    break;
                }
            }
            
            GatherBuddy.Log.Debug($"[Spearfishing] Requirements met for shadow node {shadowNode.Id}: {allMet}");
            
            if (!IsGathering)
                _spawnRequirementsMetCache[shadowNode.Id] = allMet;
            
            return allMet;
        }
        
        public void ClearSpearfishingSessionData()
        {
            SpearfishingSessionCatches.Clear();
            _spearfishingInventorySnapshot.Clear();
            _spawnRequirementsMetCache.Clear();
            
            if (_currentAutoHookTarget?.Fish?.IsSpearFish == true)
            {
                CleanupAutoHook();
            }
        }
    }
}
