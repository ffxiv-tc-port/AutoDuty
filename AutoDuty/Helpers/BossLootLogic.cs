using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace AutoDuty.Helpers;

/// <summary>BossLoot 挑寶箱、走向寶箱的判斷;只吃數值、不碰遊戲狀態,可以離線驗。</summary>
internal static class BossLootLogic
{
    /// <summary>BossLoot 呼叫 MovementHelper.Move 的最後一點容許值,走到這個距離內才算到達。</summary>
    internal const float ArriveDistance = 1f;

    /// <summary>與 InteractableCheck 開始互動的距離相同(扣掉雙方 hitbox)。</summary>
    internal const float InteractDistance = 2f;

    internal enum ChestStep { Move, InReach, GiveUp }

    internal sealed class ChestApproach
    {
        internal bool Arrived;
        internal bool GaveUp;
        internal readonly ApproachRetry Retry = new();
    }

    // canIssue 同 ApproachRetry.ShouldHold。寶箱不重試:第一條沒讓人更接近寶箱的路徑一結束,
    // 就已經停在網格上最接近它的點,依距離決定開或放棄。
    internal static ChestStep Judge(ChestApproach a, bool canIssue, float centerDist, float battleDist)
    {
        if (!canIssue || centerDist <= ArriveDistance)
            return ChestStep.Move;
        a.Retry.Settle(centerDist);
        if (a.Retry.Failures > 0)
            return battleDist <= InteractDistance ? ChestStep.InReach : ChestStep.GiveUp;
        a.Retry.MarkIssued(centerDist, 0);
        return ChestStep.Move;
    }

    /// <summary>剔除王戰開始時就已在場的寶箱(王的寶箱要等王倒下才出現);沒有快照就不動。回傳剔除數。</summary>
    internal static int ExcludePreFight(List<ulong> coffers, HashSet<ulong>? preFight)
        => preFight == null ? 0 : coffers.RemoveAll(preFight.Contains);

    /// <summary>沒有撿寶點時,撿玩家周圍這個圓柱內的寶箱(半徑、高低差)。</summary>
    internal const float NearPlayerRange = 50f;
    internal const float LootHeightRange = 10f;

    /// <summary>撿寶點沒另給半徑時的預設半徑(與上游相同)。</summary>
    internal const float LootPointDefaultRange = 5f;

    /// <summary>
    /// 路徑 Boss 步驟的撿寶點:第一個參數是「&lt;x, y, z&gt;」時回 true,第二個參數可另給半徑。
    /// 第一個參數沒有或空白＝沒指定,回 false 且 problem 為 null;有寫但解不開回 false 並在 problem 寫原因。
    /// 半徑寫壞時仍回 true(用預設半徑),problem 寫原因。只解析字串,不碰遊戲狀態。
    /// </summary>
    internal static bool TryParseLootPoint(IReadOnlyList<string>? args, out Vector3 point, out float range, out string? problem)
    {
        point = Vector3.Zero;
        range = LootPointDefaultRange;
        problem = null;
        if (args == null || args.Count == 0 || string.IsNullOrWhiteSpace(args[0]))
            return false;

        string[] parts = args[0].Replace(" ", string.Empty).Replace("<", string.Empty).Replace(">", string.Empty).Split(',');
        if (parts.Length < 3 || !TryParseFinite(parts[0], out float x) || !TryParseFinite(parts[1], out float y) || !TryParseFinite(parts[2], out float z))
        {
            problem = "不是「<x, y, z>」座標";
            return false;
        }
        point = new Vector3(x, y, z);

        if (args.Count > 1 && !string.IsNullOrWhiteSpace(args[1]))
        {
            if (TryParseFinite(args[1].Trim(), out float r) && r > 0f)
                range = r;
            else
                problem = $"的半徑「{args[1]}」不是正數,改用預設 {LootPointDefaultRange} 米";
        }
        return true;
    }

    private static bool TryParseFinite(string s, out float value)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);
}
