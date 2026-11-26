using System;
using System.Collections.Generic;
using System.Linq;
using AetherKit.Core;
using AetherKit.Game;
using AetherKit.UI;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Lumina.Excel.Sheets;

namespace AetherKit.Modules;

/// <summary>
/// 自動Fate 模組：在目前區域（或多個區域輪替）循環打臨危受命。尋路靠 vnavmesh，輸出交給 RSR 或 Wrath Combo，
/// 躲 AOE 交給 BossMod；這裡負責挑 FATE、移動、等級同步、選目標。
/// </summary>
public sealed class AutoFateModule : IModule
{
    private readonly Plugin plugin;
    private string newPriority = string.Empty;

    public AutoFateModule(Plugin plugin)
    {
        this.plugin = plugin;
        Farmer = new FateFarmer(plugin);
    }

    public FateFarmer Farmer { get; }

    private AutoFateConfig Config => plugin.Config.AutoFate;

    public string Id => "auto-fate";
    public ModuleGroup Group => ModuleGroup.Automation;
    public bool Running => Farmer.Running;
    public string Name => "自動Fate";
    public string Description =>
        "在目前區域循環打臨危受命。需要 vnavmesh（尋路）和 RSR 或 Wrath Combo（輸出），建議再開 BossMod 躲 AOE。";

    public void Enable(WindowSystem windows) { }

    public void Disable(WindowSystem windows) => Farmer.Stop("模組已停用");

    /// <summary>頁首：控制列 → 狀態 → 需要的東西（版面規則見 ModuleUi）。</summary>
    public void DrawHeader()
    {
        if (ModuleUi.Action(Farmer.Running ? "停止" : Farmer.ReturningHome ? "取消傳送" : "開始"))
            Farmer.Toggle();
        ModuleUi.Command("/aetherkit fate");

        ModuleUi.Status(Farmer.Status);
        ModuleUi.FinishPending(plugin.Core, "自動Fate");
        if (Farmer.Running && Farmer.CurrentFate.Length > 0)
            ModuleUi.Detail("FATE", Farmer.CurrentFate);

        if (Services.ClientState.IsLoggedIn)
            ModuleUi.Detail("雙色寶石", $"{FateFarmer.Gemstones} / 1500");

        if (Farmer.Elapsed is { } elapsed)
        {
            var text = $"{elapsed:hh\\:mm\\:ss}";
            if (Config.StopOnTimer)
            {
                var left = TimeSpan.FromMinutes(Config.StopAfterMinutes) - elapsed;
                text += left > TimeSpan.Zero ? $"（{left:hh\\:mm\\:ss} 後停止）" : "（時間到，打完就停）";
            }

            ModuleUi.Detail("已執行", text);
        }

        ModuleUi.Borrows(Farmer.BorrowedCapabilities);
        ModuleUi.Dependencies(FateDependencies.Check().Select(d => new ModuleUi.Dependency(d.Name, d.Purpose, d.Level switch
        {
            FateDependencies.Level.Required => ModuleUi.DependencyLevel.Required,
            FateDependencies.Level.Recommended => ModuleUi.DependencyLevel.Recommended,
            _ => ModuleUi.DependencyLevel.Conflict
        }, d.Loaded)));
    }

    public IEnumerable<ModuleTab> SettingsTabs
    {
        get
        {
            yield return new ModuleTab("設定", DrawSettings);
            yield return new ModuleTab("FATE", DrawFateTab);
            yield return new ModuleTab("戰鬥", DrawCombatTab);
            yield return new ModuleTab("輔助", DrawSupportTab);
        }
    }

