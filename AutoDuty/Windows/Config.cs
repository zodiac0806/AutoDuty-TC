using AutoDuty.Helpers;
using AutoDuty.IPC;
using global::AutoDuty.Multibox;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using ECommons.MathHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;
using Dalamud.Bindings.ImGui;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using static AutoDuty.Helpers.RepairNPCHelper;
using static AutoDuty.Windows.ConfigTab;

namespace AutoDuty.Windows;

using Data;
using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using ECommons.UIHelpers.AtkReaderImplementations;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using Properties;
using System.IO;
using System.Numerics;
using System.Text;
using ReflectionHelper = Helpers.ReflectionHelper;
using Vector2 = FFXIVClientStructs.FFXIV.Common.Math.Vector2;

[JsonObject(MemberSerialization.OptIn)]
public class ConfigurationMain : IEzConfig
{
    public const string CONFIGNAME_BARE = "Bare";

    public static ConfigurationMain Instance;

    [JsonProperty]
    public string DefaultConfigName = CONFIGNAME_BARE;

    [JsonProperty]
    private string activeProfileName = CONFIGNAME_BARE;
    
    public  string ActiveProfileName => this.activeProfileName;

    [JsonProperty]
    private readonly HashSet<ProfileData> profileData = [];

    private readonly Dictionary<string, ProfileData> profileByName = [];
    private readonly Dictionary<ulong, string> profileByCID = [];

    [JsonProperty]
    public readonly Dictionary<ulong, CharData> charByCID = [];

    [JsonObject(MemberSerialization.OptOut)]
    public struct CharData
    {
        public required ulong  CID;
        public          string Name;
        public          string World;

        public string GetName() => this.Name.Any() ? $"{this.Name}@{this.World}" : CID.ToString();

        public override int GetHashCode() => this.CID.GetHashCode();
    }

    /// <summary>
    /// 已儲存的具名排程播放清單。跨設定檔共用,存在 ConfigurationMain 這一層。
    /// </summary>
    /// <remarks>
    /// 🔴 <see cref="ConfigurationMain"/> 是 <c>MemberSerialization.OptIn</c>,
    /// 少了 <c>[JsonProperty]</c> 這個欄位會靜默不進存檔。
    /// </remarks>
    [JsonProperty]
    public List<Playlist> Playlists { get; set; } = [];

    [JsonProperty]
    //Dev Options
    internal bool updatePathsOnStartup = true;
    public bool UpdatePathsOnStartup
    {
        get => !Plugin.isDev || this.updatePathsOnStartup;
        set => this.updatePathsOnStartup = value;
    }

    // 多開協調(Multibox)設定。
    // 🔴 裡面的 MultiBox 開關本身標了 [JsonIgnore],**不會**存進設定檔 ⇒ 每次載入外掛
    // 都是關的,沒有「上次開著這次自動接上」的路徑。這裡存下來的只有連線參數
    // (管道名稱/位址/埠號)與是否為主機端。
    [JsonProperty]
    public MultiboxUtility.MultiboxConfiguration multibox = new();

    public IEnumerable<string> ConfigNames => this.profileByName.Keys;
     
    public ProfileData GetCurrentProfile
    {
        get
        {
            if (!this.profileByName.TryGetValue(this.ActiveProfileName, out ProfileData? profiles))
            {
                this.SetProfileToDefault();
                return this.GetCurrentProfile;
            }

            return profiles;
        }
    }

    public Configuration GetCurrentConfig => this.GetCurrentProfile.Config;

    public void Init()
    {
        if (this.profileData.Count == 0)
        {
            if (Svc.PluginInterface.ConfigFile.Exists)
            {
                Configuration? configuration = EzConfig.DefaultSerializationFactory.Deserialize<Configuration>(File.ReadAllText(Svc.PluginInterface.ConfigFile.FullName, Encoding.UTF8));
                if (configuration != null)
                {
                    this.CreateProfile("Migrated", configuration);
                    this.SetProfileAsDefault();
                }
            }
        }

        void RegisterProfileData(ProfileData profile)
        {
            if (profile.CIDs.Any())
                foreach (ulong cid in profile.CIDs)
                    this.profileByCID[cid] = profile.Name;
            this.profileByName[profile.Name] = profile;

            if(profile.Config.LootMethodEnum == LootMethod.RotationSolver) //RSR removed
                profile.Config.LootMethodEnum = LootMethod.All;
        }

        foreach (ProfileData profile in this.profileData)
            if(profile.Name != CONFIGNAME_BARE)
                RegisterProfileData(profile);

        RegisterProfileData(new ProfileData
                            {
                                Name = CONFIGNAME_BARE,
                                Config = new Configuration
                                         {
                                             EnablePreLoopActions     = false,
                                             EnableBetweenLoopActions = false,
                                             EnableTerminationActions = false,
                                             LootTreasure             = false
                                         }
                            });

        this.SetProfileToDefault();
    }

    public bool SetProfile(string name)
    {
        DebugLog("Changing profile to: " + name);
        if (this.profileByName.ContainsKey(name))
        {
            this.activeProfileName = name;
            EzConfig.Save();
            return true;
        }
        return false;
    }

    public void SetProfileAsDefault()
    {
        if (this.profileByName.ContainsKey(this.ActiveProfileName))
        {
            this.DefaultConfigName = this.ActiveProfileName;
            EzConfig.Save();
        }
    }

    public void SetProfileToDefault()
    {
        this.SetProfile(CONFIGNAME_BARE);
        Svc.Framework.RunOnTick(() =>
        {
            DebugLog($"Setting to default profile for {Player.Name} ({Player.CID}) {PlayerHelper.IsValid}");

            if (Player.Available && this.profileByCID.TryGetValue(Player.CID, out string? charProfile))
                if (this.SetProfile(charProfile))
                    return;
            DebugLog("No char default found. Using general default");
            if (!this.SetProfile(this.DefaultConfigName))
            {
                DebugLog("Fallback, using bare");
                this.DefaultConfigName = CONFIGNAME_BARE;
                this.SetProfile(CONFIGNAME_BARE);
            }
        });
    }

    public void CreateNewProfile() => 
        this.CreateProfile("Profile" + (this.profileByName.Count - 1).ToString(CultureInfo.InvariantCulture));

    public void CreateProfile(string name) => 
        this.CreateProfile(name, new Configuration());

    public void CreateProfile(string name, Configuration config)
    {
        DebugLog($"Creating new Profile: {name}");

        ProfileData profile = new()
                           {
                               Name   = name,
                               Config = config
                           };

        this.profileData.Add(profile);
        this.profileByName.Add(name, profile);
        this.SetProfile(name);
    }

    public void DuplicateCurrentProfile()
    {
        string name;
        int    counter = 0;

        string templateName = this.ActiveProfileName.EndsWith("_Copy") ? this.ActiveProfileName : $"{this.ActiveProfileName}_Copy";

        do
            name = counter++ > 0 ? $"{templateName}{counter}" : templateName;
        while (this.profileByName.ContainsKey(name));

        string?        oldConfig = EzConfig.DefaultSerializationFactory.Serialize(this.GetCurrentConfig);
        if(oldConfig != null)
        {
            Configuration? newConfig = EzConfig.DefaultSerializationFactory.Deserialize<Configuration>(oldConfig);
            if(newConfig != null)
                this.CreateProfile(name, newConfig);
        }
    }

    public void RemoveCurrentProfile()
    {
        DebugLog("Removing " + this.ActiveProfileName);
        this.profileData.Remove(this.GetCurrentProfile);
        this.profileByName.Remove(this.ActiveProfileName);
        this.SetProfileToDefault();
    }

    public bool RenameCurrentProfile(string newName)
    {
        if (this.profileByName.ContainsKey(newName))
            return false;

        ProfileData config = this.GetCurrentProfile;
        this.profileByName.Remove(this.ActiveProfileName);
        this.profileByName[newName] = config;
        config.Name                 = newName;
        this.activeProfileName      = newName;

        EzConfig.Save();

        return true;
    }

    public ProfileData? GetProfile(string name) => 
        this.profileByName.GetValueOrDefault(name);

    public void SetCharacterDefault()
    {
        Svc.Framework.RunOnTick(() =>
                          {

                              if (!PlayerHelper.IsValid)
                                  return;

                              ulong cid = Player.CID;

                              if (this.profileByCID.TryGetValue(cid, out string? oldProfile))
                                  this.profileByName[oldProfile].CIDs.Remove(cid);

                              this.GetCurrentProfile.CIDs.Add(cid);
                              this.profileByCID.Add(cid, this.ActiveProfileName);
                              this.charByCID[cid] = new CharData
                                                    {
                                                        CID  = cid,
                                                        Name = Player.Name,
                                                        World = Player.CurrentWorld
                              };

                              EzConfig.Save();
                          });
    }

    public void RemoveCharacterDefault()
    {
        Svc.Framework.RunOnTick(() =>
                                {
                                    if (!PlayerHelper.IsValid)
                                        return;

                                    ulong cid = Player.CID;

                                    this.profileByName[this.ActiveProfileName].CIDs.Remove(cid);
                                    this.profileByCID.Remove(cid);

                                    EzConfig.Save();
                                });
    }

    public static void DebugLog(string message)
    {
        Svc.Log.Debug($"Configuration Main: {message}");
    }
}

[JsonObject(MemberSerialization.OptOut)]
public class ProfileData
{
    public required string         Name;
    public          HashSet<ulong> CIDs = [];
    public required Configuration  Config;
}

[Serializable]
public class PlannerItem
{
    /// <summary>
    /// TerritoryType key used by ContentPathsManager/ContentHelper.
    /// </summary>
    public uint TerritoryType;

    /// <summary>
    /// How many successful completions to run this duty for.
    /// </summary>
    public int TargetRuns = 1;

    /// <summary>
    /// How many successful completions have been recorded for this duty in the current plan cycle.
    /// </summary>
    public int CompletedRuns;

    /// <summary>
    /// Selected route file name (DutyPath.FileName). Null means auto-select.
    /// </summary>
    public string? PathFileName;
}

/// <summary>
/// 一份具名的排程快照。使用者可以把目前的排程清單存成播放清單,之後載入重跑。
/// </summary>
/// <remarks>
/// 🔴 這只是<b>排程資料</b>。載入播放清單不會啟動任何跑本流程,
/// 使用者仍然要自己按「執行排程」,既有守衛一條都沒有繞過。
/// </remarks>
[JsonObject(MemberSerialization.OptOut)]
public class Playlist
{
    /// <summary>播放清單名稱,在清單集合裡唯一(比對時忽略大小寫)。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>清單內容。存的是排程項目的快照,不含執行進度。</summary>
    public List<PlannerItem> Entries { get; set; } = [];
}

public class AutoDutySerializationFactory : DefaultSerializationFactory, ISerializationFactory
{
    public override string DefaultConfigFileName { get; } = "AutoDutyConfig.json";

    /// <summary>
    /// 🔴 <b>存檔真正走的序列化出口就是這一支</b>,不是 <see cref="SerializeAsBin"/>。
    /// </summary>
    /// <remarks>
    /// <c>EzConfig.SaveConfiguration</c> 依 <c>IsBinary</c> 二選一,而
    /// <c>DefaultSerializationFactory.IsBinary</c> 是 <b>false</b> 且本類別沒有覆寫它
    /// ⇒ 走的是 <c>serializationFactory.Serialize(Configuration)</c> 這條,<c>SerializeAsBin</c>
    /// 在存檔路徑上<b>根本不會被呼叫</b>。
    /// 而本類別的基底清單重新列出了 <c>ISerializationFactory</c>,介面對應因此從本類別重算,
    /// 所以這支 <c>new</c> 方法會接走介面派送(不重列介面的話會落回基底,實測對照組已驗)。
    /// ⇒ 「IPC 覆寫值不可以被寫進設定檔」掛在這裡才有效;掛在 <c>SerializeAsBin</c> 上是死碼。
    /// ⚠️ 這裡只做序列化(純 CPU),檔案是 <c>SaveConfiguration</c> 拿到字串之後才寫的,
    /// 所以 <c>WithUserValues</c> 持鎖的期間不含檔案 I/O。
    /// </remarks>
    public new string Serialize(object config) =>
        ConfigOverrideHelper.WithUserValues(() => base.Serialize(config, true));

    /// <summary>存檔路徑不會走到這裡(見 <see cref="Serialize(object)"/>),它轉呼叫上面那支,所以一樣被包住。</summary>
    public override byte[] SerializeAsBin(object config) =>
        Encoding.UTF8.GetBytes(this.Serialize(config));
}



[Serializable]
public class Configuration
{
    //Meta
    public HashSet<string>                                    DoNotUpdatePathFiles = [];
    public Dictionary<uint, Dictionary<string, JobWithRole>?> PathSelectionsByPath = [];

    //LogOptions
    public bool AutoScroll = true;
    public LogEventLevel LogEventLevel = LogEventLevel.Debug;

    //General Options
    public int LoopTimes = 1;

    //Planner Options (fixed duty sequence)
    public bool PlannerEnabled = false;
    public bool PlannerRepeat  = false;
    public bool PlannerPaused  = false;
    public List<PlannerItem> PlannerItems = [];
    public int PlannerCurrentIndex = 0;

    /// <summary>
    /// 目前載入中的播放清單名稱,空字串代表沒有載入任何清單(既有使用者的預設值)。
    /// 只是一個標示,不影響排程怎麼跑。
    /// </summary>
    public string PlannerPlaylistName = string.Empty;

    internal DutyMode dutyModeEnum = DutyMode.None;
    public DutyMode DutyModeEnum
    {
        get => dutyModeEnum;
        set
        {
            dutyModeEnum = value;
            Plugin.CurrentTerritoryContent = null;
            MainTab.DutySelected = null;
            Plugin.LevelingModeEnum = LevelingMode.None;
        }
    }
    
    public bool Unsynced                       = false;
    public bool HideUnavailableDuties          = false;
    public bool PreferTrustOverSupportLeveling = false;

    public bool ShowMainWindowOnStartup = false;

    //Overlay Config Options
    internal bool showOverlay = true;
    public bool ShowOverlay
    {
        get => showOverlay;
        set
        {
            showOverlay = value;
            if (Plugin.Overlay != null)
                Plugin.Overlay.IsOpen = value;
        }
    }
    internal bool hideOverlayWhenStopped = false;
    public bool HideOverlayWhenStopped
    {
        get => hideOverlayWhenStopped;
        set 
        {
            hideOverlayWhenStopped = value;
            if (Plugin.Overlay != null)
            {
                SchedulerHelper.ScheduleAction("LockOverlaySetter", () => Plugin.Overlay.IsOpen = !value || Plugin.States.HasFlag(PluginState.Looping) || Plugin.States.HasFlag(PluginState.Navigating), () => Plugin.Overlay != null);
            }
        }
    }
    internal bool lockOverlay = false;
    public bool LockOverlay
    {
        get => lockOverlay;
        set 
        {
            lockOverlay = value;
            if (value)
                SchedulerHelper.ScheduleAction("LockOverlaySetter", () => { if (!Plugin.Overlay.Flags.HasFlag(ImGuiWindowFlags.NoMove)) Plugin.Overlay.Flags |= ImGuiWindowFlags.NoMove; }, () => Plugin.Overlay != null);
            else
                SchedulerHelper.ScheduleAction("LockOverlaySetter", () => { if (Plugin.Overlay.Flags.HasFlag(ImGuiWindowFlags.NoMove)) Plugin.Overlay.Flags -= ImGuiWindowFlags.NoMove; }, () => Plugin.Overlay != null);
        }
    }
    internal bool overlayNoBG = false;
    public bool OverlayNoBG
    {
        get => overlayNoBG;
        set
        {
            overlayNoBG = value;
            if (value)
                SchedulerHelper.ScheduleAction("OverlayNoBGSetter", () => { if (!Plugin.Overlay.Flags.HasFlag(ImGuiWindowFlags.NoBackground)) Plugin.Overlay.Flags |= ImGuiWindowFlags.NoBackground; }, () => Plugin.Overlay != null);
            else
                SchedulerHelper.ScheduleAction("OverlayNoBGSetter", () => { if (Plugin.Overlay.Flags.HasFlag(ImGuiWindowFlags.NoBackground)) Plugin.Overlay.Flags -= ImGuiWindowFlags.NoBackground; }, () => Plugin.Overlay != null);
        }
    }
    public bool ShowDutyLoopText       = true;
    public bool ShowActionText         = true;
    public bool UseSliderInputs        = false;
    public bool OverrideOverlayButtons = true;
    public bool GotoButton             = true;
    public bool TurninButton           = true;
    public bool DesynthButton          = true;
    public bool ExtractButton          = true;
    public bool RepairButton           = true;
    public bool EquipButton            = true;
    public bool CofferButton           = true;
    public bool TTButton               = true;


