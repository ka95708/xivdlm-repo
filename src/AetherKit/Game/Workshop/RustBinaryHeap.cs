// Ported from mji-craftwork (https://github.com/ffxiv-cyou/ffxiv-island-sanctuary-craftwork), Apache License 2.0. Copyright (c) Jim Jiang and contributors.
using System.Collections.Generic;

namespace AetherKit.Game.Workshop;

/// <summary>
/// 逐行重現 Rust std::collections::BinaryHeap（最大堆積）的 push / pop / into_sorted_vec，
/// 讓同分元素的先後順序與原版一致。TCmp 等同 Rust 的 Ord::cmp。
/// </summary>
internal sealed class RustBinaryHeap<T, TCmp>
    where TCmp : struct, IComparer<T>
{
    private readonly List<T> data;
    private readonly TCmp cmp = default;

    public RustBinaryHeap(int capacity = 0)
    {
        data = new List<T>(capacity);
    }

    public int Count => data.Count;

    private bool Le(T a, T b) => cmp.Compare(a, b) <= 0;

    public void Push(T item)
    {
        var oldLen = data.Count;
        data.Add(item);
        SiftUp(0, oldLen);
    }

    public bool Pop(out T item)
    {
        if (data.Count == 0)
        {
            item = default!;
            return false;
        }
        var last = data.Count - 1;
        item = data[last];
        data.RemoveAt(last);
        if (data.Count != 0)
        {
            (item, data[0]) = (data[0], item);
            SiftDownToBottom(0);
        }
        return true;
    }

    /// <summary>依 Ord 由小到大排序後輸出（對反向 Ord 來說即由大值到小值）。</summary>
    public T[] IntoSortedVec()
    {
        var end = data.Count;
        while (end > 1)
        {
            end -= 1;
            (data[0], data[end]) = (data[end], data[0]);
            SiftDownRange(0, end);
        }
        return data.ToArray();
    }

    private int SiftUp(int start, int pos)
    {
        var elem = data[pos];
        while (pos > start)
        {
            var parent = (pos - 1) / 2;
            if (Le(elem, data[parent]))
                break;
            data[pos] = data[parent];
            pos = parent;
        }
        data[pos] = elem;
        return pos;
    }

    private void SiftDownRange(int pos, int end)
    {
        var elem = data[pos];
        var child = 2 * pos + 1;
        while (end >= 2 && child <= end - 2)
        {
            if (Le(data[child], data[child + 1]))
                child += 1;
            if (cmp.Compare(elem, data[child]) >= 0)
            {
                data[pos] = elem;
                return;
            }
            data[pos] = data[child];
            pos = child;
            child = 2 * pos + 1;
        }
        if (end >= 1 && child == end - 1 && cmp.Compare(elem, data[child]) < 0)
        {
            data[pos] = data[child];
            pos = child;
        }
        data[pos] = elem;
    }

    private void SiftDownToBottom(int pos)
    {
        var end = data.Count;
        var start = pos;
        var elem = data[pos];
        var child = 2 * pos + 1;
        while (end >= 2 && child <= end - 2)
        {
            if (Le(data[child], data[child + 1]))
                child += 1;
            data[pos] = data[child];
            pos = child;
            child = 2 * pos + 1;
        }
        if (child == end - 1)
        {
            data[pos] = data[child];
            pos = child;
        }
        data[pos] = elem;
        SiftUp(start, pos);
    }
}
