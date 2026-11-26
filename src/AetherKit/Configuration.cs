using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace AetherKit;

public enum ConditionKind
{
    /// <summary>Arg = ClassJob 的 row id。</summary>
    Job,

    CombatJob,
    CrafterJob,
    GathererJob,

    /// <summary>Arg = TerritoryType。</summary>
    Territory,

    InCombat,
    InDuty,
    Mounted,
    Crafting,
    Gathering,
    InCutscene,
    InPvP,

    /// <summary>Text = 插件的內部名稱（例如 WrathCombo），有載入就成立。</summary>
    PluginLoaded,

    /// <summary>Text = 插件提供的查詢名稱（例如 WrathCombo.GetAutoRotationState），回答「是」就成立。</summary>
    PluginQuery,

    /// <summary>Text = AetherKit 自己的開關代號（見 AetherKitStates，例如 fate、tweak:xxx），開著就成立。</summary>
    AetherKit
}

public class ConditionEntry
{
    public ConditionKind Kind { get; set; }

    /// <summary>職業或地圖這類需要參數的條件才用得到。</summary>
    public uint Arg { get; set; }

    /// <summary>插件名稱、查詢名稱這類文字參數。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>反轉這一條的結果。</summary>
    public bool Negate { get; set; }
}

/// <summary>
/// 一組具名的顯示條件。做成共用的一組而不是每個地方各自勾一堆，
/// 這樣「生產中」這種條件改一次就全部跟著改。
/// </summary>
public class ConditionSet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新條件組";

    /// <summary>true = 全部成立才算；false = 任一成立就算。</summary>
    public bool RequireAll { get; set; } = true;

    public List<ConditionEntry> Entries { get; set; } = new();
}

public class Configuration : IPluginConfiguration
{
    /// <summary>
    /// 資料結構不相容地改過時就加一。載入時版本比這個舊，直接換成全新設定 —
    /// 目前沒有要保留的舊資料，不值得為它寫轉換。
    /// </summary>
    public const int CurrentVersion = 11;

    public int Version { get; set; } = CurrentVersion;

    public TopBarConfig TopBar { get; set; } = new();

    public List<ConditionSet> ConditionSets { get; set; } = new();

    public CursorConfig Cursor { get; set; } = new();

    public AutoLoginConfig AutoLogin { get; set; } = new();

    public ItemOwnershipConfig ItemOwnership { get; set; } = new();

    public PluginLogConfig PluginLog { get; set; } = new();

    public AutoFateConfig AutoFate { get; set; } = new();

    public BlueMageVaultFarmConfig BlueMageVaultFarm { get; set; } = new();

    public TsaiTouVounouLeveConfig TsaiTouVounouLeve { get; set; } = new();

    public IslandGatherConfig IslandGather { get; set; } = new();

    public RetainerWorkConfig RetainerWork { get; set; } = new();

    public CurrencyDisplayConfig CurrencyDisplay { get; set; } = new();

    public HideOthersConfig HideOthers { get; set; } = new();

    public CraftworksConfig Craftworks { get; set; } = new();

    public CoreConfig Core { get; set; } = new();

    public TweaksConfig Tweaks { get; set; } = new();

    /// <summary>
    /// 停用的模組（存模組的 Id）。記停用的而不是啟用的：之後新增的模組預設就是開著，
    /// 不用每加一個模組就改設定檔版本。
    /// </summary>
    public List<string> DisabledModules { get; set; } = new();

    /// <summary>預設關著的模組（IModule.OffByDefault）裡，使用者打開的（存模組的 Id）。</summary>
    public List<string> EnabledModules { get; set; } = new();

    /// <summary>管理視窗上次停在哪一頁（模組的 Id，或共用頁的代號），重開時回到那裡。</summary>
    public string LastSettingsPage { get; set; } = string.Empty;

    public Theme Theme { get; set; } = new();

    public ConditionSet? FindConditionSet(Guid id)
    {
        if (id == Guid.Empty) return null;

        foreach (var set in ConditionSets)
        {
            if (set.Id == id)
                return set;
        }

        return null;
    }
}
