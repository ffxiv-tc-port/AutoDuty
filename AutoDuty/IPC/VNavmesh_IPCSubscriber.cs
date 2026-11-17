using Dalamud.Plugin;
using Dalamud.Plugin.Ipc.Exceptions;
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

// 門面層的共同規則（wrapper 明確傳入建構式等）見 IPCSubscriber.cs 檔頭。

namespace AutoDuty.IPC
{
    using System.ComponentModel;
    using ECommons.GameFunctions;
    using Helpers;

    internal static class VNavmesh_IPCSubscriber
    {
        private static readonly VnavmeshIPC Pkg = new(SafeWrapper.IPCException);

        // 送路線與 StopOwnNavigation 都經這裡;側車欄位綁不上(null)當成端點不存在,一樣退回舊端點。
        private static readonly VnavOwnedRouting Owned = new(
            pathfindAndMoveToOwned:  (dest, fly, owner) => (VNavmeshExtraIPC.SimpleMove_PathfindAndMoveToOwned ?? throw new IpcNotReadyError("vnavmesh.SimpleMove.PathfindAndMoveToOwned"))(dest, fly, owner),
            pathfindAndMoveToLegacy: (dest, fly) => Pkg.PathfindAndMoveTo(dest, fly),
            moveToOwned:             (waypoints, fly, owner) => (VNavmeshExtraIPC.Path_MoveToOwned ?? throw new IpcNotReadyError("vnavmesh.Path.MoveToOwned"))(waypoints, fly, owner),
            moveToLegacy:            (waypoints, fly) => Pkg.MoveTo(waypoints, fly),
            stopIfOwner:             owner => (VNavmeshExtraIPC.Path_StopIfOwner ?? throw new IpcNotReadyError("vnavmesh.Path.StopIfOwner"))(owner),
            legacyIsRunning:         () => Pkg.IsRunning() || Pkg.PathfindInProgress(),
            legacyStopAll:           () =>
            {
                VNavmeshExtraIPC.Nav_PathfindCancelAll();
                Pkg.Stop();
            },
            providerLoaded:          () => IsEnabled,
            logInfo:                 message => Svc.Log.Information(message),
            nowMs:                   () => Environment.TickCount64);

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

        // ── 套件把這三支宣告成自訂具名委派(VnavmeshIPC.Delegates.Pathfind / PathMoveTo / PathfindAndMoveTo) ──
        // EzIPC 訂閱端要有 TryGetDelegateSignature 才綁得上;綁不上時欄位停在 null(靜態建構式會檢查)。
        // 端點名是 Nav.Pathfind / Path.MoveTo / SimpleMove.PathfindAndMoveTo,SafeWrapper 同為 IPCException。
        internal static Task<List<Vector3>> Nav_Pathfind(Vector3 from, Vector3 to, bool fly) => Pkg.Pathfind(from, to, fly);
        /// <summary>送路線：帶擁有者版優先，舊版 vnavmesh 退回 Path.MoveTo（見 <see cref="VnavOwnedRouting"/>）。</summary>
        internal static void Path_MoveTo(List<Vector3> waypoints, bool fly) => Owned.MoveTo(waypoints, fly);
        /// <summary>
        /// 算路徑並開始移動。<b>vnavmesh 那一側自己擲例外時回 <see langword="false"/></b>。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 🔴 <b>vnavmesh 的這支端點會擲例外，而 <c>SafeWrapper.IPCException</c> 攔不到它。</b>
        /// <c>NavmeshManager.QueryPath</c> 在導航網格沒載入時直接擲一個普通的 <c>Exception</c>
        /// （「切圖／讀取畫面期間導航網格被卸掉」與「使用者關掉 vnavmesh 的自動載入」時都成立）。
        /// Dalamud 的 <c>CallGateChannel.InvokeFunc</c> 是用 <c>Delegate.DynamicInvoke</c> 呼叫提供端的，
        /// 所以提供端擲出的東西一律被包成 <see cref="System.Reflection.TargetInvocationException"/>，
        /// 而它<b>不是</b> <c>IpcError</c> 的子型別。
        /// </para>
        /// <para>
        /// 呼叫點不一定先問過 <c>Nav_IsReady()</c>（<c>AutoDuty.StageReadingPath</c> 就沒有，而 <c>Framework_Update</c> 上
        /// 沒有 try/catch），不攔的話表現出來是「副本跑到一半不動了、每幀噴一次例外」。
        /// </para>
        /// <para>
        /// 🔴 刻意<b>只</b>攔 <see cref="System.Reflection.TargetInvocationException"/>，不是裸
        /// <c>catch (Exception)</c> —— 本外掛自己這一側的程式錯誤（<c>NullReferenceException</c> 之類）
        /// 不會被包成這個型別，照樣往上冒。<c>IpcNotReadyError</c> 仍由
        /// <c>SafeWrapper.IPCException</c> 吞掉並回 <see langword="false"/>。
        /// </para>
        /// </remarks>
        internal static bool SimpleMove_PathfindAndMoveTo(Vector3 position, bool canFly)
        {
            try
            {
                return Owned.PathfindAndMoveTo(position, canFly);
            }
            catch (System.Reflection.TargetInvocationException ex)
            {
                IPCSubscriber_Common.ReportProviderFault("vnavmesh", "SimpleMove.PathfindAndMoveTo", ex);
                return false;
            }
        }

