using System;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AetherKit.Core;

/// <summary>
/// 字串陣列裡我們改寫的一格。同一次顯示資訊框可能更新好幾次，改過的字串會留在陣列裡；
/// 再看到自己寫的字串時，要從原本的內容重新改，不然會一直往後疊。
/// </summary>
public sealed unsafe class StringArrayField
{
    private readonly int index;
    private byte[]? original;
    private byte[]? written;

    public StringArrayField(int index)
    {
        this.index = index;
    }

    public void Forget() => original = written = null;

    /// <summary>change = null 表示這次不改；之前改過的話換回原本的內容。</summary>
    public void Apply(StringArrayData* strings, Func<byte[], byte[]>? change)
    {
        var current = Read(strings);
        var source = written != null && current.AsSpan().SequenceEqual(written) ? original! : current;

        if (change == null)
        {
            if (!ReferenceEquals(source, current))
                Write(strings, source);

            Forget();
            return;
        }

        var result = change(source);
        Write(strings, result);

        original = source;
        written = result;
    }

    private byte[] Read(StringArrayData* strings)
    {
        var value = strings->StringArray[index];
        if (value.Value == null) return Array.Empty<byte>();

        return value.AsSpan().ToArray();
    }

    private void Write(StringArrayData* strings, byte[] value)
    {
        // 結尾補 0：遊戲讀的是 C 字串
        var terminated = new byte[value.Length + 1];
        value.CopyTo(terminated, 0);
        strings->SetValue(index, terminated, false, true, true);
    }
}
