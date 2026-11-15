using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace TriadBuddyPlugin
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 0;

        public bool ShowSolverHintsInGame { get; set; } = true;
        public bool ShowDeckEditHighlights { get; set; } = true;
        public bool CanUseProfileReader { get; set; } = true;
        public float DeckOptimizerCPU { get; set; } = 1.0f;

        public bool CheckCardNpcMatchOnly { get; set; } = false;
        public bool CheckCardNotOwnedOnly { get; set; } = false;
        public bool CheckNpcHideBeaten { get; set; } = false;
        public bool CheckNpcHideCompleted { get; set; } = false;

        // 自動對戰（fork 新增）。兩個開關每次載入插件都從關閉開始，不存檔，見 AutoTriad。
        public float AutoMoveDelayMin { get; set; } = 1.0f;
        public float AutoMoveDelayMax { get; set; } = 2.5f;
        public bool AutoClickInstant { get; set; } = true;
        public float AutoClickTimeout { get; set; } = 8.0f;
        public float AutoLoopDelayMin { get; set; } = 2.0f;
        public float AutoLoopDelayMax { get; set; } = 5.0f;
        public int AutoLoopMaxMatches { get; set; } = 50;
        public int AutoLoopMaxLossStreak { get; set; } = 3;
        public bool AutoLoopUseBestDeck { get; set; } = true;
        public int AutoLoopDeckIndex { get; set; } = 0;
        public bool AutoDebugLogEvents { get; set; } = false;

        // 前往 NPC（fork 新增）
        public bool TravelUseMount { get; set; } = true;
        public bool TravelFly { get; set; } = true;
        public bool TravelDismount { get; set; } = true;
        public uint TravelMountId { get; set; } = 0;        // 0 = 坐騎隨機

        [Serializable]
        public class NpcStatInfo
        {
            public Dictionary<int, int> Cards { get; set; } = new();
            public int NumCoins { get; set; } = 0;
            public int NumWins { get; set; } = 0;
            public int NumDraws { get; set; } = 0;
            public int NumLosses { get; set; } = 0;

            public int GetNumMatches() => (NumWins + NumDraws + NumLosses);
        }

        public Dictionary<int, NpcStatInfo> NpcStats { get; set; } = new();

        public void Save()
        {
            Service.pluginInterface.SavePluginConfig(this);
        }
    }
}
