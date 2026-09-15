using ECommons.DalamudServices;
using ECommons.EzIpcManager;
using ECommons.IPC.Subscribers;
using ECommons.IPC.Subscribers.AutoRetainer;
using ECommons.IPC.Subscribers.BossMod;
using ECommons.IPC.Subscribers.Gearsetter;
using ECommons.IPC.Subscribers.PandorasBox;
using ECommons.IPC.Subscribers.Vnavmesh;
using ECommons.IPC.Subscribers.YesAlready;
using ECommons.Reflection;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using WrathCombo.API;
using ApiConfigOption = WrathCombo.API.Enum.AutoRotationConfigOption;
using ApiDpsMode = WrathCombo.API.Enum.DPSRotationMode;
using ApiHealerMode = WrathCombo.API.Enum.HealerRotationMode;
using ApiSetResult = WrathCombo.API.Enum.SetResult;
#nullable disable

// ─────────────────────────────────────────────────────────────────────────────
// 這一層是**門面**：外部呼叫點看到的名字、參數與回傳型別與遷移前逐字相同，
// 底下的委派管線換成 ECommons.IPC 套件（以及 Wrath 的 WrathCombo.API）。
//
// 🔴 wrapper 一律用「明確傳入建構式」而不是 IPCBase.DefaultWrapper：
//    套件的 ECommonsIPC.X 是 lazy 單例，wrapper 在第一次存取當下就烘死，而我們這裡
//    有兩種語意並存（BossMod／Wrath 是 AnyException，其餘是 IPCException）。
//    自己 new 一份並把 wrapper 當建構式參數傳進去，就不必靠初始化順序，
//    也不會因為別處先碰了 ECommonsIPC.X 而被烘成別人的 wrapper。
//
// 套件給不了的成員在 IPCSubscriberSidecar.cs，分類理由寫在那個檔的檔頭。
// ─────────────────────────────────────────────────────────────────────────────

namespace AutoDuty.IPC
{
    using System.ComponentModel;
    using ECommons.GameFunctions;
    using Helpers;

    internal static class AutoRetainer_IPCSubscriber
    {
        private static readonly AutoRetainerIPC Pkg = new(SafeWrapper.IPCException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("AutoRetainer");

        internal static bool IsBusy() => Pkg.IsBusy();
        internal static bool AreAnyRetainersAvailableForCurrentChara() => Pkg.AreAnyRetainersAvailableForCurrentChara();
        internal static void AbortAllTasks() => Pkg.AbortAllTasks();
        internal static void DisableAllFunctions() => Pkg.DisableAllFunctions();
        internal static void EnableMultiMode() => Pkg.EnableMultiMode();
        internal static int GetInventoryFreeSlotCount() => Pkg.GetInventoryFreeSlotCount();
        internal static void EnqueueGCInitiation() => Pkg.EnqueueInitiation();

        /// <summary>側車：套件沒有這個端點。</summary>
        internal static Dictionary<ulong, HashSet<string>> GetEnabledRetainers() => AutoRetainerExtraIPC.GetEnabledRetainers();

        /// <summary>側車：套件的型別是 <c>Action&lt;bool, bool&gt;</c>，我方是 <c>Action&lt;Action&gt;</c>。</summary>
        internal static void EnqueueHET(Action onFailure) => AutoRetainerExtraIPC.EnqueueHET(onFailure);

        internal static void Dispose() => AutoRetainerExtraIPC.Dispose();
    }

    /// <summary>套件沒有 AutoBot 的訂閱類，整類不遷。</summary>
    internal static class AM_IPCSubscriber
    {
        private static EzIPCDisposalToken[] _disposalTokens = EzIPC.Init(typeof(AM_IPCSubscriber), "AutoBot", SafeWrapper.IPCException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("AutoBot");

        [EzIPC] internal static readonly Action Start;
        [EzIPC] internal static readonly Action Stop;
        [EzIPC] internal static readonly Func<bool> IsRunning;

        internal static void Dispose() => IPCSubscriber_Common.DisposeAll(_disposalTokens);
    }

    /// <summary>套件沒有 Marketbuddy 的訂閱類，整類不遷。</summary>
    internal static class Marketbuddy_IPCSubscriber
    {
        private static EzIPCDisposalToken[] _disposalTokens = EzIPC.Init(typeof(Marketbuddy_IPCSubscriber), "Marketbuddy", SafeWrapper.IPCException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("Marketbuddy");

        [EzIPC] internal static readonly Func<string, bool> IsLocked;
        [EzIPC] internal static readonly Func<string, bool> Lock;
        [EzIPC] internal static readonly Func<string, bool> Unlock;

        internal static void Dispose() => IPCSubscriber_Common.DisposeAll(_disposalTokens);
    }

    /// <summary>套件沒有 ARDiscard 的訂閱類，整類不遷。</summary>
    internal static class DiscardHelper_IPCSubscriber
    {
        private static EzIPCDisposalToken[] _disposalTokens = EzIPC.Init(typeof(DiscardHelper_IPCSubscriber), "ARDiscard", SafeWrapper.AnyException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("ARDiscard");

        [EzIPC("IsRunning", true)] internal static readonly Func<bool> IsRunning;

        internal static void Dispose() => IPCSubscriber_Common.DisposeAll(_disposalTokens);
    }

    /// <summary>
    /// 套件的 BossModIPC 沒有 <c>AI.*</c> 這一組端點（那是 BossModReborn 專有的），整類不遷。
    /// </summary>
    internal static class BossModReborn_IPCSubscriber
    {
        private static EzIPCDisposalToken[] _disposalTokens = EzIPC.Init(typeof(BossModReborn_IPCSubscriber), "BossMod", SafeWrapper.AnyException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("BossModReborn");

        [EzIPC("AI.GetPreset", true)] internal static readonly Func<string> Presets_GetActive;

        [EzIPC("AI.SetPreset", true)] internal static readonly Action<string> Presets_SetActive;

        internal static void Dispose() => IPCSubscriber_Common.DisposeAll(_disposalTokens);
    }


