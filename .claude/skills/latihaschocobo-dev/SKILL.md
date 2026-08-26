---
name: latihaschocobo-dev
description: LatihasChocobo（TC版）Dalamud 插件的完整內部架構與運作機制筆記。修改 Plugin.cs / TrackMemory.cs / MainWindow.cs / MConfiguration.cs 中任何比賽自動化、閃避判定、賽道記憶邏輯前，先讀這份文件。
---

# LatihasChocobo（TC版）開發筆記

Dalamud 插件，自動化 FFXIV 陸行鳥競賽（衝刺、閃避、拾取、匹配、學習賽道）。核心邏輯全部集中在 `Plugin.Press()`，掛在 `Framework.Update`（`Plugin.cs:87` `Framework.Update += Press;`），遊戲每幀執行一次，是整個插件唯一的「大腦」。

## 建置與環境（TC / API13）

- 現況：分支 **`tc-7.20`**、`Dalamud.NET.Sdk/13.0.0`、`LatihasChocobo.json` 的 `DalamudApiLevel` 是 **13**。
  `tc-7.15` 是凍結的 API12 archive，不要往上面提交（GitHub 的 `origin/HEAD` 還指著 `tc-7.15`，
  clone 完先 `git checkout tc-7.20`）。
- 🔴 **直接 `dotnet build` 會失敗**：`Dalamud.NET.Sdk` 預設吃 `DALAMUD_HOME` 環境變數，
  本機那個變數指向 `%APPDATA%\FFXIVSimpleLauncher\Dalamud\Injector` ——
  **那裡是啟動器自帶的舊 Dalamud 12.0.2.0**（實測 FileVersion，沒有 `Dalamud.Bindings.ImGui.dll`），
  結果是 `error CS0234: 命名空間 'Dalamud' 中沒有類型或命名空間名稱 'Bindings'`。
  建置時要覆蓋成真正的 API13 Dalamud：

  ```powershell
  $env:DALAMUD_HOME = "<pin目錄>"; dotnet build LatihasChocobo.csproj -c Release
  ```

  本機實測可用的兩處：`%APPDATA%\xivlauncher\addon\Hooks\dev`（13.0.0.6，與 CI 釘的同版）、
  `D:\ffxiv-tc-port\Dalamud\bin\Release`（13.0.0.16，TC 遊戲執行期實際載入的那份）。
  **不要**去覆寫 `%APPDATA%\FFXIVSimpleLauncher\Dalamud\Injector`。
- ⚠️ CI 釘 13.0.0.6、執行期是 13.0.0.16，**「本機編得過」不等於「CI 編得過」**。
- csproj 的 `<OutputPath>bin</OutputPath>` 讓產物是**扁平**的 `bin\LatihasChocobo.dll`
  （沒有 `Release/net9.0-windows` 那幾層），複製別的 repo 的 `PropertyGroup` 過來時不要弄丟。
- `BuildNumber.txt`（受 git 追蹤）每次 build 都會 +1、**弄髒工作區**，它是建置副產物，
  `git checkout -- BuildNumber.txt` 還原即可，小心 `git add -A`。
  工作區髒掉會讓 `release_plugin.py` 判定「有未提交變更」而**跳過這個外掛不發版**。
  csproj 的 `<VersionPrefix>7.15.0</VersionPrefix>` 跟實際發版無關（`release.yml` 用
  `-p:Version=<tag>` 從 git tag 覆蓋），feed 上是 `v7.20.0.x` 而 csproj 還寫 `7.15.0` **不是漏改**。
- 在地化：這個外掛的 UI 字串是直接寫死在 `MainWindow.cs` 的繁中，**完全沒有用到 `ClientLanguage`**
  （全 repo 搜不到這個型別）。所以 2026-07「TC 的 `ClientLanguage` 從 `ChineseSimplified`(4) 變成
  `TraditionalChinese`(7)、害一堆外掛靜默掉回英文/日文」那次事件，**本 repo 不受影響、沒有東西要修**。

