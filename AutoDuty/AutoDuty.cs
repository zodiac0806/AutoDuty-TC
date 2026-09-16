global using static AutoDuty.Data.Enums;
global using static AutoDuty.Data.Extensions;
global using static AutoDuty.Data.Classes;
global using static AutoDuty.AutoDuty;
global using AutoDuty.Managers;
global using ECommons.GameHelpers;
using System;
using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using System.Collections.Generic;
using System.IO;
using ECommons;
using ECommons.DalamudServices;
using ECommons.LanguageHelpers;
using AutoDuty.Windows;
using AutoDuty.IPC;
using AutoDuty.External;
using AutoDuty.Helpers;
using ECommons.Throttlers;
using Dalamud.Game.ClientState.Objects.Types;
using System.Linq;
using ECommons.GameFunctions;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Bindings.ImGui;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Dalamud.IoC;
using System.Diagnostics;
using Dalamud.Game.ClientState.Conditions;
using AutoDuty.Properties;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AutoDuty.Updater;

namespace AutoDuty;

using System.Text.RegularExpressions;
using Dalamud.Utility.Numerics;
using Data;
using ECommons.Configuration;
using ECommons.SimpleGui;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Lumina.Excel.Sheets;
using Pictomancy;
using static Data.Classes;
using TaskManager = ECommons.Automation.LegacyTaskManager.TaskManager;

// TODO:
// Scrapped interable list, going to implement an internal list that when a interactable step end in fail, the Dataid gets add to the list and is scanned for from there on out, if found we goto it and get it, then remove from list.
// Need to expand AutoRepair to include check for level and stuff to see if you are eligible for self repair. and check for dark matter
// make config saving per character
// drap drop on build is jacked when theres scrolling

// WISHLIST for VBM:
// Generic (Non Module) jousting respects navmesh out of bounds (or dynamically just adds forbiddenzones as Obstacles using Detour) (or at very least, vbm NavigationDecision can use ClosestPointonMesh in it's decision making) (or just spit balling here as no idea if even possible, add Everywhere non tiled as ForbiddenZones /shrug)