    internal static class BossMod_IPCSubscriber
    {
        private static readonly BossModIPC Pkg = new(SafeWrapper.AnyException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("BossMod") || IPCSubscriber_Common.IsReady("BossModReborn");

        internal static bool HasModuleByDataId(uint dataId) => Pkg.HasModuleByDataId(dataId);
        internal static List<string> Configuration(IReadOnlyList<string> args, bool b) => Pkg.Configuration(args, b);
        internal static string Presets_Get(string name) => Pkg.Presets_Get(name);
        internal static bool Presets_Create(string preset, bool overwrite) => Pkg.Presets_Create(preset, overwrite);
        internal static bool Presets_Delete(string name) => Pkg.Presets_Delete(name);
        internal static string Presets_GetActive() => Pkg.Presets_GetActive();
        internal static bool Presets_SetActive(string name) => Pkg.Presets_SetActive(name);
        internal static bool Presets_ClearActive() => Pkg.Presets_ClearActive();
        internal static bool Presets_GetForceDisabled() => Pkg.Presets_GetForceDisabled();
        internal static bool Presets_SetForceDisabled() => Pkg.Presets_SetForceDisabled();

        /// <summary>
        /// 套件把它宣告成自訂 delegate <c>BossModIPC.Delegates.AddTransientStrategyDelegate</c>。
        /// 舊版 ECommons 的 EzIPC 訂閱端對非泛型委派呼叫 <c>GetGenericTypeDefinition()</c> 會擲例外、
        /// 被外層 catch 吃掉，欄位永遠停在 null，所以這裡一度改走 <c>BossModExtraIPC</c> 原生側車。
        /// ECommons 4906fd97（本 repo 的子模組已 repin 到該顆）新增 <c>TryGetDelegateSignature</c> 後
        /// 已能綁上任何委派型別，側車撤除、收斂回套件實例。
        /// IPC 端點名逐字不變，仍是 <c>BossMod.Presets.AddTransientStrategy</c>。
        /// 離線驗證台：<c>C:/Users/lother/.claude/tools/fleet/ezipc_pkg_delegate_test</c>
        /// —— 對本 repo 建出的 ECommons.dll／ECommons.IPC.dll 實際完成綁定並往返四個參數，含反向校準。
        /// </summary>
        /// <remarks>string presetName, string moduleTypeName, string trackName, string value</remarks>
        internal static bool Presets_AddTransientStrategy(string presetName, string moduleTypeName, string trackName, string value) =>
            Pkg.Presets_AddTransientStrategy(presetName, moduleTypeName, trackName, value);

        /// <summary>
        /// 由 <see cref="IPCSubscriber_Common.DisposeAllSubscribers"/> 統一呼叫（其餘 <c>*_IPCSubscriber.Dispose()</c> 也一樣）。
        /// 套件實例的 IPC 成員全是訂閱端，而 EzIPC 只對提供端與事件產生 disposal token，
        /// 所以撤掉側車之後這裡本來就沒有東西要拆；全域拆除由 <c>ECommonsMain.Dispose()</c> 負責。
        /// </summary>
        internal static void Dispose() { }

        public static void AddPreset(string name, string preset)
        {
            if (Presets_Get(name) == null)
                Svc.Log.Debug($"BossMod Adding Preset: {name} {Presets_Create(preset, true)}");
        }

        public static void RefreshPreset(string name, string preset)
        {
            if (Presets_Get(name) != null)
                Presets_Delete(name);
            AddPreset(name, preset);
        }

        public static void SetPreset(string name, string preset)
        {
            if (Plugin.Configuration.AutoManageBossModAISettings)
            {
                if (Presets_GetActive() != name)
                {
                    Svc.Log.Debug($"BossMod Setting Preset: {name}");
                    AddPreset(name, preset);
                    Presets_SetActive(name);
                }
                // Presets.SetActive only assigns RotationModuleManager.Preset, which AIBehaviour
                // overwrites from AIManager.AiPreset every tick (see AIBehaviour.Execute). Without
                // also driving AI.SetPreset (-> AIManager.SetAIPreset), the AI tick loop reverts our
                // assignment on the very next frame and none of the transient movement/positional
                // strategies below ever take effect.
                // Only actually arm it while in combat: the preset's NormalMovement/StayCloseToTarget
                // modules have no combat gate (unlike GoToPositional), so activating them during plain
                // corridor navigation fights vnavmesh for movement control over an entirely separate
                // pathfinder, and neither system wins - the character just stands still. This call runs
                // on every SetPreset invocation (not just the first, guarded above), since duty-start
                // calls this before combat starts and the combat-transition call needs its own check.
                if (BossModReborn_IPCSubscriber.IsEnabled && PlayerHelper.InCombat && BossModReborn_IPCSubscriber.Presets_GetActive() != name)
                    BossModReborn_IPCSubscriber.Presets_SetActive(name);
            }
        }

        // Clears just the real AI.SetPreset arm (see SetPreset's comment) without touching the
        // generic Presets.SetActive state - call this once combat/an action finishes and control
        // is handing back to vnavmesh for plain navigation, so NormalMovement/StayCloseToTarget
        // stop fighting it again on the next corridor stretch.
        public static void DisableRealAIPreset()
        {
            if (Plugin.Configuration.AutoManageBossModAISettings && BossModReborn_IPCSubscriber.IsEnabled)
                BossModReborn_IPCSubscriber.Presets_SetActive("");
        }

        public static void DisablePresets()
        {
            if (Plugin.Configuration.AutoManageBossModAISettings)
            {
                if (Presets_GetActive() != null)
                {
                    Svc.Log.Debug($"BossMod Disabling Presets");
                    Presets_ClearActive();
                }
                if (BossModReborn_IPCSubscriber.IsEnabled)
                    BossModReborn_IPCSubscriber.Presets_SetActive("");
            }
        }

        /// <summary>
        /// 上一次已經輸出過的 Range 診斷指紋（值 ＋ 兩個 preset 的接受結果）。
        /// </summary>
        /// <remarks>
        /// SetRange 在戰鬥中是每幀等級的呼叫（AutoDuty.cs 依周圍敵數在兩個距離之間切換），
        /// 所以 Information 只在「值或結果變了」時輸出。<b>送出本身不受影響</b>，照舊每次都送。
        /// </remarks>
        private static string _lastRangeReport = "";

        public static void SetRange(float range)
        {
            if (Plugin.Configuration.AutoManageBossModAISettings)
            {
                Svc.Log.Debug($"BossMod Setting Range to: {range}");

                string value = MathF.Round(range, 1).ToString(CultureInfo.InvariantCulture);

                // 🔴 軌道的 InternalName 是 "Range"（大寫 R），不是 "range"。
                //    BMR 的 StayCloseToTarget 用 def.DefineFloat(Tracks.Range, ...)，而 DefineFloat
                //    直接拿列舉成員名當 InternalName（RotationModule.cs）；提供端的
                //    IPCProvider.addTransientStrategy 再用 ordinal == 去 FindIndex 找那條軌道。
                //    大小寫不符 ⇒ 找不到軌道 ⇒ 回 false 而且**完全靜默**，整筆設定被丟掉，
                //    StayCloseToTarget 就一直停在預設值 0（＝哨兵值「貼著受擊框 ±1」），
                //    使用者在設定裡調的「與目標最大距離」從來沒有生效過。
                bool active  = Presets_AddTransientStrategy("AutoDuty",         "BossMod.Autorotation.MiscAI.StayCloseToTarget", "Range", value);
                bool passive = Presets_AddTransientStrategy("AutoDuty Passive", "BossMod.Autorotation.MiscAI.StayCloseToTarget", "Range", value);

                string report = $"{value}|{active}|{passive}";
                if (report != _lastRangeReport)
                {
                    _lastRangeReport = report;

                    if (!IsEnabled)
                        Svc.Log.Information($"BMR StayCloseToTarget Range={value}：BossMod／BossModReborn 沒有啟用，這次沒有送出。");
                    else if (active || passive)
                        Svc.Log.Information($"BMR StayCloseToTarget Range={value} 已送出（AutoDuty={active}、AutoDuty Passive={passive}）。");
                    else
                        Svc.Log.Information($"BMR StayCloseToTarget Range={value} 沒有生效：兩個 preset 都不接受這條軌道（軌道名或模組不存在）。");
                }
            }
        }

        public enum DestinationStrategy { None, Pathfind, Explicit }

        public static void SetMovement(bool on)
        {
            if (Plugin.Configuration.AutoManageBossModAISettings)
            {
                Svc.Log.Debug($"BossMod Setting Movement: {on}");

                string destinationStrategy = (on ? DestinationStrategy.Pathfind : DestinationStrategy.None).ToString();

                Presets_AddTransientStrategy("AutoDuty",         "BossMod.Autorotation.MiscAI.NormalMovement", "Destination", destinationStrategy);
                Presets_AddTransientStrategy("AutoDuty Passive", "BossMod.Autorotation.MiscAI.NormalMovement", "Destination", destinationStrategy);
            }
        }

        /// <summary>上一次真的送出去的 Role 值,避免每幀重送同一個值洗 IPC 與 log。</summary>
        private static string _lastPartyRoleSent = string.Empty;

        /// <summary>快取歸零。切換副本／停止時呼叫,免得跨副本沿用上一場的判斷。</summary>
        public static void ResetStayCloseToTankCache() => _lastPartyRoleSent = string.Empty;

        /// <summary>
        /// 送出 BossMod MiscAI 的 StayCloseToPartyRole「跟著坦克走」軌道。
        /// </summary>
        /// <remarks>
        /// 🔴 <c>Configuration.PartyCoherency</c> 關著時一律送 <c>None</c>(＝BossMod 該軌道的預設值),
        /// 所以「功能沒開」與「沒有這個功能」在 BossMod 端是同一個狀態,而且使用者中途關掉時
        /// 會自己歸位,不會留下上一次設好的 Tank。
        /// ⚠️ 軌道名 <c>Role</c> 是大寫 R(BossModReborn 的 <c>Tracks.Role</c> 直接拿列舉成員名當
        /// InternalName),大小寫不符會靜默回 false、整筆被丟掉 —— 與 SetRange 那條同一個坑。
        /// </remarks>
        public static void StayCloseToTank(bool close)
        {
            if (!Plugin.Configuration.AutoManageBossModAISettings)
                return;

            string role = close && Plugin.Configuration.PartyCoherency ? nameof(Data.Enums.Role.Tank) : "None";

            if (role == _lastPartyRoleSent)
                return;

            bool active  = Presets_AddTransientStrategy("AutoDuty",         "BossMod.Autorotation.MiscAI.StayCloseToPartyRole", "Role", role);
            bool passive = Presets_AddTransientStrategy("AutoDuty Passive", "BossMod.Autorotation.MiscAI.StayCloseToPartyRole", "Role", role);

            _lastPartyRoleSent = role;

            if (!IsEnabled)
                Svc.Log.Information($"BMR StayCloseToPartyRole Role={role}:BossMod／BossModReborn 沒有啟用,這次沒有送出。");
            else if (active || passive)
                Svc.Log.Information($"BMR StayCloseToPartyRole Role={role} 已送出(AutoDuty={active}、AutoDuty Passive={passive})。");
            else
                Svc.Log.Information($"BMR StayCloseToPartyRole Role={role} 沒有生效:兩個 preset 都不接受這條軌道(軌道名或模組不存在)。");
        }

        public static void SetPositional(Positional positional)
        {
            if (Plugin.Configuration.AutoManageBossModAISettings)
            {
                Svc.Log.Debug($"BossMod Setting Positional: {positional}");

                Presets_AddTransientStrategy("AutoDuty Passive", "BossMod.Autorotation.MiscAI.GoToPositional", "Positional", positional.ToString());
            }
        }
    }


