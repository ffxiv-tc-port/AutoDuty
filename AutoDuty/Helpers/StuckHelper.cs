using AutoDuty.IPC;
using ECommons.DalamudServices;
using ECommons.Throttlers;
using System;
using System.Numerics;

namespace AutoDuty.Helpers
{
    internal static class StuckHelper
    {
        internal static Vector3 LastPosition = Vector3.Zero;
        internal static long LastPositionUpdate = 0;

        internal static Vector3 LastStuckPosition       = Vector3.Zero;
        internal static long    LastStuckPositionUpdate = 0;

        private static byte counter = 0;

        internal static bool IsStuck(out byte count)
        {
            count = 0;
            if (!Player.Available) return false;
            if (!VNavmesh_IPCSubscriber.Path_IsRunning())
            {
                LastPositionUpdate = Environment.TickCount64;
            }
            else
            {
                if (Vector3.DistanceSquared(LastPosition, Player.Position) > 1f)
                {
                    LastPositionUpdate = Environment.TickCount64;
                    LastPosition       = Player.Position;
                }
            }


            if (Environment.TickCount64 - LastPositionUpdate > Plugin.Configuration.MinStuckTime && EzThrottler.Throttle("RequeueMoveTo", 1000))
            {
                LastStuckPosition       = Player.Position;
                LastStuckPositionUpdate = Environment.TickCount64;

                count                   = counter++;
                Svc.Log.Debug($"Stuck pathfinding: " + count);
                return true;
            }

            if (Environment.TickCount64 - LastStuckPositionUpdate > Plugin.Configuration.MinStuckTime * 10)
            {
                count = counter = 0;
            }

            return false;
        }

        /// <summary>
        /// 🔴 呼叫端「因為卡住次數達標而採取了行動」之後要呼叫這個,把計數歸零。
        ///
        /// counter 原本只在「連續 10 倍 MinStuckTime 沒再卡住」時才歸零。但只要玩家真的卡死,
        /// 卡住偵測就會每秒再命中一次,那個歸零條件永遠不成立 ⇒ counter 一旦越過
        /// RebuildNavmeshAfterStuckXTimes 門檻,之後**每一次**卡住偵測都會再觸發一次重建網格,
        /// 而不是設定畫面字面上寫的「每 X 次」。全量重建期間玩家更不會動,於是自我維持。
        ///
        /// 歸零之後行為變成:第 X 次卡住觸發一次動作 → 計數重來 → 第 2X 次再觸發一次,
        /// 與設定項「Rebuild Navmesh when stuck / X times」的字面語意一致。
        /// </summary>
        internal static void ResetStuckCount() => counter = 0;
    }
}