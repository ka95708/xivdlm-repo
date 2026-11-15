using Dalamud.Game.Text.SeStringHandling.Payloads;
using System.Collections.Generic;

namespace TriadBuddyPlugin
{
    public class GameNpcInfo
    {
        public int npcId;
        public int triadId;
        public int achievementId;

        public int matchFee;

        public MapLinkPayload? Location;

        // fork：傳送＋導航用。ENpc 編號用來在附近找到 NPC 並選取；WorldPos 是 Level 表的實際座標
        public uint ENpcId;
        public uint TerritoryId;
        public System.Numerics.Vector3? WorldPos;
        public List<int> rewardCards = new();

        // call GameNpcDB.Refresh() before reading fields below
        public bool IsBeatenOnce;
        public bool IsCompleted;
        public bool IsExcludedFromAchievementTracker => (achievementId == 0xffff);
    }

    public class GameNpcDB
    {
        private static GameNpcDB instance = new();

        public UnsafeReaderTriadCards? memReader;
        public Dictionary<int, GameNpcInfo> mapNpcs = new();

        public static GameNpcDB Get() { return instance; }

        public void Refresh()
        {
            if (memReader != null && !memReader.HasErrors)
            {
                foreach (var kvp in mapNpcs)
                {
                    kvp.Value.IsBeatenOnce = memReader.IsNpcBeaten(kvp.Value.triadId);
                }
            }

            // card search window is already doing GameCardDB refresh before this
            var cardInfoDB = GameCardDB.Get();
            foreach (var kvp in mapNpcs)
            {
                bool isCompleted = true;

                foreach (var rewardId in kvp.Value.rewardCards)
                {
                    if (!cardInfoDB.ownedCardIds.Contains(rewardId))
                    {
                        isCompleted = false;
                        break;
                    }
                }

                kvp.Value.IsCompleted = isCompleted;
            }
        }
    }
}