    /// <summary>
    /// YesAlready 門面。壓制改走<b>具名租約</b>，提供端給不了時退回舊的開關寫入。
    /// </summary>
    /// <remarks>
    /// 🔴🔴 <b>改用租約的理由＝舊的開關沒有主人。</b><c>SetPluginEnabled</c> 寫的是單一格全域
    /// 布林 <c>C.Enabled</c>，而 Questionable／SomethingNeedDoing 也寫同一格：AutoDuty 跑本
    /// 副本時巨集碰一下那個開關，離開副本時我們又無條件寫回 <see langword="true"/> ——
    /// 結果不是「整趟 YesAlready 一直開著搶按窗」就是「別人的壓制被我們掀掉」。<b>全程零訊息。</b>
    /// <para>
    /// 🔑 租約是<b>記名</b>的 refcount：我們只放開自己那一把，也完全不碰使用者的開關。
    /// 長時間的多輪本要靠 <see cref="Tick"/> 續約（提供端上限 5 分鐘）。
    /// </para>
    /// <para>
    /// ⚠️ <b>呼叫點的既有閘門原樣保留</b>：<c>AutoDuty.SetGeneralSettings</c> 仍然只在
    /// <c>_settingsActive.HasFlag(SettingsActive.YesAlready)</c> 時才呼叫 <see cref="SetState"/>，
    /// 那條判斷（含 <c>GetGeneralSettings</c> 裡重複寫了兩次 <c>IsEnabled</c> 的既有寫法）
    /// <b>不在本次改動範圍內</b>，沒有動。
    /// </para>
    /// </remarks>
    internal static class YesAlready_IPCSubscriber
    {
        /// <summary>租約登記的名字，會出現在 YesAlready 的 log 與設定視窗。</summary>
        private const string LeaseOwner = "AutoDuty";

        /// <summary>每次取得／續約要求的租期（5 分鐘）＝提供端的硬性上限。</summary>
        /// <remarks>
        /// 🔑 全艦隊的壓制租約時間政策統一成「租 5 分鐘、每 30 秒續約」（AutoRetainer 那套
        /// 本來就是這個值）。取捨是：租期短 ⇒ 我們當掉或被卸載時，使用者最多等 5 分鐘
        /// YesAlready 就自己恢復；續約間隔留 10 倍餘裕 ⇒ 要連續漏掉 9 次心跳才會真的過期。
        /// <para>
        /// 🔴 這個值<b>不可以</b>大於提供端的上限：提供端是<b>夾值不是拒絕</b>，要多了只會
        /// 被靜默砍短，續約反而會來不及。
        /// </para>
        /// </remarks>
        private const int LeaseMilliseconds = 300_000;

        /// <summary>續約間隔（30 秒），是 <see cref="LeaseMilliseconds"/> 的十分之一。</summary>
        private const int RenewIntervalMilliseconds = 30_000;

        private static readonly YesAlreadyIPC Pkg = new(SafeWrapper.IPCException);

        /// <summary>我們<b>認為</b>現在應該壓著（由 <see cref="SetState"/> 驅動）。</summary>
        private static bool _suppressing;

        /// <summary>目前持有的租約；<see cref="Guid.Empty"/>＝沒有。</summary>
        private static Guid _lease;

        /// <summary>
        /// 有沒有走 fail-safe 舊路徑（真的寫過 <c>SetPluginEnabled(false)</c>）。
        /// </summary>
        /// <remarks>
        /// 🔴 這個旗標決定解除時<b>要不要把使用者的開關寫回去</b>。混用兩條路（先舊路徑壓下去、
        /// 後來又升級成租約）會讓解除時只放開租約而忘了寫回開關 ⇒ 使用者的 YesAlready
        /// <b>永遠關著</b>。所以一旦走了舊路徑就<b>不再升級</b>成租約。
        /// </remarks>
        private static bool _legacyEngaged;

        /// <summary><see cref="Environment.TickCount64"/> 座標系的下次續約時刻。</summary>
        private static long _nextRenewAt;

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("YesAlready");

        public static bool IsPluginEnabled() => Pkg.IsPluginEnabled();

        internal static void Dispose() => YesAlreadyExtraIPC.Dispose();

        /// <summary>
        /// <paramref name="on"/>＝<see langword="false"/> 請 YesAlready 讓開；
        /// <see langword="true"/> 解除。冪等。
        /// </summary>
        public static void SetState(bool on)
        {
            if (!on)
            {
                if (_suppressing)
                    return;

                _suppressing = true;

                if (TryAcquireLease())
                    return;

                // ── fail-safe：提供端沒裝、或版本太舊沒有租約端點 ⇒ 退回改動前的寫法 ──
                Svc.Log.Information("[AutoDuty] YesAlready 沒有壓制租約端點（沒安裝或版本太舊），退回舊的開關寫入");
                _legacyEngaged = true;
                Pkg.SetPluginEnabled(false);
                return;
            }

            _suppressing = false;
            ReleaseLease();

            // 當初是走舊路徑壓下去的，還原也要走舊路徑，否則使用者的開關會永遠關著。
            if (_legacyEngaged)
            {
                _legacyEngaged = false;
                Pkg.SetPluginEnabled(true);
            }
        }

