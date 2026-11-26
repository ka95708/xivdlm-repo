namespace AetherKit;

/// <summary>高山茶理符（TsaiTouVounouLeve）的設定。理符固定是「製作委託：治癒身心的茶」。</summary>
public class TsaiTouVounouLeveConfig
{
    /// <summary>最多交幾次；0 = 一直交到受理限額或高山茶用完。</summary>
    public int Rounds { get; set; }

    /// <summary>做完（次數到、限額用完、道具用完）之後：登出或關閉遊戲。手動停止、出錯不算。</summary>
    public Core.FinishAction AfterFinish { get; set; }
}
