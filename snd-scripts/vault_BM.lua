--[[
========================================================================
  聖教中樞伊修加爾德教皇廳 (The Vault, Lv.57) — 青魔自動刷本
========================================================================
  需要外掛:
    - SomethingNeedDoing  (v2, Lua 模式)
    - vnavmesh            (自動尋路移動)

  使用說明:
    1) 使用強力守護
    2) 乙太複製DD職能
    3) 在任務搜尋器選好「聖教中樞伊修加爾德教皇廳」、勾選「解除限制」與「等級同步」
    4) 設定循環次數，修改 LOOP_COUNT = (你想要打幾次副本)
    5) 啟動腳本

  必備技能:
    No.12 怒髮衝冠
    No.13 白風
    No.21 自爆
    No.30 強力守護
    No.33 冰凍咆哮
    No.47 轟雷
    No.63 音爆
    No.77 乙太複製
    No.80 正義飛踢
    No.91 鬥爭本能
    No.92 超振動
    No.102 如意大旋風
    No.122 咕嚕咕嚕
    No.124 終有一死
========================================================================
]]


----------------------------------------------------------------
-- 使用者設定
----------------------------------------------------------------
local LOOP_COUNT      = 50    -- 要刷幾次
local ENTRY_WAIT_SEC  = 300   -- 等媒合 + 進場的最長秒數
local MOVE_TIMEOUT    = 60    -- 單段路徑最長等待秒數 (有 IPC 時)
local MOVE_FALLBACK   = 12    -- 無 IPC 時每段移動的固定等待秒數
local REPAIR_THRESHOLD = 20   -- 任一裝備耐久低於此 % 就「修理全部」(自助修理,需身上有暗物質)

-- Buff StatusId
local STATUS_MOON_FLUTE = 2498   -- 鬥爭本能 buff


----------------------------------------------------------------
-- 基礎工具
----------------------------------------------------------------
local function wait(s)
    yield("/wait " .. tostring(s))
end

local function echo(msg)
    yield("/e [VaultBM] " .. tostring(msg))
end

-- 輪詢條件直到成立或超時,回傳是否在期限內成立
local function waitUntil(condFn, timeout, poll)
    timeout = timeout or 5
    poll    = poll    or 0.1
    local elapsed = 0
    while elapsed < timeout do
        local ok, v = pcall(condFn)
        if ok and v then return true end
        wait(poll)
        elapsed = elapsed + poll
    end
    return false
end


----------------------------------------------------------------
-- 跨版本 Condition 檢查
--   自動偵測下列 API 哪個可用:
--     1. Svc.Condition[flag]            — SND v2 / 新版
--     2. GetCharacterCondition(flag)    — SND 舊版
----------------------------------------------------------------
local _condImpl = nil
local _condWarned = false

local function _detectCondApi()
    -- 嘗試 Svc.Condition
    local ok, _ = pcall(function() return Svc.Condition[34] end)
    if ok then
        return function(flag)
            local ok2, v = pcall(function() return Svc.Condition[flag] end)
            return ok2 and v or false
        end
    end
    -- 嘗試 GetCharacterCondition
    if type(GetCharacterCondition) == "function" then
        return function(flag)
            local ok2, v = pcall(GetCharacterCondition, flag)
            return ok2 and v or false
        end
    end
    return nil
end

local function _cond(flag)
    if _condImpl == nil then
        _condImpl = _detectCondApi()
        if _condImpl == nil and not _condWarned then
            _condWarned = true
            yield("/e [VaultBM] 警告:找不到 Condition API,將用固定時間等待")
        end
    end
    if _condImpl then return _condImpl(flag) end
    return false
end

local function isInDuty()    return _cond(34) or _cond(56) or _cond(95) end
local function inCombat()    return _cond(26) end
local function isLoading()   return _cond(45) or _cond(51) end
local function isOccupied()
    -- 不能動作:詠唱中 / cutscene / 過場
    return _cond(27) or _cond(32) or _cond(33)
end

-- 角色是否已就緒可施放技能
--   進副本後 Entity.Player 會有一段時間 nil 或 HP=0,等到 HP > 0 才算真正可操作
local function playerReady()
    if isLoading() then return false end
    if isOccupied() then return false end
    local ok, ready = pcall(function()
        local p = Entity.Player
        if p == nil then return false end
        return (p.CurrentHp or 0) > 0
    end)
    return ok and ready
end

