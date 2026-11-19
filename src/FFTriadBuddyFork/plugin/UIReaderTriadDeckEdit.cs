using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MgAl2O4.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;

namespace TriadBuddyPlugin
{
    public class UIReaderTriadDeckEdit : IUIReader
    {
        [StructLayout(LayoutKind.Explicit, Size = 0xDA0)]
        private unsafe struct AddonTriadDeckEdit
        {
            [FieldOffset(0x0)] public AtkUnitBase AtkUnitBase;

            [FieldOffset(0xD90)] public byte PageIndex;                 // ignores writes
            [FieldOffset(0xD94)] public byte PageIndex2;                // ignores writes
            [FieldOffset(0xD98)] public byte CardIndex;                 // ignores writes
        }

        public UIStateTriadDeckEdit cachedState = new();
        public UnsafeReaderTriadDeck? unsafeDeck;
        public Action<bool>? OnVisibilityChanged;

        public bool IsVisible { get; private set; }

        private float searchSyncBlinkRemaining;
        private bool isOptimizerActive;

        private List<string> highlightTexPaths = new();

        public string GetAddonName()
        {
            return "GSInfoEditDeck";
        }

        public void OnAddonLost()
        {
            IsVisible = false;
            OnVisibilityChanged?.Invoke(false);
        }

        public void OnAddonShown(IntPtr addonPtr)
        {
            IsVisible = true;
            OnVisibilityChanged?.Invoke(true);
        }

        public unsafe void OnAddonUpdate(IntPtr addonPtr)
        {
            var addon = (AddonTriadDeckEdit*)addonPtr;
            var hasHighlights = isOptimizerActive;

            if (searchSyncBlinkRemaining > 0)
            {
                var deltaTime = ImGui.GetIO().DeltaTime;
                searchSyncBlinkRemaining -= deltaTime;
                hasHighlights = true;
            }

            // root, 14 children (sibling scan)
            //     [9] res node, card icon grid + fancy stuff
            //         [0] res node, just card icon grid
            //             [x] DragDrop components for each card, 3 children on node list
            //                 [2] icon component, 7 children on node list
            //                     [0] image node

            var nodeArrL0 = GUINodeUtils.GetImmediateChildNodes(addon->AtkUnitBase.RootNode);
            var nodeA = GUINodeUtils.PickNode(nodeArrL0, 9, 14);
            var nodeB = GUINodeUtils.GetChildNode(nodeA);
            var nodeArrCards = GUINodeUtils.GetImmediateChildNodes(nodeB);
            if (nodeArrCards != null)
            {
                foreach (var nodeD in nodeArrCards)
                {
                    var nodeE = GUINodeUtils.PickChildNode(nodeD, 2, 3);
                    var nodeImage = GUINodeUtils.PickChildNode(nodeE, 0, 7);

                    if (nodeImage != null)
                    {
                        if (!hasHighlights)
                        {
                            // no optimizer: reset highlights
                            nodeImage->MultiplyBlue = 100;
                            nodeImage->MultiplyRed = 100;
                            nodeImage->MultiplyGreen = 100;
                        }
                        else
                        {
                            var texPath = GUINodeUtils.GetNodeTexturePath(nodeImage) ?? "";
                            bool shouldHighlight = IsCardTexPathMatching(texPath);
                            byte colorV = (byte)(shouldHighlight ? 100 : 25);

                            nodeImage->MultiplyBlue = colorV;
                            nodeImage->MultiplyRed = colorV;
                            nodeImage->MultiplyGreen = colorV;
                        }
                    }
                }
            }

            (cachedState.screenPos, cachedState.screenSize) = GUINodeUtils.GetNodePosAndSize(addon->AtkUnitBase.RootNode);
            cachedState.pageIndex = addon->PageIndex;
            cachedState.cardIndex = addon->CardIndex;
        }

        public void OnDeckOptimizerVisible(bool isVisible)
        {
            isOptimizerActive = isVisible;
            highlightTexPaths.Clear();
        }

        public void SetHighlightedCards(int[]? cardIds)
        {
            highlightTexPaths.Clear();
            if (cardIds != null)
            {
                foreach (int cardId in cardIds)
                {
                    // see TriadCardDB.FindByTexture for details on patterns
                    var pathPattern = string.Format("088000/{0:D6}", FFTriadBuddy.TriadCardDB.GetCardIconTextureId(cardId));
                    highlightTexPaths.Add(pathPattern);
                }
            }
        }

        private bool IsCardTexPathMatching(string texPath)
        {
            foreach (var pathPattern in highlightTexPaths)
            {
                if (texPath.Contains(pathPattern))
                {
                    return true;
                }
            }

            return false;
        }

