// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
using System;

namespace AetherKit.Game.Workshop;

/// <summary>貓耳小員委託項目。</summary>
public struct Favor
{
    /// <summary>配方 ID（MJICraftworksObject 列號）。</summary>
    public byte Id;
    /// <summary>需求數量。</summary>
    public byte Num;

    /// <summary>建立。</summary>
    public Favor(byte id, byte num)
    {
        Id = id;
        Num = num;
    }

    /// <summary>從 [id, num] 建立。</summary>
    public static Favor FromArray(ReadOnlySpan<byte> arr) => new(arr[0], arr[1]);

    /// <summary>清單中是否有此配方。</summary>
    public static bool Contains(ReadOnlySpan<Favor> arr, byte id)
    {
        foreach (var i in arr)
        {
            if (i.Id == id)
                return true;
        }
        return false;
    }
}

/// <summary>貓耳小員委託的累計器（Rust 的 Favors&lt;3&gt;，只追蹤前 3 項）。</summary>
internal sealed class Favors
{
    public const int T = 3;

    public readonly Favor[] Items;
    private readonly byte[] sum = new byte[T];

    public Favors(Favor[] favors)
    {
        Items = favors;
    }

    private int Len => Math.Min(T, Items.Length);

    public void Add(byte id, byte count)
    {
        for (var i = 0; i < Len; i++)
        {
            if (Items[i].Id == id)
                sum[i] = (byte)(sum[i] + count);
        }
    }

    public void AddSeq(in Batch batch, byte workers)
    {
        for (var i = 0; i < batch.Seq; i++)
            Add(batch.Steps[i], (byte)(i == 0 ? 1 * workers : 2 * workers));
    }

    public byte Value()
    {
        byte s = 0;
        for (var i = 0; i < Len; i++)
            s = (byte)(s + Math.Min(sum[i], Items[i].Num));
        s = (byte)(s << 2);
        // 優先使用剛好填滿的
        for (var i = 0; i < Len; i++)
            s = (byte)(s + (Items[i].Num > 0 && sum[i] >= Items[i].Num ? 1 : 0));
        return s;
    }

    public Favor[] ToFavors()
    {
        var arr = new Favor[T];
        var len = Len;
        // Rust 的 clone_from_slice 在長度不符（超過 3 項）時會 panic
        if (Items.Length != len)
            throw new ArgumentException("Favor list must contain at most 3 items.");
        Array.Copy(Items, arr, len);
        for (var i = 0; i < len; i++)
            arr[i].Num = arr[i].Num > sum[i] ? (byte)(arr[i].Num - sum[i]) : (byte)0;
        return arr;
    }
}
