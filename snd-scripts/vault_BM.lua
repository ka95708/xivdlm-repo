--[[
========================================================================
  聖教中樞伊修加爾德教皇廳 (The Vault, Lv.57) — 青魔自動刷本
========================================================================
  需要外掛:
    - SomethingNeedDoing  (v2, Lua 模式)
    - vnavmesh            (自動尋路移動)

  使用說明:
    1) 設定循環次數，修改 LOOP_COUNT = (你想要打幾次副本)

  腳本流程:
    [開場一次] 施放非凡防禦 → 勾「解除限制」+「等級同步」→ 選定副本 (id = 34)
    [每輪循環] 檢查耐久 → 排隊進場 → 跑循環 → 退本

  必備技能:
    No.12 怒髮衝冠
    No.13 純白微風
    No.21 自爆
    No.30 非凡防禦
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

-- 自動選本
local DUTY_NAME   = "聖教中樞伊修加爾德教皇廳"  -- 要打的副本名 (需與遊戲內完全一致或為其片段)
local DUTY_CFC_ID = 34     -- ContentFinderCondition RowId (教皇廳 = 34);設 nil 則按 DUTY_NAME 查表
local WANT_UNRESTRICTED = true   -- 解除限制
local WANT_LEVEL_SYNC   = true   -- 等級同步
local WANT_MIN_IL       = false  -- 最低裝等
local WANT_SILENCE_ECHO = false  -- 無視增益效果

-- Buff StatusId
local STATUS_MOON_FLUTE   = 2498   -- 鬥爭本能 buff
local STATUS_MIGHTY_GUARD = 1719   -- 非凡防禦 buff (若一直上不去,請確認此 id)


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
--   流程:下指令 → 等詠唱結束 (最多 castTime 秒) → 輪詢 settle 秒等 buff 上身 → 沒上才重試
--
--   ※ settle 很重要:非凡防禦 / 鬥爭本能 這類技能,buff 要一下才會掛上 StatusList。
--     若太早判定失敗而重放,非凡防禦會被切掉(姿態技再按一次 = 取消),
--     結果來回開關永遠上不去。所以一定要輪詢等滿 settle 秒才准重試。
local function castUntilStatus(action, statusId, maxTries, castTime, gap, settle)
    maxTries = maxTries or 6
    castTime = castTime or 4    -- 詠唱最長等多久 (含緩衝)
    gap      = gap      or 0.5  -- 重試前的間隔
    settle   = settle   or 2    -- 施放後至少輪詢這麼久才判定失敗
    if hasStatus(statusId) then return true end
    for i = 1, maxTries do
        yield(action)
        -- 先等詠唱真的開始:指令送到伺服器有延遲,太早檢查會誤判成「沒在詠唱」,
        -- 導致下面的「等詠唱結束」當場跳過。瞬發技沒有詠唱,會在 buff 上身時提前跳出。
        waitUntil(function() return isCasting() or hasStatus(statusId) end, 1.5, 0.1)
        -- 再等詠唱完成 (cast flag 變 false) 或 buff 已上 (即放即得 buff 提前退出)
        waitUntil(function() return not isCasting() or hasStatus(statusId) end,
                  castTime, 0.1)
        -- 輪詢等 buff 結算上身;期間一出現就成功,不會提早重放
        if waitUntil(function() return hasStatus(statusId) end, settle, 0.2) then
            return true
        end
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
-- 自動選本 (ContentsFinder / AgentContentsFinder)
--   1. 用 Excel 的 ContentFinderCondition 表按名字查出 RowId
--   2. Instances.ContentsFinder:OpenRegularDuty(id) 直接把副本掛上搜尋器
--   3. 設定「解除限制 / 等級同步」等旗標
--   任何一步失敗都退回 /dutyfinder,沿用玩家自己在畫面上選好的副本。
----------------------------------------------------------------

-- 取得 DutyFinder 包裝物件 (Instances.DutyFinder → DutyFinderWrapper)
local function cfAgent()
    local ok, a = pcall(function() return Instances.DutyFinder end)
    if ok and a ~= nil then return a end
    return nil
end

-- 掃 ContentFinderCondition 表找出 RowId
local _resolvedDutyId = nil
local function resolveDutyId()
    if DUTY_CFC_ID ~= nil then return DUTY_CFC_ID end
    if _resolvedDutyId ~= nil then return _resolvedDutyId end

    local ok, id = pcall(function()
        local sheet = Excel.GetSheet("ContentFinderCondition")
        if sheet == nil then return nil end
        local count = sheet.Count or sheet.RowCount or 1000
        for i = 0, count - 1 do
            local row = sheet:GetRow(i)
            if row ~= nil then
                local name = tostring(row.Name or "")
                if name ~= "" and name:find(DUTY_NAME, 1, true) then
                    return row.RowId or i
                end
            end
        end
        return nil
    end)

    if ok and id ~= nil then
        _resolvedDutyId = id
        echo("查到副本「" .. DUTY_NAME .. "」 id = " .. tostring(id)
             .. " (可填進 DUTY_CFC_ID 省略查表)")
        return id
    end
    echo("查不到副本「" .. DUTY_NAME .. "」,改用畫面上已選好的副本")
    return nil
end

-- 勾選副本選項 (IsMinIL 注意:說明頁是 IsMinIL,不是 IsMinimalIL)
local function applyDutySettings()
    local a = cfAgent()
    if a == nil then return end
    pcall(function() a.IsUnrestrictedParty = WANT_UNRESTRICTED end)
    pcall(function() a.IsLevelSync         = WANT_LEVEL_SYNC end)
    pcall(function() a.IsMinIL             = WANT_MIN_IL end)
    pcall(function() a.IsSilenceEcho       = WANT_SILENCE_ECHO end)
    pcall(function() a.IsExplorerMode      = false end)
end

-- 是否已在排隊中 (QueueState 不是 None/空閒)
local function isQueued()
    local ok, v = pcall(function()
        local s = tostring(cfAgent().QueueState)
        return s ~= "None" and s ~= "0" and s ~= "nil"
    end)
    return ok and v == true
end

-- 開場只做一次:勾選項 + 選副本
--   順序很重要:必須「先勾解除限制 + 等級同步」才選副本,
--   否則低等單人身分選不進教皇廳。
--   回傳成功與否;失敗則沿用玩家自己在畫面上選好的副本。
local _dutyId = nil
local function prepareDutyFinder()
    local id = resolveDutyId()
    if id == nil then return false end
    local a = cfAgent()
    if a == nil then
        echo("沒有 Instances.DutyFinder,改用畫面上已選好的副本")
        return false
    end

    -- 1) 開搜尋器 (/dutyfinder 是切換式的,已開就別再按)
    if not isAddonVisible("ContentsFinder") then
        yield("/dutyfinder")
    end
    waitUntil(function() return isAddonVisible("ContentsFinder") end, 5, 0.1)
    wait(0.3)

    -- 2) 先勾解除限制 + 等級同步
    applyDutySettings()
    wait(0.3)

    -- 3) 再選副本
    if not pcall(function() a:OpenRegularDuty(id) end) then
        echo("OpenRegularDuty 失敗,改用畫面上已選好的副本")
        return false
    end
    wait(0.3)

    -- 4) 選本動作有可能重置旗標,確認一次;不符就補寫
    local ok, good = pcall(function()
        return a.IsUnrestrictedParty == WANT_UNRESTRICTED
           and a.IsLevelSync == WANT_LEVEL_SYNC
    end)
    if not (ok and good) then
        applyDutySettings()
        wait(0.3)
    end

    _dutyId = id
    echo("已選定副本 id = " .. tostring(id) .. " (解除限制 + 等級同步)")
    return true
