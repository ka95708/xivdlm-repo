using Dalamud;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using FFTriadBuddy;
using System;
using System.Numerics;

namespace TriadBuddyPlugin
{
    public class PluginWindowStatus : Window, IDisposable
    {
        private readonly UIReaderTriadGame uiReaderGame;
        private readonly UIReaderTriadPrep uiReaderPrep;

        // fork：狀態、自動對戰、收集、設定合併成同一個視窗的分頁
        public enum Tab
        {
            Status,
            Auto,
            Collection,
            Optimizer,
            Config,
        }

        private readonly PluginPanelAuto autoPanel;
        private readonly PluginPanelCollection collectionPanel;
        private readonly PluginWindowDeckOptimize optimizer;
        private Tab? requestedTab;

        private bool showDebugDetails;
        private float orgDrawPosX;
        private const float debugCellSize = 30.0f;
        private const float debugCellPading = 4.0f;

        private Vector4 colorErr = new Vector4(0.9f, 0.2f, 0.2f, 1);
        private Vector4 colorOk = new Vector4(0.2f, 0.9f, 0.2f, 1);
        private Vector4 colorYellow = new Vector4(0.9f, 0.9f, 0.2f, 1);
        private Vector4 colorInactive = new Vector4(0.5f, 0.5f, 0.5f, 1);

        private string? locStatus;
        private string? locStatusNotActive;
        private string? locStatusPvPMatch;
        private string? locStatusActive;
        private string? locGameData;
        private string? locGameDataError;
        private string? locGameDataLog;
        private string? locPrepNpc;
        private string? locPrepRule;
        private string? locGameNpc;
        private string? locGameMove;
        private string? locGameMoveDisabled;
        private string? locBoardX0;
        private string? locBoardX1;
        private string? locBoardX2;
        private string? locBoardY0;
        private string? locBoardY1;
        private string? locBoardY2;
        private string? locBoardCenter;
        private string? locDebugMode;
        private string? locConfigSolverHints;
        private string? locConfigDeckEditHighlights;
        private string? locConfigOptimizerCPU;
        private string? locConfigOptimizerCPUHint;
        private bool hasCachedLocStrings;

        public PluginWindowStatus(UIReaderTriadGame uiReaderGame, UIReaderTriadPrep uiReaderPrep, PluginPanelAuto autoPanel, PluginPanelCollection collectionPanel, PluginWindowDeckOptimize optimizer) : base("Triad Buddy")
        {
            this.uiReaderGame = uiReaderGame;
            this.uiReaderPrep = uiReaderPrep;
            this.autoPanel = autoPanel;
            this.collectionPanel = collectionPanel;
            this.optimizer = optimizer;

            IsOpen = false;

            SizeConstraints = new WindowSizeConstraints() { MinimumSize = new Vector2(420, 0), MaximumSize = new Vector2(700, 3000) };

            Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar;

            if (Plugin.CurrentLocManager != null)
            {
                Plugin.CurrentLocManager.LocalizationChanged += (_) => { hasCachedLocStrings = false; };
            }
        }

        public void Dispose()
        {
            // ...
        }