-- 玩家是否有指定狀態 (statusId)
local function hasStatus(statusId)
    local ok, found = pcall(function()
        local sl = Svc.ClientState.LocalPlayer.StatusList
        for i = 0, (sl.Length or 30) - 1 do
            local s = sl[i]
            if s ~= nil and s.StatusId == statusId then return true end
        end
        return false
    end)
    return ok and found
end

-- 是否正在詠唱
local function isCasting() return _cond(27) end

-- 反覆施放技能,直到 buff 上身或超過次數
--   流程:下指令 → 等詠唱結束 (最多 castTime 秒) → 看 buff 有沒有上 → 沒上重試
local function castUntilStatus(action, statusId, maxTries, castTime, gap)
    maxTries = maxTries or 6
    castTime = castTime or 4    -- 詠唱最長等多久 (含緩衝)
    gap      = gap      or 0.5  -- 重試前的間隔
    if hasStatus(statusId) then return true end
    for i = 1, maxTries do
        yield(action)
        wait(0.3)  -- 給遊戲時間開始詠唱
        -- 等詠唱完成 (cast flag 變 false) 或 buff 已上 (即放即得 buff 提前退出)
        waitUntil(function() return not isCasting() or hasStatus(statusId) end,
                  castTime, 0.1)
        wait(0.2)  -- 等 buff 結算上身
        if hasStatus(statusId) then return true end
        wait(gap)  -- 重試前喘口氣
    end
    return false
end


----------------------------------------------------------------
-- 移動 (vnavmesh)
--   使用 /vnav 文字指令,以 pcall 嘗試 IPC 偵測抵達;
--   若 IPC 不可用則退回固定時間等待。
----------------------------------------------------------------
-- 啟動移動,不等抵達
local function startMoveTo(x, y, z)
    yield(string.format("/vnav moveto %.2f %.2f %.2f", x, y, z))
end

-- 等 vnav 抵達 (有 IPC 用 IPC,否則 fixed timeout)
local function waitArrival()
    local elapsed = 0
    local ipcOk   = true
    while elapsed < MOVE_TIMEOUT do
        local ok, running = pcall(function()
            return IPC.vnavmesh.IsRunning() or IPC.vnavmesh.PathfindInProgress()
        end)
        if not ok then ipcOk = false; break end
        if not running then return end
        wait(0.2)
        elapsed = elapsed + 0.2
    end
    if not ipcOk then
        wait(MOVE_FALLBACK)
    else
        yield("/vnav stop")  -- 超時強制停止
    end
end

-- 阻塞式移動到指定點
local function moveTo(x, y, z)
    startMoveTo(x, y, z)
    wait(0.3)
    waitArrival()
end

local function stopMove()
    yield("/vnav stop")
end

-- 移動到指定點,途中暫停 delay 秒做一次檢查;
--   checkFn() 回傳 true → 執行 onTrigger() 然後重新前往原目標,並回傳 true
--   checkFn() 回傳 false → 等抵達後回傳 false
local function moveToWithCheck(x, y, z, delay, checkFn, onTrigger)
    startMoveTo(x, y, z)
    wait(delay or 2)
    if checkFn and checkFn() then
        stopMove()
        if onTrigger then onTrigger() end
        moveTo(x, y, z)
        return true
    end
    waitArrival()
    return false
end


----------------------------------------------------------------
-- UI Callback / Addon 偵測
----------------------------------------------------------------

