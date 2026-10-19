using ECommons.EzIpcManager;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
#nullable disable

namespace AutoDuty.IPC
{
    // ─────────────────────────────────────────────────────────────────────────
    // 側車：ECommons.IPC 套件**給不了**的 IPC 成員，仍然由 AutoDuty 自己宣告。
    // 每個側車類都用與對應門面類**完全相同的 prefix 與 SafeWrapper** 各自 EzIPC.Init，
    // 與套件實例並存；IPC 端點名稱逐字不變，所以對提供端來說沒有任何差別。
    //
    // 收進來的成員分三類，每一個都在宣告處標明屬於哪一類：
    //
    // (甲) 套件根本沒有的端點 —— 例如 vnavmesh 的 Nav.PathfindCancelable、Window.*、DTR.*。
    //      套件只收了上游 AutoDuty 用得到的子集，我們用得比較多。
    //
    // 🔴 (乙) 套件有、但 EzIPC 綁不上的 —— 套件把它們宣告成**自訂 delegate 型別**
    //      (VnavmeshIPC.Delegates.Pathfind 這種)。綁不上的失敗形態是**欄位停在 null**、呼叫時
    //      NullReferenceException，而且 SafeWrapper 攔不到(欄位從沒被指派，根本沒有 wrapper 可攔)。
    //      本 repo 釘的 ECommons 已有 TryGetDelegateSignature、接受任何委派型別，**這一類現在是空的**；
    //      收斂回套件實例的那幾支由 VNavmesh_IPCSubscriber 的靜態建構式檢查有沒有綁上（IPCSubscriber_Common.LogIfUnbound）。
    //
    // (丙) 套件有、但泛型參數與我方不同的 —— 例如 PandorasBox 的 GetFeatureEnabled 套件宣告成
    //      Func<string, bool?> 而我方是 Func<string, bool>。兩者跨 IPC 的轉換路徑不同
    //      (CallGateChannel.ConvertObject 只在型別不符時才走)，而提供端真正的簽名我們
    //      **離線證明不了**。故維持我方現行宣告，不賭。
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>AutoRetainer 側車。prefix 與門面類相同：<c>AutoRetainer.PluginState</c>。</summary>
    internal static class AutoRetainerExtraIPC
    {
        private static EzIPCDisposalToken[] _disposalTokens =
            EzIPC.Init(typeof(AutoRetainerExtraIPC), "AutoRetainer.PluginState", SafeWrapper.IPCException);

        /// <summary>(甲) 套件沒有這個端點。目前全 repo 沒有呼叫點，保留以免回退既有介面。</summary>
        [EzIPC] internal static readonly Func<Dictionary<ulong, HashSet<string>>> GetEnabledRetainers;

        /// <summary>
        /// (原 (丙)) 套件現在與我方同為 <c>Action&lt;Action&gt;</c>，與提供端 AutoRetainer 的
        /// <c>IPC_PluginState.EnqueueHET(Action onFailure)</c> 相符。參數個數不符時
        /// Dalamud 會丟例外，而這個 class 帶的是 SafeWrapper.IPCException，例外會被吞掉、
        /// 變成完全靜默的空操作。目前全 repo 沒有呼叫點。
        /// </summary>
        [EzIPC] internal static readonly Action<Action> EnqueueHET;

        internal static void Dispose() => IPCSubscriber_Common.DisposeAll(_disposalTokens);
    }

    /// <summary>vnavmesh 側車。prefix 與門面類相同：<c>vnavmesh</c>。</summary>
    internal static class VNavmeshExtraIPC
    {
        private static EzIPCDisposalToken[] _disposalTokens =
            EzIPC.Init(typeof(VNavmeshExtraIPC), "vnavmesh", SafeWrapper.IPCException);

        /// <summary>(甲) 套件沒有這個端點。</summary>
        [EzIPC("Nav.PathfindCancelable", true)] internal static readonly Func<Vector3, Vector3, bool, CancellationToken, Task<List<Vector3>>> Nav_PathfindCancelable;
        /// <summary>(甲) 套件沒有這個端點。</summary>
        [EzIPC("Nav.PathfindCancelAll", true)] internal static readonly Action Nav_PathfindCancelAll;
        /// <summary>(甲) 套件沒有這個端點（套件只有 SimpleMove.PathfindInProgress，是另一個 IPC 名字）。</summary>
        [EzIPC("Nav.PathfindInProgress", true)] internal static readonly Func<bool> Nav_PathfindInProgress;
        /// <summary>(甲) 套件沒有這個端點。</summary>
        [EzIPC("Nav.PathfindNumQueued", true)] internal static readonly Func<int> Nav_PathfindNumQueued;
        /// <summary>(甲) 套件沒有這個端點。</summary>
        [EzIPC("Nav.IsAutoLoad", true)] internal static readonly Func<bool> Nav_IsAutoLoad;
        /// <summary>(甲) 套件沒有這個端點。</summary>
        [EzIPC("Nav.SetAutoLoad", true)] internal static readonly Action<bool> Nav_SetAutoLoad;

