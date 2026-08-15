--[[
========================================================================
  舊薩雷安烹調理符自動化 — 接理符 → 製作交付循環
========================================================================
  需要外掛:
    - SomethingNeedDoing  (Jaksuhn 版 Lua 重寫版，非 daemitus 舊版 C# 版本)
    - YesAlready          (自動確認選單)
    - TextAdvance         (自動推進對話 / 自動放入交付道具)
    - vnavmesh             (自動尋路移動)
    - Lifestream           (乙太之光傳送)

  使用說明:
    1) 確認已建立烹調師的裝備套組（腳本會自動掃描找到，不用填套組編號），
       且烹調師等級達到 Config.REQUIRED_LEVEL（治癒身心的茶配方需求 89 級）
    2) TextAdvance 設定必須勾選：QC(自動完成任務)、TS(自動略過對話)、
       RH(自動確認繳交物品)、RF(自動放入所需物品)
    3) TextAdvance 的 QA(自動接受任務) 必須「取消勾選」！
       實測 QA 開著會跟本腳本自己送出的接受按鍵互搶，導致接不到理符。
       接理符這一段本腳本自己控制，不假手插件。
    4) 執行期間遊戲視窗必須保持在前景（見下方「重要限制」）
    5) 修改 Config.ROUNDS 決定要跑幾輪
    6) YesAlready「清單」(SelectString 條件清單) 必須新增以下兩筆規則，
       否則交付階段 turnIn() 交給插件自動處理時無法穩定選中正確理符：
         - 目標＝格里格　　　文字＝製作任務
         - 目標＝阿爾德伊恩　文字＝製作委託：治癒身心的茶
       第二筆是關鍵：交付清單（SelectIconString）同時列著奶油麵與治癒
       身心的茶，靠這筆規則依「文字精準比對」自動選中目標理符，而不是
       猜清單順序（清單本身是虛擬化渲染，腳本讀不到未反白項目的文字，
       見下方 openLeveWindow 註解）。

  腳本流程:
    [開場一次] 切換烹調師 → 傳送到舊薩雷安 → 走到定點
    [每輪循環] 接理符（雙輪判斷，處理奶油麵擋位）→ 交付（插件全自動代勞）

  重要限制:
    /send（模擬鍵盤）依賴 Windows 視窗焦點，遊戲不在前景時按鍵會被吃掉。
    掛機期間請勿切換視窗、勿複製貼上聊天記錄，保持遊戲視窗在前景。

    ⚠ 接理符階段（/send NUMPAD0）執行期間絕對不能移動滑鼠！
    NUMPAD0 用的是遊戲內建的「手把式游標導覽」：第一下把 UI 游標移到
    「接受」鈕反白，第二下才確認送出。滑鼠一移動，遊戲會把 UI 焦點從
    鍵盤導覽切回滑鼠指向的位置，反白的游標就被打斷或跳走，導致「接受」
    沒有真正被確認、或誤觸到別的東西。實測發生過因為切視窗複製聊天
    記錄、滑鼠移動經過遊戲畫面，導致連續多次按鍵完全不生效。
    （詳細機制見下方 acceptCurrentLeve 函式註解）

  理符「區塊策略」（本腳本能運作的前提）:
    舊薩雷安烹調 88 級理符任務區塊固定只有兩個：
      「製作委託：沒嘗試過的奶油麵」與「製作委託：治癒身心的茶」
    奶油麵所需素材（厄爾庇斯之麵）玩家長期不持有，故永遠無法交付。
    策略是：把奶油麵接起來但「永遠不交」，讓它長期佔用該區塊的一個位置，
    這樣開啟理符視窗時，預設反白選取的若不是目標理符，必定是奶油麵，
    接完它之後該區塊就只剩目標理符，不需要解析「選第幾個」這個
    在此版本 SND 中無法從清單節點讀取文字的難題（見下方 openLeveWindow）。
========================================================================
]]


