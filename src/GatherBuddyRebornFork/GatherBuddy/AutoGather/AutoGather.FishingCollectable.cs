using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.Text;
using Dalamud.Utility;
using ECommons;
using ECommons.DalamudServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private static readonly List<string> CollectablePatterns =
        [
            "collectability of",
            "収集価値",
            "Sammlerwert",
            "Valeur de collection"
        ];

        private unsafe bool HandleFishingCollectable()
        {
            if (!GatherBuddy.Config.AutoGatherConfig.AutoCollectablesFishing)
            {
                GatherBuddy.Log.Debug("[AutoCollectable] Feature disabled in config");
                return false;
            }

            var addon = SelectYesnoAddon;
            if (addon == null)
                return false;
            
            if (!addon->IsReady)
            {
                GatherBuddy.Log.Debug("[AutoCollectable] SelectYesno addon not ready");
                return false;
            }
            
            GatherBuddy.Log.Debug("[AutoCollectable] SelectYesno addon found and ready");

            var master = new AddonMaster.SelectYesno(addon);
            var text = master.TextLegacy;

            if (!CollectablePatterns.Any(text.Contains))
                return false;

            GatherBuddy.Log.Debug($"[AutoCollectable] Detected collectable dialog with text: {text}");

            var name = Enum.GetValues<SeIconChar>()
                .Cast<SeIconChar>()
                .Aggregate(addon->AtkValues[15].String.AsDalamudSeString().GetText(), 
                    (current, enumValue) => current.Replace(enumValue.ToIconString(), ""))
                .Trim();

            if (GenericHelpers.FindRow<Item>(x => x.IsCollectable && !x.Singular.IsEmpty && 
                name.Contains(x.Singular.GetText(), StringComparison.InvariantCultureIgnoreCase)) is not { RowId: > 0 } item)
            {
                GatherBuddy.Log.Debug($"[AutoCollectable] Failed to match any collectable to {name} [original={addon->AtkValues[15].String}]");
                return false;
            }

            GatherBuddy.Log.Debug($"[AutoCollectable] Detected item [{item.RowId}] {item.Name}");

            if (!int.TryParse(Regex.Match(text, @"\d+").Value, out var value))
            {
                GatherBuddy.Log.Debug($"[AutoCollectable] Failed to parse collectability value from text");
                return false;
            }

            if (GenericHelpers.FindRow<CollectablesShopItem>(x => x.Item.Value.RowId == item.RowId) is { } collectability)
            {
                var min = collectability.CollectablesShopRefine.Value.LowCollectability;
                GatherBuddy.Log.Debug($"[AutoCollectable] Minimum collectability required is {min}, value detected is {value}");
                
                if (value >= min)
                {
                    GatherBuddy.Log.Debug($"[AutoCollectable] Accepting [{item.RowId}] {item.Name} with collectability of {value}");
                    master.Yes();
                    return true;
                }
                else
                {
                    GatherBuddy.Log.Debug($"[AutoCollectable] Declining [{item.RowId}] {item.Name} with insufficient collectability of {value}");
                    master.No();
                    return true;
                }
            }
            else
            {
                if (item.AetherialReduce > 0)
                {
                    GatherBuddy.Log.Debug($"[AutoCollectable] Accepting [{item.RowId}] {item.Name} - aethersand fish");
                    master.Yes();
                    return true;
                }
                else if (GenericHelpers.TryGetRow<WKSItemInfo>(item.AdditionalData.RowId, out var wksItem))
                {
                    GatherBuddy.Log.Debug($"[AutoCollectable] Accepting [{item.RowId}] {item.Name} - stellar fish for {wksItem.WKSItemSubCategory.ValueNullable?.Name ?? "null"}");
                    master.Yes();
                    return true;
                }
                else
                {
                    GatherBuddy.Log.Debug($"[AutoCollectable] No CollectablesShopItem found for [{item.RowId}] {item.Name}");
                }
            }

            return false;
        }
    }
}