## 檔案分工
- `Plugin.cs` — 插件進入點、主迴圈 `Press()`、閃避方向判定 `GetTargetSide()`、按鍵模擬、UI 內部資料讀取（AtkResNode 解析）、比賽進出事件 `TerritoryChanged`、任務搜尋器自動加入。
- `TrackMemory.cs` — 賽道學習系統，靜態類別，資料以 `Dictionary<ushort TerritoryType, TrackData>` 存於記憶體，序列化成 `track_memory.json`。
- `MainWindow.cs` — ImGui UI，各分頁繪製，其中「賽道」分頁含路線圖 `DrawRouteMap` 與坡度剖面圖 `DrawElevationProfile`。
- `MConfiguration.cs` — `IPluginConfiguration` 實作，使用者設定與插件執行中自我調整的校正值（`JumpPeakTime`/`JumpPeakHeight`）都存在這裡，Dalamud 會自動序列化到插件設定資料夾。
- `Constant.cs` — 共用常數（如 Windows Message 代碼 `WM_KEYDOWN`/`WM_KEYUP`）。

## 插件生命週期
1. `Plugin()` 建構子：讀設定、建 UI 視窗、註冊 `/lc` `/latihaschocobo` 指令、用 `FindWindowEx` 尋找 class name `FFXIVGAME` 且 pid 符合當前行程的視窗（`mwh`）、初始化 `TrackMemory`（傳入插件設定資料夾路徑）、掛 `Framework.Update`/`TerritoryChanged` 事件。若插件重載時剛好人在賽道上，直接補記 `isRunning = true` 與 `TrackMemory.StartRace()`。
2. 每次 `TerritoryChanged` 觸發（切換地圖）：
   - 進入賽道（389/390/391）→ 記錄起始 territory、依 `BoundaryMode` 決定呼叫 `TrackMemory.StartBoundaryRace()` 或 `StartRace()`、記錄目前經驗值供賽後結算、延遲 `AutoDutyWait` 秒後才把 `isRunning` 設為 true（避免載入未完成就開始按鍵）。
   - 離開賽道 → 停止所有按鍵（放開 `PressTime` 中所有 key）、依 `BoundaryMode` 呼叫 `TrackMemory.EndBoundaryRace`/`EndRace` 收尾並存檔、結算本場經驗值增量（`LastRaceExpGain`，需處理經驗值上限重置的情況）。
   - 若啟用 `AutoDuty` 且目前 territory 屬於設定的「循環區域」清單（`|` 分隔），延遲後呼叫 `RequestRace()` 自動開任務搜尋器並點加入。