        /// <summary>
        /// 只停 AutoDuty 自己送的路線(含排隊／算路中的那一筆),別的插件的不碰;
        /// 舊版 vnavmesh 退回「有在走或在算才取消全部尋路並停路徑」。
        /// </summary>
        internal static VnavOwnedRouting.StopKind StopOwnNavigation() => Owned.StopOwn();

        // ── 以下走側車，理由見 IPCSubscriberSidecar.cs ──
        internal static Task<List<Vector3>> Nav_PathfindCancelable(Vector3 from, Vector3 to, bool fly, CancellationToken token) => VNavmeshExtraIPC.Nav_PathfindCancelable(from, to, fly, token);
        internal static void Nav_PathfindCancelAll()      => VNavmeshExtraIPC.Nav_PathfindCancelAll();
        internal static bool Nav_PathfindInProgress()     => VNavmeshExtraIPC.Nav_PathfindInProgress();
        internal static int  Nav_PathfindNumQueued()      => VNavmeshExtraIPC.Nav_PathfindNumQueued();
        internal static bool Nav_IsAutoLoad()             => VNavmeshExtraIPC.Nav_IsAutoLoad();
        internal static void Nav_SetAutoLoad(bool on)     => VNavmeshExtraIPC.Nav_SetAutoLoad(on);

        /// <summary>
        /// 這支側車欄位有沒有被 EzIPC 指派;綁不上時它是 <see langword="null"/>,呼叫會擲 <c>NullReferenceException</c> 而 <c>SafeWrapper</c> 攔不到。
        /// </summary>
        internal static bool Query_Mesh_NearestPointBound => VNavmeshExtraIPC.Query_Mesh_NearestPoint != null;

        internal static Vector3? Query_Mesh_NearestPoint(Vector3 p, float halfExtentXZ, float halfExtentY) => VNavmeshExtraIPC.Query_Mesh_NearestPoint(p, halfExtentXZ, halfExtentY);
        internal static Vector3? Query_Mesh_PointOnFloor(Vector3 p, bool allowUnlandable, float halfExtentXZ) => VNavmeshExtraIPC.Query_Mesh_PointOnFloor(p, allowUnlandable, halfExtentXZ);

        internal static bool Window_IsOpen()          => VNavmeshExtraIPC.Window_IsOpen();
        internal static void Window_SetOpen(bool on)  => VNavmeshExtraIPC.Window_SetOpen(on);
        internal static bool DTR_IsShown()            => VNavmeshExtraIPC.DTR_IsShown();
        internal static void DTR_SetShown(bool on)    => VNavmeshExtraIPC.DTR_SetShown(on);

        internal static void Dispose() => VNavmeshExtraIPC.Dispose();
    }
}