end

-- 每輪呼叫:排隊;回傳 "queued" / "selected"(需自己點按鈕)
local function queueCurrentDuty()
    local a = cfAgent()
    if a ~= nil and _dutyId ~= nil then
        if pcall(function() a:QueueDuty(_dutyId) end) then
            return "queued"
        end
        echo("QueueDuty 失敗,退回點按鈕報名")
    end
    return "selected"
end


----------------------------------------------------------------
-- 進入副本:排隊 → 等媒合 → 按 Commence → 等載入
--   副本與選項已在開場的 prepareDutyFinder() 設好,這裡只負責報名
----------------------------------------------------------------
local function enterDuty()
    local mode = queueCurrentDuty()

    -- QueueDuty 已經排進去了就不用再按按鈕
    if mode ~= "queued" then
        if not isAddonVisible("ContentsFinder") then
            yield("/dutyfinder")
        end
        waitUntil(function() return isAddonVisible("ContentsFinder") end, 3, 0.1)
        -- 按「參加副本」按鈕
        -- 若按了沒反應,把 (12,0) 改成 (12,1) / (11,0) / (14,0) 試試
        clickCallback("ContentsFinder", 12, 0)
    end

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
    if not castUntilStatus("/blueaction 鬥爭本能", STATUS_MOON_FLUTE, 4, 4, 1.0, 2.5) then
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
    yield("/blueaction 純白微風")
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
-- 開場準備 (副本外,只做一次)
--   1. 掛上非凡防禦
--   2. 勾解除限制 + 等級同步,並選定副本
----------------------------------------------------------------
local function setupOnce()
    echo("--- 開場準備 ---")

    -- 非凡防禦 (瞬發姿態技;檢查 buff 上身才算成功)
    if hasStatus(STATUS_MIGHTY_GUARD) then
        echo("非凡防禦已在身上")
    -- 姿態技,重放會取消 → 少試幾次、每次等久一點 (settle 2.5s)
    elseif castUntilStatus("/blueaction 非凡防禦", STATUS_MIGHTY_GUARD, 3, 2, 1.0, 2.5) then
        echo("非凡防禦 OK")
    else
        echo("非凡防禦上不去 (確認技能已設定 / STATUS_MIGHTY_GUARD id 是否正確)")
    end

    -- 選項 + 選本
    if not prepareDutyFinder() then
        echo("自動選本失敗,請先自行在任務搜尋器選好副本與選項")
    end
end


----------------------------------------------------------------
-- 主迴圈
----------------------------------------------------------------
setupOnce()

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
