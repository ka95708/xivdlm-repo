using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace Barkeep;

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
    InPvP
}

public class ConditionEntry
{
    public ConditionKind Kind { get; set; }

    /// <summary>職業或地圖這類需要參數的條件才用得到。</summary>
    public uint Arg { get; set; }

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
