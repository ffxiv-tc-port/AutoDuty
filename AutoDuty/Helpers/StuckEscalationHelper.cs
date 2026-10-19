using AutoDuty.IPC;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using System;
using System.Numerics;

namespace AutoDuty.Helpers
{
    /// <summary>
    /// 卡住升級階梯。只在 <see cref="StuckHelper.IsStuck"/> 命中時由框架執行緒進來。
    /// </summary>
    /// <remarks>
    /// 階數用 StuckHelper 的連續卡住計數(0 起算)判定,每一階只在計數「剛好等於」該階時觸發,
    /// 所以同一輪階梯裡每個動作至多做一次；計數要嘛被脫困後的自然歸零清掉,
    /// 要嘛被最後一階清掉,階梯因此是有界的。
    /// 順序:重排 MoveTo(呼叫端的既有行為) → 跳一次 → 側移 → 跳過本步 → 重建網格。
    /// </remarks>
    internal static class StuckEscalationHelper
    {
        // 側移本身也可能卡住,所以一定要有上界:逾時就停掉,讓階梯繼續往上爬。
        private const int SidestepTimeoutMs = 3000;

        private static bool sidestepActive;
        private static long sidestepDeadline;
        private static bool sidestepToRight;

        // 綁定缺失是整個 session 的性質,不隨 Reset() 清掉:報一次就夠,不要每次卡住都洗一行。
        private static bool nearestPointUnboundLogged;

        internal static int JumpAtCount     => Math.Max(1,                  Plugin.Configuration.StuckJumpAfterXTimes);
        internal static int SidestepAtCount => Math.Max(JumpAtCount + 1,    Plugin.Configuration.StuckSidestepAfterXTimes);
        internal static int SkipStepAtCount => Math.Max(SidestepAtCount + 1, Plugin.Configuration.StuckSkipStepAfterXTimes);

        /// <summary>
        /// 重建網格的階數,<b>永遠排在「跳過本步」之後</b>。
        /// </summary>
        /// <remarks>
        /// 既有使用者存下來的 RebuildNavmeshAfterStuckXTimes 比前面幾階小,
        /// 直接採用會讓重建搶在偏移與推進之前。
        /// 光改預設值對既有設定檔無效,所以由這裡的下限把順序釘住。
        /// 階梯關掉時逐字退回使用者存的值。
        /// </remarks>
        internal static int RebuildAtCount =>
            Plugin.Configuration.StuckEscalationEnabled
                ? Math.Max(SkipStepAtCount + 1, Plugin.Configuration.RebuildNavmeshAfterStuckXTimes)
                : Plugin.Configuration.RebuildNavmeshAfterStuckXTimes;

        /// <summary>導航整個停下來時清掉側移狀態；不碰路徑(呼叫端自己會停)。冪等。</summary>
        internal static void Reset() => sidestepActive = false;

        /// <summary>
        /// 第 2 階:原地跳一次。<b>刻意不停掉目前路徑</b> —— 邊走邊跳才有機會越過卡住的邊緣,
        /// 停下來再跳等於原地跳。回 <see langword="true"/> 代表呼叫端這一輪什麼都不用再做。
        /// </summary>
        internal static unsafe bool TryJump(byte stuckCount, Vector3 stuckAt)
        {
            if (stuckCount != JumpAtCount)
                return false;

            if (PlayerHelper.IsCasting || PlayerHelper.IsJumping)
                return false;

            if (ActionManager.Instance()->GetActionStatus(ActionType.GeneralAction, 2) != 0)
            {
                Svc.Log.Information($"[卡住處理] 第 2 階(跳躍)跳過:在 {stuckAt} 卡住,但目前跳不起來,直接走下一階。");
                return false;
            }

            Svc.Log.Information($"[卡住處理] 第 2 階:在 {stuckAt} 卡住,跳一次(不中斷目前路徑)。");
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 2);
            return true;
        }