        /// <summary>
        /// (原 (丙)) 提供端 <c>NavmeshQuery.FindNearestPointOnMesh</c> 回的是 <c>Vector3?</c>，這裡必須跟著是 <c>Vector3?</c>。
        /// 🔴 宣告成 <c>Vector3</c> 的後果**不是**「拿到零向量」而是 <b>NullReferenceException</b>：
        /// <c>CallGateChannel.InvokeFunc</c> 只在型別不同時才走 <c>ConvertObject</c>，而它對 null 立刻回 null
        /// ⇒ <c>return (TRet)result;</c> 對值型別拆箱 null 就擲。**有值時靜默成功，只有「查不到」那一次會炸**，
        /// 而且堆疊看起來與 IPC 無關。<c>SafeWrapper.IPCException</c> 只攔 <c>IpcNotReadyError</c>，攔不到。
        /// 📌 套件(<c>ECommons.IPC</c> 的 <c>VnavmeshIPC</c>)的形狀與此完全相同，要收斂回套件實例隨時可以。
        /// </summary>
        [EzIPC("Query.Mesh.NearestPoint", true)] internal static readonly Func<Vector3, float, float, Vector3?> Query_Mesh_NearestPoint;
        /// <summary>
        /// (原 (丙)) 同上，提供端是 <c>NavmeshQuery.FindPointOnFloor</c>，回 <c>Vector3?</c>。
        /// MapHelper 的旗標落地點查詢在用。
        /// 🔴 呼叫端**不可以**寫 <c>?? Vector3.Zero</c> 或 <c>GetValueOrDefault()</c> —— 那會把
        ///    「查不到落點」變成「落點在地圖原點」，然後真的導航走過去。
        /// </summary>
        [EzIPC("Query.Mesh.PointOnFloor", true)] internal static readonly Func<Vector3, bool, float, Vector3?> Query_Mesh_PointOnFloor;

        // -- 移動租約。(甲) 套件一支都沒有;狀態機在 VnavmeshToleranceLease.cs。 ------------------
        // 🔑 用來取代對 Path.SetTolerance 的單向寫入:Acquire 拿一把 Guid 憑證 → SetLeasedTolerance(憑證, 值) 才生效 →
        //    每 30 秒 RenewSuppressionFor(憑證) 心跳 → 做完 ReleaseSuppression(憑證)。放約或逾時(上限 5 分鐘)就自動還原成使用者的值。
        // 🔴 提供端全部回「不可為 null 的值型別」(Guid / bool),失敗回 Guid.Empty / false —— 這裡也必須宣告成不可空,否則
        //    CallGateChannel 走 JSON 來回轉換路徑。提供端沒有這幾支時 SafeWrapper 回 default ⇒ Guid.Empty,呼叫端靠這個訊號退回舊路徑。
        /// <summary>(甲) 取得一把記名的移動租約;回 <see cref="Guid.Empty"/>＝提供端給不了。</summary>
        [EzIPC("Path.AcquireSuppressionFor", true)] internal static readonly Func<string, int, Guid> Path_AcquireSuppressionFor;
        /// <summary>
        /// (甲) 續約(心跳)。<b>回 <see langword="false"/> 代表那把已經不在了</b>,
        /// 必須重新取得,不要當成續約成功。
        /// </summary>
        [EzIPC("Path.RenewSuppressionFor", true)] internal static readonly Func<Guid, int, bool> Path_RenewSuppressionFor;
        /// <summary>(甲) 交回一把租約。冪等。</summary>
        [EzIPC("Path.ReleaseSuppression", true)] internal static readonly Func<Guid, bool> Path_ReleaseSuppression;
        /// <summary>
        /// (甲) 用這把租約押住路徑容許值(提供端夾在 0.01～100,並明確拒絕 NaN／無限大)。
        /// 回 <see langword="false"/>＝這把租約已經不在了,或傳進去的值不是有限數。
        /// </summary>
        [EzIPC("Path.SetLeasedTolerance", true)] internal static readonly Func<Guid, float, bool> Path_SetLeasedTolerance;
        /// <summary>(甲) 套件沒有這個端點。</summary>
        [EzIPC("Window.IsOpen", true)] internal static readonly Func<bool> Window_IsOpen;
        /// <summary>(甲) 套件沒有這個端點。</summary>
        [EzIPC("Window.SetOpen", true)] internal static readonly Action<bool> Window_SetOpen;
        /// <summary>(甲) 套件沒有這個端點。</summary>
        [EzIPC("DTR.IsShown", true)] internal static readonly Func<bool> DTR_IsShown;
        /// <summary>(甲) 套件沒有這個端點。</summary>
        [EzIPC("DTR.SetShown", true)] internal static readonly Action<bool> DTR_SetShown;