## Press() 主迴圈執行順序（重要，改動時要保持順序關係）
1. `CheckRankUp()` — 讀 `RaceChocoboManager.Instance()->Rank`，40 級時自動關閉 `AutoDuty` 並播提示音，這一步無視 `Enabled`/`isRunning`，隨時都在檢查。
2. 若插件未啟用或不在跑步狀態（`!Enabled || !isRunning`）直接 return，後面全部不執行。
3. 確保遊戲視窗在前景（`SetForegroundWindow`），因為 `SendMessage` 在某些情況下（例如視窗完全被遮蔽/最小化恢復後）會被系統忽略。
4. 道具偵測與使用（`CanUseItem()` 讀 ActionBar 圖示材質路徑判斷是否為可用狀態、路徑不含 `070101` 即可用），冷卻 100ms（`_lastItemUse`），用 `PressTime` 機制持按而非瞬按瞬放。
5. 嘗試點擊 `RaceChocoboResult` 結果視窗的確認按鈕（`Click`，模擬 AtkEvent 觸發）。
6. 讀取 `_RaceChocoboParameter` addon：超速圖示材質路徑含 `180043` 判斷 `speedHigh`；體力 `HpPercent` 來自 Counter 節點文字。
7. 讀取 `_ToDoList` addon 的「進度：」文字算 `RacePercent`（UI 給的是「剩餘」，插件內部轉成「已完成」= `100 - 剩餘`）。
8. 技能2（KC_2）判斷：優先用 `TrackMemory.GetTrackProgress` 算出的真實路程百分比，賽道總長 `< 200u`（資料不足）才退回 UI 的 `RacePercent`。體力 >70% 或進度 >75%，且進度 >5%，冷卻 20 秒（200_000_000 ticks）。
9. 決定是否要按 W（`notSpeedHigh` 邏輯）：低體力/長路程時關閉超速（`DisableSpeedUpWhenLowHP`），高體力大幅領先剩餘路程時強制超速（`EnableSpeedUpWhenHighHP`，體力比進度高 ≥5%），否則依機率權重 `SpeedHighW` 隨機超速。
10. 統一放開所有超時未續按的鍵（`PressTime` 逐一檢查，超過 `PRESS_TIME` 就送 `WM_KEYUP`），W 鍵在該按的時候跳過放開。
11. 計算 `CurrentSpeed`（u/s，用連續兩幀玩家位置差 / 時間差，`dt > 0.01f` 才更新避免除以極小值誤差）。
12. 跳躍弧線錄製：`_jumpRecording` 為真時持續追蹤玩家 Y 座標找最高點（`_jumpPeakY`/`_jumpPeakTick`），判定「落地」條件是已經過 0.3 秒（下限，避免起跳瞬間誤判）且 Y 座標回到起跳點附近 `_jumpStartY + 0.3f`；落地後若峰值時間 > 0.1s 且峰值高度 > 0.5u 才視為有效跳躍樣本，用 EMA（0.7 舊值 + 0.3 新值）更新 `Configuration.JumpPeakTime`/`JumpPeakHeight` 並立刻存檔。
13. **邊界模式短路**：若 `BoundaryMode` 開啟且 `BoundaryPhase > 0`，只記錄邊界路點（`RecordBoundaryWaypoint`）並持續按 A（phase 1）或 D（phase 2），`return` 提前結束本幀，完全不做閃避判定。
14. 掃描 `ObjectTable` 找最近的壞物件 `badTarget` 與好物件 `goodTarget`（`ObjectKind` 限 `EventObj`/`BattleNpc`）。掃描時同步呼叫 `TrackMemory.RecordObject` 記錄所有壞/好物件位置（不論是否被選為最近目標）。壞物件只有在 `GetTargetSide` 會產生有效閃避方向時才會搶佔「最近」候選（避免選到明明閃不到的遠處/背後物件）。
15. `TrackMemory.RecordWaypoint` 記錄玩家自身路點；另外掃描 `DataId == 3705`（對手陸行鳥）且距離 <150u 時呼叫 `RecordOpponentWaypoint`（因為對手一定跑在賽道上，可以補強路徑資料，尤其是玩家自己還沒跑過的路段）。
16. 決定最終目標與方向：優先 `badTarget`，若其方向判定為 `InValid`（例如壞物件距離太遠、角度不合）則退而使用 `goodTarget`。
17. **邊界修正短路**：冷卻 1 秒（`_lastBoundaryFix`），呼叫 `TrackMemory.GetBoundaryCorrection`（只取半徑 15u 內的邊界點，避免彎道抓錯段落），若玩家距左/右邊界 <5u，強制按對應方向鍵修正後 `goto endPress`，跳過下面所有的物件閃避 switch。
18. 依 `dir` 執行對應按鍵：`Left`/`Right` 反方向閃避（好物件是同方向靠近，壞物件是反方向遠離，注意程式用 `isBad` 三元切換 A/D）；`FrontUp` 觸發跳躍（同時啟動跳躍錄製，但只有 `!PressTime.ContainsKey(KC_SPACE)` 時才重新啟動錄製，避免連續按跳干擾資料）；`Front`/`InValid`（無即時目標可判斷方向）時退回記憶輔助：先找記憶中「前方已確認好物件」主動靠近（`GetNearestMemoryGoodObjectAhead`），沒有的話用 `GetPathDirection`（look-ahead 15u）沿記憶賽道路徑修正方向，兩者都無效就什麼都不做（沿用目前按鍵狀態）。