        /// <summary>
        /// 續約心跳。由 <c>AutoDuty.Framework_Update</c> 每幀呼叫，內部自行節流。
        /// </summary>
        /// <remarks>
        /// 🔴 一輪多本可以跑好幾個小時，而租約上限只有 5 分鐘 ⇒ 不續約的話 YesAlready
        /// 會在副本跑到一半自己醒過來搶按窗。續約回 <see langword="false"/> 代表那把已經
        /// 不在了，必須<b>重新取得</b>，不能繼續假設自己還壓著。
        /// </remarks>
        internal static void Tick()
        {
            // 沒在壓、或走的是舊路徑（舊路徑沒有到期時間，不需要心跳）。
            if (!_suppressing || _legacyEngaged || _lease == Guid.Empty)
                return;

            if (Environment.TickCount64 < _nextRenewAt)
                return;

            _nextRenewAt = Environment.TickCount64 + RenewIntervalMilliseconds;

            bool renewed;
            try
            {
                renewed = YesAlreadyExtraIPC.RenewSuppressionFor(_lease, LeaseMilliseconds);
            }
            catch
            {
                // SafeWrapper 只吃 IpcNotReadyError；別的例外不能讓它打斷 Framework.Update。
                renewed = false;
            }

            if (renewed)
                return;

            Svc.Log.Information($"[AutoDuty] YesAlready 壓制租約 {_lease} 已經不在了，重新取得一把");
            _lease = Guid.Empty;

            if (TryAcquireLease())
                return;

            // 完全拿不到（YesAlready 被卸載或重載過？）：退回舊路徑以維持壓制，
            // 解除時 _legacyEngaged 會負責把開關寫回去。
            Svc.Log.Information("[AutoDuty] 重新取得 YesAlready 壓制租約失敗，退回舊的開關寫入");
            _legacyEngaged = true;
            Pkg.SetPluginEnabled(false);
        }

        /// <summary>取一把租約。回 <see langword="false"/>＝提供端給不了。</summary>
        private static bool TryAcquireLease()
        {
            Guid lease;
            try
            {
                lease = YesAlreadyExtraIPC.AcquireSuppressionFor(LeaseOwner, LeaseMilliseconds);
            }
            catch
            {
                lease = Guid.Empty;
            }

            if (lease == Guid.Empty)
                return false;

            _lease = lease;
            _nextRenewAt = Environment.TickCount64 + RenewIntervalMilliseconds;
            Svc.Log.Information($"[AutoDuty] 已向 YesAlready 取得壓制租約 {lease}（{LeaseMilliseconds} 毫秒）");
            return true;
        }

        /// <summary>交回租約（沒有就什麼都不做）。冪等。</summary>
        private static void ReleaseLease()
        {
            if (_lease == Guid.Empty)
                return;

            var lease = _lease;

            // 🔴 先清欄位再送出：送出途中擲例外的話手上這把也已經是廢的了。
            _lease = Guid.Empty;

            try
            {
                YesAlreadyExtraIPC.ReleaseSuppression(lease);
            }
            catch
            {
                // 交不回去也不要緊：提供端會讓它自行逾時。
            }

            Svc.Log.Information($"[AutoDuty] 已交回 YesAlready 壓制租約 {lease}");
        }
    }

    internal static class Gearsetter_IPCSubscriber
    {
        private static readonly GearsetterIPC Pkg = new(SafeWrapper.IPCException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("Gearsetter");

        internal static List<(uint ItemId, InventoryType? SourceInventory, byte? SourceInventorySlot, RaptureGearsetModule.GearsetItemIndex TargetSlot)> GetRecommendationsForGearset(byte gearset) =>
            Pkg.GetRecommendationsForGearset(gearset);

        internal static void Dispose() { }
    }

    internal static class VNavmesh_IPCSubscriber
    {
        private static readonly VnavmeshIPC Pkg = new(SafeWrapper.IPCException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("vnavmesh");

        /// <summary>
        /// 綁定可見性。這三支是 AutoDuty 的移動核心,EzIPC 綁不上時欄位停在 null、
        /// 呼叫時擲 NullReferenceException —— 使用者看到的是「角色不會動」,很難自己連到 IPC。
        /// 所以在建構完成後就檢查一次(不等到第一次呼叫),沒綁上就寫一行 Information 供回報。
        /// <para>
        /// 🔴 靜態建構式擲例外會讓整個型別終身不可用(TypeInitializationException),
        /// 所以整段包 try/catch —— 診斷本身絕不能變成新的故障源。
        /// </para>
        /// <para>
        /// 📌 C# 的靜態欄位初始設定式在靜態建構式本體之前依序執行,所以跑到這裡時
        /// <c>Pkg</c> 已經建好、EzIPC.Init 也已經在它的建構式裡跑完了。
        /// </para>
        /// </summary>
        static VNavmesh_IPCSubscriber()
        {
            try
            {
                IPCSubscriber_Common.LogIfUnbound(Pkg.Pathfind,          "vnavmesh", "Nav.Pathfind");
                IPCSubscriber_Common.LogIfUnbound(Pkg.MoveTo,            "vnavmesh", "Path.MoveTo");
                IPCSubscriber_Common.LogIfUnbound(Pkg.PathfindAndMoveTo, "vnavmesh", "SimpleMove.PathfindAndMoveTo");
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"VNavmesh_IPCSubscriber 的 IPC 綁定檢查自己失敗了(不影響 IPC 本身): {ex}");
            }
        }

        internal static bool  Nav_IsReady()       => Pkg.IsReady();
        internal static float Nav_BuildProgress() => Pkg.BuildProgress();
        internal static bool  Nav_Reload()        => Pkg.Reload();
        internal static bool  Nav_Rebuild()       => Pkg.Rebuild();

        internal static void Path_Stop()                            => Pkg.Stop();
        internal static bool Path_IsRunning()                       => Pkg.IsRunning();
        internal static int  Path_NumWaypoints()                    => Pkg.NumWaypoints();
        internal static bool Path_GetMovementAllowed()              => Pkg.GetMovementAllowed();
        internal static void Path_SetMovementAllowed(bool allowed)  => Pkg.SetMovementAllowed(allowed);
        internal static bool Path_GetAlignCamera()                  => Pkg.GetAlignCamera();
        internal static void Path_SetAlignCamera(bool align)        => Pkg.SetAlignCamera(align);
        internal static float Path_GetTolerance()                   => Pkg.GetTolerance();
        internal static void Path_SetTolerance(float tolerance)     => Pkg.SetTolerance(tolerance);

        /// <summary>
        /// 請求把路徑容許值押成 <paramref name="tolerance"/>。<b>取代對 <see cref="Path_SetTolerance"/> 的直接呼叫</b>:
        /// 走 vnavmesh 的具名租約(放約／逾時自動還原,不碰使用者那格欄位),
        /// 提供端給不了租約時逐字退回 <see cref="Path_SetTolerance"/>。詳見 <see cref="VnavmeshToleranceLease"/>。
        /// </summary>
        internal static void Path_RequestTolerance(float tolerance) => VnavmeshToleranceLease.Request(tolerance);

        /// <summary>
        /// 放開路徑容許值租約;只有在真的走過舊路徑時才順帶做舊的還原。冪等。
        /// </summary>
        internal static void Path_ReleaseToleranceRequest() => VnavmeshToleranceLease.ReleaseAndRestore();

