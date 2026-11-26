namespace AetherKit.Game;

/// <summary>
/// 讀 Wrath Combo 自動循環設定的 IPC（只讀，不借用、不改）。
/// 用來在自動Fate 開始前記下原本的選目標模式，停止時用指令設回去。
/// Wrath 沒裝或沒載入時一律回傳 null。
/// </summary>
public static class WrathIpc
{
    // Wrath 的 AutoRotationConfigOption：0 = InCombatOnly、1 = DPSRotationMode、2 = HealerRotationMode
    private const int DpsRotationMode = 1;
    private const int HealerRotationMode = 2;

    /// <summary>傷害職的選目標模式名稱（例如 Manual、Highest_Max）。</summary>
    public static string? DamageTargeting => Get(DpsRotationMode);

    /// <summary>治療職的選目標模式名稱。</summary>
    public static string? HealerTargeting => Get(HealerRotationMode);

    private static string? Get(int option)
    {
        try
        {
            var value = Services.PluginInterface
                .GetIpcSubscriber<object, object?>("WrathCombo.GetAutoRotationConfigState")
                .InvokeFunc(option);
            return value?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