## 按鍵模擬機制
- 全部透過 `SendMessage`（user32.dll）送 `WM_KEYDOWN`/`WM_KEYUP` 到遊戲視窗控制代碼 `mwh`，**不需要遊戲視窗在最前面**，但插件仍會每幀主動 `SetForegroundWindow` 一次，理由是某些情境下背景視窗會被系統丟棄按鍵訊息（尤其是被其他視窗完全遮蔽時）。
- `TryPress(code, percent)`：若該鍵尚未記錄按下時間就記錄現在並直接 return（先佔位，下一幀才真的送出，形成天然的最短按壓間隔）；若距離上次時間 ≤ `PRESS_TIME`（`Configuration.PressMs * 10000` ticks）則不重複送出；`percent` 用於機率性按鍵（如超速機率 `SpeedHighW`）。
- 放開邏輯集中在步驟 10：遍歷 `PressTime` 找出超時的鍵統一送 `WM_KEYUP` 並從邏輯上視為已放開（但沒有從字典移除，`ContainsKey` 判斷用在跳躍錄製防重複觸發）。
- 道具鍵（KC_1）刻意不用 `TryPress`，而是直接手動操作 `PressTime` + `SendMessage`，因為道具需要在偵測到「可用」的瞬間立即按下（`CanUseItem` 檢查優先於其他邏輯，寫在 `Press()` 最前面）。

## TrackMemory（賽道記憶）— 逐方法說明
資料結構：`TrackedObject`（DataId + XYZ + SeenCount）、`TrackWaypoint`（XYZ + Rotation + SeenCount）、`TrackData`（Objects / Waypoints / LeftBoundary / RightBoundary 四個 List）。`ConfirmCount = 2`：同一位置至少出現兩次（不同場次）才視為「已確認」，避免單場雜訊（例如殘影、瞬時物件）污染判斷。

