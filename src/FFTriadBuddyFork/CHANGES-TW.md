# TriadBuddy（台服版）本地修改說明

這個資料夾是台服 API13 版 TriadBuddy 的原始碼，加上本地新增的功能。
`Fork` 後綴表示內容跟上游不同。目前版本以 `TriadBuddy.csproj` 的 `<Version>` 為準。

新增的檔案整個都是本地修改；改到原本檔案的地方，大多有 `fork` 字樣的註解，搜尋 `fork` 就能找到。

## 來源

| 項目 | 來源 | Commit |
|---|---|---|
| 主程式 | 原始碼壓縮檔 `FFTriadBuddyDalamud-acb68f2…`（原作者 MgAl2O4；是上游還是 cycleapple 的分支未確認，裡面的版本號是 1.13.0.0、語系是舊的簡中） | `acb68f2db8037b797a6254a0c4a8d1fbe1dce376` |
| triad-shared（子模組） | https://github.com/MgAl2O4/FFTriadBuddy ，分支 `gamelogic-shared` | 未記錄 |

子模組已經直接展開成一般檔案，放進這個 repo。

`assets/loc/zh.json` 換成從已發佈的 `TriadBuddy-1.13.0.2.zip` 裡抽出來的繁中語系（64/64 個鍵），
原始碼裡附的是舊的簡中版。沒換的話，介面會退回簡中。

## 修改紀錄

### 1.13.0.201：自動對戰、卡片收集分頁、前往 NPC

#### 1. 自動出牌、自動循環對戰（`plugin/AutoTriad.cs`、`plugin/PluginPanelAuto.cs`）

兩個開關，預設關閉，**不存檔**，每次載入插件都要自己打開。

- **自動出牌**：只在跟 NPC 對局、解算器算出下一步時，照建議出牌。PvP、幻卡大賽、認不出 NPC 時都不動作。
- **自動循環對戰**：鎖定第一個遇到的 NPC，一直「挑戰 → 選牌組 → 對局 → 再戰」，
  直到這個 NPC 會掉的卡全部「已學會」或「背包已有卡片道具」為止。第一次要自己跟 NPC 對話打開挑戰視窗。
  - 牌組：用插件評估出的已存最佳牌組；評估不到時用設定裡指定的那組。
  - 停止條件：目標卡都拿到、打滿局數（預設 50）、連輸局數（預設 3）、進入戰鬥、換了對手、
    出現不認得的確認框、按了按鈕超過設定秒數（預設 8 秒）畫面都沒換。
- 按鈕預設「一出現就馬上按」，沒換畫面每 1.5 秒再按一次；也可以改回隨機等待。出牌前的等待是隨機範圍，可調。

送給遊戲的指令是在遊戲裡手動操作、用「記錄介面事件」錄下來的，跟手動操作一模一樣：

| 動作 | 介面 | 送出的內容 |
|---|---|---|
| 出牌 | `TripleTriad` | 回呼 `[Int 14, UInt (手牌 << 16) \| 格子]`，例：第 2 張手牌（從 0 數）放第 8 格 → `0x20008` |
| 挑戰 | `TripleTriadRequest` | 回呼 `[Int 0]` |
| 選牌組 | `TripleTriadSelDeck` | `ListItemClick` 事件，參數 1，事件資料帶清單項目；介面自己接著送 `[Int 0]` |
| 再戰 | `TripleTriadResult` | 回呼 `[Int 0]`（「結束」是 `[Int 1]`，插件不會按） |

踩過的坑：
- 一開始把手牌和格子的位置寫反，牌會出到別格。
- 自己的牌剛落下時，介面還會短暫顯示「輪到你」，解算器會用新盤面多算一手送出去（遊戲會忽略）。
  現在出完牌要等看到對手回合（或對手的牌也落下）才算下一手。
- 結算畫面出現時，對局視窗還開在底下，所以判斷畫面時要先看結算。
- `GameNpcDB` 用 `npcId`（= `TriadNpc.Id`）當 key，`triadId` 是遊戲表格的另一個編號，比對 NPC 時不能混用。