        internal static bool SimpleMove_PathfindInProgress() => Pkg.PathfindInProgress();

        // ── ECommons 4906fd97 之後才收得回來的三支 ──
        // 套件把它們宣告成自訂具名委派(VnavmeshIPC.Delegates.Pathfind / PathMoveTo /
        // PathfindAndMoveTo)。舊版 EzIPC 訂閱端對非泛型委派呼叫 GetGenericTypeDefinition()
        // 會擲例外、被外層 catch 吃掉,欄位永遠停在 null,所以這三支一度自己在側車裡
        // 用 Func<>/Action<> 重新宣告一次。4906fd97 改用 TryGetDelegateSignature 之後
        // 任何委派型別都綁得上,側車撤除、收斂回套件實例。
        // 端點名與簽名逐字不變(Nav.Pathfind / Path.MoveTo / SimpleMove.PathfindAndMoveTo),
        // SafeWrapper 也同為 IPCException,對提供端沒有任何差別。
        internal static Task<List<Vector3>> Nav_Pathfind(Vector3 from, Vector3 to, bool fly) => Pkg.Pathfind(from, to, fly);
        internal static void Path_MoveTo(List<Vector3> waypoints, bool fly) => Pkg.MoveTo(waypoints, fly);
        /// <summary>
        /// 算路徑並開始移動。<b>vnavmesh 那一側自己擲例外時回 <see langword="false"/></b>。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 🔴 <b>vnavmesh 的這支端點會擲例外，而 <c>SafeWrapper.IPCException</c> 攔不到它。</b>
        /// <c>NavmeshManager.QueryPath</c> 在 <c>_currentCTS</c> 為 null 時直接擲一個普通的
        /// <c>Exception</c>（訊息 <c>Can't initiate query - navmesh is not loaded</c>）——
        /// 那個狀態在「切圖／讀取畫面期間導航網格被卸掉」與「使用者關掉 vnavmesh 的自動載入」時都成立。
        /// Dalamud 的 <c>CallGateChannel.InvokeFunc</c> 是用 <c>Delegate.DynamicInvoke</c> 呼叫提供端的，
        /// 所以提供端擲出的東西一律被包成 <see cref="System.Reflection.TargetInvocationException"/>，
        /// 而它<b>不是</b> <c>IpcError</c> 的子型別 ⇒ 本類用的 <c>SafeWrapper.IPCException</c>
        /// （只攔 <c>IpcNotReadyError</c>）一個都攔不到。
        /// </para>
        /// <para>
        /// 補這一層之前，例外會直接逃進呼叫端。三個呼叫點裡只有 <c>MovementHelper</c> 先問過
        /// <c>Nav_IsReady()</c>；<c>AutoDuty.StageReadingPath</c>（而 <c>Framework_Update</c> 上
        /// 沒有 try/catch）與 <c>ActionsManager</c> 的 <c>KillInRange-Main</c> 任務都沒有，
        /// 表現出來是「副本跑到一半不動了、每幀噴一次例外」。
        /// </para>
        /// <para>
        /// 🔴 刻意<b>只</b>攔 <see cref="System.Reflection.TargetInvocationException"/>，不是裸
        /// <c>catch (Exception)</c> —— 本外掛自己這一側的程式錯誤（<c>NullReferenceException</c> 之類）
        /// 不會被包成這個型別，照樣往上冒。<c>IpcNotReadyError</c> 的處置也一個字都沒改
        /// （仍由 <c>SafeWrapper.IPCException</c> 吞掉並回 <see langword="false"/>）。
        /// </para>
        /// </remarks>
        internal static bool SimpleMove_PathfindAndMoveTo(Vector3 position, bool canFly)
        {
            try
            {
                return Pkg.PathfindAndMoveTo(position, canFly);
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                IPCSubscriber_Common.ReportProviderFault("vnavmesh", "SimpleMove.PathfindAndMoveTo", ex);
                return false;
            }
        }

        // ── 以下走側車，理由見 IPCSubscriberSidecar.cs ──
        internal static Task<List<Vector3>> Nav_PathfindCancelable(Vector3 from, Vector3 to, bool fly, CancellationToken token) => VNavmeshExtraIPC.Nav_PathfindCancelable(from, to, fly, token);
        internal static void Nav_PathfindCancelAll()      => VNavmeshExtraIPC.Nav_PathfindCancelAll();
        internal static bool Nav_PathfindInProgress()     => VNavmeshExtraIPC.Nav_PathfindInProgress();
        internal static int  Nav_PathfindNumQueued()      => VNavmeshExtraIPC.Nav_PathfindNumQueued();
        internal static bool Nav_IsAutoLoad()             => VNavmeshExtraIPC.Nav_IsAutoLoad();
        internal static void Nav_SetAutoLoad(bool on)     => VNavmeshExtraIPC.Nav_SetAutoLoad(on);

        internal static Vector3? Query_Mesh_NearestPoint(Vector3 p, float halfExtentXZ, float halfExtentY) => VNavmeshExtraIPC.Query_Mesh_NearestPoint(p, halfExtentXZ, halfExtentY);
        internal static Vector3? Query_Mesh_PointOnFloor(Vector3 p, bool allowUnlandable, float halfExtentXZ) => VNavmeshExtraIPC.Query_Mesh_PointOnFloor(p, allowUnlandable, halfExtentXZ);

        internal static bool Window_IsOpen()          => VNavmeshExtraIPC.Window_IsOpen();
        internal static void Window_SetOpen(bool on)  => VNavmeshExtraIPC.Window_SetOpen(on);
        internal static bool DTR_IsShown()            => VNavmeshExtraIPC.DTR_IsShown();
        internal static void DTR_SetShown(bool on)    => VNavmeshExtraIPC.DTR_SetShown(on);

        internal static void Dispose() => VNavmeshExtraIPC.Dispose();
    }

    internal static class PandorasBox_IPCSubscriber
    {
        private static readonly PandorasBoxIPC Pkg = new(SafeWrapper.IPCException);

        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("PandorasBox");

        internal static void PauseFeature(string feature, int ms)        => Pkg.PauseFeature(feature, ms);
        internal static void SetFeatureEnabled(string feature, bool on)  => Pkg.SetFeatureEnabled(feature, on);

        /// <summary>側車：套件回傳 <c>bool?</c>，我方是 <c>bool</c>。</summary>
        internal static bool GetFeatureEnabled(string feature) => PandorasBoxExtraIPC.GetFeatureEnabled(feature);

        /// <summary>側車：套件第三個參數是 <c>bool?</c>，我方是 <c>bool</c>。</summary>
        internal static void SetConfigEnabled(string feature, string config, bool on) => PandorasBoxExtraIPC.SetConfigEnabled(feature, config, on);

        internal static void Dispose() => PandorasBoxExtraIPC.Dispose();
    }

    public static class Wrath_IPCSubscriber
    {
        /// <summary>
        ///     Why a lease was cancelled.
        /// </summary>
        /// <remarks>
        ///     值與 <see cref="WrathCombo.API.Enum.CancellationReason"/> 逐一對齊；
        ///     這裡保留自己一份是因為 <see cref="CancelActions"/> 收到的是裸 int。
        /// </remarks>
        public enum CancellationReason
        {
            [Description("The Wrath user manually elected to revoke your lease.")]
            WrathUserManuallyCancelled = 0,

            [Description("Your plugin was detected as having been disabled, " +
                         "not that you're likely to see this.")]
            LeaseePluginDisabled = 1,

            [Description("The Wrath plugin is being disabled.")]
            WrathPluginDisabled = 2,

            [Description("Your lease was released by IPC call, " +
                         "theoretically this was done by you.")]
            LeaseeReleased = 3,

