# 給看這個 fork 的人(包括 upstream 維護者)

這份文件標註 `tw-fix` 分支上,哪些是**通用 bug 修正**(歡迎參考/採用),哪些是**只適用我個人環境的設定**(請不要照抄,會壞掉或沒有意義)。

## 🟢 通用修正,歡迎參考

這些是實際遊玩中發現的 bug,邏輯上跟任何人的環境都無關:

- `AutoDuty/Paths/(172) The Aurum Vale.json` — 兩段走廊 `StopForCombat` 拖怪修正
- `AutoDuty/Paths/(1037) The Tam-Tara Deepcroft.json` — `Interactable` 參數填錯(DataId 誤填成物件名稱字串)
- `AutoDuty/Paths/(243) The Binding Coil of Bahamut - Turn 3.json` — 路徑卡頓,整份換成原始上游版本
- `AutoDuty/Managers/ActionsManager.cs` —
  - `dataIds.All(x => x.Equals("0"))` 型別防呆失效(`List<uint>` 跟字面量 `string` 比,永遠 false)
  - `BossLoot()` 從沒真的打開過寶箱
- `AutoDuty/AutoDuty.cs` —
  - `StageMoving()` 的 `PathAction.Equals("Boss")` 漏寫 `.Name`,dead code
  - `StageWaitingForCombat()` 沒有重新武裝 BossMod AI 的邏輯,進戰鬥後可能卡住不出手
  - `CheckFinishing`/`DoneNavigating`/`InteractableCheck` 忘記清空 `Action`,白等 60 秒逃生口
- `AutoDuty/IPC/IPCSubscriber.cs`、`AutoDuty/Helpers/AutoEquipHelper.cs` — `GetRecommendationsForGearset` 的 `SourceInventorySlot` 型別 `int?`/`byte?` 對不上,**目前 `origin/tc-7.20` HEAD 本身編譯不過**,建議優先採用
- `AutoDuty/Managers/ContentPathsManager.cs` — 同一副本有多個路徑檔時,預設路徑改選版本最高的,而不是掃到的第一個
- `AutoDuty/Windows/Config.cs` + `AutoDuty/AutoDuty.cs` — 新增「強制只用 BossMod AutoRotation」開關(`ForceBossModAutoRotation`)。這個是新功能不是 bug fix,WrathCombo/RotationSolver 不會讀王模組的 `AIHints.Priority`,只有 BossMod 自己的 AutoRotation 會讀

上面這些已經整理成兩條乾淨分支,方便直接拿去比對或 cherry-pick:

- `upstream-pr-bugfixes` — 前面列的 bug 修正(不含新功能)
- `upstream-pr-force-bmr-autorotation` — 「強制只用 BossMod AutoRotation」開關

## 🔴 個人化設定,不要照抄

這些只對我自己的環境/發布管道有意義,套到別的地方會壞掉或沒作用:

- `AutoDuty/Updater/GitHubHelper.cs` 的 `PathRepoBaseUrl` — 指向我自己的 `okaminico/AutoDuty-1@tw-fix`,不是給別人抓路徑檔用的
- 根目錄 `repo.json` — 我自己的 Dalamud 外掛倉庫發布清單(版號、下載連結都是我自己的 release)
- `.github/workflows/sync-upstream.yml`、其版號公式修正 commit — 我自己的每日自動同步/發版 CI,不是 `origin` 的東西
- 所有「Point repo.json at twfixN release」「Auto-sync upstream + republish」類的 commit — 我自己發布流程留下的紀錄
- `.github/workflows/build-check.yml` 補上 `pull_request` 觸發 — 這個例外,是給 `origin` 用的通用 CI 改善(PR 分支合併前也該跑建置檢查),不算個人化,但因為改的是 workflow 檔案,列在這裡提醒一下

## 為什麼有這份文件

`tw-fix` 是我實際遊玩用的完整分支,個人化設定跟通用修正混在一起。如果你是從這裡比對抓修正,麻煩先看這份清單,只挑「通用修正」那些,不要整個分支直接套用。
