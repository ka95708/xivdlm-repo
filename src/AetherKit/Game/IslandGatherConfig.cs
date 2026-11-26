using System.Collections.Generic;
using Newtonsoft.Json;

namespace AetherKit;

/// <summary>無人島採集（IslandGather）的設定。</summary>
public class IslandGatherConfig
{
    /// <summary>想要的素材（道具 ID）。能採到這些素材的採集點都拿來排路線。</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public HashSet<uint> Items { get; set; } = new();

    /// <summary>目標數量：道具 ID → 數量。勾選的素材到量後不再為它繞路；勾選的全部都設了目標、也都到量就停。</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<uint, int> Targets { get; set; } = new();

    /// <summary>每種採集點實際採到過的東西（EObj 編號 → 道具 ID），採集時從聊天欄的道具連結記下來。</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<uint, HashSet<uint>> Yields { get; set; } = new();

    /// <summary>無人島工房本週＋下週排程預訂要用掉的素材（道具 ID → 數量），排程表開著時讀到的最後一份。</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<uint, int> Reserved { get; set; } = new();

    /// <summary>上面那份是什麼時候讀到的；MinValue = 還沒讀過。</summary>
    public System.DateTime ReservedAt { get; set; } = System.DateTime.MinValue;

    /// <summary>繞幾圈（一圈 11 個點）；0 = 一直繞。</summary>
    public int Loops { get; set; }

    /// <summary>聊天欄出現「持有數量已達到上限」就停。</summary>
    public bool StopAtCap { get; set; } = true;

    /// <summary>距離超過這個（米）才上坐騎，近的直接走。</summary>
    public int MountDistance { get; set; } = 30;

    /// <summary>做完（圈數到、到上限、到目標數量）之後：登出或關閉遊戲。手動停止、出錯不算。</summary>
    public Core.FinishAction AfterFinish { get; set; }
}