            [Description("IPC Services have been disabled remotely. "                 +
                         "Please see the commit history for /res/ipc_status.txt. \n " +
                         "https://github.com/PunishXIV/WrathCombo/commits/main/res/ipc_status.txt")]
            AllServicesSuspended = 4,

            [Description("Player job has been changed and leases will have to be reapplied.")]
            JobChanged = 5,
        }

        /// <summary>
        ///     The subset of <see cref="AutoRotationConfig" /> options that can be set
        ///     via IPC.
        /// </summary>
        public enum AutoRotationConfigOption
        {
            InCombatOnly         = 0, //bool
            DPSRotationMode      = 1,
            HealerRotationMode   = 2,
            FATEPriority         = 3,  //bool
            QuestPriority        = 4,  //bool
            SingleTargetHPP      = 5,  //int
            AoETargetHPP         = 6,  //int
            SingleTargetRegenHPP = 7,  //int
            ManageKardia         = 8,  //bool
            AutoRez              = 9,  //bool
            AutoRezDPSJobs       = 10, //bool
            AutoCleanse          = 11, //bool
            IncludeNPCs          = 12, //bool
            OnlyAttackInCombat   = 13, //bool
        }

        /// <remarks>
        ///     🔴 這個列舉是 <c>ConfigurationMain.Wrath_TargetingTank</c> 等設定欄位的**型別**，
        ///     換掉會動到使用者設定檔的序列化，所以維持在本類底下、不改用套件的同名列舉。
        ///     值與 <see cref="WrathCombo.API.Enum.DPSRotationMode"/> 逐一對齊。
        /// </remarks>
        public enum DPSRotationMode
        {
            Manual          = 0,
            Highest_Max     = 1,
            Lowest_Max      = 2,
            Highest_Current = 3,
            Lowest_Current  = 4,
            Tank_Target     = 5,
            Nearest         = 6,
            Furthest        = 7,
        }

        /// <summary>
        ///     The subset of <see cref="AutoRotationConfig.HealerRotationMode" /> options
        ///     that can be set via IPC.
        /// </summary>
        public enum HealerRotationMode
        {
            Manual          = 0,
            Highest_Current = 1,
            Lowest_Current  = 2
            //Self_Priority,
            //Tank_Priority,
            //Healer_Priority,
            //DPS_Priority,
        }

        public enum SetResult
        {
            [Description("A default value that shouldn't ever be seen.")]
            IGNORED = -1,

            // Success Statuses

            [Description("The configuration was set successfully.")]
            Okay = 0,

            [Description("The configuration will be set, it is working asynchronously.")]
            OkayWorking = 1,

            // Error Statuses
            [Description("IPC services are currently disabled.")]
            IPCDisabled = 10,

            [Description("Invalid lease.")]
            InvalidLease = 11,

            [Description("Blacklisted lease.")]
            BlacklistedLease = 12,

            [Description("Configuration you are trying to set is already set.")]
            Duplicate = 13,

            [Description("Player object is not available.")]
            PlayerNotAvailable = 14,

            [Description("The configuration you are trying to set is not available.")]
            InvalidConfiguration = 15,

            [Description("The value you are trying to set is invalid.")]
            InvalidValue = 16,
        }

        private static Guid? _curLease;


        internal static bool IsEnabled => IPCSubscriber_Common.IsReady("WrathCombo");

        // ── Wrath 的委派管線：WrathCombo.API（官方 IPC 用戶端程式庫），不走 EzIPC ──
        //
        // 🔴 觀測性：WrathCombo.API 有自己的一套錯誤處理，**不會**觸發
        //    EzIPC.OnSafeInvocationException，也就是不會經過 EzIpcFailureLog。
        //    若照它預設的 ErrorType.All 全部靜音，Wrath IPC 失敗會變成完全沒有 log
        //    ——那正是 EzIpcFailureLog 當初被寫出來要解決的問題。
        //    所以我們讓它照常擲例外（AutoDuty.cs 裡 Init 時不加任何 suppress），
        //    在這裡自己 catch → 交給 EzIpcFailureLog 節流印出 → 回傳與遷移前相同的 default。
        //    ⇒ 對呼叫端來說語意等同原本的 SafeWrapper.AnyException，但失敗看得見。

        private static T WrathSafe<T>(Func<T> call)
        {
            try
            {
                return call();
            }
            catch (Exception e)
            {
                EzIpcFailureLog.Report(e);
                // 與 WrathCombo.API 自己的 SafeInvokeRawMethod 同一個約定：SetResult 回 IGNORED
                // 而不是 default(=Okay)。default 會讓「呼叫根本沒送到」長得跟「設定成功」一樣。
                if (typeof(T) == typeof(ApiSetResult))
                    return (T)(object)ApiSetResult.IGNORED;
                return default;
            }
        }

        private static void WrathSafe(Action call)
        {
            try
            {
                call();
            }
            catch (Exception e)
            {
                EzIpcFailureLog.Report(e);
            }
        }

        /// <summary>
        ///     把 WrathCombo.API 的 <see cref="ApiSetResult"/> 轉回本類的 <see cref="SetResult"/>。
        ///     兩者的每一個成員值都一樣，所以是純粹的數值轉換。
        ///     ⚠️ 呼叫整個失敗時回 <see cref="SetResult.IGNORED"/>（遷移前是 <c>default</c> 也就是
        ///     <see cref="SetResult.Okay"/>）——<see cref="CheckResult"/> 對 IGNORED 已經回 false，
        ///     所以這是把「失敗被當成成功」改成「失敗被當成失敗」，只在 IPC 本來就不通時才看得出差別。
        /// </summary>
        private static SetResult FromApi(ApiSetResult result) => (SetResult)(int)result;

        /// <summary>
        ///     Get the current state of the Auto-Rotation setting in Wrath Combo.
        /// </summary>
        /// <returns>Whether Auto-Rotation is enabled or disabled</returns>
        /// <remarks>
        ///     This is only the state of Auto-Rotation, not whether any combos are
        ///     enabled in Auto-Mode.
        /// </remarks>
        internal static bool GetAutoRotationState() =>
            WrathSafe(WrathIPCWrapper.GetAutoRotationState);

        /// <summary>
        ///     Checks if the current job has a Single and Multi-Target combo configured
        ///     that are enabled in Auto-Mode.
        /// </summary>
        /// <returns>
        ///     If the user's current job is fully ready for Auto-Rotation.
        /// </returns>
        internal static bool IsCurrentJobAutoRotationReady() =>
            WrathSafe(WrathIPCWrapper.IsCurrentJobAutoRotationReady);

        /// <summary>
        ///     Get the state of Auto-Rotation Configuration in Wrath Combo.
        /// </summary>
        /// <param name="option">The option to check the value of.</param>
        /// <returns>The correctly-typed value of the configuration.</returns>
        private static object GetAutoRotationConfigState(AutoRotationConfigOption option) =>
            WrathSafe(() => WrathIPCWrapper.GetAutoRotationConfigState((ApiConfigOption)(int)option));

        private static SetResult SetAutoRotationState(Guid lease, bool enabled) =>
            FromApi(WrathSafe(() => WrathIPCWrapper.SetAutoRotationState(lease, enabled)));

        private static SetResult SetCurrentJobAutoRotationReady(Guid lease) =>
            FromApi(WrathSafe(() => WrathIPCWrapper.SetCurrentJobAutoRotationReady(lease)));

        private static SetResult SetAutoRotationConfigState(Guid lease, AutoRotationConfigOption option, bool value) =>
            FromApi(WrathSafe(() => WrathIPCWrapper.SetAutoRotationConfigState(lease, (ApiConfigOption)(int)option, value)));

