namespace AetherKit;

/// <summary>青魔教皇（BlueMageVaultFarm）的設定。</summary>
public class BlueMageVaultFarmConfig
{
    /// <summary>要刷幾輪。</summary>
    public int Loops { get; set; } = 50;

    /// <summary>跑完設定的輪數之後：登出或關閉遊戲。手動停止、出錯不算。</summary>
    public Core.FinishAction AfterFinish { get; set; }

    /// <summary>開始時不是青魔道士就換成這個套裝（完全相同優先，否則取第一個名稱包含它的）；空白 = 不換。</summary>
    public string Gearset { get; set; } = "青魔道士";

    /// <summary>每輪排本前，耐久低於核心能力頁的門檻就自己修理。</summary>
    public bool AutoRepair { get; set; } = true;

    /// <summary>排隊到進本最多等幾秒。</summary>
    public int QueueTimeoutSeconds { get; set; } = 300;
}