    /// <summary>輸出插件：RSR 或 Wrath Combo，指令固定。</summary>
    private void DrawRotation()
    {
        Paint.SettingsHeading("輸出");

        var rotation = Config.Rotation;
        if (ImGui.RadioButton("RSR", rotation == RotationPlugin.Rsr)) rotation = RotationPlugin.Rsr;
        ImGui.SameLine();
        if (ImGui.RadioButton("Wrath Combo", rotation == RotationPlugin.WrathCombo)) rotation = RotationPlugin.WrathCombo;

        if (rotation != Config.Rotation)
        {
            Config.Rotation = rotation;
            plugin.Save();
        }

        ImGui.TextDisabled(Config.Rotation == RotationPlugin.WrathCombo
            ? "開始打時把 Wrath 的傷害、治療選目標改成手動再開自動循環（/wrath auto on），停止時關掉（/wrath auto off），選目標模式設回原本的。"
            : "開始打時 /rotation manual（打目前選中的目標），停止時 /rotation off。");
        ImGui.TextDisabled("目標由自動Fate 選，輸出插件只打目前選中的目標。");
    }

    /// <summary>陸行鳥戰術：叫出來之後設成哪一個（名稱從遊戲文字表讀）。</summary>
    private void DrawChocoboStance()
    {
        var sheet = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.BuddyAction>();
        string StanceName(uint row) => row == 0 ? "不改" : sheet.GetRowOrDefault(row)?.Name.ExtractText() ?? $"#{row}";

        ImGui.SetNextItemWidth(120);
        if (ImGui.BeginCombo("陸行鳥戰術", StanceName(Config.ChocoboStance)))
        {
            foreach (var row in new uint[] { 7, 5, 6, 4, 0 })
            {
                if (!ImGui.Selectable(StanceName(row), row == Config.ChocoboStance)) continue;

                Config.ChocoboStance = row;
                plugin.Save();
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.TextDisabled("叫出來之後用 /cac 設定；治療戰術會幫你補血");
    }

    /// <summary>設定：輸出插件、什麼時候停、停了之後做什麼。</summary>
    private void DrawSettings()
    {
        DrawRotation();
        DrawStopConditions();
    }

    /// <summary>FATE：挑在哪裡打（區域輪替）、打哪些、哪些略過。</summary>
    private void DrawFateTab()
    {
        Paint.SettingsHeading("區域輪替");
        DrawZones();

        Paint.SettingsHeading("略過的 FATE");
        DrawBlacklist();

        DrawFatePicking();
    }

    /// <summary>戰鬥：站多近打、坦克姿態、菁英怪、優先打的怪。</summary>
    private void DrawCombatTab()
    {
        DrawCombatRange();

        Paint.SettingsHeading("優先目標");
        DrawPriority();
    }

    private void DrawCombatRange()
    {
        Paint.SettingsHeading("戰鬥距離");
        ImGui.TextDisabled("離目標邊緣多近才停下來打。距離不夠時會一路走過去，不會走走停停。");

        var melee = Config.MeleeRange;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderFloat("近戰（坦克、近戰）", ref melee, 1f, 5f, "%.1f 米"))
        {
            Config.MeleeRange = melee;
            plugin.Save();
        }

        var ranged = Config.RangedRange;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderFloat("遠程、治療", ref ranged, 5f, 24f, "%.1f 米"))
        {
            Config.RangedRange = ranged;
            plugin.Save();
        }

        Paint.SettingsHeading("戰鬥中");

        var stance = Config.TankStance;
        if (ImGui.Checkbox("坦克職業進 FATE 時自動開防禦姿態", ref stance))
        {
            Config.TankStance = stance;
            plugin.Save();
        }

        var flee = Config.FleeElites;
        if (ImGui.Checkbox("被野外菁英怪纏上時跑遠甩開（不打）", ref flee))
        {
            Config.FleeElites = flee;
            plugin.Save();
        }

        ImGui.TextDisabled("菁英怪 = 名字旁有怪物等級標記、又不屬於任何 FATE 的怪。騎著就繼續飛遠，地上就用跑的；30 秒甩不掉就照常打。");
    }

