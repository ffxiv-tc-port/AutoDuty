using System;

namespace AutoDuty.Helpers
{
    /// <summary>
    /// 站定等待時向 BMR 押「暫停閃避」租約的狀態機（不碰遊戲狀態、不寫 log，送出與交回由呼叫端給）；離線探針直接測這裡。
    /// 不要了就當場交回；押著時每 renewMs 續一次；送出失敗（端點不在或被拒）這一次等待就不再重試。
    /// </summary>
    internal sealed class MovementHoldSession(int renewMs)
    {
        internal enum SendResult { Ok, Rejected, Unavailable }

        internal enum Event { None, Started, Renewed, Released, Unavailable, Rejected }

        internal bool Held { get; private set; }

        private long _nextRenewAt;
        private bool _gaveUp;

        /// <summary>新的一次等待（含重做）：允許重新探測端點。</summary>
        internal void NewEpisode() => _gaveUp = false;

        internal Event Step(bool wantHold, long nowMs, Func<SendResult> send, Action release)
        {
            if (!wantHold)
                return ForceRelease(release) ? Event.Released : Event.None;

            if (_gaveUp || (Held && nowMs < _nextRenewAt))
                return Event.None;

            _nextRenewAt = nowMs + renewMs;
            SendResult result = send();
            if (result == SendResult.Ok)
            {
                Event ev = Held ? Event.Renewed : Event.Started;
                Held = true;
                return ev;
            }

            _gaveUp = true;
            ForceRelease(release);
            return result == SendResult.Unavailable ? Event.Unavailable : Event.Rejected;
        }

        /// <summary>押著才交回；先清狀態再呼叫 <paramref name="release"/>，它擲例外也不會留下「以為還押著」。</summary>
        internal bool ForceRelease(Action release)
        {
            if (!Held)
                return false;
            Held = false;
            release();
            return true;
        }
    }
}