- `RecordObject(dataId, pos)`：本場暫存 `_curObjs`，7u（`ObjMerge`）內視為同一物件不重複加入；`SkipRecording = {3596}`（移動障礙怪物）位置不固定，直接跳過不記錄。
- `RecordWaypoint(pos, rot)`：本場暫存 `_curWps`，每 5u（`WpStep`）取一個點，避免路點過密。
- `RecordOpponentWaypoint`：同樣寫入 `_curWps`，但判斷邏輯是「附近沒有既有路點才加入」而非固定步長，用意是補齊玩家自己沒走過但對手走過的路段。
- `EndRace(territory)`：把本場暫存的物件/路點與資料庫既有資料合併——物件用 `DataId + 距離 < 7u` 比對，命中則 `SeenCount++`；路點用 `距離 < 10u`（`WpMerge`）比對，命中則用「圓形平均」（sin/cos 分量按 SeenCount 加權疊加後 `Atan2`）更新朝向、`SeenCount++`。存檔後清空暫存，並讓 `_pathCache` 對該 territory 的快取失效。
- `BuildOrderedPath(wps)`：把已確認路點串成一條有序賽道路徑。從 Z 座標最小（最南端）的點開始，每步用 nearest-neighbor 找「距離 < 25u 且大致在目前朝向前方（`dot > 0.1`）」的下一點，評分公式 `dist3D / (dot + 0.5)`（既近又順著方向的優先），找不到符合條件的下一點就提前結束路徑。
- `GetOrderedPath(territory)`：對外介面，帶 `_pathCache` 快取，只用 `SeenCount >= ConfirmCount` 的路點建路徑。
- `GetTrackProgress(territory, playerPos)`：用有序路徑算 3D 總長，`< 200u` 視為資料不完整直接回傳 `default`（呼叫端要自行 fallback）；否則找玩家最近的路徑索引（用 2D XZ 距離避免高低差誤導），回傳 `(已行進%, 剩餘距離, 總長度)`。
- `IsConfirmed(territory, dataId, pos)`：物件版本的確認判斷，`GetTargetSide` 用它決定要用「已確認」還是「未確認」的距離/角度門檻。
- `GetCenterCorrection`：另一種置中修正（用左右邊界最近點中點），目前 `Press()` 主迴圈**沒有實際呼叫**，是預留/備用方法（保留 `_lastCenterCorrect` 欄位但未使用），改動時注意這是死碼還是尚待整合的功能。
- `GetBoundaryCorrection(territory, playerPos, warningDist, localRadius)`：**目前主迴圈實際使用的邊界修正**。只取 `localRadius`（15u）內的邊界點做最近距離比較，避免彎道時抓到遠處不相關的邊界段落；玩家離左邊界 < `warningDist`（5u）且比右邊界近，回傳「往右修正」，反之亦然。
- `GetPathDirection(territory, playerPos, playerRot, lookAheadDist=15, angleThreshold=12°)`：先在路徑上找「距離 <20u 且在前方（`dot>0.1`）」中分數最高（`dist - dot*5`，就近又順向優先）的最近索引，再從該索引往路徑後方走，累積距離達到 `lookAheadDist` 的點當作目標；若目標與玩家朝向夾角超過 `angleThreshold` 才回傳需要轉的方向，否則視為方向已經對齊回傳 `InValid`。
- `GetNearestMemoryGoodObjectAhead(territory, playerPos, playerRot, goodDataIds, maxDist=40u)`：在記憶庫中找「已確認、在前方 30°內、距離 3~40u」且最近的好物件，`angleDeg < 5°`（幾乎正前方）視為不需轉向刻意跳過（會自然撿到）。
- `HasConfirmedGoodObjectOnPath` / `GetUpcomingCurve`：分別是「前方走廊是否有確認好物件」與「25~50u 遠的即將到來彎道方向預測」，目前程式碼中定義了但 **`Press()` 主迴圈未直接呼叫**，屬於預留給未來擴充的工具方法，改動或清理前先確認是否真的未被引用。
- 邊界模式：`StartBoundaryRace`/`RecordBoundaryWaypoint`/`EndBoundaryRace(territory, isLeft)`，資料合併邏輯與一般路點相同（10u 合併 + 圓形平均朝向）。UI 觸發後自動連跑兩場：`BoundaryPhase` 1→2，跑完 phase 2 才把 `BoundaryMode` 關閉並歸零 phase。
- `ImportFrom(path)`：合併另一份 `track_memory.json`，物件/路點都用「取兩者 `SeenCount` 較大值」合併而非累加，避免重複匯入把數字虛灌。用於玩家之間互相分享賽道資料。
- 存檔路徑：`PluginInterface.GetPluginConfigDirectory()/track_memory.json`，`Init()` 時載入，找不到檔案或解析失敗都靜默還原成空字典（`try/catch` 吞掉例外）。

## 閃避判定 GetTargetSide()（核心決策函式）
輸入單一 `IGameObject`，回傳 `Direction`（Left/Right/Front/FrontUp/InValid）。整體邏輯：
1. 不在 Good/Bad 清單內直接 `InValid`。
2. 算距離、朝向點積（`dotProduct`，判斷是否在前方半球）、外積（`crossProduct`，判斷左右側）、角度差 `angleDeg`、高度差 `zDiff`。`zDiff < -4`（目標明顯在下方）或不在前方（`dotProduct <= 0`）一律 `InValid`（不理會背後或低處的物件）。
3. **速度自適應最大偵測距離**：`speedReach = CurrentSpeed * 0.75`（假設 0.75 秒反應時間內能到達的距離），`badBase` 依「是否確認 × 是否滿級模式」四種組合分別是 15/20/20/26u，`badMaxDist = clamp(max(speedReach, badBase), 10, 32)`——速度越快，偵測距離動態放大，但有上下限夾住。
4. **紅陷阱（2005040）/紫減速（2005039）**：
   - 距離超過 `badMaxDist` 直接 `InValid`。
   - 只有紅陷阱需要跳躍判定：`baseJumpDist` 依滿級模式是 14/20u；若當前有速度且 `JumpPeakTime > 0`，用 `CurrentSpeed * JumpPeakTime * (1 + slopeFactor)` 動態算起跳前置距離，`slopeFactor` 依陷阱與玩家的高度差（`yDiff`）除以 `JumpPeakHeight` 夾在 `[-0.5, 1]`——陷阱在上坡（`yDiff>0`）代表跳躍最高點的「有效淨空高度」會被坡度吃掉，所以要提早起跳。
   - 距離進入起跳前置範圍內且角度夠正（已確認 40°、未確認 25°）才回傳 `FrontUp`，否則一律回傳依外積決定的左右閃避（`crossProduct > 0 ? Right : Left`）——**這裡是「跳」判斷失敗後的 fallback，不是額外的 else 分支，所以紅陷阱永遠會有方向，不會出現「離很近但角度不夠、什麼都不做」的空窗**。
