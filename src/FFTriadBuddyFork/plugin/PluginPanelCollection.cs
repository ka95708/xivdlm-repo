using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using FFTriadBuddy;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using TriadBuddy;

namespace TriadBuddyPlugin
{
    /// <summary>
    /// 卡片收集分頁（fork 新增）：不用打開遊戲的幻卡列表，直接從遊戲記憶體讀「學會了哪些卡」，
    /// 從背包讀「有哪些卡片道具還沒用」，列出每個 NPC 還缺哪些卡、每張缺的卡要去找誰。
    /// </summary>
    public unsafe class PluginPanelCollection
    {
        private class NpcEntry
        {
            public TriadNpc Npc = null!;
            public GameNpcInfo Info = null!;
            public string Name = string.Empty;
            public int Missing;
            public int InInventory;
        }

        private class CardEntry
        {
            public TriadCard Card = null!;
            public GameCardInfo Info = null!;
            public string Name = string.Empty;
            public int InInventory;
            public string Npcs = string.Empty;
        }

        private readonly Vector4 colorOk = new(0.2f, 0.9f, 0.2f, 1);
        private readonly Vector4 colorYellow = new(0.9f, 0.9f, 0.2f, 1);
        private readonly Vector4 colorInactive = new(0.5f, 0.5f, 0.5f, 1);

        private readonly List<NpcEntry> npcs = new();
        private readonly Dictionary<int, NpcEntry> npcById = new();
        private CardEntry? selectedCard;
        private readonly List<CardEntry> missingCards = new();
        private readonly Dictionary<int, int> inventoryByCard = new();

        private int numOwned;
        private int numTotal;
        private bool hasData;
        private string npcFilter = string.Empty;
        private string cardFilter = string.Empty;
        private bool npcMissingOnly = true;
        private bool cardNpcOnly = true;
        private NpcEntry? selectedNpc;

        private readonly NpcTravel travel;
        private readonly PluginWindowDeckOptimize optimizer;

        public PluginPanelCollection(NpcTravel travel, PluginWindowDeckOptimize optimizer)
        {
            this.travel = travel;
            this.optimizer = optimizer;
        }

        /// <summary>切到這個分頁時重新讀一次。</summary>
        public void OnShown() => Refresh();

        public void Refresh()
        {
            var cardDB = GameCardDB.Get();
            var memReader = cardDB.memReader;
            hasData = memReader != null && !memReader.HasErrors && Service.clientState.IsLoggedIn;
            if (!hasData)
            {
                return;
            }

            cardDB.Refresh();
            GameNpcDB.Get().Refresh();

            // 背包裡的卡片道具
            inventoryByCard.Clear();
            var inventory = InventoryManager.Instance();
            foreach (var info in cardDB.mapCards.Values)
            {
                if (info.ItemId != 0 && inventory != null)
                {
                    int count = inventory->GetInventoryItemCount(info.ItemId);
                    if (count > 0)
                    {
                        inventoryByCard[info.CardId] = count;
                    }
                }
            }

            // 卡片
            var triadCards = TriadCardDB.Get();
            var triadNpcs = TriadNpcDB.Get();
            numOwned = 0;
            numTotal = 0;
            int selectedCardId = selectedCard?.Card.Id ?? -1;
            selectedCard = null;
            missingCards.Clear();

            foreach (var card in triadCards.cards)
            {
                if (card == null || !card.IsValid())
                {
                    continue;
                }

                var info = cardDB.FindById(card.Id);
                if (info == null)
                {
                    continue;
                }

                numTotal++;
                if (info.IsOwned)
                {
                    numOwned++;
                    continue;
                }

                var npcNames = info.RewardNpcs
                    .Select(id => (id >= 0 && id < triadNpcs.npcs.Count) ? triadNpcs.npcs[id]?.Name.GetLocalized() : null)
                    .Where(x => !string.IsNullOrEmpty(x));

                var cardEntry = new CardEntry
                {
                    Card = card,
                    Info = info,
                    Name = CardUtils.GetUIDesc(card),
                    InInventory = inventoryByCard.GetValueOrDefault(card.Id),
                    Npcs = string.Join("、", npcNames),
                };

                missingCards.Add(cardEntry);
                if (card.Id == selectedCardId)
                {
                    selectedCard = cardEntry;
                }
            }

            missingCards.Sort((a, b) => a.Card.SortOrder.CompareTo(b.Card.SortOrder));

            // NPC
            int selectedId = selectedNpc?.Info.npcId ?? -1;
            selectedNpc = null;
            npcs.Clear();
            npcById.Clear();

            foreach (var kvp in GameNpcDB.Get().mapNpcs)
            {
                var npc = (kvp.Key >= 0 && kvp.Key < triadNpcs.npcs.Count) ? triadNpcs.npcs[kvp.Key] : null;
                if (npc == null)
                {
                    continue;
                }

                var entry = new NpcEntry { Npc = npc, Info = kvp.Value, Name = npc.Name.GetLocalized() };
                foreach (var cardId in kvp.Value.rewardCards)
                {
                    var info = cardDB.FindById(cardId);
                    if (info == null || info.IsOwned)
                    {
                        continue;
                    }

                    if (inventoryByCard.ContainsKey(cardId))
                    {
                        entry.InInventory++;
                    }
                    else
                    {
                        entry.Missing++;
                    }
                }

                npcs.Add(entry);
                npcById[kvp.Key] = entry;
                if (kvp.Value.npcId == selectedId)
                {
                    selectedNpc = entry;
                }
            }

            npcs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        }