    private void DrawStopConditions()
    {
        Paint.SettingsHeading("停止條件");

        var stopOnGems = Config.StopOnGemstones;
        if (ImGui.Checkbox("雙色寶石達到數量時停止", ref stopOnGems))
        {
            Config.StopOnGemstones = stopOnGems;
            plugin.Save();
        }

        if (Config.StopOnGemstones)
        {
            var limit = Config.GemstoneLimit;
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("數量", ref limit, 50))
            {
                Config.GemstoneLimit = System.Math.Clamp(limit, 1, 1500);
                plugin.Save();
            }

            ImGui.TextDisabled("上限 1500。");
        }

        var stopOnTimer = Config.StopOnTimer;
        if (ImGui.Checkbox("執行一段時間後停止", ref stopOnTimer))
        {
            Config.StopOnTimer = stopOnTimer;
            plugin.Save();
        }

        if (Config.StopOnTimer)
        {
            var minutes = Config.StopAfterMinutes;
            ImGui.SetNextItemWidth(120);
            if (ImGui.InputInt("分鐘", ref minutes, 30))
            {
                Config.StopAfterMinutes = Math.Clamp(minutes, 1, 24 * 60);
                plugin.Save();
            }

            ImGui.SameLine();
            ImGui.TextDisabled($"（{Config.StopAfterMinutes / 60} 小時 {Config.StopAfterMinutes % 60} 分）");
        }

        ImGui.TextDisabled("條件達到時如果還在戰鬥，會先打完身上的怪再停。");

        var home = Config.TeleportOnStop;
        if (ImGui.Checkbox("自動停止後傳送", ref home))
        {
            Config.TeleportOnStop = home;
            plugin.Save();
        }

        if (Config.TeleportOnStop)
        {
            ImGui.SameLine();
            StopTeleportCombo();
        }

        ImGui.TextDisabled("寶石到量、時間到、移動一直卡住、出錯停止時都會傳；手動停止、死亡不會傳。指定的地點不在傳送清單裡時，改傳這張地圖最近的以太之光。");

        var after = Config.AfterFinish;
        if (ModuleUi.FinishActionCombo("寶石到量、時間到之後", ref after))
        {
            Config.AfterFinish = after;
            plugin.Save();
        }

