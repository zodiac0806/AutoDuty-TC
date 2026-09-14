using AutoDuty.Data;
using AutoDuty.Helpers;
using AutoDuty.IPC;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.LegacyTaskManager;
using ECommons.DalamudServices;
using ECommons.GameFunctions;
using ECommons.Throttlers;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using static AutoDuty.Helpers.ObjectHelper;
using static AutoDuty.Helpers.PlayerHelper;

namespace AutoDuty.Managers
{
    using System.Xml;

    internal class ActionsManager(AutoDuty _plugin, TaskManager _taskManager)
    {
        public readonly List<(string, string, string)> ActionsList =
        [
            ("<-- Comment -->","comment?","Adds a Comment to the path; AutoDuty will do nothing but display them.\nExample: <-- Trash Pack #1 -->"),
            ("Wait","how long?", "Adds a Wait (for x milliseconds) step to the path; after moving to the position, AutoDuty will wait x milliseconds.\nExample: Wait|0.02, 23.85, -394.89|8000"),
            ("WaitFor","for?","Adds a WaitFor (Condition) step to the path; after moving to the position, AutoDuty will wait for a condition from the following list:\nCombat - waits until in combat\nIsReady - waits until the player is ready\nIsValid - waits until the player is valid\nIsOccupied - waits until the player is occupied\nBNpcInRadius - waits until a battle npc either spawns or path's into the radius specified\nExample: WaitFor|-12.12, 18.76, -148.05|Combat"),
            ("Boss","false", "Adds a Boss step to the path; after (and while) moving to the position, AutoDuty will attempt to find the boss object. If not found, AD will wait 10s at the position for the boss to spawn and will then Invoke the Boss Action.\nExample: Boss|-2.91, 2.90, -204.68|"),
            ("Interactable","interact with?", "Adds an Interactable step to the path; after moving to within 2y of the position, AutoDuty will interact with the object specified (recommended to input DataId) until either the object is no longer targetable, you meet certain conditions, or a YesNo/Talk addon appears.\nExample: Interactable|21.82, 7.10, 27.40|1004346 (Goblin Pathfinder)"),
            ("TreasureCoffer","false", "Adds a TreasureCoffer flag to the path; AutoDuty will loot any treasure coffers automatically if it gets within interact range of one (while Config Loop Option is on), this is just a flag to mark the positions of Treasure Coffers.\nNote: AutoDuty will ignore this Path entry when Looting is disabled entirely or Boss Loot Only is enabled.\nExample: TreasureCoffer|3.21, 6.06, -97.63|"),
            ("SelectYesno","yes or no?", "Adds a SelectYesNo step to the path; after moving to the position, AutoDuty will click Yes or No on this addon.\nExample: SelectYesno|9.41, 1.94, -311.25|Yes"),
            ("SelectString", "list index", "Adds a SelectString step to the path; after moving to the position, AutoDuty will pick the indexed string.\nExample: SelectYesno|908.24, 327.26, -561.96|1"),
            ("MoveToObject","Object Name?", "Adds a MoveToObject step to the path; AutoDuty will will move the object specified (recommend input DataId)"),
            ("DutySpecificCode","step #?", "Adds a DutySpecificCode step to the path; after moving to the position, AutoDuty will invoke the Duty Specific Action for this TerritoryType and the step # specified.\nExample: DutySpecificCode|174.68, 102.00, -66.46|1"),
            ("BossMod", "on / off", "Adds a BossMod step to the path; after moving to the position, AutoDuty will turn BossMod on or off.\nExample: BossMod|-132.08, -342.25, 1.98|Off"),
            ("Rotation", "on / off", "Adds a Rotation step to the path; after moving to the position, AutoDuty will turn Rotation Plugin on or off.\nExample: Rotation|-132.08, -342.25, 1.98|Off"),
            ("Target", "Target what?", "Adds a Target step to the path; after moving to the position, AutoDuty will Target the object specified (recommend inputing DataId)."),
            ("AutoMoveFor", "how long?", "Adds an AutoMoveFor step to the path; AutoDuty will turn on Standard Mode and Auto Move for the time specified in milliseconds (or until player is not ready).\nExample: AutoMoveFor|-18.21, 1.61, 114.16|3000"),
            ("ChatCommand","Command with args?", "Adds a ChatCommand step to the path; after moving to the position, AutoDuty will execute the Command specified.\nExample: ChatCommand|-5.86, 164.00, 501.72|/bmrai follow Alisaie"),
            ("StopForCombat","true/false", "Adds a StopForCombat step to the path; after moving to the position, AutoDuty will turn StopForCombat on or off.\nExample: StopForCombat|-1.36, 5.76, -108.78|False"),
            ("Revival", "false", "Adds a Revive flag to the path; this is just a flag to mark the positions of Revival Points, AutoDuty will ignore this step during navigation.\nUse this if the Revive Teleporter does not take you directly to the arena of the last boss you killed, such as Sohm Al.\nExample: Revival|33.57, -202.93, -70.30|"),
            ("ForceAttack",  "false", "Adds a ForceAttack step to the path; after moving to the position, AutoDuty will ForceAttack the closest mob.\nExample: ForceAttack|-174.24, 6.56, -301.67|"),
            ("Jump", "automove for how long before", "Adds a Jump step to the path; after AutoMoving, AutoDuty will jump.\nExample: Jump|0, 0, 0|200"),
            //("PausePandora", "Which feature | how long"),
            ("CameraFacing", "Face which Coords?", "Adds a CameraFacing step to the path; after moving to the position, AutoDuty will face the coordinates specified.\nExample: CameraFacing|720.66, 57.24, 9.18|722.05, 62.47, 15.55"),
            ("ClickTalk", "false", "Adds a ClickTalk step to the path; after moving to the position, AutoDuty will click the talk addon."),
            ("ConditionAction","condition;args,action;args", "Adds a ConditionAction step to the path; after moving to the position, AutoDuty will check the condition specified and invoke Action."),
            ("ModifyIndex", "which step (+/- for relative)", "Adds a ModifyIndex step to the path; after moving to the position, AutoDuty will modify the index. A leading + or - makes it relative to this step (-1 redoes the previous step); otherwise it is an absolute 0-based index.\nExample: ModifyIndex|0, 0, 0|-1"),
            ("KillInRange", "Range", "Adds a KillInRange step to the path; AutoDuty will target and kill every hostile battle NPC within the specified range of the step position, then move on.\nExample: KillInRange|-12.12, 18.76, -148.05|15"),
            ("SelectJournalResult", "accept? (true/false)", "Adds a SelectJournalResult step to the path; after moving to the position, AutoDuty will accept (or decline) the JournalResult window.\nExample: SelectJournalResult|0, 0, 0|true"),
            ("JumpTo", "jump where? | how long before jump?", "Adds a JumpTo step to the path; AutoDuty will move towards the point without using the navmesh and then jump.\nExample: JumpTo|0, 0, 0|-12.12, 18.76, -148.05;500"),
            ("Action", "ActionType | Action ID", "Adds an Action step to the path; after moving to the position, AutoDuty will wait until the action is ready and then use it.\nExample: Action|0, 0, 0|Action;23282"),
            ("BLULoad", "enable? | which spell (Blue Magic Spellbook No.)", "Adds a BLULoad step to the path; when playing Blue Mage, AutoDuty will slot the specified spell in or out of the current loadout.\nExample: BLULoad|0, 0, 0|true;11")
        ];

        public void InvokeAction(PathAction action)
        {
            try
            {
                if (action != null)
                {
                    var thisType = GetType();
                    var actionTask = thisType.GetMethod(action.Name) ?? ResolveActionIgnoreCase(thisType, action.Name);
                    _taskManager.Enqueue(() => actionTask?.Invoke(this, [action]), $"InvokeAction-{actionTask?.Name}");
                }
                else
                    Svc.Log.Error("no action");
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex.ToString());
            }
        }

        /// <summary>
        /// 動作名稱大小寫不符時的退路。上游的路徑檔裡確實有寫成 <c>Bossmod</c>(小寫 m)的步驟,
        /// 而反射查方法是大小寫敏感的 ⇒ 那些步驟在上游與我方都是靜默地什麼都不做。
        /// 這裡只在精確比對失敗時才啟用,並且要求方法簽章正好是 (PathAction),
        /// 免得誤中 object 繼承來的 ToString/Equals 之類。
        /// </summary>
        private static System.Reflection.MethodInfo? ResolveActionIgnoreCase(Type thisType, string name)
        {
            if (name.IsNullOrEmpty())
                return null;

            System.Reflection.MethodInfo? candidate = thisType
                                                      .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                                                      .FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                                                                           m.GetParameters() is [{ } p] && p.ParameterType == typeof(PathAction));

            if (candidate != null)
                Svc.Log.Information($"路徑動作「{name}」大小寫與實作不符,已改用「{candidate.Name}」執行。");

