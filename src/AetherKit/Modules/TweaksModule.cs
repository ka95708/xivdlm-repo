using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using AetherKit.Tweaks;
using AetherKit.UI;
using Newtonsoft.Json;

namespace AetherKit.Modules;

/// <summary>
/// 小功能：所有掛在遊戲視窗或事件上的小改動都收在這一個模組，一個清單管理。
/// 清單依遊戲系統分組、可以搜尋；有選項的功能在清單上直接展開。
/// 每個小功能都有自己的開關，所以這個模組本身不能關。
/// 新增小功能：寫一個 Tweak 子類別，加進下面的 All。
/// </summary>
public sealed class TweaksModule : IModule
{
    private readonly Plugin plugin;
    private readonly HashSet<string> expanded = new();
    private AddonBar bar => plugin.AddonBar;

    private string search = string.Empty;

    public TweaksModule(Plugin plugin)
    {
        this.plugin = plugin;

        All = new Tweak[]
        {
            new Desynthesis(),
            new AetherialReduction(),
            new Materialize(),
            new CollectableExchange(),
            new MiniCactpot(),
            new SellCards(),
            new WeeklyBingoOpen(),
            new StoreToCabinet(),
            new ArmouryNotInGearset(),
            new RemoveDuplicateGlamours(),
            new RemoveArmoireItemsFromDresser(),
            new OutfitTakeOut(),
            new OutfitCombine(),
            new CollectableConfirm(),
            new PortraitUpdateConfirm(),
            new PortraitAnimationFrames(),
            new PartyFinderRefresh(),
            new CopyItemName(),
            new NearbyPlayers(),
            new AutoDismount(),
            new DutyFinderQuickSettings(),
            new LootSelectNext(),
            new AutoOpenLoot(),
            new AutoOpenCommend(),
            new AutoLockHotbar(),
            new CameraMaxDistance(),
            new AlwaysYes(),
            new GlamourNameTooltip(),
            new LootWindowMarks(),
            new ShiftQuickTransfer()
        };

        foreach (var tweak in All)
            tweak.Attach(plugin);
    }

    /// <summary>清單的預設順序（同一組裡照這個順序）。</summary>
    public IReadOnlyList<Tweak> All { get; }

    public string Id => "tweaks";
    public string Name => "小功能";
    public string Description => "各種掛在遊戲視窗上的小改動，各自開關。";

    public bool CanDisable => false;

    // 頁面由 ConfigWindow 的小功能區畫（搜尋頁、分組頁），不用模組頁
    public Action DrawPage => DrawSearch;

    private TweaksConfig Config => plugin.Config.Tweaks;

    public void Enable(WindowSystem windows)
    {
        foreach (var tweak in All)
            tweak.SetEnabled(Config.Enabled.Contains(tweak.Id));

        Services.Framework.Update += OnUpdate;
    }

    public void Disable(WindowSystem windows)
    {
        Services.Framework.Update -= OnUpdate;

        foreach (var tweak in All)
            tweak.SetEnabled(false);
    }

    private void OnUpdate(IFramework framework)
    {
        if (!Services.ClientState.IsLoggedIn) return;

        var now = DateTime.Now;
        foreach (var tweak in All)
        {
            if (!tweak.Enabled) continue;

            try
            {
                tweak.Update(now);
            }
            catch (Exception e)
            {
                if (tweak.LogError(e))
                    Services.Log.Error(e, $"小功能「{tweak.Name}」發生錯誤");
            }
        }
    }

    public void AfterWindows()
    {
        if (!Services.ClientState.IsLoggedIn) return;

        // 按鈕列是所有模組共用的（Plugin 每幀清空、畫出），這裡只登記
        foreach (var tweak in All)
        {
            if (!tweak.Enabled) continue;

            tweak.AddButtons(bar);
            tweak.Draw();
        }
    }

    // ── 開關（清單與指令共用） ─────────────────────────────────

    public void SetTweak(Tweak tweak, bool on)
    {
        tweak.SetEnabled(on);
        Config.Enabled.Remove(tweak.Id);
        if (on) Config.Enabled.Add(tweak.Id);
        plugin.Save();
    }