        private static SetResult SetAutoRotationConfigState(Guid lease, AutoRotationConfigOption option, DPSRotationMode value) =>
            FromApi(WrathSafe(() => WrathIPCWrapper.SetAutoRotationConfigState(lease, (ApiConfigOption)(int)option, (ApiDpsMode)(int)value)));

        private static SetResult SetAutoRotationConfigState(Guid lease, AutoRotationConfigOption option, HealerRotationMode value) =>
            FromApi(WrathSafe(() => WrathIPCWrapper.SetAutoRotationConfigState(lease, (ApiConfigOption)(int)option, (ApiHealerMode)(int)value)));

        private static Guid? RegisterForLeaseWithCallback(string internalPluginName, string pluginName, string ipcPrefixForCallback) =>
            WrathSafe(() => WrathIPCWrapper.RegisterForLeaseWithCallback(internalPluginName, pluginName, ipcPrefixForCallback));

        private static void ReleaseControl(Guid lease) =>
            WrathSafe(() => WrathIPCWrapper.ReleaseControl(lease));

        public static bool DoThing(Func<SetResult> action)
        {
            SetResult result = action();
            bool      check  = result.CheckResult();
            if (!check && result == SetResult.InvalidLease)
                check = action().CheckResult();
            return check;
        }

        private static bool CheckResult(this SetResult result)
        {
            switch (result)
            {
                case SetResult.Okay:
                case SetResult.OkayWorking:
                    return true;
                case SetResult.InvalidLease:
                    _curLease = null;
                    Register();
                    return false;
                case SetResult.BlacklistedLease:
                    Plugin.Configuration.AutoManageRotationPluginState = false;
                    Plugin.Configuration.Save();
                    return false;
                case SetResult.IPCDisabled:
                case SetResult.Duplicate:
                case SetResult.PlayerNotAvailable:
                case SetResult.InvalidConfiguration:
                case SetResult.InvalidValue:
                case SetResult.IGNORED:
                    return false;
                default:
                    throw new ArgumentOutOfRangeException(nameof(result), result, null);
            }
        }

        internal static bool SetJobAutoReady() =>
            Register() && DoThing(() => SetCurrentJobAutoRotationReady(_curLease!.Value));

        internal static void SetAutoMode(bool on)
        {
            if (Register())
            {
                bool autoRotationState = DoThing(() => SetAutoRotationState(_curLease!.Value, on));
                if (autoRotationState && on)
                {
                    SetAutoRotationConfigState(_curLease.Value, AutoRotationConfigOption.InCombatOnly,       false);
                    SetAutoRotationConfigState(_curLease.Value, AutoRotationConfigOption.AutoRez,            true);
                    SetAutoRotationConfigState(_curLease.Value, AutoRotationConfigOption.AutoRezDPSJobs,     true);
                    SetAutoRotationConfigState(_curLease.Value, AutoRotationConfigOption.IncludeNPCs,        true);
                    SetAutoRotationConfigState(_curLease.Value, AutoRotationConfigOption.OnlyAttackInCombat, false);

                    DPSRotationMode dpsConfig = Plugin.CurrentPlayerItemLevelandClassJob.Value.GetCombatRole() == CombatRole.Tank ?
                                                    Plugin.Configuration.Wrath_TargetingTank :
                                                    Plugin.Configuration.Wrath_TargetingNonTank;
                    SetAutoRotationConfigState(_curLease.Value, AutoRotationConfigOption.DPSRotationMode, dpsConfig);

                    SetAutoRotationConfigState(_curLease.Value, AutoRotationConfigOption.HealerRotationMode, HealerRotationMode.Lowest_Current);

                }
            }
        }

        internal static bool Register()
        {
            if (_curLease == null)
            {
                _curLease = RegisterForLeaseWithCallback("AutoDuty", "AutoDuty", null);

                if (_curLease == null && IsEnabled)
                {
                    Plugin.Configuration.AutoManageRotationPluginState = false;
                    Plugin.Configuration.Save();
                }
            }
            return _curLease != null;
        }

        internal static void CancelActions(int reason, string s)
        {
            switch ((CancellationReason) reason)
            {
                case CancellationReason.WrathUserManuallyCancelled:
                    Plugin.Configuration.AutoManageRotationPluginState = false;
                    Plugin.Configuration.Save();
                    break;
                case CancellationReason.LeaseePluginDisabled:
                case CancellationReason.WrathPluginDisabled:
                case CancellationReason.LeaseeReleased:
                case CancellationReason.AllServicesSuspended:
                case CancellationReason.JobChanged:
                default:
                    break;
            }

            _curLease = null;
            Svc.Log.Info($"Wrath lease cancelled via {(CancellationReason) reason} for: {s}");
        }

        /// <summary>
        ///     租約我們只拿得到一個 handle，真狀態在 Wrath Combo 那一端。
        ///     這個欄位記住「已經試過釋放但沒被對方確認」的那一份，用來把重試次數限制成一次。
        /// </summary>
        private static Guid? _unconfirmedReleaseLease;

        internal static void Release()
        {
            if (!_curLease.HasValue)
            {
                _unconfirmedReleaseLease = null;
                return;
            }

            Guid lease = _curLease.Value;

            // 判準＝誰持有真狀態：租約的真狀態在 Wrath Combo 手上，我們這邊只是一個 handle。
            // 對方持有 ⇒ 確認對方放掉才放手，不能無條件把 _curLease 清成 null。
            if (!IsEnabled)
            {
                // Wrath Combo 根本沒載入，租約隨它一起消失了，沒有東西要等對方放掉。
                Svc.Log.Information($"Wrath Combo is not loaded - dropping Wrath lease {lease} handle without releasing.");
                _curLease                = null;
                _unconfirmedReleaseLease = null;
                return;
            }

            bool isRetry = _unconfirmedReleaseLease == lease;

            Svc.Log.Information(isRetry ?
                                    $"Retrying release of Wrath lease {lease}." :
                                    $"Releasing Wrath lease {lease}.");

            // ⚠️ ReleaseControl 是 void，而且失敗會被 WrathSafe 記進 log 後吞掉（見上面那段說明）：
            // 租約已失效、IPC 停用、或呼叫整個擲例外，在這裡通通長得跟成功一模一樣。
            // 成功時 Wrath Combo 會**同步**回呼 AutoDuty.WrathComboCallback → CancelActions，
            // 由它把 _curLease 清成 null —— 所以「呼叫後 _curLease 已經是 null」才是對方真的放掉的證據。
            ReleaseControl(lease);

            if (!_curLease.HasValue)
            {
                _unconfirmedReleaseLease = null;
                return;
            }

            if (isRetry)
            {
                // 重試也沒被確認就不要無限拖著：本機放手。
                // Wrath Combo 會自行清掉 leasee 外掛已不在的租約，所以這裡不會留下永久的孤兒租約。
                Svc.Log.Information($"Wrath lease {lease} release is still unconfirmed after a retry - dropping the handle locally.");
                _curLease                = null;
                _unconfirmedReleaseLease = null;
            }
            else
            {
                // 保留 handle：租約很可能還在對方那裡有效，丟掉 handle 才是真的把它變成孤兒。
                // 後續的 set 呼叫若拿到 InvalidLease，CheckResult 也會自行清掉並重新註冊。
                _unconfirmedReleaseLease = lease;
                Svc.Log.Information($"Wrath lease {lease} release was not confirmed by Wrath Combo - keeping the handle and retrying on the next release.");
            }
        }

        internal static void Dispose()
        {
            Release();
        }
    }


