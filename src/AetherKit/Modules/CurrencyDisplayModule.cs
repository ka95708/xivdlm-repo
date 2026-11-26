using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AetherKit.Game;
using AetherKit.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace AetherKit.Modules;

/// <summary>
/// 更多貨幣顯示：在遊戲右下角金幣的上方或下方，多顯示幾種貨幣（神典石、軍票、各種幣……），樣式跟金幣那一列一樣。
/// 設定：選上方或下方；清單照畫面由上往下排、可以拖曳；新增時只列出貨幣類（遊戲分類「貨幣」，加上軍票、神典石這類特殊貨幣）。
/// </summary>
public sealed class CurrencyDisplayModule : IModule
{
    // 物品分類「貨幣」
    private const uint CurrencyCategory = 100;

    // 軍票、神典石、狼印戰績、金碟幣……：物品編號 100 以內、分類「其他」、排序分類 3 的特殊貨幣（不含金幣本身）
    private const uint SpecialCurrencyMaxId = 100;
    private const uint OtherCategory = 63;
    private const uint CurrencySortCategory = 3;
    private const uint Gil = 1;

    private readonly Plugin plugin;
    private string search = string.Empty;
    private List<(uint Id, string Name)>? currencies;

    public CurrencyDisplayModule(Plugin plugin)
    {
        this.plugin = plugin;
        Display = new CurrencyDisplay(plugin);
    }

    public CurrencyDisplay Display { get; }

    private CurrencyDisplayConfig Config => plugin.Config.CurrencyDisplay;

    public string Id => "currency-display";
    public string Name => "更多貨幣顯示";
    public string Description => "在遊戲右下角金幣的上方或下方，多顯示幾種貨幣（神典石、軍票、各種幣……），樣式跟金幣那一列一樣。";

    public void Enable(WindowSystem windows) => Display.Start();

    public void Disable(WindowSystem windows) => Display.Stop();

    /// <summary>頁首：狀態（版面規則見 ModuleUi）。</summary>
    public void DrawHeader()
    {
        var shown = Config.Currencies.Count(c => c.Enabled);
        ModuleUi.Status(shown == 0
            ? "還沒有要顯示的貨幣，到「設定」新增"
            : $"在金幣{(Config.Area == CurrencyArea.Above ? "上方" : "下方")}顯示 {shown} 種貨幣");
    }

    public IEnumerable<ModuleTab> SettingsTabs
    {
        get { yield return new ModuleTab("設定", DrawSettings); }
    }

    private void DrawSettings()
    {
        ModuleUi.Section("位置");

        var above = Config.Area == CurrencyArea.Above;
        if (ImGui.RadioButton("金幣上方", above))
        {
            Config.Area = CurrencyArea.Above;
            Changed();
        }

        ImGui.SameLine();
        if (ImGui.RadioButton("金幣下方", !above))
        {
            Config.Area = CurrencyArea.Below;
            Changed();
        }

        ModuleUi.Section("顯示的貨幣");
        DrawList();

        ModuleUi.Section("新增貨幣");
        DrawAdd();
    }

    /// <summary>貨幣清單：照畫面由上往下；拖曳名稱排序；勾選顯示；刪除。</summary>
    private void DrawList()
    {
        if (Config.Currencies.Count == 0)
        {
            ModuleUi.Note("還沒有貨幣，從下面新增。");
            return;
        }

        ModuleUi.Note(Config.Area == CurrencyArea.Above
            ? "照畫面由上往下排，最下面一個緊貼在金幣上方。拖曳名稱可以調整順序。"
            : "照畫面由上往下排，最上面一個緊貼在金幣下方。拖曳名稱可以調整順序。");

        var iconSize = new Vector2(ImGui.GetFrameHeight());
        for (var i = 0; i < Config.Currencies.Count; i++)
        {
            var entry = Config.Currencies[i];
            // 用物品編號當 ID（不是第幾列）：換位置後拖曳還跟著同一個貨幣，才能一路拖到底
            ImGui.PushID((int)entry.ItemId);

            var enabled = entry.Enabled;
            if (ImGui.Checkbox("##on", ref enabled))
            {
                entry.Enabled = enabled;
                Changed();
            }

            ImGui.SameLine();
            DrawIcon(entry.ItemId, iconSize);
            ImGui.SameLine();

            ImGui.Selectable($"{entry.Name}##name", false, ImGuiSelectableFlags.None, new Vector2(240, iconSize.Y));

            // 拖曳排序：按住名稱上下拖，經過鄰居就跟它換位置
            if (ImGui.IsItemActive() && !ImGui.IsItemHovered())
            {
                var target = i + (ImGui.GetMouseDragDelta(ImGuiMouseButton.Left).Y < 0 ? -1 : 1);
                if (target >= 0 && target < Config.Currencies.Count)
                {
                    (Config.Currencies[i], Config.Currencies[target]) = (Config.Currencies[target], Config.Currencies[i]);
                    ImGui.ResetMouseDragDelta();
                    Changed();
                }
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("刪除"))
            {
                Config.Currencies.RemoveAt(i);
                Changed();
                ImGui.PopID();
                break;
            }

            ImGui.PopID();
        }
    }