-- 統一呼叫 addon callback;優先用 Lua Callback(),失敗退回 /callback
local function clickCallback(addon, ...)
    local args = {...}
    local ok = pcall(function() Callback(addon, true, table.unpack(args)) end)
    if not ok then
        local parts = { "/callback", addon, "true" }
        for _, a in ipairs(args) do parts[#parts+1] = tostring(a) end
        yield(table.concat(parts, " "))
    end
end

-- 指定 addon 是否可見/ready
local function isAddonVisible(name)
    local ok, v = pcall(function()
        local a = Addons.GetAddon(name)
        return a and a.Ready
    end)
    return ok and v == true
end


----------------------------------------------------------------
-- 戰鬥輔助
----------------------------------------------------------------

-- 是否有活著的目標 (HP > 0)
local function hasLiveTarget()
    local ok, alive = pcall(function()
        local t = Entity.Target
        if t == nil then return false end
        return (t.CurrentHp or 0) > 0
    end)
    return ok and alive
end

-- 補刀:有目標還活著就用 音爆 持續打,最多 N 輪
--   音爆有 1 秒詠唱 + GCD ≈ 2.5 秒,所以每次要等詠唱結束 + GCD 才能再放
local function killLeftover(rounds)
    rounds = rounds or 5
    for i = 1, rounds do
        yield("/nexttarget")
        wait(0.2)
        if not hasLiveTarget() then return end

        yield("/blueaction 音爆")
        wait(0.2)
        -- 等詠唱結束 (最多 2 秒含緩衝)
        waitUntil(function() return not isCasting() end, 2, 0.1)
        -- 等目標死亡 OR GCD 結束 (1.5s ≈ GCD 剩餘),哪個先到都行
        waitUntil(function() return not hasLiveTarget() end, 1.5, 0.1)
    end
end


----------------------------------------------------------------
-- 裝備耐久度 / 自助修理
--   偵測順序:
--     1. NeedsRepair(threshold)   — SND 內建,任一裝備耐久 ≤ threshold% 回傳 true
--     2. Inventory 逐件掃描         — 退而求其次,自行算最低耐久 %
--   修理走「修理」一般技能 → Repair addon「全部修理」→ SelectYesno 確認。
--   ※ 自助修理需身上帶足夠等級的暗物質;若想找 NPC 修請改寫此處。
----------------------------------------------------------------

-- 任一裝備耐久是否低於 threshold%(true = 需要修理)
local function gearBelow(threshold)
    -- 1) 優先用 SND 內建 NeedsRepair
    if type(NeedsRepair) == "function" then
        local ok, v = pcall(NeedsRepair, threshold)
        if ok then return v == true end
    end
    -- 2) 退回逐件掃描裝備欄 (EquippedItems / GearSet),抓最低耐久 %
    local ok, low = pcall(function()
        local inv = Inventory.GetInventoryContainer(InventoryType.EquippedItems)
        if inv == nil then return nil end
        local minPct = 100
        for i = 0, (inv.Count or inv.Size or 13) - 1 do
            local item = inv[i]
            if item ~= nil and (item.ItemId or 0) > 0 then
                -- Condition 為 0~30000,對應 0~100%
                local cond = item.Condition or item.Durability
                if cond ~= nil then
                    local pct = (cond / 30000) * 100
                    if pct < minPct then minPct = pct end
                end
            end
        end
        return minPct
    end)
    if ok and low ~= nil then return low < threshold end
    return false  -- 兩種 API 都不可用 → 視為不需修理,避免誤觸
end

-- 開修理視窗、按「全部修理」、確認
local function repairAll()
    echo("裝備耐久偏低,開始修理全部")
    yield('/generalaction "修理"')           -- 開 Repair addon (TW 客戶端為「修理」)
    if not waitUntil(function() return isAddonVisible("Repair") end, 5, 0.2) then
        echo("修理視窗沒開起來(可能沒有暗物質),略過")
        return false
    end

    -- 按「全部修理」
    clickCallback("Repair", 0)
    -- 跳出確認框就按「是」
    if waitUntil(function() return isAddonVisible("SelectYesno") end, 3, 0.1) then
        clickCallback("SelectYesno", 0)
    end

    -- 等修理完成(全部修好後 NeedsRepair 會變 false),最多等 10 秒
    waitUntil(function() return not gearBelow(99) end, 10, 0.3)

    -- 關閉修理視窗
    if isAddonVisible("Repair") then clickCallback("Repair", -1) end
    waitUntil(function() return not isAddonVisible("Repair") end, 3, 0.1)
    echo("修理完成")
    return true
end

-- 每輪開頭呼叫:低於門檻才修
local function checkAndRepair()
    if gearBelow(REPAIR_THRESHOLD) then
        repairAll()
    end
end


----------------------------------------------------------------
-- 進入副本:開搜尋器 → 按參加 → 等媒合 → 按 Commence → 等載入
----------------------------------------------------------------
local function enterDuty()
    -- 開 ContentsFinder
    yield("/dutyfinder")
    waitUntil(function() return isAddonVisible("ContentsFinder") end, 3, 0.1)

    -- 按「參加副本」按鈕
    -- 若按了沒反應,把 (12,0) 改成 (12,1) / (11,0) / (14,0) 試試
    clickCallback("ContentsFinder", 12, 0)

    -- 等 ContentsFinderConfirm 媒合彈窗 (solo 解限通常秒進)
    if not waitUntil(function() return isAddonVisible("ContentsFinderConfirm") end,
                     ENTRY_WAIT_SEC, 0.3) then
        echo("媒合超時,中止")
        return false
    end

    -- 按 Commence,直到彈窗消失或已進入副本
    local elapsed = 0
    while isAddonVisible("ContentsFinderConfirm") and not isInDuty() and elapsed < 30 do
        clickCallback("ContentsFinderConfirm", 8, 0)
        wait(0.5)
        elapsed = elapsed + 0.5
    end

    -- 等真的進入副本
    if not waitUntil(function() return isInDuty() end, 30, 0.3) then
        echo("進場超時,中止")
        return false
    end

    -- 等 loading + 角色資料就緒(HP > 0、可操作)
    waitUntil(function() return not isLoading() end, 60, 0.3)
    waitUntil(playerReady, 15, 0.2)
    wait(0.3)  -- 微 buffer
    return true