    //Duty Config Options
    public   bool AutoExitDuty                  = true;
    public   bool OnlyExitWhenDutyDone          = false;
    public   bool AutoManageRotationPluginState = true;
    /// <summary>
    /// 強制只用 BossMod AutoRotation,不管有沒有裝 WrathCombo／RotationSolver。
    /// 理由:WrathCombo/RSR 完全不讀 BossMod 模組自己算的 AIHints.Priority
    /// (例如黃金谷、Batraal 那類需要優先打特定 add 的王），永遠不會主動去打；
    /// 只有 BossMod 自己的 AutoRotation 會照這個 Priority 選目標。
    /// </summary>
    public   bool ForceBossModAutoRotation      = false;
    internal bool autoManageBossModAISettings   = true;
    public bool AutoManageBossModAISettings
    {
        get => autoManageBossModAISettings;
        set
        {
            autoManageBossModAISettings = value;
            HideBossModAIConfig = !value;
        }
    }
    public bool       AutoManageVnavAlignCamera      = true;
    public bool       LootTreasure                   = true;
    public LootMethod LootMethodEnum                 = LootMethod.AutoDuty;
    public bool       LootBossTreasureOnly           = false;
    public int        TreasureCofferScanDistance     = 25;
    public bool       RebuildNavmeshOnStuck          = true;
    public byte       RebuildNavmeshAfterStuckXTimes = 5;
    public int        MinStuckTime                   = 500;

    public bool PathDrawEnabled   = false;
    public int  PathDrawStepCount = 5;

    public bool       OverridePartyValidation        = false;
    public bool       UsingAlternativeRotationPlugin = false;
    public bool       UsingAlternativeMovementPlugin = false;
    public bool       UsingAlternativeBossPlugin     = false;

    public bool        TreatUnsyncAsW2W = true;
    public JobWithRole W2WJobs          = JobWithRole.Tanks;

    /// <summary>
    /// 解限模式下,交戰中不要停下來,繼續走到下一個定點(技能沿路交給輪替外掛放)。
    /// 預設關 —— 開了等於放棄「停下來把這一團清乾淨」的保證。
    /// </summary>
    public bool UnsyncedKeepMovingInCombat = false;

    /// <summary>
    /// 「解除限制」這個開關現在是不是真的生效中。
    /// 判斷式與 <see cref="IsW2W"/> 和 AutoDuty.StageReadingPath 裡那份完全一致:
    /// 解限只有在隨機任務/討伐戰/大型任務這三種模式下才有意義。
    /// </summary>
    public bool IsUnsyncActive() => this.Unsynced && this.DutyModeEnum.EqualsAny(DutyMode.Raid, DutyMode.Regular, DutyMode.Trial);

    public bool IsW2W(Job? job = null, bool? unsync = null)
    {
        job ??= PlayerHelper.GetJob();

        if (this.W2WJobs.HasJob(job.Value))
            return true;

        unsync ??= this.IsUnsyncActive();

        return unsync.Value && this.TreatUnsyncAsW2W;
    }


    //PreLoop Config Options
    public bool                                       EnablePreLoopActions     = true;
    public bool                                       ExecuteCommandsPreLoop   = false;
    public List<string>                               CustomCommandsPreLoop    = [];
    public bool                                       RetireMode               = false;
    public RetireLocation                             RetireLocationEnum       = RetireLocation.Inn;
    public List<System.Numerics.Vector3>              PersonalHomeEntrancePath = [];
    public List<System.Numerics.Vector3>              FCEstateEntrancePath     = [];
    public bool                                       AutoEquipRecommendedGear;
    public bool                                       AutoEquipRecommendedGearGearsetter;
    public bool                                       AutoEquipRecommendedGearGearsetterOldToInventory;
    public bool                                       AutoRepair              = false;
    public uint                                       AutoRepairPct           = 50;
    public bool                                       AutoRepairSelf          = false;
    public RepairNpcData?                             PreferredRepairNPC      = null;
    public bool                                       AutoConsume             = false;
    public bool                                       AutoConsumeIgnoreStatus = false;
    public int                                        AutoConsumeTime         = 29;
    public List<KeyValuePair<ushort, ConsumableItem>> AutoConsumeItemsList    = [];

    //Between Loop Config Options
    public bool         EnableBetweenLoopActions         = true;
    public bool         ExecuteBetweenLoopActionLastLoop = false;
    public int          WaitTimeBeforeAfterLoopActions   = 0;
    public bool         ExecuteCommandsBetweenLoop       = false;
    public List<string> CustomCommandsBetweenLoop        = [];
    public bool         AutoExtract                      = false;

    public bool                     AutoOpenCoffers = false;
    public byte?                    AutoOpenCoffersGearset;
    public bool                     AutoOpenCoffersBlacklistUse;
    public Dictionary<uint, string> AutoOpenCoffersBlacklist = [];

    internal bool autoExtractAll = false;
    public bool AutoExtractAll
    {
        get => autoExtractAll;
        set => autoExtractAll = value;
    }
    internal bool autoDesynth = false;
    public bool AutoDesynth
    {
        get => autoDesynth;
        set
        {
            autoDesynth = value;
            if (value && !AutoDesynthSkillUp)
                AutoGCTurnin = false;
        }
    }
    internal bool autoDesynthSkillUp = false;
    public bool AutoDesynthSkillUp
    {
        get => autoDesynthSkillUp;
        set
        {
            autoDesynthSkillUp = value;
            if (!value && AutoGCTurnin)
                AutoDesynth = false;
        }
    }
    public int AutoDesynthSkillUpLimit = 50;
    internal bool autoGCTurnin = false;
    public bool AutoGCTurnin
    {
        get => autoGCTurnin;
        set
        {
            autoGCTurnin = value;
            if (value && !AutoDesynthSkillUp)
                AutoDesynth = false;
        }
    }
    public int AutoGCTurninSlotsLeft = 5;
    public bool AutoGCTurninSlotsLeftBool = false;
    public bool AutoGCTurninUseTicket = false;

    public bool TripleTriadEnabled;
    public bool TripleTriadRegister;
    public bool TripleTriadSell;

    public bool DiscardItems;

    public bool EnableAutoRetainer = false;
    public SummoningBellLocations PreferredSummoningBellEnum = 0;
    //Termination Config Options
    public bool EnableTerminationActions = true;
    public bool StopLevel = false;
    public int StopLevelInt = 1;
    public bool StopNoRestedXP = false;
    public bool StopItemQty = false;
    public bool StopItemAll = false;
    public Dictionary<uint, KeyValuePair<string, int>> StopItemQtyItemDictionary = [];
    public int StopItemQtyInt = 1;
    public bool ExecuteCommandsTermination = false;
    public List<string> CustomCommandsTermination = [];
    public bool PlayEndSound = false;
    public bool CustomSound = false;
    public float CustomSoundVolume = 0.5f;
    public Sounds SoundEnum = Sounds.None;
    public string SoundPath = "";
    public TerminationMode TerminationMethodEnum = TerminationMode.Do_Nothing;
    public bool TerminationKeepActive = true;
    // 「自動化不是被你停掉、是自己停了」通知。預設關 —— 既有使用者的 JSON 已經有這個鍵之前
    // 都吃不到任何行為變化,要開必須自己到設定裡勾。
    public bool NotifyWhenStoppedItself = false;

    /// <summary>
    /// AutoDuty 自己停下來時，透過 IPC 請 TataruPraise（塔塔露誇獎）念一句。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>：TataruPraise 沒安裝時整條路是靜默 no-op（IPC 擲
    /// <c>IpcNotReadyError</c> 被吃掉），而 TataruPraise 自己的總開關（預設關）與冷卻也還在，
    /// 所以預設開<b>不會</b>讓任何人多聽到聲音。
    /// ⚠️ 與 <see cref="NotifyWhenStoppedItself"/> 刻意分成兩個旗標：那個是桌面通知，
    /// 這個是語音。想聽聲音的人不必連帶被彈通知，反之亦然。
    /// </remarks>
    public bool TataruPraiseOnStoppedItself = true;
    
    //BMAI Config Options
    public bool HideBossModAIConfig           = false;
    public bool BM_UpdatePresetsAutomatically = true;

    /// <summary>
    /// 非王步驟時要不要請 BossMod 的 AI 跟著隊伍裡的坦克走(StayCloseToPartyRole)。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>預設關,等於本欄位加入之前的行為</b> —— 這個功能本 fork 從來沒有過(上游有),
    /// 開了會讓角色在戰鬥中多一個「靠向坦克」的目標區,與既有的 StayCloseToTarget 疊加。
    /// 路徑步驟可以用 <see cref="PathActionFlags.NoPartyCoherency"/> 單獨關掉某一步;
    /// 王步驟(Name == "Boss")一律不跟。
    /// </remarks>
    public bool PartyCoherency = false;


    internal bool maxDistanceToTargetRoleBased = true;
    public bool MaxDistanceToTargetRoleBased
    {
        get => maxDistanceToTargetRoleBased;
        set
        {
            maxDistanceToTargetRoleBased = value;
            if (value)
                SchedulerHelper.ScheduleAction("MaxDistanceToTargetRoleBasedBMRoleChecks", () => Plugin.BMRoleChecks(), () => PlayerHelper.IsReady);
        }
    }
    public float MaxDistanceToTargetFloat = 2.6f;
    public float MaxDistanceToTargetAoEFloat = 12;
    
    internal bool positionalRoleBased = true;
    public bool PositionalRoleBased
    {
        get => positionalRoleBased;
        set
        {
            positionalRoleBased = value;
            if (value)
                SchedulerHelper.ScheduleAction("PositionalRoleBasedBMRoleChecks", () => Plugin.BMRoleChecks(), () => PlayerHelper.IsReady);
        }
    }
    public float MaxDistanceToTargetRoleMelee  = 2.6f;
    public float MaxDistanceToTargetRoleRanged = 10f;


    internal bool       positionalAvarice = true;
    public   Positional PositionalEnum    = Positional.Any;

    #region Wrath

    public   bool                                                       Wrath_AutoSetupJobs { get; set; } = true;
    public Wrath_IPCSubscriber.DPSRotationMode    Wrath_TargetingTank    = Wrath_IPCSubscriber.DPSRotationMode.Highest_Max;
    public Wrath_IPCSubscriber.DPSRotationMode    Wrath_TargetingNonTank = Wrath_IPCSubscriber.DPSRotationMode.Lowest_Current;


    #endregion


    public void Save()
    {
        EzConfig.Save();
    }

    public TrustMemberName?[] SelectedTrustMembers = new TrustMemberName?[3];
}

public static class ConfigTab
{
    internal static string FollowName = "";

    private static Configuration Configuration => Plugin.Configuration;
    private static string preLoopCommand = string.Empty;
    private static string betweenLoopCommand = string.Empty;
    private static string terminationCommand = string.Empty;

    private static string plannerSearchText = string.Empty;
    private static int plannerAddTargetRuns = 1;
    private static Dictionary<uint, Item> Items { get; set; } = Svc.Data.GetExcelSheet<Item>()?.Where(x => !x.Name.ToString().IsNullOrEmpty()).ToDictionary(x => x.RowId, x => x) ?? [];
    private static string stopItemQtyItemNameInput = "";
    private static KeyValuePair<uint, string> stopItemQtySelectedItem = new(0, "");

    private static string                     autoOpenCoffersNameInput    = "";
    private static KeyValuePair<uint, string> autoOpenCoffersSelectedItem = new(0, "");

    public class ConsumableItem
    {
        public uint ItemId;
        public string Name = string.Empty;
        public bool CanBeHq;
        public ushort StatusId;
    }

    private static List<ConsumableItem> ConsumableItems { get; set; } = Svc.Data.GetExcelSheet<Item>()?.Where(x => !x.Name.ToString().IsNullOrEmpty() && x.ItemUICategory.ValueNullable?.RowId is 44 or 45 or 46 && x.ItemAction.ValueNullable?.Data[0] is 48 or 49).Select(x => new ConsumableItem() { StatusId = x.ItemAction.Value!.Data[0], ItemId = x.RowId, Name = x.Name.ToString(), CanBeHq = x.CanBeHq }).ToList() ?? [];

    private static string consumableItemsItemNameInput = "";
    private static ConsumableItem consumableItemsSelectedItem = new();

    private static string profileRenameInput = "";

    private static readonly Sounds[] _validSounds = ((Sounds[])Enum.GetValues(typeof(Sounds))).Where(s => s != Sounds.None && s != Sounds.Unknown).ToArray();

    private static bool overlayHeaderSelected      = false;
    private static bool devHeaderSelected          = false;
    private static bool dutyConfigHeaderSelected   = false;
    private static bool bmaiSettingHeaderSelected  = false;
    private static bool wrathSettingHeaderSelected = false;
    private static bool w2wSettingHeaderSelected   = false;
    private static bool advModeHeaderSelected      = false;
    private static bool preLoopHeaderSelected      = false;
    private static bool betweenLoopHeaderSelected  = false;
    private static bool terminationHeaderSelected  = false;
    private static bool multiboxHeaderSelected     = false;

    public static void BuildManuals()
    {
        ConsumableItems.Add(new ConsumableItem { StatusId = 1086, ItemId = 14945, Name = "Squadron Enlistment Manual", CanBeHq = false });
        ConsumableItems.Add(new ConsumableItem { StatusId = 1080, ItemId = 14948, Name = "Squadron Battle Manual", CanBeHq = false });
        ConsumableItems.Add(new ConsumableItem { StatusId = 1081, ItemId = 14949, Name = "Squadron Survival Manual", CanBeHq = false });
        ConsumableItems.Add(new ConsumableItem { StatusId = 1082, ItemId = 14950, Name = "Squadron Engineering Manual", CanBeHq = false });
        ConsumableItems.Add(new ConsumableItem { StatusId = 1083, ItemId = 14951, Name = "Squadron Spiritbonding Manual", CanBeHq = false });
        ConsumableItems.Add(new ConsumableItem { StatusId = 1084, ItemId = 14952, Name = "Squadron Rationing Manual", CanBeHq = false });
        ConsumableItems.Add(new ConsumableItem { StatusId = 1085, ItemId = 14953, Name = "Squadron Gear Maintenance Manual", CanBeHq = false });
    }

    private static string plannerPlaylistNameInput     = string.Empty;
    private static bool   plannerPlaylistNameInputInit = false;

