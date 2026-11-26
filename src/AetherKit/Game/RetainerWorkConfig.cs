namespace AetherKit;

/// <summary>自動雇員的設定。</summary>
public class RetainerWorkConfig
{
    /// <summary>改價：比市場參考價（不含自己雇員、濾掉過低離群值後的最低價）便宜幾金幣。0 = 一樣價錢。</summary>
    public int Undercut { get; set; } = 1;

    /// <summary>出售清單上顯示價格表（出售清單上方的按鈕開關）。</summary>
    public bool ShowPriceTable { get; set; } = true;
}
