using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Components;
using System;
using System.Numerics;

namespace TriadBuddyPlugin
{
    /// <summary>自動對戰分頁：開關與設定（fork 新增，畫在主視窗裡）。</summary>
    public class PluginPanelAuto
    {
        private readonly AutoTriad autoTriad;

        private readonly Vector4 colorOk = new(0.2f, 0.9f, 0.2f, 1);
        private readonly Vector4 colorYellow = new(0.9f, 0.9f, 0.2f, 1);
        private readonly Vector4 colorInactive = new(0.5f, 0.5f, 0.5f, 1);

        public PluginPanelAuto(AutoTriad autoTriad)
        {
            this.autoTriad = autoTriad;
        }

        public void Draw()
        {
            var config = Service.pluginConfig;
            bool changed = false;

            // ── 開關 ──
            bool autoPlay = autoTriad.AutoPlay;
            if (ImGui.Checkbox("自動出牌", ref autoPlay))
            {
                autoTriad.AutoPlay = autoPlay;
            }
            ImGui.SameLine();
            ImGuiComponents.HelpMarker("只在跟 NPC 對局時，照解算器的建議自動出牌。");
            ImGui.SameLine();
            ImGui.TextColored(autoPlay ? colorOk : colorInactive, autoTriad.PlayStatus);

            bool autoLoop = autoTriad.AutoLoop;
            if (ImGui.Checkbox("自動循環對戰", ref autoLoop))
            {
                autoTriad.AutoLoop = autoLoop;
            }
            ImGui.SameLine();
            ImGuiComponents.HelpMarker("打開後先跟 NPC 對話打開挑戰視窗，之後會自動「挑戰 → 選牌組 → 對局 → 再挑戰」，\n" +
                "直到這個 NPC 會掉的卡全部已經學會、或背包裡已有卡片道具為止。\n打開循環會一併打開自動出牌。");
            ImGui.SameLine();
            ImGui.TextColored(autoLoop ? colorOk : colorInactive, autoTriad.LoopStatus);

            ImGui.TextColored(colorInactive, "兩個開關每次載入插件都是關閉的。");

            // ── 循環狀態 ──
            if (autoTriad.LoopNpc != null)
            {
                ImGui.Separator();
                ImGui.Text("NPC：");
                ImGui.SameLine();
                ImGui.TextColored(colorYellow, autoTriad.LoopNpcName);

                ImGui.Text($"已打 {autoTriad.MatchesPlayed} 局：勝 {autoTriad.Wins} / 和 {autoTriad.Draws} / 負 {autoTriad.Losses}（連敗 {autoTriad.LossStreak}）");

                if (ImGui.BeginTable("##targets", 2, ImGuiTableFlags.SizingFixedFit))
                {
                    foreach (var target in autoTriad.Targets)
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.Text(target.Name);
                        ImGui.TableNextColumn();
                        if (target.Owned)
                        {
                            ImGui.TextColored(colorInactive, "已學會");
                        }
                        else if (target.InInventory > 0)
                        {
                            ImGui.TextColored(colorOk, $"背包有 {target.InInventory} 張");
                        }
                        else
                        {
                            ImGui.TextColored(colorYellow, "目標");
                        }
                    }

                    ImGui.EndTable();
                }
            }

            // ── 設定 ──
            ImGui.Separator();
            if (ImGui.CollapsingHeader("設定"))
            {
                float moveMin = config.AutoMoveDelayMin, moveMax = config.AutoMoveDelayMax;
                ImGui.SetNextItemWidth(200);
                if (ImGui.DragFloatRange2("出牌前等待（秒）", ref moveMin, ref moveMax, 0.1f, 0.0f, 10f, "%.1f", "%.1f"))
                {
                    config.AutoMoveDelayMin = moveMin;
                    config.AutoMoveDelayMax = moveMax;
                    changed = true;
                }
                ImGui.SameLine();
                ImGuiComponents.HelpMarker("每一手在這個範圍內隨機挑一個秒數再出牌。兩端設成一樣就是固定秒數。");

                bool instant = config.AutoClickInstant;
                if (ImGui.Checkbox("按鈕一出現就馬上按", ref instant))
                {
                    config.AutoClickInstant = instant;
                    changed = true;
                }
                ImGui.SameLine();
                ImGuiComponents.HelpMarker("挑戰、選牌組、再戰的按鈕，畫面準備好就按。\n取消勾選則改成先隨機等一段時間再按。");

                if (!instant)
                {
                    float loopMin = config.AutoLoopDelayMin, loopMax = config.AutoLoopDelayMax;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.DragFloatRange2("按按鈕前等待（秒）", ref loopMin, ref loopMax, 0.1f, 0.0f, 30f, "%.1f", "%.1f"))
                    {
                        config.AutoLoopDelayMin = loopMin;
                        config.AutoLoopDelayMax = loopMax;
                        changed = true;
                    }
                    ImGui.SameLine();
                    ImGuiComponents.HelpMarker("每次在這個範圍內隨機挑一個秒數再按。");
                }

                float timeout = config.AutoClickTimeout;
                ImGui.SetNextItemWidth(200);
                if (ImGui.InputFloat("按了幾秒沒反應算失敗", ref timeout, 1.0f, 5.0f, "%.0f"))
                {
                    config.AutoClickTimeout = Math.Clamp(timeout, 2.0f, 60.0f);
                    changed = true;
                }
                ImGui.SameLine();
                ImGuiComponents.HelpMarker("按下後畫面沒換會每 1.5 秒再按一次，超過這個秒數還是沒換就停止循環。");

                int maxMatches = config.AutoLoopMaxMatches;
                ImGui.SetNextItemWidth(200);
                if (ImGui.InputInt("最多打幾局（0 = 不限）", ref maxMatches))
                {
                    config.AutoLoopMaxMatches = Math.Max(0, maxMatches);
                    changed = true;
                }

                int maxLosses = config.AutoLoopMaxLossStreak;
                ImGui.SetNextItemWidth(200);
                if (ImGui.InputInt("連輸幾局就停（0 = 不限）", ref maxLosses))
                {
                    config.AutoLoopMaxLossStreak = Math.Max(0, maxLosses);
                    changed = true;
                }

                bool useBest = config.AutoLoopUseBestDeck;
                if (ImGui.Checkbox("使用插件評估的最佳牌組", ref useBest))
                {
                    config.AutoLoopUseBestDeck = useBest;
                    changed = true;
                }

                if (!useBest)
                {
                    int deck = config.AutoLoopDeckIndex + 1;
                    ImGui.SetNextItemWidth(200);
                    if (ImGui.InputInt("固定用第幾組牌組", ref deck))
                    {
                        config.AutoLoopDeckIndex = Math.Clamp(deck - 1, 0, 9);
                        changed = true;
                    }
                }

                bool logEvents = config.AutoDebugLogEvents;
                if (ImGui.Checkbox("記錄介面事件（除錯）", ref logEvents))
                {
                    config.AutoDebugLogEvents = logEvents;
                    autoTriad.UpdateEventLog();
                    changed = true;
                }
                ImGui.SameLine();
                ImGuiComponents.HelpMarker("在聊天欄印出幻卡相關視窗收到的事件與回呼。\n自動操作沒反應時，打開這個並手動操作一次，把聊天欄內容回報。");
            }

            if (changed)
            {
                config.Save();
            }
        }
    }
}