----------------------------------------------------------------
-- 使用者設定
----------------------------------------------------------------
local Config = {
    -- 地點
    TERRITORY       = 962,               -- 舊薩雷安 TerritoryType
    AETHERYTE       = "舊薩雷安",         -- Lifestream /li 傳送目的地名稱
    SPOT_X          = 50.13,             -- 站定點世界座標（格里格 / 阿爾德伊恩皆在此互動範圍內）
    SPOT_Y          = -15.65,
    SPOT_Z          = 111.94,
    ARRIVE_RADIUS   = 3.0,               -- 距離定點幾碼內算抵達

    -- 職業
    CULINARIAN_JOB  = 15,                -- 烹調師 ClassJob id（台服譯名為「烹調師」，非「廚師」）
    REQUIRED_LEVEL  = 89,                -- 治癒身心的茶（高山茶）配方需求等級，職業等級不足就中止

    -- NPC 與理符
    NPC_LEVE        = "格里格",           -- 拾穗人理符發行人
    NPC_TURNIN      = "阿爾德伊恩",       -- 交付對象
    TARGET_LEVE     = "製作委託：治癒身心的茶",  -- 目標理符名稱（需與遊戲內文字完全一致）

    -- 交付道具
    ITEM_ID         = 36060,             -- 高山茶（英文 Tsai tou Vounou，烹調 89）
    ITEM_HQ_OFFSET  = 1000000,           -- FFXIV 慣例：item id + 1000000 = 該道具 HQ 版本

    -- 執行次數與重試
    ROUNDS              = 50,            -- 要跑幾輪（接理符+交付算一輪）
    MAX_OUTER_RETRIES   = 5,             -- 單輪接理符整體失敗時，關窗重來的最大次數
}

-- HQ 判斷的偏移量另存一份短名，避免下面每次都寫 Config.ITEM_HQ_OFFSET
local HQ_OFFSET = Config.ITEM_HQ_OFFSET


----------------------------------------------------------------
-- 基礎工具
----------------------------------------------------------------
local function echo(msg)
    yield("/e [Leve] " .. tostring(msg))
end

local function wait(seconds)
    yield("/wait " .. tostring(seconds))
end

local function toStr(v)
    return tostring(v)
end

-- Addon 是否已開啟且可操作
--   一律用 pcall 包住：Addon 不存在時 GetAddon 可能回傳 nil，
--   直接存取 .Ready 會噴 Lua 錯誤，用 pcall 轉成安全的 false。
local function isAddonReady(addonName)
    local ok, result = pcall(function()
        local addon = Addons.GetAddon(addonName)
        return addon ~= nil and addon.Ready == true
    end)
    if ok then return result end
    return false
end

-- 讀取 addon 節點文字，並驗證節點型別確實存在
--   這版 SND 的節點路徑是「路徑」不是「連續編號」（GetNode(1) 是根節點，
--   GetNode(1,25,27,2) 是沿著樹狀結構走下去的第 4 層），且路徑寫死後
--   若畫面版面改變會失效，故永遠要用 pcall 包住，讀不到就回 nil
--   而不是讓整個腳本崩潰。
local function getNodeTextSafe(addonName, p1, p2, p3, p4)
    local addon = Addons.GetAddon(addonName)
    if addon == nil then return nil end

    local ok, result = pcall(function()
        if p4 ~= nil then
            return addon:GetNode(p1, p2, p3, p4).Text
        end
        return addon:GetNode(p1, p2, p3).Text
    end)

    if not ok or result == nil then return nil end
    local str = toStr(result)
    if str == "" or str == "nil" then return nil end
    return str
end

-- 把可能殘留的視窗全部關掉
--   每次要送出 /target + /interact 前都呼叫這個，避免上一步留下的
--   視窗（例如接理符後沒關乾淨的 GuildLeve）干擾下一步的互動判斷。
local function closeStrayWindows()
    for _, addonName in ipairs({
        "Talk", "SelectString", "SelectIconString",
        "JournalDetail", "GuildLeve", "Journal",
    }) do
        if isAddonReady(addonName) then
            yield("/callback " .. addonName .. " true -1")
            wait(0.4)
        end
    end
end

-- 反覆點掉 Talk 對話框，直到它消失或超過嘗試上限
--   TextAdvance 的 TalkSkip 平常會自動處理這個，這裡自己做一份是為了
--   在插件反應較慢、或該次對話沒被插件接管時當作保險。
local function advanceTalkDialogue()
    local attempts = 0
    while isAddonReady("Talk") and attempts < 15 do
        yield("/callback Talk true 0")
        wait(0.4)
        attempts = attempts + 1
    end
end