除錯用的「記錄介面事件」：在聊天欄和 dalamud.log 印出幻卡相關視窗收到的事件與回呼
（用 `IAddonLifecycle` 的 `PreReceiveEvent`，再 hook `AtkUnitBase.FireCallback`），平常保持關閉。

#### 2. 主視窗改成分頁（`plugin/PluginWindowStatus.cs`）

狀態、自動對戰、卡片收集、設定合併成同一個視窗。原本獨立的設定畫面、自動對戰視窗都併進來了；
跟遊戲介面貼在一起的小視窗（牌組評估、卡片搜尋等）不變。

指令：`/triadbuddy`（狀態）、`/triadbuddy auto`、`/triadbuddy cards`、`/triadbuddy config`。

#### 3. 卡片收集分頁（`plugin/PluginPanelCollection.cs`）

不用打開遊戲的幻卡列表：學會了哪些卡直接問遊戲記憶體（原本就有的 `IsCardOwned`），
背包裡的卡片道具用 `InventoryManager.GetInventoryItemCount` 讀。

- **依 NPC**：每個 NPC 還缺幾張；點選後顯示地點（地圖、前往按鈕）、對局費、有沒有打贏過、每張卡的狀態。
- **缺的卡**：所有還沒學會的卡、背包有幾張、哪些 NPC 會掉；點選後列出每個會掉這張卡的 NPC 資訊。

#### 4. 前往 NPC（`plugin/NpcTravel.cs`、`plugin/MountPicker.cs`）

地圖按鈕旁的跑步按鈕。做法參考 `src/GatherBuddyRebornFork` 的自動採集移動（`AutoGather.Movement.cs`、`IpcSubscribers.cs`、`Teleporter.cs`）。

1. 不在 NPC 的區域：傳送到該區域離 NPC 最近、已登記的以太之光（查 `Telepo` 傳送清單判斷登記）。
   區域裡只有城內以太之光：先傳送到主城，再呼叫 `Lifestream.AethernetTeleport(名稱)`。
2. 離 NPC 超過 30 米就上坐騎：有指定且已取得的坐騎就騎它，否則用一般動作 24「坐騎隨機」。
3. 這個區域的風脈泉都解完（`PlayerState.IsAetherCurrentZoneComplete`）就飛。
4. `vnavmesh.Query.Mesh.NearestPoint` 把終點修正到網格上，再 `vnavmesh.SimpleMove.PathfindAndMoveTo(座標, 飛)`；
   到 3 米內就 `vnavmesh.Path.Stop`。
5. 抵達後下坐騎（`UseAction(ActionType.Mount, 0)` 每秒一次，飛行中第一下先降落），並選取 NPC。

設定在「設定」分頁的「前往 NPC」：騎坐騎前往、坐騎、能飛就飛、抵達後下坐騎。

為此在 `GameNpcInfo` 多存了 `ENpcId`、`TerritoryId`、`WorldPos`（`Level` 表的實際座標，`GameDataLoader` 原本就有讀，只是沒存）。

## 編譯

需要用台服啟動器附的 Dalamud（API13）：

```bash
DALAMUD_HOME='C:\Users\how\AppData\Roaming\FFXIVSimpleLauncher\Dalamud\Injector\' dotnet build -c Release
```

產出：
- `bin/x64/Release/TriadBuddy/latest.zip`：安裝包，結構跟發佈的 zip 相同
- `bin/x64/Release/TriadBuddy.dll`

要透過 repo 發佈更新時，先調高 `TriadBuddy.csproj` 的 `<Version>`，插件管理器才會偵測到新版。
版本號只能是最多四段的純數字。本 fork 的版本規則是 `1.13.0.2NN`：前面的 `2` 代表基於已發佈的 1.13.0.2，
`NN` 是第幾次本地修改（`1.13.0.201`、`1.13.0.202`…）。

## 待確認

- [x] 自動出牌、挑戰、選牌組、再戰（實測連跑 3 局正常）
- [ ] 出完牌等對手回合的修正，還沒實測
- [ ] 按鈕「一出現就馬上按」與逾時判斷，還沒實測
- [ ] 卡片收集分頁的畫面，還沒實測
- [ ] 前往 NPC（傳送、城內傳送、坐騎、飛行、下坐騎），還沒實測
- [ ] triad-shared 子模組的 commit 還沒記錄
