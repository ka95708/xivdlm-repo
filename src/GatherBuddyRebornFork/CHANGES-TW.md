# GatherBuddyReborn（台服版）本地修改說明

這個資料夾是台服 API13 版 GatherBuddyReborn 的原始碼，加上本地的修改和修正。
`Fork` 後綴表示內容跟上游不同。目前版本以 `GatherBuddy/GatherBuddy.csproj` 的 `<Version>` 為準。

程式碼裡所有本地修改都用 `[fork]` 註解標記，搜尋 `[fork]` 就能找到全部改動。

## 來源

| 項目 | 來源 | Commit |
|---|---|---|
| 主程式 | https://github.com/cycleapple/GatherBuddyReborn ，分支 `api13-tw-7.3.5` | `81c2f89d7f1ff2736444df4771cda645a8ebe477`（2026-08-19，Release 7.3.5.7） |
| ECommons（子模組） | https://github.com/NightmareXIV/ECommons | `6cfc5a577ffd9fa442f43aafa3c76245850af1f3` |
| OtterGui（子模組） | https://github.com/Ottermandias/OtterGui | `9523b7ac725656b21fa98faef96962652e86e64f` |
| OtterGuiInternal（OtterGui 的子模組） | https://github.com/Ottermandias/OtterGuiInternal | `49631aab5c267953f9d442546488ea3bfdf52fa7` |

`repo.json` 發佈的 `GatherBuddyReborn-7.3.5.7.zip` 就是用這份原始碼編譯的。
子模組已經直接展開成一般檔案，各層的 `.git` 都移除了，方便放進這個 repo。

注意：這份跟上游 FFXIV-CombatReborn 的原版不同。原版 7.3.5.7 沒有 AutoHook 整合。

## 修改紀錄

### 7.3.5.701：換釣點後卡在「Fishing...」，不會繼續釣魚

**檔案：** `GatherBuddy/AutoGather/AutoGather.AutoHook.cs` 的 `SetupAutoHookForFishing()`，在沿用既有預設的那個分支裡。

**症狀：**
- 自動釣魚每到 `MaxFishingSpotMinutes`（預設 20 分鐘）會換一次釣點。走到新釣點後，狀態停在「Fishing...」，不會拋竿。
- 手動關掉再打開 GBR 的自動採集，就恢復正常。

**原因：**
1. 換釣點時，`AutoGather.cs` 的 `DoFishMovement()` 在釣點到期的分支裡會呼叫 `AutoHook.SetPluginState(false)` 和 `AutoHook.SetAutoStartFishing(false)`。精煉魔晶石、分解道具的流程也會這樣做。
2. 到了新釣點，`HandleReady()` 會呼叫 `SetupAutoHookForFishing()`。因為魚種沒變，所以走「沿用既有 AutoHook 預設」的分支。這個分支只呼叫了 `SetPluginState(true)`，**沒有重新開啟 `SetAutoStartFishing(true)`**。
3. 有用 AutoHook 時，GBR 自己不會拋竿，是交給 AutoHook 的「自動開始釣魚」處理。這個開關一直沒被打開，所以沒有人拋竿。
4. 重開 GBR 時會呼叫 `CleanupAutoHook()` 清掉預設，接著走「建立新預設」的分支。那個分支有呼叫 `SetAutoStartFishing(true)`，所以又正常了。

上游 FFXIV-CombatReborn 7.3.8.14 在同一個分支裡就有 `SetAutoStartFishing(true)`，是台服分支漏掉了這一行。

**修正：**
```csharp
else
{
    AutoHook.SetPluginState?.Invoke(true);
    // [fork] Relocation / materia / reduce paths turn auto-start off; re-arm it or AutoHook never casts again.
    AutoHook.SetAutoStartFishing?.Invoke(true);
}
```

**補充：** AutoHook 設定裡的「自動開始釣魚」被 GBR 關掉是原本的設計。`HandleReady()` 會在第一竿拋出 5 秒後把它關掉（日誌：`Disabled SetAutoStartFishing after initial cast`），後續由 AutoHook 自己收竿、再拋竿。這部分沒有修改。

## 編譯

需要用台服啟動器附的 Dalamud（API13）。ECommons 在 Windows 上不會讀 `DALAMUD_HOME`，所以要另外用 `DalamudLibPath` 指定路徑：

```bash
DALAMUD_HOME="C:/Users/how/AppData/Roaming/FFXIVSimpleLauncher/Dalamud/Injector/" dotnet build GatherBuddy/GatherBuddy.csproj -c Release "-p:DalamudLibPath=C:/Users/how/AppData/Roaming/FFXIVSimpleLauncher/Dalamud/Injector/"
```

產出：
- `GatherBuddy/bin/Release/GatherBuddyReborn/latest.zip`：安裝包，結構跟發佈的 zip 相同
- `GatherBuddy/bin/Release/GatherBuddyReborn.dll`

要透過 repo 發佈更新時，先調高 `GatherBuddy/GatherBuddy.csproj` 的 `<Version>`，插件管理器才會偵測到新版。版本號只能是最多四段的純數字，不能加 `-fix` 之類的後綴。本 fork 的版本規則是 `7.3.5.7NN`：前面的 `7` 代表基於上游 7.3.5.7，`NN` 是第幾次本地修正（`7.3.5.701`、`7.3.5.702`…）。

## 待確認

- [ ] 在遊戲內實測：換釣點後會不會自動繼續釣魚
- [ ] 自動收藏品（`AutoGather.FishingCollectable.cs`）不作用的問題，還沒調查