-- /target 之後驗證真的鎖到目標 NPC，鎖錯就重試
--   實測發生過：/target 對錯了目標（仍鎖著前一個 NPC），
--   後面的 /interact 就對錯的 NPC 說話，跳出不相干的選單。
--   驗證 Entity.Target.Name 之後再繼續，比較保險。
local function targetNpcVerified(npcName, maxTries)
    maxTries = maxTries or 3
    for attempt = 1, maxTries do
        yield("/target " .. npcName)
        wait(0.6)

        local ok, actualName = pcall(function() return Entity.Target.Name end)
        if ok and actualName == npcName then
            return true
        end

        echo("目標驗證失敗（第 " .. attempt .. " 次），目前鎖定：" .. toStr(ok and actualName or "無"))
        wait(0.5)
    end
    return false
end


----------------------------------------------------------------
-- 狀態讀取
----------------------------------------------------------------
local function getTerritory()
    local ok, result = pcall(function() return Svc.ClientState.TerritoryType end)
    if ok then return result end
    return nil
end

local function getPlayerDistanceToSpot()
    local ok, pos = pcall(function() return Entity.Player.Position end)
    if not ok or pos == nil then return nil end

    local dx = pos.X - Config.SPOT_X
    local dy = pos.Y - Config.SPOT_Y
    local dz = pos.Z - Config.SPOT_Z
    return math.sqrt(dx * dx + dy * dy + dz * dz)
end

-- 查詢 HQ 道具數量
--   Inventory.GetItemCount 只吃「一個」參數；曾誤傳第二個參數（想指定
--   HQ/NQ）觸發 NLua 攔不住的 .NET 例外，直接打死整個 macro。
--   正確做法是把 item id 加上 1000000 查「那個道具的 HQ 版本」。
local function getHqItemCount(itemId)
    local ok, result = pcall(function()
        return Inventory.GetItemCount(itemId + HQ_OFFSET)
    end)
    if ok then return result end
    return nil
end

local function getCurrentJobId()
    local ok, jobId = pcall(function() return Player.Job.Id end)
    if ok then return jobId end
    return nil
end

-- 讀目前啟用中職業的等級
--   只反映「目前生效的職業」等級，所以要在確定烹調師已切換啟用之後
--   才呼叫，不能在切換前就拿來判斷（那時讀到的會是切換前那個職業的等級）。
local function getCurrentJobLevel()
    local ok, level = pcall(function() return Player.Job.Level end)
    if ok then return level end
    return nil
end

-- 掃描全部裝備套組，找到指定職業的那一組
--   不寫死套組編號：每個玩家的套組排列不同，寫死了別人拿去用就會壞。
--   GetGearset 是 0-based，但遊戲的 /gearset change 指令是 1-based，
--   所以呼叫時要 +1。
local function findGearsetForJob(jobId)
    for i = 0, 99 do
        local ok, gearset = pcall(function() return Player.GetGearset(i) end)
        if ok and gearset ~= nil then
            local ok2, classJob = pcall(function() return gearset.ClassJob end)
            if ok2 and classJob == jobId then
                local ok3, name = pcall(function() return gearset.Name end)
                return i, (ok3 and name or nil)
            end
        end
    end
    return nil, nil
end

-- 讀「理符任務」目前持有數量（GuildLeve 視窗左下角的 X/16）
--   節點路徑靠深度優先掃描實測得出，標籤在 1.25.26，數值在 1.25.27.2。
local function getLeveCount()
    return tonumber(getNodeTextSafe("GuildLeve", 1, 25, 27, 2))
end

-- 讀 JournalDetail 右側面板目前顯示（反白選取）的理符名稱
local function getCurrentLeveName()
    return getNodeTextSafe("JournalDetail", 1, 37, 38)
end


----------------------------------------------------------------
-- 職業切換
----------------------------------------------------------------

-- 確認烹調師等級足夠製作目標配方
--   必須在「烹調師已是目前生效職業」之後才能呼叫，理由見
--   getCurrentJobLevel() 的註解。等級不足直接中止，不讓腳本繼續往下
--   跑到接理符甚至製作那一步才失敗。
local function ensureLevelSufficient()
    local level = getCurrentJobLevel()
    if level == nil then
        echo("!! 讀不到烹調師等級")
        return false
    end

    if level < Config.REQUIRED_LEVEL then
        echo("!! 烹調師等級不足：需要 " .. Config.REQUIRED_LEVEL
          .. " 級，目前 " .. level .. " 級")
        return false
    end

    echo("烹調師等級 " .. level .. "（需求 " .. Config.REQUIRED_LEVEL .. "）")
    return true
end