    internal class IPCSubscriber_Common
    {
        internal static bool IsReady(string pluginName) => DalamudReflector.TryGetDalamudPlugin(pluginName, out _, false, true);

        /// <summary>
        /// IPC 成員綁定檢查:<paramref name="member"/> 是 null 就代表 EzIPC 沒有把這個端點接上
        /// (委派型別不受支援、或參數超過 9 個),之後呼叫它會擲 NullReferenceException。
        /// <para>
        /// ⚠️ 這個檢查與「提供端有沒有安裝」無關:EzIPC 走的 GetIpcSubscriber 不看 InstalledPlugins,
        /// 沒裝那個外掛照樣綁得上(要到呼叫時才回 default)。所以這一行只會在真的接線失敗時出現,
        /// 不會對沒裝該外掛的使用者洗版。
        /// </para>
        /// <para>
        /// 📌 寫 Information 而不是 Debug:使用者的 LogLevel 是 1(Debug 其實收得到,盲區只有
        /// Verbose),但實機 log 單檔有 12~69 萬行 Debug,要使用者回報的訊息寫 Debug 會被淹沒。
        /// </para>
        /// </summary>
        internal static void LogIfUnbound(Delegate member, string pluginName, string endpoint)
        {
            if (member != null)
                return;

            Svc.Log.Information($"[IPC 綁定檢查] {pluginName} 的「{endpoint}」綁定失敗:" +
                                "EzIPC 沒有指派這個成員,呼叫它會擲 NullReferenceException。" +
                                "請連同這一行回報給開發者。");
        }

        internal static Version Version(string pluginName) => DalamudReflector.TryGetDalamudPlugin(pluginName, out var dalamudPlugin, false, true) ? dalamudPlugin.GetType().Assembly.GetName().Version : new Version(0, 0, 0, 0);

        /// <summary>同一個端點的故障訊息重印間隔（毫秒）。</summary>
        private const long ProviderFaultLogIntervalMs = 10000;

        /// <summary>節流表上限，避免端點名意外發散時無限成長。</summary>
        private const int MaxTrackedFaultKeys = 64;

        private static readonly Dictionary<string, long> ProviderFaultLogTimes = new Dictionary<string, long>();

        /// <summary>
        /// 提供端的端點實作自己擲了例外（被 <c>Delegate.DynamicInvoke</c> 包成
        /// <see cref="System.Reflection.TargetInvocationException"/>）—— 寫一行給使用者看的說明。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 🔴 寫 <c>Information</c>：這是要使用者回報的診斷。使用者的 <c>LogLevel</c> 是 1，
        /// <c>Debug</c> 其實收得到，但實機 log 單檔有數十萬行 <c>Debug</c>，寫那一級會被淹沒。
        /// </para>
        /// <para>
        /// 🔴 自帶節流字典而不是用 <c>EzThrottler</c>：後者是整個外掛共用的靜態 <c>Dictionary</c>
        /// 且零同步，並行插入弄壞的是整張表 —— 連帶弄壞外掛裡所有模組的節流。
        /// 鎖內只碰字典，不寫 log、不做 I/O、不呼叫別的外掛。
        /// </para>
        /// </remarks>
        internal static void ReportProviderFault(string pluginName, string endpoint, Exception ex)
        {
            if (!ShouldLogProviderFault(endpoint))
                return;

            Exception inner = ex.InnerException ?? ex;
            Svc.Log.Information($"[IPC] {pluginName} 的「{endpoint}」端點自己擲了例外，這一次當成「沒有開始」處理：{inner.GetType().Name}: {inner.Message}。這通常代表對方那一側現在做不到這件事（例如導航網格還沒載入）；持續出現請連同這一行回報。");
        }

        private static bool ShouldLogProviderFault(string key)
        {
            long now = Environment.TickCount64;
            lock (ProviderFaultLogTimes)
            {
                if (ProviderFaultLogTimes.TryGetValue(key, out long last) && now - last < ProviderFaultLogIntervalMs)
                    return false;
                if (ProviderFaultLogTimes.Count >= MaxTrackedFaultKeys && !ProviderFaultLogTimes.ContainsKey(key))
                    ProviderFaultLogTimes.Clear();
                ProviderFaultLogTimes[key] = now;
                return true;
            }
        }

        internal static void DisposeAll(EzIPCDisposalToken[] _disposalTokens)
        {
            foreach (var token in _disposalTokens)
            {
                try
                {
                    token.Dispose();
                }
                catch (Exception ex)
                {
                    Svc.Log.Error($"Error while unregistering IPC: {ex}");
                }
            }
        }

        /// <summary>
        /// 逐一拆掉所有 <c>*_IPCSubscriber</c>。每一步各自 try/catch —— 其中一支擲例外
        /// 不可以讓後面幾支拆不掉。
        /// <para>
        /// 🔴 必須在 <c>ECommonsMain.Dispose()</c> <b>之前</b>呼叫：拆除要用到 Svc 的服務，
        /// ECommons 收掉之後就沒有東西可以 Unregister 了。
        /// </para>
        /// <para>
        /// 📌 現況下多數 <c>Dispose()</c> 是空操作，這不是漏寫：這些類的 IPC 成員全是
        /// <b>訂閱端欄位</b>，而 <c>EzIPC.Init</c> 只對提供端與 <c>[EzIPCEvent]</c> 產生
        /// disposal token（訂閱端欄位不產生）⇒ 它們的 <c>_disposalTokens</c> 是空陣列。
        /// 真的有動作的是 <c>Wrath_IPCSubscriber</c>（交回 Wrath Combo 的租約），而那一支
        /// 在 <c>StopAndResetALL()</c> 裡已經先放過一次，這裡是冪等的第二次。
        /// </para>
        /// <para>
        /// 所以接上這條鏈<b>現在不改變任何行為</b>，是為了以後往這些類加提供端或事件時
        /// 不會靜默漏拆（在此之前這些 <c>Dispose()</c> 全 repo 零呼叫點）。
        /// </para>
        /// </summary>
        internal static void DisposeAllSubscribers()
        {
            SafeDispose(nameof(AutoRetainer_IPCSubscriber), AutoRetainer_IPCSubscriber.Dispose);
            SafeDispose(nameof(AM_IPCSubscriber), AM_IPCSubscriber.Dispose);
            SafeDispose(nameof(Marketbuddy_IPCSubscriber), Marketbuddy_IPCSubscriber.Dispose);
            SafeDispose(nameof(DiscardHelper_IPCSubscriber), DiscardHelper_IPCSubscriber.Dispose);
            SafeDispose(nameof(BossModReborn_IPCSubscriber), BossModReborn_IPCSubscriber.Dispose);
            SafeDispose(nameof(BossMod_IPCSubscriber), BossMod_IPCSubscriber.Dispose);
            SafeDispose(nameof(YesAlready_IPCSubscriber), YesAlready_IPCSubscriber.Dispose);
            SafeDispose(nameof(Gearsetter_IPCSubscriber), Gearsetter_IPCSubscriber.Dispose);
            SafeDispose(nameof(VNavmesh_IPCSubscriber), VNavmesh_IPCSubscriber.Dispose);
            SafeDispose(nameof(PandorasBox_IPCSubscriber), PandorasBox_IPCSubscriber.Dispose);
            SafeDispose(nameof(Wrath_IPCSubscriber), Wrath_IPCSubscriber.Dispose);
        }

        private static void SafeDispose(string name, Action dispose)
        {
            try
            {
                dispose();
            }
            catch (Exception ex)
            {
                Svc.Log.Error($"Error while disposing {name}: {ex}");
            }
        }
    }
}