        private void UpdateLocalizationCache()
        {
            if (hasCachedLocStrings) { return; }
            hasCachedLocStrings = true;

            locStatus = Localization.Localize("ST_Status", "Status:");
            locStatusNotActive = Localization.Localize("ST_StatusNotActive", "Minigame not active");
            locStatusPvPMatch = Localization.Localize("ST_StatusPvP", "PvP match");
            locStatusActive = Localization.Localize("ST_StatusActive", "Active");
            locGameData = Localization.Localize("ST_GameData", "Game data:");
            locGameDataError = Localization.Localize("ST_GameDataError", "missing! solver disabled");
            locGameDataLog = Localization.Localize("ST_GameDataLog", "cards: {0}, npcs: {1}");
            locPrepNpc = Localization.Localize("ST_PrepNpc", "Prep.NPC:");
            locPrepRule = Localization.Localize("ST_PrepRules", "Prep.Rules:");
            locGameNpc = Localization.Localize("ST_GameNpc", "Solver.NPC:");
            locGameMove = Localization.Localize("ST_GameMove", "Solver.Move:");
            locGameMoveDisabled = Localization.Localize("ST_GameMoveDisabled", "disabled");
            locBoardX0 = Localization.Localize("ST_BoardXLeft", "left");
            locBoardX1 = Localization.Localize("ST_BoardXCenter", "center");
            locBoardX2 = Localization.Localize("ST_BoardXRight", "right");
            locBoardY0 = Localization.Localize("ST_BoardYTop", "top");
            locBoardY1 = Localization.Localize("ST_BoardYCenter", "center");
            locBoardY2 = Localization.Localize("ST_BoardYBottom", "bottom");
            locBoardCenter = Localization.Localize("ST_BoardXYCenter", "center");
            locDebugMode = Localization.Localize("ST_DebugMode", "Show debug details");
            locConfigSolverHints = Localization.Localize("CFG_GameToggleHints", "Show solver hints in game");
            locConfigDeckEditHighlights = Localization.Localize("CFG_DeckEditHighlights", "Show highlights in deck edit");
            locConfigOptimizerCPU = Localization.Localize("CFG_OptimizerParallelLoad", "CPU usage for Deck Optimizer");
            locConfigOptimizerCPUHint = Localization.Localize("CFG_OptimizerParallelLoadHint", "Controls number of logical processors used for calculations. Does not reduce load of individual CPUs!");
        }

        public override void OnOpen()
        {
            showDebugDetails = false;
            collectionPanel.Refresh();
            if (currentTab == Tab.Optimizer)
            {
                optimizer.OnTabShown();
            }
        }

        public override void OnClose()
        {
            // 跟原本關掉最佳化視窗一樣：中止還在跑的最佳化、拿掉牌組編輯畫面的提示
            optimizer.OnClose();
        }

        /// <summary>打開視窗並切到指定分頁；已經開著又在同一頁時就關掉（給指令切換用）。</summary>
        public void Toggle(Tab tab)
        {
            if (IsOpen && currentTab == tab)
            {
                IsOpen = false;
                return;
            }

            OpenTab(tab);
        }

        public void OpenTab(Tab tab)
        {
            requestedTab = tab;
            IsOpen = true;
        }

        private Tab currentTab = Tab.Status;

        public override void Draw()
        {
            UpdateLocalizationCache();

            if (!ImGui.BeginTabBar("##triadBuddyTabs"))
            {
                return;
            }

            DrawTab(Tab.Status, "狀態", DrawStatus);
            DrawTab(Tab.Auto, "自動對戰", autoPanel.Draw);
            DrawTab(Tab.Collection, "卡片收集", collectionPanel.Draw);
            DrawTab(Tab.Optimizer, "牌組最佳化", DrawOptimizer);
            DrawTab(Tab.Config, "設定", DrawConfiguration);

            requestedTab = null;
            ImGui.EndTabBar();
        }

        private void DrawTab(Tab tab, string label, Action draw)
        {
            var flags = (requestedTab == tab) ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (ImGui.BeginTabItem(label, flags))
            {
                if (currentTab != tab)
                {
                    if (currentTab == Tab.Optimizer)
                    {
                        optimizer.OnTabHidden();
                    }

                    currentTab = tab;
                    if (tab == Tab.Collection)
                    {
                        collectionPanel.OnShown();
                    }
                    else if (tab == Tab.Optimizer)
                    {
                        optimizer.OnTabShown();
                    }
                }

                ImGui.Spacing();
                draw();
                ImGui.EndTabItem();
            }
        }

        private void DrawOptimizer()
        {
            if (!optimizer.CanRunOptimizer())
            {
                ImGui.TextColored(colorErr, "讀不到你擁有的卡片，無法最佳化");
                return;
            }

            if (!optimizer.HasNpc)
            {
                ImGui.TextWrapped("請從「卡片收集」分頁的 NPC 資訊，或對戰前牌組評估視窗的最佳化按鈕選擇對手。");
                return;
            }

            optimizer.Draw();
        }