local function ensureCulinarian()
    if getCurrentJobId() == Config.CULINARIAN_JOB then
        echo("已經是烹調師")
        return ensureLevelSufficient()
    end

    local gearsetIndex, gearsetName = findGearsetForJob(Config.CULINARIAN_JOB)
    if gearsetIndex == nil then
        echo("!! 找不到烹調師套組，請先在遊戲中建立")
        return false
    end

    echo("切換到套組 #" .. (gearsetIndex + 1) .. "（" .. toStr(gearsetName) .. "）")
    yield("/gearset change " .. (gearsetIndex + 1))

    local elapsed = 0
    while elapsed < 10 do
        if getCurrentJobId() == Config.CULINARIAN_JOB then
            return ensureLevelSufficient()
        end
        wait(0.3)
        elapsed = elapsed + 0.3
    end

    echo("!! 切換逾時，目前職業 ID = " .. toStr(getCurrentJobId()))
    return false
end


----------------------------------------------------------------
-- 移動：傳送 + 走到定點
----------------------------------------------------------------
local function ensureAtSpot()
    if getTerritory() ~= Config.TERRITORY then
        echo("不在舊薩雷安（目前地圖 " .. toStr(getTerritory()) .. "），傳送")
        yield("/li " .. Config.AETHERYTE)

        -- 傳送指令送出後先無條件等待，讓場景轉換穩定下來，
        -- 曾在轉場瞬間讀取 Svc.ClientState 觸發過無法定位原因的崩潰。
        wait(3)

        local elapsed = 0
        while elapsed < 120 do
            local ok, territory = pcall(getTerritory)
            if ok and territory == Config.TERRITORY then break end
            wait(1)
            elapsed = elapsed + 1
        end

        if getTerritory() ~= Config.TERRITORY then
            echo("!! 傳送逾時")
            return false
        end
        wait(1)
    else
        echo("已在舊薩雷安")
    end

    local distance = getPlayerDistanceToSpot()
    if distance ~= nil and distance > Config.ARRIVE_RADIUS then
        echo(string.format("距離定點 %.1f 碼，開始移動", distance))
        yield(string.format("/vnav moveto %.2f %.2f %.2f", Config.SPOT_X, Config.SPOT_Y, Config.SPOT_Z))

        -- 先等 vnav 真的「動起來」，再等它「停下來」。
        -- 直接等 IsRunning() == false 會在指令還沒生效時就誤判成已抵達。
        local started = false
        local elapsed = 0
        while elapsed < 8 do
            local ok, running = pcall(function() return IPC.vnavmesh.IsRunning() end)
            if ok and running then started = true; break end
            wait(0.2)
            elapsed = elapsed + 0.2
        end

        if started then
            local elapsed2 = 0
            while elapsed2 < 90 do
                local ok, running = pcall(function() return IPC.vnavmesh.IsRunning() end)
                if ok and not running then break end
                wait(0.3)
                elapsed2 = elapsed2 + 0.3
            end
        end
    else
        echo("已在定點附近")
    end

    local finalDistance = getPlayerDistanceToSpot()
    echo("最終距離定點 = " .. toStr(finalDistance))
    return finalDistance ~= nil and finalDistance <= Config.ARRIVE_RADIUS + 1
end


----------------------------------------------------------------
-- 接理符
----------------------------------------------------------------

-- 開啟理符任務視窗
--   流程：/target 格里格 → /interact → 對話跳出「有什麼事？」四選一選單
--   → 選「製作任務」(0-based index，經實測為 1，不算標題) → GuildLeve + JournalDetail 開啟
--
--   為什麼不直接掃清單找目標理符：
--   GuildLeve 的理符清單是虛擬化渲染，GetNode 只讀得到「目前反白」那一項
--   的文字，其餘項目一律讀不到。因此無法用「掃描找名稱」的方式選取，
--   只能依賴「區塊策略」讓預設反白永遠是我們要的那個（見檔頭說明）。
local function openLeveWindow()
    if isAddonReady("GuildLeve") and isAddonReady("JournalDetail") then return true end

    closeStrayWindows()

    if not targetNpcVerified(Config.NPC_LEVE) then
        echo("!! 鎖不到 " .. Config.NPC_LEVE)
        return false
    end

    yield("/interact")
    wait(1.2)
    advanceTalkDialogue()

    if isAddonReady("SelectString") then
        yield("/callback SelectString true 1")   -- 「製作任務」
        wait(1.5)
    end

    local elapsed = 0
    while elapsed < 8 do
        if isAddonReady("GuildLeve") and isAddonReady("JournalDetail") then
            wait(0.5)
            return true
        end
        wait(0.3)
        elapsed = elapsed + 0.3
    end

    echo("!! 理符視窗沒開起來")
    return false