        /// <summary>
        /// 第 3 階:往垂直於「目前航向」的方向側移一小段,左右輪替。
        /// 回 <see langword="true"/> 代表側移路徑已經下出去,呼叫端應該把 Stage 交回 Reading_Path
        /// （側移跑完前 StageReadingPath 不會重下原目標）。
        /// </summary>
        internal static bool TrySidestep(byte stuckCount, Vector3 target)
        {
            if (stuckCount != SidestepAtCount)
                return false;

            // 🔴 沒有這支就驗不到落點在不在導航網格上(而且裸呼叫 null 會每幀擲 NRE),寧可整階跳過。
            if (!VNavmesh_IPCSubscriber.Query_Mesh_NearestPointBound)
            {
                if (!nearestPointUnboundLogged)
                {
                    nearestPointUnboundLogged = true;
                    Svc.Log.Information("[卡住處理] vnavmesh 未提供 Query.Mesh.NearestPoint,側移階整個跳過,直接走下一階(本 session 只報這一次)。");
                }

                return false;
            }

            Vector3 self = Player.Position;
            Vector3 flat = new(target.X - self.X, 0, target.Z - self.Z);
            if (flat.LengthSquared() < 0.01f)
                return false;

            Vector3 heading       = Vector3.Normalize(flat);
            Vector3 perpendicular = new(heading.Z, 0, -heading.X);
            float   distance      = Math.Clamp(Plugin.Configuration.StuckSidestepDistance, 0.5f, 10f);

            sidestepToRight = !sidestepToRight;
            bool toRight = sidestepToRight;
            if (!TryResolveSidestepPoint(self, perpendicular, distance, toRight, out Vector3 point))
            {
                toRight = !toRight;
                if (!TryResolveSidestepPoint(self, perpendicular, distance, toRight, out point))
                {
                    Svc.Log.Information($"[卡住處理] 第 3 階(側移)跳過:在 {self} 卡住,左右 {distance:0.0} 米處都不在導航網格上,直接走下一階。");
                    return false;
                }
            }

            VNavmesh_IPCSubscriber.Path_MoveTo([point], false);
            sidestepActive   = true;
            sidestepDeadline = Environment.TickCount64 + SidestepTimeoutMs;
            Svc.Log.Information($"[卡住處理] 第 3 階:在 {self} 卡住,先往{(toRight ? "右" : "左")}側移到 {point},到了再走原目標。");
            return true;
        }

        /// <summary>第 4 階:跳過本步。實際的跳過動作由呼叫端用既有的 SkipCurrentStep 做。</summary>
        internal static bool ShouldSkipStep(byte stuckCount) => stuckCount == SkipStepAtCount;

        /// <summary>
        /// 側移的看門狗,由 StageReadingPath 每幀呼叫。
        /// </summary>
        /// <remarks>
        /// 側移期間不在 Stage.Moving,卡住偵測不會跑,所以逾時只能靠這裡收。
        /// </remarks>
        internal static void TickSidestep()
        {
            if (!sidestepActive)
                return;

            if (Environment.TickCount64 < sidestepDeadline)
            {
                if (!VNavmesh_IPCSubscriber.Path_IsRunning())
                    sidestepActive = false;
                return;
            }

            sidestepActive = false;
            if (VNavmesh_IPCSubscriber.Path_IsRunning())
                VNavmesh_IPCSubscriber.Path_Stop();

            Svc.Log.Information($"[卡住處理] 側移 {SidestepTimeoutMs} 毫秒內沒走到,停止側移並回到原目標。");
        }

        /// <summary>
        /// 把側移落點投影到導航網格上。<b>投影不到就不走</b> —— 側移用的是不看地形的直線推進,
        /// 落在網格外等於往牆裡或懸崖外走。
        /// </summary>
        private static bool TryResolveSidestepPoint(Vector3 self, Vector3 perpendicular, float distance, bool toRight, out Vector3 point)
        {
            point = Vector3.Zero;

            Vector3  candidate = self + perpendicular * (toRight ? distance : -distance);
            Vector3? onMesh    = VNavmesh_IPCSubscriber.Query_Mesh_NearestPoint(candidate, 2f, 2f);
            if (onMesh is not { } resolved)
                return false;

            // 投影回來太靠近原地就不算側移(整片牆的最近點常常就是自己腳下)。
            float dx = resolved.X - self.X;
            float dz = resolved.Z - self.Z;
            if (dx * dx + dz * dz < distance * distance * 0.25f)
                return false;

            point = resolved;
            return true;
        }
    }
}