        private void DrawConfiguration()
        {
            bool hasChanges = false;

            var showSolverHintsInGameCopy = Service.pluginConfig.ShowSolverHintsInGame;
            var showDeckEditHighlightsCopy = Service.pluginConfig.ShowDeckEditHighlights;
            var deckOptimizerCPUCopy = (int)(100 * Service.pluginConfig.DeckOptimizerCPU);

            hasChanges = ImGui.Checkbox(locConfigSolverHints, ref showSolverHintsInGameCopy) || hasChanges;
            hasChanges = ImGui.Checkbox(locConfigDeckEditHighlights, ref showDeckEditHighlightsCopy) || hasChanges;

            ImGui.Spacing();
            ImGui.Text(locConfigOptimizerCPU);
            ImGui.SameLine();
            ImGuiComponents.HelpMarker(locConfigOptimizerCPUHint ?? "");
            ImGui.SameLine();
            int maxProcessors = Math.Max(1, Environment.ProcessorCount);
            int numProcessors = Math.Max(1, (int)(maxProcessors * deckOptimizerCPUCopy * 0.01f));
            ImGui.TextColored(colorInactive, $"({numProcessors} / {maxProcessors})");

            hasChanges = ImGui.SliderInt("##optimizerCPU", ref deckOptimizerCPUCopy, 1, 100, "%d%%") || hasChanges;

            // fork：前往 NPC
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Text("前往 NPC");

            var useMount = Service.pluginConfig.TravelUseMount;
            if (ImGui.Checkbox("騎坐騎前往", ref useMount))
            {
                Service.pluginConfig.TravelUseMount = useMount;
                hasChanges = true;
            }
            ImGui.SameLine();
            ImGuiComponents.HelpMarker("離 NPC 超過 30 米、而且這裡可以騎坐騎時，先上坐騎。");

            if (useMount)
            {
                ImGui.Indent();

                ImGui.SetNextItemWidth(220);
                if (MountPicker.Draw("坐騎", Service.pluginConfig.TravelMountId, out var newMount))
                {
                    Service.pluginConfig.TravelMountId = newMount;
                    hasChanges = true;
                }

                var fly = Service.pluginConfig.TravelFly;
                if (ImGui.Checkbox("能飛就飛", ref fly))
                {
                    Service.pluginConfig.TravelFly = fly;
                    hasChanges = true;
                }
                ImGui.SameLine();
                ImGuiComponents.HelpMarker("這個區域的風脈泉都解完了才會飛，主城等不能飛的地方會用騎的。");

                var dismount = Service.pluginConfig.TravelDismount;
                if (ImGui.Checkbox("抵達後下坐騎", ref dismount))
                {
                    Service.pluginConfig.TravelDismount = dismount;
                    hasChanges = true;
                }
                ImGui.SameLine();
                ImGuiComponents.HelpMarker("飛行中會先降落再下坐騎。");

                ImGui.Unindent();
            }

            if (hasChanges)
            {
                Service.pluginConfig.ShowSolverHintsInGame = showSolverHintsInGameCopy;
                Service.pluginConfig.ShowDeckEditHighlights = showDeckEditHighlightsCopy;
                Service.pluginConfig.DeckOptimizerCPU = deckOptimizerCPUCopy * 0.01f;
                Service.pluginConfig.Save();
            }
        }

