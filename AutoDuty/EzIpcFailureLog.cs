using Dalamud.Plugin.Ipc.Exceptions;
using ECommons.EzIpcManager;
using System;
using System.Collections.Generic;

namespace AutoDuty;

/// <summary>
/// 跨外掛 IPC 呼叫失敗的觀測網。
/// <para>
/// EzIPC.Init 帶 <see cref="SafeWrapper.AnyException"/> 或 <see cref="SafeWrapper.IPCException"/> 時，
/// 呼叫對方沒有註冊的 IPC 方法（或參數型別／數量不符）會被 wrapper 吞掉並直接回傳 default，
/// 而 <see cref="EzIPC.OnSafeInvocationException"/> 預設沒有任何訂閱者
/// —— 結果就是「功能完全不動、log 一行都沒有」。
/// </para>
/// <para>
/// 實例：ICE 呼叫 AutoHook.SwapBaitById，但台服的 AutoHook 版本較舊、根本沒註冊這個 IPC，
/// 症狀只是「餌永遠裝不上」，查了三輪才找到。
/// </para>
/// <para>
/// ⚠️ ECommons 是逐 repo vendored 的，每個外掛都編出自己的一份 ECommons.dll，
/// <see cref="EzIPC.OnSafeInvocationException"/> 是**那一份 DLL 裡的靜態事件**。
/// 所以每個外掛都必須自己訂閱一次，別的外掛訂閱不算數。
/// </para>
/// </summary>
internal static class EzIpcFailureLog
{
    /// <summary>同一則失敗訊息的重印間隔。IPC 若在每幀路徑上，不節流會洗爆 log。</summary>
    private const long ThrottleMs = 60000;

    /// <summary>節流表上限，避免訊息內容意外發散時無限成長。</summary>
    private const int MaxTrackedMessages = 128;

    private static readonly Dictionary<string, long> LastLogged = new();

    /// <summary>已經用 Information 印過的訊息。與 <see cref="LastLogged"/> 共用同一把鎖。</summary>
    private static readonly HashSet<string> InformedOnce = [];

    private static bool Subscribed;

    /// <summary>在外掛進入點呼叫一次。重複呼叫是安全的。</summary>
    internal static void Enable()
    {
        lock(LastLogged)
        {
            if(Subscribed) return;
            Subscribed = true;
        }
        EzIPC.OnSafeInvocationException += OnSafeInvocationException;
    }

    /// <summary>
    /// 在 Dispose 裡呼叫，而且要放在 ECommonsMain.Dispose() **之前**。重複呼叫是安全的。
    /// 不取消訂閱的話，外掛重載會讓處理常式一直累積。
    /// </summary>
    internal static void Disable()
    {
        lock(LastLogged)
        {
            if(!Subscribed) return;
            Subscribed = false;
            LastLogged.Clear();
            InformedOnce.Clear();
        }
        EzIPC.OnSafeInvocationException -= OnSafeInvocationException;
    }

    /// <summary>
    /// 手動投遞一則 IPC 失敗。
    /// <para>
    /// 給**不走 EzIPC 的**跨外掛管線用 —— 目前是 Wrath Combo：它的用戶端程式庫
    /// WrathCombo.API 有自己一套錯誤處理，不會觸發 <see cref="EzIPC.OnSafeInvocationException"/>。
    /// <c>Wrath_IPCSubscriber</c> 自己 catch 之後轉交到這裡，才能與其他 IPC 失敗走同一條
    /// 節流／格式／記錄等級的路。
    /// </para>
    /// <para>
    /// ⚠️ 與事件路徑一樣，這個方法保證不擲例外（實作整段包在 try 裡）。
    /// </para>
    /// </summary>
    internal static void Report(Exception e) => OnSafeInvocationException(e);

    private static void OnSafeInvocationException(Exception e)
    {
        // 這個處理常式是在 SafeWrapper 的 catch 區塊裡被呼叫的。
        // 它自己若丟出例外，例外會逸出 SafeWrapper 傳到呼叫端，
        // 反而破壞 SafeWrapper「絕不丟例外」的保證 —— 所以整段包起來。
        try
        {
            // Dalamud 的 IpcNotReadyError 訊息長這樣：
            //   "IPC method <Prefix>.<Method> was not registered yet"
            // 也就是訊息本身就指認得出是「對方哪個外掛的哪個 IPC 方法」；
            // 而 Svc.Log 是外掛自己的 IPluginLog，Dalamud 會自動冠上呼叫端外掛名。
            //
            // 拿這則訊息當節流鍵：不同的 IPC 失敗不會互相蓋掉，
            // 而每一種失敗的**第一次一定會印出來**。
            var detail = $"{e.GetType().Name}: {e.Message}";
            var now = Environment.TickCount64;
            bool firstForThisDetail;
            lock(LastLogged)
            {
                if(LastLogged.TryGetValue(detail, out var last) && now - last < ThrottleMs) return;
                if(LastLogged.Count >= MaxTrackedMessages)
                {
                    LastLogged.Clear();
                    InformedOnce.Clear();
                }
                LastLogged[detail] = now;
                firstForThisDetail = InformedOnce.Add(detail);
            }
            // 對方外掛沒裝時,IpcNotReadyError 每 60 秒重印一次是純噪音:第一則就說完了全部資訊。
            // 所以同一個端點只有第一則走 Information,之後降 Verbose;其他例外型別維持原樣 ——
            // 型別不符、提供端自己擲的例外才是這張觀測網的價值。
            var message =
                $"[EzIPC] 跨外掛 IPC 呼叫失敗，例外已被 SafeWrapper 吞掉並回傳 default 值：{detail}";
            if(!firstForThisDetail && e is IpcNotReadyError)
                global::ECommons.DalamudServices.Svc.Log.Verbose(message);
            else
                global::ECommons.DalamudServices.Svc.Log.Information(message);
        }
        catch
        {
            // 觀測網本身絕對不能變成新的失敗來源。
        }
    }
}
