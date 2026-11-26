using System.Collections.Generic;
using Dalamud.Game.ClientState.Keys;
using Newtonsoft.Json;

namespace AetherKit;

/// <summary>
/// 核心能力什麼時候作用。
/// 關 = 誰借都不作用；只在接管時 = 有功能借用期間才作用；常開 = 平常也作用。
/// </summary>
public enum CapabilityMode
{
    Off,
    OnLease,
    Always
}

/// <summary>核心能力的設定。各能力的模式用能力的 Id 存，沒記錄的用能力自己的預設值。</summary>
public class CoreConfig
{
    public Dictionary<string, CapabilityMode> Modes { get; set; } = new();

    /// <summary>按住這個鍵時，所有核心能力暫停（例如想自己看對話）。NO_KEY = 不使用。</summary>
    public VirtualKey PauseKey { get; set; } = VirtualKey.NO_KEY;

    /// <summary>修理：最低耐久低於這個百分比就修。</summary>
    public int RepairBelowPercent { get; set; } = 30;

    /// <summary>所有功能把執行過程寫進 log 檔（每個功能各自一個檔、每天一個、留 7 天），出問題時拿來查。</summary>
    public bool WriteLogs { get; set; } = true;

    /// <summary>過場跳過：這些地圖不跳（想看劇情的地方）。</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<uint> CutsceneKeepZones { get; set; } = new();
}