        private void DrawStatus()
        {
            if (SolverUtils.solverGame == null)
            {
                return;
            }

            ImGui.AlignTextToFramePadding();
            ImGui.Text(locStatus);
            ImGui.SameLine();

            bool isPvPMatch = (uiReaderGame.status == UIReaderTriadGame.Status.PvPMatch) || (SolverUtils.solverGame.status == SolverGame.Status.FailedToParseNpc);
            var statusDesc =
                isPvPMatch ? locStatusPvPMatch :
                uiReaderGame.HasErrors ? uiReaderGame.status.ToString() :
                SolverUtils.solverGame.HasErrors ? SolverUtils.solverGame.status.ToString() :
                !uiReaderGame.IsVisible ? locStatusNotActive :
                locStatusActive;

            var statusColor =
                isPvPMatch ? colorYellow :
                uiReaderGame.HasErrors || SolverUtils.solverGame.HasErrors ? colorErr :
                colorOk;

            var availRegionWidth = ImGui.GetWindowContentRegionMax().X - ImGui.GetWindowContentRegionMin().X;

            ImGui.TextColored(statusColor, statusDesc);
            ImGui.SameLine(availRegionWidth - (20 * ImGuiHelpers.GlobalScale));

            if (ImGuiComponents.IconButton(FontAwesomeIcon.Bug))
            {
                showDebugDetails = !showDebugDetails;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(locDebugMode);
            }

            ImGui.Text(locGameData);
            ImGui.SameLine();
            int numCards = TriadCardDB.Get().cards.Count;
            int numNpcs = TriadNpcDB.Get().npcs.Count;
            bool isGameDataMissing = numCards == 0 || numNpcs == 0;
            if (isGameDataMissing)
            {
                ImGui.TextColored(colorErr, locGameDataError);
            }
            else
            {
                ImGui.Text(string.Format(locGameDataLog ?? "", numCards, numNpcs));
            }

            ImGui.Separator();

            // context sensitive part
            if (uiReaderPrep.HasDeckSelectionUI || uiReaderPrep.HasMatchRequestUI)
            {
                if (SolverUtils.solverPreGameDecks != null)
                {
                    var rulesDesc = "--";

                    var npcDesc = (SolverUtils.solverPreGameDecks.preGameNpc != null) ? SolverUtils.solverPreGameDecks.preGameNpc.Name.GetLocalized() : uiReaderPrep.cachedState.npc;
                    if (SolverUtils.solverPreGameDecks.preGameMods.Count > 0)
                    {
                        rulesDesc = "";
                        foreach (var ruleOb in SolverUtils.solverPreGameDecks.preGameMods)
                        {
                            if (rulesDesc.Length > 0) { rulesDesc += ", "; }
                            rulesDesc += ruleOb.GetLocalizedName();
                        }
                    }
                    else
                    {
                        rulesDesc = string.Join(", ", uiReaderPrep.cachedState.rules);
                    }

                    ImGui.Text(locPrepNpc);
                    ImGui.SameLine();
                    ImGui.TextColored(colorYellow, npcDesc);

                    ImGui.Text(locPrepRule);
                    ImGui.SameLine();
                    ImGui.TextColored(colorYellow, rulesDesc);
                }
            }
            else
            {
                ImGui.Text(locGameNpc);
                ImGui.SameLine();
                ImGui.TextColored(colorYellow, (SolverUtils.solverGame.currentNpc != null) ? SolverUtils.solverGame.currentNpc.Name.GetLocalized() : "--");

                ImGui.Text(locGameMove);
                ImGui.SameLine();

                if (isPvPMatch || isGameDataMissing || !Service.pluginConfig.ShowSolverHintsInGame)
                {
                    ImGui.TextColored(colorYellow, locGameMoveDisabled);
                }
                else if (SolverUtils.solverGame.hasMove)
                {
                    var useColor =
                        (SolverUtils.solverGame.moveWinChance.expectedResult == ETriadGameState.BlueWins) ? colorOk :
                        (SolverUtils.solverGame.moveWinChance.expectedResult == ETriadGameState.BlueDraw) ? colorYellow :
                        colorErr;

                    string humanCard = (SolverUtils.solverGame.moveCard != null) ? SolverUtils.solverGame.moveCard.Name.GetLocalized() : "??";
                    int boardX = SolverUtils.solverGame.moveBoardIdx % 3;
                    int boardY = SolverUtils.solverGame.moveBoardIdx / 3;
                    string? humanBoardX = boardX == 0 ? locBoardX0 : (boardX == 1) ? locBoardX1 : locBoardX2;
                    string? humanBoardY = boardY == 0 ? locBoardY0 : (boardY == 1) ? locBoardY1 : locBoardY2;
                    string? humanBoard = (SolverUtils.solverGame.moveBoardIdx == 4) ? locBoardCenter : $"{humanBoardY}, {humanBoardX}";

                    // 1 based indexing for humans, disgusting
                    ImGui.TextColored(useColor, $"[{SolverUtils.solverGame.moveCardIdx + 1}] {humanCard} => {humanBoard}");
                }
                else
                {
                    ImGui.TextColored(colorYellow, "--");
                }
            }

            if (showDebugDetails)
            {
                ImGui.Separator();
                ImGui.Spacing();

                DrawDebugDetails();
            }
        }