end


----------------------------------------------------------------
-- 戰鬥流程
----------------------------------------------------------------
local function runRotation()
    -- 開場:鬥爭本能 (2.5 秒詠唱;重試直到 buff 上身)
    if not castUntilStatus("/blueaction 鬥爭本能", STATUS_MOON_FLUTE, 6, 4, 0.5) then
        echo("鬥爭本能上不去,中止本輪")
        return
    end

    -- 第一個定位點
    moveTo(-0.20, -299.98, 51.43)
    yield("/nexttarget")
    yield("/blueaction 正義飛踢")
    wait(2)
    yield("/previoustarget")
    yield("/blueaction 咕嚕咕嚕")
    wait(1)
    yield("/blueaction 終有一死")
    wait(1)
    yield("/blueaction 轟雷")
    wait(2)
    yield("/blueaction 如意大旋風")

    -- 邊走邊判斷殘血:先朝第二點走,2 秒後檢查
    -- DoT (終有一死) 通常會在這 2 秒內解決最後一隻;若還有就停下補刀
    moveToWithCheck(48.17, -299.98, -10.51, 2,
        function()
            yield("/nexttarget")
            wait(0.2)
            return hasLiveTarget()
        end,
        function()
            echo("殘血補刀")
            killLeftover(5)
        end)
    yield("/blueaction 冰凍咆哮")
    wait(3)
    yield("/blueaction 超振動")
    wait(3)
    yield("/blueaction 白風")
    wait(2)
    yield("/ac 衝刺")

    -- 第三個定位點
    moveTo(-15.51, -300.00, -66.54)

    -- 結算:自爆
    yield("/blueaction 怒髮衝冠")
    wait(1)
    yield("/ac 即刻詠唱")
    wait(1)
    yield("/blueaction 自爆")
    yield("/blueaction 自爆")
    yield("/blueaction 自爆")
    yield("/blueaction 自爆")
    yield("/blueaction 自爆")

    wait(5)
end


----------------------------------------------------------------
-- 退出副本
--   FFXIV 沒有 /leaveduty 原生指令,改走 ContentsFinderMenu 的 callback。
--   注意:死亡復活對話框 (SelectYesno) 必須先按 No 關掉,
--         否則後面點「是否退出?」的 SelectYesno callback 會打到舊的復活框。
----------------------------------------------------------------
local function leaveDuty()
    stopMove()

    -- 等死亡對話框冒出
    waitUntil(function() return isAddonVisible("SelectYesno") end, 8, 0.2)

    -- 1) 關掉復活對話框 (按 No,只關視窗不復活)
    clickCallback("SelectYesno", 1)
    waitUntil(function() return not isAddonVisible("SelectYesno") end, 2, 0.1)

    -- 2) 開任務搜尋器
    yield("/dutyfinder")
    waitUntil(function() return isAddonVisible("ContentsFinderMenu") end, 3, 0.1)

    -- 3) 按退出
    clickCallback("ContentsFinderMenu", 0, 1)
    waitUntil(function() return isAddonVisible("SelectYesno") end, 2, 0.1)

    -- 4) 按是
    clickCallback("SelectYesno", 0)
    waitUntil(function() return not isInDuty() end, 15, 0.3)

    -- 5) 等 loading 結束
    waitUntil(function() return not isLoading() end, 30, 0.3)
    wait(0.5)
end


----------------------------------------------------------------
-- 主迴圈
----------------------------------------------------------------
echo("========= 開始執行,共 " .. LOOP_COUNT .. " 次 =========")

for i = 1, LOOP_COUNT do
    echo("===== 第 " .. i .. " / " .. LOOP_COUNT .. " 次 =====")

    -- 每輪開始前檢查裝備耐久,任一低於門檻就修理全部
    checkAndRepair()

    if not enterDuty() then
        echo("報名失敗,中止腳本")
        break
    end

    runRotation()
    leaveDuty()
end

echo("========= 全部完成 =========")