    /// <summary>
    /// 排程播放清單列:載入 / 儲存 / 刪除具名排程快照。
    /// </summary>
    /// <remarks>
    /// 🔴 這一段只搬動排程資料,<b>不啟動任何跑本流程</b>。載入之後仍然要由使用者按
    /// 「執行排程」,既有守衛(任務模式、組隊人數、練級互斥、路徑是否存在)一條都沒有繞過,
    /// 也沒有任何「登入自動跑清單」的路徑。
    /// ⚠️ 存的是<b>快照</b>:載入之後再改排程不會回寫已存的清單,要再按一次儲存才會覆蓋。
    /// </remarks>
    private static void DrawPlaylistBar()
    {
        List<Playlist> playlists = ConfigurationMain.Instance.Playlists;

        // 名稱輸入框第一次繪製時帶入目前載入中的清單名,之後就完全交給使用者編輯。
        if (!plannerPlaylistNameInputInit)
        {
            plannerPlaylistNameInputInit = true;
            plannerPlaylistNameInput     = Configuration.PlannerPlaylistName;
        }

        ImGui.TextDisabled("播放清單：");
        ImGui.SameLine();

        // 「沒載入」與「沒有存檔」是兩種不同狀態,列上就要分得出來,不要都畫成空白。
        string comboPreview = playlists.Count == 0                              ? "(尚無已儲存的清單)" :
                              Configuration.PlannerPlaylistName.IsNullOrEmpty() ? "(未載入)" :
                                                                                  Configuration.PlannerPlaylistName;

        ImGui.SetNextItemWidth(220 * ImGuiHelpers.GlobalScale);
        using (ImRaii.Disabled(playlists.Count == 0))
        {
            if (ImGui.BeginCombo("##PlannerPlaylistCombo", comboPreview))
            {
                for (int i = 0; i < playlists.Count; i++)
                {
                    Playlist playlist = playlists[i];

                    if (ImGui.Selectable($"{playlist.Name}（{playlist.Entries?.Count ?? 0} 項）##PlannerPlaylistLoad{i}",
                                         playlist.Name.Equals(Configuration.PlannerPlaylistName, StringComparison.OrdinalIgnoreCase)))
                        LoadPlaylist(playlist);
                }

                ImGui.EndCombo();
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(playlists.Count == 0 ?
                                 "還沒有已儲存的播放清單。在右邊填名稱後按儲存,就會把目前的排程存成一份。" :
                                 "載入已儲存的播放清單:取代目前的排程清單,並把進度歸零以便重跑。\n載入不會開始執行,仍要自己按「執行排程」。");

        ImGui.SameLine(0, 15f);

        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##PlannerPlaylistName", "清單名稱", ref plannerPlaylistNameInput, 64);

        string trimmedName = plannerPlaylistNameInput.Trim();

        ImGui.SameLine();
        using (ImRaii.Disabled(trimmedName.IsNullOrEmpty() || Configuration.PlannerItems.Count == 0))
            if (ImGuiComponents.IconButton("##PlannerPlaylistSave", FontAwesomeIcon.Save))
                SavePlaylist(trimmedName);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("把目前的排程清單存成這個名稱。\n同名的清單會被覆蓋。\n排程清單為空、或沒填名稱時不能按。");

        int existingIndex = playlists.FindIndex(p => p.Name.Equals(trimmedName, StringComparison.OrdinalIgnoreCase));

        ImGui.SameLine();
        using (ImRaii.Disabled(existingIndex < 0 || !ImGui.GetIO().KeyCtrl))
            if (ImGuiComponents.IconButton("##PlannerPlaylistDelete", FontAwesomeIcon.TrashAlt))
                DeletePlaylist(existingIndex);

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("刪除名稱欄位裡那個已儲存的播放清單。\n按住 Ctrl 才能按。\n只刪存檔,目前的排程清單不受影響。");
    }

    /// <summary>
    /// 載入具名播放清單:取代目前的排程清單並把進度歸零(這就是「重跑」語意)。
    /// </summary>
    private static void LoadPlaylist(Playlist playlist)
    {
        // 存的是快照,載入必須再複製一份,否則之後編輯排程會就地改到存檔內容。
        List<PlannerItem> entries = (playlist.Entries ?? []).JSONClone();

        foreach (PlannerItem entry in entries)
        {
            entry.TargetRuns    = Math.Max(1, entry.TargetRuns);
            entry.CompletedRuns = 0;
        }

        Configuration.PlannerItems        = entries;
        Configuration.PlannerCurrentIndex = 0;
        Configuration.PlannerPlaylistName = playlist.Name;
        plannerPlaylistNameInput          = playlist.Name;
        Configuration.Save();

        Svc.Log.Information($"AutoDuty 排程:已載入播放清單「{playlist.Name}」,共 {entries.Count} 項,進度已歸零。未開始執行。");
    }

    /// <summary>
    /// 把目前的排程清單存成具名播放清單。同名(忽略大小寫)覆蓋,否則新增。
    /// </summary>
    private static void SavePlaylist(string name)
    {
        List<PlannerItem> snapshot = (Configuration.PlannerItems ?? []).JSONClone();

        // 存的是「要跑什麼」不是「跑到哪」——進度不進存檔,載入時才會是乾淨的重跑。
        foreach (PlannerItem entry in snapshot)
            entry.CompletedRuns = 0;

        List<Playlist> playlists     = ConfigurationMain.Instance.Playlists;
        int            existingIndex = playlists.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Playlist       playlist      = new() { Name = name, Entries = snapshot };

        if (existingIndex >= 0)
            playlists[existingIndex] = playlist;
        else
            playlists.Add(playlist);

        Configuration.PlannerPlaylistName = name;
        Configuration.Save();

        Svc.Log.Information($"AutoDuty 排程:已{(existingIndex >= 0 ? "覆蓋" : "新增")}播放清單「{name}」,共 {snapshot.Count} 項。");
    }

    /// <summary>
    /// 刪除已儲存的播放清單。只動存檔,目前的排程清單保持原狀。
    /// </summary>
    private static void DeletePlaylist(int index)
    {
        List<Playlist> playlists = ConfigurationMain.Instance.Playlists;
        if (index < 0 || index >= playlists.Count)
            return;

        string name = playlists[index].Name;
        playlists.RemoveAt(index);

        // 存檔沒了,「目前載入的是誰」這個標示就不能再指著它。
        if (Configuration.PlannerPlaylistName.Equals(name, StringComparison.OrdinalIgnoreCase))
            Configuration.PlannerPlaylistName = string.Empty;

        Configuration.Save();

        Svc.Log.Information($"AutoDuty 排程:已刪除播放清單「{name}」。目前的排程清單未變動。");
    }

    internal static void DrawPlannerUi()
    {
        var plannerLocked = Plugin.States.HasFlag(PluginState.Looping) || Plugin.States.HasFlag(PluginState.Navigating);
        if (plannerLocked)
            ImGui.TextColored(ImGuiColors.DalamudYellow, "請先停止 AutoDuty 以編輯排程。");

        using (ImRaii.Disabled(plannerLocked))
        {
            if (ImGui.Checkbox("啟用排程器", ref Configuration.PlannerEnabled))
                Configuration.Save();
            ImGui.SameLine();
            if (ImGui.Checkbox("循環執行", ref Configuration.PlannerRepeat))
                Configuration.Save();
            ImGuiComponents.HelpMarker("依序執行任務：A×N 次後執行 B×M 次。成功完成後計數增加。");

            ImGui.Separator();
            DrawPlaylistBar();

            // Align key Main tab toggles in Planner.
            if (Configuration.DutyModeEnum.EqualsAny(DutyMode.Regular, DutyMode.Trial, DutyMode.Raid))
            {
                if (ImGuiEx.CheckboxWrapped("解除限制", ref Configuration.Unsynced))
                    Configuration.Save();
            }

            if (ImGui.Checkbox("隱藏不可用", ref Configuration.HideUnavailableDuties))
                Configuration.Save();

            ImGui.Separator();
            ImGui.TextDisabled("新增任務至排程：");

            plannerAddTargetRuns = Math.Max(1, plannerAddTargetRuns);
            ImGui.SetNextItemWidth(120 * ImGuiHelpers.GlobalScale);
            ImGui.InputInt("次數##PlannerAddRuns", ref plannerAddTargetRuns);
            if (plannerAddTargetRuns < 1)
                plannerAddTargetRuns = 1;

            ImGui.InputTextWithHint("##PlannerSearch", "搜尋任務...", ref plannerSearchText, 100);
            ImGuiComponents.HelpMarker("與 Main 一致：可用此開關隱藏目前不可執行的任務。");

            using (ImRaii.Child("##PlannerDutySearch", new Vector2(0, 140 * ImGuiHelpers.GlobalScale), true))
            {
                var level = PlayerHelper.GetCurrentLevelFromSheet();
                foreach (var content in ContentHelper.DictionaryContent.Values
                             .Where(c => c.DutyModes.HasFlag(Configuration.DutyModeEnum))
                             .OrderBy(c => c.ClassJobLevelRequired)
                             .ThenBy(c => c.Name))
                {
                    if (!plannerSearchText.IsNullOrEmpty() && !content.Name.Contains(plannerSearchText, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var canRun = content.CanRun(level);
                    if (Configuration.HideUnavailableDuties && !canRun)
                        continue;

                    using (ImRaii.Disabled(!canRun))
                    {
                        if (ImGui.Selectable($"L{content.ClassJobLevelRequired} ({content.TerritoryType}) {content.Name}##PlannerAdd{content.TerritoryType}"))
                        {
                            Configuration.PlannerItems.Add(new PlannerItem
                            {
                                TerritoryType = content.TerritoryType,
                                TargetRuns = plannerAddTargetRuns,
                                CompletedRuns = 0,
                                PathFileName = null,
                            });

                            if (Configuration.PlannerCurrentIndex < 0)
                                Configuration.PlannerCurrentIndex = 0;
                            Configuration.Save();
                        }
                    }
                }
            }

            if (Configuration.DutyModeEnum == DutyMode.Trust && Configuration.PlannerItems.Count > 0)
            {
                var trustIndex = Math.Clamp(Configuration.PlannerCurrentIndex, 0, Configuration.PlannerItems.Count - 1);
                var trustTerritory = Configuration.PlannerItems[trustIndex].TerritoryType;
                if (ContentHelper.DictionaryContent.TryGetValue(trustTerritory, out var trustContent) && trustContent.TrustMembers.Count > 0)
                {
                    ImGui.Separator();
                    ImGuiEx.LineCentered(() => ImGuiEx.TextUnderlined("選擇親信隊友（目前排程項）"));

                    TrustHelper.ResetTrustIfInvalid();
                    for (int i = 0; i < Configuration.SelectedTrustMembers.Length; i++)
                    {
                        var member = Configuration.SelectedTrustMembers[i];
                        if (member is null)
                            continue;

                        if (trustContent.TrustMembers.All(x => x.MemberName != member))
                            Configuration.SelectedTrustMembers[i] = null;
                    }

                    ImGui.Columns(3);
                    MainTab.DrawTrustMembers(trustContent);
                    ImGui.Columns(1);
                }
            }

            ImGui.Separator();

            if (Configuration.PlannerItems.Count == 0)
            {
                ImGui.TextDisabled("排程清單為空。");
                return;
            }

            if (ImGui.Button("重置排程進度"))
            {
                foreach (var item in Configuration.PlannerItems)
                    item.CompletedRuns = 0;
                Configuration.PlannerCurrentIndex = 0;
                Configuration.Save();
            }

            if (ImGui.BeginTable("##PlannerTable", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 22);
                ImGui.TableSetupColumn("任務", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("路徑", ImGuiTableColumnFlags.WidthFixed, 180);
                ImGui.TableSetupColumn("次數", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableSetupColumn("進度", ImGuiTableColumnFlags.WidthFixed, 70);
                ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 120);
                ImGui.TableHeadersRow();

                for (var i = 0; i < Configuration.PlannerItems.Count; i++)
                {
                    var item = Configuration.PlannerItems[i];
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted((i + 1).ToString());

                    ImGui.TableNextColumn();
                    var name = ContentHelper.DictionaryContent.TryGetValue(item.TerritoryType, out var c)
                                    ? $"L{c.ClassJobLevelRequired} ({c.TerritoryType}) {c.Name}"
                                    : $"({item.TerritoryType}) <未知>";
                    ImGui.TextUnformatted(name);
                    if (i == Configuration.PlannerCurrentIndex)
                    {
                        ImGui.SameLine();
                        ImGui.TextColored(ImGuiColors.DalamudYellow, "← 目前");
                    }

                    ImGui.TableNextColumn();
                    if (!ContentPathsManager.DictionaryPaths.TryGetValue(item.TerritoryType, out var container) || container.Paths.Count == 0)
                    {
                        ImGui.TextDisabled("無路徑");
                    }
                    else
                    {
                        string preview;
                        if (item.PathFileName.IsNullOrEmpty())
                        {
                            preview = "(自動)";
                        }
                        else
                        {
                            var selectedPath = container.Paths.FirstOrDefault(p => p.FileName.Equals(item.PathFileName, StringComparison.OrdinalIgnoreCase));
                            preview = selectedPath != null ? selectedPath.Name : "(缺失)";
                        }

                        ImGui.SetNextItemWidth(-1);
                        if (ImGui.BeginCombo($"##PlannerPath{i}", preview))
                        {
                            var isAuto = item.PathFileName.IsNullOrEmpty();
                            if (ImGui.Selectable("(自動)", isAuto))
                            {
                                item.PathFileName = null;
                                Configuration.Save();
                            }

                            foreach (var path in container.Paths)
                            {
                                var selected = !item.PathFileName.IsNullOrEmpty() && path.FileName.Equals(item.PathFileName, StringComparison.OrdinalIgnoreCase);
                                if (ImGui.Selectable(path.Name, selected))
                                {
                                    item.PathFileName = path.FileName;
                                    Configuration.Save();
                                }
                            }

                            ImGui.EndCombo();
                        }

                        if (!item.PathFileName.IsNullOrEmpty() && !container.Paths.Any(p => p.FileName.Equals(item.PathFileName, StringComparison.OrdinalIgnoreCase)))
                        {
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(item.PathFileName);
                        }
                    }

                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(-1);
                    var runs = item.TargetRuns;
                    if (ImGui.InputInt($"##PlannerRuns{i}", ref runs, 0, 0))
                    {
                        item.TargetRuns = Math.Max(1, runs);
                        item.CompletedRuns = Math.Clamp(item.CompletedRuns, 0, item.TargetRuns);
                        Configuration.Save();
                    }

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{item.CompletedRuns}/{item.TargetRuns}");

                    ImGui.TableNextColumn();
                    var changed = false;
                    if (ImGui.Button($"上移##PlannerUp{i}") && i > 0)
                    {
                        (Configuration.PlannerItems[i - 1], Configuration.PlannerItems[i]) = (Configuration.PlannerItems[i], Configuration.PlannerItems[i - 1]);
                        if (Configuration.PlannerCurrentIndex == i)
                            Configuration.PlannerCurrentIndex = i - 1;
                        else if (Configuration.PlannerCurrentIndex == i - 1)
                            Configuration.PlannerCurrentIndex = i;
                        changed = true;
                    }
                    ImGui.SameLine();
                    if (ImGui.Button($"刪除##PlannerDel{i}"))
                    {
                        Configuration.PlannerItems.RemoveAt(i);
                        if (Configuration.PlannerItems.Count == 0)
                            Configuration.PlannerCurrentIndex = 0;
                        else if (Configuration.PlannerCurrentIndex >= Configuration.PlannerItems.Count)
                            Configuration.PlannerCurrentIndex = Configuration.PlannerItems.Count - 1;
                        changed = true;
                        i--;
                    }

                    if (changed)
                        Configuration.Save();
                }

                ImGui.EndTable();
            }
        }
    }

    /// <summary>
    /// 多開協調(Multibox)設定區塊。
    ///
    /// 🔴 這裡是**唯一**的啟用入口:整個外掛沒有任何其他地方會把 MultiBox 打開。
    ///    開關本身不落地(MultiboxConfiguration.MultiBox 標了 [JsonIgnore]),
    ///    所以每次重載外掛都是關的。
    /// </summary>
    private static void DrawMultiboxSection()
    {
        MultiboxUtility.MultiboxConfiguration mb = MultiboxUtility.Config;

        ImGui.Separator();
        ImGui.Spacing();
        ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f));
        bool multiboxHeader = ImGui.Selectable("Multibox Settings".Loc(), multiboxHeaderSelected, ImGuiSelectableFlags.DontClosePopups);
        ImGui.PopStyleVar();
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (multiboxHeader)
            multiboxHeaderSelected = !multiboxHeaderSelected;

        if (!multiboxHeaderSelected)
            return;

        ImGui.Indent();

        // ── 開關(手動觸發,預設關,不落地) ────────────────────────────────
        bool enabled = mb.MultiBox;
        if (ImGui.Checkbox("Enable Multibox".Loc(), ref enabled))
            mb.MultiBox = enabled;
        ImGuiComponents.HelpMarker("Coordinates several game clients running AutoDuty together.\n\nWhat currently works: the host invites the clients to its party, the host's queue makes the clients accept their duty pop, and deaths are reported to the host.\n\nNot yet wired up: per-step lockstep along the path and sending the host's path to the clients. Clients still walk their own path on their own timing.\n\nThis switch is never saved - AutoDuty always starts with Multibox off, and nothing turns it on by itself. You must tick it manually on every client each time.".Loc());

        // ── 狀態:一律畫在列上,「不知道」也要看得見(不要畫成 0) ──────────
        ImGui.SameLine();
        if (!mb.MultiBox)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey, "(off)".Loc());
        }
        else if (mb.Host)
        {
            if (MultiboxUtility.Server.Running)
                ImGui.TextColored(ImGuiColors.HealerGreen, $"{"host".Loc()}: {MultiboxUtility.Server.ConnectedCount}/{MultiboxUtility.Server.MAX_SERVERS}");
            else
                ImGui.TextColored(ImGuiColors.DalamudRed, $"{"host".Loc()}: {"not started".Loc()}");
        }
        else
        {
            if (MultiboxUtility.Client.Connected)
                ImGui.TextColored(ImGuiColors.HealerGreen, $"{"client".Loc()}: {"connected".Loc()}");
            else if (MultiboxUtility.Client.Connecting)
                ImGui.TextColored(ImGuiColors.DalamudYellow, $"{"client".Loc()}: {"connecting...".Loc()}");
            else
                ImGui.TextColored(ImGuiColors.DalamudRed, $"{"client".Loc()}: {"not connected".Loc()}");
        }

        // 連線參數在連線期間不給改:改了也不會套用到已建立的連線,只會讓 UI 說謊。
        using (ImRaii.Disabled(mb.MultiBox))
        {
            bool host = mb.Host;
            if (ImGui.Checkbox("This client is the host".Loc(), ref host))
            {
                mb.Host = host;
                Configuration.Save();
            }
            ImGuiComponents.HelpMarker("Exactly one game client must be the host. The host runs the path and drives everyone else; the others follow.".Loc());

            bool syncPath = mb.SynchronizePath;
            if (ImGui.Checkbox("Synchronize path from host".Loc(), ref syncPath))
            {
                mb.SynchronizePath = syncPath;
                Configuration.Save();
            }
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey, "(not active yet)".Loc());
            ImGuiComponents.HelpMarker("Intended to make the host send its loaded path to every client. The transport for this exists but nothing calls it yet, so this setting currently has no effect - each client still uses its own path file.".Loc());

            TransportType transport = mb.TransportType;
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            if (ImGuiEx.EnumCombo("Transport".Loc(), ref transport))
            {
                mb.TransportType = transport;
                Configuration.Save();
            }

            if (mb.TransportType == TransportType.NamedPipe)
            {
                string pipeName = mb.PipeName;
                ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputText("Pipe name".Loc(), ref pipeName, 64))
                {
                    mb.PipeName = pipeName;
                    Configuration.Save();
                }

                if (!mb.Host)
                {
                    string serverName = mb.ServerName;
                    ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("Server name".Loc(), ref serverName, 64))
                    {
                        mb.ServerName = serverName;
                        Configuration.Save();
                    }
                    ImGuiComponents.HelpMarker("Use . for game clients running on this same computer.".Loc());
                }
            }
            else
            {
                if (!mb.Host)
                {
                    string serverAddress = mb.ServerAddress;
                    ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputText("Server address".Loc(), ref serverAddress, 64))
                    {
                        mb.ServerAddress = serverAddress;
                        Configuration.Save();
                    }
                }