        private DateTime nextAutoRefresh;

        public void Draw()
        {
            // 分頁開著時每 3 秒重讀一次（學會卡片、拿到卡片道具都會反映出來）
            if (DateTime.Now >= nextAutoRefresh)
            {
                nextAutoRefresh = DateTime.Now + TimeSpan.FromSeconds(3);
                Refresh();
            }

            if (ImGuiComponents.IconButton(FontAwesomeIcon.Sync))
            {
                Refresh();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("重新讀取");
            }

            ImGui.SameLine();
            if (!hasData)
            {
                ImGui.TextColored(colorInactive, "登入遊戲後才讀得到收集資料");
                return;
            }

            ImGui.Text($"已學會 {numOwned} / {numTotal} 張，背包有 {inventoryByCard.Count} 張卡片道具還沒用");

            if (!ImGui.BeginTabBar("##collectionTabs"))
            {
                return;
            }

            if (ImGui.BeginTabItem("依 NPC"))
            {
                DrawNpcTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem($"缺的卡（{missingCards.Count}）"))
            {
                DrawCardTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        private void DrawNpcTab()
        {
            ImGui.SetNextItemWidth(200);
            ImGui.InputTextWithHint("##npcFilter", "搜尋 NPC", ref npcFilter, 64);
            ImGui.SameLine();
            ImGui.Checkbox("只顯示還缺卡的", ref npcMissingOnly);

            if (ImGui.BeginListBox("##npcList", new Vector2(-1, ImGui.GetTextLineHeightWithSpacing() * 10)))
            {
                foreach (var entry in npcs)
                {
                    if (npcMissingOnly && entry.Missing == 0)
                    {
                        continue;
                    }

                    if (npcFilter.Length > 0 && !entry.Name.Contains(npcFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var label = entry.Missing > 0 ? $"{entry.Name}（缺 {entry.Missing}）" :
                        entry.InInventory > 0 ? $"{entry.Name}（背包有 {entry.InInventory}）" :
                        entry.Name;

                    if (ImGui.Selectable($"{label}##{entry.Info.npcId}", selectedNpc == entry))
                    {
                        selectedNpc = entry;
                    }
                }

                ImGui.EndListBox();
            }

            if (selectedNpc != null)
            {
                ImGui.Separator();
                DrawNpcDetails(selectedNpc);
            }
        }

        /// <summary>NPC 的名字、地點（地圖按鈕）、對局費與有沒有打贏過。兩個分頁共用。</summary>
        private void DrawNpcHeader(NpcEntry entry)
        {
            ImGui.TextColored(colorYellow, entry.Name);

            var location = entry.Info.Location;
            if (location != null)
            {
                if (ImGuiComponents.IconButton($"##map{entry.Info.npcId}", FontAwesomeIcon.Map))
                {
                    Service.gameGui.OpenMapWithMapLink(location);
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("在地圖上顯示");
                }

                ImGui.SameLine();
                bool isTarget = travel.Running && travel.TargetNpcId == entry.Info.npcId;
                if (ImGuiComponents.IconButton($"##go{entry.Info.npcId}", isTarget ? FontAwesomeIcon.Stop : FontAwesomeIcon.Running))
                {
                    if (isTarget)
                    {
                        travel.Stop("已停止");
                    }
                    else
                    {
                        travel.Start(entry.Info, entry.Name);
                    }
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(isTarget ? "停止" : "傳送並走過去（需要 vnavmesh；城內以太之光需要 Lifestream）");
                }

                ImGui.SameLine();
                if (ImGuiComponents.IconButton($"##opt{entry.Info.npcId}", FontAwesomeIcon.LayerGroup))
                {
                    // 從這裡開不知道對局地點的地區規則，只帶入 NPC 自己的規則
                    optimizer.SetupAndOpen(entry.Npc, new List<TriadGameModifier>());
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("用這個 NPC 最佳化牌組（不含地區規則）");
                }

                ImGui.SameLine();
                ImGui.Text($"{location.PlaceName} {location.CoordinateString}");
            }

            if (travel.TargetNpcId == entry.Info.npcId || (!travel.Running && travel.LastNpcId == entry.Info.npcId && travel.Status.Length > 0))
            {
                ImGui.TextColored(colorOk, travel.Status);
            }

            var status = new List<string>();
            if (entry.Info.matchFee > 0) status.Add($"對局費 {entry.Info.matchFee} MGP");
            status.Add(entry.Info.IsBeatenOnce ? "已打贏過" : "還沒打贏過");
            if (entry.Missing > 0) status.Add($"這個 NPC 還缺 {entry.Missing} 張");
            ImGui.TextColored(colorInactive, string.Join("，", status));
        }

        private void DrawNpcDetails(NpcEntry entry)
        {
            DrawNpcHeader(entry);

            if (entry.Info.rewardCards.Count == 0)
            {
                ImGui.TextColored(colorInactive, "不會掉卡");
                return;
            }

            if (ImGui.BeginTable("##npcRewards", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
            {
                var cardDB = GameCardDB.Get();
                foreach (var cardId in entry.Info.rewardCards)
                {
                    var card = TriadCardDB.Get().FindById(cardId);
                    var info = cardDB.FindById(cardId);
                    if (card == null || info == null)
                    {
                        continue;
                    }

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text(CardUtils.GetRarityDesc(card));
                    ImGui.TableNextColumn();
                    ImGui.Text(CardUtils.GetUIDesc(card));
                    ImGui.TableNextColumn();

                    if (info.IsOwned)
                    {
                        ImGui.TextColored(colorInactive, "已學會");
                    }
                    else if (inventoryByCard.TryGetValue(cardId, out var count))
                    {
                        ImGui.TextColored(colorOk, $"背包有 {count} 張");
                    }
                    else
                    {
                        ImGui.TextColored(colorYellow, "缺");
                    }
                }

                ImGui.EndTable();
            }
        }

        private void DrawCardTab()
        {
            ImGui.SetNextItemWidth(200);
            ImGui.InputTextWithHint("##cardFilter", "搜尋卡片或 NPC", ref cardFilter, 64);
            ImGui.SameLine();
            ImGui.Checkbox("只顯示 NPC 會掉的", ref cardNpcOnly);

            if (ImGui.BeginTable("##missingCards", 4,
                ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV,
                new Vector2(-1, ImGui.GetTextLineHeightWithSpacing() * 11)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("編號");
                ImGui.TableSetupColumn("卡片");
                ImGui.TableSetupColumn("背包");
                ImGui.TableSetupColumn("掉落 NPC", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();

                foreach (var entry in missingCards)
                {
                    if (cardNpcOnly && entry.Npcs.Length == 0)
                    {
                        continue;
                    }

                    if (cardFilter.Length > 0 &&
                        !entry.Name.Contains(cardFilter, StringComparison.OrdinalIgnoreCase) &&
                        !entry.Npcs.Contains(cardFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    if (ImGui.Selectable($"{CardUtils.GetOrderDesc(entry.Card)}##card{entry.Card.Id}", selectedCard == entry, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        selectedCard = entry;
                    }
                    ImGui.TableNextColumn();
                    ImGui.Text($"{CardUtils.GetRarityDesc(entry.Card)} {entry.Name}");
                    ImGui.TableNextColumn();
                    if (entry.InInventory > 0)
                    {
                        ImGui.TextColored(colorOk, $"{entry.InInventory}");
                    }
                    ImGui.TableNextColumn();
                    ImGui.TextWrapped(entry.Npcs.Length > 0 ? entry.Npcs : "—");
                }

                ImGui.EndTable();
            }

            if (selectedCard != null)
            {
                ImGui.Separator();
                DrawCardDetails(selectedCard);
            }
        }

        /// <summary>選中的缺卡：卡片本身，以及每個會掉這張卡的 NPC 的地點資訊。</summary>
        private void DrawCardDetails(CardEntry entry)
        {
            ImGui.Text($"{CardUtils.GetOrderDesc(entry.Card)} {CardUtils.GetRarityDesc(entry.Card)} {entry.Name}");
            if (entry.InInventory > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(colorOk, $"（背包有 {entry.InInventory} 張，用掉就學會了）");
            }

            var dropNpcs = entry.Info.RewardNpcs
                .Select(id => npcById.GetValueOrDefault(id))
                .Where(x => x != null)
                .ToList();

            if (dropNpcs.Count == 0)
            {
                ImGui.TextColored(colorInactive, "沒有 NPC 會掉這張卡（要從其他管道取得）");
                return;
            }

            foreach (var npc in dropNpcs)
            {
                ImGui.Spacing();
                DrawNpcHeader(npc!);
            }
        }
    }
}