    /// <summary>用名稱（或 Id）找小功能，給指令用。完全相同優先，其次是名稱包含這段文字的唯一一個。</summary>
    public Tweak? Find(string name)
    {
        var exact = All.FirstOrDefault(t => t.Name == name || t.Id.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        var partial = All.Where(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] : null;
    }

    // ── 清單 ──────────────────────────────────────────────────

    // 側邊欄「小功能」一區：第一項是搜尋頁，下面一個分組一頁（ConfigWindow 依頁面呼叫這兩個）

    /// <summary>搜尋頁：只顯示搜尋到的，沒輸入時什麼都不列。結果照分組排，每組前面一個小標題。</summary>
    public void DrawSearch()
    {
        ImGui.SetNextItemWidth(260);
        ImGui.InputTextWithHint("##search", "搜尋名稱或說明……", ref search, 64);
        DrawHints();

        if (search.Trim().Length == 0)
        {
            ModuleUi.Note("輸入名稱或說明裡的字，找出符合的小功能。");
            return;
        }

        var shown = All.Where(Matches).ToList();
        if (shown.Count == 0)
        {
            ModuleUi.Note("沒有符合的小功能。");
            return;
        }

        foreach (var group in Enum.GetValues<TweakGroup>())
        {
            var items = shown.Where(t => t.Group == group).ToList();
            if (items.Count == 0) continue;

            ModuleUi.Section(group.Name());
            foreach (var tweak in items)
                DrawRow(tweak);
        }
    }

    /// <summary>分組頁：這一組的全部小功能。</summary>
    public void DrawGroup(TweakGroup group)
    {
        var items = All.Where(t => t.Group == group).ToList();
        ModuleUi.Detail("已開啟", $"{items.Count(t => t.Enabled)} / {items.Count}");
        DrawHints();

        foreach (var tweak in items)
            DrawRow(tweak);
    }

    /// <summary>每頁上方共用的提示：指令。</summary>
    private static void DrawHints()
    {
        ModuleUi.Note("指令：/aetherkit tweak 名稱 [on|off]（可以放進工具列按鈕或快捷鍵）。");
        ImGui.Separator();
        ImGui.Spacing();
    }

    private bool Matches(Tweak tweak)
    {
        var text = search.Trim();
        return tweak.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
               || tweak.Description.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>一行：開關、名稱、運作方式、說明；有選項的可以展開。</summary>
    private void DrawRow(Tweak tweak)
    {
        ImGui.PushID(tweak.Id);

        var on = tweak.Enabled;
        if (ImGui.Checkbox(tweak.Name, ref on))
            SetTweak(tweak, on);

        ImGui.SameLine();
        ImGui.TextDisabled($"［{tweak.Kind.Name()}］");

        if (tweak.HasSettings)
        {
            ImGui.SameLine();
            var open = expanded.Contains(tweak.Id);
            if (ImGui.SmallButton(open ? "收起" : "選項"))
            {
                if (open) expanded.Remove(tweak.Id);
                else expanded.Add(tweak.Id);
            }
        }

        ImGui.Indent(ImGui.GetFrameHeight() * 1.5f);
        ImGui.TextDisabled(tweak.Where == null ? tweak.Description : $"{tweak.Description}（{tweak.Where}）");

        if (tweak.HasSettings && expanded.Contains(tweak.Id))
            tweak.DrawSettings();

        ImGui.Unindent(ImGui.GetFrameHeight() * 1.5f);
        ImGui.Spacing();
        ImGui.PopID();
    }

    // ── 匯入匯出 ──────────────────────────────────────────────

    public bool HasSettings(Configuration config) => config.Tweaks.Enabled.Count > 0;

    public void CopySettings(Configuration from, Configuration to)
    {
        to.Tweaks = JsonConvert.DeserializeObject<TweaksConfig>(JsonConvert.SerializeObject(from.Tweaks)) ?? new TweaksConfig();

        // 套用到目前設定時，各小功能的開關要跟著新的設定走
        if (to == plugin.Config && plugin.IsEnabled(this))
        {
            foreach (var tweak in All)
                tweak.SetEnabled(to.Tweaks.Enabled.Contains(tweak.Id));
        }
    }

    public void Dispose()
    {
        foreach (var tweak in All)
            tweak.SetEnabled(false);
    }
}