5. 好物件近距離：距離 <14/20u（依滿級模式）且角度 <20°時，依高度差決定 `FrontUp`（跳起來撿，`zDiff>2`）或 `Front`；距離 <22/30u 且角度 <15° 只給 `Front`（不轉向，直走過去撿）。
6. 好物件幾乎正前方（角度 <22°）視為會自然撿到，`InValid`（不用主動轉向浪費按鍵）。
7. 好物件偏側：已確認的物件轉向距離放寬到 45u，未確認維持 22/30u（依滿級模式），超出距離 `InValid`，否則依外積轉向。
8. 壞物件（怪物 3595/3596）距離超過 `badMaxDist` 一律 `InValid`，否則依外積轉向遠離。

## 修改這塊邏輯時的注意事項
- 新增/調整距離、角度常數時，同時考慮「未確認」與「已確認」兩種情況，並維持「未確認更保守、已確認後更積極」的既有設計關係，否則賽道記憶累積後行為反而會變差。
- `TrackMemory` 所有查詢方法在無資料/資料不足時要回傳 `Direction.InValid`（或 `default` tuple），呼叫端才能安全 fallback 到即時偵測或 UI 百分比；不要讓查詢方法在資料不足時拋例外或回傳誤導性的預設方向。
- `Press()` 內任何 early return / `goto`（`BoundaryMode` 短路、`goto endPress`）都要重新檢查有沒有漏掉「每幀必須執行」的記錄邏輯（速度計算、`RecordWaypoint`、跳躍錄製更新），否則賽道記憶或校正值會不完整或斷斷續續。目前邊界模式的 `return` 是在速度計算與跳躍錄製「之後」才觸發，屬於刻意設計，改動順序前務必確認。
- `GetCenterCorrection`、`HasConfirmedGoodObjectOnPath`、`GetUpcomingCurve` 目前未被 `Press()` 呼叫，屬於預留擴充或已被 `GetBoundaryCorrection`/`GetPathDirection` 取代的舊方法，清理前先全文搜尋確認真的沒有引用點（含 UI 層）。
- 修改 `MConfiguration.cs` 欄位要注意這是直接序列化存檔的 class，欄位改名/型別變更會讓舊存檔的該欄位還原成 default 而非報錯，一般不需要額外遷移邏輯，但要意識到玩家舊存檔會悄悄「重設」某些值（例如 `JumpPeakTime`/`JumpPeakHeight` 這類靠執行期累積校正的欄位，改名後等於歸零重新學習）。
- `TrackMemory.Save()` 每次 `EndRace`/`EndBoundaryRace`/`ImportFrom` 都會整份 `_db` 重新序列化寫檔，賽道數量、物件數量大量增加後注意 I/O 成本；目前寫法是同步阻塞寫檔，若之後要優化建議做成非同步或節流。
- UI（`MainWindow.cs` 賽道分頁）畫的路線圖把世界座標旋轉 90° 顯示（`W2C` 轉換：`maxZ - wz` 當畫布 X、`wx - minX` 當畫布 Y），新增任何疊加圖層（例如新增物件類型的圖示）都要套用同一個 `W2C` 轉換，否則會跟既有路線/邊界對不齊。
