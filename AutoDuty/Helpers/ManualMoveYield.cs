using AutoDuty.IPC;
using Dalamud.Game.ClientState.Keys;
using ECommons.DalamudServices;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace AutoDuty.Helpers
{
    /// <summary>
    /// 跑副本導航途中，使用者自己動方向時讓位。vnavmesh「玩家輸入移動時取消目前路徑」清掉路徑，
    /// 那不是「走到了」：讓位中不送路徑、不推進步驟；放開按鍵且角色靜止 <see cref="ResumeQuietMs"/> 後
    /// 從目前位置重走同一步；單次讓位最多 <see cref="MaxYieldMs"/>。
    /// 只在框架執行緒用。讀不到按鍵狀態就當成沒按＝與沒有這個判斷時相同。
    /// </summary>
    internal static class ManualMoveYield
    {
        internal enum YieldEvent { None, Started, Resumed, Expired, Cancelled }

        /// <summary>放開後還算「剛按過」的時間：短按一下的取消也要接得到。</summary>
        internal const long LatchMs = 1000;

        internal const long ResumeQuietMs = 1500;

        internal const long MaxYieldMs = 30_000;

        /// <summary>算「靜止」的水平速度上限（米／秒）。</summary>
        internal const float StillSpeedMax = 1f;

        private const long SpeedWindowMs = 300;

        /// <summary>手把左搖桿的值域約 ±99，超過這個值才算在推。</summary>
        private const float StickThreshold = 30f;

        private static readonly VirtualKey[] Candidates =
        [
            VirtualKey.W, VirtualKey.A, VirtualKey.S, VirtualKey.D, VirtualKey.Q, VirtualKey.E,
            VirtualKey.UP, VirtualKey.DOWN, VirtualKey.LEFT, VirtualKey.RIGHT,
        ];

        private static VirtualKey[]? _keys;
        private static bool          _mouseValid;
        private static bool          _broken;

        private static bool _inputSeen;
        private static long _lastInputAt;

        private static long _lastFrameTicks;
        private static bool _wasNavigating;

        private static bool _yielding;
        private static long _yieldStartedAt;
        private static bool _still;
        private static long _stillSince;
        private static bool _resumeDue;

        private static bool    _speedHasSample;
        private static Vector3 _speedFrom;
        private static long    _speedFromAt;
        private static float   _speed = -1f;

        internal static bool Yielding => _yielding;

        /// <summary>
        /// 一幀只算一次，重複呼叫是空操作。路徑任務（TaskManager 比 Framework_Update 先跑）送路徑前也要先呼叫，
        /// 否則被取消的那一幀會先被任務重送、永遠看不到「路徑停了」。只在跑副本路徑且沒暫停時作用，其餘時候清掉狀態。
        /// </summary>
        internal static void Tick()
        {
            long frame = Svc.Framework.LastUpdateUTC.Ticks;
            if (frame == _lastFrameTicks)
                return;

            _lastFrameTicks = frame;
            if (!Plugin.States.HasFlag(PluginState.Navigating) || Plugin.States.HasFlag(PluginState.Paused) || !Player.Available)
            {
                Reset();
                return;
            }

            long now        = Environment.TickCount64;
            bool input      = Latch(ReadPressed(), now);
            bool navigating = VNavmesh_IPCSubscriber.Path_IsRunning() || VNavmesh_IPCSubscriber.SimpleMove_PathfindInProgress();

            switch (Observe(navigating, input, PlayerHelper.InCombat, Player.Position, now))
            {
                case YieldEvent.Started:
                    Svc.Log.Information($"[手動讓位] 導航途中偵測到你在操作移動、路徑被取消：暫停送路徑、不推進步驟，放開按鍵並停下 {ResumeQuietMs} 毫秒後從目前位置重走這一步。");
                    break;
                case YieldEvent.Resumed:
                    Svc.Log.Information($"[手動讓位] 已放開按鍵並停下 {ResumeQuietMs} 毫秒：從目前位置重走這一步。");
                    break;
                case YieldEvent.Expired:
                    Svc.Log.Information($"[手動讓位] 讓位滿 {MaxYieldMs / 1000} 秒：從目前位置重走這一步。");
                    break;
                case YieldEvent.Cancelled:
                    Svc.Log.Information("[手動讓位] 導航又開始了或進入戰鬥：交回原本的流程。");
                    break;
            }
        }

        /// <summary>
        /// 純邏輯（不碰遊戲）：導航從「在走／在算」變成停下的那一拍，使用者剛按過移動鍵且不在戰鬥中 ⇒ 開始讓位。
        /// 讓位中導航又動了或進入戰鬥 ⇒ 取消；沒按鍵且水平速度低於 <see cref="StillSpeedMax"/> 連續 <see cref="ResumeQuietMs"/>，
        /// 或讓位滿 <see cref="MaxYieldMs"/> ⇒ 結束並要求重走這一步（<see cref="TakeResume"/>）。
        /// </summary>
        internal static YieldEvent Observe(bool navigating, bool input, bool inCombat, Vector3 position, long now)
        {
            UpdateSpeed(position, now);
            YieldEvent result = YieldEvent.None;

            if (_yielding)
            {
                if (navigating || inCombat)
                    result = End(false, YieldEvent.Cancelled);
                else if (now - _yieldStartedAt >= MaxYieldMs)
                    result = End(true, YieldEvent.Expired);
                else if (input || _speed < 0 || _speed >= StillSpeedMax)
                    _still = false;
                else if (!_still)
                {
                    _still      = true;
                    _stillSince = now;
                }
                else if (now - _stillSince >= ResumeQuietMs)
                    result = End(true, YieldEvent.Resumed);
            }
            else if (_wasNavigating && !navigating && input && !inCombat)
            {
                _yielding       = true;
                _yieldStartedAt = now;
                _still          = false;
                _resumeDue      = false;
                result          = YieldEvent.Started;
            }

            _wasNavigating = navigating;
            return result;
        }

        /// <summary>按著時記下時刻，放開後 <see cref="LatchMs"/> 內仍算剛按過。</summary>
        internal static bool Latch(bool pressed, long now)
        {
            if (pressed)
            {
                _inputSeen   = true;
                _lastInputAt = now;
            }

            return _inputSeen && now - _lastInputAt <= LatchMs;
        }

        /// <summary>讓位結束、該從目前位置重走這一步時回 true 並清掉（一次讓位只接續一次）。</summary>
        internal static bool TakeResume()
        {
            bool due = _resumeDue;
            _resumeDue = false;
            return due;
        }

        internal static void Reset()
        {
            _wasNavigating  = false;
            _yielding       = false;
            _still          = false;
            _resumeDue      = false;
            _inputSeen      = false;
            _speedHasSample = false;
            _speed          = -1f;
        }

        private static YieldEvent End(bool resume, YieldEvent why)
        {
            _yielding  = false;
            _still     = false;
            _resumeDue = resume;
            return why;
        }

        // 水平速度：與約 SpeedWindowMs 前那一筆比；還沒有那麼舊的取樣時是 -1（不知道，不算靜止）。
        private static void UpdateSpeed(Vector3 position, long now)
        {
            if (!_speedHasSample)
            {
                _speedHasSample = true;
                _speedFrom      = position;
                _speedFromAt    = now;
                _speed          = -1f;
                return;
            }

            long elapsed = now - _speedFromAt;
            if (elapsed < SpeedWindowMs)
                return;

            float dx = position.X - _speedFrom.X;
            float dz = position.Z - _speedFrom.Z;
            _speed       = MathF.Sqrt(dx * dx + dz * dz) / (elapsed / 1000f);
            _speedFrom   = position;
            _speedFromAt = now;
        }

        private static bool ReadPressed()
        {
            if (_broken)
                return false;

            try
            {
                return Pressed();
            }
            catch (Exception ex)
            {
                _broken = true;
                Svc.Log.Information($"[手動讓位] 讀不到按鍵／手把狀態（{ex.GetType().Name}：{ex.Message}），之後不再偵測手動移動。");
                return false;
            }
        }

        private static bool Pressed()
        {
            var state = Svc.KeyState;
            if (_keys == null)
            {
                var valid = new List<VirtualKey>(Candidates.Length);
                foreach (var key in Candidates)
                    if (state.IsVirtualKeyValid(key))
                        valid.Add(key);

                _keys       = valid.ToArray();
                _mouseValid = state.IsVirtualKeyValid(VirtualKey.LBUTTON) && state.IsVirtualKeyValid(VirtualKey.RBUTTON);
            }

            foreach (var key in _keys)
                if (state[key])
                    return true;

            if (_mouseValid && state[VirtualKey.LBUTTON] && state[VirtualKey.RBUTTON])
                return true;

            Vector2 stick = Svc.GamepadState.LeftStick;
            return MathF.Abs(stick.X) >= StickThreshold || MathF.Abs(stick.Y) >= StickThreshold;
        }
    }
}