        ImGui.TextDisabled($"有開回水晶就等傳送完；倒數 {FinishService.CountdownSeconds} 秒，可以在上面取消。手動停止、出錯、卡住不會登出或關閉。");
    }

    private void DrawFatePicking()
    {
        Paint.SettingsHeading("挑選 FATE");

        var bonus = Config.PreferBonus;
        if (ImGui.Checkbox("有加成（★）的一律優先，趕路中出現也會改去", ref bonus))
        {
            Config.PreferBonus = bonus;
            plugin.Save();
        }

        ImGui.TextUnformatted("要打的類型：");
        foreach (var (kind, label, hint) in FateKinds.All)
        {
            var on = Config.Kinds.Contains(kind);
            if (ImGui.Checkbox($"{label}##kind", ref on))
            {
                if (on) Config.Kinds.Add(kind);
                else Config.Kinds.Remove(kind);

                plugin.Save();
            }

            ImGui.SameLine();
            ImGui.TextDisabled(hint);
        }

        ImGui.Spacing();
        if (Config.Kinds.Contains(FateKind.Collect))
        {
            var turnIn = Config.TurnInAt;
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderInt("繳交型：道具幾個就去交", ref turnIn, 1, 20))
            {
                Config.TurnInAt = turnIn;
                plugin.Save();
            }

            ImGui.TextDisabled("7 個以上算全額貢獻。FATE 進度 90% 以上或剩不到 1 分鐘時，不管幾個都先交。\n繳交靠核心的「對話推進」和「交道具」，其中一個設成「關」就不會去打繳交型。");
        }

        var npc = Config.StartNpcFates;
        if (ImGui.Checkbox("要找 NPC 對話才開始的 FATE，自動過去開始", ref npc))
        {
            Config.StartNpcFates = npc;
            plugin.Save();
        }

        var seconds = Config.MinSecondsLeft;
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("剩餘秒數少於這個就不去", ref seconds, 10))
        {
            Config.MinSecondsLeft = System.Math.Clamp(seconds, 0, 1800);
            plugin.Save();
        }

        var progress = Config.MaxProgress;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderInt("進度超過這個就不去", ref progress, 0, 100, "%d%%"))
        {
            Config.MaxProgress = progress;
            plugin.Save();
        }
    }

    private void DrawMount()
    {
        Paint.SettingsHeading("坐騎");

        var mount = Config.UseMount;
        if (ImGui.Checkbox("距離遠時自動上坐騎（坐騎隨機）", ref mount))
        {
            Config.UseMount = mount;
            plugin.Save();
        }

        if (Config.UseMount)
        {
            var distance = Config.MountDistance;
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderFloat("超過這個距離才騎", ref distance, 10f, 100f, "%.0f 米"))
            {
                Config.MountDistance = distance;
                plugin.Save();
            }
        }

        Paint.SettingsHeading("傳送");

        var shortcut = Config.ShortcutTeleport;
        if (ImGui.Checkbox("同一張地圖有離 FATE 比較近的以太之光時，先傳送過去再走", ref shortcut))
        {
            Config.ShortcutTeleport = shortcut;
            plugin.Save();
        }

        if (Config.ShortcutTeleport)
        {
            var saving = Config.ShortcutSaving;
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderFloat("至少省下這麼多距離才傳送", ref saving, 100f, 1000f, "%.0f 米"))
            {
                Config.ShortcutSaving = saving;
                plugin.Save();
            }
        }
    }

    /// <summary>輔助：坐騎、陸行鳥、修理、拒絕交易。</summary>
    private void DrawSupportTab()
    {
        DrawMount();

        Paint.SettingsHeading("陸行鳥");

        var chocobo = Config.SummonChocobo;
        if (ImGui.Checkbox("陸行鳥夥伴快到時間或不在時，餵基薩爾野菜叫出來", ref chocobo))
        {
            Config.SummonChocobo = chocobo;
            plugin.Save();
        }

        if (Config.SummonChocobo)
        {
            var minutes = Config.ChocoboMinSeconds / 60;
            ImGui.SetNextItemWidth(120);
            if (ImGui.SliderInt("剩不到幾分鐘就續", ref minutes, 1, 30, "%d 分"))
            {
                Config.ChocoboMinSeconds = minutes * 60;
                plugin.Save();
            }

            DrawChocoboStance();
        }

        Paint.SettingsHeading("其他");

        var trade = Config.RefuseTrade;
        if (ImGui.Checkbox("執行期間自動拒絕別人發起的交易", ref trade))
        {
            Config.RefuseTrade = trade;
            plugin.Save();
        }

        var repair = Config.AutoRepair;
        if (ImGui.Checkbox("裝備耐久低時自動修理", ref repair))
        {
            Config.AutoRepair = repair;
            plugin.Save();
        }

        if (Config.AutoRepair)
        {
            ImGui.TextDisabled($"耐久低於 {plugin.Config.Core.RepairBelowPercent}% 時自己修理（身上要帶暗物質）。門檻在核心能力頁設定，所有功能共用。");
            ImGui.SameLine();
            if (ImGui.SmallButton("修改##repair"))
                plugin.OpenCorePage();
        }

        var materialize = Config.AutoMaterialize;
        if (ImGui.Checkbox("裝備精煉度滿時自動精製魔晶石", ref materialize))
        {
            Config.AutoMaterialize = materialize;
            plugin.Save();
        }

        if (Config.AutoMaterialize)
            ImGui.TextDisabled("兩場 FATE 之間，身上或兵裝庫有精煉度 100% 的裝備就開精製視窗全部精製（會自動切換分類）。");
    }

    private uint zoneToAdd;

    private void DrawZones()
    {
        var multi = Config.MultiZone;
        if (ImGui.Checkbox("多區域輪替", ref multi))
        {
            Config.MultiZone = multi;
            plugin.Save();
        }

        ImGui.TextDisabled("關閉時只打目前所在的區域，沒有 FATE 就原地等。\n開啟時目前區域沒有能打的 FATE，就依下面的順序傳送到下一個區域；不在清單裡的區域開始時，會先傳到第一個。");

        var wait = Config.ZoneWaitSeconds;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderInt("沒有 FATE 等多久才換", ref wait, 0, 300, "%d 秒"))
        {
            Config.ZoneWaitSeconds = wait;
            plugin.Save();
        }

        Paint.SettingsHeading("輪替順序");

        int? remove = null, up = null;
        for (var i = 0; i < Config.Zones.Count; i++)
        {
            var zone = Config.Zones[i];
            var here = zone == Services.ClientState.TerritoryType ? "  ← 目前所在" : string.Empty;

            if (ImGui.SmallButton($"上移##up{i}") && i > 0) up = i;
            ImGui.SameLine();
            if (ImGui.SmallButton($"移除##rm{i}")) remove = i;
            ImGui.SameLine();
            ImGui.TextUnformatted($"{i + 1}. {Aetherytes.ZoneName(zone)}{here}");
        }

        if (Config.Zones.Count == 0)
            ImGui.TextDisabled("還沒有加入區域。");

        if (up is { } u)
        {
            (Config.Zones[u - 1], Config.Zones[u]) = (Config.Zones[u], Config.Zones[u - 1]);
            plugin.Save();
        }

        if (remove is { } r)
        {
            Config.Zones.RemoveAt(r);
            plugin.Save();
        }

        ImGui.Spacing();

        var current = (uint)Services.ClientState.TerritoryType;
        if (current != 0 && !Config.Zones.Contains(current) && ImGui.Button($"加入目前區域：{Aetherytes.ZoneName(current)}"))
        {
            Config.Zones.Add(current);
            plugin.Save();
        }

        // 只列出有解鎖大水晶的區域，傳得過去才有意義
        if (!Services.ClientState.IsLoggedIn) return;

        var zones = Aetherytes.Unlocked()
            .Select(a => a.Territory)
            .Distinct()
            .Where(z => !Config.Zones.Contains(z))
            .Select(z => (Id: z, Name: Aetherytes.ZoneName(z)))
            .OrderBy(z => z.Name)
            .ToList();

        ImGui.SetNextItemWidth(220);
        var preview = zones.FirstOrDefault(z => z.Id == zoneToAdd).Name ?? "選擇區域…";
        if (ImGui.BeginCombo("##zoneAdd", preview))
        {
            foreach (var (id, name) in zones)
            {
                if (ImGui.Selectable($"{name}##{id}", id == zoneToAdd))
                    zoneToAdd = id;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("加入") && zoneToAdd != 0 && !Config.Zones.Contains(zoneToAdd))
        {
            Config.Zones.Add(zoneToAdd);
            zoneToAdd = 0;
            plugin.Save();
        }
    }

    private void DrawPriority()
    {
        ImGui.TextDisabled("這些怪在範圍內出現時，放下手上的目標先去打。預設是迷失少女和迷失者。");

        var range = Config.PriorityRange;
        ImGui.SetNextItemWidth(120);
        if (ImGui.SliderFloat("範圍", ref range, 20f, 100f, "%.0f 米"))
        {
            Config.PriorityRange = range;
            plugin.Save();
        }

        Paint.SettingsHeading("怪物");

        var sheet = Services.Data.GetExcelSheet<BNpcName>();
        uint? remove = null;

        foreach (var id in Config.PriorityNames)
        {
            var name = sheet.GetRowOrDefault(id)?.Singular.ExtractText();
            ImGui.TextUnformatted($"{(string.IsNullOrEmpty(name) ? "（找不到名稱）" : name)}  #{id}");
            ImGui.SameLine();
            if (ImGui.SmallButton($"移除##{id}"))
                remove = id;
        }

        if (remove is { } r)
        {
            Config.PriorityNames.Remove(r);
            plugin.Save();
        }

        ImGui.Spacing();
        ImGui.SetNextItemWidth(200);
        ImGui.InputText("名稱或 ID", ref newPriority, 100);
        ImGui.SameLine();
        if (ImGui.Button("加入"))
            AddPriority(sheet);

        if (Services.Targets.Target is Dalamud.Game.ClientState.Objects.Types.IBattleNpc target)
        {
            if (ImGui.Button($"加入目前目標：{target.Name}"))
            {
                if (!Config.PriorityNames.Contains(target.NameId))
                    Config.PriorityNames.Add(target.NameId);

                plugin.Save();
            }
        }
    }

    private void AddPriority(Lumina.Excel.ExcelSheet<BNpcName> sheet)
    {
        var text = newPriority.Trim();
        if (text.Length == 0) return;

        if (uint.TryParse(text, out var id))
        {
            if (!Config.PriorityNames.Contains(id))
                Config.PriorityNames.Add(id);
        }
        else
        {
            var matches = sheet.Where(r => r.Singular.ExtractText() == text).Select(r => r.RowId).ToList();
            if (matches.Count == 0)
            {
                Notify.Error("自動Fate", $"找不到名稱是「{text}」的怪物。");
                return;
            }

            foreach (var match in matches.Where(m => !Config.PriorityNames.Contains(m)))
                Config.PriorityNames.Add(match);
        }

        newPriority = string.Empty;
        plugin.Save();
    }

    private void DrawBlacklist()
    {
        ImGui.TextDisabled("勾選的 FATE 不會去打。清單是目前區域正在進行的 FATE，加上之前勾過的。");

        if (Config.FateDeaths.Count > 0)
        {
            if (ImGui.Button("清除死亡次數"))
            {
                Config.FateDeaths.Clear();
                plugin.Save();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("把每場 FATE 的死亡次數歸零（不動黑名單）。");
        }

        ImGui.TextDisabled("死過的 FATE 會一直列在這裡、標出死了幾次，死得多的排前面。");

        var sheet = Services.Data.GetExcelSheet<Fate>();
        var ids = Services.Fates.Select(f => (uint)f.FateId)
            .Union(Config.Blacklist)
            .Union(Config.FateDeaths.Keys)
            .Distinct()
            .OrderByDescending(id => Config.FateDeaths.GetValueOrDefault(id))
            .ToList();

        if (ids.Count == 0)
        {
            ImGui.TextDisabled("這個區域目前沒有 FATE。");
            return;
        }

        foreach (var id in ids)
        {
            var name = sheet.GetRowOrDefault(id)?.Name.ExtractText();
            var skip = Config.Blacklist.Contains(id);
            var changed = ImGui.Checkbox($"{(string.IsNullOrEmpty(name) ? $"FATE #{id}" : name)}##{id}", ref skip);

            if (Config.FateDeaths.TryGetValue(id, out var died))
            {
                ImGui.SameLine();
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.45f, 0.45f, 1f), $"死了 {died} 次");
            }

            if (!changed) continue;

            if (skip) Config.Blacklist.Add(id);
            else Config.Blacklist.Remove(id);

            plugin.Save();
        }
    }

    public bool HasSettings(Configuration config) => true;

    public void CopySettings(Configuration from, Configuration to)
    {
        var f = from.AutoFate;
        to.AutoFate = new AutoFateConfig
        {
            MeleeRange = f.MeleeRange,
            RangedRange = f.RangedRange,
            StopOnGemstones = f.StopOnGemstones,
            GemstoneLimit = f.GemstoneLimit,
            StopOnTimer = f.StopOnTimer,
            StopAfterMinutes = f.StopAfterMinutes,
            UseMount = f.UseMount,
            MountDistance = f.MountDistance,
            ShortcutTeleport = f.ShortcutTeleport,
            ShortcutSaving = f.ShortcutSaving,
            PreferBonus = f.PreferBonus,
            MinSecondsLeft = f.MinSecondsLeft,
            MaxProgress = f.MaxProgress,
            Kinds = new List<FateKind>(f.Kinds),
            StartNpcFates = f.StartNpcFates,
            TankStance = f.TankStance,
            RefuseTrade = f.RefuseTrade,
            SummonChocobo = f.SummonChocobo,
            ChocoboMinSeconds = f.ChocoboMinSeconds,
            ChocoboStance = f.ChocoboStance,
            TurnInAt = f.TurnInAt,
            AutoRepair = f.AutoRepair,
            AutoMaterialize = f.AutoMaterialize,
            FleeElites = f.FleeElites,
            Rotation = f.Rotation,
            PriorityNames = new List<uint>(f.PriorityNames),
            PriorityRange = f.PriorityRange,
            Blacklist = new List<uint>(f.Blacklist),
            FateDeaths = to.AutoFate.FateDeaths, // 紀錄不是設定，留著對方自己的
            TeleportOnStop = f.TeleportOnStop,
            StopAetheryte = f.StopAetheryte,
            StopAetheryteSub = f.StopAetheryteSub,
            AfterFinish = f.AfterFinish,
            MultiZone = f.MultiZone,
            Zones = new List<uint>(f.Zones),
            ZoneWaitSeconds = f.ZoneWaitSeconds,
        };
    }

    /// <summary>自動停止後傳送到哪：這張地圖最近的以太之光、住宅，或指定一個已解鎖的以太之光。</summary>
    private void StopTeleportCombo()
    {
        const string nearest = "這張地圖最近的以太之光";
        var loggedIn = Services.ClientState.IsLoggedIn;
        var housing = loggedIn ? Aetherytes.Housing() : new List<Aetherytes.Entry>();
        var crystals = loggedIn
            ? Aetherytes.Unlocked().OrderBy(a => Aetherytes.ZoneName(a.Territory)).ThenBy(a => a.Name).ToList()
            : new List<Aetherytes.Entry>();

        string Label(Aetherytes.Entry a) => a.Housing ? a.Name : $"{Aetherytes.ZoneName(a.Territory)}：{a.Name}";
        bool IsChosen(Aetherytes.Entry a) => a.Id == Config.StopAetheryte && a.Sub == Config.StopAetheryteSub;

        var chosen = housing.Concat(crystals).Where(IsChosen).Cast<Aetherytes.Entry?>().FirstOrDefault();
        var preview = Config.StopAetheryte == 0 ? nearest
            : chosen is { } c ? Label(c)
            : $"傳送點 #{Config.StopAetheryte}（不在傳送清單裡）";

        ImGui.SetNextItemWidth(320);
        if (!ImGui.BeginCombo("##stopTeleport", preview, ImGuiComboFlags.HeightLarge)) return;

        if (ImGui.Selectable(nearest, Config.StopAetheryte == 0))
            SetStopTeleport(0, 0);

        if (housing.Count > 0) ImGui.Separator();
        foreach (var a in housing)
            if (ImGui.Selectable($"{Label(a)}##h{a.Id}_{a.Sub}", IsChosen(a)))
                SetStopTeleport(a.Id, a.Sub);

        if (crystals.Count > 0) ImGui.Separator();
        foreach (var a in crystals)
            if (ImGui.Selectable($"{Label(a)}##a{a.Id}", IsChosen(a)))
                SetStopTeleport(a.Id, 0);

        ImGui.EndCombo();
    }

    private void SetStopTeleport(uint id, byte sub)
    {
        Config.StopAetheryte = id;
        Config.StopAetheryteSub = sub;
        plugin.Save();
    }

    public void Dispose() => Farmer.Dispose();
}