end

-- 接受目前反白選取的那個理符
--   「接受」按鈕走的是原生按鈕元件點擊（AtkComponentButton），不是
--   /callback 送數值那條路——這件事是翻 ClickLib 原始碼才確認的，
--   之前試過 /callback JournalDetail true 0~8 與各種雙參數組合全數無效。
--   最終解法是 /send NUMPAD0 模擬鍵盤操作遊戲內建的「手把式游標導覽」：
--   第一下把 UI 游標移到「接受」鈕並反白，第二下才是真正確認送出。
--   因為次數會因遊戲反應而異，這裡用重試迴圈，每按一次就讀一次
--   「理符任務」持有數量，數字增加就代表成功。
--
--   ⚠ 執行期間絕對不能移動滑鼠！
--   滑鼠移動會讓遊戲把 UI 焦點（focus）從鍵盤導覽切回滑鼠指向的位置，
--   NUMPAD0 反白的游標會被打斷或跳到滑鼠所在的其他按鈕/選項，
--   導致「接受」沒有真正被確認、或誤觸到別的東西。實測發生過因為
--   切視窗複製聊天記錄、滑鼠移動經過遊戲畫面，導致連續多次
--   NUMPAD0 按鍵完全不生效的情況（見 acceptTargetLeveWithRetry 的
--   外層重試機制，就是為了緩解這個焦點被打斷的問題）。
--
--   /send 本身也依賴 Windows 視窗焦點，遊戲不在前景時按鍵會被吃掉，
--   這是另一層已知限制（見檔頭「重要限制」）。
local function acceptCurrentLeve()
    local before = getLeveCount()
    if before == nil then
        echo("!! 讀不到理符任務數")
        return false
    end

    for attempt = 1, 8 do
        yield("/send NUMPAD0")
        wait(0.5)

        local now = getLeveCount()
        if now ~= nil and now > before then
            echo("接受成功  " .. before .. " -> " .. now)
            return true
        end
    end

    echo("!! 按鍵未生效（確認遊戲視窗是否在前景、TextAdvance 的 QA 是否已關閉）")
    return false
end

-- 單次嘗試：處理「預設反白不一定是目標理符」的情況
--   最多繞 3 輪：若第一個不是目標理符（推測是奶油麵），先接受清空該位置，
--   重新開窗後理應輪到目標理符。這個迴圈同時涵蓋「一開始就是目標」和
--   「需要先清掉奶油麵」兩種情況，呼叫端不需要事先判斷是哪一種。
local function acceptTargetLeveOnce()
    for round = 1, 3 do
        if not openLeveWindow() then return false end

        local leveName = getCurrentLeveName()
        echo("目前選中：" .. toStr(leveName))

        if leveName == nil then
            echo("!! 讀不到理符名稱")
            return false
        end

        if leveName == Config.TARGET_LEVE then
            return acceptCurrentLeve()
        end

        echo("不是目標理符，先接受清空")
        if not acceptCurrentLeve() then return false end
    end

    echo("!! 繞了 3 輪仍未接到目標理符")
    return false
end

-- 外層重試：整個接理符流程失敗就關窗重來
--   /send 按鍵偶爾會不生效（曾因 TextAdvance 的 QA 誤開跟腳本搶按鈕、
--   也曾疑似焦點瞬斷），單純在同一個視窗裡多按幾次不一定能解決，
--   乾脆整個關窗、重新走一次 /target → /interact，相當於「重新開始」。
--   實測這層重試確實在某一輪派上用場，不是過度設計。
local function acceptTargetLeveWithRetry()
    for attempt = 1, Config.MAX_OUTER_RETRIES do
        echo("--- 接理符嘗試 " .. attempt .. " / " .. Config.MAX_OUTER_RETRIES .. " ---")

        if acceptTargetLeveOnce() then
            return true
        end

        echo("整個流程失敗，關閉視窗後重試")
        closeStrayWindows()
        wait(1.5)
    end

    echo("!! 接理符重試 " .. Config.MAX_OUTER_RETRIES .. " 次仍失敗")
    return false
end


----------------------------------------------------------------
-- 交付
----------------------------------------------------------------

