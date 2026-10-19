namespace AutoDuty.Helpers;

/// <summary>走向定點時「vnavmesh 路徑結束了卻還沒到位」之後的重排節奏;只吃數值、不碰遊戲狀態,可以離線驗。</summary>
/// <remarks>
/// 有前進＝上一條路徑讓人離目標近了 MinProgress 以上,照舊立刻再排。沒前進就記一次失敗:
/// 前 QuickRetries 次照樣立刻再排,之後每 RetryIntervalMs 才排一次,不再每幀重排;一有前進就歸零。
/// </remarks>
internal sealed class ApproachRetry
{
    internal const float MinProgress = 0.5f;
    internal const int QuickRetries = 2;
    internal const int RetryIntervalMs = 2000;

    private bool pending;
    private float issuedDist;
    private long issuedAt;

    internal int Failures { get; private set; }

    /// <summary>呼叫端自用,例如「開始節流的說明只寫一次」。</summary>
    internal bool HoldAnnounced;

    // 上一條路徑結束後第一次呼叫時結算它有沒有前進;同一條路徑只算一次。
    internal void Settle(float distToTarget)
    {
        if (!pending)
            return;
        pending = false;
        Failures = issuedDist - distToTarget >= MinProgress ? 0 : Failures + 1;
    }

    internal void MarkIssued(float distToTarget, long nowMs)
    {
        pending = true;
        issuedDist = distToTarget;
        issuedAt = nowMs;
    }

    // canIssue:vnavmesh 閒著、玩家與導航都就緒,此刻呼叫 MovementHelper.Move 會真的排一條新路徑。
    // 回 true ＝ 這一幀先別呼叫 Move。
    internal bool ShouldHold(bool canIssue, float distToTarget, long nowMs)
    {
        if (!canIssue)
            return false;
        Settle(distToTarget);
        if (Failures > QuickRetries && nowMs - issuedAt < RetryIntervalMs)
            return true;
        MarkIssued(distToTarget, nowMs);
        return false;
    }
}
