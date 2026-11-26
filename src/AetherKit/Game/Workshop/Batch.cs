// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace AetherKit.Game.Workshop;

/// <summary>固定 6 格的步驟 ID 陣列（值型別，0 表示空）。</summary>
[InlineArray(6)]
public struct StepArray
{
    private byte element0;

    /// <summary>從最多 6 個 ID 建立（不足補 0）。</summary>
    public static StepArray From(ReadOnlySpan<byte> steps)
    {
        var s = new StepArray();
        for (var i = 0; i < steps.Length && i < 6; i++)
            s[i] = steps[i];
        return s;
    }

    /// <summary>轉成長度 6 的陣列。</summary>
    public readonly byte[] ToArray()
    {
        var a = new byte[6];
        for (var i = 0; i < 6; i++)
            a[i] = this[i];
        return a;
    }
}

/// <summary>固定 6 格的步驟價格陣列（值型別）。</summary>
[InlineArray(6)]
public struct ValueArray
{
    private ushort element0;

    /// <summary>轉成長度 6 的陣列。</summary>
    public readonly ushort[] ToArray()
    {
        var a = new ushort[6];
        for (var i = 0; i < 6; i++)
            a[i] = this[i];
        return a;
    }
}

/// <summary>單一工房一天的工序（最多 6 步）。</summary>
public struct Batch
{
    /// <summary>步驟總數。</summary>
    public byte Seq;
    /// <summary>每一步的配方 ID（前 Seq 格有效）。</summary>
    public StepArray Steps;
    /// <summary>每一步的單價（單一工房；連擊步驟已乘 2 份產量）。</summary>
    public ValueArray Values;
    /// <summary>排序用的值。</summary>
    public ushort CmpValue;
    /// <summary>總價（單一工房）。</summary>
    public ushort Value;
    /// <summary>總成本（單一工房）。</summary>
    public ushort Cost;
    /// <summary>總耗時（小時）。</summary>
    public byte Time;

    /// <summary>空工序。</summary>
    public static Batch New() => default;

    /// <summary>上一個步驟的 ID；沒有則為 0。</summary>
    public readonly byte Last() => Seq == 0 ? (byte)0 : Steps[Seq - 1];

    /// <summary>加一個新步驟，回傳新的工序（自身不變）。</summary>
    public readonly Batch Push(byte id, ushort val, ushort cost, byte time)
    {
        if (Seq >= 6)
            throw new InvalidOperationException("Step overflow!");

        // 非第一步有連擊加成
        if (Seq != 0)
            val = (ushort)(val * 2);
        var ret = this;
        ret.Seq = (byte)(Seq + 1);
        ret.Value = (ushort)(Value + val);
        ret.Cost = (ushort)(Cost + cost);
        ret.Time = (byte)(Time + time);
        ret.Steps[Seq] = id;
        ret.Values[Seq] = val;
        return ret;
    }

    /// <summary>指定物品的產量（第一步 1 個，其餘每步 2 個）。</summary>
    public readonly byte GetProduce(byte id)
    {
        byte count = 0;
        for (var i = 0; i < Seq; i++)
        {
            if (Steps[i] == id)
                count = (byte)(count + (i == 0 ? 1 : 2));
        }
        return count;
    }

    /// <summary>設定排序用的值。</summary>
    public void SetCmpValue(bool withCost)
        => CmpValue = withCost ? (ushort)(Value - Cost) : Value;

    /// <summary>把此工序在 worker 個工房下的產量加到需求變動表（i8 環繞加法）。</summary>
    public readonly void DemandSub(sbyte[] demands, sbyte worker)
    {
        for (var i = 0; i < Seq; i++)
        {
            int id = Steps[i];
            var add = (sbyte)((i == 0 ? 1 : 2) * worker);
            demands[id] = (sbyte)(demands[id] + add);
        }
    }

    /// <summary>步驟 ID 陣列（長度 6）。</summary>
    public readonly byte[] StepsToArray() => Steps.ToArray();

    /// <summary>步驟價格陣列（長度 6）。</summary>
    public readonly ushort[] ValuesToArray() => Values.ToArray();

    /// <inheritdoc/>
    public override readonly string ToString()
        => $"Batch(seq={Seq}, steps=[{string.Join(",", Steps.ToArray())}], values=[{string.Join(",", Values.ToArray())}], value={Value}, cost={Cost}, time={Time}, cmp={CmpValue})";
}

/// <summary>Batch 的 Rust Ord：比較 cmp_value*10+seq，且方向反轉（大值排前面）。</summary>
internal struct BatchOrd : IComparer<Batch>
{
    public readonly int Compare(Batch x, Batch y)
    {
        var a = (uint)x.CmpValue * 10 + x.Seq;
        var b = (uint)y.CmpValue * 10 + y.Seq;
        return b.CompareTo(a);
    }
}

/// <summary>（工房數量, 工序）。</summary>
public struct WorkerBatch
{
    /// <summary>工房數量；0 表示此格為空。</summary>
    public byte Workers;
    /// <summary>單一工房的工序結果。</summary>
    public Batch Batch;

    /// <summary>建立。</summary>
    public WorkerBatch(byte workers, Batch batch)
    {
        Workers = workers;
        Batch = batch;
    }
}