            return candidate;
        }

        public void Follow(PathAction action) => FollowHelper.SetFollow(GetObjectByName(action.Arguments[0]));

        public void SetBMSettings(PathAction action) => Plugin.SetBMSettings(bool.TryParse(action.Arguments[0], out bool defaultsettings) && defaultsettings);

        public unsafe void ConditionAction(PathAction action)
        {
            var conditionActionArray = action.Arguments.ToArray();
            // There are 4 paths that uses conditionaction before the argument array was split, 
            // so we need to handle that case until they can be modified to use properly split arguments and retested
            if (action.Arguments.Count == 0) return;
            if (action.Arguments.Count == 1)
            {
                if (!action.Arguments[0].Any(x => x.Equals('&'))) return;

                conditionActionArray = action.Arguments[0].Split("&");
            }
            Plugin.Action = $"ConditionAction: {conditionActionArray[0]}, {conditionActionArray[1]}";
            var condition = conditionActionArray[0];
            string[] conditionArray = [];
            if (condition.Any(x => x.EqualsAny(';')))
                conditionArray = condition.Split(";");
            var actions = conditionActionArray[1];
            string[] actionArray = [];
            if (actions.Any(x => x.EqualsAny(';')))
                actionArray = actions.Split(";");
            var invokeAction = false;
            var operation = new Dictionary<string, Func<object, object, bool>>
                            {
                                { ">", (x,  y) => Convert.ToSingle(x) > Convert.ToSingle(y) },
                                { ">=", (x, y) => Convert.ToSingle(x) >= Convert.ToSingle(y) },
                                { "<", (x,  y) => Convert.ToSingle(x) < Convert.ToSingle(y) },
                                { "<=", (x, y) => Convert.ToSingle(x) <= Convert.ToSingle(y) },
                                { "==", (x, y) => x                   == y },
                                { "!=", (x, y) => x                   != y }
                            };
            var operatorValue = string.Empty;
            var operationResult = false;

            switch (conditionArray[0])
            {
                case "GetDistanceToPlayer":
                    {
                        if (conditionArray.Length < 4) return;
                        if (!conditionArray[1].TryGetVector3(out var vector3)) return;
                        if (!float.TryParse(conditionArray[3], out var distance)) return;
                        if (!(operatorValue = conditionArray[2]).EqualsAny(operation.Keys)) return;
                        var getDistance = GetDistanceToPlayer(vector3);
                        if (operationResult = operation[operatorValue](getDistance, distance))
                            invokeAction = true;
                        Svc.Log.Info($"Condition: {getDistance}{operatorValue}{distance} = {operationResult}");
                        break;
                    }
                case "ObjectDistanceToPoint":
                    {
                        if (conditionArray.Length < 5) return;
                        if (!conditionArray[2].TryGetVector3(out var vector3)) return;
                        if (!float.TryParse(conditionArray[4], out var distance)) return;
                        if (!(operatorValue = conditionArray[3]).EqualsAny(operation.Keys)) return;
                        IGameObject? targetObject = null;
                        if ((targetObject = GetObjectByDataId(uint.TryParse(conditionArray[1], out uint dataId) ? dataId : 0)) == null) return;
                        var getDistance = Vector3.Distance(vector3, targetObject.Position);
                        if (operationResult = operation[operatorValue](getDistance, distance))
                            invokeAction = true;
                        Svc.Log.Info($"Condition: {getDistance}{operatorValue}{distance} = {operationResult}");
                        break;
                    }
                case "ItemCount":
                    if (conditionArray.Length < 4) return;
                    if (!uint.TryParse(conditionArray[1], out var itemId)) return;
                    if (!uint.TryParse(conditionArray[3], out var quantity)) return;
                    if (!operation.TryGetValue(operatorValue = conditionArray[2], out var operationFunc)) return;
                    var itemCount = InventoryHelper.ItemCount(itemId);
                    if (operationResult = operationFunc(itemCount, quantity))
                        invokeAction = true;
                    Svc.Log.Info($"Condition: {itemCount}{operatorValue}{quantity} = {operationResult}");
                    break;
                case "ObjectData":
                    if (conditionArray.Length > 3)
                    {
                        IGameObject? gameObject = null;
                        if ((gameObject = GetObjectByDataId(uint.TryParse(conditionArray[1], out uint dataId) ? dataId : 0)) != null)
                        {
                            var csObj = *gameObject.Struct();
                            switch (conditionArray[2])
                            {
                                case "EventState":
                                    if (csObj.EventState == (int.TryParse(conditionArray[3], out int es) ? es : -1))
                                        invokeAction = true;
                                    break;
                                case "IsTargetable":
                                    if (csObj.GetIsTargetable() == (bool.TryParse(conditionArray[3], out bool it) && it))
                                        invokeAction = true;
                                    break;
                            }
                        }
                    }
                    break;
            }
            if (invokeAction)
            {
                var actionActual = actionArray[0];
                string actionArguments = actionArray.Length > 1 ? actionArray[1] : "";
                Svc.Log.Debug($"ConditionAction: Invoking Action: {actionActual} with Arguments: {actionArguments}");
                InvokeAction(new PathAction() { Name = actionActual, Arguments = [actionArguments] });
            }
        }

        public void BossMod(PathAction action)
        {
            BossMod_IPCSubscriber.SetMovement(action.Arguments[0].Equals("on", StringComparison.InvariantCultureIgnoreCase));
        }

        /// <summary>
        /// 路徑檔的 ModifyIndex 步驟。引數開頭是 <c>+</c> 或 <c>-</c> 時是<b>相對</b>位移,
        /// 否則才是絕對索引(0 起算)。這是上游語意。
        /// 🔴 本 fork 先前一律當成絕對索引 —— 而 <c>int.TryParse("-1")</c> / <c>TryParse("+3")</c>
        ///    都會成功,所以失敗形式是<b>靜默跳到錯的步驟</b>而不是例外。`-1` 更會把 Indexer 設成 -1,
        ///    使 <c>StageReadingPath()</c> 開頭的 <c>Indexer == -1</c> 前置檢查每幀直接 return ⇒ 整條路徑停住。
        ///    2026-08-13 稽核:326 個路徑檔的 80 個 ModifyIndex 用法<b>全部</b>是相對語意。
        /// </summary>
        public void ModifyIndex(PathAction action)
        {
            if (!int.TryParse(action.Arguments[0], out int _index)) return;
            ModifyIndex(_index, action.Arguments[0][0] is '+' or '-');
        }

        /// <summary>
        /// 相對位移的基準點是「ModifyIndex 這一步自己的索引」。時序:
        /// 本方法是以 TaskManager 任務的身分執行的,而從 <c>Stage.Action</c> 被設定(setter 裡呼叫
        /// <c>ActionInvoke()</c> 把任務排進佇列)到任務真的執行為止,沒有任何地方動過 <c>Plugin.Indexer</c>
        /// —— <c>StageAction()</c> 的 <c>Indexer++</c> 要 <c>!TaskManager.IsBusy</c> 才會跑,此刻佇列還沒空。
        /// 而本方法結尾把 Stage 改回 <c>Reading_Path</c>,之後 <c>StageAction()</c> 再也不會被派送到
        /// ⇒ 那個 <c>Indexer++</c> 不會補套上來。所以 <c>-1</c> 就是「退回前一步重做」。與上游行為一致。
        /// </summary>
        private void ModifyIndex(int index, bool modify)
        {
            int before = Plugin.Indexer;
            if (modify)
                Plugin.Indexer += index;
            else
                Plugin.Indexer = index;
            // 使用者跑 LogLevel 1(盲區只有 Verbose),診斷一律 Information。ModifyIndex 步驟很少,不會洗版。
            Svc.Log.Information($"ModifyIndex: {before} -> {Plugin.Indexer} (arg={index}, relative={modify})");
            Plugin.Stage = Stage.Reading_Path;
        }

        private bool _autoManageRotationPluginState = false;
        public void Rotation(PathAction action)
        {
            if (action.Arguments[0].Equals("off", StringComparison.InvariantCultureIgnoreCase))
            {
                if (Plugin.Configuration.AutoManageRotationPluginState)
                {
                    _autoManageRotationPluginState = true;
                    Plugin.Configuration.AutoManageRotationPluginState = false;
                }
                Plugin.SetRotationPluginSettings(false, true);
            }
            else if (action.Arguments[0].Equals("on", StringComparison.InvariantCultureIgnoreCase))
            {
                if (_autoManageRotationPluginState)
                    Plugin.Configuration.AutoManageRotationPluginState = true;

                Plugin.SetRotationPluginSettings(true, true);
            }
        }

        public void StopForCombat(PathAction action)
        {
            if (!Player.Available)
                return;

            var boolTrueFalse = action.Arguments[0].Equals("true", StringComparison.InvariantCultureIgnoreCase);
            Plugin.Action = $"StopForCombat: {action.Arguments[0]}";
            Plugin.StopForCombat = boolTrueFalse;
            _taskManager.Enqueue(() => BossMod_IPCSubscriber.SetMovement(boolTrueFalse), "StopForCombat");
            if(boolTrueFalse && (action.Arguments.Count <= 1 || action.Arguments[1] != "noWait"))
                this.Wait(new PathAction {Arguments = ["500"]});
        }

        /// <summary>
        /// ForceAttack 期間被暫時關掉的 BossMod「自動攻擊管理」原值;null = 目前沒有暫停中。
        /// </summary>
        private bool? _bossModAutoAutosSuspendedFrom;

        /// <summary>ForceAttack 後備目標的搜尋半徑(公尺),只在遊戲的切換敵人指令沒鎖到目標時才會用到。</summary>
        private const float ForceAttackFallbackRadius = 30f;

        /// <summary>
        /// ForceAttack:停在定點主動打掉擋路的敵人,進入戰鬥後才往下走。
        /// </summary>
        /// <remarks>
        /// 🔴 2026-08-30 由使用者實機 log 確認的根因(與台服在地化無關,國際服同樣會中):
        /// 這一步靠「一般動作 1(自動攻擊)」起手,但 BossModReborn 的「自動攻擊管理」
        /// (<c>ActionTweaksConfig.AutoAutos</c>)hook 了 <c>SetAutoAttackState</c>,
        /// 而 <c>AutoAutosTweak.GetDesiredState</c> 的最後一行是
        /// <code>return player.InCombat || ws.Client.CountdownRemaining &lt;= PrePullThreshold;</code>
        /// <c>CountdownRemaining</c> 的型別是 <c>float?</c>,沒有倒數計時的時候是 null,
        /// 而 C# 的提升比較讓 <c>null &lt;= 0.5f</c> 恆為 false ⇒
        /// 只要「不在戰鬥中而且沒有倒數」,啟動自動攻擊就一律被否決
        /// (BossModReborn 會 log <c>[AMEx] Prevented starting autoattacks</c>)。
        /// ForceAttack 的前提正好就是「不在戰鬥中」,所以自動攻擊永遠送不出去、
        /// <c>InCombat</c> 永遠不會變 true,整步就退化成純粹等 tot 毫秒 ——
        /// 這就是使用者回報的「沒有自動攻擊擋路的障礙物,只有等時間」。
        /// (實機 log 的六次 ForceAttack 全部剛好逾時 10.01 秒,其中五次伴隨上述 AMEx 訊息;
        ///  另外兩次是連目標都沒鎖到 —— 500 毫秒的等目標那一段直接逾時。)
        ///
        /// 對應的修法有兩件:
        /// (1) 這一步期間暫時把 BossMod 的 AutoAutos 關掉,做完立刻還原;
        /// (2) 遊戲的切換敵人指令沒鎖到目標時,自己掃物件表補一個最近的可攻擊敵人。
        /// 另外把目標與逾時狀況寫成 Information 級 log,下次看 log 就能直接判是哪一段沒過。
        /// </remarks>
        public unsafe void ForceAttack(PathAction action)
        {
            var tot = action.Arguments[0].IsNullOrEmpty() ? 10000 : int.TryParse(action.Arguments[0], out int time) ? time : 0;
            if (action.Arguments[0].IsNullOrEmpty())
                action.Arguments[0] = "10000";

            _taskManager.Enqueue(() => SuspendBossModAutoAutos(), "ForceAttack-SuspendAutoAutos");
            _taskManager.Enqueue(() => ActionManager.Instance()->UseAction(ActionType.GeneralAction, 16), "ForceAttack-GA16");
            _taskManager.Enqueue(() => Svc.Targets.Target != null, 500, "ForceAttack-WaitForTarget");
            _taskManager.Enqueue(() => ForceAttackAcquireTarget(), "ForceAttack-AcquireTarget");
            _taskManager.Enqueue(() => ActionManager.Instance()->UseAction(ActionType.GeneralAction, 1), "ForceAttack-GA1");
            _taskManager.Enqueue(() => InCombat, tot, "ForceAttack-WaitForCombat");
            _taskManager.Enqueue(() => ForceAttackFinish(tot), "ForceAttack-Finish");
        }

        /// <summary>
        /// 讀 BossMod 的「自動攻擊管理」開關。讀不到(BossMod 沒載入、欄位改名、IPC 失敗)一律回 null,
        /// 呼叫端就當作「不要動它」。
        /// 🔑 這裡刻意拿「只有真的解析得出 bool 才算數」當校準閘門 —— BossMod 的 ConsoleCommand 在
        /// 找不到設定型別或欄位時回傳的是多行說明文字,Count != 1 就會被這個條件擋掉,
        /// 不會把「查不到」誤讀成「值是 false」。
        /// </summary>
        private static bool? GetBossModAutoAutos()
        {
            if (!BossMod_IPCSubscriber.IsEnabled)
                return null;

            List<string>? result = BossMod_IPCSubscriber.Configuration(["ActionTweaks", "AutoAutos"], false);

            return result is { Count: 1 } && bool.TryParse(result[0], out bool value) ? value : null;
        }

        /// <summary>設定 BossMod 的「自動攻擊管理」,並回報有沒有真的改成功。</summary>
        private static bool SetBossModAutoAutos(bool value)
        {
            if (!BossMod_IPCSubscriber.IsEnabled)
                return false;

            // 第二個參數 save 傳 false:只改 BossMod 記憶體裡的欄位、不觸發它把設定寫回磁碟。
            // 這樣即使 AutoDuty 中途被強制關掉來不及還原,使用者設定檔裡的值仍然是原本的,
            // BossMod 下次重載就會自己回到原狀。
            BossMod_IPCSubscriber.Configuration(["ActionTweaks", "AutoAutos", value ? "true" : "false"], false);

            return GetBossModAutoAutos() == value;
        }

        /// <summary>ForceAttack 起手前暫時關掉 BossMod 的「自動攻擊管理」(理由見 ForceAttack 的註解)。</summary>
        private bool SuspendBossModAutoAutos()
        {
            // 上一輪如果因為中止而沒還原,這裡不要拿現值去覆蓋掉記下來的原值。
            if (_bossModAutoAutosSuspendedFrom != null)
                return true;

            bool? current = GetBossModAutoAutos();

            if (current == null)
            {
                Svc.Log.Information("[ForceAttack] 讀不到 BossMod 的「自動攻擊管理」設定(BossMod 未載入或 IPC 失敗),不動它。");
                return true;
            }

            if (current == false)
                return true;

            if (SetBossModAutoAutos(false))
            {
                _bossModAutoAutosSuspendedFrom = true;
                Svc.Log.Information("[ForceAttack] 已暫時關閉 BossMod 的「自動攻擊管理」(它會否決非戰鬥中啟動自動攻擊),這一步做完立刻還原。");
            }
            else
                Svc.Log.Information("[ForceAttack] 想暫時關閉 BossMod 的「自動攻擊管理」但沒有成功,ForceAttack 很可能仍然只會等時間。");

            return true;
        }

        /// <summary>
        /// 還原先前被 ForceAttack 暫時關掉的 BossMod「自動攻擊管理」。
        /// 沒有暫停中就什麼都不做,所以可以安全地重複呼叫(收工路徑上也會叫一次當保險)。
        /// </summary>
        internal void RestoreBossModAutoAutos()
        {
            if (_bossModAutoAutosSuspendedFrom is not bool original)
                return;

            _bossModAutoAutosSuspendedFrom = null;

            if (SetBossModAutoAutos(original))
                Svc.Log.Information("[ForceAttack] 已還原 BossMod 的「自動攻擊管理」= " + original + "。");
            else
                Svc.Log.Information("[ForceAttack] 還原 BossMod 的「自動攻擊管理」失敗(想還原成 " + original + ");BossMod 重載後會回到設定檔裡的值。");
        }

        /// <summary>
        /// 確保 ForceAttack 有目標:遊戲的「從左至右切換敵人」沒鎖到人時,自己掃物件表挑最近的可攻擊敵人。
        /// </summary>
        /// <remarks>
        /// 🔴 全程在同一幀內做完 —— 掃描、挑選、設定目標之後就不再持有任何 IGameObject,
        /// 不會把原生指標留到下一幀(排隊中的任務是在後面的幀才執行的)。
        /// 判敵意刻意只用 Dalamud 自己的 <c>BattleNpcKind</c>/<c>IsTargetable</c>/<c>IsDead</c>,
        /// 不走 ECommons 那個吃寫死特徵碼的 IsHostile(),免得特徵碼在台服失效時靜默失準。
        /// </remarks>
        private bool ForceAttackAcquireTarget()
        {
            if (Svc.Targets.Target is { } existing)
            {
                Svc.Log.Information("[ForceAttack] 目標:" + existing.Name.TextValue + " (BaseId=" + existing.BaseId + ", 距離 " + GetDistanceToPlayer(existing).ToString("F1") + "m)");
                return true;
            }

            int         scanned  = 0;
            int         hostiles = 0;
            IGameObject? nearest = null;
            float       nearestDistance = float.MaxValue;

            foreach (IGameObject obj in Svc.Objects)
            {
                scanned++;

                if (obj is not IBattleNpc battleNpc
                    || battleNpc.BattleNpcKind != Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Enemy
                    || !obj.IsTargetable
                    || obj.IsDead)
                    continue;

                hostiles++;

                float distance = GetDistanceToPlayer(obj);

                if (distance > ForceAttackFallbackRadius || distance >= nearestDistance)
                    continue;

                nearest         = obj;
                nearestDistance = distance;
            }

            if (nearest == null)
            {
                Svc.Log.Information("[ForceAttack] 沒有鎖定到目標:掃了 " + scanned + " 個物件,其中 " + hostiles + " 個是可攻擊的敵人,但都不在 " + ForceAttackFallbackRadius.ToString("F0") + "m 內。");
                return true;
            }

            Svc.Log.Information("[ForceAttack] 遊戲的切換敵人指令沒鎖到目標,改鎖最近的可攻擊敵人:" + nearest.Name.TextValue + " (BaseId=" + nearest.BaseId + ", 距離 " + nearestDistance.ToString("F1") + "m)");
            Svc.Targets.Target = nearest;

            return true;
        }

        /// <summary>ForceAttack 收尾:回報這一步到底有沒有把人打起來,並還原 BossMod 的設定。</summary>
        private bool ForceAttackFinish(int timeoutMs)
        {
            if (InCombat)
                Svc.Log.Information("[ForceAttack] 已進入戰鬥。");
            else
                Svc.Log.Information("[ForceAttack] 等了 " + timeoutMs + " 毫秒仍未進入戰鬥,放棄這一步繼續往下走。");

            RestoreBossModAutoAutos();

            return true;
        }

        /// <summary>
        /// 往指定座標「不走導航網格」直線推進一段時間後起跳,再繼續推進到定點。
        /// 用在有 mesh 斷開的跳躍捷徑上(例如落雷之獄的中段出口)。
        /// </summary>
        public unsafe void JumpTo(PathAction action)
        {
            if (action.Arguments.Count == 0)
                return;

            if (!action.Arguments[0].TryGetVector3(out Vector3 position))
                return;

            Plugin.Action = $"Jumping To {action.Arguments[0]}";

            int wait = 100;
            if (action.Arguments.Count > 1 && int.TryParse(action.Arguments[1], out int parsedWait) && parsedWait > 0)
                wait = parsedWait;

            _taskManager.Enqueue(() => VNavmesh_IPCSubscriber.Path_MoveTo([position], false), "Start-JumpTo-Move");

            _taskManager.Enqueue(() => EzThrottler.Throttle("JumpTo", wait), "JumpTo-Wait");
            _taskManager.Enqueue(() => EzThrottler.Check("JumpTo"), wait, "JumpTo-Wait");

            _taskManager.Enqueue(() => ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2), "JumpTo-Jump");
            _taskManager.Enqueue(() => MovementHelper.Move(position, useMesh: false), "Finish-JumpTo-Move");
            _taskManager.Enqueue(() => Plugin.Action = "");
        }

        /// <summary>
        /// 等指定技能可用後施放一次。參數為 [ActionType, 技能 ID]。
        /// 狀態碼 573 代表「這個職業沒有這個技能」,直接跳過不等。
        /// </summary>
        public unsafe void Action(PathAction action)
        {
            if (action.Arguments.Count < 2)
                return;

            if (!Enum.TryParse(action.Arguments[0], out ActionType type))
                return;

            if (!uint.TryParse(action.Arguments[1], out uint id))
                return;

            Svc.Log.Debug($"Action: {type} {id}");

            if (ActionManager.Instance()->GetActionStatus(type, id) == 573)
                return;

            Plugin.Action = $"Action: {type} {id}";
            _taskManager.Enqueue(() => ActionManager.Instance()->GetActionStatus(type, id) == 0, "Action-WaitTillReady");
            _taskManager.Enqueue(() => ActionManager.Instance()->UseAction(type, id), "Action-UsingAction");
            _taskManager.Enqueue(() => Plugin.Action = "");
        }

        /// <summary>
        /// 青魔道士專用:把某個青魔法書編號的魔法換進/換出當前配置。
        /// 參數為 [true/false 是否放入, 青魔法書編號]。非青魔時整步不做事。
        /// </summary>
        public void BLULoad(PathAction action)
        {
            if (action.Arguments.Count < 2)
                return;

            if (GetJob() != ECommons.ExcelServices.Job.BLU)
                return;

            if (!bool.TryParse(action.Arguments[0], out bool enable))
                return;

            if (!byte.TryParse(action.Arguments[1], out byte spell))
                return;

            Plugin.Action = $"BLULoad: {(enable ? "+" : "-")}{spell}";
            _taskManager.Enqueue(() => !Svc.Condition.Any(ConditionFlag.InCombat, ConditionFlag.Casting), "BLULoad-WaitOOC");

            if (enable)
                _taskManager.Enqueue(() => BLUHelper.SpellLoadoutIn(spell), "BLULoad-In");
            else
                _taskManager.Enqueue(() => BLUHelper.SpellLoadoutOut(spell), "BLULoad-Out");

            _taskManager.Enqueue(() => Plugin.Action = "");
        }

        /// <summary>
        /// 只給 <see cref="KillInRange"/> 用的鎖定判定。
        /// 📌 與下面的 <see cref="TargetCheck"/> 判斷式相同,刻意分成兩支是為了各自持有
        /// 獨立的 <see cref="EzThrottler"/> 鍵 —— 共用同一個鍵會讓兩個呼叫點互相節流。
        /// 回傳 true＝「這個目標不用再處理了」(不可鎖定/已經是當前目標),false＝還在鎖定中。
        /// </summary>
        private bool AcquireTargetCheck(IGameObject? gameObject)
        {
            if (gameObject is not { IsTargetable: true } || !gameObject.IsValid() || (Svc.Targets.Target?.Equals(gameObject) ?? false))
                return true;

            if (EzThrottler.Check("AcquireTargetCheck"))
            {
                EzThrottler.Throttle("AcquireTargetCheck", 25);
                Svc.Targets.Target = gameObject;
            }

            return false;
        }

        /// <summary>
        /// 把步驟座標周圍指定半徑內的敵對戰鬥 NPC 逐一鎖定並打完,清空之後才往下一步。
        /// 施放什麼技能由使用者自己的循環外掛決定,這裡只負責選目標與靠近。
        /// </summary>
        public void KillInRange(PathAction action)
        {
            if (action.Arguments.Count < 1)
                return;

            if (!uint.TryParse(action.Arguments[0], out uint range))
                return;

            Plugin.Action = $"Killing in {range}y";

            // 只捕獲純量:半徑與步驟座標,不捕獲任何 IGameObject。
            Vector3 center = action.Position;

            _taskManager.Enqueue(() => BossMod_IPCSubscriber.SetMovement(true), "KillInRange-StopForCombat");

            _taskManager.Enqueue(() =>
                                 {
                                     if (!EzThrottler.Throttle("KillInRange"))
                                         return false;

                                     // 每次檢查都重新列舉物件表,不跨幀保存任何原生指標。
                                     List<IGameObject> gameObjects = Svc.Objects.Where(igo => igo is { ObjectKind: Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc, IsTargetable: true } &&
                                                                                              igo.IsHostile() &&
                                                                                              BelowDistanceToPoint(igo.Position, center, range, range / 2f))
                                                                                .ToList();

                                     if (gameObjects.Count == 0)
                                         return true;

                                     IGameObject? current = Svc.Targets.Target;
                                     if (current != null && gameObjects.Contains(current))
                                     {
                                         if (GetDistanceToPlayer(current) < 30)
                                             VNavmesh_IPCSubscriber.Path_Stop();
                                         return false;
                                     }

                                     IGameObject target = gameObjects.OrderBy(GetDistanceToPlayer).First();

                                     if (AcquireTargetCheck(target) && GetDistanceToPlayer(target) < 30)
                                         VNavmesh_IPCSubscriber.Path_Stop();
                                     else
                                         VNavmesh_IPCSubscriber.SimpleMove_PathfindAndMoveTo(target.Position, false);

                                     return false;
                                 }, int.MaxValue, "KillInRange-Main");

            _taskManager.Enqueue(() =>
                                 {
                                     // 副本本身不是「戰鬥時停下」模式的話,把 BossMod 的移動控制還回去。
                                     if (!Plugin.StopForCombat)
                                         BossMod_IPCSubscriber.SetMovement(false);
                                 }, "KillInRange-RestoreMovement");
            _taskManager.Enqueue(() => Plugin.Action = "");
        }

        public unsafe void Jump(PathAction action)
        {
            Plugin.Action = $"Jumping";

            if (int.TryParse(action.Arguments[0], out int wait) && wait > 0)
            {
                _taskManager.Enqueue(() => Chat.ExecuteCommand("/automove on"), "Jump");
                _taskManager.Enqueue(() => EzThrottler.Throttle("AutoMove", Convert.ToInt32(wait)), "Jump");
                _taskManager.Enqueue(() => EzThrottler.Check("AutoMove"), Convert.ToInt32(wait), "Jump");
            }

            _taskManager.Enqueue(() => ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2), "Jump");

            if (wait > 0)
            {
                _taskManager.Enqueue(() => EzThrottler.Throttle("AutoMove", Convert.ToInt32(100)), "Jump");
                _taskManager.Enqueue(() => EzThrottler.Check("AutoMove"), Convert.ToInt32(100), "AutoMove");
                _taskManager.Enqueue(() => Chat.ExecuteCommand("/automove off"), "Jump");
            }
        }

        public void ChatCommand(PathAction action)
        {
            if (!Player.Available)
                return;
            Plugin.Action = $"ChatCommand: {action.Arguments[0]}";
            _taskManager.Enqueue(() => Chat.ExecuteCommand(action.Arguments[0]), "ChatCommand");
            _taskManager.Enqueue(() => Plugin.Action = "");
        }

        public void AutoMoveFor(PathAction action)
        {
            if (!Player.Available)
                return;
            Plugin.Action = $"AutoMove For {action.Arguments[0]}";
            var movementMode = Svc.GameConfig.UiControl.TryGetUInt("MoveMode", out var mode) ? mode : 0;
            _taskManager.Enqueue(() => { if (movementMode == 1) Svc.GameConfig.UiControl.Set("MoveMode", 0); }, "AutoMove-MoveMode");
            _taskManager.Enqueue(() => Chat.ExecuteCommand("/automove on"), "AutoMove-On");
            _taskManager.Enqueue(() => EzThrottler.Throttle("AutoMove", Convert.ToInt32(action.Arguments[0])), "AutoMove-Throttle");
            _taskManager.Enqueue(() => EzThrottler.Check("AutoMove") || !IsReady, Convert.ToInt32(action.Arguments[0]), "AutoMove-CheckThrottleOrNotReady");
            _taskManager.Enqueue(() => { if (movementMode == 1) Svc.GameConfig.UiControl.Set("MoveMode", 1); }, "AutoMove-MoveMode2");
            _taskManager.Enqueue(() => IsReady, int.MaxValue, "AutoMove-WaitIsReady");
            _taskManager.Enqueue(() => Chat.ExecuteCommand("/automove off"), "AutoMove-Off");
        }

        /// <summary>
        /// 等待 N 毫秒。
        /// </summary>
        /// <remarks>
        /// 「跳過目前步驟」要把已經等掉的時間寫回路徑檔,所以這裡順便記下自己的一組計時
        /// (<c>Plugin.WaitStepEndTick</c> / <c>Plugin.WaitStepDurationMs</c>)。
        /// 🔴 不可以只靠 <c>EzThrottler.GetRemainingTime("Wait")</c> 反推:EzThrottler 的 key 是
        /// 全域且永久的,上一次的等待被中止時(按停止、換區、按跳過)那個尚未到期的殘值會留下來,
        /// 下一次 Wait 一開始就會量到「已經等了一段時間」的假象,而且完全不報錯。
        /// 我們自己的計時只在 throttle 真的重新起算的那一刻寫入,所以不會有殘留。
        /// ⚠️ StopForCombat 開著時前後各有一個「等脫戰」任務,那段時間不算等待時間 ——
        /// 計時只涵蓋中間那段 throttle,那正是要寫回的值。
        /// </remarks>
        public unsafe void Wait(PathAction action)
        {
            Plugin.Action = $"Wait: {action.Arguments[0]}";
            int waitMs = Convert.ToInt32(action.Arguments[0]);
            if (Plugin.StopForCombat)
                _taskManager.Enqueue(() => !Player.Character->InCombat, int.MaxValue, "Wait");
            _taskManager.Enqueue(() => StartWaitThrottle(waitMs), "Wait");
            _taskManager.Enqueue(() => EzThrottler.Check("Wait"), waitMs, "Wait");
            if (Plugin.StopForCombat)
                _taskManager.Enqueue(() => !Player.Character->InCombat, int.MaxValue, "Wait");
            _taskManager.Enqueue(() => { Plugin.ClearWaitStepTiming(); Plugin.Action = ""; });
        }

        /// <summary>
        /// 起算 Wait 的 throttle。只有在 throttle 真的重新起算(回 true)時才記下計時,
        /// 這樣「已等時間」永遠對應目前這一次等待,不會沿用上一次的殘值。
        /// </summary>
        private static bool StartWaitThrottle(int waitMs)
        {
            if (!EzThrottler.Throttle("Wait", waitMs))
                return false;

            Plugin.WaitStepDurationMs = waitMs;
            Plugin.WaitStepEndTick    = Environment.TickCount64 + waitMs;
            return true;
        }

        public unsafe void WaitFor(PathAction action)
        {
            Plugin.Action = $"WaitFor: {action.Arguments[0]}";
            var waitForWhats = action.Arguments[0].Split(';');
            switch (waitForWhats[0])
            {
                case "Combat":
                    _taskManager.Enqueue(() => Player.Character->InCombat, "WaitFor-Combat");
                    break;
                case "OOC":
                    _taskManager.Enqueue(() => Player.Character->InCombat, 500, "WaitFor-Combat-500");
                    _taskManager.Enqueue(() => !Player.Character->InCombat, int.MaxValue, "WaitFor-OOC");
                    break;
                case "IsValid":
                    _taskManager.Enqueue(() => !IsValid, 500, "WaitFor-NotIsValid-500");
                    _taskManager.Enqueue(() => IsValid, int.MaxValue, "WaitFor-IsValid");
                    break;
                case "IsOccupied":
                    _taskManager.Enqueue(() => !IsOccupied, 500, "WaitFor-NotIsOccupied-500");
                    _taskManager.Enqueue(() => IsOccupied, int.MaxValue, "WaitFor-IsOccupied");
                    break;
                case "IsReady":
                    _taskManager.Enqueue(() => !IsReady, 500, "WaitFor-NotIsReady-500");
                    _taskManager.Enqueue(() => IsReady, int.MaxValue, "WaitFor-IsReady");
                    break;
                case "DistanceTo":
                    if (waitForWhats.Length < 3)
                        return;
                    if (waitForWhats[1].TryGetVector3(out var position)) return;
                    if (float.TryParse(waitForWhats[2], out var distance)) return;

                    _taskManager.Enqueue(() => Vector3.Distance(Player.Position, position) <= distance, int.MaxValue, $"WaitFor-DistanceTo({position})<={distance}");
                    break;
                case "ConditionFlag":
                    if (waitForWhats.Length < 3)
                        return;
                    ConditionFlag conditionFlag = Enum.TryParse(waitForWhats[1], out ConditionFlag condition) ? condition : ConditionFlag.None;
                    bool active = bool.TryParse(waitForWhats[2], out active) && active;

                    if (conditionFlag == ConditionFlag.None) return;

                    _taskManager.Enqueue(() => Svc.Condition[conditionFlag] == !active, 500, $"WaitFor-{conditionFlag}=={!active}-500");
                    _taskManager.Enqueue(() => Svc.Condition[conditionFlag] == active, int.MaxValue, $"WaitFor-{conditionFlag}=={!active}");
                    break;
                case "BNpcInRadius":
                    if (waitForWhats.Length == 1)
                        return;
                    _taskManager.Enqueue(() => !(GetObjectsByRadius(int.TryParse(waitForWhats[1], out var radius) ? radius : 0)?.Count > 0), $"WaitFor-BNpcInRadius{waitForWhats[1]}");
                    _taskManager.Enqueue(() => IsReady, int.MaxValue, "WaitFor");
                    break;
            }
            _taskManager.Enqueue(() => Plugin.Action = "");

        }

        private bool CheckPause() => _plugin.Stage == Stage.Paused;

        public unsafe void ExitDuty(PathAction action)
        {
            _taskManager.Enqueue(() => { ExitDutyHelper.Invoke(); }, "ExitDuty-Invoke");
            _taskManager.Enqueue(() => ExitDutyHelper.State != ActionState.Running, "ExitDuty-WaitExitDutyRunning");
        }

        public unsafe bool IsAddonReady(nint addon) => addon > 0 && GenericHelpers.IsAddonReady((AtkUnitBase*)addon);

        public void SelectYesno(PathAction action)
        {
            _taskManager.Enqueue(() => Plugin.Action = $"SelectYesno: {action.Arguments[0]}", "SelectYesno");
            _taskManager.Enqueue(() => AddonHelper.ClickSelectYesno(action.Arguments[0].ToUpper().Equals("YES")), "SelectYesno");
            _taskManager.DelayNext("SelectYesno", 500);
            _taskManager.Enqueue(() => !IsCasting, "SelectYesno");
            _taskManager.Enqueue(() => Plugin.Action = "");
        }
        public void SelectString(PathAction action)
        {


            _taskManager.Enqueue(() => Plugin.Action = $"SelectString: {action.Arguments[0]}, {action.Note}", "SelectString");
            _taskManager.Enqueue(() => AddonHelper.ClickSelectString(Convert.ToInt32(action.Arguments[0])), "SelectString");
            _taskManager.DelayNext("SelectString", 500);
            _taskManager.Enqueue(() => !IsCasting, "SelectString");
            _taskManager.Enqueue(() => Plugin.Action = "");
        }

        /// <summary>接受(或拒絕)JournalResult 視窗。新人訓練所那批路徑檔用它結算每一課。</summary>
        public void SelectJournalResult(PathAction action)
        {
            if (action.Arguments.Count == 0)
                return;

            bool accept = bool.TryParse(action.Arguments[0], out bool parsed) && parsed;

            _taskManager.Enqueue(() => Plugin.Action = $"JournalResult: {action.Arguments[0]}, {action.Note}", "JournalResult");
            _taskManager.Enqueue(() => AddonHelper.SelectJournalResult(accept), "JournalResult");
            _taskManager.DelayNext("JournalResult", 500);
            _taskManager.Enqueue(() => !IsCasting, "JournalResult");
            _taskManager.Enqueue(() => Plugin.Action = "");
        }

        public unsafe void MoveToObject(PathAction action)
        {
            if (!TryGetObjectIdRegex(action.Arguments[0], out var objectDataId)) return;

            // 閉包只捕獲 GameObjectId:下面的 Move 是 int.MaxValue 重試,會跨數百幀
            // 反覆解參考。目標在這期間消失就是攔不到的 AccessViolation。
            ulong? objectId = null;
            Plugin.Action = $"MoveToObject: {objectDataId}";

            _taskManager.Enqueue(() => TryGetObjectIdByDataId(uint.Parse(objectDataId), out objectId), "MoveToObject-GetGameObject");
            _taskManager.Enqueue(() => MovementHelper.Move(ResolveObject(objectId)), int.MaxValue, "MoveToObject-Move");
            _taskManager.Enqueue(() => Plugin.Action = "");
        }

        public void TreasureCoffer(PathAction _)
        {
            this.Wait(new PathAction() { Arguments = ["250"] });
        }

        private bool TargetCheck(IGameObject? gameObject)
        {
            if (gameObject is not { IsTargetable: true } || !gameObject.IsValid() || (Svc.Targets.Target?.Equals(gameObject) ?? false))
                return true;

            if (EzThrottler.Check("TargetCheck"))
            {
                EzThrottler.Throttle("TargetCheck", 25);
                Svc.Targets.Target = gameObject;
            }
            return false;
        }

        public unsafe void Target(PathAction action)
        {
            if (!TryGetObjectIdRegex(action.Arguments[0], out var objectDataId)) return;

            // 閉包只捕獲 GameObjectId;TargetCheck 會反覆重跑直到成功,
            // 期間目標消失時原本會解參考已釋放的位址,並把它交給 Svc.Targets.Target。
            ulong? objectId = null;
            Plugin.Action = $"Target: {objectDataId}";

            _taskManager.Enqueue(() => TryGetObjectIdByDataId(uint.Parse(objectDataId), out objectId), "Target-GetGameObject");
            _taskManager.Enqueue(() => TargetCheck(ResolveObject(objectId)), "Target-Check");
            _taskManager.Enqueue(() => Plugin.Action = "");
        }

        public void ClickTalk(PathAction action) => _taskManager.Enqueue(() => AddonHelper.ClickTalk(), "ClickTalk");

        private unsafe bool InteractableCheck(IGameObject? gameObject)
        {
            if (Conditions.Instance()->Mounted || Conditions.Instance()->RidingPillion)
            {
                Plugin.Action = "";
                Svc.Log.Debug("InteractableCheck: giving up, Mounted/RidingPillion");
                return true;
            }

            if (Player.Available && IsCasting)
                return false;

            if (GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* addonSelectYesno) && GenericHelpers.IsAddonReady(addonSelectYesno) && !AddonHelper.ClickSelectYesno(true))
                return false;
            else if (AddonHelper.ClickSelectYesno(true))
            {
                Plugin.Action = "";
                Svc.Log.Debug("InteractableCheck: giving up, clicked SelectYesno(true)");
                return true;
            }

            if (GenericHelpers.TryGetAddonByName("SelectString", out AtkUnitBase* addonSelectString) && GenericHelpers.IsAddonReady(addonSelectString))
            {
                Plugin.Action = "";
                Svc.Log.Debug("InteractableCheck: giving up, SelectString addon is open");
                return true;
            }

            if (GenericHelpers.TryGetAddonByName("Talk", out AtkUnitBase* addonTalk) && GenericHelpers.IsAddonReady(addonTalk) && !AddonHelper.ClickTalk())
                return false;
            else if (AddonHelper.ClickTalk())
            {
                Plugin.Action = "";
                Svc.Log.Debug("InteractableCheck: giving up, clicked Talk");
                return true;
            }

            if (gameObject == null || !IsValid)
            {
                Plugin.Action = "";
                Svc.Log.Debug($"InteractableCheck: giving up, gameObject is {(gameObject == null ? "null" : "non-null")}, IsValid={IsValid}");
                return true;
            }

            // 只快取 DataId 這個純量。傳進來的 gameObject 是呼叫端每幀用 ResolveObject
            // (依 GameObjectId 查表)重解出來的,所以此刻讀它的欄位是安全的;但不能把物件
            // 本身留到後續的幀——那等於持有一根建構時就凍結、之後永不重解析的原生指標。
            var targetDataId = gameObject.BaseId;

            // TryGetObjectByDataId 是「找最近的同 DataId 物件」,要對整個物件表做距離排序。
            // 把它連同所有解參考一起放進節流視窗,每秒一次而不是每幀一次。
            // 代價:目標消失時的中止判斷最多延遲 1 秒——但那一秒內只是不動作,
            // 不會去解參考任何舊指標,所以延遲是安全的方向。
            if (!EzThrottler.Throttle("Interactable", 1000))
                return false;

            if (!TryGetObjectByDataId(targetDataId, out var target) || target == null)
            {
                Plugin.Action = "";
                Svc.Log.Debug($"InteractableCheck: giving up, no object found for dataId {targetDataId}");
                return true;
            }

            if (!target.IsTargetable || !target.IsValid())
            {
                Plugin.Action = "";
                Svc.Log.Debug($"InteractableCheck: giving up on {target.Name} <{target.GameObjectId:X}> at {target.Position}, IsTargetable={target.IsTargetable}, IsValid={target.IsValid()}");
                return true;
            }

            if (GetBattleDistanceToPlayer(target) > 2f)
                MovementHelper.Move(target, 0.25f, 2f, false);
            else
            {
                Svc.Log.Debug($"InteractableCheck: Interacting with {target.Name} at {target.Position} which is {GetDistanceToPlayer(target)} away, IsTargetable: {target.IsTargetable}");
                if (VNavmesh_IPCSubscriber.Path_IsRunning())
                    VNavmesh_IPCSubscriber.Path_Stop();
                InteractWithObject(target);
            }

            return false;
        }
        // 參數收 GameObjectId 而不是 IGameObject:下面每個任務都在後續的幀執行,
        // 捕獲 IGameObject 等於跨幀持有一根建構時就凍結的原生指標。
        private unsafe void Interactable(ulong? objectId)
        {
            _taskManager.Enqueue(() => BossMod_IPCSubscriber.SetMovement(false));
            _taskManager.Enqueue(() => InteractableCheck(ResolveObject(objectId)), "Interactable-InteractableCheck");
            _taskManager.Enqueue(() => IsCasting, 500, "Interactable-WaitIsCasting");
            _taskManager.Enqueue(() => !IsCasting, "Interactable-WaitNotIsCasting");
            _taskManager.Enqueue(() => BossMod_IPCSubscriber.SetMovement(true));
            _taskManager.DelayNext("Interactable-DelayNext100", 100);
            _taskManager.Enqueue(() =>
            {
                var boolAddonSelectYesno = GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* addonSelectYesno) && GenericHelpers.IsAddonReady(addonSelectYesno);

                var boolAddonSelectString = GenericHelpers.TryGetAddonByName("SelectString", out AtkUnitBase* addonSelectString) && GenericHelpers.IsAddonReady(addonSelectString);

                var boolAddonTalk = GenericHelpers.TryGetAddonByName("Talk", out AtkUnitBase* addonTalk) && GenericHelpers.IsAddonReady(addonTalk);

                // 這個任務在後續的幀才執行,所以在這裡重查一次物件表。
                var gameObject = ResolveObject(objectId);

                if (!boolAddonSelectYesno && !boolAddonTalk && (!(gameObject?.IsTargetable ?? false) ||
                Conditions.Instance()->Mounted ||
                Conditions.Instance()->RidingPillion ||
                Svc.Condition[ConditionFlag.BetweenAreas] ||
                Svc.Condition[ConditionFlag.BetweenAreas51] ||
                Svc.Condition[ConditionFlag.BeingMoved] ||
                Svc.Condition[ConditionFlag.Jumping61] ||
                Svc.Condition[ConditionFlag.CarryingItem] ||
                Svc.Condition[ConditionFlag.CarryingObject] ||
                Svc.Condition[ConditionFlag.Occupied] ||
                Svc.Condition[ConditionFlag.Occupied30] ||
                Svc.Condition[ConditionFlag.Occupied33] ||
                Svc.Condition[ConditionFlag.Occupied38] ||
                Svc.Condition[ConditionFlag.Occupied39] ||
                boolAddonSelectString ||
                gameObject?.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj))
                {
                    Plugin.Action = "";
                }
                else
                {
                    if (TryGetObjectIdByDataId(gameObject?.BaseId ?? 0, out var nextObjectId))
                    {
                        var next = ResolveObject(nextObjectId);
                        if (next != null)
                        {
                            Svc.Log.Debug($"Interactable - Looping because {next.Name} is still Targetable: {next.IsTargetable} and we did not change conditions,  Position: {next.Position} Distance: {GetDistanceToPlayer(next.Position)}");
                            Interactable(nextObjectId);
                        }
                    }
                }
            }, "Interactable-LoopCheck");
        }

        public unsafe void Interactable(PathAction action)
        {
            List<uint> dataIds = [];
            string objectDataId = string.Empty;
            if (action.Arguments.Count > 1)
                action.Arguments.Each(x => dataIds.Add(TryGetObjectIdRegex(x, out objectDataId) ? (uint.TryParse(objectDataId, out var dataId) ? dataId : 0) : 0));
            else
                dataIds.Add(TryGetObjectIdRegex(action.Arguments[0], out objectDataId) ? (uint.TryParse(objectDataId, out var dataId) ? dataId : 0) : 0);

            // 🔴 dataIds 是 List<uint>，跟字面量 "0"（string）永遠不可能相等——這個防呆
            //    形同虛設，路徑檔的 Arguments 只要不是純數字（例如誤填成物件顯示名稱），
            //    parse 失敗會靜默退回 0，然後去找 BaseId=0 的物件亂互動、永遠卡住重試，
            //    而不是像這裡原本想要的那樣直接跳過這一步。
            if (dataIds.All(x => x == 0))
            {
                Svc.Log.Warning($"Interactable: 所有參數都解析不出有效的 DataId（Arguments=[{string.Join(", ", action.Arguments)}]），跳過這一步。");
                return;
            }

            // 閉包只捕獲 GameObjectId,每個任務執行時才重查物件表。
            ulong? objectId = null;
            Plugin.Action = $"Interactable";
            // 極火龍殲滅戰打完後的個人化剝取物件（每個玩家各自一份、同 DataId 同座標）實測要等到
            // 快 40 幾秒才會變成 IsTargetable（推測是伺服器依序處理每個玩家的領取）。TaskManager 的
            // 預設逾時只有 10 秒（TimeLimitMS），而 AutoDuty 的實例是 AbortOnTimeout=false ⇒ 逾時會
            // 直接放行往下跑、objectId 還是 null，接著整個互動被放棄，材料永遠拿不到。拉長到 90 秒。
            _taskManager.Enqueue(() => Player.Character->InCombat || (objectId = Svc.Objects.Where(x => x.BaseId.EqualsAny(dataIds) && x.IsTargetable).OrderBy(GetDistanceToPlayer).FirstOrDefault()?.GameObjectId) != null, 90000, "Interactable-GetGameObjectUnlessInCombat");
            _taskManager.Enqueue(() => { Plugin.Action = $"Interactable: {ResolveObject(objectId)?.BaseId}"; }, "Interactable-SetActionVar");
            _taskManager.Enqueue(() =>
            {
                if (Player.Character->InCombat)
                {
                    _taskManager.Abort();
                    _taskManager.Enqueue(() => !Player.Character->InCombat, int.MaxValue, "Interactable-InCombatWait");
                    Interactable(action);
                }
                else if (objectId == null)
                {
                    // 等不到可互動目標，放棄並中止任務鏈 —— 但中止之前一定要把 Action 清空，
                    // 不然它會停在上一行設的 "Interactable: "（objectId 是 null，冒號後面印出來是空的，
                    // 但字串本身不是空字串）。CheckFinishing 只認 Action 是不是空字串來判斷「這步
                    // 真的做完了沒」，漏清空的話它會誤以為還在忙，平白多等一輪逃生口的時間才退本。
                    Plugin.Action = "";
                    _taskManager.Abort();
                }
                }, "Interactable-InCombatCheck");
            _taskManager.Enqueue(() => ResolveObject(objectId)?.IsTargetable ?? true, "Interactable-WaitGameObjectTargetable");
            _taskManager.Enqueue(() => Interactable(objectId), "Interactable-InteractableLoop");
        }

        private bool TryGetObjectIdRegex(string input, out string output) => (RegexHelper.ObjectIdRegex().Match(input).Success ? output = RegexHelper.ObjectIdRegex().Match(input).Captures.First().Value : output = string.Empty) != string.Empty;

        private bool BossCheck()
        {
            if (!Svc.Condition[ConditionFlag.InCombat])
                return true;

            
            if (EzThrottler.Throttle("PositionalChecker", 25) && ReflectionHelper.Avarice_Reflection.PositionalChanged(out Positional positional))
                BossMod_IPCSubscriber.SetPositional(positional);
            
            return false;
        }

        private unsafe bool BossMoveCheck(Vector3 bossV3)
        {
            if (Plugin.BossObject != null && Plugin.BossObject.Struct()->InCombat)
            {
                VNavmesh_IPCSubscriber.Path_Stop();
                return true;
            }
            return MovementHelper.Move(bossV3);
        }

        private void BossLoot(List<IGameObject>? gameObjects, int index)
        {
            if (gameObjects == null || gameObjects.Count < 1)
            {
                _taskManager.DelayNext("BossLoot-WaitASecToLootChest", 1000);
                return;
            }

            _taskManager.Enqueue(() => MovementHelper.Move(gameObjects[index], 0.25f, 1f), "BossLoot-MoveToChest");
            this.Wait(new PathAction() { Arguments = ["250"] });
            
            _taskManager.Enqueue(() =>
            {
                index++;
                if (gameObjects.Count > index)
                    BossLoot(gameObjects, index);
                else
                    _taskManager.DelayNext("BossLoot-WaitASecToLootChest", 1000);
            }, "BossLoot-LoopOrDelay");
        }

        public void Boss(PathAction action)
        {
            Svc.Log.Info($"Starting Action Boss: {Plugin.BossObject?.Name.TextValue ?? "null"}");
            int index = 0;
            List<IGameObject>? treasureCofferObjects = null;
            Plugin.SkipTreasureCoffer = false;
            StopForCombat(new PathAction() { Arguments = ["true", "noWait"] });
            _taskManager.Enqueue(() => BossMoveCheck(action.Position),                           "Boss-MoveCheck");
            if (Plugin.BossObject == null)
                _taskManager.Enqueue(() => (Plugin.BossObject = GetBossObject()) != null, "Boss-GetBossObject");
            _taskManager.Enqueue(() => Plugin.Action = $"Boss: {Plugin.BossObject?.Name.TextValue ?? ""}", "Boss-SetActionVar");
            _taskManager.Enqueue(() => Svc.Targets.Target = Plugin.BossObject, "Boss-SetTarget");
            _taskManager.Enqueue(() => Svc.Condition[ConditionFlag.InCombat], "Boss-WaitInCombat");
            _taskManager.Enqueue(() => BossCheck(), int.MaxValue, "Boss-BossCheck");
            _taskManager.Enqueue(() => { Plugin.BossObject = null; }, "Boss-ClearBossObject");

            if (Plugin.Configuration.LootTreasure)
            {
                _taskManager.DelayNext("Boss-TreasureDelay", 1000);
                _taskManager.Enqueue(() => treasureCofferObjects = GetObjectsByObjectKind(Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure)?.Where(x => BelowDistanceToPlayer(x.Position, 50, 10)).ToList(), "Boss-GetTreasureChests");
                _taskManager.Enqueue(() => BossLoot(treasureCofferObjects, index), "Boss-LootCheck");
            }
        }

        public void PausePandora(PathAction _)
        {
            return;
            //disable for now until we have a need other than interact objects
            //if (PandorasBox_IPCSubscriber.IsEnabled)
            //_taskManager.Enqueue(() => PandorasBox_IPCSubscriber.PauseFeature(featureName, int.Parse(intMs)));
        }

        public void Revival(PathAction _)
        {
            _taskManager.Enqueue(() => Plugin.Action = "");
        }

        public void CameraFacing(PathAction action)
        {
            if (action != null)
            {
                string[] v = action.Arguments[0].Split(", ");
                if (v.Length == 3)
                {
                    Vector3 facingPos = new(float.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(v[2], System.Globalization.CultureInfo.InvariantCulture));
                    Plugin.OverrideCamera.Face(facingPos);
                }
            }
        }

        public enum OID : uint
        {
            Blue = 0x1E8554,
            Red = 0x1E8A8C,
            Green = 0x1E8A8D,
        }

        private string? GlobalStringStore;

        private unsafe void PraeFrameworkUpdateMount(IFramework _)
        {
            if (!EzThrottler.Throttle("PraeUpdate", 50))
                return;

            var objects = GetObjectsByObjectKind(Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc);

            if (objects != null)
            {
                var protoArmOrDoor = objects.FirstOrDefault(x => x.IsTargetable && x.BaseId is 14566 or 14616 && GetDistanceToPlayer(x) <= 25);
                if (protoArmOrDoor != null)
                    Svc.Targets.Target = protoArmOrDoor;
            }

            if (Svc.Condition[ConditionFlag.Mounted] && Svc.Targets.Target != null && Svc.Targets.Target.IsHostile())
            {
                var dir = Vector2.Normalize(new Vector2(Svc.Targets.Target.Position.X, Svc.Targets.Target.Position.Z) - new Vector2(Player.Position.X, Player.Position.Z));
                float rot = (float)Math.Atan2(dir.X, dir.Y);

                Player.Object.Struct()->SetRotation(rot);

                var targetPosition = Svc.Targets.Target.Position;
                ActionManager.Instance()->UseActionLocation(ActionType.Action, 1128, Player.Object.GameObjectId, &targetPosition);
            }
        }


        private static readonly uint[] praeGaiusIds = [9020u, 14453u, 14455u];
        private void PraeFrameworkUpdateGaius(IFramework _)
        {
            if (!EzThrottler.Throttle("PraeUpdate", 50) || !IsReady || Svc.Targets.Target != null && praeGaiusIds.Contains(Svc.Targets.Target.BaseId))
                return;

            List<IGameObject>? objects = GetObjectsByObjectKind(Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc);

            IGameObject? gaius = objects?.FirstOrDefault(x => x.IsTargetable && praeGaiusIds.Contains(x.BaseId));
            if (gaius != null)
                Svc.Targets.Target = gaius;
        }


        public unsafe void DutySpecificCode(PathAction action)
        {
            // 閉包只捕獲 GameObjectId,每個任務執行時才重查物件表。
            // 這些任務都是在後續的幀執行的,捕獲 IGameObject 等於跨幀持有原生指標。
            ulong? objectId = null;
            switch (Svc.ClientState.TerritoryType)
            {
                //Prae
                case 1044:
                    switch (action.Arguments[0])
                    {
                        case "1":
                            Plugin.Framework_Update_InDuty += this.PraeFrameworkUpdateMount;
                            Interactable(new PathAction { Arguments = ["2012819"] });
                            break;
                        case "2":
                            Plugin.Framework_Update_InDuty -= this.PraeFrameworkUpdateMount;
                            break;
                        case "3":
                            Plugin.Framework_Update_InDuty += this.PraeFrameworkUpdateGaius;
                            break;
                    }
                    break;
                //Sastasha
                //Blue -  2000213
                //Red -  2000214
                //Green - 2000215
                case 1036:
                    switch (action.Arguments[0])
                    {
                        case "1":
                            _taskManager.Enqueue(() => (objectId = GetObjectsByObjectKind(Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj)?.FirstOrDefault(a => a.IsTargetable && (OID)a.BaseId is OID.Blue or OID.Red or OID.Green)?.GameObjectId) != null, "DutySpecificCode");
                            _taskManager.Enqueue(() =>
                            {
                                var gameObject = ResolveObject(objectId);
                                if (gameObject != null)
                                {
                                    switch ((OID)gameObject.BaseId)
                                    {
                                        case OID.Blue:
                                            GlobalStringStore = "2000213";
                                            break;
                                        case OID.Red:
                                            GlobalStringStore = "2000214";
                                            break;
                                        case OID.Green:
                                            GlobalStringStore = "2000215";
                                            break;
                                    }
                                }
                            }, "DutySpecificCode");
                            break;
                        case "2":
                            _taskManager.Enqueue(() => Interactable(new PathAction() { Arguments = [GlobalStringStore ?? ""] }), "DutySpecificCode");
                            break;
                        case "3":
                            _taskManager.Enqueue(() => (objectId = GetObjectIdByDataId(2000216)) != null, "DutySpecificCode");
                            _taskManager.Enqueue(() => MovementHelper.Move(ResolveObject(objectId), 0.25f, 2.5f), "DutySpecificCode");
                            _taskManager.DelayNext("DutySpecificCode", 1000);
                            _taskManager.Enqueue(() => InteractWithObject(ResolveObject(objectId)), "DutySpecificCode");
                            break;
                        default: break;
                    }
                    break;
                //Mount Rokkon
                case 1137:
                    switch (action.Arguments[0])
                    {
                        case "5":
                            _taskManager.Enqueue(() => (objectId = GetObjectIdByDataId(16140)) != null, "DutySpecificCode");
                            _taskManager.Enqueue(() => MovementHelper.Move(ResolveObject(objectId), 0.25f, 2.5f), "DutySpecificCode");
                            _taskManager.DelayNext("DutySpecificCode", 1000);
                            _taskManager.Enqueue(() => InteractWithObject(ResolveObject(objectId)), "DutySpecificCode");
                            if (IsValid)
                            {
                                _taskManager.Enqueue(() => InteractWithObject(ResolveObject(objectId)), "DutySpecificCode");
                                _taskManager.Enqueue(() => AddonHelper.ClickSelectString(0));
                            }
                            break;
                        case "6":
                            _taskManager.Enqueue(() => (objectId = GetObjectIdByDataId(16140)) != null, "DutySpecificCode");
                            _taskManager.Enqueue(() => MovementHelper.Move(ResolveObject(objectId), 0.25f, 2.5f), "DutySpecificCode");
                            _taskManager.DelayNext("DutySpecificCode", 1000);
                            if (IsValid)
                            {
                                _taskManager.Enqueue(() => InteractWithObject(ResolveObject(objectId)), "DutySpecificCode");
                                _taskManager.Enqueue(() => AddonHelper.ClickSelectString(1));
                            }
                            break;
                        case "12":
                            _taskManager.Enqueue(() => Chat.ExecuteCommand("/rotation Settings AoEType Off"), "DutySpecificCode");
                            _taskManager.Enqueue(() => ActionManager.Instance()->UseAction(ActionType.GeneralAction, 16), "DutySpecificCode");
                            _taskManager.Enqueue(() => EzThrottler.Throttle("DutySpecificCode", Convert.ToInt32(500)));
                            _taskManager.Enqueue(() => EzThrottler.Check("DutySpecificCode"), Convert.ToInt32(500), "DutySpecificCode");
                            _taskManager.Enqueue(() => Chat.ExecuteCommand("/mk ignore1"), "DutySpecificCode");
                            _taskManager.Enqueue(() => EzThrottler.Throttle("DutySpecificCode", Convert.ToInt32(100)));
                            _taskManager.Enqueue(() => EzThrottler.Check("DutySpecificCode"), Convert.ToInt32(100), "DutySpecificCode");

                            _taskManager.Enqueue(() => ActionManager.Instance()->UseAction(ActionType.GeneralAction, 16), "DutySpecificCode");
                            _taskManager.Enqueue(() => EzThrottler.Throttle("DutySpecificCode", Convert.ToInt32(500)));
                            _taskManager.Enqueue(() => EzThrottler.Check("DutySpecificCode"), Convert.ToInt32(500), "DutySpecificCode");
                            _taskManager.Enqueue(() => Chat.ExecuteCommand("/mk ignore2"), "DutySpecificCode");
                            _taskManager.Enqueue(() => EzThrottler.Throttle("DutySpecificCode", Convert.ToInt32(100)));
                            _taskManager.Enqueue(() => EzThrottler.Check("DutySpecificCode"), Convert.ToInt32(100), "DutySpecificCode");

                            _taskManager.Enqueue(() => ActionManager.Instance()->UseAction(ActionType.GeneralAction, 16), "DutySpecificCode");
                            _taskManager.Enqueue(() => EzThrottler.Throttle("DutySpecificCode", Convert.ToInt32(500)));
                            _taskManager.Enqueue(() => EzThrottler.Check("DutySpecificCode"), Convert.ToInt32(500), "DutySpecificCode");
                            _taskManager.Enqueue(() => Chat.ExecuteCommand("/mk attack1"), "DutySpecificCode");
                            break;
                        case "13":
                            _taskManager.Enqueue(() => ActionManager.Instance()->UseAction(ActionType.GeneralAction, 16), "DutySpecificCode");
                            _taskManager.Enqueue(() => EzThrottler.Throttle("DutySpecificCode", Convert.ToInt32(500)));
                            _taskManager.Enqueue(() => EzThrottler.Check("DutySpecificCode"), Convert.ToInt32(500), "DutySpecificCode");
                            _taskManager.Enqueue(() => Chat.ExecuteCommand("/mk attack1"), "DutySpecificCode");
                            break;

                        default: break;
                    }
                    break;
                //Xelphatol
                case 1113:
                    switch (action.Arguments[0])
                    {
                        case "1":
                            _taskManager.Enqueue(() => TryGetObjectIdByDataId(2007400, out objectId), "DutySpecificCode");
                            _taskManager.Enqueue(() =>
                                {
                                    if (!EzThrottler.Throttle("DSC", 500) || Player.Character->IsCasting) return false;

                                    if (GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* addonSelectYesno) && GenericHelpers.IsAddonReady(addonSelectYesno) && !AddonHelper.ClickSelectYesno(true))
                                        return false;
                                    else if (AddonHelper.ClickSelectYesno(true))
                                        return true;

                                    // 這個檢查式會反覆重跑很多幀,每次都要重查物件表。
                                    var gameObject = ResolveObject(objectId);
                                    if (gameObject == null) return true;

                                    if (GetBattleDistanceToPlayer(gameObject) > 2.5f)
                                        MovementHelper.Move(gameObject, 0.25f, 2.5f);
                                    else
                                    {
                                        MovementHelper.Stop();
                                        InteractWithObject(gameObject);
                                    }

                                    return false;
                                }, "DSC-Xelphatol-ClickTailWind");
                            break;
                        case "2":
                            _taskManager.Enqueue(() => TryGetObjectIdByDataId(2007401, out objectId), "DutySpecificCode");
                            _taskManager.Enqueue(() =>
                            {
                                if (!EzThrottler.Throttle("DSC", 500) || Player.Character->IsCasting) return false;

                                if (GenericHelpers.TryGetAddonByName("SelectYesno", out AtkUnitBase* addonSelectYesno) && GenericHelpers.IsAddonReady(addonSelectYesno) && !AddonHelper.ClickSelectYesno(true))
                                    return false;
                                else if (AddonHelper.ClickSelectYesno(true))
                                    return true;

                                // 這個檢查式會反覆重跑很多幀,每次都要重查物件表。
                                var gameObject = ResolveObject(objectId);
                                if (gameObject == null) return true;

                                if (GetBattleDistanceToPlayer(gameObject) > 2.5f)
                                    MovementHelper.Move(gameObject, 0.25f, 2.5f);
                                else
                                {
                                    MovementHelper.Stop();
                                    InteractWithObject(gameObject);
                                }

                                return false;
                            }, "DSC-Xelphatol-ClickTailWind");
                            break;
                        default:
                            break;
                    }
                    break;
                default: break;
            }
        }
    }
}
