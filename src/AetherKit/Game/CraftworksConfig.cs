using System.Collections.Generic;
using Newtonsoft.Json;

namespace AetherKit;

/// <summary>開拓工房（Craftworks）的設定與每日紀錄。</summary>
public class CraftworksConfig
{
    /// <summary>
    /// 每天看到的供給與需求變動：週（該週第 1 天的日期 yyyy-MM-dd）→ 第幾天（0 起算）→ 每個產品一個位元組的十六進位字串
    /// （高 4 位元 = 供給、低 4 位元 = 需求變動，跟遊戲記憶體裡一樣）。遊戲只留當天的，預測整週走勢要用到前幾天的。
    /// </summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, Dictionary<int, string>> Observations { get; set; } = new();

    /// <summary>幹勁上限；0 = 照已完成的地標數自動算。</summary>
    public int MaxGroove { get; set; }

    /// <summary>工坊等級（1～5）；0 = 讀遊戲（取最低的那間）。</summary>
    public int WorkshopLevel { get; set; }

    /// <summary>工坊數量（1～4）；0 = 讀遊戲（蓋好的）。</summary>
    public int Workshops { get; set; }

    /// <summary>排班時把貓耳小員本週的需求做完。</summary>
    public bool IncludeFavors { get; set; } = true;

    /// <summary>同一天最多幾種不同的隊列（1 = 所有工坊做一樣的；2 = 可以分兩種）。</summary>
    public int QueueKinds { get; set; } = 2;

    /// <summary>開拓穀倉視窗上方加「領取並派遣」按鈕。</summary>
    public bool GranaryButton { get; set; } = true;

    /// <summary>不參與排班的產品（MJICraftworksObject 列號）。</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public HashSet<uint> Banned { get; set; } = new();
}