        public void SetSearchResultHighlight(int[] cardIds)
        {
            if (!isOptimizerActive)
            {
                SetHighlightedCards(cardIds);
                searchSyncBlinkRemaining = 1.0f;
            }
        }

        /// <summary>
        /// fork：把 5 張卡放進正在編輯的牌組。放卡的格式是從手動操作錄下來的：
        ///   回呼 [Int 11, Int 欄位(0~4), Int 卡片編號, Undefined, Undefined]（不用翻到卡片所在的那一頁）
        /// 實測：
        ///   - 回呼要跟遊戲一樣送滿 5 個值，只送 3 個時介面讀到殘留記憶體，只有第一下生效。
        ///   - 同一幀連送好幾個，遊戲只處理第一個，所以每一步間隔一下。
        ///   - 某張卡已經在別格時再放，遊戲會跳「重複」錯誤；清空欄位不管是送回呼 [13, 欄位]
        ///     還是模擬點擊事件都沒有用。
        /// 所以先讀出牌組目前的 5 張卡，再排出不會撞到重複、也不會超過星數限制
        /// （5 星最多 1 張、4 星加 5 星最多 2 張）的放卡順序。
        /// 不會自動存檔（存檔是 [Int 10]），讓玩家確認後自己按。
        /// </summary>
        public unsafe void ApplyDeck(int[] cardIds)
        {
            if (applyRunning || cardIds.Length != 5)
            {
                return;
            }

            var addon = (AtkUnitBase*)Service.gameGui.GetAddonByName(GetAddonName(), 1).Address;
            if (addon == null || !addon->IsVisible)
            {
                return;
            }

            var current = ReadDeckSlots(addon);
            if (current == null)
            {
                Service.chatGui.PrintError("[TriadBuddy] 讀不到牌組目前的卡片，無法套用");
                return;
            }

            var (steps, problem) = PlanDeckSteps(current, cardIds);
            Service.logger.Info($"apply deck: current {string.Join(",", current)} -> target {string.Join(",", cardIds)}, steps {string.Join(" ", steps.Select(s => $"{s.slot}:{s.card}"))}");

            if (steps.Count == 0)
            {
                Service.chatGui.Print(problem ?? "[TriadBuddy] 牌組已經是這 5 張卡了");
                return;
            }

            applyRunning = true;
            for (int idx = 0; idx < steps.Count; idx++)
            {
                var step = steps[idx];
                bool isLast = idx == steps.Count - 1;

                Service.framework.RunOnTick(() =>
                {
                    var addonNow = (AtkUnitBase*)Service.gameGui.GetAddonByName(GetAddonName(), 1).Address;
                    if (applyRunning && addonNow != null && addonNow->IsVisible)
                    {
                        var values = stackalloc AtkValue[5];
                        for (int i = 0; i < 5; i++)
                        {
                            values[i] = default;
                        }

                        values[0].SetInt(11);
                        values[1].SetInt(step.slot);
                        values[2].SetInt(step.card);
                        addonNow->FireCallback(5, values, true);
                    }
                    else
                    {
                        // 牌組編輯畫面被關掉了，後面的步驟都不做
                        applyRunning = false;
                    }

                    if (isLast && applyRunning)
                    {
                        applyRunning = false;
                        Service.chatGui.Print(problem ?? "[TriadBuddy] 已套用牌組，確認後請按存檔");
                    }
                }, TimeSpan.FromMilliseconds(ApplyStepMs * idx));
            }
        }

        /// <summary>
        /// 讀出牌組 5 個欄位目前的卡片編號（空格是 0）。欄位是拖曳元件，點擊事件的參數是 1~5
        /// （手動清空時錄到的 DragDropClick 參數），用這個分辨是第幾格；卡片則看元件裡的卡片圖示。
        /// 讀不到 5 格就回傳 null。
        /// </summary>
        private static unsafe int[]? ReadDeckSlots(AtkUnitBase* addon)
        {
            var result = new int[5];
            var found = new bool[5];
            var cardDB = FFTriadBuddy.TriadCardDB.Get();

            for (int i = 0; i < addon->UldManager.NodeListCount; i++)
            {
                var node = addon->UldManager.NodeList[i];
                if (node == null || (int)node->Type < 1000)
                {
                    continue;
                }

                int slot = -1;
                for (var evt = node->AtkEventManager.Event; evt != null; evt = evt->NextEvent)
                {
                    if (evt->State.EventType == AtkEventType.DragDropClick && evt->Param >= 1 && evt->Param <= 5)
                    {
                        slot = (int)evt->Param - 1;
                        break;
                    }
                }

                if (slot < 0 || found[slot])
                {
                    continue;
                }

                found[slot] = true;
                var children = GUINodeUtils.GetAllChildNodes(node);
                if (children == null)
                {
                    continue;
                }

                foreach (var child in children)
                {
                    var texPath = GUINodeUtils.GetNodeTexturePath(child);
                    if (!string.IsNullOrEmpty(texPath) && texPath.Contains("088000/"))
                    {
                        var card = cardDB.FindByTexture(texPath);
                        if (card != null && card.IsValid())
                        {
                            result[slot] = card.Id;
                            break;
                        }
                    }
                }
            }

            return found.All(x => x) ? result : null;
        }

