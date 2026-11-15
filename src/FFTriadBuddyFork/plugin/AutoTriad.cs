using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using FFTriadBuddy;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TriadBuddyPlugin
{
    /// <summary>
    /// 自動對戰（fork 新增），兩個開關，預設都是關的，也不存檔 — 每次載入插件都要自己打開：
    ///
    /// 自動出牌：輪到你、對手是 NPC、解算器算出下一步時，隨機等一下就照解算器的建議出牌。
    ///
    /// 自動循環：鎖定第一個遇到的 NPC，一直「挑戰 → 選牌組 → 對局 → 再挑戰」，
    /// 直到這個 NPC 會掉的卡全部「已經學會」或「背包裡有卡片道具」為止。
    /// 第一次要自己跟 NPC 對話打開挑戰視窗，之後就交給插件。
    ///
    /// 只對 NPC：對局畫面判斷是 PvP / 大賽、或認不出 NPC 時一律不動作。
    /// 出狀況就停：進入戰鬥、換了對手、出現不認得的確認框、按了沒反應、連輸、達到局數上限。
    /// </summary>
    public sealed unsafe class AutoTriad : IDisposable
    {
        public class TargetCard
        {
            public int CardId;
            public uint ItemId;
            public string Name = string.Empty;
            public bool Owned;
            public int InInventory;

            public bool Done => Owned || InInventory > 0;
        }

        private enum Stage
        {
            None,
            Request,
            DeckSelect,
            Playing,
            Result,
        }

        private static readonly string[] ObservedAddons = { "TripleTriad", "TripleTriadRequest", "TripleTriadSelDeck", "TripleTriadResult", "SelectYesno" };

        private static readonly TimeSpan MoveConfirmTimeout = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan LoopIdleTimeout = TimeSpan.FromSeconds(25);

        // 按下按鈕後畫面沒換，每隔這麼久再按一次（剛出現時介面偶爾會吞掉第一下），直到逾時才算失敗
        private static readonly TimeSpan ActionRetryInterval = TimeSpan.FromSeconds(1.5);

        private readonly UIReaderTriadGame uiReaderGame;
        private readonly UIReaderTriadPrep uiReaderPrep;
        private readonly Random rng = new();

        // ── 自動出牌狀態 ──
        private bool autoPlay;
        private int plannedTurn = -1;
        private DateTime plannedAt;
        private int firedTurn = -1;
        private DateTime firedAt;
        private int fireAttempts;
        private bool sawOpponentTurn;

        // ── 自動循環狀態 ──
        private bool autoLoop;
        private Stage stage = Stage.None;
        private DateTime stageEnteredAt;
        private DateTime stageActionAt;
        private DateTime stageFirstActionAt;
        private int stageAttempts;
        private DateTime lastActivity;
        private bool resultCounted;

        // ── 除錯：記錄介面事件 ──
        private delegate bool FireCallbackDelegate(AtkUnitBase* addon, uint valueCount, AtkValue* values, bool close);
        private Hook<FireCallbackDelegate>? fireCallbackHook;
        private bool eventLogRegistered;

        public string PlayStatus { get; private set; } = "關閉";
        public string LoopStatus { get; private set; } = "關閉";
        public string LoopNpcName { get; private set; } = string.Empty;
        public GameNpcInfo? LoopNpc { get; private set; }
        public List<TargetCard> Targets { get; } = new();
        public int MatchesPlayed { get; private set; }
        public int Wins { get; private set; }
        public int Draws { get; private set; }
        public int Losses { get; private set; }
        public int LossStreak { get; private set; }

        public AutoTriad(UIReaderTriadGame uiReaderGame, UIReaderTriadPrep uiReaderPrep, UIReaderTriadResults uiReaderResults)
        {
            this.uiReaderGame = uiReaderGame;
            this.uiReaderPrep = uiReaderPrep;
            uiReaderResults.OnUpdated += OnMatchResult;
            UpdateEventLog();
        }

        public void Dispose()
        {
            SetEventLog(false);
            fireCallbackHook?.Dispose();
            fireCallbackHook = null;
        }

        // ─────────────────────────────────────────────────────────
        // 開關
        // ─────────────────────────────────────────────────────────

        public bool AutoPlay
        {
            get => autoPlay;
            set
            {
                if (autoPlay == value) return;
                autoPlay = value;
                ResetPlay();
                PlayStatus = value ? "等待輪到你" : "關閉";

                // 循環要靠自動出牌，關掉出牌就一起停
                if (!value && autoLoop)
                {
                    StopLoop("自動出牌已關閉", false);
                }
            }
        }

        public bool AutoLoop
        {
            get => autoLoop;
            set
            {
                if (autoLoop == value) return;

                if (value)
                {
                    autoLoop = true;
                    AutoPlay = true;

                    LoopNpc = null;
                    LoopNpcName = string.Empty;
                    Targets.Clear();
                    MatchesPlayed = Wins = Draws = Losses = LossStreak = 0;
                    stage = Stage.None;
                    lastActivity = DateTime.Now;
                    LoopStatus = "請跟 NPC 對話，打開幻卡挑戰視窗";
                }
                else
                {
                    StopLoop("已手動關閉", false);
                }
            }
        }

        private void StopLoop(string reason, bool printToChat = true)
        {
            autoLoop = false;
            stage = Stage.None;
            LoopStatus = $"已停止：{reason}";

            if (printToChat)
            {
                Service.chatGui.Print($"[TriadBuddy] 自動循環停止：{reason}");
            }
        }

        private void StopAll(string reason)
        {
            Service.chatGui.PrintError($"[TriadBuddy] 自動對戰停止：{reason}");
            if (autoLoop)
            {
                StopLoop(reason, false);
            }

            autoPlay = false;
            ResetPlay();
            PlayStatus = $"已停止：{reason}";
        }

        // ─────────────────────────────────────────────────────────
        // 每幀更新（在 UI 讀取之後呼叫）
        // ─────────────────────────────────────────────────────────

        public void Update()
        {
            if (!autoPlay && !autoLoop)
            {
                return;
            }

            var now = DateTime.Now;

            if (autoPlay)
            {
                TickPlay(now);
            }

            if (autoLoop)
            {
                TickLoop(now);
            }
        }

        // ─────────────────────────────────────────────────────────
        // 自動出牌
        // ─────────────────────────────────────────────────────────

        private void ResetPlay()
        {
            plannedTurn = -1;
            firedTurn = -1;
            fireAttempts = 0;
        }

        private void TickPlay(DateTime now)
        {
            var state = uiReaderGame.currentState;
            var solver = SolverUtils.solverGame;

            if (state == null)
            {
                // 對局視窗關了：清掉這局的紀錄，下局重新開始
                ResetPlay();
                PlayStatus = "等待對局";
                return;
            }

            if (state.isPvP || uiReaderGame.status == UIReaderTriadGame.Status.PvPMatch)
            {
                ResetPlay();
                PlayStatus = "不是 NPC 對局，不動作";
                return;
            }

            if (uiReaderGame.status != UIReaderTriadGame.Status.NoErrors || solver == null || solver.HasErrors || solver.currentNpc == null)
            {
                ResetPlay();
                PlayStatus = "認不出對局內容，不動作";
                return;
            }

            if (state.move == 0)
            {
                sawOpponentTurn = true;
                PlayStatus = "等待對手";
                return;
            }

            // 用「棋盤上有幾張牌」當作回合編號：每出一張牌就 +1，不會跟上一手搞混
            int turn = state.board.Count(x => x != null && x.isPresent);

            // 我們的牌剛落下時，介面會有一小段時間還顯示「輪到你」。這時解算器會用新盤面再算一手，
            // 送出去會被遊戲忽略（實測會多送好幾手）。所以出完牌後要等到看見對手回合
            // （或對手的牌也落下了）才算下一手。
            if (firedTurn >= 0 && turn > firedTurn && !sawOpponentTurn && turn < firedTurn + 2)
            {
                PlayStatus = "等待對手";
                return;
            }

            if (firedTurn == turn)
            {
                // 已經出過這一手，等畫面反應
                if (now - firedAt < MoveConfirmTimeout)
                {
                    return;
                }

                if (fireAttempts >= 2)
                {
                    StopAll("出牌沒有反應（請開啟「記錄介面事件」手動出一張牌，把聊天欄內容回報）");
                    return;
                }

                // 再送一次
                if (TryPlaceSolverMove(state, solver))
                {
                    fireAttempts++;
                    firedAt = now;
                }

                return;
            }

            if (!solver.hasMove || solver.moveCardIdx < 0 || solver.moveBoardIdx < 0)
            {
                PlayStatus = "計算中…";
                return;
            }

            if (plannedTurn != turn)
            {
                plannedTurn = turn;
                plannedAt = now + RandomDelay(Service.pluginConfig.AutoMoveDelayMin, Service.pluginConfig.AutoMoveDelayMax);
                PlayStatus = "準備出牌…";
                return;
            }

            if (now < plannedAt)
            {
                return;
            }

            if (TryPlaceSolverMove(state, solver))
            {
                firedTurn = turn;
                firedAt = now;
                fireAttempts = 1;
                sawOpponentTurn = false;
            }
        }

        private bool TryPlaceSolverMove(UIStateTriadGame state, SolverGame solver)
        {
            int cardIdx = solver.moveCardIdx;
            int boardIdx = solver.moveBoardIdx;

            if (cardIdx >= state.blueDeck.Length || boardIdx >= state.board.Length)
            {
                return false;
            }

            var handCard = state.blueDeck[cardIdx];
            var boardCell = state.board[boardIdx];
            if (handCard == null || !handCard.isPresent || (boardCell != null && boardCell.isPresent))
            {
                // 解算器的結果跟畫面對不上（可能還沒更新），下一幀再看
                return false;
            }

            var addon = GetVisibleAddon("TripleTriad");
            if (addon == null)
            {
                return false;
            }

            PlaceCard(addon, cardIdx, boardIdx);

            string cardName = solver.moveCard?.Name.GetLocalized() ?? "??";
            PlayStatus = $"出牌：第 {cardIdx + 1} 張 {cardName} → 格子 {boardIdx + 1}";
            Service.logger.Info($"auto move: card {cardIdx} -> board {boardIdx}");
            return true;
        }

        /// <summary>
        /// 送出「把第 card 張手牌放到第 slot 格」的介面回呼。
        /// 格式是從手動拖曳錄下來的：[Int 14, UInt (手牌 &lt;&lt; 16) | 格子]，close = true。
        /// 例：第 2 張手牌（從 0 數）拖到第 8 格 → 0x20008。
        /// </summary>
        private static void PlaceCard(AtkUnitBase* addon, int card, int slot)
        {
            var values = stackalloc AtkValue[2];
            values[0].SetInt(14);
            values[1].SetUInt((uint)((card << 16) | slot));
            addon->FireCallback(2, values, true);
        }

        /// <summary>挑戰視窗的「挑戰」、結算畫面的「再戰」：手動按下去時遊戲送的都是 [Int 0]。</summary>
        private static void FireConfirm(AtkUnitBase* addon)
        {
            var values = stackalloc AtkValue[1];
            values[0].SetInt(0);
            addon->FireCallback(1, values, true);
        }

        // ─────────────────────────────────────────────────────────
        // 自動循環
        // ─────────────────────────────────────────────────────────

        private void TickLoop(DateTime now)
        {
            if (Service.condition[ConditionFlag.InCombat])
            {
                StopAll("進入戰鬥");
                return;
            }

            // 結算畫面出現時對局視窗可能還開在底下，所以先看結算
            Stage current;
            if (GetVisibleAddon("SelectYesno") != null)
            {
                StopLoop($"出現確認視窗「{GetYesnoText()}」，請手動處理");
                return;
            }
            else if (GetVisibleAddon("TripleTriadResult") != null)
            {
                current = Stage.Result;
            }
            else if (uiReaderGame.currentState != null || GetVisibleAddon("TripleTriad") != null)
            {
                current = Stage.Playing;
            }
            else if (GetVisibleAddon("TripleTriadSelDeck") != null)
            {
                current = Stage.DeckSelect;
            }
            else if (GetVisibleAddon("TripleTriadRequest") != null)
            {
                current = Stage.Request;
            }
            else
            {
                current = Stage.None;
            }

            if (current == Stage.None)
            {
                if (LoopNpc == null)
                {
                    // 還沒開始：等玩家自己去跟 NPC 對話
                    lastActivity = now;
                    LoopStatus = "請跟 NPC 對話，打開幻卡挑戰視窗";
                }
                else if (now - lastActivity > LoopIdleTimeout)
                {
                    StopLoop("流程中斷，一段時間沒看到幻卡視窗");
                }

                stage = Stage.None;
                return;
            }

            lastActivity = now;

            if (current != stage)
            {
                stage = current;
                stageAttempts = 0;
                stageEnteredAt = now;
                stageActionAt = Service.pluginConfig.AutoClickInstant ? now :
                    now + RandomDelay(Service.pluginConfig.AutoLoopDelayMin, Service.pluginConfig.AutoLoopDelayMax);

                // 結算資料可能跟結算畫面同一幀就讀到，所以在「開始對局」時清，而不是進結算時清
                if (current == Stage.Playing)
                {
                    resultCounted = false;
                }
            }

            switch (current)
            {
                case Stage.Playing: TickLoopPlaying(); break;
                case Stage.Request: TickLoopRequest(now); break;
                case Stage.DeckSelect: TickLoopDeckSelect(now); break;
                case Stage.Result: TickLoopResult(now); break;
            }
        }

        private void TickLoopPlaying()
        {
            var state = uiReaderGame.currentState;
            if (state != null && state.isPvP)
            {
                StopAll("這不是 NPC 對局");
                return;
            }

            var npc = SolverUtils.solverGame?.currentNpc;
            if (npc != null && !CheckLoopNpc(npc))
            {
                return;
            }

            LoopStatus = $"對局中（第 {MatchesPlayed + 1} 局）";
        }

        private void TickLoopRequest(DateTime now)
        {
            var npc = SolverUtils.solverPreGameDecks?.preGameNpc;
            if (npc == null)
            {
                LoopStatus = "讀取 NPC 中…";
                if (now > stageEnteredAt + TimeSpan.FromSeconds(5))
                {
                    StopLoop("認不出這個 NPC");
                }

                return;
            }

            if (!CheckLoopNpc(npc) || !CheckContinue())
            {
                return;
            }

            if (!ReadyForAction(now, "挑戰"))
            {
                return;
            }

            var addon = GetVisibleAddon("TripleTriadRequest");
            if (addon == null)
            {
                return;
            }

            FireConfirm(addon);
            LoopStatus = "按下挑戰";
        }

        private void TickLoopDeckSelect(DateTime now)
        {
            var addon = GetVisibleAddon("TripleTriadSelDeck");
            if (addon == null)
            {
                return;
            }

            int deckIdx = Service.pluginConfig.AutoLoopDeckIndex;
            var preGame = SolverUtils.solverPreGameDecks;
            if (Service.pluginConfig.AutoLoopUseBestDeck && preGame != null)
            {
                if (preGame.preGameBestId < 0 && now < stageEnteredAt + TimeSpan.FromSeconds(10))
                {
                    LoopStatus = "評估牌組中…";
                    return;
                }

                if (preGame.preGameBestId >= 0)
                {
                    deckIdx = preGame.preGameBestId;
                }
            }

            if (!ReadyForAction(now, "選牌組"))
            {
                return;
            }

            if (!SelectDeck(addon, deckIdx))
            {
                StopLoop($"選不到第 {deckIdx + 1} 組牌組");
                return;
            }

            LoopStatus = $"選擇第 {deckIdx + 1} 組牌組";
        }

        private void TickLoopResult(DateTime now)
        {
            if (!resultCounted)
            {
                // 結算資料還沒讀到（OnMatchResult 會設定），先等
                LoopStatus = "結算中…";
                if (now > stageEnteredAt + TimeSpan.FromSeconds(10))
                {
                    StopLoop("讀不到結算結果");
                }

                return;
            }

            if (!CheckContinue())
            {
                return;
            }

            if (!ReadyForAction(now, "再挑戰"))
            {
                return;
            }

            var addon = GetVisibleAddon("TripleTriadResult");
            if (addon == null)
            {
                return;
            }

            FireConfirm(addon);
            LoopStatus = "按下再戰";
        }

        /// <summary>
        /// 輪到按按鈕了沒。第一次按下後畫面沒換，每 1.5 秒再按一次；
        /// 從第一次按下算起超過設定的秒數還是同一個畫面，就算失敗並停止。
        /// </summary>
        private bool ReadyForAction(DateTime now, string what)
        {
            if (now < stageActionAt)
            {
                return false;
            }

            if (stageAttempts == 0)
            {
                stageFirstActionAt = now;
            }
            else
            {
                float timeout = Math.Max(1.0f, Service.pluginConfig.AutoClickTimeout);
                if (now - stageFirstActionAt > TimeSpan.FromSeconds(timeout))
                {
                    StopLoop($"按「{what}」後 {timeout:0.#} 秒畫面都沒換");
                    return false;
                }
            }

            stageAttempts++;
            stageActionAt = now + ActionRetryInterval;
            return true;
        }

        private bool CheckLoopNpc(TriadNpc npc)
        {
            if (LoopNpc == null)
            {
                if (!GameNpcDB.Get().mapNpcs.TryGetValue(npc.Id, out var npcInfo))
                {
                    StopLoop("找不到這個 NPC 的資料");
                    return false;
                }

                LoopNpc = npcInfo;
                LoopNpcName = npc.Name.GetLocalized();
                RefreshTargets();

                var missing = Targets.Where(x => !x.Done).Select(x => x.Name).ToList();
                Service.chatGui.Print(missing.Count > 0 ?
                    $"[TriadBuddy] 自動循環開始：{LoopNpcName}，目標卡片：{string.Join("、", missing)}" :
                    $"[TriadBuddy] {LoopNpcName} 的卡都已經有了");
                return true;
            }

            // GameNpcDB 用 npcId（= TriadNpc.Id）當 key；triadId 是遊戲表格的另一個編號，不能拿來比
            if (LoopNpc.npcId != npc.Id)
            {
                StopLoop($"對手換成了 {npc.Name.GetLocalized()}");
                return false;
            }

            return true;
        }

        /// <summary>檢查還要不要繼續打，不用就停下來並說明原因。</summary>
        private bool CheckContinue()
        {
            RefreshTargets();

            if (Targets.Count == 0)
            {
                StopLoop($"{LoopNpcName} 不會掉卡");
                return false;
            }

            if (Targets.All(x => x.Done))
            {
                StopLoop($"{LoopNpcName} 的卡都拿到了");
                return false;
            }

            int maxMatches = Service.pluginConfig.AutoLoopMaxMatches;
            if (maxMatches > 0 && MatchesPlayed >= maxMatches)
            {
                StopLoop($"已打滿 {maxMatches} 局");
                return false;
            }

            int maxLosses = Service.pluginConfig.AutoLoopMaxLossStreak;
            if (maxLosses > 0 && LossStreak >= maxLosses)
            {
                StopLoop($"連輸 {LossStreak} 局，牌組可能打不過");
                return false;
            }

            return true;
        }

        public void RefreshTargets()
        {
            if (LoopNpc == null)
            {
                return;
            }

            var cardDB = GameCardDB.Get();
            var memReader = cardDB.memReader;
            var inventory = InventoryManager.Instance();

            // 結算時拿到的卡片道具，背包可能還沒更新；保留已記下的數量
            var previous = Targets.ToDictionary(x => x.CardId, x => x.InInventory);
            Targets.Clear();

            foreach (var cardId in LoopNpc.rewardCards)
            {
                var info = cardDB.FindById(cardId);
                var target = new TargetCard
                {
                    CardId = cardId,
                    ItemId = info?.ItemId ?? 0,
                    Name = TriadCardDB.Get().FindById(cardId)?.Name.GetLocalized() ?? $"#{cardId}",
                    Owned = memReader != null && !memReader.HasErrors && memReader.IsCardOwned(cardId),
                };

                if (target.ItemId != 0 && inventory != null)
                {
                    target.InInventory = inventory->GetInventoryItemCount(target.ItemId);
                }

                if (previous.TryGetValue(cardId, out var prevCount) && prevCount > target.InInventory)
                {
                    target.InInventory = prevCount;
                }

                Targets.Add(target);
            }
        }

        private void OnMatchResult(UIStateTriadResults result)
        {
            if (!autoLoop || LoopNpc == null)
            {
                return;
            }

            MatchesPlayed++;
            if (result.isWin) { Wins++; LossStreak = 0; }
            else if (result.isDraw) { Draws++; }
            else if (result.isLose) { Losses++; LossStreak++; }

            if (result.cardItemId != 0)
            {
                var target = Targets.FirstOrDefault(x => x.ItemId == result.cardItemId);
                if (target != null)
                {
                    target.InInventory++;
                    Service.chatGui.Print($"[TriadBuddy] 拿到目標卡片：{target.Name}（第 {MatchesPlayed} 局）");
                }
            }

            resultCounted = true;
        }

        // ─────────────────────────────────────────────────────────
        // 介面操作
        // ─────────────────────────────────────────────────────────

        private static AtkUnitBase* GetVisibleAddon(string name)
        {
            var addon = RaptureAtkUnitManager.Instance()->GetAddonByName(name, 1);
            return (addon != null && addon->IsVisible && addon->IsReady) ? addon : null;
        }

        private static string GetYesnoText()
        {
            var yesno = (AddonSelectYesno*)GetVisibleAddon("SelectYesno");
            return (yesno != null && yesno->PromptText != null) ? yesno->PromptText->NodeText.ToString() : "?";
        }

        /// <summary>
        /// 在牌組清單上點插件編號第 deckId 組的牌組。
        /// 清單元件在節點 4（跟 UIReaderTriadPrep 讀牌組的位置一樣）。手動點的時候，介面收到的是
        /// ListItemClick、參數 1，事件資料裡帶著被點的項目；這裡照樣送一次，介面自己會接著送 [Int 0]。
        /// </summary>
        private bool SelectDeck(AtkUnitBase* addon, int deckId)
        {
            if (addon->UldManager.NodeListCount != 5 || deckId < 0)
            {
                return false;
            }

            var node = addon->UldManager.NodeList[4];
            if (node == null || (int)node->Type < 1000)
            {
                return false;
            }

            var component = ((AtkComponentNode*)node)->Component;
            if (component == null || component->GetComponentType() != ComponentType.List)
            {
                return false;
            }

            var list = (AtkComponentList*)component;

            // 插件的牌組編號是照讀到的清單項目順序排的；用項目的節點對回清單索引，對不到就直接當索引用
            int itemIndex = deckId;
            var decks = uiReaderPrep.cachedState.decks;
            if (deckId < decks.Count && decks[deckId].rootNodeAddr != 0)
            {
                var deckNode = (AtkComponentNode*)decks[deckId].rootNodeAddr;
                for (int i = 0; i < list->ListLength; i++)
                {
                    var renderer = list->ItemRendererList[i].AtkComponentListItemRenderer;
                    if (renderer != null && renderer->OwnerNode == deckNode)
                    {
                        itemIndex = i;
                        break;
                    }
                }
            }

            if (itemIndex >= list->ListLength)
            {
                return false;
            }

            var evt = new AtkEvent
            {
                Listener = (AtkEventListener*)addon,
                Target = &AtkStage.Instance()->AtkEventTarget,
            };

            var data = new AtkEventData();
            data.ListItemData.SelectedIndex = itemIndex;
            data.ListItemData.ListItemRenderer = list->ItemRendererList[itemIndex].AtkComponentListItemRenderer;

            Service.logger.Info($"auto deck: plugin deck {deckId} -> list item {itemIndex}");
            addon->ReceiveEvent(AtkEventType.ListItemClick, 1, &evt, &data);
            return true;
        }

        private TimeSpan RandomDelay(float min, float max)
        {
            min = Math.Max(0, min);
            max = Math.Max(min, max);
            return TimeSpan.FromSeconds(min + rng.NextDouble() * (max - min));
        }

        // ─────────────────────────────────────────────────────────
        // 除錯：記錄介面事件（自己手動操作一次，就能看到遊戲實際送了什麼）
        // ─────────────────────────────────────────────────────────

        public void UpdateEventLog() => SetEventLog(Service.pluginConfig.AutoDebugLogEvents);

        private void SetEventLog(bool enable)
        {
            if (enable && !eventLogRegistered)
            {
                Service.addonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, ObservedAddons, OnReceiveEvent);
                eventLogRegistered = true;

                try
                {
                    fireCallbackHook ??= Service.gameInterop.HookFromAddress<FireCallbackDelegate>(
                        (nint)AtkUnitBase.MemberFunctionPointers.FireCallback, FireCallbackDetour);
                    fireCallbackHook.Enable();
                }
                catch (Exception ex)
                {
                    Service.logger.Error(ex, "failed to hook FireCallback");
                }
            }
            else if (!enable && eventLogRegistered)
            {
                Service.addonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, ObservedAddons, OnReceiveEvent);
                eventLogRegistered = false;
                fireCallbackHook?.Disable();
            }
        }

        private void OnReceiveEvent(AddonEvent type, AddonArgs args)
        {
            if (args is AddonReceiveEventArgs evt)
            {
                var eventType = (AtkEventType)evt.AtkEventType;

                // 滑鼠移動、懸停這類事件太多，略過
                if (eventType is AtkEventType.MouseOver or AtkEventType.MouseOut or AtkEventType.MouseMove or AtkEventType.FocusStart or AtkEventType.FocusStop)
                {
                    return;
                }

                var extra = string.Empty;
                if (eventType == AtkEventType.ListItemClick && evt.Data != 0)
                {
                    extra = $" 項目 {((AtkEventData*)evt.Data)->ListItemData.SelectedIndex}";
                }

                LogEvent($"{args.AddonName} 事件 {eventType}({evt.AtkEventType}) 參數 {evt.EventParam}{extra}");
            }
        }

        private bool FireCallbackDetour(AtkUnitBase* addon, uint valueCount, AtkValue* values, bool close)
        {
            try
            {
                var name = addon != null ? addon->NameString : string.Empty;
                if (ObservedAddons.Contains(name))
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < valueCount; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(DescribeValue(values[i]));
                    }

                    LogEvent($"{name} 回呼 [{sb}] close={close}");
                }
            }
            catch (Exception ex)
            {
                Service.logger.Error(ex, "callback log failed");
            }

            return fireCallbackHook!.Original(addon, valueCount, values, close);
        }

        private static string DescribeValue(AtkValue value) => value.Type switch
        {
            FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int => $"Int {value.Int}",
            FFXIVClientStructs.FFXIV.Component.GUI.ValueType.UInt => $"UInt {value.UInt} (0x{value.UInt:X})",
            FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Bool => $"Bool {value.Byte != 0}",
            _ => $"{value.Type}",
        };

        private static void LogEvent(string text)
        {
            Service.logger.Info($"[event] {text}");
            Service.chatGui.Print($"[TriadBuddy 事件] {text}");
        }
    }
}