    /// <summary>新增：列出還沒加的貨幣（可以打字篩選），點一下加到清單最後。</summary>
    private void DrawAdd()
    {
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##search", "篩選名稱……", ref search, 64);

        var text = search.Trim();
        var candidates = AllCurrencies()
            .Where(c => Config.Currencies.All(e => e.ItemId != c.Id) && (text.Length == 0 || c.Name.Contains(text)))
            .ToList();

        if (candidates.Count == 0)
        {
            ModuleUi.Note(text.Length == 0 ? "所有貨幣都已經在清單裡了。" : "沒有符合的貨幣。");
            return;
        }

        var iconSize = new Vector2(ImGui.GetFrameHeight());
        if (ImGui.BeginChild("##candidates", new Vector2(320, iconSize.Y * 8), true))
        {
            foreach (var (id, name) in candidates)
            {
                ImGui.PushID((int)id);
                DrawIcon(id, iconSize);
                ImGui.SameLine();
                if (ImGui.Selectable(name, false, ImGuiSelectableFlags.None, new Vector2(0, iconSize.Y)))
                {
                    Config.Currencies.Add(new CurrencyEntry { ItemId = id, Name = name });
                    Changed();
                }

                ImGui.PopID();
            }
        }

        // BeginChild 回傳 false 也一定要 EndChild
        ImGui.EndChild();
    }

    /// <summary>所有貨幣類物品：分類「貨幣」，加上軍票、神典石、狼印戰績、金碟幣這類特殊貨幣（不含金幣）。</summary>
    private List<(uint Id, string Name)> AllCurrencies()
    {
        if (currencies != null) return currencies;

        currencies = Services.Data.GetExcelSheet<Item>()
            .Where(item => item.Icon != 0 && item.Name.ExtractText().Length > 0
                           && (item.ItemUICategory.RowId == CurrencyCategory
                               || (item.RowId < SpecialCurrencyMaxId && item.RowId != Gil
                                   && item.ItemUICategory.RowId == OtherCategory
                                   && item.ItemSortCategory.RowId == CurrencySortCategory)))
            .Select(item => (item.RowId, item.Name.ExtractText()))
            .ToList();
        return currencies;
    }

    private static void DrawIcon(uint itemId, Vector2 size)
    {
        var icon = Services.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Icon ?? 0u;
        var texture = Icons.Get(icon);
        if (texture != null) ImGui.Image(texture.Handle, size);
        else ImGui.Dummy(size);
    }

    private void Changed()
    {
        plugin.Save();
        Display.Refresh();
    }

    public bool HasSettings(Configuration config) => config.CurrencyDisplay.Currencies.Count > 0;

    public void CopySettings(Configuration from, Configuration to)
    {
        var f = from.CurrencyDisplay;
        to.CurrencyDisplay = new CurrencyDisplayConfig
        {
            Area = f.Area,
            Currencies = f.Currencies.Select(c => new CurrencyEntry { ItemId = c.ItemId, Name = c.Name, Enabled = c.Enabled }).ToList()
        };

        if (to == plugin.Config) Display.Refresh();
    }

    public void Dispose() => Display.Dispose();
}