-- 交付目標理符
--   跟接理符不同，交付這一段完全交給 YesAlready + TextAdvance 代勞：
--   選擇要交付哪個理符（SelectIconString）、把道具放進交付欄
--   （TextAdvance 的 RF）、確認交付（TextAdvance 的 RH），這幾步都曾
--   嘗試純 Lua 處理，但 SelectIconString 清單一樣是虛擬化的讀不到
--   文字，Request 視窗掃遍全部節點也找不到任何道具識別資訊，純腳本
--   驗證不了「交付的是不是目標道具」，投入產出比太差，故放棄。
--
--   插件處理不了的部分，只剩「確定要交易優質道具嗎？」這個 SelectYesno
--   確認框——實測插件不會每次都自動按掉，腳本自己補上這一步。
--
--   完成判斷：輪詢 HQ 目標道具數量，減少了就代表交付成功。
local function turnIn()
    closeStrayWindows()

    local before = getHqItemCount(Config.ITEM_ID)
    echo("交付前 HQ 道具數量 = " .. toStr(before))
    if before == nil then return false end

    if not targetNpcVerified(Config.NPC_TURNIN) then
        echo("!! 鎖不到 " .. Config.NPC_TURNIN)
        return false
    end

    yield("/interact")

    local elapsed = 0
    while elapsed < 30 do
        if isAddonReady("SelectYesno") then
            yield("/callback SelectYesno true 0")   -- 「確定」
            wait(1)
        end

        local after = getHqItemCount(Config.ITEM_ID)
        if after ~= nil and after < before then
            echo("交付成功  " .. before .. " -> " .. after)
            return true
        end

        wait(0.5)
        elapsed = elapsed + 0.5
    end

    echo("!! 30 秒內沒偵測到交付完成")
    return false
end


----------------------------------------------------------------
-- 插件初始化
----------------------------------------------------------------

-- 啟用 YesAlready 與 TextAdvance
--   兩者提供的 IPC 介面不同：YesAlready 有直接的 SetPluginEnabled(bool)，
--   冪等可重複呼叫；TextAdvance 只能用聊天指令 /at e 開啟（沒有對應的
--   setter），且沒有 toggle 以外的寫入介面，這裡先判斷 IsEnabled()
--   避免誤觸關閉。
local function enablePlugins()
    pcall(function() IPC.YesAlready.SetPluginEnabled(true) end)
    if not IPC.TextAdvance.IsEnabled() then
        yield("/at e")
    end
    wait(1)

    echo("插件狀態 YesAlready=" .. toStr(IPC.YesAlready.IsPluginEnabled())
      .. " TextAdvance=" .. toStr(IPC.TextAdvance.IsEnabled()))
end

-- 關閉 YesAlready 與 TextAdvance
--   腳本結束後（不管是正常跑完還是中途失敗）都要關掉，避免這兩個插件
--   繼續在背景自動接受任務 / 推進對話，干擾使用者接下來的手動操作。
--   TextAdvance 同樣沒有直接的 setter，用 /at d 關閉。
local function disablePlugins()
    pcall(function() IPC.YesAlready.SetPluginEnabled(false) end)
    if IPC.TextAdvance.IsEnabled() then
        yield("/at d")
    end
    wait(1)

    echo("插件已關閉 YesAlready=" .. toStr(IPC.YesAlready.IsPluginEnabled())
      .. " TextAdvance=" .. toStr(IPC.TextAdvance.IsEnabled()))
end


----------------------------------------------------------------
-- 主流程
----------------------------------------------------------------
echo("=== 開始執行，請保持遊戲視窗在前景 ===")

enablePlugins()

if not ensureCulinarian() then
    echo("!! 職業切換失敗，中止")
    disablePlugins()
    return
end

if not ensureAtSpot() then
    echo("!! 移動失敗，中止")
    disablePlugins()
    return
end

local completedRounds = 0
for round = 1, Config.ROUNDS do
    echo("=== 第 " .. round .. " / " .. Config.ROUNDS .. " 輪 ===")

    if not acceptTargetLeveWithRetry() then
        echo("!! 接理符失敗，停止")
        break
    end
    closeStrayWindows()

    if not turnIn() then
        echo("!! 交付失敗，停止")
        break
    end

    completedRounds = completedRounds + 1
    wait(1)
end

echo("=== 結束，完成 " .. completedRounds .. " / " .. Config.ROUNDS .. " 輪 ===")
disablePlugins()
