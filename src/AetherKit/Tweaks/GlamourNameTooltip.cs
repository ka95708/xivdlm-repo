using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Text;
using Lumina.Text.ReadOnly;
using AetherKit.Core;

namespace AetherKit.Tweaks;

/// <summary>
/// 物品資訊顯示投影名稱（Simple Tweaks 的 Show Glamour in Item Name）：有投影外觀的物品，
/// 在物品資訊框的名稱下面多一行投影的名稱（灰色）。
/// 做法跟「持有狀況」改右上角數量一樣：在資訊框更新前改它要顯示的名稱文字（字串陣列），不另外加節點。
/// </summary>
public sealed unsafe class GlamourNameTooltip : Tweak
{
    private const string Addon = "ItemDetail";

    // 物品資訊框的字串陣列（StringArrayType.ItemDetail），第 0 格是名稱、第 1 格是投影名稱
    private const int StringArrayIndex = 27;
    private const int NameIndex = 0;
    private const int GlamourIndex = 1;

    private readonly StringArrayField name = new(NameIndex);

    public override string Id => "glamour-name-tooltip";
    public override string Name => "物品資訊顯示投影名稱";
    public override string Description => "有投影外觀的物品，在物品資訊框的名稱下面多一行投影的名稱。";
    public override TweakGroup Group => TweakGroup.Glamour;
    public override TweakKind Kind => TweakKind.Auto;
    public override string Where => "物品資訊框的名稱下面";

    protected override void OnEnable() =>
        Services.AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, Addon, OnPreUpdate);

    protected override void OnDisable()
    {
        Services.AddonLifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, Addon, OnPreUpdate);
        name.Forget();
    }

    private void OnPreUpdate(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonRequestedUpdateArgs update) return;

        var arrays = (StringArrayData**)update.StringArrayData;
        if (arrays == null) return;

        var strings = arrays[StringArrayIndex];
        if (strings == null || strings->Size <= GlamourIndex) return;

        var glamour = strings->StringArray[GlamourIndex];
        var glamourBytes = glamour.Value == null ? Array.Empty<byte>() : glamour.AsSpan().ToArray();

        // 沒有投影：名稱換回原本的（之前改過的話）
        if (glamourBytes.Length == 0)
        {
            name.Apply(strings, null);
            return;
        }

        name.Apply(strings, original =>
        {
            var builder = new SeStringBuilder();
            builder.Append(new ReadOnlySeStringSpan(original));
            builder.AppendNewLine();
            builder.PushColorRgba(0xB0, 0xB0, 0xB0, 0xFF).Append(new ReadOnlySeStringSpan(glamourBytes)).PopColor();
            return builder.ToArray();
        });
    }
}
