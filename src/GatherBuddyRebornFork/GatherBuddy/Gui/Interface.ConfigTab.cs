using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface.Utility;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.STD;
using GatherBuddy.Alarms;
using GatherBuddy.AutoGather;
using GatherBuddy.Classes;
using GatherBuddy.Config;
using GatherBuddy.Enums;
using GatherBuddy.FishTimer;
using OtterGui;
using OtterGui.Widgets;
using FishRecord = GatherBuddy.FishTimer.FishRecord;
using GatheringType = GatherBuddy.Enums.GatheringType;
using ImRaii = OtterGui.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class Interface
{
    private static class ConfigFunctions
    {
        public static Interface _base = null!;
        
        private static string _fishFilterText = "";
        private static Fish? _selectedFish = null;
        private static string _presetName = "";

        public static void DrawSetInput(string jobName, string oldName, Action<string> setName)
        {
            var tmp = oldName;
            ImGui.SetNextItemWidth(SetInputWidth);
            if (ImGui.InputText($"{jobName} 套裝", ref tmp, 15) && tmp != oldName)
            {
                setName(tmp);
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip($"設定你的 {jobName.ToLowerInvariant()} 套裝的名稱或編號。");
        }

        private static void DrawCheckbox(string label, string description, bool oldValue, Action<bool> setter)
        {
            if (ImGuiUtil.Checkbox(label, description, oldValue, setter))
                GatherBuddy.Config.Save();
        }

        private static void DrawChatTypeSelector(string label, string description, XivChatType currentValue, Action<XivChatType> setter)
        {
            ImGui.SetNextItemWidth(SetInputWidth);
            if (Widget.DrawChatTypeSelector(label, description, currentValue, setter))
                GatherBuddy.Config.Save();
        }

        // Auto-Gather Config
        public static void DrawAutoGatherBox()
            => DrawCheckbox("啟用採集視窗互動（不建議停用）",
                "是否啟用自動採集物品。（「僅尋路模式」中建議停用）",
                GatherBuddy.Config.AutoGatherConfig.DoGathering, b => GatherBuddy.Config.AutoGatherConfig.DoGathering = b);

        public static void DrawGoHomeBox()
        {
            DrawCheckbox("採集完成時回家", "當採集完成時, 自動使用 '/li auto' 指令回家",
                        GatherBuddy.Config.AutoGatherConfig.GoHomeWhenDone, b => GatherBuddy.Config.AutoGatherConfig.GoHomeWhenDone = b);
            ImGui.SameLine();
            ImGuiEx.PluginAvailabilityIndicator([new("Lifestream")]);
            DrawCheckbox("空閒時回家", "當等待下一限時採集點出現時, 自動使用 '/li auto' 指令回家",
                        GatherBuddy.Config.AutoGatherConfig.GoHomeWhenIdle, b => GatherBuddy.Config.AutoGatherConfig.GoHomeWhenIdle = b);
            ImGui.SameLine();
            ImGuiEx.PluginAvailabilityIndicator([new("Lifestream")]);
        }

        public static void DrawUseSkillsForFallabckBox()
            => DrawCheckbox("為備選採集列表物品使用技能",
                "當採集備選採集列表內的物品時, 自動使用相關的採集技能",
                GatherBuddy.Config.AutoGatherConfig.UseSkillsForFallbackItems, b => GatherBuddy.Config.AutoGatherConfig.UseSkillsForFallbackItems = b);

        public static void DrawAbandonNodesBox()
            => DrawCheckbox("自動取消採集點",
                "當採集點沒有目標物品或你已經採集了足夠的目標物品, 且採集點已經無其他需要採集的物品時, 自動取消採集該採集點",
                GatherBuddy.Config.AutoGatherConfig.AbandonNodes, 
                b => GatherBuddy.Config.AutoGatherConfig.AbandonNodes = b);

        public static void DrawCheckRetainersBox()
        {
            DrawCheckbox("檢查雇員庫存", "計算庫存時使用 Allagan Tools 檢查雇員庫存",
                GatherBuddy.Config.AutoGatherConfig.CheckRetainers, b => GatherBuddy.Config.AutoGatherConfig.CheckRetainers = b);
            ImGui.SameLine();
            ImGuiEx.PluginAvailabilityIndicator([new("InventoryTools", "Allagan Tools")]);
        }

        public static void DrawHonkVolumeSlider()
        {
            ImGui.SetNextItemWidth(150);
            var volume = GatherBuddy.Config.AutoGatherConfig.SoundPlaybackVolume;
            if (ImGui.DragInt("播放音量", ref volume, 1, 0, 100))
            {
                if (volume < 0)
                    volume = 0;
                else if (volume > 100)
                    volume = 100;
                GatherBuddy.Config.AutoGatherConfig.SoundPlaybackVolume = volume;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip(
                "自動採集因列表完成而停止時播放音效的音量。\n按住 Ctrl 並點選即可輸入自訂值");
        }

        public static void DrawHonkModeBox()
            => DrawCheckbox("採集完成時播放音效", "完成列表採集後結束自動採集並播放音效",
                GatherBuddy.Config.AutoGatherConfig.HonkMode,   b => GatherBuddy.Config.AutoGatherConfig.HonkMode = b);

        public static void DrawRepairBox()
            => DrawCheckbox("需要時修理裝備",        "裝備即將損壞時自動修理",
                GatherBuddy.Config.AutoGatherConfig.DoRepair, b => GatherBuddy.Config.AutoGatherConfig.DoRepair = b);

        public static void DrawRepairThreshold()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.RepairThreshold;
            if (ImGui.DragInt("修理門檻", ref tmp, 1, 1, 100))
            {
                GatherBuddy.Config.AutoGatherConfig.RepairThreshold = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("裝備耐久度低於此百分比時進行修理。");
        }

        public static void DrawFishingSpotMinutes()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.MaxFishingSpotMinutes;
            if (ImGui.DragInt("釣場最長停留時間（分鐘）", ref tmp, 1, 1, 40))
            {
                GatherBuddy.Config.AutoGatherConfig.MaxFishingSpotMinutes = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("在同一釣場停留釣魚的最長分鐘數。");
        }

        public static void DrawAutoretainerBox()
        {
            DrawCheckbox("等待 AutoRetainer 多重模式", "AutoRetainer 多重模式有雇員需要處理時，自動暫停 GBR",
                GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiMode, b => GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiMode = b);
            ImGui.SameLine();
            ImGuiEx.PluginAvailabilityIndicator([new ImGuiEx.RequiredPluginInfo("AutoRetainer")]);
        }

        public static void DrawAutoretainerThreshold()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiModeThreshold;
            if (ImGui.DragInt("AutoRetainer 門檻（秒）", ref tmp, 1, 0, 3600))
            {
                GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiModeThreshold = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("雇員探險完成前多少秒，GBR 應暫停並等待多重模式。");
        }

        public static void DrawAutoretainerTimedNodeDelayBox()
            => DrawCheckbox("為限時採集點延後 AutoRetainer",
                "等目前或即將出現的限時採集點完成後，再處理雇員。",
                GatherBuddy.Config.AutoGatherConfig.AutoRetainerDelayForTimedNodes,
                b => GatherBuddy.Config.AutoGatherConfig.AutoRetainerDelayForTimedNodes = b);

        public static void DrawLifestreamCommandTextInput()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.LifestreamCommand;
            if (ImGui.InputText("Lifestream 指令", ref tmp, 100))
            {
                if (string.IsNullOrEmpty(tmp))
                    tmp = "auto";
                GatherBuddy.Config.AutoGatherConfig.LifestreamCommand = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip(
                "閒置或採集完成時使用的指令。請勿包含「/li」。\n修改時請小心，GBR 不會驗證此指令！");
        }

        public static void DrawFishCollectionBox()
            => DrawCheckbox("參與釣魚資料收集",
                "啟用後，每次釣到魚都會將該魚的資料上傳至遠端伺服器。\n"
              + "這些資料用於協助建立可用的自動釣魚功能。\n"
              + "不會收集你或角色的個人資訊，只會收集與釣獲魚類相關的資料。\n"
              + "隨時取消勾選即可退出資料收集。", GatherBuddy.Config.AutoGatherConfig.FishDataCollection,
                b => GatherBuddy.Config.AutoGatherConfig.FishDataCollection = b);

        public static void DrawMaterialExtraction()
            => DrawCheckbox("自動精製",
                "你需要安裝 Daily Routines 並開啟 \"自動精製魔晶石\" 模組",
                GatherBuddy.Config.AutoGatherConfig.DoMaterialize,
                b => GatherBuddy.Config.AutoGatherConfig.DoMaterialize = b);

        public static void DrawAetherialReduction()
            => DrawCheckbox("自動精選",
                "閒置或庫存已滿時自動進行以太之光淨化",
                GatherBuddy.Config.AutoGatherConfig.DoReduce,
                b => GatherBuddy.Config.AutoGatherConfig.DoReduce = b);

        public static void DrawUseFlagBox()
            => DrawCheckbox("停用地圖標記導航",            "是否使用地圖標記進行導航（僅限時採集點）",
                GatherBuddy.Config.AutoGatherConfig.DisableFlagPathing, b => GatherBuddy.Config.AutoGatherConfig.DisableFlagPathing = b);

        public static void DrawFarNodeFilterDistance()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.FarNodeFilterDistance;
            if (ImGui.DragFloat("過遠採集點距離過濾", ref tmp, 0.1f, 0.1f, 100f))
            {
                GatherBuddy.Config.AutoGatherConfig.FarNodeFilterDistance = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip(
                "在尋找非空採集點時，GBR將過濾掉任何比這更靠近你的採集點。防止檢查已經看到為空的採集點。");
        }

        public static void DrawTimedNodePrecog()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.TimedNodePrecog;
            if (ImGui.DragInt("限時採集點預知（秒）", ref tmp, 1, 0, 600))
            {
                GatherBuddy.Config.AutoGatherConfig.TimedNodePrecog = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("在限時採集點出現前，GBR應該提前多久認為限時採集點出現");
        }

        public static void DrawExecutionDelay()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = (int)GatherBuddy.Config.AutoGatherConfig.ExecutionDelay;
            if (ImGui.DragInt("執行延遲 (毫秒)", ref tmp, 1, 0, 1500))
            {
                GatherBuddy.Config.AutoGatherConfig.ExecutionDelay = (uint)Math.Min(Math.Max(0, tmp), 10000);
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("使用任一技能前需要等待的時間");
        }

        public static void DrawUseGivingLandOnCooldown()
            => DrawCheckbox("當 大地的恩惠 冷卻完成時, 優先採集任意水晶",
                "當大地的恩惠可用時，在任意普通採集點上隨機採集水晶，不管當前的目標物品是什麼",
                GatherBuddy.Config.AutoGatherConfig.UseGivingLandOnCooldown,
                b => GatherBuddy.Config.AutoGatherConfig.UseGivingLandOnCooldown = b);

        public static void DrawMountUpDistance()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.MountUpDistance;
            if (ImGui.DragFloat("需要上坐騎的距離", ref tmp, 0.1f, 0.1f, 100f))
            {
                GatherBuddy.Config.AutoGatherConfig.MountUpDistance = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("你將會上坐騎來移動到下一個採集點的距離。");
        }

        public static void DrawMoveWhileMounting()
            => DrawCheckbox("召喚坐騎時便開始尋路",
                "當讀條召喚坐騎時便直接開始尋路",
                GatherBuddy.Config.AutoGatherConfig.MoveWhileMounting,
                b => GatherBuddy.Config.AutoGatherConfig.MoveWhileMounting = b);

        public static void DrawAntiStuckCooldown()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.NavResetCooldown;
            if (ImGui.DragFloat("防卡冷卻", ref tmp, 0.1f, 0.1f, 10f))
            {
                GatherBuddy.Config.AutoGatherConfig.NavResetCooldown = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("你卡死之後多少秒導航系統將會重置。");
        }

        public static void DrawForceWalkingBox()
            => DrawCheckbox("強制行走",                      "強制以行走替代騎乘前往採集點。",
                GatherBuddy.Config.AutoGatherConfig.ForceWalking, b => GatherBuddy.Config.AutoGatherConfig.ForceWalking = b);

        public static void DrawUseNavigationBox()
            => DrawCheckbox("使用 vnavmesh 導航",             "使用 vnavmesh 導航自動移動角色",
                GatherBuddy.Config.AutoGatherConfig.UseNavigation, b => GatherBuddy.Config.AutoGatherConfig.UseNavigation = b);

        public static void DrawStuckThreshold()
        {
            ImGui.SetNextItemWidth(150);
            var tmp = GatherBuddy.Config.AutoGatherConfig.NavResetThreshold;
            if (ImGui.DragFloat("卡死閾值", ref tmp, 0.1f, 0.1f, 10f))
            {
                GatherBuddy.Config.AutoGatherConfig.NavResetThreshold = tmp;
                GatherBuddy.Config.Save();
            }

            ImGuiUtil.HoverTooltip("多少秒之後導航系統將會判定你是否卡死。");
        }

        public static void DrawAntiStuckSettings()
        {
            var config = GatherBuddy.Config.AutoGatherConfig.AntiStuck;
            DrawCheckbox("啟用防卡死功能",
                "先使用隨機位移做近端復原；多次復原仍失敗且持續停在同一區域時，可升級為傳送或回家。",
                config.Enabled, b => config.Enabled = b);
            if (!config.Enabled)
                return;

            ImGui.Indent();
            DrawCheckbox("啟用近端復原", "偵測 vnavmesh 卡住時嘗試短暫隨機移動。",
                config.LocalRecoveryEnabled, b => config.LocalRecoveryEnabled = b);
            DrawCheckbox("啟用升級措施", "達到近端復原失敗次數後，才開始計算區域停滯時間。預設關閉。",
                config.EscalationEnabled, b => config.EscalationEnabled = b);

            if (config.EscalationEnabled)
            {
                ImGui.Indent();
                var failures = config.EscalationAfterFails;
                ImGui.SetNextItemWidth(SetInputWidth);
                if (ImGui.SliderInt("復原失敗次數", ref failures, 1, 10))
                {
                    config.EscalationAfterFails = Math.Clamp(failures, 1, 10);
                    GatherBuddy.Config.Save();
                }
                ImGuiUtil.HoverTooltip("至少觸發幾次近端復原後，才允許強制措施。");

                var radius = config.AreaRadius;
                ImGui.SetNextItemWidth(SetInputWidth);
                if (ImGui.SliderFloat("停滯區域半徑", ref radius, 10, 100, "%.0f yalms"))
                {
                    config.AreaRadius = Math.Clamp(radius, 10, 100);
                    GatherBuddy.Config.Save();
                }

                var seconds = config.AreaTimeSeconds;
                ImGui.SetNextItemWidth(SetInputWidth);
                if (ImGui.SliderInt("區域停滯時間（秒）", ref seconds, 30, 600))
                {
                    config.AreaTimeSeconds = Math.Clamp(seconds, 30, 600);
                    GatherBuddy.Config.Save();
                }

                var action = (int)config.DrasticAction;
                var actions = new[] { "不執行", "傳送至區域內以太之光並繼續", "使用 Lifestream 回家並停止" };
                ImGui.SetNextItemWidth(SetInputWidth * 1.5f);
                if (ImGui.Combo("強制措施", ref action, actions, actions.Length))
                {
                    config.DrasticAction = (AutoGatherConfig.PositionUnstuckAction)action;
                    GatherBuddy.Config.Save();
                }

                if (ImGui.TreeNodeEx("進階設定##AntiStuckAdvanced"))
                {
                    var cooldown = config.DrasticCooldownSeconds;
                    ImGui.SetNextItemWidth(SetInputWidth);
                    if (ImGui.SliderInt("強制措施冷卻（秒）", ref cooldown, 60, 1800))
                    {
                        config.DrasticCooldownSeconds = Math.Clamp(cooldown, 60, 1800);
                        GatherBuddy.Config.Save();
                    }

                    var maximum = config.MaxDrasticPerSession;
                    ImGui.SetNextItemWidth(SetInputWidth);
                    if (ImGui.SliderInt("每次啟用上限", ref maximum, 1, 10))
                    {
                        config.MaxDrasticPerSession = Math.Clamp(maximum, 1, 10);
                        GatherBuddy.Config.Save();
                    }
                    ImGui.TreePop();
                }
                ImGui.Unindent();
            }
            ImGui.Unindent();
        }

        public static void DrawScheduledCommandSettings()
        {
            var config = GatherBuddy.Config.AutoGatherConfig;
            DrawCheckbox("啟用排程指令",
                "自動採集啟用期間定期暫停，等待目前採集完成後執行指定指令，再自動恢復。",
                config.EnableScheduledCommand, b => config.EnableScheduledCommand = b);
            if (!config.EnableScheduledCommand)
                return;

            ImGui.Indent();
            var interval = config.ScheduledCommandIntervalMinutes;
            ImGui.SetNextItemWidth(SetInputWidth);
            if (ImGui.SliderInt("執行間隔（分鐘）##ScheduledInterval", ref interval, 1, 240))
            {
                config.ScheduledCommandIntervalMinutes = Math.Clamp(interval, 1, 240);
                GatherBuddy.Config.Save();
            }

            var command = config.ScheduledCommand;
            ImGui.SetNextItemWidth(SetInputWidth * 1.5f);
            if (ImGui.InputText("執行指令##ScheduledCommand", ref command, 200))
            {
                config.ScheduledCommand = command;
                GatherBuddy.Config.Save();
            }
            ImGuiUtil.HoverTooltip("必須以 / 開頭，例如 /li auto。若為 /li 指令，恢復前也會確認 Lifestream 已完成。");

            var delay = config.ScheduledCommandResumeDelayMinutes;
            ImGui.SetNextItemWidth(SetInputWidth);
            if (ImGui.SliderInt("最短恢復延遲（分鐘）##ScheduledDelay", ref delay, 1, 60))
            {
                config.ScheduledCommandResumeDelayMinutes = Math.Clamp(delay, 1, 60);
                GatherBuddy.Config.Save();
            }

            DrawCheckbox("執行前關閉採集視窗", "排程指令執行前關閉採集相關視窗。",
                config.CloseGatheringUiBeforeScheduledCommand, b => config.CloseGatheringUiBeforeScheduledCommand = b);

            var status = GatherBuddy.AutoGather.GetScheduledCommandStatus();
            if (!string.IsNullOrEmpty(status))
                ImGui.TextColored(new Vector4(0.5f, 1, 0.5f, 1), $"[排程狀態] {status}");
            ImGui.Unindent();
        }

        public static void DrawSortingMethodCombo()
        {
            var v = GatherBuddy.Config.AutoGatherConfig.SortingMethod;
            ImGui.SetNextItemWidth(150);

            using var combo = ImRaii.Combo("物品排序方法", v.ToString());
            ImGuiUtil.HoverTooltip("內部排序物品時所使用的方法");
            if (!combo)
                return;

            if (ImGui.Selectable("依位置排序", v == AutoGatherConfig.SortingType.Location))
            {
                GatherBuddy.Config.AutoGatherConfig.SortingMethod = AutoGatherConfig.SortingType.Location;
                GatherBuddy.Config.Save();
            }

            if (ImGui.Selectable("不排序", v == AutoGatherConfig.SortingType.None))
            {
                GatherBuddy.Config.AutoGatherConfig.SortingMethod = AutoGatherConfig.SortingType.None;
                GatherBuddy.Config.Save();
            }
        }

        // General Config
        public static void DrawOpenOnStartBox()
            => DrawCheckbox("啟動時開啟設定介面",
                "切換在啟動遊戲後 GatherBuddy 介面是否可見。",
                GatherBuddy.Config.OpenOnStart, b => GatherBuddy.Config.OpenOnStart = b);

        public static void DrawLockPositionBox()
            => DrawCheckbox("鎖定設定介面移動",
                "切換 GatherBuddy 介面移動是否被鎖定。",
                GatherBuddy.Config.MainWindowLockPosition, b =>
                {
                    GatherBuddy.Config.MainWindowLockPosition = b;
                    _base.UpdateFlags();
                });

        public static void DrawLockResizeBox()
            => DrawCheckbox("鎖定設定介面尺寸",
                "切換 GatherBuddy 介面尺寸是否被鎖定。",
                GatherBuddy.Config.MainWindowLockResize, b =>
                {
                    GatherBuddy.Config.MainWindowLockResize = b;
                    _base.UpdateFlags();
                });

        public static void DrawRespectEscapeBox()
            => DrawCheckbox("ESC 關閉主介面",
                "切換聚焦主視窗時按 ESC 鍵是否關閉主視窗。",
                GatherBuddy.Config.CloseOnEscape, b =>
                {
                    GatherBuddy.Config.CloseOnEscape = b;
                    _base.UpdateFlags();
                });

        public static void DrawGearChangeBox()
            => DrawCheckbox("啟用套裝切換",
                "切換是否根據採集點自動切換到正確的職業套裝。\n使用採礦工套裝，園藝工套裝以及捕魚人套裝。",
                GatherBuddy.Config.UseGearChange, b => GatherBuddy.Config.UseGearChange = b);

        public static void DrawTeleportBox()
            => DrawCheckbox("啟用傳送",
                "切換是否自動傳送到所選的採集點。",
                GatherBuddy.Config.UseTeleport, b => GatherBuddy.Config.UseTeleport = b);

        public static void DrawMapOpenBox()
            => DrawCheckbox("開啟帶位置的地圖",
                "切換是否自動開啟所選的採集點所在地區的地圖且高亮其採集位置。",
                GatherBuddy.Config.UseCoordinates, b => GatherBuddy.Config.UseCoordinates = b);

        public static void DrawPlaceMarkerBox()
            => DrawCheckbox("在地圖上放置旗幟標記",
                "切換是否自動在所選採集點的近似位置放一個紅色小旗標記且不開啟地圖。",
                GatherBuddy.Config.UseFlag, b => GatherBuddy.Config.UseFlag = b);

        public static void DrawMapMarkerPrintBox()
            => DrawCheckbox("列印地圖位置",
                "切換是否自動在聊天欄中傳送一個所選採集點近似位置的地圖連結。",
                GatherBuddy.Config.WriteCoordinates, b => GatherBuddy.Config.WriteCoordinates = b);

        public static void DrawPlaceWaymarkBox()
            => DrawCheckbox("放置自定義場景標記",
                "切換是否放置你為確定位置手動設定的場景標記。",
                GatherBuddy.Config.PlaceCustomWaymarks, b => GatherBuddy.Config.PlaceCustomWaymarks = b);

        public static void DrawPrintUptimesBox()
            => DrawCheckbox("採集時列印採集點出現時間",
                "列印你嘗試 /gather 的限時採集點的出現時間到聊天欄。",
                GatherBuddy.Config.PrintUptime, b => GatherBuddy.Config.PrintUptime = b);

        public static void DrawSkipTeleportBox()
            => DrawCheckbox("跳過附近傳送",
                "如果你已經在同一地圖且比所選的以太之光更靠近目標，跳過傳送。",
                GatherBuddy.Config.SkipTeleportIfClose, b => GatherBuddy.Config.SkipTeleportIfClose = b);

        public static void DrawShowStatusLineBox()
            => DrawCheckbox("顯示狀態線",
                "在可採集物和魚類表下面顯示一條狀態線。",
                GatherBuddy.Config.ShowStatusLine, v => GatherBuddy.Config.ShowStatusLine = v);

        public static void DrawHideClippyBox()
            => DrawCheckbox("隱藏採集助手按鈕",
                "永遠隱藏可採集物和魚類標籤頁中的採集助手按鈕。",
                GatherBuddy.Config.HideClippy, v => GatherBuddy.Config.HideClippy = v);

        private const string ChatInformationString =
            "注意，無論選擇的頻道是什麼，訊息只會被列印到你的聊天日誌中"
          + " - 其他人不會看到你“說”的話。";

        public static void DrawPrintTypeSelector()
            => DrawChatTypeSelector("訊息的聊天型別",
                "用於列印由 GatherBuddy 發出的常規訊息的聊天型別。\n"
              + ChatInformationString,
                GatherBuddy.Config.ChatTypeMessage, t => GatherBuddy.Config.ChatTypeMessage = t);

        public static void DrawErrorTypeSelector()
            => DrawChatTypeSelector("錯誤的聊天型別",
                "用於列印由 GatherBuddy 發出的錯誤訊息的聊天型別。\n"
              + ChatInformationString,
                GatherBuddy.Config.ChatTypeError, t => GatherBuddy.Config.ChatTypeError = t);

        public static void DrawContextMenuBox()
            => DrawCheckbox("新增遊戲內上下文選單",
                "為可採集物的遊戲內右鍵選單新增“採集”條目。",
                GatherBuddy.Config.AddIngameContextMenus, b =>
                {
                    GatherBuddy.Config.AddIngameContextMenus = b;
                    if (b)
                        _plugin.ContextMenu.Enable();
                    else
                        _plugin.ContextMenu.Disable();
                });

        public static void DrawPreferredJobSelect()
        {
            var v       = GatherBuddy.Config.PreferredGatheringType;
            var current = v == GatheringType.多职业 ? "無偏好" : v.ToString();
            ImGui.SetNextItemWidth(SetInputWidth);
            using var combo = ImRaii.Combo("偏好職業", current);
            ImGuiUtil.HoverTooltip(
                "在採集採礦工和園藝工都可以採集物品時，選擇你的職業偏好。\n"
              + "當兩者都可以採集物品時，這實際上將通常的採集命令轉換為 /gathermin 或 /gatherbtn，"
              + "即便連續嘗試，也忽略其他選項。");
            if (!combo)
                return;

            if (ImGui.Selectable("無偏好", v == GatheringType.多职业) && v != GatheringType.多职业)
            {
                GatherBuddy.Config.PreferredGatheringType = GatheringType.多职业;
                GatherBuddy.Config.Save();
            }

            if (ImGui.Selectable("採礦工", v == GatheringType.采矿工) && v != GatheringType.采矿工)
            {
                GatherBuddy.Config.PreferredGatheringType = GatheringType.采矿工;
                GatherBuddy.Config.Save();
            }

            if (ImGui.Selectable("園藝工", v == GatheringType.园艺工) && v != GatheringType.园艺工)
            {
                GatherBuddy.Config.PreferredGatheringType = GatheringType.园艺工;
                GatherBuddy.Config.Save();
            }
        }

        public static void DrawPrintClipboardBox()
            => DrawCheckbox("列印剪貼簿資訊",
                "當你儲存一個物件到剪貼簿時，不論是否可行都嘗試列印到聊天欄中。",
                GatherBuddy.Config.PrintClipboardMessages, b => GatherBuddy.Config.PrintClipboardMessages = b);

        // Weather Tab
        public static void DrawWeatherTabNamesBox()
            => DrawCheckbox("在天氣標籤頁顯示名稱",
                "切換是否在天氣標籤頁的表格中顯示名稱，或者僅僅顯示在滑鼠懸浮時會顯示名字的圖示。",
                GatherBuddy.Config.ShowWeatherNames, b => GatherBuddy.Config.ShowWeatherNames = b);

        // Alarms
        public static void DrawAlarmToggle()
            => DrawCheckbox("啟用鬧鐘", "切換所有鬧鐘開關", GatherBuddy.Config.AlarmsEnabled,
                b =>
                {
                    if (b)
                        _plugin.AlarmManager.Enable();
                    else
                        _plugin.AlarmManager.Disable();
                });

        private static bool _gatherDebug = false;

        public static void DrawAlarmsInDutyToggle()
            => DrawCheckbox("在副本任務中啟用鬧鐘", "設定當你在副本任務中時鬧鐘是否應該觸發。",
                GatherBuddy.Config.AlarmsInDuty,     b => GatherBuddy.Config.AlarmsInDuty = b);

        public static void DrawAlarmsOnlyWhenLoggedInToggle()
            => DrawCheckbox("僅在遊戲內啟用鬧鐘",  "設定當你未登入任何角色時鬧鐘是否應該觸發。",
                GatherBuddy.Config.AlarmsOnlyWhenLoggedIn, b => GatherBuddy.Config.AlarmsOnlyWhenLoggedIn = b);

        private static void DrawAlarmPicker(string label, string description, Sounds current, Action<Sounds> setter)
        {
            var cur = (int)current;
            ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
            if (ImGui.Combo(new ImU8String(label), ref cur, AlarmCache.SoundIdNames))
                setter((Sounds)cur);
            ImGuiUtil.HoverTooltip(description);
        }

        public static void DrawWeatherAlarmPicker()
            => DrawAlarmPicker("天氣改變鬧鐘提醒", "選擇一個在每8個艾歐澤亞時的正常天氣改變的時候播放的聲音。",
                GatherBuddy.Config.WeatherAlarm,       _plugin.AlarmManager.SetWeatherAlarm);

        public static void DrawHourAlarmPicker()
            => DrawAlarmPicker("艾歐澤亞時改變鬧鐘提醒", "選擇一個在當前艾歐澤亞時改變的時候播放的聲音。",
                GatherBuddy.Config.HourAlarm,              _plugin.AlarmManager.SetHourAlarm);

        // Fish Timer
        public static void DrawFishTimerBox()
            => DrawCheckbox("顯示捕魚計時器",
                "切換是否在釣魚時顯示捕魚計時器。",
                GatherBuddy.Config.ShowFishTimer, b => GatherBuddy.Config.ShowFishTimer = b);

        public static void DrawFishTimerEditBox()
            => DrawCheckbox("編輯捕魚計時器",
                "啟用編輯捕魚計時器視窗。",
                GatherBuddy.Config.FishTimerEdit, b => GatherBuddy.Config.FishTimerEdit = b);

        public static void DrawFishTimerClickthroughBox()
            => DrawCheckbox("捕魚計時器滑鼠穿透",
                "允許滑鼠穿透捕魚計時器，並禁用其上下文選單。",
                GatherBuddy.Config.FishTimerClickthrough, b => GatherBuddy.Config.FishTimerClickthrough = b);

        public static void DrawFishTimerHideBox()
            => DrawCheckbox("在捕魚計時器中隱藏未捕獲的魚",
                "在捕魚計時器視窗中隱藏所有未被給定的釣組和釣餌的組合記錄的魚。",
                GatherBuddy.Config.HideUncaughtFish, b => GatherBuddy.Config.HideUncaughtFish = b);

        public static void DrawFishTimerHideBox2()
            => DrawCheckbox("在捕魚計時器中隱藏不可捕獲的魚",
                "在捕魚計時器視窗中隱藏所有前置條件未滿足的魚，例如 捕魚人之識 或 釣組。",
                GatherBuddy.Config.HideUnavailableFish, b => GatherBuddy.Config.HideUnavailableFish = b);

        public static void DrawFishTimerUptimesBox()
            => DrawCheckbox("在捕魚計時器中顯示出現時間",
                "在捕魚計時器視窗中顯示限時魚的出現時間。",
                GatherBuddy.Config.ShowFishTimerUptimes, b => GatherBuddy.Config.ShowFishTimerUptimes = b);

        public static void DrawKeepRecordsBox()
            => DrawCheckbox("保持捕魚記錄",
                "在你的計算機上儲存捕魚記錄。這對於捕魚計時器視窗的咬鉤計時是必要的。",
                GatherBuddy.Config.StoreFishRecords, b => GatherBuddy.Config.StoreFishRecords = b);

        public static void DrawShowLocalTimeInRecordsBox()
            => DrawCheckbox("記錄使用本地時間",
                "在釣魚記錄分頁顯示時間戳記時，使用本地時間而非 Unix 時間。",
                GatherBuddy.Config.UseUnixTimeFishRecords, b => GatherBuddy.Config.UseUnixTimeFishRecords = b);
        
        public static void DrawFishTimerScale()
        {
            var value = GatherBuddy.Config.FishTimerScale / 1000f;
            ImGui.SetNextItemWidth(SetInputWidth);
            var ret = ImGui.DragFloat("捕魚計時器咬鉤計時尺寸", ref value, 0.1f, FishRecord.MinBiteTime / 500f,
                FishRecord.MaxBiteTime / 1000f,
                "%2.3f 秒");

            ImGuiUtil.HoverTooltip("捕魚計時器視窗咬鉤計時的尺寸取決於這個值。\n"
              + "如果你的咬鉤時間超過該值，進度條和咬鉤視窗將不顯示。\n"
              + "你應該把這個值保持在你的最高咬鉤視窗，且儘可能低。通常來說40秒夠了。");

            if (!ret)
                return;

            var newValue = (ushort)Math.Clamp((int)(value * 1000f + 0.9), FishRecord.MinBiteTime * 2, FishRecord.MaxBiteTime);
            if (newValue == GatherBuddy.Config.FishTimerScale)
                return;

            GatherBuddy.Config.FishTimerScale = newValue;
            GatherBuddy.Config.Save();
        }

        public static void DrawFishTimerIntervals()
        {
            int value = GatherBuddy.Config.ShowSecondIntervals;
            ImGui.SetNextItemWidth(SetInputWidth);
            var ret = ImGui.DragInt("捕魚計時器間隔分隔符", ref value, 0.01f, 0, 16);
            ImGuiUtil.HoverTooltip("捕魚計時器視窗可以顯示若干間隔線和0到16之間的相應的秒數。\n"
              + "設定為0關閉此功能。");
            if (!ret)
                return;

            var newValue = (byte)Math.Clamp(value, 0, 16);
            if (newValue == GatherBuddy.Config.ShowSecondIntervals)
                return;

            GatherBuddy.Config.ShowSecondIntervals = newValue;
            GatherBuddy.Config.Save();
        }
        
        public static void DrawFishTimerIntervalsRounding()
        {
            var value = GatherBuddy.Config.SecondIntervalsRounding;
            ImGui.SetNextItemWidth(SetInputWidth);
            var ret = ImGui.DragInt("釣魚計時器區間小數位數", ref value, 0.01f, 0, 3);
            ImGuiUtil.HoverTooltip("將顯示的秒數四捨五入至指定的小數位數。\n"
                + "設為 0 時僅顯示整數。");
            if (!ret)
                return;

            var newValue = (byte)Math.Clamp(value, 0, 3);
            if (newValue == GatherBuddy.Config.SecondIntervalsRounding)
                return;

            GatherBuddy.Config.SecondIntervalsRounding = newValue;
            GatherBuddy.Config.Save();
        }

        public static void DrawHideFishPopupBox()
            => DrawCheckbox("隱藏捕獲彈窗",
                "阻止彈出顯示你捕獲的魚以及它的大小、數量和質量的彈窗。",
                GatherBuddy.Config.HideFishSizePopup, b => GatherBuddy.Config.HideFishSizePopup = b);

        public static void DrawCollectableHintPopupBox()
            => DrawCheckbox("顯示收藏品提示",
                "在釣魚計時器視窗顯示魚類是否可作為收藏品。",
                GatherBuddy.Config.ShowCollectableHints, b => GatherBuddy.Config.ShowCollectableHints = b);

        public static void DrawDoubleHookHintPopupBox()
            => DrawCheckbox("顯示多重提鉤提示",
                "顯示宇宙探索中的魚是否能以雙重提鉤或三重提鉤捕獲。", // TODO: add ocean fishing when implemented.
                GatherBuddy.Config.ShowMultiHookHints, b => GatherBuddy.Config.ShowMultiHookHints = b);
        
        
        // Fish Stats Window
        public static void DrawEnableFishStats()
            => DrawCheckbox("啟用釣魚統計",
                "新增根據本機記錄彙整與回報釣魚統計的分頁。目前仍在測試。",
                GatherBuddy.Config.EnableFishStats, b => GatherBuddy.Config.EnableFishStats = b);
        public static void DrawEnableReportTime()
            => DrawCheckbox("複製報告時包含時間統計",
                "複製報告時，加入最短與最長咬鉤時間。",
                GatherBuddy.Config.EnableReportTime, b => GatherBuddy.Config.EnableReportTime = b);
        public static void DrawEnableReportSize()
            => DrawCheckbox("複製報告時包含尺寸統計",
                "複製報告時，加入最小與最大尺寸。",
                GatherBuddy.Config.EnableReportSize, b => GatherBuddy.Config.EnableReportSize = b);
        public static void DrawEnableReportMulti()
            => DrawCheckbox("複製報告時包含多重提鉤統計",
                "複製報告時，加入多重提鉤收穫量統計。",
                GatherBuddy.Config.EnableReportMulti, b => GatherBuddy.Config.EnableReportMulti = b);
        public static void DrawEnableGraphs()
            => DrawCheckbox("啟用圖表",
                "檢視釣場時，以圖表呈現釣魚報告資料。此功能仍在高度測試階段！",
                GatherBuddy.Config.EnableFishStatsGraphs, b => GatherBuddy.Config.EnableFishStatsGraphs = b);

        // Spearfishing Helper
        public static void DrawSpearfishHelperBox()
            => DrawCheckbox("顯示刺魚助手",
                "切換是否在刺魚時顯示刺魚助手。",
                GatherBuddy.Config.ShowSpearfishHelper, b => GatherBuddy.Config.ShowSpearfishHelper = b);

        public static void DrawSpearfishNamesBox()
            => DrawCheckbox("顯示魚名懸浮窗",
                "切換是否在刺魚視窗中顯示被識別的魚的名稱。",
                GatherBuddy.Config.ShowSpearfishNames, b => GatherBuddy.Config.ShowSpearfishNames = b);

        public static void DrawAvailableSpearfishBox()
            => DrawCheckbox("顯示可捕獲的魚的列表",
                "切換是否在刺魚視窗的一側顯示當前刺魚點可捕獲的魚的列表。",
                GatherBuddy.Config.ShowAvailableSpearfish, b => GatherBuddy.Config.ShowAvailableSpearfish = b);

        public static void DrawSpearfishSpeedBox()
            => DrawCheckbox("在懸浮窗中顯示魚的速度",
                "切換是否在刺魚視窗中魚的名字旁額外顯示魚的速度。",
                GatherBuddy.Config.ShowSpearfishSpeed, b => GatherBuddy.Config.ShowSpearfishSpeed = b);

        public static void DrawSpearfishCenterLineBox()
            => DrawCheckbox("顯示中心線",
                "切換是否在刺魚視窗中顯示一條從魚叉的中心向上的直線。",
                GatherBuddy.Config.ShowSpearfishCenterLine, b => GatherBuddy.Config.ShowSpearfishCenterLine = b);

        public static void DrawSpearfishIconsAsTextBox()
            => DrawCheckbox("以文字方式顯示速度和尺寸",
                "切換是否以文字替代圖示顯示可捕獲的魚的速度和大小。",
                GatherBuddy.Config.ShowSpearfishListIconsAsText, b => GatherBuddy.Config.ShowSpearfishListIconsAsText = b);

        public static void DrawSpearfishFishNameFixed()
            => DrawCheckbox("在固定位置顯示魚的名稱",
                "切換是在移動的魚上顯示已識別的魚的名稱還是在一個固定位置上顯示。",
                GatherBuddy.Config.FixNamesOnPosition, b => GatherBuddy.Config.FixNamesOnPosition = b);

        public static void DrawSpearfishFishNamePercentage()
        {
            if (!GatherBuddy.Config.FixNamesOnPosition)
                return;

            var tmp = (int)GatherBuddy.Config.FixNamesPercentage;
            ImGui.SetNextItemWidth(SetInputWidth);
            if (!ImGui.DragInt("魚的名稱的位置百分比", ref tmp, 0.1f, 0, 100, "%i%%"))
                return;

            tmp = Math.Clamp(tmp, 0, 100);
            if (tmp == GatherBuddy.Config.FixNamesPercentage)
                return;

            GatherBuddy.Config.FixNamesPercentage = (byte)tmp;
            GatherBuddy.Config.Save();
        }

        // Gather Window
        public static void DrawShowGatherWindowBox()
            => DrawCheckbox("顯示採集視窗",
                "顯示一個包含選定的可採集物和它們的出現時間的小視窗。",
                GatherBuddy.Config.ShowGatherWindow, b => GatherBuddy.Config.ShowGatherWindow = b);

        public static void DrawGatherWindowAnchorBox()
            => DrawCheckbox("以左下角錨定採集視窗",
                "讓採集視窗向頂部增長並從頂部收縮。",
                GatherBuddy.Config.GatherWindowBottomAnchor, b => GatherBuddy.Config.GatherWindowBottomAnchor = b);

        public static void DrawGatherWindowTimersBox()
            => DrawCheckbox("顯示採集視窗計時器",
                "在採集視窗中顯示可採集物的出現時間。",
                GatherBuddy.Config.ShowGatherWindowTimers, b => GatherBuddy.Config.ShowGatherWindowTimers = b);

        public static void DrawGatherWindowAlarmsBox()
            => DrawCheckbox("在採集視窗中顯示啟用的鬧鐘",
                "額外顯示啟用的鬧鐘作為採集視窗的最後一個預設，遵守該視窗的常規規則。",
                GatherBuddy.Config.ShowGatherWindowAlarms, b =>
                {
                    GatherBuddy.Config.ShowGatherWindowAlarms = b;
                    _plugin.GatherWindowManager.SetShowGatherWindowAlarms(b);
                });

        public static void DrawSortGatherWindowBox()
            => DrawCheckbox("在採集視窗中以出現時間排序",
                "按出現時間對採集視窗中所選的物品進行排序。",
                GatherBuddy.Config.SortGatherWindowByUptime, b => GatherBuddy.Config.SortGatherWindowByUptime = b);

        public static void DrawGatherWindowShowOnlyAvailableBox()
            => DrawCheckbox("僅顯示可採集物品",
                "僅顯示採集視窗設定中的當前可採集的物品。",
                GatherBuddy.Config.ShowGatherWindowOnlyAvailable, b => GatherBuddy.Config.ShowGatherWindowOnlyAvailable = b);

        public static void DrawHideGatherWindowCompletedItemsBox()
            => DrawCheckbox("隱藏已經完成的物品",
                "隱藏已經滿足自動採集數量要求的物品",
                GatherBuddy.Config.HideGatherWindowCompletedItems, b => GatherBuddy.Config.HideGatherWindowCompletedItems = b);

        public static void DrawHideGatherWindowInDutyBox()
            => DrawCheckbox("副本任務中隱藏採集視窗",
                "進入任何副本任務後隱藏採集視窗。",
                GatherBuddy.Config.HideGatherWindowInDuty, b => GatherBuddy.Config.HideGatherWindowInDuty = b);

        public static void DrawGatherWindowHoldKey()
        {
            DrawCheckbox("僅在按住快捷鍵時顯示採集視窗",
                "僅在你按住你所選的快捷鍵時顯示採集視窗。",
                GatherBuddy.Config.OnlyShowGatherWindowHoldingKey, b => GatherBuddy.Config.OnlyShowGatherWindowHoldingKey = b);

            if (!GatherBuddy.Config.OnlyShowGatherWindowHoldingKey)
                return;

            ImGui.SetNextItemWidth(SetInputWidth);
            Widget.KeySelector("需要按住的快捷鍵", "設定一個用來按下保持視窗可見的快捷鍵。",
                GatherBuddy.Config.GatherWindowHoldKey,
                k => GatherBuddy.Config.GatherWindowHoldKey = k, Configuration.ValidKeys);
        }

        public static void DrawGatherWindowLockBox()
            => DrawCheckbox("鎖定採集視窗位置",
                "防止拖動移動採集視窗。",
                GatherBuddy.Config.LockGatherWindow, b => GatherBuddy.Config.LockGatherWindow = b);


        public static void DrawGatherWindowHotkeyInput()
        {
            if (Widget.ModifiableKeySelector("開啟採集視窗的快捷鍵", "設定一個用來開啟採集視窗的快捷鍵。", SetInputWidth,
                    GatherBuddy.Config.GatherWindowHotkey, k => GatherBuddy.Config.GatherWindowHotkey = k, Configuration.ValidKeys))
                GatherBuddy.Config.Save();
        }

        public static void DrawMainInterfaceHotkeyInput()
        {
            if (Widget.ModifiableKeySelector("開啟主介面的快捷鍵", "設定一個用來開啟 GatherBuddy 主介面的快捷鍵",
                    SetInputWidth,
                    GatherBuddy.Config.MainInterfaceHotkey, k => GatherBuddy.Config.MainInterfaceHotkey = k, Configuration.ValidKeys))
                GatherBuddy.Config.Save();
        }


        public static void DrawGatherWindowDeleteModifierInput()
        {
            ImGui.SetNextItemWidth(SetInputWidth);
            if (Widget.ModifierSelector("右鍵刪除物品的修飾鍵",
                    "設定用來在右鍵單擊採集視窗中的物品以刪除它們時配合使用的修飾鍵。",
                    GatherBuddy.Config.GatherWindowDeleteModifier, k => GatherBuddy.Config.GatherWindowDeleteModifier = k))
                GatherBuddy.Config.Save();
        }


        public static void DrawAetherytePreference()
        {
            var tmp     = GatherBuddy.Config.AetherytePreference == AetherytePreference.Cost;
            var oldPref = GatherBuddy.Config.AetherytePreference;
            if (ImGui.RadioButton("偏好更便宜的以太之光", tmp))
                GatherBuddy.Config.AetherytePreference = AetherytePreference.Cost;
            var hovered = ImGui.IsItemHovered();
            ImGui.SameLine();
            if (ImGui.RadioButton("偏好更少跑圖時長", !tmp))
                GatherBuddy.Config.AetherytePreference = AetherytePreference.Distance;
            hovered |= ImGui.IsItemHovered();
            if (hovered)
                ImGui.SetTooltip(
                    "指定你是更喜歡離目標更近的以太之光（更少的跑圖時間）"
                  + " 還是在為了指定物品掃描所有可採集點後傳送花費更便宜的以太之光。"
                  + " 只有在物品沒有時間限制並且有多個可採集點時有效。");

            if (oldPref != GatherBuddy.Config.AetherytePreference)
            {
                GatherBuddy.UptimeManager.ResetLocations();
                GatherBuddy.Config.Save();
            }
        }

        public static void DrawAlarmFormatInput()
            => DrawFormatInput("鬧鐘的訊息格式",
                "保持為空以不輸出任何訊息。\n"
              + "可替換：\n"
              + "- {Alarm} 帶括號的鬧鐘名稱。\n"
              + "- {Item} 物品連結。\n"
              + "- {Offset} 以秒為單位的鬧鐘偏移量。\n"
              + "- {DurationString} “還需要等待...”或“還能夠維持...”。\n"
              + "- {Location} 地圖旗幟連結與位置名稱。",
                GatherBuddy.Config.AlarmFormat, Configuration.DefaultAlarmFormat, s => GatherBuddy.Config.AlarmFormat = s);

        public static void DrawIdentifiedGatherableFormatInput()
            => DrawFormatInput("被識別的可採集物的訊息格式",
                "保持為空以不輸出任何訊息。\n"
              + "可替換：\n"
              + "- {Input} 輸入的搜尋文字。\n"
              + "- {Item} 物品連結。",
                GatherBuddy.Config.IdentifiedGatherableFormat, Configuration.DefaultIdentifiedGatherableFormat,
                s => GatherBuddy.Config.IdentifiedGatherableFormat = s);

        public static void DrawAlwaysMapsBox()
            => DrawCheckbox("有地圖時優先採集",      "採集點內有地圖時，GBR 會優先採集地圖",
                GatherBuddy.Config.AutoGatherConfig.AlwaysGatherMaps, b => GatherBuddy.Config.AutoGatherConfig.AlwaysGatherMaps = b);

        public static void DrawUseExistingAutoHookPresetsBox()
        {
            DrawCheckbox("使用現有 AutoHook 預設",
                "使用你自己的 AutoHook 預設，而非 GBR 自動產生的預設。\n"
              + "Name your preset using the fish's Item ID (e.g., '46188' for Goldentail).\n"
              + "將游標移至「釣魚／刺魚」分頁中的魚即可查看物品 ID。\n"
              + "你的預設不會被刪除，系統只會清理 GBR 產生的預設。",
                GatherBuddy.Config.AutoGatherConfig.UseExistingAutoHookPresets,
                b => GatherBuddy.Config.AutoGatherConfig.UseExistingAutoHookPresets = b);
            ImGui.SameLine();
            ImGuiEx.PluginAvailabilityIndicator([new("AutoHook")]);
        }

        public static void DrawSurfaceSlapConfig()
        {
            DrawCheckbox("自動使用「拍擊水面」",
                "對與目標魚具有相同咬鉤類型的非目標魚，自動啟用「拍擊水面」。\n"
              + "這能排除不需要的魚，提高目標魚的捕獲率。",
                GatherBuddy.Config.AutoGatherConfig.EnableSurfaceSlap,
                b => GatherBuddy.Config.AutoGatherConfig.EnableSurfaceSlap = b);
            
            if (GatherBuddy.Config.AutoGatherConfig.EnableSurfaceSlap)
            {
                ImGui.Indent();
                
                var gpAbove = GatherBuddy.Config.AutoGatherConfig.SurfaceSlapGPAbove;
                if (ImGui.RadioButton("GP 高於門檻時使用「拍擊水面」", gpAbove))
                {
                    GatherBuddy.Config.AutoGatherConfig.SurfaceSlapGPAbove = true;
                    GatherBuddy.Config.Save();
                }
                
                ImGui.SameLine();
                if (ImGui.RadioButton("低於", !gpAbove))
                {
                    GatherBuddy.Config.AutoGatherConfig.SurfaceSlapGPAbove = false;
                    GatherBuddy.Config.Save();
                }
                
                var gpThreshold = GatherBuddy.Config.AutoGatherConfig.SurfaceSlapGPThreshold;
                ImGui.SetNextItemWidth(SetInputWidth);
                if (ImGui.DragInt("GP 門檻", ref gpThreshold, 1, 0, 10000))
                {
                    GatherBuddy.Config.AutoGatherConfig.SurfaceSlapGPThreshold = Math.Max(0, gpThreshold);
                    GatherBuddy.Config.Save();
                }
                ImGuiUtil.HoverTooltip("當 GP 高於／低於此門檻時使用「拍擊水面」。");
                
                ImGui.Unindent();
            }
        }

        public static void DrawIdenticalCastConfig()
        {
            DrawCheckbox("自動使用「專一垂釣」",
                "自動對目標魚啟用「專一垂釣」以提高捕獲率。\n"
              + "在同一釣場使用「專一垂釣」可提高捕獲率。",
                GatherBuddy.Config.AutoGatherConfig.EnableIdenticalCast,
                b => GatherBuddy.Config.AutoGatherConfig.EnableIdenticalCast = b);
            
            if (GatherBuddy.Config.AutoGatherConfig.EnableIdenticalCast)
            {
                ImGui.Indent();
                
                var gpAbove = GatherBuddy.Config.AutoGatherConfig.IdenticalCastGPAbove;
                if (ImGui.RadioButton("GP 高於門檻時使用「專一垂釣」", gpAbove))
                {
                    GatherBuddy.Config.AutoGatherConfig.IdenticalCastGPAbove = true;
                    GatherBuddy.Config.Save();
                }
                
                ImGui.SameLine();
                if (ImGui.RadioButton("低於##IdenticalCast", !gpAbove))
                {
                    GatherBuddy.Config.AutoGatherConfig.IdenticalCastGPAbove = false;
                    GatherBuddy.Config.Save();
                }
                
                var gpThreshold = GatherBuddy.Config.AutoGatherConfig.IdenticalCastGPThreshold;
                ImGui.SetNextItemWidth(SetInputWidth);
                if (ImGui.DragInt("GP 門檻##IdenticalCast", ref gpThreshold, 1, 0, 10000))
                {
                    GatherBuddy.Config.AutoGatherConfig.IdenticalCastGPThreshold = Math.Max(0, gpThreshold);
                    GatherBuddy.Config.Save();
                }
                ImGuiUtil.HoverTooltip("當 GP 高於／低於此門檻時使用「專一垂釣」。");
                
                ImGui.Unindent();
            }
        }

        public static void DrawAutoCollectablesFishingBox()
            => DrawCheckbox("自動收藏品處理",
                "依最低收藏價值自動接受或拒絕收藏品魚類。",
                GatherBuddy.Config.AutoGatherConfig.AutoCollectablesFishing,
                b => GatherBuddy.Config.AutoGatherConfig.AutoCollectablesFishing = b);
        
        public static void DrawDiademAutoAetherCannonBox()
            => DrawCheckbox("雲冠群島自動以太炮",
                "量譜達到可用條件（≥200）時，自動瞄準附近敵人並發射以太炮。\n"
              + "僅在未尋路／導航時發射，每次使用間隔 2 秒。",
                GatherBuddy.Config.AutoGatherConfig.DiademAutoAetherCannon,
                b => GatherBuddy.Config.AutoGatherConfig.DiademAutoAetherCannon = b);
        
        public static void DrawManualPresetGenerator()
        {
            ImGui.Separator();
            ImGui.TextUnformatted("手動預設產生器");
            ImGui.Spacing();
            
            var availableFish = GatherBuddy.GameData.Fishes.Values.Where(f => !f.IsSpearFish).ToList();
            
            ImGui.TextUnformatted("選擇目標魚：");
            ImGui.SetNextItemWidth(SetInputWidth);
            
            if (ImGui.BeginCombo("###FishSelector", _selectedFish?.Name[GatherBuddy.Language] ?? "無"))
            {
                ImGui.SetNextItemWidth(SetInputWidth - 20);
                ImGui.InputTextWithHint("###FishFilter", "搜尋…", ref _fishFilterText, 100);
                ImGui.Separator();
                
                using (var child = ImRaii.Child("###FishList", new Vector2(0, 200 * ImGuiHelpers.GlobalScale), false))
                {
                    for (int i = 0; i < availableFish.Count; i++)
                    {
                        var fish = availableFish[i];
                        var fishName = fish.Name[GatherBuddy.Language];
                        
                        if (_fishFilterText.Length > 0 && !fishName.ToLower().Contains(_fishFilterText.ToLower()))
                            continue;
                        
                        using var id = ImRaii.PushId($"{fish.ItemId}###{i}");
                        if (ImGui.Selectable(fishName, _selectedFish?.ItemId == fish.ItemId))
                        {
                            _selectedFish = fish;
                            _presetName = fish.ItemId.ToString();
                            _fishFilterText = "";
                            ImGui.CloseCurrentPopup();
                        }
                    }
                }
                
                ImGui.EndCombo();
            }
            
            if (_selectedFish != null)
            {
                ImGui.Spacing();
                ImGui.TextUnformatted("預設名稱：");
                ImGui.SetNextItemWidth(SetInputWidth);
                ImGui.InputText("###PresetNameInput", ref _presetName, 64);
                ImGuiUtil.HoverTooltip("預設名稱必須與魚類物品 ID 相同，GBR 才能自動使用。");
                
                ImGui.Spacing();
                if (ImGui.Button("產生預設"))
                {
                    GenerateManualPreset(_selectedFish, _presetName);
                }
            }
        }
        
        private static void GenerateManualPreset(Fish fish, string presetName)
        {
            if (string.IsNullOrWhiteSpace(presetName))
                presetName = fish.ItemId.ToString();
            
            var success = AutoHookIntegration.AutoHookService.ExportPresetToAutoHook(presetName, [fish]);
            
            if (success)
            {
                Svc.Chat.Print($"[GatherBuddy] Generated preset '{presetName}' for {fish.Name[GatherBuddy.Language]}");
            }
            else
            {
                Svc.Chat.PrintError($"[GatherBuddy] Failed to generate preset for {fish.Name[GatherBuddy.Language]}");
            }
        }
    }


    private void DrawConfigTab()
    {
        using var id  = ImRaii.PushId("設定");
        using var tab = ImRaii.TabItem("設定");

        if (!tab)
            return;

        using var child = ImRaii.Child("設定標籤頁");
        if (!child)
            return;

        if (ImGui.CollapsingHeader("自動採集設定"))
        {
            if (ImGui.TreeNodeEx("通用##autoGeneral"))
            {
                AutoGatherUI.DrawMountSelector();
                ConfigFunctions.DrawMountUpDistance();
                ConfigFunctions.DrawMoveWhileMounting();
                ConfigFunctions.DrawHonkModeBox();
                if (GatherBuddy.Config.AutoGatherConfig.HonkMode)
                {
                    ConfigFunctions.DrawHonkVolumeSlider();
                }
                ConfigFunctions.DrawCheckRetainersBox();
                ConfigFunctions.DrawGoHomeBox();
                ConfigFunctions.DrawUseGivingLandOnCooldown();
                ConfigFunctions.DrawUseSkillsForFallabckBox();
                ConfigFunctions.DrawAbandonNodesBox();
                ConfigFunctions.DrawAlwaysMapsBox();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("捕魚"))
            {
                ConfigFunctions.DrawUseExistingAutoHookPresetsBox();
                ConfigFunctions.DrawFishingSpotMinutes();
                ConfigFunctions.DrawFishCollectionBox();
                ConfigFunctions.DrawAutoCollectablesFishingBox();
                ConfigFunctions.DrawSurfaceSlapConfig();
                ConfigFunctions.DrawIdenticalCastConfig();
                ConfigFunctions.DrawManualPresetGenerator();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("高階"))
            {
                ConfigFunctions.DrawRepairBox();
                if (GatherBuddy.Config.AutoGatherConfig.DoRepair)
                {
                    ConfigFunctions.DrawRepairThreshold();
                }
                ConfigFunctions.DrawMaterialExtraction();
                ConfigFunctions.DrawAetherialReduction();
                ConfigFunctions.DrawAutoretainerBox();
                if (GatherBuddy.Config.AutoGatherConfig.AutoRetainerMultiMode)
                {
                    ConfigFunctions.DrawAutoretainerThreshold();
                    ConfigFunctions.DrawAutoretainerTimedNodeDelayBox();
                }
                ConfigFunctions.DrawDiademAutoAetherCannonBox();
                ConfigFunctions.DrawSortingMethodCombo();
                ConfigFunctions.DrawLifestreamCommandTextInput();
                ConfigFunctions.DrawAntiStuckCooldown();
                ConfigFunctions.DrawStuckThreshold();
                ConfigFunctions.DrawAntiStuckSettings();
                ConfigFunctions.DrawScheduledCommandSettings();
                ConfigFunctions.DrawTimedNodePrecog();
                ConfigFunctions.DrawExecutionDelay();
                ConfigFunctions.DrawAutoGatherBox();
                ConfigFunctions.DrawUseFlagBox();
                ConfigFunctions.DrawUseNavigationBox();
                ConfigFunctions.DrawForceWalkingBox();
                ImGui.TreePop();
            }
        }

        if (ImGui.CollapsingHeader("通用設定"))
        {
            if (ImGui.TreeNodeEx("採集命令"))
            {
                ConfigFunctions.DrawPreferredJobSelect();
                ConfigFunctions.DrawGearChangeBox();
                ConfigFunctions.DrawTeleportBox();
                ConfigFunctions.DrawMapOpenBox();
                ConfigFunctions.DrawPlaceMarkerBox();
                ConfigFunctions.DrawPlaceWaymarkBox();
                ConfigFunctions.DrawAetherytePreference();
                ConfigFunctions.DrawSkipTeleportBox();
                ConfigFunctions.DrawContextMenuBox();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("套裝名"))
            {
                ConfigFunctions.DrawSetInput("採礦工",    GatherBuddy.Config.MinerSetName,    s => GatherBuddy.Config.MinerSetName    = s);
                ConfigFunctions.DrawSetInput("園藝工", GatherBuddy.Config.BotanistSetName, s => GatherBuddy.Config.BotanistSetName = s);
                ConfigFunctions.DrawSetInput("捕魚人",   GatherBuddy.Config.FisherSetName,   s => GatherBuddy.Config.FisherSetName   = s);
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("鬧鐘"))
            {
                ConfigFunctions.DrawAlarmToggle();
                ConfigFunctions.DrawAlarmsInDutyToggle();
                ConfigFunctions.DrawAlarmsOnlyWhenLoggedInToggle();
                ConfigFunctions.DrawWeatherAlarmPicker();
                ConfigFunctions.DrawHourAlarmPicker();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("訊息"))
            {
                ConfigFunctions.DrawPrintTypeSelector();
                ConfigFunctions.DrawErrorTypeSelector();
                ConfigFunctions.DrawMapMarkerPrintBox();
                ConfigFunctions.DrawPrintUptimesBox();
                ConfigFunctions.DrawPrintClipboardBox();
                ConfigFunctions.DrawAlarmFormatInput();
                ConfigFunctions.DrawIdentifiedGatherableFormatInput();
                ImGui.TreePop();
            }

            ImGui.NewLine();
        }

        if (ImGui.CollapsingHeader("介面設定"))
        {
            if (ImGui.TreeNodeEx("設定視窗"))
            {
                ConfigFunctions._base = this;
                ConfigFunctions.DrawOpenOnStartBox();
                ConfigFunctions.DrawRespectEscapeBox();
                ConfigFunctions.DrawLockPositionBox();
                ConfigFunctions.DrawLockResizeBox();
                ConfigFunctions.DrawWeatherTabNamesBox();
                ConfigFunctions.DrawShowStatusLineBox();
                ConfigFunctions.DrawHideClippyBox();
                ConfigFunctions.DrawMainInterfaceHotkeyInput();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("釣魚視窗"))
            {
                ConfigFunctions.DrawKeepRecordsBox();
                ConfigFunctions.DrawShowLocalTimeInRecordsBox();
                ConfigFunctions.DrawFishTimerBox();
                ConfigFunctions.DrawFishTimerEditBox();
                ConfigFunctions.DrawFishTimerClickthroughBox();
                ConfigFunctions.DrawFishTimerHideBox();
                ConfigFunctions.DrawFishTimerHideBox2();
                ConfigFunctions.DrawFishTimerUptimesBox();
                ConfigFunctions.DrawFishTimerScale();
                ConfigFunctions.DrawFishTimerIntervals();
                ConfigFunctions.DrawFishTimerIntervalsRounding();
                ConfigFunctions.DrawHideFishPopupBox();
                ConfigFunctions.DrawCollectableHintPopupBox();
                ConfigFunctions.DrawDoubleHookHintPopupBox();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("釣魚資料 [測試]"))
            {
                ConfigFunctions.DrawEnableFishStats();
                ConfigFunctions.DrawEnableReportTime();
                ConfigFunctions.DrawEnableReportSize();
                ConfigFunctions.DrawEnableReportMulti();
                ConfigFunctions.DrawEnableGraphs();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("採集視窗"))
            {
                ConfigFunctions.DrawShowGatherWindowBox();
                ConfigFunctions.DrawGatherWindowAnchorBox();
                ConfigFunctions.DrawGatherWindowTimersBox();
                ConfigFunctions.DrawGatherWindowAlarmsBox();
                ConfigFunctions.DrawSortGatherWindowBox();
                ConfigFunctions.DrawGatherWindowShowOnlyAvailableBox();
                ConfigFunctions.DrawHideGatherWindowCompletedItemsBox();
                ConfigFunctions.DrawHideGatherWindowInDutyBox();
                ConfigFunctions.DrawGatherWindowHoldKey();
                ConfigFunctions.DrawGatherWindowLockBox();
                ConfigFunctions.DrawGatherWindowHotkeyInput();
                ConfigFunctions.DrawGatherWindowDeleteModifierInput();
                ImGui.TreePop();
            }

            if (ImGui.TreeNodeEx("刺魚助手"))
            {
                ConfigFunctions.DrawSpearfishHelperBox();
                ConfigFunctions.DrawSpearfishNamesBox();
                ConfigFunctions.DrawSpearfishSpeedBox();
                ConfigFunctions.DrawAvailableSpearfishBox();
                ConfigFunctions.DrawSpearfishIconsAsTextBox();
                ConfigFunctions.DrawSpearfishCenterLineBox();
                ConfigFunctions.DrawSpearfishFishNameFixed();
                ConfigFunctions.DrawSpearfishFishNamePercentage();
                ImGui.TreePop();
            }

            ImGui.NewLine();
        }

        if (ImGui.CollapsingHeader("顏色"))
        {
            foreach (var color in Enum.GetValues<ColorId>())
            {
                var (defaultColor, name, description) = color.Data();
                var currentColor = GatherBuddy.Config.Colors.TryGetValue(color, out var current) ? current : defaultColor;
                if (Widget.ColorPicker(name, description, currentColor, c => GatherBuddy.Config.Colors[color] = c, defaultColor))
                    GatherBuddy.Config.Save();
            }

            ImGui.NewLine();

            if (Widget.PaletteColorPicker("聊天欄中的名字", Vector2.One * ImGui.GetFrameHeight(), GatherBuddy.Config.SeColorNames,
                    Configuration.DefaultSeColorNames, Configuration.ForegroundColors, out var idx))
                GatherBuddy.Config.SeColorNames = idx;
            if (Widget.PaletteColorPicker("聊天欄中的命令", Vector2.One * ImGui.GetFrameHeight(), GatherBuddy.Config.SeColorCommands,
                    Configuration.DefaultSeColorCommands, Configuration.ForegroundColors, out idx))
                GatherBuddy.Config.SeColorCommands = idx;
            if (Widget.PaletteColorPicker("聊天欄中的引數", Vector2.One * ImGui.GetFrameHeight(), GatherBuddy.Config.SeColorArguments,
                    Configuration.DefaultSeColorArguments, Configuration.ForegroundColors, out idx))
                GatherBuddy.Config.SeColorArguments = idx;
            if (Widget.PaletteColorPicker("聊天欄中的鬧鐘訊息", Vector2.One * ImGui.GetFrameHeight(), GatherBuddy.Config.SeColorAlarm,
                    Configuration.DefaultSeColorAlarm, Configuration.ForegroundColors, out idx))
                GatherBuddy.Config.SeColorAlarm = idx;

            ImGui.NewLine();
        }
    }
}
