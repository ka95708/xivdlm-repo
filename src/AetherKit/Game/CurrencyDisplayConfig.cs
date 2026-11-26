using System.Collections.Generic;
using Newtonsoft.Json;

namespace AetherKit;

/// <summary>更多貨幣顯示：放在金幣那一列的上方還是下方。</summary>
public enum CurrencyArea
{
    Above,
    Below
}

/// <summary>更多貨幣顯示的一種貨幣。</summary>
public class CurrencyEntry
{
    public uint ItemId { get; set; }

    /// <summary>名稱（設定頁顯示用，從物品資料表抄來）。</summary>
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

/// <summary>更多貨幣顯示的設定。</summary>
public class CurrencyDisplayConfig
{
    /// <summary>放在金幣那一列的上方或下方；清單一律由上往下排。</summary>
    public CurrencyArea Area { get; set; } = CurrencyArea.Above;

    /// <summary>要顯示的貨幣，照畫面上由上往下的順序。</summary>
    [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<CurrencyEntry> Currencies { get; set; } = new();
}