                int port = mb.ServerPort;
                ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputInt("Server port".Loc(), ref port))
                {
                    mb.ServerPort = Math.Clamp(port, 1, 65535);
                    Configuration.Save();
                }
            }
        }

        if (mb.MultiBox)
            ImGuiEx.TextWrapped(ImGuiColors.DalamudGrey, "Turn Multibox off to change the connection settings.".Loc());

        ImGui.Unindent();
    }

    public static void Draw()
    {
        if (MainWindow.CurrentTabName != "Config")
            MainWindow.CurrentTabName = "Config";

        // 有別的外掛正在用 IPC 覆寫設定時,這一頁顯示的是「覆寫後」的值,改了也會在覆寫結束時
        // 被還原掉 —— 這件事必須在頁面上看得見,不能只寫進 log。
        bool overridesActive = ConfigOverrideHelper.HasOverrides;
        if (overridesActive)
        {
            ImGuiEx.TextWrapped(ImGuiColors.DalamudYellow,
                                "Another plugin is temporarily overriding AutoDuty's settings. The values below are the overridden ones and cannot be edited until it releases them; your own settings are untouched and will come back.".Loc());
            ImGui.Separator();
        }

        using var overrideLock = ImRaii.Disabled(overridesActive);

        //Start of Profile Selection
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Currently selected profile: ".Loc());
        ImGui.SameLine();
        if (ConfigurationMain.Instance.ActiveProfileName == ConfigurationMain.CONFIGNAME_BARE)
            ImGuiHelper.DrawIcon(FontAwesomeIcon.Lock);
        if (ConfigurationMain.Instance.ActiveProfileName == ConfigurationMain.Instance.DefaultConfigName)
            ImGuiHelper.DrawIcon(FontAwesomeIcon.CheckCircle);
        ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 180 * ImGuiHelpers.GlobalScale);
        ImGui.SetItemAllowOverlap();
        using (ImRaii.IEndObject configCombo = ImRaii.Combo("##ConfigCombo", ConfigurationMain.Instance.ActiveProfileName))
        {
            if (configCombo)
                foreach (string key in ConfigurationMain.Instance.ConfigNames)
                {
                    float selectableX = ImGui.GetCursorPosX();
                    if (key == ConfigurationMain.CONFIGNAME_BARE)
                        ImGuiHelper.DrawIcon(FontAwesomeIcon.Lock);
                    if (key == ConfigurationMain.Instance.DefaultConfigName)
                        ImGuiHelper.DrawIcon(FontAwesomeIcon.CheckCircle);

                    float textX = ImGui.GetCursorPosX();
                        
                    ImGui.SetCursorPosX(selectableX);
                    ImGui.SetItemAllowOverlap();
                    if (ImGui.Selectable($"###{key}ConfigSelectable"))
                        ConfigurationMain.Instance.SetProfile(key);
                    ImGui.SameLine(textX);
                    ImGui.Text(key);

                    ProfileData? profile = ConfigurationMain.Instance.GetProfile(key);
                    if(profile?.CIDs.Any() ?? false)
                    {
                        ImGui.SameLine();
                        ImGuiEx.TextWrapped(ImGuiHelper.VersionColor, string.Join(", ", profile.CIDs.Select(cid => ConfigurationMain.Instance.charByCID.TryGetValue(cid, out ConfigurationMain.CharData cd) ? cd.GetName() : cid.ToString())));
                    }
                }
        }

        ImGui.PopItemWidth();
        ImGui.SameLine();

        if (ImGui.IsPopupOpen("##RenameProfile"))
        {
            bool    open     = true;
            Vector2 textSize = ImGui.CalcTextSize(profileRenameInput);
            ImGui.SetNextWindowSize(new Vector2(textSize.X + 200, textSize.Y + 120) * ImGuiHelpers.GlobalScale);
            if (ImGui.BeginPopupModal($"##RenameProfile", ref open, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoMove))
            {
                ImGuiHelper.CenterNextElement(ImGui.CalcTextSize("New Profile Name".Loc()).X);
                ImGui.Text("New Profile Name".Loc());
                ImGui.NewLine();
                ImGui.SameLine(50);
                ImGui.SetNextItemWidth((textSize.X + 100) * ImGuiHelpers.GlobalScale);

                ImGui.InputText("##RenameProfileInput", ref profileRenameInput, 100);
                ImGui.Spacing();
                ImGuiHelper.CenterNextElement(ImGui.CalcTextSize("Change Profile Name".Loc()).X);
                if (ImGui.Button("Change Profile Name".Loc()))
                {
                    if (ConfigurationMain.Instance.RenameCurrentProfile(profileRenameInput))
                    {
                        open = false;
                        ImGui.CloseCurrentPopup();
                    }
                }

                ImGui.EndPopup();
            }
        }



        bool bareProfile = ConfigurationMain.Instance.ActiveProfileName == ConfigurationMain.CONFIGNAME_BARE;

        if (ImGuiComponents.IconButton(FontAwesomeIcon.Plus))
            ConfigurationMain.Instance.CreateNewProfile();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Create new Profile".Loc());

        ImGui.SameLine(0, 15f);
        using (ImRaii.Disabled(bareProfile))
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Pen))
            {
                profileRenameInput = ConfigurationMain.Instance.ActiveProfileName;
                ImGui.OpenPopup("##RenameProfile");
            }

        if (ImGui.IsMouseHoveringRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax()))
            ImGui.SetTooltip("Rename Profile".Loc());

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.Copy))
            ConfigurationMain.Instance.DuplicateCurrentProfile();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Duplicate Profile".Loc());

        ImGui.SameLine();
        using (ImRaii.Disabled(ImGui.GetIO().KeyCtrl ? ConfigurationMain.Instance.GetCurrentProfile.CIDs.Contains(Player.CID) != ImGui.GetIO().KeyShift : ConfigurationMain.Instance.DefaultConfigName == ConfigurationMain.Instance.ActiveProfileName))
            if (ImGuiComponents.IconButton(FontAwesomeIcon.CheckCircle))
                if(ImGui.GetIO().KeyCtrl)
                    if (ImGui.GetIO().KeyShift)
                        ConfigurationMain.Instance.RemoveCharacterDefault();
                    else
                        ConfigurationMain.Instance.SetCharacterDefault();
                else
                    ConfigurationMain.Instance.SetProfileAsDefault();
        if (ImGui.IsMouseHoveringRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax()))
            ImGui.SetTooltip("Make Default\nHold ctrl to make default for the current character\nctrl+shift to remove it as default for the current character".Loc());


        ImGui.SameLine();
        using (ImRaii.Disabled(bareProfile || !ImGui.GetIO().KeyCtrl))
            if (ImGuiComponents.IconButton(FontAwesomeIcon.TrashAlt))
                ConfigurationMain.Instance.RemoveCurrentProfile();
        if (ImGui.IsMouseHoveringRect(ImGui.GetItemRectMin(), ImGui.GetItemRectMax()))
            ImGui.SetTooltip("Delete Config\nHold ctrl to enable".Loc());

        if (bareProfile)
            ImGuiEx.TextWrapped("The bare profile is for just running a duty, and nothing else. You can duplicate it to make edits.".Loc());
        using ImRaii.IEndObject _ = ImRaii.Disabled(bareProfile);

        //Start of Window & Overlay Settings
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f));
        var overlayHeader = ImGui.Selectable("Window & Overlay Settings".Loc(), overlayHeaderSelected, ImGuiSelectableFlags.DontClosePopups);
        ImGui.PopStyleVar();      
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (overlayHeader)
            overlayHeaderSelected = !overlayHeaderSelected;

        if (overlayHeaderSelected == true)
        {
            if (ImGui.Checkbox("Show Overlay".Loc(), ref Configuration.showOverlay))
            {
                Configuration.ShowOverlay = Configuration.showOverlay;
                Configuration.Save();
            }
            ImGuiComponents.HelpMarker("Note that the quickaction buttons (TurnIn/Desynth/etc) require their respective configs to be enabled!\nOr Override Overlay Buttons to be Enabled".Loc());
            if (Configuration.ShowOverlay)
            {
                ImGui.Indent();
                ImGui.Columns(2, "##OverlayColumns", false);

                //ImGui.SameLine(0, 53);
                if (ImGui.Checkbox("Hide When Stopped".Loc(), ref Configuration.hideOverlayWhenStopped))
                {
                    Configuration.HideOverlayWhenStopped = Configuration.hideOverlayWhenStopped;
                    Configuration.Save();
                }
                ImGui.NextColumn();
                if (ImGui.Checkbox("Lock Overlay".Loc(), ref Configuration.lockOverlay))
                {
                    Configuration.LockOverlay = Configuration.lockOverlay;
                    Configuration.Save();
                }
                ImGui.NextColumn();
                //ImGui.SameLine(0, 57);

                if (ImGui.Checkbox("Show Duty/Loops Text".Loc(), ref Configuration.ShowDutyLoopText))
                    Configuration.Save();
                ImGui.NextColumn();
                if (ImGui.Checkbox("Use Transparent BG".Loc(), ref Configuration.overlayNoBG))
                {
                    Configuration.OverlayNoBG = Configuration.overlayNoBG;
                    Configuration.Save();
                }
                ImGui.NextColumn();
                if (ImGui.Checkbox("Override Overlay Buttons".Loc(), ref Configuration.OverrideOverlayButtons))
                    Configuration.Save();
                ImGuiComponents.HelpMarker("Overlay buttons by default are enabled if their config is enabled\nThis will allow you to chose which buttons are enabled".Loc());
                ImGui.NextColumn();
                if (ImGui.Checkbox("Show AD Action Text".Loc(), ref Configuration.ShowActionText))
                    Configuration.Save();
                if (Configuration.OverrideOverlayButtons)
                {
                    ImGui.Indent();
                    ImGui.Columns(3, "##OverlayButtonColumns", false);
                    if (ImGui.Checkbox("Goto".Loc(), ref Configuration.GotoButton))
                        Configuration.Save();
                    ImGui.NextColumn();
                    if (ImGui.Checkbox("Turnin".Loc(), ref Configuration.TurninButton))
                        Configuration.Save();
                    ImGui.NextColumn();
                    if (ImGui.Checkbox("Desynth".Loc(), ref Configuration.DesynthButton))
                        Configuration.Save();
                    ImGui.NextColumn();
                    if (ImGui.Checkbox("Extract".Loc(), ref Configuration.ExtractButton))
                        Configuration.Save();
                    ImGui.NextColumn();
                    if (ImGui.Checkbox("Repair".Loc(), ref Configuration.RepairButton))
                        Configuration.Save();
                    ImGui.NextColumn();
                    if (ImGui.Checkbox("Equip".Loc(), ref Configuration.EquipButton))
                        Configuration.Save();
                    ImGui.NextColumn();
                    if (ImGui.Checkbox("Coffer".Loc(), ref Configuration.CofferButton))
                        Configuration.Save();
                    ImGui.NextColumn();
                    if (ImGui.Checkbox("Triple Triad".Loc() + "##TTButton", ref Configuration.TTButton))
                        Configuration.Save();
                    ImGui.Unindent();
                }
                ImGui.Unindent();
            }
            ImGui.Columns(1);
            if (ImGui.Checkbox("Show Main Window on Startup".Loc(), ref Configuration.ShowMainWindowOnStartup))
                Configuration.Save();
            ImGui.SameLine();
            if (ImGui.Checkbox("Slider Inputs".Loc(), ref Configuration.UseSliderInputs))
                Configuration.Save();
            
        }

        DrawMultiboxSection();

        if (Plugin.isDev)
        {
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f));
            var devHeader = ImGui.Selectable("Dev Settings", devHeaderSelected, ImGuiSelectableFlags.DontClosePopups);
            ImGui.PopStyleVar();
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (devHeader)
                devHeaderSelected = !devHeaderSelected;

            if (devHeaderSelected)
            {
                if (ImGui.Checkbox("Update Paths on startup", ref ConfigurationMain.Instance.updatePathsOnStartup))
                    Configuration.Save();

                if (ImGui.Button("Print mod list")) 
                    Svc.Log.Info(string.Join("\n", PluginInterface.InstalledPlugins.Where(pl => pl.IsLoaded).GroupBy(pl => pl.Manifest.InstalledFromUrl).OrderByDescending(g => g.Count()).Select(g => g.Key+"\n\t"+string.Join("\n\t", g.Select(pl => pl.Name)))));

                if (ImGui.CollapsingHeader("Available Duty Support"))//ImGui.Button("check duty support?"))
                {
                    if(GenericHelpers.TryGetAddonMaster<AddonMaster.DawnStory>(out AddonMaster.DawnStory? m))
                    {
                        if (m.IsAddonReady)
                        {
                            ImGuiEx.Text("Selected: " + m.Reader.CurrentSelection);

                            ImGuiEx.Text($"Cnt: {m.Reader.EntryCount}");
                            foreach (var x in m.Entries)
                            {
                                ImGuiEx.Text($"{x.Name} / {x.ReaderEntry.Callback} / {x.Index}");
                                if (ImGuiEx.HoveredAndClicked() && x.Status != 2)
                                {
                                    // 全 repo 唯一不經 AddonHelper 的按法(Entry.Select() 逐字是 Callback.Fire(Base, true, 12, cb)),
                                    // 手動點擊也走同一道守衛:同一扇窗同一個項目在窗走完前重按一律擋。
                                    unsafe
                                    {
                                        if (AddonPressGuard.TryBeginPress("DawnStory", m.Base, AddonPressGuard.BuildPressKey(true, [12, x.ReaderEntry.Callback])))
                                            x.Select();
                                    }
                                }
                            }
                        }
                    }
                }

                if (ImGui.CollapsingHeader("Available TT cards"))
                {
                    unsafe
                    {
                        if (GenericHelpers.TryGetAddonByName("TripleTriadCoinExchange", out AtkUnitBase* exchangeAddon))
                        {
                            if (exchangeAddon->IsReady)
                            {
                                ReaderTripleTriadCoinExchange exchange = new(exchangeAddon);

                                ImGuiEx.Text($"Cnt: {exchange.EntryCount}");
                                foreach (var x in exchange.Entries)
                                {
                                    ImGuiEx.Text($"({x.Id}) {x.Name} | {x.Count} | {x.Value} | {x.InDeck}");
                                    if (ImGuiEx.HoveredAndClicked())
                                    {
                                        //x.Select();
                                    }
                                }
                            }
                        }
                    }
                }



                if (ImGui.Button("Turn on rotation"))
                {
                    Plugin.SetRotationPluginSettings(true, ignoreTimer: true);
                }

                ImGui.SameLine();
                if (ImGui.Button("Turn off rotation"))
                {
                    Plugin.SetRotationPluginSettings(false);
                    if(Wrath_IPCSubscriber.IsEnabled)
                        Wrath_IPCSubscriber.Release();
                }

                if (ImGui.Button("BetweenLoopActions##DevBetweenLoops"))
                {
                    Plugin.CurrentTerritoryContent =  ContentHelper.DictionaryContent.Values.First();
                    Plugin.States                  |= PluginState.Other;
                    Plugin.LoopTasks(false);
                }

                if (ImGui.CollapsingHeader("teleport playthings"))
                {
                    if (ImGui.CollapsingHeader("Warps"))
                    {
                        ImGui.Indent();
                        foreach (Warp warp in Svc.Data.GameData.GetExcelSheet<Warp>())
                        {
                            if (warp.TerritoryType.RowId != 152)
                                continue;

                            if (ImGui.CollapsingHeader($"{warp.Name} {warp.Question} to {warp.TerritoryType.ValueNullable?.PlaceName.ValueNullable?.Name.ToString()}##{warp.RowId}"))
                            {
                                if (warp.PopRange.ValueNullable is { } level)
                                {
                                    ImGui.Text($"{level.X} {level.Y} {level.Z} in {level.Territory.ValueNullable?.PlaceName.ValueNullable?.Name.ToString()}");
                                    ImGui.Text($"{(new Vector3(level.X, level.Y, level.Z) - Player.Position)}");
                                }
                            }
                        }

                        ImGui.Unindent();
                    }

                    if (ImGui.CollapsingHeader("LevelTest"))
                    {
                        foreach ((Level lvl, Vector3, Vector3) level in Svc.Data.GameData.GetExcelSheet<Level>().Where(lvl => lvl.Territory.RowId == 152)
                                                                           .Select(lvl => (lvl, (new Vector3(lvl.X, lvl.Y, lvl.Z))))
                                                                           .Select(tuple => (tuple.lvl, tuple.Item2, (tuple.Item2 - Player.Position))).OrderBy(lvl => lvl.Item3.LengthSquared()))
                        {
                            ImGui.Text($"{level.lvl.RowId} {level.Item2} {level.Item3} {string.Join(" | ", level.lvl.Object.GetType().GenericTypeArguments.Select(t => t.FullName))}: {level.lvl.Object.RowId}");
                        }
                    }

                    ImGuiEx.Text($"{typeof(Achievement).Assembly.GetTypes().Where(x => x.FullName.StartsWith("Lumina.Excel.Sheets")).Select(x => (x, x.GetProperties().Where(f => f.PropertyType.Name == "RowRef`1" && f.PropertyType.GenericTypeArguments[0].FullName == typeof(Map).FullName))).Where(x => x.Item2.Any()).Select(x => $"{x.Item1} references {x.Item2.Select(x => x.Name).Print(", ")}").Print("\n")}");
                }
            }
        }
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f));
        var dutyConfigHeader = ImGui.Selectable("Duty Config Settings".Loc(), dutyConfigHeaderSelected, ImGuiSelectableFlags.DontClosePopups);
        ImGui.PopStyleVar();
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (dutyConfigHeader)
            dutyConfigHeaderSelected = !dutyConfigHeaderSelected;

        if (dutyConfigHeaderSelected == true)
        {
            ImGui.Columns(2, "##DutyConfigHeaderColumns");
            if (ImGui.Checkbox("Auto Leave Duty in last loop".Loc(), ref Configuration.AutoExitDuty))
                Configuration.Save();
            ImGuiComponents.HelpMarker("Will automatically exit the dungeon upon completion of the path.".Loc());
            ImGui.NextColumn();
            if (ImGui.Checkbox("Block leaving duty until it's complete".Loc(), ref Configuration.OnlyExitWhenDutyDone))
                Configuration.Save();
            //ImGuiComponents.HelpMarker("Blocks leaving dungeon before duty is completed");
            ImGui.Columns(1);
            if (ImGui.Checkbox("Auto Manage Rotation Plugin State".Loc(), ref Configuration.AutoManageRotationPluginState))
                Configuration.Save();
            ImGuiComponents.HelpMarker("Autoduty will enable the Rotation Plugin at the start of each duty\n*Only if using Wrath Combo, Rotation Solver or BossMod AutoRotation\n**AutoDuty will try to use them in that order".Loc());

            if (Configuration.AutoManageRotationPluginState)
            {
                if (ImGui.Checkbox("強制只用 BossMod AutoRotation".Loc(), ref Configuration.ForceBossModAutoRotation))
                    Configuration.Save();
                ImGuiComponents.HelpMarker("開啟後：不管有沒有裝 WrathCombo 或 RotationSolver，戰鬥輸出一律改用 BossMod 自己的 AutoRotation，原本裝的循環外掛會被主動關掉。\n\n" +
                                            "為什麼要開：有些王會標記某個敵人是「要優先打」的目標（例如需要優先擊殺、否則會造成團滅的 add）。這個優先度資訊只存在 BossMod 的王模組裡，只有 BossMod 自己的 AutoRotation 讀得到——WrathCombo、RotationSolver 完全不知道這件事，永遠只會照自己的選怪邏輯（例如打體型最大或血量最高的）打，不會主動去打那個 add。".Loc());

                // 🔴 這個開關單獨開沒有用:送出 "AutoDuty" preset 的 SetPreset() 整個被
                //    「Auto Manage BossMod AI Settings」gate 住,兩個都要開才會有輸出。沒開的時候
                //    SetRotationPluginSettings() 會直接忽略這個開關(退回 Wrath → RSR → BossMod 的
                //    原順序),不會讓角色乾站著 —— 但使用者得知道「我勾了卻沒作用」是為什麼。
                if (Configuration.ForceBossModAutoRotation && !Configuration.AutoManageBossModAISettings)
                    ImGui.TextColored(ImGuiColors.DalamudYellow,
                                      "　⚠ 需要同時開啟下方的「Auto Manage BossMod AI Settings」才會生效,目前暫時忽略。".Loc());
            }

            ImGui.Separator();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("排程器：");
            ImGui.SameLine(0, 6);
            if (ImGui.Button("開啟排程分頁"))
                MainWindow.OpenTab("排程器");
            ImGui.SameLine(0, 6);
            ImGui.TextDisabled(Configuration.PlannerEnabled ? $"已啟用（{Configuration.PlannerItems.Count} 項）" : "未啟用");
            ImGuiComponents.HelpMarker("排程設定已移至「排程器」分頁。");

            if (Configuration.AutoManageRotationPluginState)
            {
                if (Wrath_IPCSubscriber.IsEnabled)
                {
                    ImGui.Indent();
                    ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f));
                    var wrathSettingHeader = ImGui.Selectable("> Wrath Combo Config Options <".Loc(), wrathSettingHeaderSelected, ImGuiSelectableFlags.DontClosePopups);
                    ImGui.PopStyleVar();
                    if (ImGui.IsItemHovered())
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    if (wrathSettingHeader)
                        wrathSettingHeaderSelected = !wrathSettingHeaderSelected;

                    if (wrathSettingHeaderSelected)
                    {
                        bool wrath_AutoSetupJobs = Configuration.Wrath_AutoSetupJobs;
                        if (ImGui.Checkbox("Auto setup jobs for autorotation".Loc(), ref wrath_AutoSetupJobs))
                        {
                            Configuration.Wrath_AutoSetupJobs = wrath_AutoSetupJobs;
                            Configuration.Save();
                        }
                        ImGuiComponents.HelpMarker("If this is not enabled and a job is not setup in Wrath Combo, AD will instead use RSR or bm AutoRotation".Loc());

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text("Targeting | Tank: ".Loc());
                        ImGui.SameLine(0, 5);
                        ImGui.PushItemWidth(150 * ImGuiHelpers.GlobalScale);
                        if (ImGui.BeginCombo("##ConfigWrathTargetingTank", Configuration.Wrath_TargetingTank.ToCustomString()))
                        {
                            foreach (Wrath_IPCSubscriber.DPSRotationMode targeting in Enum.GetValues(typeof(Wrath_IPCSubscriber.DPSRotationMode)))
                            {
                                if(targeting == Wrath_IPCSubscriber.DPSRotationMode.Tank_Target)
                                    continue;

                                if (ImGui.Selectable(targeting.ToCustomString()))
                                {
                                    Configuration.Wrath_TargetingTank = targeting;
                                    Configuration.Save();
                                }
                            }
                            ImGui.EndCombo();
                        }

                        ImGui.AlignTextToFramePadding();
                        ImGui.Text("Targeting | Non-Tank: ".Loc());
                        ImGui.SameLine(0, 5);
                        ImGui.PushItemWidth(150 * ImGuiHelpers.GlobalScale);
                        if (ImGui.BeginCombo("##ConfigWrathTargetingNonTank", Configuration.Wrath_TargetingNonTank.ToCustomString()))
                        {
                            foreach (Wrath_IPCSubscriber.DPSRotationMode targeting in Enum.GetValues(typeof(Wrath_IPCSubscriber.DPSRotationMode)))
                            {
                                if (ImGui.Selectable(targeting.ToCustomString()))
                                {
                                    Configuration.Wrath_TargetingNonTank = targeting;
                                    Configuration.Save();
                                }
                            }
                            ImGui.EndCombo();
                        }

                        ImGui.Separator();
                    }
                    ImGui.Unindent();
                }
            }

            if (ImGui.Checkbox("Auto Manage BossMod AI Settings".Loc(), ref Configuration.autoManageBossModAISettings))
                Configuration.Save();
            ImGuiComponents.HelpMarker("Autoduty will enable BMAI and any options you configure at the start of each duty.".Loc());

            if (Configuration.autoManageBossModAISettings)
            {
                ImGui.Indent();
                ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f));
                var bmaiSettingHeader = ImGui.Selectable("> BMAI Config Options <".Loc(), bmaiSettingHeaderSelected, ImGuiSelectableFlags.DontClosePopups);
                ImGui.PopStyleVar();
                if (ImGui.IsItemHovered())
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (bmaiSettingHeader)
                    bmaiSettingHeaderSelected = !bmaiSettingHeaderSelected;
            
                if (bmaiSettingHeaderSelected == true)
                {
                    if (ImGui.Button("Update Presets".Loc()))
                    {
                        BossMod_IPCSubscriber.RefreshPreset("AutoDuty", Resources.AutoDutyPreset);
                        BossMod_IPCSubscriber.RefreshPreset("AutoDuty Passive", Resources.AutoDutyPassivePreset);
                    }
                    if (ImGui.Checkbox("Update Presets automatically".Loc(), ref Configuration.BM_UpdatePresetsAutomatically))
                        Configuration.Save();
                    if (ImGui.Checkbox("Stay Close to Tank (Party Coherency)".Loc(), ref Configuration.PartyCoherency))
                    {
                        // 關掉的當下就把 BossMod 那條軌道歸零,不要等到下一次讀路徑步驟。
                        BossMod_IPCSubscriber.StayCloseToTank(false);
                        Configuration.Save();
                    }
                    ImGuiComponents.HelpMarker("Off by default. When on, AutoDuty asks BossMod AI to keep you near the party's tank on every non-boss step. Individual path steps can opt out with the NoPartyCoherency flag.".Loc());
                    if (ImGui.Checkbox("Set Max Distance To Target Based on Player Role".Loc(), ref Configuration.maxDistanceToTargetRoleBased))
                    {
                        Configuration.MaxDistanceToTargetRoleBased = Configuration.maxDistanceToTargetRoleBased;
                        Configuration.Save();
                    }
                    using (ImRaii.Disabled(Configuration.MaxDistanceToTargetRoleBased))
                    {
                        ImGui.PushItemWidth(195 * ImGuiHelpers.GlobalScale);
                        // 滑桿在拖曳期間每一幀都回 true，存檔只能在放開滑鼠（編輯結束）那一刻做一次
                        if (ImGui.SliderFloat("Max Distance To Target".Loc(), ref Configuration.MaxDistanceToTargetFloat, 1, 30))
                            Configuration.MaxDistanceToTargetFloat = Math.Clamp(Configuration.MaxDistanceToTargetFloat, 1, 30);
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            Configuration.Save();
                        if (ImGui.SliderFloat("Max Distance To Target AoE".Loc(), ref Configuration.MaxDistanceToTargetAoEFloat, 1, 10))
                            Configuration.MaxDistanceToTargetAoEFloat = Math.Clamp(Configuration.MaxDistanceToTargetAoEFloat, 1, 10);
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            Configuration.Save();
                        ImGui.PopItemWidth();
                    }
                    using (ImRaii.Disabled(!Configuration.MaxDistanceToTargetRoleBased))
                    {
                        ImGui.PushItemWidth(195 * ImGuiHelpers.GlobalScale);
                        if (ImGui.SliderFloat("Max Distance To Target | Melee".Loc(), ref Configuration.MaxDistanceToTargetRoleMelee, 1, 30))
                            Configuration.MaxDistanceToTargetRoleMelee = Math.Clamp(Configuration.MaxDistanceToTargetRoleMelee, 1, 30);
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            Configuration.Save();
                        if (ImGui.SliderFloat("Max Distance To Target | Ranged".Loc(), ref Configuration.MaxDistanceToTargetRoleRanged, 1, 30))
                            Configuration.MaxDistanceToTargetRoleRanged = Math.Clamp(Configuration.MaxDistanceToTargetRoleRanged, 1, 30);
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            Configuration.Save();
                        ImGui.PopItemWidth();
                    }
                    if (ImGui.Checkbox("Set Positional Based on Player Role".Loc(), ref Configuration.positionalRoleBased))
                    {
                        Configuration.PositionalRoleBased = Configuration.positionalRoleBased;
                        Plugin.BMRoleChecks();
                        Configuration.Save();
                    }
                    using (ImRaii.Disabled(Configuration.positionalRoleBased))
                    {
                        ImGui.SameLine(0, 10);
                        if (ImGui.Button(Configuration.PositionalEnum.ToCustomString()))
                            ImGui.OpenPopup("PositionalPopup");
            
                        if (ImGui.BeginPopup("PositionalPopup"))
                        {
                            foreach (Positional positional in Enum.GetValues(typeof(Positional)))
                            {
                                if (ImGui.Selectable(positional.ToCustomString()))
                                {
                                    Configuration.PositionalEnum = positional;
                                    Configuration.Save();
                                }
                            }
                            ImGui.EndPopup();
                        }
                    }
                    if (ImGui.Button("Use Default BMAI Settings".Loc()))
                    {
                        Configuration.maxDistanceToTargetRoleBased = true;
                        Configuration.positionalRoleBased = true;
                        Configuration.Save();
                    }
                    ImGuiComponents.HelpMarker("Clicking this will reset your BMAI config to the default and *recommended* settings for AD".Loc());

                    ImGui.Separator();
                }
                ImGui.Unindent();
            }
            if (ImGui.Checkbox("Auto Manage Vnav Align Camera".Loc(), ref Configuration.AutoManageVnavAlignCamera))
                Configuration.Save();
            ImGuiComponents.HelpMarker("Autoduty will enable AlignCamera in VNav at the start of each duty, and disable it when done if it was not set.".Loc());

            if (ImGui.Checkbox("Loot Treasure Coffers".Loc(), ref Configuration.LootTreasure))
                Configuration.Save();

            if (Configuration.LootTreasure)
            {
                ImGui.Indent();
                ImGui.Text("Select Method: ".Loc());
                ImGui.SameLine(0, 5);
                ImGui.PushItemWidth(150 * ImGuiHelpers.GlobalScale);
                if (ImGui.BeginCombo("##ConfigLootMethod", Configuration.LootMethodEnum.ToCustomString()))
                {
                    foreach (LootMethod lootMethod in Enum.GetValues(typeof(LootMethod)))
                    {
                        if(lootMethod == LootMethod.RotationSolver)
                            continue;
                        using (ImRaii.Disabled((lootMethod == LootMethod.Pandora && !PandorasBox_IPCSubscriber.IsEnabled)))
                        {
                            if (ImGui.Selectable(lootMethod.ToCustomString()))
                            {
                                Configuration.LootMethodEnum = lootMethod;
                                Configuration.Save();
                            }
                        }
                    }
                    ImGui.EndCombo();
                }
                
                if (ImGui.Checkbox("Loot Boss Treasure Only".Loc(), ref Configuration.LootBossTreasureOnly))
                        Configuration.Save();

                ImGuiComponents.HelpMarker("AutoDuty will walk around non-boss chests, and only loot boss chests.\nNot all paths may accomodate.".Loc());
                ImGui.PopItemWidth();
                ImGui.Unindent();
            }
            ImGui.PushItemWidth(150 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("Minimum time before declared stuck (in ms)".Loc(), ref Configuration.MinStuckTime))
            {
                Configuration.MinStuckTime = Math.Max(250, Configuration.MinStuckTime);
                Configuration.Save();
            }

            if (ImGui.Checkbox("Rebuild Navmesh when stuck".Loc(), ref Configuration.RebuildNavmeshOnStuck))
                Configuration.Save();

            if (Configuration.RebuildNavmeshOnStuck)
            {
                ImGui.SameLine();
                int rebuildX = Configuration.RebuildNavmeshAfterStuckXTimes;
                if(ImGui.InputInt("times".Loc() + "##RebuildNavmeshAfterStuckXTimes", ref rebuildX))
                {
                    Configuration.RebuildNavmeshAfterStuckXTimes = (byte) Math.Clamp(rebuildX, byte.MinValue+1, byte.MaxValue);
                    Configuration.Save();
                }
            }

            if(ImGui.Checkbox("Draw next steps in Path".Loc(), ref Configuration.PathDrawEnabled))
                Configuration.Save();
            ImGui.PopItemWidth();
            if (Configuration.PathDrawEnabled)
            {
                ImGui.Indent();
                ImGui.PushItemWidth(150 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputInt("Drawing X steps".Loc() + "##PathDrawStepCount", ref Configuration.PathDrawStepCount, 1))
                {
                    Configuration.PathDrawStepCount = Math.Max(1, Configuration.PathDrawStepCount);
                    Configuration.Save();
                }
                ImGui.PopItemWidth();
                ImGui.Unindent();
            }



            ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f));
            bool w2wSettingHeader = ImGui.Selectable("> ?? Config <".Loc(PathIdentifiers.W2W), w2wSettingHeaderSelected, ImGuiSelectableFlags.DontClosePopups);
            ImGui.PopStyleVar();
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (w2wSettingHeader)
                w2wSettingHeaderSelected = !w2wSettingHeaderSelected;

            if (w2wSettingHeaderSelected)
            {
                if(ImGui.Checkbox("Treat Unsync as W2W".Loc(), ref Configuration.TreatUnsyncAsW2W))
                    Configuration.Save();
                ImGuiComponents.HelpMarker("Only works in paths with W2W tags on steps".Loc());

                if (ImGui.Checkbox("解限模式:交戰中繼續走到定點", ref Configuration.UnsyncedKeepMovingInCombat))
                    Configuration.Save();
                ImGuiComponents.HelpMarker("只在「解除限制」生效時(隨機任務/討伐戰/大型任務)才有作用。\n" +
                                           "開啟後交戰不會停在原地等打完,而是繼續走到下一個定點,技能沿路交給輪替外掛(BossMod / Wrath)自己放。\n" +
                                           "走位權會交給 vnavmesh 獨佔,BossMod 的自動移動在交戰期間會被關掉,離開戰鬥再還原。\n\n" +
                                           "頭目戰、以及需要確實清怪才能推進的關卡不建議開。");


                ImGui.BeginListBox("##W2WConfig", new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X, 300));
                JobWithRoleHelper.DrawCategory(JobWithRole.All, ref Configuration.W2WJobs);
                ImGui.EndListBox();
            }

            if (ImGui.Checkbox("Override Party Validation".Loc(), ref Configuration.OverridePartyValidation))
                Configuration.Save();
            ImGuiComponents.HelpMarker("AutoDuty will ignore your party makeup when queueing for duties\nThis is for Multi-Boxing Only\n*AutoDuty is not recommended to be used with other players*".Loc());


            ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f));
            var advModeHeader = ImGui.Selectable("Advanced Config Options".Loc(), advModeHeaderSelected, ImGuiSelectableFlags.DontClosePopups);
            ImGui.PopStyleVar();
            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (advModeHeader)
                advModeHeaderSelected = !advModeHeaderSelected;

            if (advModeHeaderSelected == true)
            {
                if (ImGui.Checkbox("Using Alternative Rotation Plugin".Loc(), ref Configuration.UsingAlternativeRotationPlugin))
                    Configuration.Save();
                ImGuiComponents.HelpMarker("You are deciding to use a plugin other than Wrath Combo, Rotation Solver or BossMod AutoRotation.".Loc());

                if (ImGui.Checkbox("Using Alternative Movement Plugin".Loc(), ref Configuration.UsingAlternativeMovementPlugin))
                    Configuration.Save();
                ImGuiComponents.HelpMarker("You are deciding to use a plugin other than Vnavmesh.".Loc());

                if (ImGui.Checkbox("Using Alternative Boss Plugin".Loc(), ref Configuration.UsingAlternativeBossPlugin))
                    Configuration.Save();
                ImGuiComponents.HelpMarker("You are deciding to use a plugin other than BossMod/BMR.".Loc());
            }
        }

        //Start of Pre-Loop Settings
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f));
        var preLoopHeader = ImGui.Selectable("Pre-Loop Initialization Settings".Loc(), preLoopHeaderSelected, ImGuiSelectableFlags.DontClosePopups);
        ImGui.PopStyleVar();
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (preLoopHeader)
            preLoopHeaderSelected = !preLoopHeaderSelected;

        if (preLoopHeaderSelected == true)
        {
            if (ImGui.Checkbox("Enable".Loc() + "###PreLoopEnable", ref Configuration.EnablePreLoopActions))
                Configuration.Save();

            using (ImRaii.Disabled(!Configuration.EnablePreLoopActions))
            {
                ImGui.Separator();
                MakeCommands("Execute commands on start of all loops",
                             ref Configuration.ExecuteCommandsPreLoop, ref Configuration.CustomCommandsPreLoop, ref preLoopCommand);

                ImGui.Separator();

                ImGui.TextColored(ImGuiHelper.VersionColor,
                                  "The following are also done between loop, if Between Loop is enabled (currently ??)".Loc(Configuration.EnableBetweenLoopActions ? "enabled".Loc() : "disabled".Loc()));

                if (ImGui.Checkbox("Retire To ".Loc(), ref Configuration.RetireMode))
                    Configuration.Save();

                using (ImRaii.Disabled(!Configuration.RetireMode))
                {
                    ImGui.SameLine(0, 5);
                    ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                    if (ImGui.BeginCombo("##RetireLocation", Configuration.RetireLocationEnum.ToCustomString()))
                    {
                        foreach (RetireLocation retireLocation in Enum.GetValues(typeof(RetireLocation)))
                        {
                            if (ImGui.Selectable(retireLocation.ToCustomString()))
                            {
                                Configuration.RetireLocationEnum = retireLocation;
                                Configuration.Save();
                            }
                        }

                        ImGui.EndCombo();
                    }

                    if (Configuration is { RetireMode: true, RetireLocationEnum: RetireLocation.Personal_Home })
                    {
                        if (ImGui.Button("Add Current Position".Loc()))
                        {
                            Configuration.PersonalHomeEntrancePath.Add(Player.Position);
                            Configuration.Save();
                        }

                        ImGuiComponents
                           .HelpMarker("For most houses where the door is a straight shot from teleport location this is not needed, in the rare situations where the door needs a path to get to it, you can create that path here, or if your door seems to be further away from the teleport location than your neighbors, simply goto your door and hit Add Current Position".Loc());

                        using (ImRaii.ListBox("##PersonalHomeVector3List", new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X,
                                                                                                       (ImGui.GetTextLineHeightWithSpacing() * Configuration.PersonalHomeEntrancePath.Count) + 5)))
                        {
                            var removeItem = false;
                            var removeAt   = 0;

                            foreach (var item in Configuration.PersonalHomeEntrancePath.Select((Value, Index) => (Value, Index)))
                            {
                                ImGui.Selectable($"{item.Value}");
                                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                                {
                                    removeItem = true;
                                    removeAt   = item.Index;
                                }
                            }

                            if (removeItem)
                            {
                                Configuration.PersonalHomeEntrancePath.RemoveAt(removeAt);
                                Configuration.Save();
                            }
                        }
                    }

                    if (Configuration is { RetireMode: true, RetireLocationEnum: RetireLocation.FC_Estate })
                    {
                        if (ImGui.Button("Add Current Position".Loc()))
                        {
                            Configuration.FCEstateEntrancePath.Add(Player.Position);
                            Configuration.Save();
                        }

                        ImGuiComponents
                           .HelpMarker("For most houses where the door is a straight shot from teleport location this is not needed, in the rare situations where the door needs a path to get to it, you can create that path here, or if your door seems to be further away from the teleport location than your neighbors, simply goto your door and hit Add Current Position".Loc());

                        using (ImRaii.ListBox("##FCEstateVector3List", new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X,
                                                                                                   (ImGui.GetTextLineHeightWithSpacing() * Configuration.FCEstateEntrancePath.Count) + 5)))
                        {
                            var removeItem = false;
                            var removeAt   = 0;

                            foreach (var item in Configuration.FCEstateEntrancePath.Select((Value, Index) => (Value, Index)))
                            {
                                ImGui.Selectable($"{item.Value}");
                                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                                {
                                    removeItem = true;
                                    removeAt   = item.Index;
                                }
                            }

                            if (removeItem)
                            {
                                Configuration.FCEstateEntrancePath.RemoveAt(removeAt);
                                Configuration.Save();
                            }
                        }
                    }
                }

                if (ImGui.Checkbox("Auto Equip Recommended Gear".Loc(), ref Configuration.AutoEquipRecommendedGear))
                    Configuration.Save();

                ImGuiComponents.HelpMarker("Uses Gear from Armory Chest Only".Loc());


                if (Configuration.AutoEquipRecommendedGear)
                {
                    ImGui.Indent();
                    using (ImRaii.Disabled(!Gearsetter_IPCSubscriber.IsEnabled))
                    {
                        if (ImGui.Checkbox("Consider items outside of armoury chest".Loc(), ref Configuration.AutoEquipRecommendedGearGearsetter))
                            Configuration.Save();

                        if (Configuration.AutoEquipRecommendedGearGearsetter)
                        {
                            ImGui.Indent();
                            if (ImGui.Checkbox("Move old items to inventory".Loc(), ref Configuration.AutoEquipRecommendedGearGearsetterOldToInventory))
                                Configuration.Save();
                            ImGuiComponents.HelpMarker("Except for weapons, this will move the gear to be replaced to the inventory.".Loc());
                            ImGui.Unindent();
                        }
                    }

                    if (!Gearsetter_IPCSubscriber.IsEnabled)
                    {
                        if (Configuration.AutoEquipRecommendedGearGearsetter)
                        {
                            Configuration.AutoEquipRecommendedGearGearsetter = false;
                            Configuration.Save();
                        }

                        ImGui.Text("* Items outside the armoury chest requires Gearsetter plugin".Loc());
                        ImGui.Text("Get @ ".Loc());
                        ImGui.SameLine(0, 0);
                        ImGuiEx.TextCopy(ImGuiHelper.LinkColor, @"https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json");
                    }

                    ImGui.Unindent();
                }

                if (ImGui.Checkbox("Auto Repair".Loc(), ref Configuration.AutoRepair))
                    Configuration.Save();

                if (Configuration.AutoRepair)
                {
                    ImGui.SameLine();

                    if (ImGui.RadioButton("Self".Loc(), Configuration.AutoRepairSelf))
                    {
                        Configuration.AutoRepairSelf = true;
                        Configuration.Save();
                    }

                    ImGui.SameLine();
                    ImGuiComponents.HelpMarker("Will use DarkMatter to Self Repair (Requires Leveled Crafters!)".Loc());
                    ImGui.SameLine();

                    if (ImGui.RadioButton("CityNpc".Loc(), !Configuration.AutoRepairSelf))
                    {
                        Configuration.AutoRepairSelf = false;
                        Configuration.Save();
                    }

                    ImGui.SameLine();
                    ImGuiComponents.HelpMarker("Will use preferred repair npc to repair.".Loc());
                    ImGui.Indent();
                    ImGui.Text("Trigger @".Loc());
                    ImGui.SameLine();
                    ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                    int autoRepairPct = (int)Configuration.AutoRepairPct;
                    if (ImGui.SliderInt("##Repair@", ref autoRepairPct, 0, 99, "%d%%"))
                        Configuration.AutoRepairPct = Math.Clamp((uint)autoRepairPct, 0, 99);
                    if (ImGui.IsItemDeactivatedAfterEdit())
                        Configuration.Save();

                    ImGui.PopItemWidth();
                    if (!Configuration.AutoRepairSelf)
                    {
                        ImGui.Text("Preferred Repair NPC: ".Loc());
                        ImGuiComponents.HelpMarker("It's a good idea to match the Repair NPC with Summoning Bell and if possible Retire Location".Loc());
                        ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                        if (ImGui.BeginCombo("##PreferredRepair",
                                             Configuration.PreferredRepairNPC != null ?
                                                 $"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Configuration.PreferredRepairNPC.Name.ToLowerInvariant())} ({Svc.Data.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(Configuration.PreferredRepairNPC.TerritoryType)?.PlaceName.ValueNullable?.Name.ToString()})  ({MapHelper.ConvertWorldXZToMap(Configuration.PreferredRepairNPC.Position.ToVector2(), Svc.Data.GetExcelSheet<TerritoryType>().GetRow(Configuration.PreferredRepairNPC.TerritoryType).Map.Value!).X.ToString("0.0", CultureInfo.InvariantCulture)}, {MapHelper.ConvertWorldXZToMap(Configuration.PreferredRepairNPC.Position.ToVector2(), Svc.Data.GetExcelSheet<TerritoryType>().GetRow(Configuration.PreferredRepairNPC.TerritoryType).Map.Value).Y.ToString("0.0", CultureInfo.InvariantCulture)})" :
                                                 "Grand Company Inn".Loc()))
                        {
                            if (ImGui.Selectable("Grand Company Inn".Loc()))
                            {
                                Configuration.PreferredRepairNPC = null;
                                Configuration.Save();
                            }

                            foreach (RepairNpcData repairNPC in RepairNPCs)
                            {
                                if (repairNPC.TerritoryType <= 0)
                                {
                                    ImGui.Text(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(repairNPC.Name.ToLowerInvariant()));
                                    continue;
                                }

                                var territoryType = Svc.Data.GetExcelSheet<TerritoryType>()?.GetRow(repairNPC.TerritoryType);

                                if (territoryType == null) continue;

                                if
                                    (ImGui.Selectable($"{CultureInfo.InvariantCulture.TextInfo.ToTitleCase(repairNPC.Name.ToLowerInvariant())} ({territoryType.Value.PlaceName.ValueNullable?.Name.ToString()})  ({MapHelper.ConvertWorldXZToMap(repairNPC.Position.ToVector2(), territoryType.Value.Map.Value!).X.ToString("0.0", CultureInfo.InvariantCulture)}, {MapHelper.ConvertWorldXZToMap(repairNPC.Position.ToVector2(), territoryType.Value.Map.Value!).Y.ToString("0.0", CultureInfo.InvariantCulture)})"))
                                {
                                    Configuration.PreferredRepairNPC = repairNPC;
                                    Configuration.Save();
                                }
                            }

                            ImGui.EndCombo();
                        }

                        ImGui.PopItemWidth();
                    }

                    ImGui.Unindent();
                }

                if (ImGui.Checkbox("Auto Consume".Loc(), ref Configuration.AutoConsume))
                    Configuration.Save();

                ImGuiComponents.HelpMarker("AutoDuty will consume these items on run and between each loop (if status does not exist)".Loc());
                if (Configuration.AutoConsume)
                {
                    ImGui.SameLine();
                    ImGui.Columns(3, "##AutoConsumeColumns");
                    //ImGui.SameLine(0, 5);
                    ImGui.NextColumn();
                    if (ImGui.Checkbox("Ignore Status".Loc(), ref Configuration.AutoConsumeIgnoreStatus))
                        Configuration.Save();

                    ImGuiComponents.HelpMarker("AutoDuty will consume these items on run and between each loop every time (even if status does exists)".Loc());
                    ImGui.NextColumn();
                    //ImGui.SameLine(0, 5);

                    ImGui.PushItemWidth(80 * ImGuiHelpers.GlobalScale);

                    using (ImRaii.Disabled(Configuration.AutoConsumeIgnoreStatus))
                    {
                        if (ImGui.InputInt("Min time remaining".Loc(), ref Configuration.AutoConsumeTime))
                        {
                            Configuration.AutoConsumeTime = Math.Clamp(Configuration.AutoConsumeTime, 0, 59);
                            Configuration.Save();
                        }

                        ImGuiComponents.HelpMarker("If the status has less than this amount of time remaining (in minutes), it will consume these items".Loc());
                    }

                    ImGui.PopItemWidth();
                    ImGui.Columns(1);
                    ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 115 * ImGuiHelpers.GlobalScale);
                    if (ImGui.BeginCombo("##SelectAutoConsumeItem", consumableItemsSelectedItem.Name))
                    {
                        ImGui.InputTextWithHint("Item Name".Loc(), "Start typing item name to search".Loc(), ref consumableItemsItemNameInput, 1000);
                        foreach (var item in ConsumableItems.Where(x => x.Name.Contains(consumableItemsItemNameInput, StringComparison.InvariantCultureIgnoreCase))!)
                        {
                            if (ImGui.Selectable($"{item.Name}"))
                            {
                                consumableItemsSelectedItem = item;
                            }
                        }

                        ImGui.EndCombo();
                    }

                    ImGui.PopItemWidth();

                    ImGui.SameLine(0, 5);
                    using (ImRaii.Disabled(consumableItemsSelectedItem == null))
                    {
                        if (ImGui.Button("Add Item".Loc()))
                        {
                            if (Configuration.AutoConsumeItemsList.Any(x => x.Key == consumableItemsSelectedItem!.StatusId))
                                Configuration.AutoConsumeItemsList.RemoveAll(x => x.Key == consumableItemsSelectedItem!.StatusId);

                            Configuration.AutoConsumeItemsList.Add(new(consumableItemsSelectedItem!.StatusId, consumableItemsSelectedItem));
                            Configuration.Save();
                        }
                    }

                    using (ImRaii.ListBox("##ConsumableItemList", new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X,
                                                                                              (ImGui.GetTextLineHeightWithSpacing() * Configuration.AutoConsumeItemsList.Count) + 5)))
                    {
                        var                                  boolRemoveItem = false;
                        KeyValuePair<ushort, ConsumableItem> removeItem     = new();
                        foreach (var item in Configuration.AutoConsumeItemsList)
                        {
                            ImGui.Selectable($"{item.Value.Name}");
                            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                            {
                                boolRemoveItem = true;
                                removeItem     = item;
                            }
                        }

                        if (boolRemoveItem)
                        {
                            Configuration.AutoConsumeItemsList.Remove(removeItem);
                            Configuration.Save();
                        }
                    }
                }
            }
        }

        //Between Loop Settings
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f));
        var betweenLoopHeader = ImGui.Selectable("Between Loop Settings".Loc(), betweenLoopHeaderSelected, ImGuiSelectableFlags.DontClosePopups);
        ImGui.PopStyleVar();
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (betweenLoopHeader)
            betweenLoopHeaderSelected = !betweenLoopHeaderSelected;

        if (betweenLoopHeaderSelected == true)
        {
            ImGui.Columns(2, "##BetweenLoopHeaderColumns");

            if (ImGui.Checkbox("Enable".Loc() + "###BetweenLoopEnable", ref Configuration.EnableBetweenLoopActions))
                Configuration.Save();

            using (ImRaii.Disabled(!Configuration.EnableBetweenLoopActions))
            {
                ImGui.NextColumn();

                if (ImGui.Checkbox("Run on last Loop".Loc() + "###BetweenLoopEnableLastLoop", ref Configuration.ExecuteBetweenLoopActionLastLoop))
                    Configuration.Save();

                ImGui.Columns(1);

                ImGui.Separator();
                ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - ImGui.CalcItemWidth());
                if (ImGui.InputInt("(s) Wait time between loops".Loc(), ref Configuration.WaitTimeBeforeAfterLoopActions))
                {
                    if (Configuration.WaitTimeBeforeAfterLoopActions < 0) Configuration.WaitTimeBeforeAfterLoopActions = 0;
                    Configuration.Save();
                }
                ImGui.PopItemWidth();
                ImGuiComponents.HelpMarker("Will delay all AutoDuty between-loop Processes for X seconds.".Loc());
                ImGui.Separator();

                MakeCommands("Execute commands in between of all loops",
                             ref Configuration.ExecuteCommandsBetweenLoop,    ref Configuration.CustomCommandsBetweenLoop, ref betweenLoopCommand);

                if (ImGui.Checkbox("Auto Extract".Loc(), ref Configuration.AutoExtract))
                    Configuration.Save();

                if (Configuration.AutoExtract)
                {
                    ImGui.SameLine(0, 10);
                    if (ImGui.RadioButton("Equipped".Loc(), !Configuration.autoExtractAll))
                    {
                        Configuration.AutoExtractAll = false;
                        Configuration.Save();
                    }
                    ImGui.SameLine(0, 5);
                    if (ImGui.RadioButton("All".Loc(), Configuration.autoExtractAll))
                    {
                        Configuration.AutoExtractAll = true;
                        Configuration.Save();
                    }
                }

                if (ImGui.Checkbox("Auto open gear coffers".Loc(), ref Configuration.AutoOpenCoffers))
                    Configuration.Save();

                ImGuiComponents.HelpMarker("AutoDuty will open gear coffers (like paladin arms) between each loop".Loc());
                if (Configuration.AutoOpenCoffers)
                {
                    unsafe
                    {
                        ImGui.Indent();
                        ImGui.Text("Open Coffers with Gearset: ".Loc());
                        ImGui.AlignTextToFramePadding();
                        ImGui.SameLine();

                        // RaptureGearsetModule.Instance() 是 FFXIVClientStructs 裡手寫的取得子
                        // (`uiModule == null ? null : uiModule->GetRaptureGearsetModule()`),UIModule 尚未建立時會回 null。
                        // 原本 module 取出後從沒判過空,卻在 IsValidGearset / GetGearset / NumGearsets 四處被解參考,
                        // 而設定視窗是每幀重畫的。判空後同幀即用;為 null 時整列改顯示「尚未就緒」——
                        // 不畫成空白,也不畫成看起來合法的「目前配裝」,免得使用者誤以為設定已生效。
                        RaptureGearsetModule* module = RaptureGearsetModule.Instance();

                        if (module == null)
                        {
                            ImGui.TextDisabled("Gearset data not ready".Loc());
                        }
                        else
                        {
                            if (Configuration.AutoOpenCoffersGearset != null && !module->IsValidGearset((int) Configuration.AutoOpenCoffersGearset))
                            {
                                Configuration.AutoOpenCoffersGearset = null;
                                Configuration.Save();
                            }

                            // GetGearset() 是原生 MemberFunction,查無此 id 會回 null。
                            // 預覽字串與清單列都先判空:取不到就退回「目前配裝」(預覽)或跳過該列(清單)。
                            RaptureGearsetModule.GearsetEntry* selectedGearset =
                                Configuration.AutoOpenCoffersGearset != null ? module->GetGearset(Configuration.AutoOpenCoffersGearset.Value) : null;

                            if (ImGui.BeginCombo("##CofferGearsetSelection", selectedGearset != null ? selectedGearset->NameString : "Current Gearset".Loc()))
                            {
                                if (ImGui.Selectable("Current Gearset".Loc()))
                                {
                                    Configuration.AutoOpenCoffersGearset = null;
                                    Configuration.Save();
                                }

                                for (int i = 0; i < module->NumGearsets; i++)
                                {
                                    RaptureGearsetModule.GearsetEntry* gearset = module->GetGearset(i);
                                    if (gearset == null)
                                        continue;

                                    if(ImGui.Selectable(gearset->NameString))
                                    {
                                        Configuration.AutoOpenCoffersGearset = gearset->Id;
                                        Configuration.Save();
                                    }
                                }

                                ImGui.EndCombo();
                            }
                        }

                        if (ImGui.Checkbox("Use Blacklist".Loc(), ref Configuration.AutoOpenCoffersBlacklistUse))
                            Configuration.Save();

                        ImGuiComponents.HelpMarker("Option to disable some coffers from being opened automatically.".Loc());
                        if (Configuration.AutoOpenCoffersBlacklistUse)
                        {
                            if (ImGui.BeginCombo("Select Coffer".Loc(), autoOpenCoffersSelectedItem.Value))
                            {
                                ImGui.InputTextWithHint("Coffer Name".Loc(), "Start typing coffer name to search".Loc(), ref autoOpenCoffersNameInput, 1000);
                                foreach (var item in Items.Where(x => CofferHelper.ValidCoffer(x.Value) && x.Value.Name.ToString().Contains(autoOpenCoffersNameInput, StringComparison.InvariantCultureIgnoreCase)))
                                {
                                    if (ImGui.Selectable($"{item.Value.Name.ToString()}"))
                                        autoOpenCoffersSelectedItem = new KeyValuePair<uint, string>(item.Key, item.Value.Name.ToString());
                                }
                                ImGui.EndCombo();
                            }

                            ImGui.SameLine(0, 5);
                            using (ImRaii.Disabled(autoOpenCoffersSelectedItem.Value.IsNullOrEmpty()))
                            {
                                if (ImGui.Button("Add Coffer".Loc()))
                                {
                                    if (!Configuration.AutoOpenCoffersBlacklist.TryAdd(autoOpenCoffersSelectedItem.Key, autoOpenCoffersSelectedItem.Value))
                                    {
                                        Configuration.AutoOpenCoffersBlacklist.Remove(autoOpenCoffersSelectedItem.Key);
                                        Configuration.AutoOpenCoffersBlacklist.Add(autoOpenCoffersSelectedItem.Key, autoOpenCoffersSelectedItem.Value);
                                    }
                                    autoOpenCoffersSelectedItem = new(0, "");
                                    Configuration.Save();
                                }
                            }
                            
                            if (!ImGui.BeginListBox("##CofferBlackList", new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X, (ImGui.GetTextLineHeightWithSpacing() * Configuration.AutoOpenCoffersBlacklist.Count) + 5))) return;

                            foreach (var item in Configuration.AutoOpenCoffersBlacklist)
                            {
                                ImGui.Selectable($"{item.Value}");
                                if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                                {
                                    Configuration.AutoOpenCoffersBlacklist.Remove(item);
                                    Configuration.Save();
                                }
                            }
                            ImGui.EndListBox();
                        }
                        
                        ImGui.Unindent();
                    }
                }

                using (ImRaii.Disabled(!DiscardHelper_IPCSubscriber.IsEnabled))
                {
                    if (ImGui.Checkbox("Discard Items".Loc(), ref Configuration.DiscardItems))
                    {
                        Configuration.Save();
                    }
                }
                if (!DiscardHelper_IPCSubscriber.IsEnabled)
                {
                    if (Configuration.DiscardItems)
                    {
                        Configuration.DiscardItems = false;
                        Configuration.Save();
                    }
                    ImGui.SameLine();
                    ImGui.Text("* Discarding Items Requires DiscardHelper plugin!".Loc());
                    ImGui.SameLine();
                    ImGui.Text("Get @ ".Loc());
                    ImGui.SameLine(0, 0);
                    // 這條仍是國際服的庫，因為台服艦隊沒有 DiscardHelper 的移植版可指。
                    ImGuiEx.TextCopy(ImGuiHelper.LinkColor, @"https://puni.sh/api/repository/vera");
                }


                ImGui.Columns(2, "##DesynthColumns");

                if (ImGui.Checkbox("Auto Desynth".Loc(), ref Configuration.autoDesynth))
                {
                    Configuration.AutoDesynth = Configuration.autoDesynth;
                    Configuration.Save();
                }
                ImGui.NextColumn();
                //ImGui.SameLine(0, 5);
                using (ImRaii.Disabled(!AutoRetainer_IPCSubscriber.IsEnabled))
                {
                    if (ImGui.Checkbox("Auto GC Turnin".Loc(), ref Configuration.autoGCTurnin))
                    {
                        Configuration.AutoGCTurnin = Configuration.autoGCTurnin;
                        Configuration.Save();
                    }
                    ImGuiComponents.HelpMarker("Runs Grand Company expert delivery at the end of each loop. AutoDuty travels to your Grand Company, then AutoRetainer hands in the gear in your inventory.\n\nIt never switches character and never starts AutoRetainer's multi-character loop.\n\nHeads up: AutoRetainer runs its full Deliver Items flow, which visits the quartermaster and spends your Grand Company seals on ventures before handing anything in. To keep a reserve, set 'Seals to keep' in AutoRetainer's Grand Company exchange plan.".Loc());
                    
                    ImGui.NextColumn();

                    //slightly cursed
                    using (ImRaii.Enabled())
                    {
                        if (Configuration.AutoDesynth)
                        {
                            ImGui.Indent();
                            if (ImGui.Checkbox("Only Skill Ups".Loc(), ref Configuration.autoDesynthSkillUp))
                            {
                                Configuration.AutoDesynthSkillUp = Configuration.autoDesynthSkillUp;
                                Configuration.Save();
                            }
                            if (Configuration.AutoDesynthSkillUp)
                            {
                                ImGui.Indent();
                                ImGui.Text("Item Level Limit".Loc());
                                ImGuiComponents.HelpMarker("Stops desynthesising an item once your desynthesis skill reaches the Item Level + this limit.".Loc());
                                ImGui.SameLine();
                                ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                                if (ImGui.SliderInt("##AutoDesynthSkillUpLimit", ref Configuration.AutoDesynthSkillUpLimit, 0, 50))
                                    Configuration.AutoDesynthSkillUpLimit = Math.Clamp(Configuration.AutoDesynthSkillUpLimit, 0, 50);
                                if (ImGui.IsItemDeactivatedAfterEdit())
                                    Configuration.Save();
                                ImGui.PopItemWidth();
                                ImGui.Unindent();
                            }
                            ImGui.Unindent();
                        }
                    }

                    if (Configuration.AutoGCTurnin)
                    {
                        ImGui.NextColumn();

                        ImGui.Indent();
                        if (ImGui.Checkbox("Inventory Slots Left @".Loc(), ref Configuration.AutoGCTurninSlotsLeftBool))
                            Configuration.Save();
                        ImGui.SameLine(0);
                        using (ImRaii.Disabled(!Configuration.AutoGCTurninSlotsLeftBool))
                        {
                            ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                            if (Configuration.UseSliderInputs)
                            {
                                if (ImGui.SliderInt("##Slots", ref Configuration.AutoGCTurninSlotsLeft, 0, 140))
                                    Configuration.AutoGCTurninSlotsLeft = Math.Clamp(Configuration.AutoGCTurninSlotsLeft, 0, 140);
                                if (ImGui.IsItemDeactivatedAfterEdit())
                                    Configuration.Save();
                            }
                            else
                            {
                                Configuration.AutoGCTurninSlotsLeft = Math.Clamp(Configuration.AutoGCTurninSlotsLeft, 0, 140);

                                if (ImGui.InputInt("##Slots", ref Configuration.AutoGCTurninSlotsLeft))
                                {
                                    Configuration.AutoGCTurninSlotsLeft = Math.Clamp(Configuration.AutoGCTurninSlotsLeft, 0, 140);
                                    Configuration.Save();
                                }
                            }
                            ImGui.PopItemWidth();
                        }
                        if (ImGui.Checkbox("Use GC Aetheryte Ticket".Loc(), ref Configuration.AutoGCTurninUseTicket))
                            Configuration.Save();
                        ImGui.Unindent();
                    }
                }
                ImGui.Columns(1);

                if (!AutoRetainer_IPCSubscriber.IsEnabled)
                {
                    if (Configuration.AutoGCTurnin)
                    {
                        Configuration.AutoGCTurnin = false;
                        Configuration.Save();
                    }
                    ImGui.Text("* Auto GC Turnin Requires AutoRetainer plugin".Loc());
                    ImGui.Text("Get @ ".Loc());
                    ImGui.SameLine(0, 0);
                    ImGuiEx.TextCopy(ImGuiHelper.LinkColor, @"https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json");
                }

                if(ImGui.Checkbox("Triple Triad".Loc(), ref Configuration.TripleTriadEnabled))
                    Configuration.Save();
                ImGui.SameLine();
                ImGui.TextColored(Configuration.TripleTriadEnabled ? GradientColor.Get(ImGuiHelper.ExperimentalColor, ImGuiHelper.ExperimentalColor2, 500) : ImGuiHelper.ExperimentalColor, "EXPERIMENTAL".Loc());
                if (Configuration.TripleTriadEnabled)
                {
                    ImGui.Indent();
                    if (ImGui.Checkbox("Register Triple Triad Cards".Loc(), ref Configuration.TripleTriadRegister))
                        Configuration.Save();
                    if (ImGui.Checkbox("Sell Triple Triad Cards".Loc(), ref Configuration.TripleTriadSell))
                        Configuration.Save();
                    ImGui.Unindent();
                }

                using (ImRaii.Disabled(!AutoRetainer_IPCSubscriber.IsEnabled))
                {
                    if (ImGui.Checkbox("Enable AutoRetainer Integration".Loc(), ref Configuration.EnableAutoRetainer))
                        Configuration.Save();
                }
                if (Configuration.EnableAutoRetainer)
                {
                    ImGui.Text("Preferred Summoning Bell Location: ".Loc());
                    ImGuiComponents.HelpMarker("No matter what location is chosen, if there is a summoning bell in the location you are in when this is invoked it will go there instead".Loc());
                    if (ImGui.BeginCombo("##PreferredBell", Configuration.PreferredSummoningBellEnum.ToCustomString()))
                    {
                        foreach (SummoningBellLocations summoningBells in Enum.GetValues(typeof(SummoningBellLocations)))
                        {
                            if (ImGui.Selectable(summoningBells.ToCustomString()))
                            {
                                Configuration.PreferredSummoningBellEnum = summoningBells;
                                Configuration.Save();
                            }
                        }
                        ImGui.EndCombo();
                    }
                }
                if (!AutoRetainer_IPCSubscriber.IsEnabled)
                {
                    if (Configuration.EnableAutoRetainer)
                    {
                        Configuration.EnableAutoRetainer = false;
                        Configuration.Save();
                    }
                    ImGui.Text("* AutoRetainer requires a plugin".Loc());
                    ImGui.Text("Visit ".Loc());
                    ImGui.SameLine(0, 0);
                    ImGuiEx.TextCopy(ImGuiHelper.LinkColor, @"https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json");
                }
            }
        }

        //Loop Termination Settings
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.PushStyleVar(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f));
        var terminationHeader = ImGui.Selectable("Loop Termination Settings".Loc(), terminationHeaderSelected, ImGuiSelectableFlags.DontClosePopups);
        ImGui.PopStyleVar();
        if (ImGui.IsItemHovered())
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (terminationHeader)
            terminationHeaderSelected = !terminationHeaderSelected;
        if (terminationHeaderSelected == true)
        {
            if (ImGui.Checkbox("Enable".Loc() + "###TerminationEnable", ref Configuration.EnableTerminationActions))
                Configuration.Save();

            // 刻意放在 ImRaii.Disabled 之外:自己停下來的通知不該被「終止動作」總開關牽連,
            // 因為失敗停止的那兩條路徑根本不經過終止動作。
            if (ImGui.Checkbox("Notify when AutoDuty stops on its own".Loc(), ref Configuration.NotifyWhenStoppedItself))
                Configuration.Save();
            ImGuiComponents.HelpMarker("Shows a Dalamud notification when AutoDuty stops because it finished all loops or hit an error. Stopping it yourself never notifies.".Loc());

            // 與上面那個勾選框刻意分開：桌面通知與語音通知是兩件事。
            if (ImGui.Checkbox("Ask Tataru to speak when AutoDuty stops on its own (requires TataruPraise)".Loc(), ref Configuration.TataruPraiseOnStoppedItself))
                Configuration.Save();
            ImGuiComponents.HelpMarker("Needs the TataruPraise plugin installed and its own master switch turned on. Without it this option does nothing at all - no error, no sound. Stopping AutoDuty yourself never speaks.".Loc());

            using (ImRaii.Disabled(!Configuration.EnableTerminationActions))
            {
                ImGui.Separator();

                if (ImGui.Checkbox("Stop Looping @ Level".Loc(), ref Configuration.StopLevel))
                    Configuration.Save();

                if (Configuration.StopLevel)
                {
                    ImGui.SameLine(0, 10);
                    ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                    if (Configuration.UseSliderInputs)
                    {
                        if (ImGui.SliderInt("##Level", ref Configuration.StopLevelInt, 1, 100))
                            Configuration.StopLevelInt = Math.Clamp(Configuration.StopLevelInt, 1, 100);
                        if (ImGui.IsItemDeactivatedAfterEdit())
                            Configuration.Save();
                    }
                    else
                    {
                        if (ImGui.InputInt("##Level", ref Configuration.StopLevelInt))
                        {
                            Configuration.StopLevelInt = Math.Clamp(Configuration.StopLevelInt, 1, 100);
                            Configuration.Save();
                        }
                    }
                    ImGui.PopItemWidth();
                }
                ImGuiComponents.HelpMarker("Looping will stop when these conditions are reached, so long as an adequate number of loops have been allocated.".Loc());
                if (ImGui.Checkbox("Stop When No Rested XP".Loc(), ref Configuration.StopNoRestedXP))
                    Configuration.Save();

                ImGuiComponents.HelpMarker("Looping will stop when these conditions are reached, so long as an adequate number of loops have been allocated.".Loc());
                if (ImGui.Checkbox("Stop Looping When Reach Item Qty".Loc(), ref Configuration.StopItemQty))
                    Configuration.Save();

                ImGuiComponents.HelpMarker("Looping will stop when these conditions are reached, so long as an adequate number of loops have been allocated.".Loc());
                if (Configuration.StopItemQty)
                {
                    ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 125 * ImGuiHelpers.GlobalScale);
                    if (ImGui.BeginCombo("Select Item".Loc(), stopItemQtySelectedItem.Value))
                    {
                        ImGui.InputTextWithHint("Item Name".Loc(), "Start typing item name to search".Loc(), ref stopItemQtyItemNameInput, 1000);
                        foreach (var item in Items.Where(x => x.Value.Name.ToString().Contains(stopItemQtyItemNameInput, StringComparison.InvariantCultureIgnoreCase))!)
                        {
                            if (ImGui.Selectable($"{item.Value.Name.ToString()}"))
                                stopItemQtySelectedItem = new KeyValuePair<uint, string>(item.Key, item.Value.Name.ToString());
                        }
                        ImGui.EndCombo();
                    }
                    ImGui.PopItemWidth();
                    ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 220 * ImGuiHelpers.GlobalScale);
                    if (ImGui.InputInt("Quantity".Loc(), ref Configuration.StopItemQtyInt))
                        Configuration.Save();

                    ImGui.SameLine(0, 5);
                    using (ImRaii.Disabled(stopItemQtySelectedItem.Value.IsNullOrEmpty()))
                    {
                        if (ImGui.Button("Add Item".Loc()))
                        {
                            if (!Configuration.StopItemQtyItemDictionary.TryAdd(stopItemQtySelectedItem.Key, new(stopItemQtySelectedItem.Value, Configuration.StopItemQtyInt)))
                            {
                                Configuration.StopItemQtyItemDictionary.Remove(stopItemQtySelectedItem.Key);
                                Configuration.StopItemQtyItemDictionary.Add(stopItemQtySelectedItem.Key, new(stopItemQtySelectedItem.Value, Configuration.StopItemQtyInt));
                            }
                            Configuration.Save();
                        }
                    }
                    ImGui.PopItemWidth();
                    if (!ImGui.BeginListBox("##ItemList", new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X, (ImGui.GetTextLineHeightWithSpacing() * Configuration.StopItemQtyItemDictionary.Count) + 5))) return;

                    foreach (var item in Configuration.StopItemQtyItemDictionary)
                    {
                        ImGui.Selectable($"{item.Value.Key} ({"Qty: ".Loc()}{item.Value.Value})");
                        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                        {
                            Configuration.StopItemQtyItemDictionary.Remove(item);
                            Configuration.Save();
                        }
                    }
                    ImGui.EndListBox();
                    if (ImGui.Checkbox("Stop Looping Only When All Items Obtained".Loc(), ref Configuration.StopItemAll))
                        Configuration.Save();
                }

                MakeCommands("Execute commands on termination of all loops",
                             ref Configuration.ExecuteCommandsTermination,  ref Configuration.CustomCommandsTermination, ref terminationCommand);

                if (ImGui.Checkbox("Play Sound on Completion of All Loops: ".Loc(), ref Configuration.PlayEndSound)) //Heavily Inspired by ChatAlerts
                        Configuration.Save();
                if (Configuration.PlayEndSound)
                {
                    if (ImGuiEx.IconButton(FontAwesomeIcon.Play, "##ConfigSoundTest", new Vector2(ImGui.GetItemRectSize().Y)))
                        SoundHelper.StartSound(Configuration.PlayEndSound, Configuration.CustomSound, Configuration.SoundEnum);
                    ImGui.SameLine();
                    DrawGameSound();
                }

                ImGui.Text("On Completion of All Loops: ".Loc());
                ImGui.SameLine(0, 10);
                ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X);
                if (ImGui.BeginCombo("##ConfigTerminationMethod", Configuration.TerminationMethodEnum.ToCustomString()))
                {
                    foreach (TerminationMode terminationMode in Enum.GetValues(typeof(TerminationMode)))
                    {
                        if (terminationMode != TerminationMode.Kill_PC || OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                            if (ImGui.Selectable(terminationMode.ToCustomString()))
                            {
                                Configuration.TerminationMethodEnum = terminationMode;
                                Configuration.Save();
                            }
                    }
                    ImGui.EndCombo();
                }

                if (Configuration.TerminationMethodEnum is TerminationMode.Kill_Client or TerminationMode.Kill_PC or TerminationMode.Logout)
                {
                    ImGui.Indent();
                    if (ImGui.Checkbox("Keep Termination option after execution ".Loc(), ref Configuration.TerminationKeepActive))
                        Configuration.Save();
                    ImGui.Unindent();
                }
            }
        }

        void MakeCommands(string checkbox, ref bool execute, ref List<string> commands, ref string curCommand)
        {
            if (ImGui.Checkbox($"{checkbox.Loc()}{(execute ? ":" : string.Empty)} ", ref execute))
                Configuration.Save();

            ImGuiComponents.HelpMarker("??.\nFor example, /echo test".Loc(checkbox));

            if (execute)
            {
                ImGui.Indent();
                ImGui.PushItemWidth(ImGui.GetContentRegionAvail().X - 185 * ImGuiHelpers.GlobalScale);
                if (ImGui.InputTextWithHint($"##Commands{checkbox}", "enter command starting with /".Loc(), ref curCommand, 500, ImGuiInputTextFlags.EnterReturnsTrue))
                {
                    if (!curCommand.IsNullOrEmpty() && curCommand[0] == '/' && (ImGui.IsKeyDown(ImGuiKey.Enter) || ImGui.IsKeyDown(ImGuiKey.KeypadEnter)))
                    {
                        Configuration.CustomCommandsPreLoop.Add(curCommand);
                        curCommand = string.Empty;
                        Configuration.Save();
                    }
                }
                ImGui.PopItemWidth();

                ImGui.SameLine(0, 5);
                using (ImRaii.Disabled(curCommand.IsNullOrEmpty() || curCommand[0] != '/'))
                {
                    if (ImGui.Button("Add Command".Loc() + $"##CommandButton{checkbox}"))
                    {
                        commands.Add(curCommand);
                        Configuration.Save();
                    }
                }
                if (!ImGui.BeginListBox($"##CommandList{checkbox}", new System.Numerics.Vector2(ImGui.GetContentRegionAvail().X, (ImGui.GetTextLineHeightWithSpacing() * commands.Count) + 5))) 
                    return;

                var removeItem = false;
                var removeAt   = 0;

                foreach (var item in commands.Select((Value, Index) => (Value, Index)))
                {
                    ImGui.Selectable($"{item.Value}##Selectable{checkbox}");
                    if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    {
                        removeItem = true;
                        removeAt   = item.Index;
                    }
                }
                if (removeItem)
                {
                    commands.RemoveAt(removeAt);
                    Configuration.Save();
                }
                ImGui.EndListBox();
                ImGui.Unindent();
            }
        }
    }

    private static void DrawGameSound()
    {
        ImGui.SameLine(0, 10);
        ImGui.PushItemWidth(150 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##ConfigEndSoundMethod", Configuration.SoundEnum.ToName()))
        {
            foreach (var sound in _validSounds)
            {
                if (ImGui.Selectable(sound.ToName()))
                {
                    Configuration.SoundEnum = sound;
                    UIGlobals.PlaySoundEffect((uint)sound);
                    Configuration.Save();
                }
            }
            ImGui.EndCombo();
        }
    }
}
