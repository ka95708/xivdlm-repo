// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
using System;

namespace AetherKit.Game.Workshop;

/// <summary>單個配方（對應 MJICraftworksObject 一列）。</summary>
public struct Recipe
{
    /// <summary>配方 ID（MJICraftworksObject 列號）。</summary>
    public byte Id;
    /// <summary>主題 1（MJICraftworksObjectTheme 列號，0 表示無）。</summary>
    public byte Theme1;
    /// <summary>主題 2（同上）。</summary>
    public byte Theme2;
    /// <summary>配方需要的無人島開拓等級。</summary>
    public byte Level;
    /// <summary>製作時長（小時）；0 代表此列不可用。</summary>
    public byte CraftTime;
    /// <summary>產物基礎價格。</summary>
    public ushort Value;
    /// <summary>原料成本（不考慮成本時可填 0）。</summary>
    public ushort Cost;

    /// <summary>建立配方。</summary>
    public Recipe(byte id, byte theme1, byte theme2, byte level, byte craftTime, ushort value, ushort cost)
    {
        Id = id;
        Theme1 = theme1;
        Theme2 = theme2;
        Level = level;
        CraftTime = craftTime;
        Value = value;
        Cost = cost;
    }

    /// <summary>從 [id, theme1, theme2, level, time, value, cost] 建立（與 Rust 的 from_array 相同，前五項截成 u8）。</summary>
    public static Recipe FromArray(ReadOnlySpan<ushort> arr)
        => new((byte)arr[0], (byte)arr[1], (byte)arr[2], (byte)arr[3], (byte)arr[4], arr[5], arr[6]);
}

/// <summary>配方狀態（配方＋當下需求與歡迎度）。</summary>
public readonly struct RecipeState
{
    private readonly byte id;
    private readonly ushort value;
    private readonly ushort cost;
    private readonly byte time;
    private readonly sbyte demand;
    private readonly Popularity popularity;

    /// <summary>建立配方狀態。</summary>
    public RecipeState(in Recipe recipe, sbyte demand, Popularity popularity)
    {
        id = recipe.Id;
        value = recipe.Value;
        cost = recipe.Cost;
        time = recipe.CraftTime;
        this.demand = demand;
        this.popularity = popularity;
    }

    private RecipeState(byte id, ushort value, ushort cost, byte time, sbyte demand, Popularity popularity)
    {
        this.id = id;
        this.value = value;
        this.cost = cost;
        this.time = time;
        this.demand = demand;
        this.popularity = popularity;
    }

    /// <summary>空值。</summary>
    public static RecipeState Empty() => new(0, 0, 0, 0, 0, Popularity.Average);

    /// <summary>配方係數 = 歡迎度係數 × 需求係數。</summary>
    public float Factor(short demandSub)
    {
        var d = DemandUtil.DemandFromVal((short)(demand - demandSub)).Factor();
        var p = popularity.Factor();
        var prod = (ushort)(d * p);
        return (float)prod / 10000f;
    }

    /// <summary>歡迎度係數。</summary>
    public float FactorPop() => (float)popularity.Factor() / 100f;

    /// <summary>需求係數。</summary>
    public float FactorDemand(short demandSub) => (float)DemandUtil.DemandFromVal((short)(demand - demandSub)).Factor() / 100f;

    /// <summary>產品收益。</summary>
    public ushort Value => value;
    /// <summary>原料成本。</summary>
    public ushort Cost => cost;
    /// <summary>配方 ID。</summary>
    public byte Id => id;
    /// <summary>工作時間。</summary>
    public byte CraftTime => time;
    /// <summary>需求值。</summary>
    public sbyte DemandValue => demand;
    /// <summary>歡迎度。</summary>
    public Popularity Popularity => popularity;
}