public sealed class AutoDuty : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    internal List<PathAction> Actions { get; set; } = [];
    internal List<uint> Interactables { get; set; } = [];
    internal int CurrentLoop = 0;
    internal KeyValuePair<ushort, Job?> CurrentPlayerItemLevelandClassJob = new(0, null);
    private Content? currentTerritoryContent = null;
    internal Content? CurrentTerritoryContent
    {
        get => currentTerritoryContent;
        set
        {
            CurrentPlayerItemLevelandClassJob = PlayerHelper.IsValid ? new(InventoryHelper.CurrentItemLevel, Player.Job) : new(0, null);
            currentTerritoryContent = value;
        }
    }
    internal uint CurrentTerritoryType = 0;
    internal int CurrentPath = -1;

    internal bool SupportLevelingEnabled => LevelingModeEnum == LevelingMode.Support;
    internal bool TrustLevelingEnabled => LevelingModeEnum == LevelingMode.Trust;
    internal bool LevelingEnabled => LevelingModeEnum == LevelingMode.Support || LevelingModeEnum == LevelingMode.Trust;

    internal static string Name => "AutoDuty";
    internal static AutoDuty Plugin { get; private set; }
    internal bool StopForCombat = true;
    internal DirectoryInfo PathsDirectory;
    internal FileInfo AssemblyFileInfo;
    internal FileInfo ConfigFile;
    internal DirectoryInfo? DalamudDirectory;
    internal DirectoryInfo? AssemblyDirectoryInfo;

    internal Configuration Configuration => ConfigurationMain.Instance.GetCurrentConfig;

    public int EffectiveLoopTimes => GetEffectiveLoopTimes();
    public bool PlannerActive => PlannerEnabled;
    internal RunContext? ActiveRunContext = null;
    internal WindowSystem WindowSystem = new("AutoDuty");

    public   int   Version { get; set; }
    internal Stage PreviousStage = Stage.Stopped;
    internal Stage Stage
    {
        get => _stage;
        set
        {
            switch (value)
            {
                case Stage.Stopped:
                    // 必須在 StopAndResetALL() 之前:它結尾會把 Action 清空,
                    // 而通知要拿 Action 當「停在哪一步」的佐證。
                    NotifyIfAutomationStoppedItself();
                    StopAndResetALL();
                    break;
                case Stage.Paused:
                    PreviousStage = Stage;
                    if (VNavmesh_IPCSubscriber.Path_NumWaypoints() > 0)
                        VNavmesh_IPCSubscriber.Path_Stop();
                    FollowHelper.SetFollow(null);
                    TaskManager.SetStepMode(true);
                    States |= PluginState.Paused;
                    break;
                case Stage.Action:
                    ActionInvoke();
                    break;
                case Stage.Condition:
                    Action = $"ConditionChange";
                    SchedulerHelper.ScheduleAction("ConditionChangeStageReadingPath", () => _stage = Stage.Reading_Path, () => !Svc.Condition[ConditionFlag.BetweenAreas] && !Svc.Condition[ConditionFlag.BetweenAreas51] && !Svc.Condition[ConditionFlag.Jumping61]);
                    break;
                case Stage.Waiting_For_Combat:
                    BossMod_IPCSubscriber.SetRange(Plugin.Configuration.MaxDistanceToTargetFloat);
                    break;
            }
            _stage = value;
            Svc.Log.Debug($"Stage={_stage.ToCustomString()}");
        }
    }
    internal LevelingMode LevelingModeEnum
    {
        get => levelingModeEnum;
        set
        {
            if (value == LevelingMode.Manual)
            {
                // Manual mode should not auto-pick a duty.
                levelingModeEnum = LevelingMode.Manual;
                MainTab.DutySelected = null;
                MainTab.SelectedDuty = null;
                MainTab.SelectedPath = -1;
                MainListClicked = false;
                CurrentTerritoryContent = null;
                CurrentPath = -1;
                return;
            }

            if (value != LevelingMode.None)
            {
                Svc.Log.Debug($"Setting Leveling mode to {value}");
                Content? duty = LevelingHelper.SelectHighestLevelingRelevantDuty(value == LevelingMode.Trust);

                if (duty != null)
                {
                    levelingModeEnum = value;
                    MainTab.DutySelected = ContentPathsManager.DictionaryPaths[duty.TerritoryType];
                    CurrentTerritoryContent = duty;
                    MainTab.DutySelected.SelectPath(out CurrentPath);
                    // Keep manual selection state isolated from auto-leveling selection.
                    MainTab.SelectedDuty = null;
                    MainTab.SelectedPath = -1;
                    Svc.Log.Debug($"Leveling Mode: Setting duty to {duty.Name}");
                }
                else
                {
                    MainTab.DutySelected = null;
                    MainTab.SelectedDuty = null;
                    MainTab.SelectedPath = -1;
                    MainListClicked = false;
                    CurrentTerritoryContent = null;
                    CurrentPath = -1;
                    levelingModeEnum = LevelingMode.None;
                    Svc.Log.Debug($"Leveling Mode: No appropriate leveling duty found");
                }
            }
            else
            {
                MainTab.DutySelected = null;
                MainTab.SelectedDuty = null;
                MainTab.SelectedPath = -1;
                MainListClicked = false;
                CurrentTerritoryContent = null;
                CurrentPath = -1;
                levelingModeEnum = LevelingMode.None;
            }
        }
    }
    internal PluginState States = PluginState.None;
    internal int Indexer = -1;
    internal bool MainListClicked = false;
    // ⚠️ 不要把 IGameObject 存進欄位跨幀用。
    // Dalamud 的 GameObject.Address 在建構時就凍結、永不重新解析
    // (GameObject.cs:137-139,所有屬性都走 Struct => (GameObject*)this.Address),
    // 而 IGameObject.IsValid() 只檢查「玩家有沒有登入」、完全不驗證位址
    // (GameObject.cs:170-177)。所以存 IGameObject == 存一根原生指標。
    // BossObject 會跨很多幀存活(ActionsManager.BossMoveCheck 是 TaskManager 的
    // 檢查式,會反覆重跑並解 BossObject.Struct()->InCombat),王在分階段消失/重生、
    // 團滅或離開副本時就是攔不到的 AccessViolation。
    // 改成只存 GameObjectId、每次讀取時重查物件表:既有的 BossObject != null
    // 守衛自動變成有效的存活檢查,查不到就走 null 分支而不是崩潰。
    private ulong? bossObjectId = null;

    internal IBattleChara? BossObject
    {
        get => this.bossObjectId is null ? null : Svc.Objects.SearchById(this.bossObjectId.Value) as IBattleChara;
        set => this.bossObjectId = value?.GameObjectId;
    }
    internal static IGameObject? ClosestObject => Svc.Objects.Where(o => o.IsTargetable && o.ObjectKind.EqualsAny(Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj, Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)).OrderBy(ObjectHelper.GetDistanceToPlayer).TryGetFirst(out var gameObject) ? gameObject : null;
    internal OverrideCamera OverrideCamera;
    internal MainWindow MainWindow { get; init; }
    internal Overlay Overlay { get; init; }
    internal bool InDungeon => ContentHelper.DictionaryContent.ContainsKey(Svc.ClientState.TerritoryType);
    internal bool SkipTreasureCoffer = false;

    /// <summary>
    /// 多變迷宮(Variant Dungeon)目前走到的分歧編號,給
    /// <see cref="Data.PathActionConditionVariantPath"/> 判斷步驟該不該執行。
    /// ⚠️ 目前沒有任何程式碼會改變它 —— 上游是由 VariantVote 動作在投票時寫入的,
    /// 而我方刻意不移植 VariantVote(2026-08-08 查證):它的唯一消費者是
    /// (1315) The Merchant's Tale,而台服沒有這個副本 —— TerritoryType 1315 是空佔位列,
    /// 對應 CFC 1066 超過台服表末列 1065,台服也沒有任何曉月之後的異聞迷宮。
    /// 所以它恆為 0,只有 pathIndices 含 0 的條件會成立。
    /// 等台服實裝該副本(判準:CFC 表出現指向 territory 1315 的列)再移植,
    /// 屆時 BMR 模組的正確型別名字串才是已知的(上游路徑檔傳的名字對 BMR 少一個 N,本來就是靜默 no-op)。
    /// </summary>
    internal byte VariantPath = 0;

    internal string Action = "";
    internal string PathFile = "";

    /// <summary>
    /// 目前這次 Wait 等待的結束時刻(<see cref="Environment.TickCount64"/> 基準)。0 = 沒有在等。
    /// 由 <c>ActionsManager.Wait</c> 寫入 —— 為什麼不直接用 EzThrottler 反推,見那裡的說明。
    /// </summary>
    internal long WaitStepEndTick;

    /// <summary>目前這次 Wait 等待原本設定的毫秒數。0 = 沒有在等。</summary>
    internal int WaitStepDurationMs;

    internal void ClearWaitStepTiming()
    {
        WaitStepEndTick    = 0;
        WaitStepDurationMs = 0;
    }

    internal TaskManager TaskManager;
    internal Job JobLastKnown;
    internal DutyState DutyState = DutyState.None;
    internal PathAction PathAction = new();
    internal List<Data.Classes.LogMessage> DalamudLogEntries = [];
    private LevelingMode levelingModeEnum = LevelingMode.None;
    private Stage _stage = Stage.Stopped;

    /// <summary>
    /// 下一次 Stage 被設成 Stage.Stopped 時的「自己停下來」原因。
    /// null 代表那次停止是使用者主動要求的(按鈕 / /autoduty stop / 外部 IPC),不通知。
    /// 這是白名單:只有 MarkSelfStop 會填它。將來新增停止點若忘了呼叫,
    /// 預設結果是「不通知」而不是「誤報」。
    /// </summary>
    private string? _pendingStopReason;

    /// <summary>
    /// 那次停止的完成音效是不是已經由既有的終止流程(LoopsCompleteActions)排過了。
    /// 用來避免正常跑完的路徑響兩次。
    /// </summary>
    private bool _pendingStopSoundHandled;
    private const string CommandName = "/autoduty";
    private readonly DirectoryInfo _configDirectory;
    private readonly ActionsManager _actions;

    /// <summary>
    /// 解限模式「交戰中繼續走到定點」目前有沒有把 BossMod 的自動移動關掉。
    /// 只在進出戰鬥的那一次送 IPC,不要每幀送。
    /// </summary>
    private bool _unsyncedKeepMovingArmed;
    private readonly SquadronManager _squadronManager;
    private readonly VariantManager _variantManager;
    private readonly OverrideAFK _overrideAFK;
    private readonly IPCProvider _ipcProvider;
    private IGameObject? treasureCofferGameObject = null;
    //private readonly TinyMessageBus _messageBusSend = new("AutoDutyBroadcaster");
    //private readonly TinyMessageBus _messageBusReceive = new("AutoDutyBroadcaster");
    private         bool           _recentlyWatchedCutscene = false;
    private         bool           _lootTreasure;
    private         SettingsActive _settingsActive         = SettingsActive.None;
    private         SettingsActive _bareModeSettingsActive = SettingsActive.None;
    private         DateTime       _lastRotationSetTime    = DateTime.MinValue;
    public readonly bool           isDev;

    public AutoDuty()
    {
        try
        {
            Plugin = this;
            ECommonsMain.Init(PluginInterface, Plugin, Module.DalamudReflector, Module.ObjectFunctions);
            // 讓「呼叫了對方沒有的 IPC 方法」不再完全靜默。
            // 訂閱越早越好：事件只在 IPC **呼叫**當下才被查閱，在這裡訂閱就涵蓋往後所有呼叫。
            EzIpcFailureLog.Enable();

            // ECommons.IPC 的 IPCBase 預設 wrapper 是 SafeWrapper.None(例外直接往外擲)，
            // 而且它是 lazy 單例、在第一次存取當下就烘死。AutoDuty 的門面類一律自己 new 並
            // 把 wrapper 當建構式參數傳進去,所以不依賴這個值;這裡設成我方最常用的
            // IPCException,是為了「萬一有人改用 ECommonsIPC.X 單例」時不會退回會擲例外的 None。
            ECommons.IPC.Subscribers.IPCBase.DefaultWrapper = ECommons.EzIpcManager.SafeWrapper.IPCException;

            // WrathCombo.API 若沒初始化,第一次呼叫會擲 UninitializedException;它也能自己從
            // ECommons 反射拿 PluginInterface,但那條路失敗時是靜默回 null,所以明確傳進去。
            // 🔴 不加任何 ErrorType 抑制:讓它照常擲例外,由 Wrath_IPCSubscriber.WrathSafe
            // 接住並轉交 EzIpcFailureLog —— 抑制掉的話 Wrath IPC 失敗會完全沒有 log。
            WrathCombo.API.WrathIPCWrapper.Init(PluginInterface);

            ECommons.LanguageHelpers.Localization.Init("ChineseTraditional");
            PictoService.Initialize(PluginInterface);

            this.isDev = PluginInterface.IsDev;

            //EzConfig.Init<ConfigurationMain>();
            EzConfig.DefaultSerializationFactory = new AutoDutySerializationFactory();
            (ConfigurationMain.Instance = EzConfig.Init<ConfigurationMain>()).Init();



            //Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            
            ConfigTab.BuildManuals();
            _configDirectory = PluginInterface.ConfigDirectory;
            ConfigFile = PluginInterface.ConfigFile;
            DalamudDirectory = ConfigFile.Directory?.Parent;
            PathsDirectory = new(_configDirectory.FullName + "/paths");
            AssemblyFileInfo = PluginInterface.AssemblyLocation;
            AssemblyDirectoryInfo = AssemblyFileInfo.Directory;
            
            Version =
                ((PluginInterface.IsTesting
                      ? PluginInterface.Manifest.TestingAssemblyVersion ?? PluginInterface.Manifest.AssemblyVersion
                      : PluginInterface.Manifest.AssemblyVersion)!).Revision;

            if (!_configDirectory.Exists)
                _configDirectory.Create();
            if (!PathsDirectory.Exists)
                PathsDirectory.Create();

            TaskManager = new()
            {
                AbortOnTimeout = false,

                // 🔴 這裡原本是 true。ECommons 的 LegacyTaskManager 在 TimeoutSilently=true 時
                //    把逾時訊息從 PluginLog.Warning 導去 PluginLog.Verbose(TaskManager.cs 的
                //    LogTimeout),而使用者的 Dalamud LogLevel 是 1(Serilog 的 Debug 門檻)——
                //    Verbose(0) 正好是唯一被濾掉的等級 ⇒ 每一次任務逾時在實機 log 上完全不存在。
                //    配上 AbortOnTimeout=false(逾時不中止、直接放行往下跑),症狀就是王戰整段被
                //    跳過、寶箱沒撿、人還在副本裡就走下一步,而使用者看不到任何訊息。
                //    改成 false 讓它回到 Warning;另外 TaskTimeoutWatcher 會在 UI 上累計次數。
                TimeoutSilently = false
            };

            TrustHelper.PopulateTrustMembers();
            Svc.Data.GameData.Options.PanicOnSheetChecksumMismatch = false; // TODO: remove - temporary workaround until lumina is updated
            ContentHelper.PopulateDuties();
            Svc.Data.GameData.Options.PanicOnSheetChecksumMismatch = true; // TODO: remove - temporary workaround until lumina is updated
            RepairNPCHelper.PopulateRepairNPCs();
            FileHelper.Init();
            Patcher.Patch(startup: true);

            _overrideAFK = new();
            _ipcProvider = new();
            _squadronManager = new(TaskManager);
            _variantManager = new(TaskManager);
            _actions = new(Plugin, TaskManager);
            BuildTab.ActionsList = _actions.ActionsList;
            OverrideCamera = new();
            Overlay = new();
            MainWindow = new();
            WindowSystem.AddWindow(MainWindow);
            WindowSystem.AddWindow(Overlay);

            if (Svc.ClientState.IsLoggedIn) 
                this.ClientStateOnLogin();
             
            Svc.Commands.AddHandler("/ad", new CommandInfo(OnCommand) { });
            Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "\n/autoduty or /ad -> opens main window\n".Loc() +
                "/autoduty or /ad config or cfg -> opens config window / modifies config\n".Loc() +
                "/autoduty or /ad start -> starts autoduty when in a Duty\n".Loc() +
                "/autoduty or /ad stop -> stops everything\n".Loc() +
                "/autoduty or /ad pause -> pause route\n".Loc() +
                "/autoduty or /ad resume -> resume route\n".Loc() +
                "/autoduty or /ad turnin -> GC Turnin\n".Loc() +
                "/autoduty or /ad desynth -> Desynth's your inventory\n".Loc() +
                "/autoduty or /ad repair -> Repairs your gear\n".Loc() +
                "/autoduty or /ad equiprec-> Equips recommended gear\n".Loc() +
                "/autoduty or /ad extract -> Extract's materia from equipment\n".Loc() +
                "/autoduty or /ad turnin -> GC Turnin\n".Loc() +
                "/autoduty or /ad goto -> goes to\n".Loc() +
                "/autoduty or /ad dataid -> Logs and copies your target's dataid to clipboard\n".Loc() +
                "/autoduty or /ad exitduty -> exits duty\n".Loc() +
                "/autoduty or /ad queue -> queues duty\n".Loc() +
                "/autoduty or /ad moveto -> move's to territorytype and location sent\n".Loc() +
                "/autoduty or /ad overlay -> opens overlay\n".Loc() +
                "/autoduty or /ad overlay lock-> toggles locking the overlay\n".Loc() +
                "/autoduty or /ad overlay nobg-> toggles the overlay's background\n".Loc() +
                "/autoduty or /ad movetoflag -> moves to the flag map marker\n".Loc() +
                "/autoduty or /ad run -> starts auto duty in territory type specified\n".Loc() +
                "/autoduty or /ad tt -> logs and copies to clipboard the Territory Type number for duty specified\n".Loc()
            });

            PluginInterface.UiBuilder.Draw += DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUI;
            PluginInterface.UiBuilder.OpenMainUi += OpenMainUI;

            Svc.Framework.Update += Framework_Update;
            Svc.Framework.Update += SchedulerHelper.ScheduleInvoker;
            Svc.ClientState.TerritoryChanged += ClientState_TerritoryChanged;
            Svc.ClientState.Login += ClientStateOnLogin;
            Svc.Condition.ConditionChange += Condition_ConditionChange;
            Svc.DutyState.DutyStarted += DutyState_DutyStarted;
            Svc.DutyState.DutyWiped += DutyState_DutyWiped;
            Svc.DutyState.DutyRecommenced += DutyState_DutyRecommenced;
            Svc.DutyState.DutyCompleted += DutyState_DutyCompleted;
            // 此行原設為 LogEventLevel.Debug 但無實際作用：非開發模式外掛的預設值
            // (ScopedPluginLogService.GetDefaultLevel) 本來就已經是 Debug，等於設回原值。
            // 外掛自訂等級只能比全域記錄等級更嚴格、不能更寬鬆，真正要看到 Debug 內容
            // 要到 /xllog 調整全域等級（LogTab.cs 的說明文字已經這樣告知使用者）。
            // 保留此註解避免日後又加回這行無效設定。
            PluginInterface.UiBuilder.Draw += UiBuilderOnDraw;
        }
        catch (Exception e)
        {
            Svc.Log.Info($"Failed loading plugin\n{e}");
        }
    }

    private void ClientStateOnLogin()
    {
        ConfigurationMain.Instance.SetProfileToDefault();

        Svc.Framework.RunOnTick(() =>
                                {
                                    if (Configuration.ShowOverlay && (!Configuration.HideOverlayWhenStopped || States.HasFlag(PluginState.Looping) || States.HasFlag(PluginState.Navigating)))
                                        SchedulerHelper.ScheduleAction("ShowOverlay", () => Overlay.IsOpen = true, () => PlayerHelper.IsReady);

                                    if (Configuration.ShowMainWindowOnStartup)
                                        SchedulerHelper.ScheduleAction("ShowMainWindowOnStartup", () => OpenMainUI(), () => PlayerHelper.IsReady);
                                });
    }

    private void UiBuilderOnDraw()
    {
        if (PlayerHelper.IsValid)
        {
            using PctDrawList? drawList = PictoService.Draw();

            if (drawList != null)
            {
                BuildTab.DrawHelper(drawList);

                if (Plugin.Configuration.PathDrawEnabled && CurrentTerritoryContent?.TerritoryType == Svc.ClientState.TerritoryType && this.Actions.Any() && (this.Indexer < 0 || this.Indexer >= this.Actions.Count || !this.Actions[this.Indexer].Name.Equals("Boss") || Stage != Stage.Action))
                {
                    Vector3 lastPos         = Player.Position;
                    float   stepCountFactor = (1f / this.Configuration.PathDrawStepCount);

                    for (int index = Math.Clamp(this.Indexer, 0, this.Actions.Count-1); index < this.Actions.Count; index++)
                    {
                        PathAction action = this.Actions[index];
                        if (action.Position.LengthSquared() > 1)
                        {
                            float alpha = MathF.Max(0f, 1f - (index - this.Indexer) * stepCountFactor);

                            if (alpha > 0)
                            {
                                drawList.AddCircle(action.Position, 3, ImGui.GetColorU32(new Vector4(1f, 0.2f, 0f, alpha)), 0, 3);

                                if (index > 0)
                                    drawList.AddLine(lastPos, action.Position, 0f, ImGui.GetColorU32(new Vector4(0.8f, 0.8f, 0.8f, alpha)));
                                if (index == this.Indexer)
                                    drawList.AddLine(Player.Position, action.Position, 0, 0x00FFFFFF);

                                drawList.AddText(action.Position, ImGui.GetColorU32(new Vector4(alpha + 0.25f)), index.ToString(), 20f);
                            }

                            lastPos = action.Position;
                        }
                    }
                }
            }
        }
    }

    private void DutyState_DutyStarted(object? sender, ushort e) => DutyState = DutyState.DutyStarted;
    private void DutyState_DutyWiped(object? sender, ushort e) => DutyState = DutyState.DutyWiped;
    private void DutyState_DutyRecommenced(object? sender, ushort e) => DutyState = DutyState.DutyRecommenced;
    private void DutyState_DutyCompleted(object? sender, ushort e)
    {
        DutyState = DutyState.DutyComplete;
        PlannerOnDutyCompleted();
        this.CheckFinishing();
    }

    private bool PlannerEnabled => Configuration.PlannerEnabled && Configuration.PlannerItems.Count > 0;

    private bool PlannerTryGetCurrentItem(out PlannerItem item)
    {
        item = default!;
        if (!PlannerEnabled)
            return false;
        if (Configuration.PlannerCurrentIndex < 0 || Configuration.PlannerCurrentIndex >= Configuration.PlannerItems.Count)
            return false;
        item = Configuration.PlannerItems[Configuration.PlannerCurrentIndex];
        return true;
    }

    internal RunContext? BuildPlannerRunContext(bool startFromZero = true, bool bareMode = false)
    {
        if (!PlannerEnabled)
            return null;

        var index = Math.Clamp(Configuration.PlannerCurrentIndex, 0, Configuration.PlannerItems.Count - 1);
        var item = Configuration.PlannerItems[index];

        if (!ContentHelper.DictionaryContent.TryGetValue(item.TerritoryType, out var content))
            return null;

        var targetRuns = Math.Max(1, item.TargetRuns);
        var completedRuns = Math.Clamp(item.CompletedRuns, 0, targetRuns);
        var loopsRemaining = targetRuns - completedRuns;
        if (loopsRemaining <= 0)
            return null;

        var pathIndex = -1;
        if (ContentPathsManager.DictionaryPaths.TryGetValue(item.TerritoryType, out var container))
        {
            if (!item.PathFileName.IsNullOrEmpty())
            {
                var idx = container.Paths.FindIndex(p => p.FileName.Equals(item.PathFileName, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                    pathIndex = idx;
                else
                    container.SelectPath(out pathIndex);
            }
            else
            {
                container.SelectPath(out pathIndex);
            }
        }

        return new RunContext
        {
            Source = RunSource.Planner,
            Duty = content,
            PathIndex = pathIndex,
            Loops = loopsRemaining,
            StartFromZero = startFromZero,
            BareMode = bareMode,
            PlannerItemIndex = index,
            PersistLoopsToConfig = false,
        };
    }

    internal RunContext? BuildCommandRunContext(uint territoryType, int loops = 0, bool startFromZero = true, bool bareMode = false, RunSource source = RunSource.Command, bool persistLoopsToConfig = true)
    {
        if (territoryType <= 0)
            return null;

        if (!ContentHelper.DictionaryContent.TryGetValue(territoryType, out var content))
            return null;

        var pathIndex = -1;
        if (ContentPathsManager.DictionaryPaths.TryGetValue(territoryType, out var container))
            container.SelectPath(out pathIndex);

        return new RunContext
        {
            Source = source,
            Duty = content,
            PathIndex = pathIndex,
            Loops = loops,
            StartFromZero = startFromZero,
            BareMode = bareMode,
            PlannerItemIndex = -1,
            PersistLoopsToConfig = persistLoopsToConfig,
        };
    }

    public void Run(RunContext ctx)
    {
        if (ctx == null)
            return;

        if (ctx.Duty == null)
            return;

        // Mutual exclusion (policy 3): forbid switching between Planner and Main while running.
        if (States.HasFlag(PluginState.Looping) && ActiveRunContext != null)
        {
            var runningPlanner = ActiveRunContext.Source == RunSource.Planner;
            var startingPlanner = ctx.Source == RunSource.Planner;
            if (runningPlanner != startingPlanner)
            {
                MainWindow.ShowPopup("Mode".Loc(), runningPlanner
                    ? "Planner is running. Stop it first.".Loc()
                    : "AutoDuty is running. Stop it first.".Loc());
                return;
            }
        }

        ActiveRunContext = ctx;

        CurrentTerritoryContent = ctx.Duty;
        if (ctx.PathIndex >= 0)
            CurrentPath = ctx.PathIndex;

        if (ctx.PersistLoopsToConfig && ctx.Loops > 0)
            Configuration.LoopTimes = ctx.Loops;

        // Reuse the existing legacy Run() pipeline to avoid duplicating logic.
        Run(0, 0, ctx.StartFromZero, ctx.BareMode);
    }

    private int GetEffectiveLoopTimes()
    {
        if (ActiveRunContext?.Source == RunSource.Planner && PlannerTryGetCurrentItem(out var plannerItem))
            return Math.Max(1, plannerItem.TargetRuns);

        if (ActiveRunContext is { Loops: > 0 })
            return Math.Max(1, ActiveRunContext.Loops);

        // Legacy/UI fallback: when not running, keep displaying planner target runs if planner is active.
        if (ActiveRunContext == null && PlannerTryGetCurrentItem(out var item))
            return Math.Max(1, item.TargetRuns);

        return Math.Max(1, Configuration.LoopTimes);
    }

    private void PlannerResetProgress()
    {
        foreach (var it in Configuration.PlannerItems)
            it.CompletedRuns = 0;
        Configuration.PlannerCurrentIndex = 0;
        Configuration.Save();
    }

    private bool PlannerIsPlanCompleteNoRepeat()
    {
        if (!PlannerEnabled)
            return false;
        if (Configuration.PlannerRepeat)
            return false;
        // complete when every item hit target
        return Configuration.PlannerItems.All(it => it.CompletedRuns >= Math.Max(1, it.TargetRuns));
    }

    private DateTime _plannerLastDutyCompletedAtUtc = DateTime.MinValue;
    private uint _plannerLastDutyCompletedTerritoryType;

    private bool PlannerTryApplyCurrentSelection(bool resetLoopCounter)
    {
        if (!PlannerEnabled)
            return true;

        // sanitize targets/progress
        foreach (var it in Configuration.PlannerItems)
        {
            it.TargetRuns = Math.Max(1, it.TargetRuns);
            it.CompletedRuns = Math.Clamp(it.CompletedRuns, 0, it.TargetRuns);
        }
        if (Configuration.PlannerCurrentIndex < 0)
            Configuration.PlannerCurrentIndex = 0;
        if (Configuration.PlannerCurrentIndex >= Configuration.PlannerItems.Count)
            Configuration.PlannerCurrentIndex = Configuration.PlannerItems.Count - 1;

        // If the plan is complete and we're not repeating, do NOT auto-reset.
        // Reset is an explicit user action (button), or happens at the repeat boundary.
        if (PlannerIsPlanCompleteNoRepeat())
        {
            if (resetLoopCounter)
                MainWindow.ShowPopup("排程器", "排程已完成。若要再次執行，請先重置進度或啟用循環執行。");
            return false;
        }

        if (!PlannerTryGetCurrentItem(out var item))
            return false;

        if (!ContentHelper.DictionaryContent.TryGetValue(item.TerritoryType, out var content))
        {
            MainWindow.ShowPopup("排程器", $"任務 ({item.TerritoryType}) 在目前的任務模式下不可用。");
            return false;
        }

        CurrentTerritoryContent = content;
        if (ContentPathsManager.DictionaryPaths.TryGetValue(content.TerritoryType, out var container))
        {
            if (!item.PathFileName.IsNullOrEmpty())
            {
                var index = container.Paths.FindIndex(p => p.FileName.Equals(item.PathFileName, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                    CurrentPath = index;
                else
                    container.SelectPath(out CurrentPath);
            }
            else
            {
                container.SelectPath(out CurrentPath);
            }
        }
        if (resetLoopCounter)
            CurrentLoop = 0;
        return true;
    }

    private void PlannerOnDutyCompleted()
    {
        if (ActiveRunContext?.Source != RunSource.Planner)
            return;

        if (!PlannerEnabled)
            return;

        if (!PlannerTryGetCurrentItem(out var item))
            return;

        // Guard against duplicate DutyCompleted events.
        var now = DateTime.UtcNow;
        if (item.TerritoryType == _plannerLastDutyCompletedTerritoryType && (now - _plannerLastDutyCompletedAtUtc).TotalMilliseconds < 2000)
        {
            Svc.Log.Debug("Planner: ignored duplicate DutyCompleted event.");
            return;
        }
        _plannerLastDutyCompletedTerritoryType = item.TerritoryType;
        _plannerLastDutyCompletedAtUtc = now;

        // Count only successful completions.
        item.TargetRuns = Math.Max(1, item.TargetRuns);
        item.CompletedRuns = Math.Clamp(item.CompletedRuns + 1, 0, item.TargetRuns);

        // Persist progress immediately (requirement: progress persistence).
        Configuration.Save();

        // If planner is paused, do not advance/retarget any further.
        if (Configuration.PlannerPaused)
            return;

        // Still have runs remaining for this duty.
        if (item.CompletedRuns < item.TargetRuns)
            return;

        // Duty finished for this item: advance.
        if (Configuration.PlannerCurrentIndex < Configuration.PlannerItems.Count - 1)
        {
            Configuration.PlannerCurrentIndex++;
            Configuration.Save();
            // reset per-duty loop counter so run-to-run logic treats the next duty as a fresh sequence
            CurrentLoop = 0;

            if (!PlannerTryApplyCurrentSelection(resetLoopCounter: false))
            {
                MarkSelfStop("Planner could not apply the next duty.");
                Stage = Stage.Stopped;
                return;
            }

            Svc.Log.Info($"Planner: advanced to index {Configuration.PlannerCurrentIndex + 1}/{Configuration.PlannerItems.Count} ({CurrentTerritoryContent?.Name}).");
            return;
        }

        // End of plan.
        if (Configuration.PlannerRepeat)
        {
            PlannerResetProgress();
            CurrentLoop = 0;
            PlannerTryApplyCurrentSelection(resetLoopCounter: false);
            Svc.Log.Info("Planner: plan cycle completed, repeating from start.");
        }
        else
        {
            MainWindow.ShowPopup("排程器", "排程已完成。");
            Svc.Log.Info("Planner: plan completed.");
        }
    }

    private void MessageReceived(string messageJson)
    {
        if (!Player.Available || messageJson.IsNullOrEmpty())
            return;

        var message = System.Text.Json.JsonSerializer.Deserialize<Message>(messageJson, BuildTab.jsonSerializerOptions);

        if (message == null) return;

        if (message.Sender == Player.Name || message.Action.Count == 0 || Svc.Party.All(x => x.Name.ExtractText() != message.Sender))
            return;

        message.Action.Each(_actions.InvokeAction);
    }

    internal void ExitDuty() => _actions.ExitDuty(new());

    internal void LoadPath()
    {
        try
        {
            if (CurrentTerritoryContent == null || (CurrentTerritoryContent != null && CurrentTerritoryContent.TerritoryType != Svc.ClientState.TerritoryType))
            {
                if (ContentHelper.DictionaryContent.TryGetValue(Svc.ClientState.TerritoryType, out var content))
                    CurrentTerritoryContent = content;
                else
                {
                    Actions.Clear();
                    PathFile = "";
                    return;
                }
            }

            Actions.Clear();
            if (!ContentPathsManager.DictionaryPaths.TryGetValue(Svc.ClientState.TerritoryType, out ContentPathsManager.ContentPathContainer? container))
            {
                PathFile = ContentPathsManager.BuildDefaultPathFilePath(Svc.ClientState.TerritoryType, CurrentTerritoryContent?.EnglishName);
                return;
            }

            // container.Paths 可能是空的,CurrentPath 也可能指到已經被 RemoveInvalidPaths
            // 拿掉的索引 —— 這兩種情形舊寫法的 container.Paths[...] 都會擲
            // ArgumentOutOfRangeException,被下面的 catch 吞成一行 Error,結果是
            // PathFile 與 Actions 整個沒被設定,留著上一個副本的殘值。
            // 索引無效時就重新選一次:SelectPath 會一併把有效的索引寫回 CurrentPath,
            // 容器是空的時候它回 null 並把 CurrentPath 設成 -1,也就是這個檔案既有的
            // 「沒有路徑」值(見 RunContext.PathIndex 與 MainTab)。
            // 📌 原本的 `CurrentPath > -1 ? CurrentPath : 0` 是死條件:能走到那一個分支的
            //    前提就是 CurrentPath >= 0,那個三元運算永遠只會取到 CurrentPath。
            ContentPathsManager.DutyPath? path = CurrentPath >= 0 && CurrentPath < container.Paths.Count ?
                                                     container.Paths[CurrentPath] :
                                                     container.SelectPath(out CurrentPath);

            PathFile = path?.FilePath ?? "";
            // path 可能是 null(SelectPath 在容器沒有任何可用路徑時回 null)。
            // 上一行早就用 ?. 承認了這件事,但 [.. path?.Actions] 在 path 為 null 時
            // 會對 null 做展開 ⇒ 直接 NRE。空路徑要退回空清單。
            Actions = path == null ? [] : [.. path.Actions];
            //Svc.Log.Info($"Loading Path: {CurrentPath} {ListBoxPOSText.Count}");
        }
        catch (Exception e)
        {
            Svc.Log.Error(e.ToString());
            //throw;
        }
    }

    private unsafe bool StopLoop
    {
        get
        {
            // AgentHUD.Instance() 是產生器產出的取得子
            // (`agentModule == null ? null : (AgentHUD*)agentModule->GetAgentByInternalId(AgentId.Hud)`),
            // UIModule/代理人尚未建立時會回 null,原本無條件解參考。
            // 取不到就讓「無休息經驗」這條停止條件不成立 —— 不能拿未知資料去觸發「停止循環」。
            AgentHUD* agentHud = AgentHUD.Instance();

            return Configuration.EnableTerminationActions &&
                                        (CurrentTerritoryContent == null ||
                                        (Configuration.StopLevel && Player.Level >= Configuration.StopLevelInt) ||
                                        (Configuration.StopNoRestedXP && agentHud != null && agentHud->ExpRestedExperience == 0) ||
                                        (Configuration.StopItemQty && (Configuration.StopItemAll
                                            ? Configuration.StopItemQtyItemDictionary.All(x => InventoryManager.Instance()->GetInventoryItemCount(x.Key) >= x.Value.Value)
                                            : Configuration.StopItemQtyItemDictionary.Any(x => InventoryManager.Instance()->GetInventoryItemCount(x.Key) >= x.Value.Value))));
        }
    }

    private void TrustLeveling()
    {
        if (TrustLevelingEnabled && TrustHelper.Members.Any(tm => tm.Value.Level < tm.Value.LevelCap))
        {
            TaskManager.Enqueue(() => Svc.Log.Debug($"Trust Leveling Enabled"), "TrustLeveling-Debug");
            TaskManager.Enqueue(() => TrustHelper.ClearCachedLevels(CurrentTerritoryContent!), "TrustLeveling-ClearCachedLevels");
            TaskManager.Enqueue(() => TrustHelper.GetLevels(CurrentTerritoryContent), "TrustLeveling-GetLevels");
            TaskManager.DelayNext(50);
            TaskManager.Enqueue(() => TrustHelper.State != ActionState.Running, "TrustLeveling-RecheckingTrustLevels");
        }
    }

    private void ClientState_TerritoryChanged(ushort t)
    {
        if (Stage == Stage.Stopped) return;

        Svc.Log.Debug($"ClientState_TerritoryChanged: t={t}");

        CurrentTerritoryType         = t;
        MainListClicked              = false;
        this.Framework_Update_InDuty = _ => { };
        if (t == 0)
            return;
        CurrentPath = -1;

        LoadPath();

        if (!States.HasFlag(PluginState.Looping) || GCTurninHelper.State == ActionState.Running || RepairHelper.State == ActionState.Running || GotoHelper.State == ActionState.Running || GotoInnHelper.State == ActionState.Running || GotoBarracksHelper.State == ActionState.Running || GotoHousingHelper.State == ActionState.Running || CurrentTerritoryContent == null)
        {
            Svc.Log.Debug("We Changed Territories but are doing after loop actions or not running at all or in a Territory not supported by AutoDuty");
            return;
        }

        if (Configuration.ShowOverlay && Configuration.HideOverlayWhenStopped && !States.HasFlag(PluginState.Looping))
        {
            Overlay.IsOpen = false;
            MainWindow.IsOpen = true;
        }

        Action = "";

        if (t != CurrentTerritoryContent.TerritoryType)
        {
            if (CurrentLoop < GetEffectiveLoopTimes())
            {
                TaskManager.Abort();
                TaskManager.Enqueue(() => Svc.Log.Debug($"Loop {CurrentLoop} of {GetEffectiveLoopTimes()}"), "Loop-Debug");
                TaskManager.Enqueue(() => { Stage = Stage.Looping; }, "Loop-SetStage=99");
                TaskManager.Enqueue(() => { States &= ~PluginState.Navigating; }, "Loop-RemoveNavigationState");
                TaskManager.Enqueue(() => PlayerHelper.IsReady, int.MaxValue, "Loop-WaitPlayerReady");
                if (Configuration.EnableBetweenLoopActions)
                {
                    TaskManager.Enqueue(() => { Action = $"Waiting {Configuration.WaitTimeBeforeAfterLoopActions}s"; }, "Loop-WaitTimeBeforeAfterLoopActionsActionSet");
                    TaskManager.Enqueue(() => EzThrottler.Throttle("Loop-WaitTimeBeforeAfterLoopActions", Configuration.WaitTimeBeforeAfterLoopActions * 1000), "Loop-WaitTimeBeforeAfterLoopActionsThrottle");
                    TaskManager.Enqueue(() => EzThrottler.Check("Loop-WaitTimeBeforeAfterLoopActions"), Configuration.WaitTimeBeforeAfterLoopActions * 1000 + ActionsManager.ThrottleTimeoutMarginMs, "Loop-WaitTimeBeforeAfterLoopActionsCheck");
                    TaskManager.Enqueue(() => { Action = $"After Loop Actions"; }, "Loop-AfterLoopActionsSetAction");
                }

                TrustLeveling();

                TaskManager.Enqueue(() =>
                {
                    if (StopLoop)
                    {
                        TaskManager.Enqueue(() => Svc.Log.Info($"Loop Stop Condition Encountered, Stopping Loop"));
                        LoopsCompleteActions();
                    }
                    else
                        LoopTasks();
                }, "Loop-CheckStopLoop");

            }
            else
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"Loops Done"),                                                                                         "Loop-Debug");
                TaskManager.Enqueue(() => { States &= ~PluginState.Navigating; },                                                                               "Loop-RemoveNavigationState");
                TaskManager.Enqueue(() => PlayerHelper.IsReady,                                                                                                 int.MaxValue, "Loop-WaitPlayerReady");
                TaskManager.Enqueue(() => Svc.Log.Debug($"Loop {CurrentLoop} == {GetEffectiveLoopTimes()} we are done Looping, Invoking LoopsCompleteActions"), "Loop-Debug");
                TaskManager.Enqueue(() =>
                                    {
                                        if (this.Configuration.ExecuteBetweenLoopActionLastLoop)
                                            this.LoopTasks(false);
                                        else
                                            this.LoopsCompleteActions();
                                    },     "Loop-LoopCompleteActions");
            }
        }
    }

    private unsafe void Condition_ConditionChange(ConditionFlag flag, bool value)
    {
        if (Stage == Stage.Stopped) return;

        if (flag == ConditionFlag.Unconscious)
        {
            if (value && (Stage != Stage.Dead || DeathHelper.DeathState != PlayerLifeState.Dead))
            {
                Svc.Log.Debug($"We Died, Setting Stage to Dead");
                DeathHelper.DeathState = PlayerLifeState.Dead;
                Stage = Stage.Dead;
            }
            else if (!value && (Stage != Stage.Revived || DeathHelper.DeathState != PlayerLifeState.Revived))
            {
                Svc.Log.Debug($"We Revived, Setting Stage to Revived");
                DeathHelper.DeathState = PlayerLifeState.Revived;
                Stage = Stage.Revived;
            }
            return;
        }
        //Svc.Log.Debug($"{flag} : {value}");
        if (Stage != Stage.Dead && Stage != Stage.Revived && !_recentlyWatchedCutscene && !Conditions.Instance()->WatchingCutscene && flag != ConditionFlag.WatchingCutscene && flag != ConditionFlag.WatchingCutscene78 && flag != ConditionFlag.OccupiedInCutSceneEvent && Stage != Stage.Action && Stage != Stage.Condition && value && States.HasFlag(PluginState.Navigating) && (flag == ConditionFlag.BetweenAreas || flag == ConditionFlag.BetweenAreas51 || flag == ConditionFlag.Jumping61))
        {
            Svc.Log.Info($"Condition_ConditionChange: Indexer Increase and Change Stage to Condition");
            Indexer++;
            VNavmesh_IPCSubscriber.Path_Stop();
            Stage = Stage.Condition;
        }
        if (Conditions.Instance()->WatchingCutscene || flag == ConditionFlag.WatchingCutscene || flag == ConditionFlag.WatchingCutscene78 || flag == ConditionFlag.OccupiedInCutSceneEvent)
        {
            _recentlyWatchedCutscene = true;
            SchedulerHelper.ScheduleAction("RecentlyWatchedCutsceneTimer", () => _recentlyWatchedCutscene = false, 5000);
        }
    }

    public void Run(uint territoryType = 0, int loops = 0, bool startFromZero = true, bool bareMode = false)
    {
        Svc.Log.Debug($"Run: territoryType={territoryType} loops={loops} bareMode={bareMode}");
        if (territoryType > 0)
        {
            if (ContentHelper.DictionaryContent.TryGetValue(territoryType, out var content))
                CurrentTerritoryContent = content;
            else
            {
                Svc.Log.Error($"({territoryType}) is not in our Dictionary as a compatible Duty");
                return;
            }
        }

        if (CurrentTerritoryContent == null)
            return;

        // 這是 Run(RunContext) 也會匯流進來的唯一入口,而且過了上面那幾道退出檢查才算真的要跑。
        // 每開新的一輪就把「本次執行」的逾時計數歸零(工作階段總數不歸零,tooltip 仍看得到)。
        TaskTimeoutWatcher.OnRunStarted();

        // Legacy entrypoint: infer source only when no explicit RunContext was provided.
        ActiveRunContext ??= new RunContext
        {
            Source = territoryType > 0 ? RunSource.Command : RunSource.Manual,
            Duty = CurrentTerritoryContent,
            PathIndex = CurrentPath,
            Loops = 0,
            StartFromZero = startFromZero,
            BareMode = bareMode,
            PlannerItemIndex = -1,
            PersistLoopsToConfig = false,
        };

        // Preserve legacy behavior: only persist loop times from the legacy Run() path
        // when planner is not active.
        if (!PlannerEnabled && loops > 0)
            Configuration.LoopTimes = loops;

        if (bareMode)
        {
            _bareModeSettingsActive |= SettingsActive.BareMode_Active;
            if (Configuration.EnablePreLoopActions)
                _bareModeSettingsActive |= SettingsActive.PreLoop_Enabled;
            if (Configuration.EnableBetweenLoopActions)
                _bareModeSettingsActive |= SettingsActive.BetweenLoop_Enabled;
            if (Configuration.EnableTerminationActions)
                _bareModeSettingsActive |= SettingsActive.TerminationActions_Enabled;
            Configuration.EnablePreLoopActions = false;
            Configuration.EnableBetweenLoopActions = false;
            Configuration.EnableTerminationActions = false;
        }

        Svc.Log.Info($"Running AutoDuty in {CurrentTerritoryContent.EnglishName}, Looping {GetEffectiveLoopTimes()} times{(bareMode ? " in BareMode (No Pre, Between or Termination Loop Actions)" : "")}");

        //MainWindow.OpenTab("Mini");
        if (Configuration.ShowOverlay)
        {
            //MainWindow.IsOpen = false;
            Overlay.IsOpen = true;
        }
        Stage = Stage.Looping;
        States |= PluginState.Looping;
        SetGeneralSettings(false);
        if (!VNavmesh_IPCSubscriber.Path_GetMovementAllowed())
            VNavmesh_IPCSubscriber.Path_SetMovementAllowed(true);
        TaskManager.Abort();
        Svc.Log.Info($"Running {CurrentTerritoryContent.Name} {GetEffectiveLoopTimes()} Times");
        if (!InDungeon)
        {
            CurrentLoop = 0;
            if (Configuration.EnablePreLoopActions)
            {
                if (Configuration.ExecuteCommandsPreLoop)
                {
                    TaskManager.Enqueue(() => Svc.Log.Debug($"ExecutingCommandsPreLoop, executing {Configuration.CustomCommandsTermination.Count} commands"));
                    Configuration.CustomCommandsPreLoop.Each(x => TaskManager.Enqueue(() => Chat.ExecuteCommand(x), "Run-ExecuteCommandsPreLoop"));
                }

                AutoConsume();

                if (LevelingModeEnum == LevelingMode.None)
                    AutoEquipRecommendedGear();

                if (Configuration.AutoRepair && InventoryHelper.CanRepair())
                {
                    TaskManager.Enqueue(() => Svc.Log.Debug($"AutoRepair PreLoop Action"));
                    TaskManager.Enqueue(() => RepairHelper.Invoke(), "Run-AutoRepair");
                    TaskManager.DelayNext("Run-AutoRepairDelay50", 50);
                    TaskManager.Enqueue(() => RepairHelper.State != ActionState.Running, int.MaxValue, "Run-WaitAutoRepairComplete");
                    TaskManager.Enqueue(() => PlayerHelper.IsReadyFull, "Run-WaitAutoRepairIsReadyFull");
                }

                if (Configuration.DutyModeEnum != DutyMode.Squadron && Configuration.RetireMode)
                {
                    TaskManager.Enqueue(() => Svc.Log.Debug($"Retire PreLoop Action"));
                    if (Configuration.RetireLocationEnum == RetireLocation.GC_Barracks)
                        TaskManager.Enqueue(() => GotoBarracksHelper.Invoke(), "Run-GotoBarracksInvoke");
                    else if (Configuration.RetireLocationEnum == RetireLocation.Inn)
                        TaskManager.Enqueue(() => GotoInnHelper.Invoke(), "Run-GotoInnInvoke");
                    else
                        TaskManager.Enqueue(() => GotoHousingHelper.Invoke((Housing)Configuration.RetireLocationEnum), "Run-GotoHousingInvoke");
                    TaskManager.DelayNext("Run-RetireModeDelay50", 50);
                    TaskManager.Enqueue(() => GotoHousingHelper.State != ActionState.Running && GotoBarracksHelper.State != ActionState.Running && GotoInnHelper.State != ActionState.Running, int.MaxValue, "Run-WaitGotoComplete");
                }
            }

            TaskManager.Enqueue(() => Svc.Log.Debug($"Queueing First Run"));
            Queue(CurrentTerritoryContent);
        }
        TaskManager.Enqueue(() => Svc.Log.Debug($"Done Queueing-WaitDutyStarted, NavIsReady"));
        TaskManager.Enqueue(() => Svc.DutyState.IsDutyStarted,          "Run-WaitDutyStarted");
        TaskManager.Enqueue(WaitForNavReady(), int.MaxValue, "Run-WaitNavIsReady");
        TaskManager.Enqueue(() => Svc.Log.Debug($"Start Navigation"));
        TaskManager.Enqueue(() => StartNavigation(startFromZero), "Run-StartNavigation");
        if (CurrentLoop == 0)
            CurrentLoop = 1;
    }

    internal unsafe void LoopTasks(bool queue = true)
    {
        if (CurrentTerritoryContent == null) return;

        // IMPORTANT: queue=false is used to run completion actions (LoopsCompleteActions).
        // Do not apply planner duty selection on that path, or it can interfere with termination/between-loop actions.
        if (queue && ActiveRunContext?.Source == RunSource.Planner && !Configuration.PlannerPaused && !PlannerTryApplyCurrentSelection(resetLoopCounter: false))
        {
            MarkSelfStop("Planner could not apply the next duty.");
            Stage = Stage.Stopped;
            return;
        }

        if (Configuration.EnableBetweenLoopActions)
        {
            if (Configuration.ExecuteCommandsBetweenLoop)
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"ExecutingCommandsBetweenLoops, executing {Configuration.CustomCommandsBetweenLoop.Count} commands"));
                Configuration.CustomCommandsBetweenLoop.Each(x => Chat.ExecuteCommand(x));
                TaskManager.DelayNext("Loop-DelayAfterCommands", 1000);
            }

            if (Configuration.AutoOpenCoffers) 
                EnqueueActiveHelper<CofferHelper>();

            if (Configuration.EnableAutoRetainer && AutoRetainer_IPCSubscriber.IsEnabled && AutoRetainer_IPCSubscriber.AreAnyRetainersAvailableForCurrentChara())
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"AutoRetainer BetweenLoop Actions"));
                if (Configuration.EnableAutoRetainer)
                {
                    TaskManager.Enqueue(() => AutoRetainerHelper.Invoke(), "Loop-AutoRetainer");
                    TaskManager.DelayNext("Loop-Delay50", 50);
                    TaskManager.Enqueue(() => AutoRetainerHelper.State != ActionState.Running, int.MaxValue, "Loop-WaitAutoRetainerComplete");
                }
                else
                {
                    TaskManager.Enqueue(() => AutoRetainer_IPCSubscriber.IsBusy(), 15000, "Loop-AutoRetainerIntegrationDisabledWait15sRetainerSense");
                    TaskManager.Enqueue(() => !AutoRetainer_IPCSubscriber.IsBusy(), int.MaxValue, "Loop-AutoRetainerIntegrationDisabledWaitARNotBusy");
                    TaskManager.Enqueue(() => AutoRetainerHelper.ForceStop(), "Loop-AutoRetainerStop");
                }
            }

            AutoConsume();

            AutoEquipRecommendedGear();

            if (Configuration.AutoRepair && InventoryHelper.CanRepair()) 
                EnqueueActiveHelper<RepairHelper>();

            if (Configuration.AutoExtract && QuestManager.IsQuestComplete(66174)) 
                EnqueueActiveHelper<ExtractHelper>();

            if (Configuration.AutoDesynth) 
                EnqueueActiveHelper<DesynthHelper>();

            if (Configuration.AutoGCTurnin && (!Configuration.AutoGCTurninSlotsLeftBool || InventoryManager.Instance()->GetEmptySlotsInBag() <= Configuration.AutoGCTurninSlotsLeft) && PlayerHelper.GetGrandCompanyRank() > 5)
                EnqueueActiveHelper<GCTurninHelper>();

            if (Configuration.TripleTriadEnabled)
            {
                if (Configuration.TripleTriadRegister) 
                    EnqueueActiveHelper<TripleTriadCardUseHelper>();
                if (Configuration.TripleTriadSell) 
                    EnqueueActiveHelper<TripleTriadCardSellHelper>();
            }

            if (Configuration.DiscardItems) 
                EnqueueActiveHelper<DiscardHelper>();

            if (Configuration.DutyModeEnum != DutyMode.Squadron && Configuration.RetireMode)
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"Retire Between Loop Action"));
                if (Configuration.RetireLocationEnum == RetireLocation.GC_Barracks)
                    TaskManager.Enqueue(() => GotoBarracksHelper.Invoke(), "Loop-GotoBarracksInvoke");
                else if (Configuration.RetireLocationEnum == RetireLocation.Inn)
                    TaskManager.Enqueue(() => GotoInnHelper.Invoke(), "Loop-GotoInnInvoke");
                else
                {
                    Svc.Log.Info($"{(Housing)Configuration.RetireLocationEnum} {Configuration.RetireLocationEnum}");
                    TaskManager.Enqueue(() => GotoHousingHelper.Invoke((Housing)Configuration.RetireLocationEnum), "Loop-GotoHousingInvoke");
                }
                TaskManager.DelayNext("Loop-Delay50", 50);
                TaskManager.Enqueue(() => GotoHousingHelper.State != ActionState.Running && GotoBarracksHelper.State != ActionState.Running && GotoInnHelper.State != ActionState.Running, int.MaxValue, "Loop-WaitGotoComplete");
            }
        }

        void EnqueueActiveHelper<T>() where T : ActiveHelperBase<T>, new()
        {
            TaskManager.Enqueue(() => Svc.Log.Debug($"Enqueueing {typeof(T).Name}"), "Loop-ActiveHelper");
            TaskManager.Enqueue(() => ActiveHelperBase<T>.Invoke(), $"Loop-{typeof(T).Name}");
            TaskManager.DelayNext("Loop-Delay50", 50);
            TaskManager.Enqueue(() => ActiveHelperBase<T>.State != ActionState.Running, int.MaxValue, $"Loop-Wait-{typeof(T).Name}-Complete");
            TaskManager.Enqueue(() => PlayerHelper.IsReadyFull, "Loop-WaitIsReadyFull");
        }


        if (!queue)
        {
            LoopsCompleteActions();
            return;
        }

        if (LevelingEnabled)
        {
            Svc.Log.Info("Leveling Enabled");
            Content? duty = LevelingHelper.SelectHighestLevelingRelevantDuty(LevelingModeEnum == LevelingMode.Trust);
            if (duty != null)
            {
                if (this.LevelingModeEnum == LevelingMode.Support && Configuration.PreferTrustOverSupportLeveling && duty.ClassJobLevelRequired > 70)
                {
                    levelingModeEnum           = LevelingMode.Trust;
                    Configuration.dutyModeEnum = DutyMode.Trust;

                    Content? dutyTrust = LevelingHelper.SelectHighestLevelingRelevantDuty(true);

                    if (duty != dutyTrust)
                    {
                        levelingModeEnum           = LevelingMode.Support;
                        Configuration.dutyModeEnum = DutyMode.Support;
                    }
                }

                Svc.Log.Info("Next Leveling Duty: " + duty.Name);
                CurrentTerritoryContent = duty;
                ContentPathsManager.DictionaryPaths[duty.TerritoryType].SelectPath(out CurrentPath);
            }
            else
            {
                CurrentLoop = GetEffectiveLoopTimes();
                LoopsCompleteActions();
                return;
            }
        }
        TaskManager.Enqueue(() => Svc.Log.Debug($"Registering New Loop"));
        Queue(CurrentTerritoryContent);
        TaskManager.Enqueue(() => Svc.Log.Debug($"Incrementing LoopCount, Setting Action Var, Wait for CorrectTerritory, PlayerIsValid, DutyStarted, and NavIsReady"));
        TaskManager.Enqueue(() => CurrentLoop++, "Loop-IncrementCurrentLoop");
        TaskManager.Enqueue(() => { Action = $"Looping: {CurrentTerritoryContent.Name} {CurrentLoop} of {GetEffectiveLoopTimes()}"; }, "Loop-SetAction");
        TaskManager.Enqueue(() => Svc.ClientState.TerritoryType == CurrentTerritoryContent.TerritoryType, int.MaxValue, "Loop-WaitCorrectTerritory");
        TaskManager.Enqueue(() => PlayerHelper.IsValid, int.MaxValue, "Loop-WaitPlayerValid");
        TaskManager.Enqueue(() => Svc.DutyState.IsDutyStarted, int.MaxValue, "Loop-WaitDutyStarted");
        TaskManager.Enqueue(WaitForNavReady(), int.MaxValue, "Loop-WaitNavReady");
        TaskManager.Enqueue(() => Svc.Log.Debug($"StartNavigation"));
        TaskManager.Enqueue(() => StartNavigation(true), "Loop-StartNavigation");
    }

    private void LoopsCompleteActions()
    {

        SetGeneralSettings(false);

        if (Configuration.EnableTerminationActions)
        {
            TaskManager.Enqueue(() => PlayerHelper.IsReadyFull);
            TaskManager.Enqueue(() => Svc.Log.Debug($"TerminationActions are Enabled"));
            if (Configuration.ExecuteCommandsTermination)
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"ExecutingCommandsTermination, executing {Configuration.CustomCommandsTermination.Count} commands"));
                Configuration.CustomCommandsTermination.Each(x => Chat.ExecuteCommand(x));
            }

            if (Configuration.PlayEndSound)
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"Playing End Sound"));
                SoundHelper.StartSound(Configuration.PlayEndSound, Configuration.CustomSound, Configuration.SoundEnum);
            }

            if (Configuration.TerminationMethodEnum == TerminationMode.Kill_PC)
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"Killing PC"));
                if (!Configuration.TerminationKeepActive)
                {
                    Configuration.TerminationMethodEnum = TerminationMode.Do_Nothing;
                    Configuration.Save();
                }
                TaskManager.Enqueue(() =>
                                    {
                                        if (OperatingSystem.IsWindows())
                                        {
                                            ProcessStartInfo startinfo = new("shutdown.exe", "-s -t 20");
                                            Process.Start(startinfo);
                                        }
                                        else if (OperatingSystem.IsLinux())
                                        {
                                            //Educated guess
                                            ProcessStartInfo startinfo = new("shutdown", "-t 20");
                                            Process.Start(startinfo);
                                        }
                                        else if (OperatingSystem.IsMacOS())
                                        {
                                            //hell if I know
                                        }
                                    }, "Enqueuing SystemShutdown");
                TaskManager.Enqueue(() => Chat.ExecuteCommand($"/xlkill"), "Killing the game");
            }
            else if (Configuration.TerminationMethodEnum == TerminationMode.Kill_Client)
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"Killing Client"));
                if (!Configuration.TerminationKeepActive)
                {
                    Configuration.TerminationMethodEnum = TerminationMode.Do_Nothing;
                    Configuration.Save();
                }

                TaskManager.Enqueue(() => Chat.ExecuteCommand($"/xlkill"), "Killing the game");
            }
            else if (Configuration.TerminationMethodEnum == TerminationMode.Logout)
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"Logging Out"));
                if (!Configuration.TerminationKeepActive)
                {
                    Configuration.TerminationMethodEnum = TerminationMode.Do_Nothing;
                    Configuration.Save();
                }

                TaskManager.Enqueue(() => PlayerHelper.IsReady);
                TaskManager.DelayNext(2000);
                TaskManager.Enqueue(() => Chat.ExecuteCommand($"/logout"));
                TaskManager.Enqueue(() => AddonHelper.ClickSelectYesno());
            }
            else if (Configuration.TerminationMethodEnum == TerminationMode.Start_AR_Multi_Mode)
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"Starting AR Multi Mode"));
                TaskManager.Enqueue(() => Chat.ExecuteCommand($"/ays multi e"));
            }
        }

        Svc.Log.Debug($"Removing Looping, Setting CurrentLoop to 0, and Setting Stage to Stopped");

        States      &= ~PluginState.Looping;
        CurrentLoop =  0;
        // 這一停是排程延遲執行的,所以原因必須跟賦值寫在同一個 lambda 裡 ——
        // 旗標的存活期才不會被中間插進來的別的停止動作吃掉。
        TaskManager.Enqueue(() => SchedulerHelper.ScheduleAction("SetStageStopped", () =>
        {
            MarkSelfStop("All loops finished.", Configuration.EnableTerminationActions && Configuration.PlayEndSound);
            Stage = Stage.Stopped;
        }, 1));
    }

    private void AutoEquipRecommendedGear()
    {
        if (Configuration.AutoEquipRecommendedGear)
        {
            TaskManager.Enqueue(() => Svc.Log.Debug($"AutoEquipRecommendedGear Between Loop Action"));
            TaskManager.Enqueue(() => AutoEquipHelper.Invoke(), "AutoEquipRecommendedGear-Invoke");
            TaskManager.DelayNext("AutoEquipRecommendedGear-Delay50", 50);
            TaskManager.Enqueue(() => AutoEquipHelper.State != ActionState.Running, int.MaxValue, "AutoEquipRecommendedGear-WaitAutoEquipComplete");
            TaskManager.Enqueue(() => PlayerHelper.IsReadyFull, "AutoEquipRecommendedGear-WaitANotIsOccupied");
        }
    }

    private void AutoConsume()
    {
        if (Configuration.AutoConsume)
        {
            TaskManager.Enqueue(() => Svc.Log.Debug($"AutoConsume PreLoop Action"));
            Configuration.AutoConsumeItemsList.Each(x =>
            {
                var isAvailable = InventoryHelper.IsItemAvailable(x.Value.ItemId, x.Value.CanBeHq);
                if (isAvailable)
                {
                    if (Configuration.AutoConsumeIgnoreStatus)
                        TaskManager.Enqueue(() => InventoryHelper.UseItemUntilAnimationLock(x.Value.ItemId, x.Value.CanBeHq), $"AutoConsume - {x.Value.Name} is available: {isAvailable}");
                    else
                        TaskManager.Enqueue(() => InventoryHelper.UseItemUntilStatus(x.Value.ItemId, x.Key, Plugin.Configuration.AutoConsumeTime * 60, x.Value.CanBeHq), $"AutoConsume - {x.Value.Name} is available: {isAvailable}");
                }
                TaskManager.DelayNext("AutoConsume-DelayNext50", 50);
                TaskManager.Enqueue(() => PlayerHelper.IsReadyFull, "AutoConsume-WaitPlayerIsReadyFull");
                TaskManager.DelayNext("AutoConsume-DelayNext250", 250);
            });
        }
    }

    private void Queue(Content content)
    {
        if (Configuration.DutyModeEnum == DutyMode.Variant)
            _variantManager.RegisterVariantDuty(content);
        else if (Configuration.DutyModeEnum.EqualsAny(DutyMode.Regular, DutyMode.Trial, DutyMode.Raid, DutyMode.Support, DutyMode.Trust))
        {
            TaskManager.Enqueue(() => QueueHelper.Invoke(content, Configuration.DutyModeEnum), "Queue-Invoke");
            TaskManager.DelayNext("Queue-Delay50", 50);
            TaskManager.Enqueue(() => QueueHelper.State != ActionState.Running, int.MaxValue, "Queue-WaitQueueComplete");
        }
        else if (Configuration.DutyModeEnum == DutyMode.Squadron)
        {
            TaskManager.Enqueue(() => GotoBarracksHelper.Invoke(), "Queue-GotoBarracksInvoke");
            TaskManager.DelayNext("Queue-GotoBarracksDelay50", 50);
            TaskManager.Enqueue(() => GotoBarracksHelper.State != ActionState.Running && GotoInnHelper.State != ActionState.Running, int.MaxValue, "Queue-WaitGotoComplete");
            _squadronManager.RegisterSquadron(content);
        }
        TaskManager.Enqueue(() => !PlayerHelper.IsValid, "Queue-WaitNotValid");
        TaskManager.Enqueue(() => PlayerHelper.IsValid, int.MaxValue, "Queue-WaitValid");
    }

    private void StageReadingPath()
    {
        if (!PlayerHelper.IsValid || !EzThrottler.Check("PathFindFailure") || Indexer == -1 || Indexer >= Actions.Count)
            return;

        Action = $"{(Actions.Count > Indexer ? Plugin.Actions[Indexer].ToCustomString() : "")}";

        PathAction = Actions[Indexer];

        bool sync = !this.Configuration.Unsynced || !this.Configuration.DutyModeEnum.EqualsAny(DutyMode.Raid, DutyMode.Regular, DutyMode.Trial);
        if (PathAction.Tag.HasFlag(ActionTag.Unsynced) && sync)
        {
            Svc.Log.Debug($"Skipping path entry {Actions[Indexer]} because we are synced");
            Indexer++;
            return;
        }

        if (PathAction.Tag.HasFlag(ActionTag.W2W) && !Configuration.IsW2W(unsync: !sync))
        {
            Svc.Log.Debug($"Skipping path entry {Actions[Indexer]} because we are not W2W-ing");
            this.Indexer++;
            return;
        }

        if (PathAction.Tag.HasFlag(ActionTag.Synced) && Configuration.Unsynced)
        {
            Svc.Log.Debug($"Skipping path entry {Actions[Indexer]} because we are unsynced");
            Indexer++;
            return;
        }

        if (PathAction.Tag.HasFlag(ActionTag.Comment))
        {
            Svc.Log.Debug($"Skipping path entry {Actions[Indexer].Name} because it is a comment");
            Indexer++;
            return;
        }

        if (PathAction.Tag.HasFlag(ActionTag.Revival))
        {
            Svc.Log.Debug($"Skipping path entry {Actions[Indexer].Name} because it is a Revival Tag");
            Indexer++;
            return;
        }

        if ((SkipTreasureCoffer || !Configuration.LootTreasure || Configuration.LootBossTreasureOnly) && PathAction.Tag.HasFlag(ActionTag.Treasure))
        {
            Svc.Log.Debug($"Skipping path entry {Actions[Indexer].Name} because we are either in revival mode, LootTreasure is off or BossOnly");
            Indexer++;
            return;
        }

        // 步驟條件:全部成立才執行。空集合(絕大多數步驟)不進這個分支,行為與加入條件之前相同。
        if (PathAction.Conditions.Count > 0)
        {
            PathActionCondition? unfulfilled = null;
            foreach (PathActionCondition condition in PathAction.Conditions)
            {
                bool ok;
                try
                {
                    ok = condition.IsFulfilled();
                }
                catch (Exception ex)
                {
                    // 條件算不出來就當成不成立(跳過該步驟),而不是讓例外把整個 Framework.Update 打斷。
                    Svc.Log.Warning($"Path condition {condition.ParseKey} threw, treating as not fulfilled: {ex}");
                    ok = false;
                }

                if (!ok)
                {
                    unfulfilled = condition;
                    break;
                }
            }

            if (unfulfilled != null)
            {
                Svc.Log.Debug($"Skipping path entry {PathAction.Name} because condition [{unfulfilled.Describe()}] is not fulfilled");
                Indexer++;
                return;
            }
        }

        // 隊形:非王步驟時跟著坦克走,除非這一步標了 NoPartyCoherency。
        // ⚠️ StayCloseToTank 內部還有 Configuration.PartyCoherency 這道閘門(預設關),關著時一律送 None。
        BossMod_IPCSubscriber.StayCloseToTank(!(PathAction.Name.Equals("Boss", StringComparison.InvariantCultureIgnoreCase) ||
                                                PathAction.Flags.HasFlag(PathActionFlags.NoPartyCoherency)));

        if (PathAction.Position == Vector3.Zero)
        {
            Stage = Stage.Action;
            return;
        }

        if (!VNavmesh_IPCSubscriber.SimpleMove_PathfindInProgress() && !VNavmesh_IPCSubscriber.Path_IsRunning())
        {
            Chat.Instance.ExecuteCommand("/automove off");
            VNavmesh_IPCSubscriber.Path_RequestTolerance(0.25f);
            if (PathAction.Name == "MoveTo" && PathAction.Arguments.Count > 0 && bool.TryParse(PathAction.Arguments[0], out bool useMesh) && !useMesh)
            {
                VNavmesh_IPCSubscriber.Path_MoveTo([PathAction.Position], false);
            }
            else
                VNavmesh_IPCSubscriber.SimpleMove_PathfindAndMoveTo(PathAction.Position, false);
            Stage = Stage.Moving;
        }
    }

    private void StageMoving()
    {
        if (!PlayerHelper.IsReady || Indexer == -1 || Indexer >= Actions.Count)
            return;

        if (Configuration.DutyModeEnum == DutyMode.Regular && Svc.Party.PartyId > 0)
        {
            Message message = new()
            {
                Sender = Player.Name,
                Action =
                [
                    new PathAction(){ Name = "Follow", Arguments = [$"{Player.Name}"] }
                ]
            };

            var messageJson = System.Text.Json.JsonSerializer.Serialize(message, BuildTab.jsonSerializerOptions);

            //_messageBusSend.PublishAsync(Encoding.UTF8.GetBytes(messageJson));
        }

        if (Indexer == -1 || Indexer >= Actions.Count)
            return;

        Action = $"{Plugin.Actions[Indexer].ToCustomString()}";

        unsafe
        {
            if (PlayerHelper.IsMoving && EzThrottler.Throttle("AutoSprint", 300) && ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 4) == 0 && ActionManager.Instance()->QueuedActionId != 4 && !PlayerHelper.IsCasting)
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 4);
        }

        if (PlayerHelper.InCombat && Plugin.StopForCombat)
        {
            if (Configuration.AutoManageRotationPluginState && !Configuration.UsingAlternativeRotationPlugin)
                SetRotationPluginSettings(true);

            if (Configuration.UnsyncedKeepMovingInCombat && Configuration.IsUnsyncActive())
            {
                // 解限模式「交戰中繼續走到定點」:不切進 Waiting_For_Combat,讓 vnavmesh 繼續跑路徑,
                // 技能交給輪替外掛(BossMod / Wrath)的自動循環 —— 移動中本來就可以放即時技。
                // 🔴 走位權必須只有一個主人:BossMod 的 NormalMovement 是另一套獨立的 pathfinder,
                //    兩邊同時要走會互相抵銷、角色原地不動(這個形狀在 IPCSubscriber.SetPreset 的註解
                //    裡已經記錄過)。所以這裡明確把 BossMod 的移動關掉,由 vnavmesh 獨佔走位。
                //    ⚠️ SetMovement 只有在 AutoManageBossModAISettings 開著時才會真的送出去;
                //    沒開的話 AutoDuty 本來就沒有幫忙套 BossMod 的 preset,也就沒有走位權之爭。
                if (!_unsyncedKeepMovingArmed)
                {
                    _unsyncedKeepMovingArmed = true;
                    BossMod_IPCSubscriber.SetMovement(false);
                    Svc.Log.Information("[解限] 交戰中繼續走到定點:已關閉 BossMod 的自動移動,改由 vnavmesh 獨佔走位。");
                }

                KeepMovingTargetAssist();
            }
            else
            {
                VNavmesh_IPCSubscriber.Path_Stop();
                Stage = Stage.Waiting_For_Combat;
                return;
            }
        }
        else if (_unsyncedKeepMovingArmed && !PlayerHelper.InCombat)
        {
            // 離開戰鬥就把移動權還給 BossMod,免得這個設定的影響漏到後面的一般導航。
            // 🔴 這裡一定要再判一次 !InCombat:上面那個 if 為假也可能是「還在戰鬥中,但
            //    StopForCombat 被路徑步驟關掉了」,那種情況的 SetMovement(false) 是那個步驟
            //    自己要的,不可以被我們還原掉。
            _unsyncedKeepMovingArmed = false;
            BossMod_IPCSubscriber.SetMovement(true);
            Svc.Log.Information("[解限] 交戰結束:已還原 BossMod 的自動移動。");
        }

        if (StuckHelper.IsStuck(out byte stuckCount))
        {
            VNavmesh_IPCSubscriber.Path_Stop();
            if (Configuration.RebuildNavmeshOnStuck && stuckCount >= Configuration.RebuildNavmeshAfterStuckXTimes)
            {
                // 🔴 觸發後必須把卡住計數歸零,否則計數只會一直往上加(玩家還卡著就不會滿足
                //    StuckHelper 那個「一段時間沒再卡住」的歸零條件),結果是門檻一旦越過,
                //    之後每一次卡住偵測都再重建一次網格 —— 而全量重建期間玩家更動不了,
                //    形成自我維持迴圈(實機 log 曾連續 128 次全量重建)。
                Svc.Log.Information($"[卡住處理] 連續卡住達 {Configuration.RebuildNavmeshAfterStuckXTimes} 次,要求 vnavmesh 重建網格(計數已歸零,下次要再累積同樣次數才會重建)。");
                VNavmesh_IPCSubscriber.Nav_Rebuild();
                StuckHelper.ResetStuckCount();
            }
            Stage = Stage.Reading_Path;
            return;
        }

        if ((!VNavmesh_IPCSubscriber.SimpleMove_PathfindInProgress() && VNavmesh_IPCSubscriber.Path_NumWaypoints() == 0) || (!PathAction.Name.IsNullOrEmpty() && PathAction.Position != Vector3.Zero && ObjectHelper.GetDistanceToPlayer(PathAction.Position) <= (PathAction.Name.EqualsIgnoreCase("Interactable") ? 2f : 0.25f)))
        {
            if (PathAction.Name.IsNullOrEmpty() || PathAction.Name.Equals("MoveTo") || PathAction.Name.Equals("TreasureCoffer") || PathAction.Name.Equals("Revival"))
            {
                Stage = Stage.Reading_Path;
                Indexer++;
            }
            else
            {
                VNavmesh_IPCSubscriber.Path_Stop();
                Stage = Stage.Action;
            }

            return;
        }

        if (EzThrottler.Throttle("BossChecker", 25) && PathAction.Name.Equals("Boss") && PathAction.Position != Vector3.Zero && ObjectHelper.BelowDistanceToPlayer(PathAction.Position, 50, 10))
        {
            BossObject = ObjectHelper.GetBossObject(25);
            if (BossObject != null)
            {
                VNavmesh_IPCSubscriber.Path_Stop();
                Stage = Stage.Action;
                return;
            }
        }
    }

    private void StageAction()
    {
        if (Indexer == -1 || Indexer >= Actions.Count)
            return;
        
        if (this.Configuration is { AutoManageRotationPluginState: true, UsingAlternativeRotationPlugin: false } && !Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent])
            SetRotationPluginSettings(true);
        
        if (!TaskManager.IsBusy)
        {
            BossMod_IPCSubscriber.DisableRealAIPreset();
            Stage = Stage.Reading_Path;
            Indexer++;
            return;
        }
    }

    /// <summary>
    /// 解限模式「交戰中繼續走」用的目標補位:沒有目標時鎖定最近的、已經在戰鬥中的敵對 NPC,
    /// 讓輪替外掛有東西可以打。(這條路徑不會進 Waiting_For_Combat,所以那邊的鎖定邏輯不會跑。)
    /// </summary>
    /// <remarks>
    /// 🔴 只在同一幀內掃描並設定目標,不把任何 IGameObject 留到下一幀。
    /// 判敵意刻意只用 Dalamud 的 <c>StatusFlags</c>/<c>BattleNpcKind</c>(直接讀 CS 結構欄位),
    /// 不走 ECommons 那個吃寫死特徵碼的 IsHostile()/GetNameplateKind(),
    /// 免得特徵碼在台服失效時靜默失準。
    /// </remarks>
    private static void KeepMovingTargetAssist()
    {
        if (Svc.Targets.Target != null)
            return;

        IGameObject? nearest         = null;
        float        nearestDistance = float.MaxValue;

        foreach (IGameObject obj in Svc.Objects)
        {
            if (obj is not IBattleNpc battleNpc
                || battleNpc.BattleNpcKind != Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Enemy
                || !obj.IsTargetable
                || obj.IsDead
                || !battleNpc.StatusFlags.HasFlag(Dalamud.Game.ClientState.Objects.Enums.StatusFlags.Hostile)
                || !battleNpc.StatusFlags.HasFlag(Dalamud.Game.ClientState.Objects.Enums.StatusFlags.InCombat))
                continue;

            float distance = ObjectHelper.GetDistanceToPlayer(obj);

            if (distance > 75f || distance >= nearestDistance)
                continue;

            nearest         = obj;
            nearestDistance = distance;
        }

        if (nearest != null)
            Svc.Targets.Target = nearest;
    }

    private void StageWaitingForCombat()
    {
        if (!EzThrottler.Throttle("CombatCheck", 250) || !PlayerHelper.IsReady || Indexer == -1 || Indexer >= Actions.Count || PathAction == null)
            return;

        Action = $"Waiting For Combat";

        // AI.SetPreset 只在 InCombat 時才真的武裝(見 IPCSubscriber.SetPreset)。進副本時打的
        // 那一次還沒進戰鬥、武裝不到卻已經吃掉 5 秒節流;若進場沒幾秒就撞到雜魚,StageMoving
        // 的補打會被節流擋掉並立刻切進這裡,而這裡原本沒有任何重試 —— 角色會「有分配 preset
        // 但沒開機」乾站著,直到走進王 50 碼內才由 StageAction 補回。改成持續嘗試(受同一個
        // 節流保護,不會洗頻),節流一過期就補上。
        if (PlayerHelper.InCombat && this.Configuration is { AutoManageRotationPluginState: true, UsingAlternativeRotationPlugin: false })
            SetRotationPluginSettings(true);

        if (ReflectionHelper.Avarice_Reflection.PositionalChanged(out Positional positional) && !Plugin.Configuration.UsingAlternativeBossPlugin && IPCSubscriber_Common.IsReady("BossModReborn"))
            BossMod_IPCSubscriber.SetPositional(positional);

        if (PathAction.Name.Equals("Boss") && PathAction.Position != Vector3.Zero && ObjectHelper.GetDistanceToPlayer(PathAction.Position) < 50)
        {
            BossObject = ObjectHelper.GetBossObject(25);
            if (BossObject != null)
            {
                VNavmesh_IPCSubscriber.Path_Stop();
                Stage = Stage.Action;
                return;
            }
        }

        if (PlayerHelper.InCombat)
        {
            if (Svc.Targets.Target == null)
            {
                //find and target closest attackable npc, if we are not targeting
                var gos = ObjectHelper.GetObjectsByObjectKind(Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)?.FirstOrDefault(o => o.GetNameplateKind() is NameplateKind.HostileEngagedSelfUndamaged or NameplateKind.HostileEngagedSelfDamaged && ObjectHelper.GetBattleDistanceToPlayer(o) <= 75);

                if (gos != null)
                    Svc.Targets.Target = gos;
            }
            if (Configuration.AutoManageBossModAISettings)
            {
                if (Svc.Targets.Target != null)
                {
                    var enemyCount = ObjectFunctions.GetAttackableEnemyCountAroundPoint(Svc.Targets.Target.Position, 15);

                    if (!VNavmesh_IPCSubscriber.SimpleMove_PathfindInProgress() && VNavmesh_IPCSubscriber.Path_IsRunning())
                        VNavmesh_IPCSubscriber.Path_Stop();

                    if (enemyCount > 2)
                    {
                        Svc.Log.Debug($"Changing MaxDistanceToTarget to {Configuration.MaxDistanceToTargetAoEFloat}, because enemy count = {enemyCount}");
                        BossMod_IPCSubscriber.SetRange(Configuration.MaxDistanceToTargetAoEFloat);
                    }
                    else
                    {
                        Svc.Log.Debug($"Changing MaxDistanceToTarget to {this.Configuration.MaxDistanceToTargetFloat}, because enemy count = {enemyCount}");
                        BossMod_IPCSubscriber.SetRange(this.Configuration.MaxDistanceToTargetFloat);
                    }
                }
            }
            else if (!VNavmesh_IPCSubscriber.SimpleMove_PathfindInProgress() && VNavmesh_IPCSubscriber.Path_IsRunning())
                VNavmesh_IPCSubscriber.Path_Stop();
        }
        else if (!PlayerHelper.InCombat && !VNavmesh_IPCSubscriber.SimpleMove_PathfindInProgress())
        {
            BossMod_IPCSubscriber.SetRange(Configuration.MaxDistanceToTargetFloat);
            BossMod_IPCSubscriber.DisableRealAIPreset();

            VNavmesh_IPCSubscriber.Path_Stop();
            Stage = Stage.Reading_Path;
        }
    }

    public void StartNavigation(bool startFromZero = true)
    {
        Svc.Log.Debug($"StartNavigation: startFromZero={startFromZero}");
        if (ContentHelper.DictionaryContent.TryGetValue(Svc.ClientState.TerritoryType, out var content))
        {
            CurrentTerritoryContent = content;
            PathFile = ContentPathsManager.BuildDefaultPathFilePath(Svc.ClientState.TerritoryType, content.EnglishName);
            LoadPath();
        }
        else
        {
            CurrentTerritoryContent = null;
            PathFile = "";
            MainWindow.ShowPopup("Error".Loc(), "Unable to load content for Territory".Loc());
            return;
        }
        //MainWindow.OpenTab("Mini");
        if (Configuration.ShowOverlay)
        {
            //MainWindow.IsOpen = false;
            Overlay.IsOpen = true;
        }
        MainListClicked = false;
        Stage = Stage.Reading_Path;
        States |= PluginState.Navigating;
        StopForCombat = true;
        // 對齊鏡頭:設定說明承諾的是「開始時打開,結束時若本來沒開就關回去」
        // (Config.cs 的 HelpMarker:"...and disable it when done if it was not set")。
        // 這裡只負責前半段,並記下「進來時是關的」;還原在 SetGeneralSettings(true)。
        // 🔴 一定要先過 IsEnabled:vnavmesh 沒裝時 SafeWrapper.IPCException 會把
        //    Path_GetAlignCamera() 靜默吃成 default(false),少了這道閘門就會把
        //    「根本沒有 vnavmesh」誤記成「本來是關的」,結束時對著空氣還原。
        if (Configuration.AutoManageVnavAlignCamera && VNavmesh_IPCSubscriber.IsEnabled && !VNavmesh_IPCSubscriber.Path_GetAlignCamera())
        {
            _settingsActive |= SettingsActive.Vnav_Align_Camera_On;
            Svc.Log.Information("vnavmesh 對齊鏡頭:進導航前是關的,導航期間打開,整趟結束時會關回去");
            VNavmesh_IPCSubscriber.Path_SetAlignCamera(true);
        }

        if (this.Configuration is { AutoManageBossModAISettings: true, BM_UpdatePresetsAutomatically: true })
        {
            BossMod_IPCSubscriber.RefreshPreset("AutoDuty",         Resources.AutoDutyPreset);
            BossMod_IPCSubscriber.RefreshPreset("AutoDuty Passive", Resources.AutoDutyPassivePreset);
        }

        if (Configuration.AutoManageBossModAISettings)
            SetBMSettings();
        if (this.Configuration is { AutoManageRotationPluginState: true, UsingAlternativeRotationPlugin: false })
            SetRotationPluginSettings(true);
        if (Configuration.LootTreasure)
        {
            if (PandorasBox_IPCSubscriber.IsEnabled)
                PandorasBox_IPCSubscriber.SetFeatureEnabled("Automatically Open Chests", this.Configuration.LootMethodEnum is LootMethod.Pandora or LootMethod.All);
            this._lootTreasure = this.Configuration.LootMethodEnum is LootMethod.AutoDuty or LootMethod.All;
        }
        else
        {
            if (PandorasBox_IPCSubscriber.IsEnabled)
                PandorasBox_IPCSubscriber.SetFeatureEnabled("Automatically Open Chests", false);
            this._lootTreasure = false;
        }
        Svc.Log.Info("Starting Navigation");
        if (startFromZero)
            Indexer = 0;
    }

    private void DoneNavigating()
    {
        States &= ~PluginState.Navigating;
        // 路徑真的跑完了。Update() 尾端那個通用 fallback 的條件是 Stage > Stage.Condition(4),
        // 而這時 Stage 是 Reading_Path(1) ⇒ 永遠蓋不到這裡;最後一步若是單純 MoveTo,Action 會
        // 停在舊字串,讓 CheckFinishing 誤判「還沒做完」白等一輪 60 秒逃生口。
        Action = string.Empty;
        this.CheckFinishing();
    }

    /// <summary>
    /// Nav.IsReady only means vnavmesh has *a* navmesh object loaded, not that it actually
    /// covers the player's current position (some instanced solo/Trust duties stream in
    /// geometry vnavmesh's auto-build never fully captures). Passively waiting on IsReady
    /// forever, as the old code did, hangs indefinitely in that case. Force a full rebuild
    /// (no cache) if it hasn't become ready within a few seconds.
    /// </summary>
    private static Func<bool?> WaitForNavReady()
    {
        DateTime waitStart = DateTime.MinValue;
        return () =>
        {
            if (VNavmesh_IPCSubscriber.Nav_IsReady())
                return true;
            if (waitStart == DateTime.MinValue)
                waitStart = DateTime.Now;
            else if ((DateTime.Now - waitStart).TotalSeconds >= 5)
            {
                Svc.Log.Info("Navmesh not ready after 5s, forcing rebuild");
                VNavmesh_IPCSubscriber.Nav_Rebuild();
                waitStart = DateTime.Now;
            }
            return false;
        };
    }

    // CheckFinishing 底下那個「等 Action／路徑真的跑完」的守衛自己沒有逾時：如果戰後的
    // Interactable 步驟卡在無止境重試（例如撿寶那條 InteractableCheck 迴圈永遠到不了結束條件），
    // Action 就永遠不會變回空字串，CheckFinishing-WaitAction 會每 500ms 把自己重新排一次、
    // 永遠退不出副本。這個欄位記錄「目前這一段等待」從什麼時候開始，超過上限就放棄等待直接退本，
    // 不要無限期掛著。
    private DateTime? _checkFinishingWaitSince;

    // 🔴 逃生口的長度。下游（okaminico）取 60 秒。
    //    ⚠️ 它與 ActionsManager.Interactable 的 90 秒等待有張力：極火龍那種「個人化剝取物件」
    //    實測要等 40 幾秒才會變成 IsTargetable，正常情況塞得進 60 秒，但若伺服器更慢，
    //    這個逃生口會先到期把人拉出副本。兩個數字都沒有台服實機驗證，要調就一起調。
    private const int CheckFinishingWaitTimeoutSeconds = 60;

    private unsafe void CheckFinishing()
    {
        //we finished lets exit the duty or stop
        var plannerRun = ActiveRunContext?.Source == RunSource.Planner;

        if (Configuration.AutoExitDuty || plannerRun || CurrentLoop < GetEffectiveLoopTimes())
        {
            if (!Stage.EqualsAny(Stage.Stopped, Stage.Paused)                                     &&
                (!Configuration.OnlyExitWhenDutyDone || this.DutyState == DutyState.DutyComplete) &&
                !this.States.HasFlag(PluginState.Navigating))
            {
                // 骰寶箱（Need/Greed/Pass）視窗還開著就先別退本 —— 就算是靠別的外掛（例如
                // LazyLoot）自動幫忙骰，也要親自確認那個視窗真的關閉了才算數，不能只憑
                // Action／路徑這種間接狀態去猜「應該骰完了」。這個等待刻意不受下面的逾時
                // 保底影響：骰寶箱是玩家在意、有真實意義的等待，不是卡死的 bug，強制跳過
                // 反而會在還沒骰完的時候就把大家拉出副本，弄丟稀有素材／坐騎的擲骰機會。
                if (GenericHelpers.TryGetAddonByName("NeedGreed", out AtkUnitBase* needGreedAddon) && GenericHelpers.IsAddonReady(needGreedAddon))
                {
                    SchedulerHelper.ScheduleAction("CheckFinishing-WaitNeedGreed", this.CheckFinishing, 500);
                    return;
                }

                // 遊戲可能在戰後的路徑步驟還沒跑完時就把 DutyCompleted 立起來 —— 例如極火龍
                // 打完後的剝取素材是「Boss」步驟之後的幾個 Interactable 動作，那時候並不處於
                // Navigating，所以上面那個 PluginState.Navigating 判斷蓋不到，以前就會在互動到
                // 一半的時候被拉出副本。
                //
                // 只看 Action 不夠：Update() 裡在 DoneNavigating 判斷之後有一行
                // `Action = Stage.ToCustomString()`，每一格都會把它重設成 Stage 自己的名字，
                // 所以在「戰鬥結束」與「下一個路徑動作真的開始」之間，它有可能有一格讀到空字串，
                // 而 DutyCompleted 剛好落在那個縫裡就會漏過去。所以另外要求路徑本身也真的跑到
                // 底（Indexer >= Actions.Count，跟上面 Navigating 完成判斷用的是同一個條件），
                // 這樣即使落在那一格縫裡，還沒做完的 Interactable 步驟一樣擋得住退本。
                if (!string.IsNullOrEmpty(Action) || (Actions.Count > 0 && Indexer < Actions.Count))
                {
                    _checkFinishingWaitSince ??= DateTime.Now;
                    if (DateTime.Now - _checkFinishingWaitSince.Value < TimeSpan.FromSeconds(CheckFinishingWaitTimeoutSeconds))
                    {
                        SchedulerHelper.ScheduleAction("CheckFinishing-WaitAction", this.CheckFinishing, 500);
                        return;
                    }

                    Svc.Log.Information($"[CheckFinishing] 等待 Action／路徑跑完已超過 {CheckFinishingWaitTimeoutSeconds} 秒" +
                                        $"（Action='{Action}', Indexer={Indexer}/{Actions.Count}），放棄等待直接退本，避免永遠卡在副本裡。");
                }

                _checkFinishingWaitSince = null;
                if (ExitDutyHelper.State != ActionState.Running)
                    ExitDuty();
                if (Configuration.AutoManageRotationPluginState && !Configuration.UsingAlternativeRotationPlugin)
                    SetRotationPluginSettings(false);
                if (Configuration.AutoManageBossModAISettings) 
                    BossMod_IPCSubscriber.DisablePresets();
            }
        }
        else
        {
            // 這一條不經過 LoopsCompleteActions(),所以既有的完成音效在這裡本來就不會響。
            MarkSelfStop("All loops finished.");
            Stage = Stage.Stopped;
        }
    }

    private void GetGeneralSettings()
    {
        // ── 歷史:對齊鏡頭的「舊政策」擷取端 ──
        // 下面這兩行(原文逐字保留)是上游早年的政策:「使用者本來開著 → 進場先關掉 →
        // 結束再開回來」,對應旗標 SettingsActive.Vnav_Align_Camera_Off。
        // 它在本 repo 可考的最早一顆 commit 就已經是註解掉的狀態,而且 erdelf(上游)、
        // aliceric27、okaminico 等所有 fork 至今都維持註解掉——不是我們的合併弄丟的。
        // 現行政策方向相反(StartNavigation 進導航時「打開」),所以這段不可以直接取消註解:
        // 那會變成兩個相反的政策同時存在。現行政策的擷取端在 StartNavigation(),
        // 還原端在 SetGeneralSettings() 裡的 Vnav_Align_Camera_On 分支。
        /*
        if (Configuration.AutoManageVnavAlignCamera && VNavmesh_IPCSubscriber.IsEnabled && VNavmesh_IPCSubscriber.Path_GetAlignCamera())
            _settingsActive |= SettingsActive.Vnav_Align_Camera_Off;
        */
        // 🔴 這裡原本是 `IsEnabled && IsEnabled`（同一個條件寫兩次）——上游 erdelf 是
        //    `IsEnabled && IsPluginEnabled()`。重複的版本讓「YesAlready 只要裝著」就設旗標，
        //    不管使用者有沒有開它 ⇒ AutoDuty 進場把它關掉、結束再打開，
        //    於是**本來刻意關著的 YesAlready 會被 AutoDuty 打開**。
        if (YesAlready_IPCSubscriber.IsEnabled && YesAlready_IPCSubscriber.IsPluginEnabled())
            _settingsActive |= SettingsActive.YesAlready;

        if (PandorasBox_IPCSubscriber.IsEnabled && PandorasBox_IPCSubscriber.GetFeatureEnabled("Auto-interact with Objects in Instances"))
            _settingsActive |= SettingsActive.Pandora_Interact_Objects;

        Svc.Log.Debug($"General Settings Active: {_settingsActive}");
    }

    internal void SetGeneralSettings(bool on)
    {
        if (!on)
            GetGeneralSettings();

        if (Configuration.AutoManageVnavAlignCamera && _settingsActive.HasFlag(SettingsActive.Vnav_Align_Camera_Off))
        {
            Svc.Log.Debug($"Setting VnavAlignCamera: {on}");
            VNavmesh_IPCSubscriber.Path_SetAlignCamera(on);
        }

        // 上面那條是舊政策的還原端:設它的旗標那段在 GetGeneralSettings() 裡是註解掉的,
        // 所以條件永遠不成立、實際是死碼。原樣保留(與上游一致),不要當成清理刪掉。
        //
        // 下面這條才是現行政策的還原端:StartNavigation() 把「本來關著」的對齊鏡頭打開了,
        // 這裡在整趟真的結束時關回去——設定說明承諾的那一半,在此之前一直沒有實作,
        // 於是使用者的對齊鏡頭被單向打開後就再也不會關回去。
        //
        // 🔴 為什麼要額外擋 Looping/Navigating:SetGeneralSettings(true) 有四個呼叫點,
        //    只有 StopAndResetALL() 代表「整趟結束」(它是 Stage.Stopped 的 setter 唯一入口,
        //    所有停止路徑與 Dispose 都會過)。另外三個是單機流程的收尾
        //    (ActiveHelperBase.HelperStopUpdate / GotoHelper.Stop / MapHelper.StopMoveToMapMarker),
        //    它們雖然都有 !Looping 閘門,但 IPC 的 Start() / 指令 "start" 會走進
        //    「Navigating 有、Looping 沒有」的狀態,那時助手收尾就會提早打進來,
        //    把還在導航中的鏡頭設定關掉。補上 Navigating 這一軸才是完整的閘門。
        //    StopAndResetALL() 在呼叫本函式之前已經把 States 清成 None,所以正常收尾照樣會過。
        // 🔴 旗標只在「真的還原到了」才清:被閘門擋下時留著,等真正結束時再還原,不會漏掉。
        if (on
            && Configuration.AutoManageVnavAlignCamera
            && _settingsActive.HasFlag(SettingsActive.Vnav_Align_Camera_On)
            && !States.HasAnyFlag(PluginState.Looping, PluginState.Navigating))
        {
            _settingsActive &= ~SettingsActive.Vnav_Align_Camera_On;
            Svc.Log.Information("還原 vnavmesh 對齊鏡頭:關回進導航前的狀態(關閉)");
            if (VNavmesh_IPCSubscriber.IsEnabled)
                VNavmesh_IPCSubscriber.Path_SetAlignCamera(false);
        }
        // 🔴 還原之後要把旗標清掉。原本 _settingsActive 只有 `|=`、全檔沒有任何清除 ⇒ 旗標終身累積，
        //    於是：第 1 趟時 Pandora 的某功能開著（旗標設起）→ 使用者之後手動關掉它 →
        //    第 2 趟結束時 AutoDuty 仍然照著過期的旗標**把它重新打開**。
        //    清掉之後，下一趟的 GetGeneralSettings() 會重新評估當下的實際狀態。
        //    ⚠️ 只在 on==true（還原那一側）清；on==false 是剛設起旗標的那一側，不能清。
        if (PandorasBox_IPCSubscriber.IsEnabled && _settingsActive.HasFlag(SettingsActive.Pandora_Interact_Objects))
        {
            Svc.Log.Debug($"Setting PandorasBos Auto-interact with Objects in Instances: {on}");
            PandorasBox_IPCSubscriber.SetFeatureEnabled("Auto-interact with Objects in Instances", on);
            if (on) _settingsActive &= ~SettingsActive.Pandora_Interact_Objects;
        }
        if (YesAlready_IPCSubscriber.IsEnabled && _settingsActive.HasFlag(SettingsActive.YesAlready))
        {
            Svc.Log.Debug($"Setting YesAlready Enabled: {on}");
            YesAlready_IPCSubscriber.SetState(on);
            if (on) _settingsActive &= ~SettingsActive.YesAlready;
        }
    }

    internal void SetRotationPluginSettings(bool on, bool ignoreConfig = false, bool ignoreTimer = false)
    {
        // Only try to set the rotation state every few seconds
        if (on && (DateTime.Now - _lastRotationSetTime).TotalSeconds < 5 && !ignoreTimer)
            return;
        
        if(on)
            _lastRotationSetTime = DateTime.Now;

        if (!ignoreConfig && !this.Configuration.AutoManageRotationPluginState)
            return;
        bool bmEnabled     = BossMod_IPCSubscriber.IsEnabled;
        bool foundRotation = false;

        // 強制只用 BossMod AutoRotation:不能只是跳過偵測——Wrath/RSR 若原本已經在
        // Auto 狀態,不主動關掉的話會跟 BossMod 的 AutoRotation 搶著放技能。所以這裡是把
        // 「要不要真的開 Wrath/RSR」跟這個開關掛鉤:該關的時候一樣會呼叫 SetAutoMode(false)
        // /RotationStop(),只是永遠不會被視為「找到可用的循環外掛」(foundRotation 保持
        // false),讓下面 bmEnabled 那段走「!foundRotation」分支,套用會讀 AIHints.Priority
        // 的 "AutoDuty" preset。
        // 🔴 沒裝 BossMod 就不准強制,否則等於把場上唯一的循環外掛關掉、整場不出手。
        bool forceBossMod = bmEnabled && this.Configuration.ForceBossModAutoRotation;

        if (Wrath_IPCSubscriber.IsEnabled)
        {
            bool wrathOn            = on && !forceBossMod;
            bool wrathRotationReady = true;
            if (wrathOn)
                wrathRotationReady = Wrath_IPCSubscriber.IsCurrentJobAutoRotationReady() ||
                                     this.Configuration.Wrath_AutoSetupJobs && Wrath_IPCSubscriber.SetJobAutoReady();

            if (!wrathOn || wrathRotationReady)
            {
                Svc.Log.Debug(wrathOn ? "Wrath rotation enabled" : "Wrath rotation disabled");
                Wrath_IPCSubscriber.SetAutoMode(wrathOn);
                // 🔴 沒開強制時必須維持上游語意:只要 Wrath 在就算「找到循環外掛」,連 on==false
                //    的收尾那一側也一樣 —— 下面 DisablePresets 的條件讀的就是這個值,寫成
                //    `|| wrathOn` 會讓 AutoManageBossModAISettings=false 的人多吃一次
                //    DisablePresets(),那是本開關關著時不該發生的行為改變。
                foundRotation = !forceBossMod;
            }
        }

        if (ReflectionHelper.RotationSolver_Reflection.RotationSolverEnabled)
        {
            if (on && !foundRotation && !forceBossMod)
            {
                Svc.Log.Debug("RSR enabled");
                if (ReflectionHelper.RotationSolver_Reflection.GetStateType != ReflectionHelper.RotationSolver_Reflection.StateTypeEnum.Auto)
                    ReflectionHelper.RotationSolver_Reflection.RotationAuto();
                foundRotation = true;
            }
            else
            {
                if (ReflectionHelper.RotationSolver_Reflection.GetStateType != ReflectionHelper.RotationSolver_Reflection.StateTypeEnum.Off)
                    ReflectionHelper.RotationSolver_Reflection.RotationStop();
            }
        }


        if (bmEnabled)
        {
            if (on)
            {
                BossMod_IPCSubscriber.SetRange(Plugin.Configuration.MaxDistanceToTargetFloat);
                if (!foundRotation)
                {
                    BossMod_IPCSubscriber.SetPreset("AutoDuty", Resources.AutoDutyPreset);
                }
                else if(this.Configuration.AutoManageBossModAISettings)
                {
                    BossMod_IPCSubscriber.SetPreset("AutoDuty Passive", Resources.AutoDutyPassivePreset);
                }
            } 
            else if(!foundRotation || this.Configuration.AutoManageBossModAISettings)
            {
                BossMod_IPCSubscriber.DisablePresets();
            }
        }
    }

    internal void SetBMSettings(bool defaults = false)
    {
        BMRoleChecks();

        if (defaults)
        {
            Configuration.MaxDistanceToTargetRoleBased = true;
            Configuration.PositionalRoleBased = true;
        }

        BossMod_IPCSubscriber.SetMovement(true);
        BossMod_IPCSubscriber.SetRange(Plugin.Configuration.MaxDistanceToTargetFloat);
    }

    internal void BMRoleChecks()
    {
        //RoleBased Positional
        if (PlayerHelper.IsValid && Configuration.PositionalRoleBased && Configuration.PositionalEnum != (Player.Object.ClassJob.Value.GetJobRole() == JobRole.Melee ? Positional.Rear : Positional.Any))
        {
            Configuration.PositionalEnum = (Player.Object.ClassJob.Value.GetJobRole() == JobRole.Melee ? Positional.Rear : Positional.Any);
            Configuration.Save();
        }

        //RoleBased MaxDistanceToTarget
        float maxDistanceToTarget = (Player.Object.ClassJob.Value.GetJobRole() is JobRole.Melee or JobRole.Tank ? 
                                         Plugin.Configuration.MaxDistanceToTargetRoleMelee : Plugin.Configuration.MaxDistanceToTargetRoleRanged);
        if (PlayerHelper.IsValid && Configuration.MaxDistanceToTargetRoleBased && Math.Abs(this.Configuration.MaxDistanceToTargetFloat - maxDistanceToTarget) > 0.01f)
        {
            Configuration.MaxDistanceToTargetFloat = maxDistanceToTarget;
            Configuration.Save();
        }

        //RoleBased MaxDistanceToTargetAoE
        float maxDistanceToTargetAoE = (Player.Object.ClassJob.Value!.GetJobRole() is JobRole.Melee or JobRole.Tank or JobRole.Ranged_Physical ?
                                            Plugin.Configuration.MaxDistanceToTargetRoleMelee : Plugin.Configuration.MaxDistanceToTargetRoleRanged);
        if (PlayerHelper.IsValid && Configuration.MaxDistanceToTargetRoleBased && Math.Abs(this.Configuration.MaxDistanceToTargetAoEFloat - maxDistanceToTargetAoE) > 0.01f)
        {
            Configuration.MaxDistanceToTargetAoEFloat = maxDistanceToTargetAoE;
            Configuration.Save();
        }
    }

    private unsafe void ActionInvoke()
    {
        if (PathAction == null) return;

        if (!TaskManager.IsBusy && !PathAction.Name.IsNullOrEmpty())
        {
            if (PathAction.Name.Equals("Boss"))
            {

                if (Configuration.DutyModeEnum == DutyMode.Regular && Svc.Party.PartyId > 0)
                {
                    Message message = new()
                    {
                        Sender = Player.Name,
                        Action =
                        [
                            new PathAction(){ Name = "Follow", Arguments = [$"null"] },
                            new PathAction(){ Name = "SetBMSettings", Arguments = [$"true"] }
                        ]
                    };

                    var messageJson = System.Text.Json.JsonSerializer.Serialize(message, BuildTab.jsonSerializerOptions);

                    //_messageBusSend.PublishAsync(Encoding.UTF8.GetBytes(messageJson));
                }
            }
            _actions.InvokeAction(PathAction);
            PathAction = new();
        }
    }

    private void GetJobAndLevelingCheck()
    {
        Job curJob = Player.Object.GetJob();
        if (curJob != JobLastKnown)
        {
            if (LevelingEnabled)
            {
                Svc.Log.Info($"{(Configuration.DutyModeEnum == DutyMode.Support || Configuration.DutyModeEnum == DutyMode.Trust) && (Configuration.DutyModeEnum == DutyMode.Support || SupportLevelingEnabled) && (Configuration.DutyModeEnum != DutyMode.Trust || TrustLevelingEnabled)} ({Configuration.DutyModeEnum == DutyMode.Support} || {Configuration.DutyModeEnum == DutyMode.Trust}) && ({Configuration.DutyModeEnum == DutyMode.Support} || {SupportLevelingEnabled}) && ({Configuration.DutyModeEnum != DutyMode.Trust} || {TrustLevelingEnabled})");
                // Re-apply current auto leveling mode through a single code path
                // so duty/path/container state stays consistent.
                LevelingModeEnum = LevelingModeEnum;
                MainListClicked = true;
            }
        }

        JobLastKnown = curJob;
    }

    private void CheckRetainerWindow()
    {
        // AutoBot(AutoMarket)/AutoRetainer沒裝時就不要打這兩支 IPC。SafeWrapper 雖然會吃掉
        // IpcNotReadyError 並回 false，但 EzIpcFailureLog 會每 60 秒印一行 Information。
        if (AutoRetainerHelper.State == ActionState.Running || (AutoRetainer_IPCSubscriber.IsEnabled && AutoRetainer_IPCSubscriber.IsBusy()) || (AM_IPCSubscriber.IsEnabled && AM_IPCSubscriber.IsRunning()) || Stage == Stage.Paused)
            return;

        if (Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            while(!AutoRetainerHelper.Instance.CloseAddons());
    }

    private void InteractablesCheck()
    {
        if (Interactables.Count == 0) return;

        var list = Svc.Objects.Where(x => Interactables.Contains(x.BaseId));

        if (!list.Any()) return;

        var index = this.Actions.Select((Value, Index) => (Value, Index)).First(x => this.Interactables.Contains(x.Value.Arguments.Any(y => y.Any(z => z == ' ')) ? uint.Parse(x.Value.Arguments[0].Split(" ")[0]) : uint.Parse(x.Value.Arguments[0]))).Index;

        if (index > Indexer)
        {
            Indexer = index;
            Stage = Stage.Reading_Path;
        }
    }

    private void PreStageChecks()
    {
        if (Stage == Stage.Stopped)
            return;

        CheckRetainerWindow();

        InteractablesCheck();

        if (EzThrottler.Throttle("OverrideAFK") && States.HasFlag(PluginState.Navigating) && PlayerHelper.IsValid)
            _overrideAFK.ResetTimers();

        if (!Player.Available) return;

        if (!InDungeon && CurrentTerritoryContent != null)
            GetJobAndLevelingCheck();

        if (!PlayerHelper.IsValid || !BossMod_IPCSubscriber.IsEnabled || !VNavmesh_IPCSubscriber.IsEnabled) return;

        if (!ReflectionHelper.RotationSolver_Reflection.RotationSolverEnabled && !BossMod_IPCSubscriber.IsEnabled && !Configuration.UsingAlternativeRotationPlugin) return;

        if (CurrentTerritoryType == 0 && Svc.ClientState.TerritoryType != 0 && InDungeon)
            ClientState_TerritoryChanged(Svc.ClientState.TerritoryType);

        if (this.States.HasFlag(PluginState.Navigating) && this.Configuration.LootTreasure && (!this.Configuration.LootBossTreasureOnly || (this.PathAction?.Name == "Boss" && this.Stage == Stage.Action)) &&
            (this.treasureCofferGameObject = ObjectHelper.GetObjectsByObjectKind(Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure)
                                                        ?.FirstOrDefault(x => ObjectHelper.GetDistanceToPlayer(x) < 2)) != null)
        {
            BossMod_IPCSubscriber.SetRange(30f);
            ObjectHelper.InteractWithObject(this.treasureCofferGameObject, false);
        }

        if (Indexer >= Actions.Count && Actions.Count > 0 && States.HasFlag(PluginState.Navigating))
            DoneNavigating();

        if (Stage > Stage.Condition && !States.HasFlag(PluginState.Other))
            Action = Stage.ToCustomString();
    }

    public void Framework_Update(IFramework framework)
    {
        // 從別的執行緒打進來的 IPC 指令(Run／Start／Stop),照先進先出在這裡排乾。
        // 🔴 放在最前面:同一格排進來的指令要在這一格的 Stage 判斷之前生效,
        //    否則會白白多等一格,而 Stop 多等一格代表多跑一格的自動化。
        //    細節見 IPC/IPCProvider.cs 的 RunOnFramework。
        IPCProvider.DrainPendingWork();

        // 🔴 YesAlready 壓制租約的續約心跳（內部自行節流，沒在壓制時是一個布林判斷就返回）。
        // 一輪多本可以跑好幾個小時，而租約上限只有 5 分鐘 —— 不續約的話 YesAlready
        // 會在副本跑到一半自己醒過來搶按窗。
        // (LeaseMilliseconds = 300_000 = YesAlready SuppressionLeases.MaxLeaseMilliseconds; provider clamps, it does not reject)
        YesAlready_IPCSubscriber.Tick();

        // 🔴 vnavmesh 路徑容許值租約的續約心跳＋閒置放約(內部自行節流;
        //    沒有租約時是一個欄位判斷就返回)。租約上限 5 分鐘,不續約的話
        //    容許值會在副本跑到一半跳回使用者的值。
        VnavmeshToleranceLease.Tick();

        // 任務逾時的觀察哨。必須跑在 TaskManager 自己的 Tick 之後 —— 這是成立的:
        // 建構子先 TaskManager = new()(它在自己的建構子裡掛上 Svc.Framework.Update),
        // 之後才 Svc.Framework.Update += Framework_Update,多播委派照訂閱順序呼叫。
        TaskTimeoutWatcher.Tick();

        PreStageChecks();

        this.Framework_Update_InDuty(framework);

        switch (Stage)
        {
            case Stage.Reading_Path:
                StageReadingPath();
                break;
            case Stage.Moving:
                StageMoving();
                break;
            case Stage.Action:
                StageAction();
                break;
            case Stage.Waiting_For_Combat:
                StageWaitingForCombat();
                break;
            default:
                break;
        }
    }

    public event IFramework.OnUpdateDelegate Framework_Update_InDuty = _ => {};

    /// <summary>
    /// 目前這一步能不能按「跳過」。
    /// </summary>
    /// <remarks>
    /// 只在「真的有一個路徑步驟正在跑」時成立。刻意排除:
    /// <c>Stage.Paused</c>(暫停會把 TaskManager 切成單步模式並記下 PreviousStage,
    /// 跳過要中止佇列並換 Stage,兩者疊起來會讓「繼續」回到一個已經不存在的狀態 —— 請先按繼續)、
    /// <c>Stage.Condition</c>(換區中,有一個排程動作稍後會把 Stage 拉回 Reading_Path,
    /// 這時跳過會被那個排程覆蓋掉)、<c>Stage.Dead</c>/<c>Stage.Revived</c>/<c>Stage.Looping</c>/
    /// <c>Stage.Stopped</c>(當下根本沒有正在執行的路徑步驟)。
    /// </remarks>
    internal bool CanSkipCurrentStep =>
        States.HasFlag(PluginState.Navigating) &&
        Indexer >= 0 && Indexer < Actions.Count &&
        Stage.EqualsAny(Stage.Reading_Path, Stage.Moving, Stage.Action, Stage.Waiting_For_Combat);

    /// <summary>
    /// 目前正在跑的那一步是不是「Wait」而且真的在計時中。是的話回傳原本設定的毫秒數與已經等掉的毫秒數。
    /// </summary>
    /// <remarks>
    /// 判「正在等」看的是 <c>ActionsManager.Wait</c> 自己記的計時,不是 <c>Plugin.Action</c> 的前綴 ——
    /// StopForCombat 開著時,佇列前面那個「等脫戰」任務執行期間 Action 已經是 "Wait: N" 了,
    /// 但 throttle 還沒起算,那段時間不算等待時間。
    /// 另外要求記下的時長與步驟參數相同,免得把動作內部自己排的短等待(例如 BossLoot 的 250ms)
    /// 誤認成這一步的等待。
    /// </remarks>
    internal bool TryGetCurrentWaitProgress(out int configuredMs, out int elapsedMs)
    {
        configuredMs = 0;
        elapsedMs    = 0;

        if (Indexer < 0 || Indexer >= Actions.Count)
            return false;

        PathAction step = Actions[Indexer];

        if (!step.Name.Equals("Wait", StringComparison.OrdinalIgnoreCase) || step.Arguments.Count == 0)
            return false;

        if (!int.TryParse(step.Arguments[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out configuredMs) || configuredMs <= 0)
        {
            configuredMs = 0;
            return false;
        }

        int duration = WaitStepDurationMs;

        if (duration <= 0 || duration != configuredMs)
            return false;

        long remaining = WaitStepEndTick - Environment.TickCount64;

        if (remaining <= 0 || remaining > duration)
            return false;

        elapsedMs = (int)(duration - remaining);
        return true;
    }

    /// <summary>
    /// 跳過正在執行的那一步,直接進入下一步。
    /// 跳過的是計時中的 Wait 步驟時,順便把已經等掉的時間寫回路徑檔。
    /// </summary>
    /// <remarks>
    /// 只動「這一步」的執行狀態:中止排隊中的任務、停掉導航、<see cref="Indexer"/> 加一、
    /// 回到 <c>Stage.Reading_Path</c>。迴圈計數(<c>CurrentLoop</c>)、<see cref="States"/>、
    /// 設定、已載入的路徑一律不動。
    /// </remarks>
    internal void SkipCurrentStep()
    {
        if (!CanSkipCurrentStep)
        {
            Svc.Log.Information($"[跳過步驟] 目前不在可跳過的狀態(Stage={Stage.ToCustomString()}, Indexer={Indexer}, Actions={Actions.Count}),不做任何事。");
            return;
        }

        int        skippedIndex = Indexer;
        PathAction step         = Actions[skippedIndex];
        var        stageBefore  = Stage;

        // 🔴 已等時間必須在做任何清理之前量 —— 清掉計時之後就量不到了。
        bool waitInProgress = TryGetCurrentWaitProgress(out int configuredMs, out int elapsedMs);

        switch (stageBefore)
        {
            case Stage.Action:
                // 對照 StageAction() 正常走完一步的收尾,再補兩件 Abort() 會弄丟的事:
                // (1) ForceAttack 可能正暫停著 BossMod 的「自動攻擊管理」,負責還原的任務排在佇列尾端;
                // (2) 動作自己發起的導航(MoveToObject / Interactable / JumpTo …)還在跑,不停掉的話
                //     StageReadingPath 會因為 Path_IsRunning() 而不肯開始下一步 —— 表現成「按了跳過卻卡住」。
                TaskManager.Abort();
                _actions?.RestoreBossModAutoAutos();
                BossMod_IPCSubscriber.DisableRealAIPreset();
                StopNavigationIfRunning();
                break;
            case Stage.Moving:
                // 對照 StageMoving() 卡住重走的分支:停導航後回 Reading_Path。
                StopNavigationIfRunning();
                break;
            case Stage.Waiting_For_Combat:
                // 對照 StageWaitingForCombat() 脫戰後的收尾。
                BossMod_IPCSubscriber.SetRange(Configuration.MaxDistanceToTargetFloat);
                BossMod_IPCSubscriber.DisableRealAIPreset();
                StopNavigationIfRunning();
                break;
            case Stage.Reading_Path:
                // 這一步還沒開始跑,沒有東西要清。
                break;
        }

        // EzThrottler 的 "Wait" 是全域且永久的 key。被我們 Abort() 中斷之後,尚未到期的殘值會留到
        // 下一個 Wait 步驟 —— 那一步的 Throttle 會先回 false、被反覆重試,等於多等掉殘餘的時間。
        // 這是我們自己造成的,所以自己清掉。
        if (step.Name.Equals("Wait", StringComparison.OrdinalIgnoreCase))
            EzThrottler.Reset("Wait");

        ClearWaitStepTiming();

        PathAction = new();
        Action     = "";

        Indexer = skippedIndex + 1;
        Stage   = Stage.Reading_Path;

        Svc.Log.Information($"[跳過步驟] 已跳過第 {skippedIndex} 步「{step.Name}」(Stage={stageBefore.ToCustomString()}),Indexer -> {Indexer}。");

        if (waitInProgress)
            StepSkipHelper.WriteBackWaitTime(skippedIndex, step, configuredMs, elapsedMs);
        else
            Svc.Chat.Print($"已跳過目前步驟：{step.Name}", "AutoDuty");
    }

    /// <summary>導航真的在跑時才叫 Path_Stop(),vnavmesh 沒載入就什麼都不做。</summary>
    private static void StopNavigationIfRunning()
    {
        if (!VNavmesh_IPCSubscriber.IsEnabled)
            return;

        if (VNavmesh_IPCSubscriber.Path_IsRunning() || VNavmesh_IPCSubscriber.SimpleMove_PathfindInProgress())
            VNavmesh_IPCSubscriber.Path_Stop();
    }

    private void StopAndResetALL()
    {
        ClearWaitStepTiming();
        BossMod_IPCSubscriber.ResetStayCloseToTankCache();
        // 別的外掛透過 IPC 壓上來的設定覆寫,在停止時一律還原(呼叫端不必記得 Pop)。
        ConfigOverrideHelper.Pop();
        if (_bareModeSettingsActive != SettingsActive.None)
        {
            Configuration.EnablePreLoopActions = _bareModeSettingsActive.HasFlag(SettingsActive.PreLoop_Enabled);
            Configuration.EnableBetweenLoopActions = _bareModeSettingsActive.HasFlag(SettingsActive.BetweenLoop_Enabled);
            Configuration.EnableTerminationActions = _bareModeSettingsActive.HasFlag(SettingsActive.TerminationActions_Enabled);
            _bareModeSettingsActive = SettingsActive.None;
        }
        States = PluginState.None;
        TaskManager?.SetStepMode(false);
        TaskManager?.Abort();

        // ForceAttack 有可能正暫停著 BossMod 的「自動攻擊管理」,而負責還原的那個任務
        // 剛剛被 TaskManager.Abort() 一起清掉了,所以這裡補一次(沒有暫停中就是 no-op)。
        // ⚠️ 用 ?. 的理由和上面 TaskManager?. 一樣:建構式整段包在 try 裡,_actions 是到
        //    中後段才指派的,建構式提早擲例外時 Dispose 仍會走到這裡。裸呼叫會在這裡再擲一個
        //    NullReferenceException,把真正的載入失敗原因蓋掉。
        _actions?.RestoreBossModAutoAutos();

        if (_unsyncedKeepMovingArmed)
        {
            _unsyncedKeepMovingArmed = false;
            BossMod_IPCSubscriber.SetMovement(true);
        }
        MainListClicked              = false;
        this.Framework_Update_InDuty = _ => {};
        if (!InDungeon)
            CurrentLoop = 0;
        if (Configuration.AutoManageBossModAISettings) 
            BossMod_IPCSubscriber.DisablePresets();

        SetGeneralSettings(true);
        if (Configuration.AutoManageRotationPluginState && !Configuration.UsingAlternativeRotationPlugin)
            SetRotationPluginSettings(false);
        if (Indexer > 0 && !MainListClicked)
            Indexer = -1;
        if (this.Configuration is { ShowOverlay: true, HideOverlayWhenStopped: true })
            Overlay.IsOpen = false;
        // 放開路徑容許值租約。
        // 🔴 改動前這裡是無條件的 「Path_GetTolerance() > 0.25 就寫回 0.25」——那在租約模式下會變成新的越權:
        //    Path.GetTolerance 回的是「實際生效的值」(租約值 ?? 使用者的值),讀到的可能是
        //    我們自己、甚至是別的外掛押著的租約值,而寫回去的目標卻是使用者那格欄位。
        //    改成:租約模式下只放約(提供端自動還原),只有真的走過舊路徑時才做原本那段還原。
        VNavmesh_IPCSubscriber.Path_ReleaseToleranceRequest();
        FollowHelper.SetFollow(null);

        ActiveRunContext = null;

        if (VNavmesh_IPCSubscriber.IsEnabled && VNavmesh_IPCSubscriber.Path_IsRunning())
            VNavmesh_IPCSubscriber.Path_Stop();

        if (MapHelper.State == ActionState.Running)
            MapHelper.StopMoveToMapMarker();

        if (DeathHelper.DeathState == PlayerLifeState.Revived)
            DeathHelper.Stop();

        foreach (IActiveHelper helper in ActiveHelper.activeHelpers) 
            helper.StopIfRunning();


        Wrath_IPCSubscriber.Release();
        Action = "";
    }

    /// <summary>
    /// 標記「接下來這一次 Stage = Stage.Stopped 是 AutoDuty 自己停的」並附上原因。
    /// 只能在非使用者觸發的停止點呼叫。使用者按 Stop、下 /autoduty stop、
    /// 或別的外掛叫 IPC 端點 AutoDuty.Stop 都不可以呼叫 —— 那些不算「自己停了」。
    /// </summary>
    /// <param name="reason">給使用者看的英文原文,顯示時才做 .Loc() 查表。</param>
    /// <param name="endSoundAlreadyQueued">既有的終止流程是不是已經排了完成音效。</param>
    private void MarkSelfStop(string reason, bool endSoundAlreadyQueued = false)
    {
        _pendingStopReason       = reason;
        _pendingStopSoundHandled = endSoundAlreadyQueued;
    }

    /// <summary>
    /// 在「Stage 真的由非 Stopped 掉到 Stopped」而且那次是 AutoDuty 自己停的時候,發一則通知。
    /// </summary>
    private void NotifyIfAutomationStoppedItself()
    {
        string? reason       = _pendingStopReason;
        bool    soundHandled = _pendingStopSoundHandled;
        _pendingStopReason       = null;
        _pendingStopSoundHandled = false;

        // 使用者主動停:MainWindow 的十個按鈕、/autoduty stop、IPC Stop 都不會填 _pendingStopReason。
        if (reason == null)
            return;

        // Stage 的 setter 沒有 early-return 守衛:已經是 Stopped 時再賦值一次仍會整段跑一遍。
        // CheckFinishing() 的 else 分支就在每幀路徑上,少了這個邊緣判斷會一直念。
        if (_stage == Stage.Stopped)
            return;

        // 🔴 刻意放在 NotifyWhenStoppedItself 的閘門「之前」：那個旗標是「Dalamud 桌面通知」
        //    的開關而且預設關；語音通知是另一件事、有自己的開關，兩者不該互相牽連。
        // 🔴 IPC 的實作跑在呼叫端的執行緒上 ⇒ 一律丟回主執行緒再叫（已在主執行緒時是同步執行）。
        // 🔴 fail-safe：TataruPraise 沒裝／關著／池裡沒句子都只是安靜的 no-op，絕不影響跑本流程。
        if (Configuration.TataruPraiseOnStoppedItself)
        {
            // 區域副本：流程分析的「非 null」推不進 lambda，直接捕獲 reason 會是 string?。
            string praiseReason = reason;
            _ = Svc.Framework.RunOnFrameworkThread(() => TataruPraiseIPC.TryPraise(praiseReason));
        }

        if (!Configuration.NotifyWhenStoppedItself)
            return;

        // 刻意用 Information:使用者跑 LogLevel 1,這一級一定收得到,事後對照用。
        Svc.Log.Information($"[StopNotify] AutoDuty 自己停了:{reason}(最後動作:{Action})");

        string localizedReason = reason.Loc();
        string headline        = "AutoDuty stopped on its own.".Loc();
        _ = Svc.Framework.RunOnFrameworkThread(() =>
        {
            Svc.NotificationManager.AddNotification(new Notification
            {
                Title           = "AutoDuty",
                Content         = headline + "\n" + localizedReason,
                MinimizedText   = headline,
                Type            = NotificationType.Warning,
                InitialDuration = TimeSpan.FromSeconds(30),
                Minimized       = false,
            });

            // 沿用既有的完成音效,不另外做播放層;正常跑完的路徑已經響過就不重複。
            if (Configuration.PlayEndSound && !soundHandled)
                SoundHelper.StartSound(true, Configuration.CustomSound, Configuration.SoundEnum);
        });
    }

    public void Dispose()
    {
        GitHubHelper.Dispose();
        StopAndResetALL();
        Svc.Framework.Update -= Framework_Update;
        Svc.Framework.Update -= SchedulerHelper.ScheduleInvoker;
        FileHelper.FileSystemWatcher.Dispose();
        FileHelper.FileWatcher.Dispose();
        WindowSystem.RemoveAllWindows();
        // 🔴 要在 ECommonsMain.Dispose() 之前拆:AddonPressGuard 的解除封鎖監聽器掛在
        //    Svc.AddonLifecycle 上,ECommons 收掉服務之後就沒有東西可以 Unregister 了,
        //    留著的委派會指向已卸載的組件。
        AddonPressGuard.ForceTeardown();
        // 🔴 拆掉所有 *_IPCSubscriber，要在 ECommonsMain.Dispose() 之前：拆除動作用得到
        //    Svc 的服務。放在 EzIpcFailureLog.Disable() 之前，拆的過程若有 IPC 失敗才記得到 log。
        IPCSubscriber_Common.DisposeAllSubscribers();
        EzIpcFailureLog.Disable();
        ECommonsMain.Dispose();
        MainWindow.Dispose();
        OverrideCamera.Dispose();
        Svc.ClientState.TerritoryChanged -= ClientState_TerritoryChanged;
        Svc.Condition.ConditionChange    -= Condition_ConditionChange;
        PictoService.Dispose();
        PluginInterface.UiBuilder.Draw   -= UiBuilderOnDraw;
        Svc.Commands.RemoveHandler(CommandName);
    }

    private unsafe void OnCommand(string command, string args)
    {
        // in response to the slash command
        Match        match   = RegexHelper.ArgumentParserRegex().Match(args.ToLower());
        List<string> matches = [];

        while (match.Success)
        {
            matches.Add(match.Groups[match.Groups[1].Length > 0 ? 1 : 0].Value);
            match = match.NextMatch();
        }

        string[] argsArray = matches.Count > 0 ? matches.ToArray() : [string.Empty];

        switch (argsArray[0])
        {
            case "config" or "cfg":
                if (argsArray.Length < 2)
                    OpenConfigUI();
                else if (argsArray[1].Equals("list"))
                    ConfigHelper.ListConfig();
                else
                    ConfigHelper.ModifyConfig(argsArray[1], argsArray[2..]);
                break;
            case "start":
                StartNavigation();
                break;
            case "stop":
                Plugin.Stage = Stage.Stopped;
                break;
            case "pause":
                Plugin.Stage = Stage.Paused;
                break;
            case "resume":
                if (Plugin.Stage == Stage.Paused)
                {
                    Plugin.TaskManager.SetStepMode(false);
                    Plugin.Stage  =  Plugin.PreviousStage;
                    Plugin.States &= ~PluginState.Paused;
                }
                break;
            case "goto":
                switch (argsArray[1])
                {
                    case "inn":
                        GotoInnHelper.Invoke(argsArray.Length > 2 ? Convert.ToUInt32(argsArray[2]) : PlayerHelper.GetGrandCompany());
                        break;
                    case "barracks":
                        GotoBarracksHelper.Invoke();
                        break;
                    case "gcsupply":
                        GotoHelper.Invoke(PlayerHelper.GetGrandCompanyTerritoryType(PlayerHelper.GetGrandCompany()), [GCTurninHelper.GCSupplyLocation], 0.25f, 2f, false);
                        break;
                    case "summoningbell":
                        SummoningBellHelper.Invoke(Configuration.PreferredSummoningBellEnum);
                        break;
                    case "apartment":
                        GotoHousingHelper.Invoke(Housing.Apartment);
                        break;
                    case "personalhome":
                        GotoHousingHelper.Invoke(Housing.Personal_Home);
                        break;
                    case "fcestate":
                        GotoHousingHelper.Invoke(Housing.FC_Estate);
                        break;
                    default:
                        break;
                }
                //GotoAction(args.Replace("goto ", ""));
                break;
            case "turnin":
                if (PlayerHelper.GetGrandCompanyRank() > 5)
                    GCTurninHelper.Invoke();
                else
                    Svc.Log.Info("GC Turnin requires GC Rank 6 or Higher");
                break;
            case "desynth":
                DesynthHelper.Invoke();
                break;
            case "repair":
                if (InventoryHelper.CanRepair(100))
                    RepairHelper.Invoke();
                break;
            case "autoretainer":
            case "ar":
                AutoRetainerHelper.Invoke();
                break;
            case "equiprec":
                AutoEquipHelper.Invoke();
                break;
            case "extract":
                if (QuestManager.IsQuestComplete(66174))
                    ExtractHelper.Invoke();
                else
                    Svc.Log.Info("Materia Extraction requires having completed quest: Forging the Spirit");
                break;
            case "dataid":
                IGameObject? obj = null;
                if (argsArray.Length == 2)
                    obj = Svc.Objects[int.TryParse(argsArray[1], out int index) ? index : -1] ?? null;
                else
                    obj = ObjectHelper.GetObjectByName(Svc.Targets.Target?.Name.TextValue ?? "");

                Svc.Log.Info($"{obj?.BaseId}");
                ImGui.SetClipboardText($"{obj?.BaseId}");
                break;
            case "moveto":
                var argss = args.Replace("moveto ", "").Split("|");
                var vs = argss[1].Split(", ");
                var v3 = new Vector3(float.Parse(vs[0]), float.Parse(vs[1]), float.Parse(vs[2]));

                GotoHelper.Invoke(Convert.ToUInt32(argss[0]), [v3], argss.Length > 2 ? float.Parse(argss[2]) : 0.25f, argss.Length > 3 ? float.Parse(argss[3]) : 0.25f);
                break;
            case "exitduty":
                _actions.ExitDuty(new());
                break;
            case "queue":
                QueueHelper.Invoke(ContentHelper.DictionaryContent.FirstOrDefault(x => x.Value.Name!.Equals(args.ToLower().Replace("queue ", ""), StringComparison.InvariantCultureIgnoreCase)).Value ?? null, Configuration.DutyModeEnum);
                break;
            case "overlay":
                if (argsArray.Length == 1)
                {
                    this.Configuration.ShowOverlay = true;
                    this.Overlay.IsOpen            = true;

                    if (!Plugin.States.HasAnyFlag(PluginState.Looping, PluginState.Navigating))
                        this.Configuration.HideOverlayWhenStopped = false;
                }
                else
                {
                    switch (argsArray[1].ToLower())
                    {
                        case "lock":
                            if (Overlay.Flags.HasFlag(ImGuiWindowFlags.NoMove))
                                Overlay.Flags -= ImGuiWindowFlags.NoMove;
                            else
                                Overlay.Flags |= ImGuiWindowFlags.NoMove;
                            break;
                        case "nobg":
                            if (Overlay.Flags.HasFlag(ImGuiWindowFlags.NoBackground))
                                Overlay.Flags -= ImGuiWindowFlags.NoBackground;
                            else
                                Overlay.Flags |= ImGuiWindowFlags.NoBackground;
                            break;
                    }
                }
                break;
            case "skipstep":
                if (States.HasFlag(PluginState.Navigating))
                {
                    Indexer++;
                    Stage = Stage.Reading_Path;
                }
                break;
            case "movetoflag":
                MapHelper.MoveToMapMarker();
                break;
            case "run":
                var failPreMessage = "Run Error: Incorrect usage: ";
                var failPostMessage = "\nCorrect usage: /autoduty run DutyMode TerritoryTypeInteger LoopTimesInteger (optional)BareModeBool\nexample: /autoduty run Support 1036 10 true\nYou can get the TerritoryTypeInteger from /autoduty tt name of territory (will be logged and copied to clipboard)";
                if (argsArray.Length < 4)
                {
                    Svc.Log.Info($"{failPreMessage}Argument count must be at least 3, you inputed {argsArray.Length - 1}{failPostMessage}");
                    return;
                }
                if (!Enum.TryParse(argsArray[1], true, out DutyMode dutyMode))
                {
                    Svc.Log.Info($"{failPreMessage}Argument 1 must be a DutyMode enum Type, you inputed {argsArray[1]}{failPostMessage}");
                    return;
                }
                if (!uint.TryParse(argsArray[2], out uint territoryType))
                {
                    Svc.Log.Info($"{failPreMessage}Argument 2 must be an unsigned integer, you inputed {argsArray[2]}{failPostMessage}");
                    return;
                }
                if (!int.TryParse(argsArray[3], out int loopTimes))
                {
                    Svc.Log.Info($"{failPreMessage}Argument 3 must be an integer, you inputed {argsArray[3]}{failPostMessage}");
                    return;
                }
                if (!ContentHelper.DictionaryContent.TryGetValue(territoryType, out var content))
                {
                    Svc.Log.Info($"{failPreMessage}Argument 2 value was not in our ContentList or has no Path, you inputed {argsArray[2]}{failPostMessage}");
                    return;
                }
                if (!content.DutyModes.HasFlag(dutyMode))
                {
                    Svc.Log.Info($"{failPreMessage}Argument 2 value was not of type {dutyMode}, which you inputed in Argument 1, Argument 2 value was {argsArray[2]}{failPostMessage}");
                    return;
                }
                if (!content.CanRun(trust: dutyMode == DutyMode.Trust))
                {
                    var failReason = !UIState.IsInstanceContentCompleted(content.Id) ? "You dont have it unlocked" : (!ContentPathsManager.DictionaryPaths.ContainsKey(content.TerritoryType) ? "There is no path file" : (PlayerHelper.GetCurrentLevelFromSheet() < content.ClassJobLevelRequired ? $"Your Lvl({PlayerHelper.GetCurrentLevelFromSheet()}) is less than {content.ClassJobLevelRequired}" : (InventoryHelper.CurrentItemLevel < content.ItemLevelRequired ? $"Your iLvl({InventoryHelper.CurrentItemLevel}) is less than {content.ItemLevelRequired}" : "Your trust party is not of correct levels")));
                    Svc.Log.Info($"Unable to run {content.Name}, {failReason} {content.CanTrustRun()}");
                    return;
                }

                Configuration.DutyModeEnum = dutyMode;

                var bareMode = argsArray.Length > 4 && bool.TryParse(argsArray[4], out bool parsedBool) && parsedBool;
                var ctx = BuildCommandRunContext(territoryType, loopTimes, startFromZero: true, bareMode: bareMode, source: RunSource.Command, persistLoopsToConfig: true);
                if (ctx != null)
                    Run(ctx);
                else
                    Run(territoryType, loopTimes, bareMode: bareMode);
                break;
            case "tt":
                var tt = Svc.Data.Excel.GetSheet<TerritoryType>()?.FirstOrDefault(x => x.ContentFinderCondition.ValueNullable != null && x.ContentFinderCondition.Value.Name.ToString().Equals(args.Replace("tt ", ""), StringComparison.InvariantCultureIgnoreCase)) ?? Svc.Data.Excel.GetSheet<TerritoryType>()?.GetRow(1);
                Svc.Log.Info($"{tt?.RowId}");
                ImGui.SetClipboardText($"{tt?.RowId}");
                break;
            case "range":
                if (float.TryParse(argsArray[1], out float newRange))
                    BossMod_IPCSubscriber.SetRange(Math.Clamp(newRange, 1, 30));
                break;
            case "spew":
                IGameObject? spewObj = null;
                if (argsArray.Length == 2)
                    spewObj = ObjectHelper.GetObjectByDataId(uint.TryParse(argsArray[1], out uint dataId) ? dataId : 0);
                else
                    spewObj = ObjectHelper.GetObjectByName(Svc.Targets.Target?.Name.TextValue ?? "");

                if (spewObj == null) return;

                GameObject gObj = *spewObj.Struct();
                try { Svc.Log.Info($"Spewing Object Information for: {gObj.NameString}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"Spewing Object Information for: {gObj.GetName()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                //DrawObject: {gObj.DrawObject}\n
                //LayoutInstance: { gObj.LayoutInstance}\n
                //EventHandler: { gObj.EventHandler}\n
                //LuaActor: {gObj.LuaActor}\n
                try { Svc.Log.Info($"DefaultPosition: {gObj.DefaultPosition}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"DefaultRotation: {gObj.DefaultRotation}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"EventState: {gObj.EventState}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"EntityId {gObj.EntityId}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"LayoutId: {gObj.LayoutId}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"BaseId {gObj.BaseId}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"OwnerId: {gObj.OwnerId}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"ObjectIndex: {gObj.ObjectIndex}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"ObjectKind {gObj.ObjectKind}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"SubKind: {gObj.SubKind}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"Sex: {gObj.Sex}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"YalmDistanceFromPlayerX: {gObj.YalmDistanceFromPlayerX}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"TargetStatus: {gObj.TargetStatus}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"YalmDistanceFromPlayerZ: {gObj.YalmDistanceFromPlayerZ}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"TargetableStatus: {gObj.TargetableStatus}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"Position: {gObj.Position}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"Rotation: {gObj.Rotation}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"Scale: {gObj.Scale}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"Height: {gObj.Height}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"VfxScale: {gObj.VfxScale}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"HitboxRadius: {gObj.HitboxRadius}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"DrawOffset: {gObj.DrawOffset}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"EventId: {gObj.EventId.Id}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"FateId: {gObj.FateId}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"NamePlateIconId: {gObj.NamePlateIconId}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"RenderFlags: {gObj.RenderFlags}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"GetGameObjectId().ObjectId: {gObj.GetGameObjectId().ObjectId}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"GetGameObjectId().Type: {gObj.GetGameObjectId().Type}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"GetObjectKind: {gObj.GetObjectKind()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"GetIsTargetable: {gObj.GetIsTargetable()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"GetName: {gObj.GetName()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"GetRadius: {gObj.GetRadius()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"GetHeight: {gObj.GetHeight()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"GetDrawObject: {*gObj.GetDrawObject()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"GetNameId: {gObj.GetNameId()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"IsDead: {gObj.IsDead()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"IsNotMounted: {gObj.IsNotMounted()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"IsCharacter: {gObj.IsCharacter()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                try { Svc.Log.Info($"IsReadyToDraw: {gObj.IsReadyToDraw()}"); } catch (Exception ex) { Svc.Log.Info($": {ex}"); };
                break;
            default:
                OpenMainUI();
                break;
        }
    }

    private void DrawUI() => WindowSystem.Draw();

    public void OpenConfigUI()
    {
        if (MainWindow != null)
        {
            MainWindow.IsOpen = true;
            MainWindow.OpenTab("Config");
        }
    }

    public void OpenMainUI()
    {
        if (MainWindow != null)
            MainWindow.IsOpen = true;
    }
}
