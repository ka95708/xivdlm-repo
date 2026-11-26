// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
using System.Collections.Generic;

namespace AetherKit.Game.Workshop;

/// <summary>遊戲資料庫：配方表與歡迎度模式表。</summary>
public sealed class GameDataRepo
{
    private readonly Recipe[] recipes;
    private readonly Popularity[][] popularity;
    private int popularPattern;

    /// <summary>
    /// 建立資料庫。recipes[i] 必須是 ID 為 i 的配方（第 0 列為空白佔位）；
    /// pops[p][id] 為歡迎度模式 p 下配方 id 的歡迎度。
    /// </summary>
    public GameDataRepo(IReadOnlyList<Recipe> recipes, IReadOnlyList<Popularity[]> pops)
    {
        this.recipes = new Recipe[recipes.Count];
        for (var i = 0; i < recipes.Count; i++)
            this.recipes[i] = recipes[i];
        popularity = new Popularity[pops.Count][];
        for (var i = 0; i < pops.Count; i++)
            popularity[i] = (Popularity[])pops[i].Clone();
        popularPattern = 0;
    }

    /// <summary>目前的歡迎度模式。</summary>
    public int PopularPattern => popularPattern;

    /// <summary>設定歡迎度模式。</summary>
    public void SetPopularPattern(int pat) => popularPattern = pat;

    /// <summary>取得配方（以陣列索引）。</summary>
    public ref readonly Recipe Recipe(int id) => ref recipes[id];

    /// <summary>取得歡迎度。</summary>
    public Popularity Popular(int id) => popularity[popularPattern][id];

    /// <summary>配方數量。</summary>
    public int RecipeLen => recipes.Length;

    /// <summary>取得配方狀態。</summary>
    public RecipeState State(int id, sbyte demand) => new(in recipes[id], demand, Popular(id));
}