        /// <summary>
        /// 排出放卡順序：每次挑一格「目標卡沒有在別格、放下去星數也不會超過限制」的放。
        /// 剩下的只差在順序時（5 張卡相同但位置不同），遊戲不讓重複放，沒辦法調，就保持原樣並說明。
        /// </summary>
        private static (List<(int slot, int card)> steps, string? problem) PlanDeckSteps(int[] current, int[] target)
        {
            var cardDB = FFTriadBuddy.TriadCardDB.Get();
            var deck = (int[])current.Clone();
            var steps = new List<(int slot, int card)>();

            bool IsValid(int[] d)
            {
                int num5 = 0, num45 = 0;
                foreach (var id in d)
                {
                    var card = id > 0 ? cardDB.FindById(id) : null;
                    if (card == null) continue;
                    if (card.Rarity == FFTriadBuddy.ETriadCardRarity.Legendary) { num5++; num45++; }
                    else if (card.Rarity == FFTriadBuddy.ETriadCardRarity.Epic) { num45++; }
                }

                return num5 <= 1 && num45 <= 2;
            }

            for (int guard = 0; guard < 10; guard++)
            {
                var mismatched = Enumerable.Range(0, 5).Where(i => deck[i] != target[i]).ToList();
                if (mismatched.Count == 0)
                {
                    return (steps, null);
                }

                int pick = -1;
                foreach (var slot in mismatched)
                {
                    if (Array.IndexOf(deck, target[slot]) >= 0)
                    {
                        continue;       // 目標卡還在別格，現在放會重複
                    }

                    var test = (int[])deck.Clone();
                    test[slot] = target[slot];
                    if (IsValid(test))
                    {
                        pick = slot;
                        break;
                    }
                }

                if (pick < 0)
                {
                    bool onlyOrder = mismatched.All(slot => Array.IndexOf(deck, target[slot]) >= 0);
                    return (steps, onlyOrder ?
                        "[TriadBuddy] 卡片已經一樣，只有順序不同（遊戲不能直接換位置），保留目前順序；確認後請按存檔" :
                        "[TriadBuddy] 有幾張卡因為星數限制放不進去，請手動調整");
                }

                deck[pick] = target[pick];
                steps.Add((pick, target[pick]));
            }

            return (steps, null);
        }

        private const int ApplyStepMs = 300;
        private bool applyRunning;
        public bool IsApplyingDeck => applyRunning;

        public unsafe bool SetPageAndGridView(int pageIndex, int cellIndex)
        {
            // doesn't really belong to a ui "reader", but won't be making a class just for calling one function

            // basic sanity checks on values before writing them to memory
            // this will NOT be enough when filters are active!
            if (pageIndex < 0 || pageIndex >= GameCardDB.MaxGridPages || cellIndex < 0 || cellIndex >= GameCardDB.MaxGridCells)
            {
                return false;
            }

            // agentPtr is NOT available through deck edit addon here
            // use GSInfoCardDeck instead

            IntPtr addonPtr = Service.gameGui.GetAddonByName(GetAddonName(), 1);
            IntPtr agentPtr = Service.gameGui.FindAgentInterface("GSInfoCardDeck");
            if (agentPtr == IntPtr.Zero)
            {
                agentPtr = UIReaderTriadCardList.LoadFailsafeAgent();
            }

            if (addonPtr != IntPtr.Zero && agentPtr != IntPtr.Zero)
            {
                OnAddonShown(addonPtr);

                var addon = (AddonTriadDeckEdit*)addonPtr;
                var agent = (UIReaderTriadCardList.AgentTriadCardList*)agentPtr;

                // reset all filters, otherwise page/card won't be correct
                agent->FilterDeckTypeRarity = 0;
                agent->FilterDeckSides = 0;
                agent->FilterDeckSorting = 0;
                agent->FilterMode = 0;

                agent->PageIndex = (byte)pageIndex;

                unsafeDeck?.RefreshUI(agentPtr);
                unsafeDeck?.SetSelectedCard(addonPtr, cellIndex);

                return true;
            }

            return false;
        }
    }

    public class UIStateTriadDeckEdit
    {
        public Vector2 screenPos;
        public Vector2 screenSize;

        public byte pageIndex;
        public byte cardIndex;
    }
}
