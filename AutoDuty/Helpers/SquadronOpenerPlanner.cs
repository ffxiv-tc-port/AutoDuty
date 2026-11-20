using System.Collections.Generic;

namespace AutoDuty.Helpers
{
    /// <summary>
    /// 分隊先發的時機與節流（純邏輯，不碰原生、不寫 log），<see cref="SquadronOpener"/> 與離線探針共用。
    /// 一次先發：路徑 A＝玩家不選目標、號令帶目標參數；等到分隊跟上或逾時。逾時而 A 下過令 ⇒ 路徑 B＝先選目標再下一次令並放手。
    /// 時間一律是呼叫端給的毫秒；一個領地一份，換領地呼叫 <see cref="ResetZone"/>。
    /// </summary>
    internal sealed class SquadronOpenerPlanner
    {
        /// <summary>任兩次「路徑 A」號令的最小間隔（號令冷卻未知，與 BMR 號令元件同值）。</summary>
        internal const long AnyGapMs = 2500;

        /// <summary>A 連續這麼多次沒人跟上 ⇒ 本領地改成直接走 B；B 再連續這麼多次沒人跟上 ⇒ 本領地停用先發。</summary>
        internal const int FailStreakLimit = 3;

        /// <summary>等待中玩家血量比例低於這個值就不再等。</summary>
        internal const float LowHpRatio = 0.5f;

        internal enum Step { Wait, IssueA, IssueBAndRelease, Release, Abandon }

        internal enum Reason { None, Followed, FollowedWithoutCommand, Timeout, LowHp, CommandUnavailable, BOnly, TargetGone, TargetSetElsewhere, BossStarted, Stopped }

        internal readonly struct Inputs(bool targetValid, bool targetSetElsewhere, bool bossActive, bool stopped, bool squadFollowed, float hpRatio, uint commandStatus, bool busy, long maxWaitMs)
        {
            internal readonly bool  TargetValid        = targetValid;
            internal readonly bool  TargetSetElsewhere = targetSetElsewhere;
            internal readonly bool  BossActive         = bossActive;
            internal readonly bool  Stopped            = stopped;
            internal readonly bool  SquadFollowed      = squadFollowed;
            internal readonly float HpRatio            = hpRatio;
            internal readonly uint  CommandStatus      = commandStatus;
            internal readonly bool  Busy               = busy;
            internal readonly long  MaxWaitMs          = maxWaitMs;
        }

        private readonly HashSet<ulong> _handled = [];
        private long? _lastACommandMs;
        private int  _aFailStreak;
        private int  _bFailStreak;

        internal ulong Pending      { get; private set; }
        internal long  StartMs      { get; private set; }
        internal long? AIssuedMs    { get; private set; }
        internal uint  LastStatus   { get; private set; }
        internal bool  BOnly        { get; private set; }
        internal bool  GaveUp       { get; private set; }

        // 本領地彙總
        internal int Started, AlreadyOn, ACommands, AFollowed, BCommands, BFollowed, Timeouts, LowHp, Unavailable, Abandoned;
        internal double AFollowSecondsSum, BFollowSecondsSum;

        internal void ResetZone()
        {
            _handled.Clear();
            _lastACommandMs = null;
            _aFailStreak = _bFailStreak = 0;
            Pending = 0;
            AIssuedMs = null;
            LastStatus = 0;
            BOnly = GaveUp = false;
            Started = AlreadyOn = ACommands = AFollowed = BCommands = BFollowed = Timeouts = LowHp = Unavailable = Abandoned = 0;
            AFollowSecondsSum = BFollowSecondsSum = 0;
        }

        internal bool Handled(ulong target) => _handled.Contains(target);

        /// <summary>同一隻只先發一次；分隊已經在打它＝記一筆、不先發。回 true＝開始等待。</summary>
        internal bool TryStart(ulong target, long nowMs, bool squadAlreadyOn)
        {
            if (GaveUp || Pending != 0 || target == 0 || !_handled.Add(target))
                return false;
            if (squadAlreadyOn)
            {
                ++AlreadyOn;
                return false;
            }
            Pending = target;
            StartMs = nowMs;
            AIssuedMs = null;
            LastStatus = 0;
            ++Started;
            return true;
        }

        internal Step Update(long nowMs, in Inputs i, out Reason reason)
        {
            reason = Reason.None;
            if (Pending == 0)
                return Step.Wait;

            if (!i.TargetValid)
                return Finish(Step.Abandon, reason = Reason.TargetGone);
            if (i.TargetSetElsewhere)
                return Finish(Step.Abandon, reason = Reason.TargetSetElsewhere);
            if (i.BossActive)
                return Finish(Step.Abandon, reason = Reason.BossStarted);
            if (i.Stopped)
                return Finish(Step.Abandon, reason = Reason.Stopped);

            if (i.SquadFollowed)
            {
                if (AIssuedMs is { } a)
                {
                    ++AFollowed;
                    AFollowSecondsSum += (nowMs - a) / 1000.0;
                    _aFailStreak = 0;
                    reason = Reason.Followed;
                }
                else
                    reason = Reason.FollowedWithoutCommand;
                return Finish(Step.Release, reason);
            }

            if (i.HpRatio < LowHpRatio)
            {
                ++LowHp;
                return Finish(Step.Release, reason = Reason.LowHp);
            }

            LastStatus = i.CommandStatus;
            var timedOut = nowMs - StartMs >= i.MaxWaitMs;
            if (BOnly)
            {
                if (i.CommandStatus == 0 && !i.Busy)
                    return Finish(Step.IssueBAndRelease, reason = Reason.BOnly);
                if (!timedOut)
                    return Step.Wait;
                ++Unavailable;
                return Finish(Step.Release, reason = Reason.CommandUnavailable);
            }

            if (timedOut)
            {
                if (AIssuedMs == null)
                {
                    ++Unavailable;
                    return Finish(Step.Release, reason = Reason.CommandUnavailable);
                }
                ++Timeouts;
                if (++_aFailStreak >= FailStreakLimit)
                    BOnly = true;
                reason = Reason.Timeout;
                return Finish(i.CommandStatus == 0 && !i.Busy ? Step.IssueBAndRelease : Step.Release, reason);
            }

            if (AIssuedMs == null && i.CommandStatus == 0 && !i.Busy && (_lastACommandMs == null || nowMs - _lastACommandMs.Value >= AnyGapMs))
                return Step.IssueA;
            return Step.Wait;
        }

        /// <summary>呼叫端真的按下路徑 A 之後呼叫（UseAction 回傳值不論）。</summary>
        internal void NoteAIssued(long nowMs)
        {
            AIssuedMs = nowMs;
            _lastACommandMs = nowMs;
            ++ACommands;
        }

        internal void NoteBIssued() => ++BCommands;

        /// <summary>路徑 B 下令後的驗收結果；連續沒跟上達上限 ⇒ 本領地停用。</summary>
        internal void NoteBResult(double? followSeconds)
        {
            if (followSeconds is { } s)
            {
                ++BFollowed;
                BFollowSecondsSum += s;
                _bFailStreak = 0;
            }
            else if (++_bFailStreak >= FailStreakLimit)
                GaveUp = true;
        }

        private Step Finish(Step step, Reason reason)
        {
            if (step == Step.Abandon)
                ++Abandoned;
            Pending = 0;
            return step;
        }
    }
}
