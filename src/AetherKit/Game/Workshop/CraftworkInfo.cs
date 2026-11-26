// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
namespace AetherKit.Game.Workshop;

/// <summary>工房目前狀態。</summary>
public struct CraftworkInfo
{
    /// <summary>目前幹勁（groove）。</summary>
    public byte Tension;
    /// <summary>最高幹勁。</summary>
    public byte MaxTension;
    /// <summary>工房等級係數用的等級：係數 = 1 + 0.1 × Level（0 = 工房 I 級）。</summary>
    public byte Level;
    /// <summary>同時跑這個佇列的工房數量（一般 1~4）；影響每次連擊增加的幹勁與需求變動量。</summary>
    public byte Workers;

    /// <summary>建立工房狀態。</summary>
    public CraftworkInfo(byte tension, byte maxTension, byte level, byte workers)
    {
        Tension = tension;
        MaxTension = maxTension;
        Level = level;
        Workers = workers;
    }

    /// <summary>目前工房係數。</summary>
    public readonly float Factor()
    {
        var tension = Tension < MaxTension ? Tension : MaxTension;
        var a = (float)(1.0f + (float)(Level * 0.1f));
        var b = (float)(1.0f + (float)(tension * 0.01f));
        return (float)(a * b);
    }

    /// <summary>等級係數。</summary>
    public readonly float FactorLevel() => (float)(1.0f + (float)(Level * 0.1f));

    /// <summary>幹勁係數。</summary>
    public readonly float FactorTension()
    {
        var tension = Tension < MaxTension ? Tension : MaxTension;
        return (float)(1.0f + (float)(tension * 0.01f));
    }

    /// <summary>增加對應工房數量的幹勁。</summary>
    public readonly CraftworkInfo Next() => AddTension(Workers);

    /// <summary>增加指定點數的幹勁（u8 飽和加法）。</summary>
    public readonly CraftworkInfo AddTension(byte n)
    {
        var t = Tension + n;
        return new CraftworkInfo((byte)(t > 255 ? 255 : t), MaxTension, Level, Workers);
    }

    /// <inheritdoc/>
    public override readonly string ToString() => $"CraftworkInfo(tension={Tension}, max={MaxTension}, level={Level}, workers={Workers})";
}