        internal static void Dispose() => IPCSubscriber_Common.DisposeAll(_disposalTokens);
    }

    /// <summary>PandorasBox 側車。prefix 與門面類相同：<c>PandorasBox</c>。</summary>
    internal static class PandorasBoxExtraIPC
    {
        private static EzIPCDisposalToken[] _disposalTokens =
            EzIPC.Init(typeof(PandorasBoxExtraIPC), "PandorasBox", SafeWrapper.IPCException);

        /// <summary>
        /// (丙) 套件回傳 <c>bool?</c>，我方是 <c>bool</c>。AutoDuty.cs 直接把它當條件判斷用
        /// （「Auto-interact with Objects in Instances 現在開著沒」），改型別會連帶改掉
        /// 「拿不到值時算開還是算關」的語意，故維持我方宣告。
        /// </summary>
        [EzIPC] internal static readonly Func<string, bool> GetFeatureEnabled;

        /// <summary>(丙) 套件第三個參數是 <c>bool?</c>，我方是 <c>bool</c>。目前沒有呼叫點。</summary>
        [EzIPC] internal static readonly Action<string, string, bool> SetConfigEnabled;

        internal static void Dispose() => IPCSubscriber_Common.DisposeAll(_disposalTokens);
    }

    /// <summary>YesAlready 側車。prefix 與門面類相同：<c>YesAlready</c>。</summary>
    /// <remarks>
    /// (甲) 套件（<c>ECommons.IPC.Subscribers.YesAlready.YesAlreadyIPC</c>）只收了
    /// <c>IsPluginEnabled</c>／<c>SetPluginEnabled</c> 兩支，<b>沒有</b>壓制租約那一組。
    /// <para>
    /// 🔴 <b>為什麼是側車而不是去改套件</b>：<c>ECommons.IPC</c> 是<b>子模組</b>，動它要連帶
    /// 改 gitlink 並影響其他消費端 —— 而這一組端點目前只有 AutoDuty 要用。側車的存在理由
    /// 正是這個（見本檔檔頭），所以收在這裡。
    /// </para>
    /// <para>
    /// 🔴 <b>提供端沒有這幾支時</b>（YesAlready 沒安裝、或版本太舊）：
    /// <see cref="SafeWrapper.IPCException"/> 會把 <c>IpcNotReadyError</c> 吃掉並回
    /// <see langword="default"/> ⇒ <see cref="Guid.Empty"/>／<see langword="false"/>。
    /// 呼叫端（<c>YesAlready_IPCSubscriber</c>）就是靠這個訊號退回舊的開關寫入的。
    /// </para>
    /// </remarks>
    internal static class YesAlreadyExtraIPC
    {
        private static EzIPCDisposalToken[] _disposalTokens =
            EzIPC.Init(typeof(YesAlreadyExtraIPC), "YesAlready", SafeWrapper.IPCException);

        /// <summary>(甲) 取得一把記名的壓制租約；回 <see cref="Guid.Empty"/>＝提供端給不了。</summary>
        [EzIPC] internal static readonly Func<string, int, Guid> AcquireSuppressionFor;

        /// <summary>
        /// (甲) 續約（心跳）。<b>回 <see langword="false"/> 代表那把已經不在了</b>，
        /// 必須重新取得，不要當成續約成功。
        /// </summary>
        [EzIPC] internal static readonly Func<Guid, int, bool> RenewSuppressionFor;

        /// <summary>(甲) 交回一把租約。冪等。</summary>
        [EzIPC] internal static readonly Func<Guid, bool> ReleaseSuppression;

        internal static void Dispose() => IPCSubscriber_Common.DisposeAll(_disposalTokens);
    }
}