        private void DrawDebugDetails()
        {
            // mostly for debugging purposes, try avoiding localized texts
            // visualize current uistate 
            if (SolverUtils.solverGame == null || SolverUtils.solverGame.DebugScreenMemory == null)
            {
                return;
            }

            // rules
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.Text($"{FontAwesomeIcon.Book.ToIconString()}");
            ImGui.PopFont();
            ImGui.SameLine();

            string modDesc = "";
            foreach (var mod in SolverUtils.solverGame.DebugScreenMemory.gameSolver.simulation.modifiers)
            {
                if (modDesc.Length > 0) { modDesc += ", "; }
                modDesc += mod.GetLocalizedName();
            }
            ImGui.TextColored(colorYellow, modDesc);

            // swap card
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.Text($"{FontAwesomeIcon.ArrowsAltH.ToIconString()}");
            ImGui.PopFont();
            ImGui.SameLine();
            ImGui.Text($"{SolverUtils.solverGame.DebugScreenMemory.swappedBlueCardIdx}");

            // solver chances
            ImGui.PushFont(UiBuilder.IconFont);
            ImGui.Text($"{FontAwesomeIcon.Question.ToIconString()}");
            ImGui.PopFont();
            ImGui.SameLine();
            ImGui.TextUnformatted(SolverUtils.solverGame.moveWinChance.ToString());

            // cards
            const uint colorBlue = 0x80ff9400;
            const uint colorRed = 0x800000ff;
            const uint colorNope = 0x80ffffff;
            const uint colorHidden = 0x8000ffff;
            const uint colorForced = 0x80ffffff;

            var pos = ImGui.GetCursorScreenPos();
            pos.Y += 10;
            orgDrawPosX = pos.X;

            var linePos = new Vector2[3];
            var redDeckOffsetX = 50;

            // - board
            for (int idxY = 0; idxY < 3; idxY++)
            {
                for (int idxX = 0; idxX < 3; idxX++)
                {
                    int cellId = idxX + (idxY * 3);
                    var boardCellOb = SolverUtils.solverGame.DebugScreenMemory.gameState.board[cellId];
                    uint useColor = (boardCellOb == null) ? colorNope :
                        (boardCellOb.owner == ETriadCardOwner.Blue) ? colorBlue :
                        (boardCellOb.owner == ETriadCardOwner.Red) ? colorRed :
                        colorNope;

                    DrawPaddedCardHelper(ref pos, boardCellOb == null ? null : boardCellOb.card, useColor);
                }

                linePos[idxY] = pos;
                DrawPaddedNewline(ref pos);
            }

            // - red: unknown cards
            var redDeckInst = SolverUtils.solverGame.DebugScreenMemory.deckRed;
            var (redKnownCards, redUnknownCards) = SolverUtils.solverGame.GetScreenRedDeckDebug();

            pos = linePos[0] + new Vector2(redDeckOffsetX, 0);
            var posDeckStartX = pos.X;
            DrawPaddedIcon(ref pos, FontAwesomeIcon.Question, colorRed);
            for (int idx = 0; idx < 5; idx++)
            {
                if (idx < redUnknownCards.Count)
                {
                    var cardOb = redUnknownCards[idx];
                    DrawPaddedCardHelper(ref pos, cardOb, cardOb != null && !cardOb.IsValid() ? colorHidden : colorRed);
                }
                else
                {
                    DrawPaddedCardHelper(ref pos, null, colorRed);
                }
            }

            // - red: known cards
            pos = linePos[1] + new Vector2(redDeckOffsetX, 0);
            DrawPaddedIcon(ref pos, FontAwesomeIcon.Check, colorRed);
            for (int idx = 0; idx < 5; idx++)
            {
                if (idx < redKnownCards.Count)
                {
                    var cardOb = redKnownCards[idx];
                    DrawPaddedCardHelper(ref pos, cardOb, cardOb != null && !cardOb.IsValid() ? colorHidden : colorRed);
                }
                else
                {
                    DrawPaddedCardHelper(ref pos, null, colorRed);
                }
            }

            // - red: ui state
            pos = linePos[2] + new Vector2(redDeckOffsetX, 0);
            DrawPaddedIcon(ref pos, FontAwesomeIcon.Eye, colorRed);
            if (SolverUtils.solverGame.DebugScreenState != null)
            {
                for (int idx = 0; idx < 5; idx++)
                {
                    var cardOb = SolverUtils.solverGame.DebugScreenState.redDeck[idx];
                    DrawPaddedCardHelper(ref pos, cardOb, cardOb != null && !cardOb.IsValid() ? colorHidden : colorRed);
                }
            }

            // - blue: known cards
            DrawPaddedNewline(ref pos);
            pos = new Vector2(posDeckStartX, pos.Y + 10);
            DrawPaddedIcon(ref pos, FontAwesomeIcon.Check, colorBlue);
            if (SolverUtils.solverGame.DebugScreenMemory.deckBlue != null)
            {
                int forcedCardIdx = SolverUtils.solverGame.DebugScreenMemory.gameState?.forcedCardIdx ?? -1;
                for (int idx = 0; idx < 5; idx++)
                {
                    var cardOb = SolverUtils.solverGame.DebugScreenMemory.deckBlue.GetCard(idx);
                    DrawPaddedCardHelper(ref pos, cardOb, idx == forcedCardIdx ? colorForced : colorBlue);
                }
            }

            ImGui.Dummy(new Vector2(400, 180));
        }

