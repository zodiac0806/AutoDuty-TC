# 給看這個 fork 的人(包括 upstream 維護者)

這份文件標註 `tw-fix` 分支上,哪些是**通用 bug 修正**(歡迎參考/採用),哪些是**只適用我個人環境的設定**(請不要照抄,會壞掉或沒有意義)。

**最後核對:2026-09-17,對照 `origin/tc-7.20` 的 `d22ddb29`,而且 `tw-fix` 已經合併到那一顆(零落後)。**
底下的「已採納 / 未採納」是逐項比對上游現況得到的,不是憑印象。

## 🟢 通用修正

### 已經在上游了 ✅

這些當初列在這裡請上游參考,現在 `origin/tc-7.20` 已經有等價的內容(不管是採納了這裡的版本,還是上游自己修的)。**不用再看,留著只是記錄。**

- `AutoDuty/AutoDuty.cs` — `StageMoving()` 的 `PathAction.Equals("Boss")` 漏寫 `.Name`(dead code)
- `AutoDuty/AutoDuty.cs` — `StageWaitingForCombat()` 沒有重新武裝 BossMod AI,進戰鬥後卡住不出手(上游有同樣的程式碼,這邊只多了中文註解)
- `AutoDuty/AutoDuty.cs` — `DoneNavigating` 忘記清空 `Action`,白等 60 秒逃生口
- `AutoDuty/Managers/ActionsManager.cs` — `dataIds.All(x => x.Equals("0"))` 型別防呆失效(`List<uint>` 跟字面量 `string` 比,永遠 false)
- `AutoDuty/Managers/ActionsManager.cs` — `BossLoot()` 從沒真的打開過寶箱
- `AutoDuty/Helpers/ExitDutyHelper.cs` — 已經顯示的 `ContentsFinderMenu` 還一直 `Show()`;以及 `Exit()` 的診斷 log(整個檔案現在與上游逐字相同)
- `AutoDuty/Helpers/DeathHelper.cs`、`AutoDuty/Helpers/ObjectHelper.cs` — 死亡重生炸例外(同上,與上游相同)
- `AutoDuty/IPC/IPCSubscriber.cs`、`AutoDuty/Helpers/AutoEquipHelper.cs` — `GetRecommendationsForGearset` 的 `SourceInventorySlot` 型別。**上游在 `4dedf612`(2026-09-06)就修好了**,連 `ECommons.IPC` 指標一起 repin;反而是 `tw-fix` 一直沒跟上,自己編不過,2026-09-17 才補齊
- `AutoDuty/Paths/(1037) The Tam-Tara Deepcroft.json` — `Interactable` 參數填錯(DataId 誤填成物件名稱字串)。上游不但修了,還把檔名正規化成 `(1037) The TamTara Deepcroft.json`、把修正擴大到更多步驟
- `AutoDuty/Paths/(172) The Aurum Vale.json` — 兩段走廊 `StopForCombat` 拖怪修正(`StopForCombat` 的值已經一致,只剩 `Note` 措辭不同)
- `AutoDuty/Paths/(243) The Binding Coil of Bahamut - Turn 3.json` — 現在與上游相同。這邊曾經整份換成 awgil 原始版(87 步、小寫 key)想處理卡路徑,2026-09-17 決定改回採用 `origin/tc-7.20` 的版本(70 步、PascalCase),不再維護自己的分岔

### 還沒進上游,歡迎參考 🔎

- `AutoDuty/Managers/ActionsManager.cs` — 7 處 Action 收尾忘記清空 `Plugin.Action`,跟上面那個 `DoneNavigating` 是同一個 bug 的另一半;上游只修了 `AutoDuty.cs` 那半
- `AutoDuty/Managers/ContentPathsManager.cs` — 同一副本有多個路徑檔時,預設路徑改選版本最高的,而不是掃到的第一個
- `AutoDuty/AutoDuty.cs` + `AutoDuty/IPC/IPCSubscriber.cs` + `AutoDuty/Resources/AutoDuty.json` — 「強制只用 BossMod AutoRotation」這個開關的三個後續修正:
  - 沒跟 `AutoManageBossModAISettings` 連動,兩個設定錯開時會變成 Wrath/RSR 被關掉、BossMod preset 又沒送出去,全程零技能
  - 強制模式下啟用的是 `AutoDuty` preset,但 `SetPositional()` 只寫 `AutoDuty Passive`,而且 `AutoDuty` preset 裡根本沒有 `GoToPositional` 模組 —— 近戰整場王戰不繞側背,完全靜默
  - `SetAutoMode(false)` 為了關一個本來就關著的 Wrath 而去拿租約(`Register()` 失敗的副作用是把 `AutoManageRotationPluginState` 關掉存檔)

### 新功能(不是 bug fix)

- `AutoDuty/Windows/Config.cs` + `AutoDuty/AutoDuty.cs` — 「強制只用 BossMod AutoRotation」開關(`ForceBossModAutoRotation`)。WrathCombo/RotationSolver 不會讀王模組的 `AIHints.Priority`,只有 BossMod 自己的 AutoRotation 會讀
  - 也適用原版 BossMod(非 Reborn):IPC 端點名一致,preset 裡的模組原版都有,`MiscAI.AutoFarm` 會被原版自己的 preset converter 轉成 `MiscAI.AutoTarget`。只有 `AI.SetPreset` 那組是 BossModReborn 專有,但原版不需要它

### 想 cherry-pick 的話

- `upstream-pr-bugfixes` — 早期那批 bug 修正
- `upstream-pr-force-bmr-autorotation` — 「強制只用 BossMod AutoRotation」開關

⚠️ **這兩條分支停在 2026-09-14,不含上面「還沒進上游」清單裡 2026-09-17 之後補的三顆修正**,也不含 `Resources/AutoDuty.json` 的 `GoToPositional` 改動。要完整的請直接看 `tw-fix`。

## 🔴 個人化設定,不要照抄

這些只對我自己的環境/發布管道有意義,套到別的地方會壞掉或沒作用:

- `AutoDuty/Updater/GitHubHelper.cs` 的 `PathRepoBaseUrl` — 指向我自己的 fork,不是給別人抓路徑檔用的
- 根目錄 `repo.json` — 我自己的 Dalamud 外掛倉庫發布清單(版號、下載連結都是我自己的 release)
- `.github/workflows/sync-tc-upstream.yml`、`.github/workflows/sync-customize.yml` — 我自己的每日自動同步/發版 CI(原本是一份 `sync-upstream.yml`,後來拆成兩份),不是 `origin` 的東西
- 所有「Point repo.json at twfixN release」「Auto-sync upstream + republish」類的 commit — 我自己發布流程留下的紀錄
- `.github/workflows/build-check.yml` 補上 `pull_request` 觸發、以及觸發分支加上 `tw-fix`/`customize` — 這兩個例外,是通用的 CI 改善(PR 合併前也該跑建置檢查;只列上游鏡像分支的話這份檢查在個人 fork 上永遠不會跑),不算個人化,但因為改的是 workflow 檔案,列在這裡提醒一下

## 為什麼有這份文件

`tw-fix` 是我實際遊玩用的完整分支,個人化設定跟通用修正混在一起。如果你是從這裡比對抓修正,麻煩先看這份清單,只挑「還沒進上游」那些,不要整個分支直接套用。
