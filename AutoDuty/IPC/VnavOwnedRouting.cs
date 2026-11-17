using Dalamud.Plugin.Ipc.Exceptions;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoDuty.IPC
{
    // 本檔契約：只在框架執行緒呼叫（狀態欄位不加鎖）；端點全部以委派注入，本身不碰 Svc。
    // 帶擁有者端點可用就送／停都走它，Path.StopIfOwner 只停 AutoDuty 自己送的路線；
    // 擲 IpcError（舊版 vnavmesh、端點沒註冊、型別對不上）就逐字退回舊端點，RetryAfterMs 內不再試，退回時寫一行 Information（每次載入一次）。
    // 停的方式跟最後一次送的方式一致：舊端點送的路線沒有擁有者，StopIfOwner 停不到，只能用舊的全域停止。
    internal sealed class VnavOwnedRouting
    {
        internal const string Owner = "AutoDuty";
        internal const long RetryAfterMs = 30_000;

        internal enum StopKind
        {
            OwnedStopped,
            OwnedNothing,
            LegacyStopped,
            LegacyIdle,
        }

        private readonly Func<Vector3, bool, string, bool> pathfindAndMoveToOwned;
        private readonly Func<Vector3, bool, bool> pathfindAndMoveToLegacy;
        private readonly Action<List<Vector3>, bool, string> moveToOwned;
        private readonly Action<List<Vector3>, bool> moveToLegacy;
        private readonly Func<string, bool> stopIfOwner;
        private readonly Func<bool> legacyIsRunning;
        private readonly Action legacyStopAll;
        private readonly Func<bool> providerLoaded;
        private readonly Action<string> logInfo;
        private readonly Func<long> nowMs;

        private bool ownedUnavailable;
        private long retryAtMs;
        private bool lastSendLegacy;
        private bool fallbackLogged;

        internal VnavOwnedRouting(
            Func<Vector3, bool, string, bool> pathfindAndMoveToOwned,
            Func<Vector3, bool, bool> pathfindAndMoveToLegacy,
            Action<List<Vector3>, bool, string> moveToOwned,
            Action<List<Vector3>, bool> moveToLegacy,
            Func<string, bool> stopIfOwner,
            Func<bool> legacyIsRunning,
            Action legacyStopAll,
            Func<bool> providerLoaded,
            Action<string> logInfo,
            Func<long> nowMs)
        {
            this.pathfindAndMoveToOwned  = pathfindAndMoveToOwned;
            this.pathfindAndMoveToLegacy = pathfindAndMoveToLegacy;
            this.moveToOwned             = moveToOwned;
            this.moveToLegacy            = moveToLegacy;
            this.stopIfOwner             = stopIfOwner;
            this.legacyIsRunning         = legacyIsRunning;
            this.legacyStopAll           = legacyStopAll;
            this.providerLoaded          = providerLoaded;
            this.logInfo                 = logInfo;
            this.nowMs                   = nowMs;
        }

        internal bool OwnedUnavailable => ownedUnavailable;
        internal bool LastSendLegacy   => lastSendLegacy;

        /// <summary>SimpleMove.PathfindAndMoveTo（帶擁有者版優先）。提供端自己擲的例外（非 IpcError）照樣往上冒。</summary>
        internal bool PathfindAndMoveTo(Vector3 dest, bool fly)
        {
            if (TryOwned())
            {
                try
                {
                    bool started = pathfindAndMoveToOwned(dest, fly, Owner);
                    lastSendLegacy = false;
                    return started;
                }
                catch (IpcError ex)
                {
                    MarkUnavailable("SimpleMove.PathfindAndMoveToOwned", ex);
                }
            }

            lastSendLegacy = true;
            return pathfindAndMoveToLegacy(dest, fly);
        }

        /// <summary>Path.MoveTo（帶擁有者版優先）。</summary>
        internal void MoveTo(List<Vector3> waypoints, bool fly)
        {
            if (TryOwned())
            {
                try
                {
                    moveToOwned(waypoints, fly, Owner);
                    lastSendLegacy = false;
                    return;
                }
                catch (IpcError ex)
                {
                    MarkUnavailable("Path.MoveToOwned", ex);
                }
            }

            lastSendLegacy = true;
            moveToLegacy(waypoints, fly);
        }

        /// <summary>
        /// 停 AutoDuty 自己送的路線（含排隊／算路中的那一筆），別的插件的不碰。
        /// 退回舊端點時＝原本的「有在走或在算才取消全部尋路並停路徑」。
        /// </summary>
        internal StopKind StopOwn()
        {
            if (!lastSendLegacy && TryOwned())
            {
                try
                {
                    return stopIfOwner(Owner) ? StopKind.OwnedStopped : StopKind.OwnedNothing;
                }
                catch (IpcError ex)
                {
                    MarkUnavailable("Path.StopIfOwner", ex);
                }
            }

            if (!legacyIsRunning())
                return StopKind.LegacyIdle;
            legacyStopAll();
            return StopKind.LegacyStopped;
        }

        private bool TryOwned()
        {
            if (!ownedUnavailable)
                return true;
            if (nowMs() < retryAtMs)
                return false;
            ownedUnavailable = false;
            return true;
        }

        private void MarkUnavailable(string endpoint, IpcError ex)
        {
            ownedUnavailable = true;
            retryAtMs        = nowMs() + RetryAfterMs;
            // vnavmesh 根本沒載入時舊端點也一樣失敗，這行說的「舊版」不成立，留到它載入後再記。
            if (fallbackLogged || !providerLoaded())
                return;
            fallbackLogged = true;
            logInfo($"[vnavmesh] 這版 vnavmesh 沒有帶擁有者的導航端點（vnavmesh.{endpoint}：{ex.GetType().Name}），AutoDuty 改用舊端點：中止或跳過步驟時會連別的插件的導航一起停。更新 vnavmesh 後會自動改回只停自己的。（每次載入只記這一行）");
        }
    }
}