        private void DrawPaddedCardHelper(ref Vector2 pos, TriadCard? cardOb, uint cellColor)
        {
            const float halfThickness = debugCellPading / 2;
            float totalSize = debugCellSize + debugCellPading * 2;

            var drawList = ImGui.GetWindowDrawList();
            drawList.AddRect(pos + new Vector2(halfThickness, halfThickness),
                pos + new Vector2(totalSize - halfThickness, totalSize - halfThickness),
                cellColor, 0.0f, ImDrawFlags.None, halfThickness * 2);

            if (cardOb != null && cardOb.IsValid())
            {
                var texture = GetCardTexture(cardOb.Id);
                if (texture != null)
                {
                    drawList.AddImage(texture.Handle,
                        pos + new Vector2(debugCellPading, debugCellPading),
                        pos + new Vector2(debugCellPading + debugCellSize, debugCellPading + debugCellSize));
                }
            }
            else
            {
                var textStr = cardOb != null ? "??" : "-";
                var textSize = ImGui.CalcTextSize(textStr);
                drawList.AddText(pos + new Vector2((totalSize - textSize.X) * 0.5f, (totalSize - textSize.Y) * 0.5f), 0xffffffff, textStr);
            }

            pos.X += totalSize;
        }

        private void DrawPaddedIcon(ref Vector2 pos, FontAwesomeIcon icon, uint color)
        {
            float totalSize = debugCellSize + debugCellPading * 2;

            ImGui.PushFont(UiBuilder.IconFont);
            var textStr = icon.ToIconString();
            var textSize = ImGui.CalcTextSize(textStr);
            var drawList = ImGui.GetWindowDrawList();
            drawList.AddText(pos + new Vector2((totalSize - textSize.X) * 0.5f, (totalSize - textSize.Y) * 0.5f), color, textStr);
            ImGui.PopFont();

            pos.X += totalSize;
        }

        private void DrawPaddedNewline(ref Vector2 pos)
        {
            pos.X = orgDrawPosX;
            pos.Y += debugCellSize + debugCellPading * 2;
        }

        private IDalamudTextureWrap? GetCardTexture(int cardId)
        {
            uint iconId = TriadCardDB.GetCardTextureId(cardId);
            GameIconLookup iconLookup = new(iconId);

            var resource = Service.textureProvider.GetFromGameIcon(iconLookup);
            if (resource != null)
            {
                var hasWrap = resource.TryGetWrap(out IDalamudTextureWrap? resultOb, out Exception? ex);
                if (hasWrap)
                {
                    return resultOb;
                }
            }

            return null;
        }
    }
}
