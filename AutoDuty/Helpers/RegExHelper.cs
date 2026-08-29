using System.Text.RegularExpressions;

namespace AutoDuty.Helpers
{
    public static partial class RegexHelper
    {
        [GeneratedRegex(@"([^<]*)?(?><?([0-9\. ]*\,[0-9\. ]*\,[0-9\. ]*)>([^<]*)<\/>)?", RegexOptions.CultureInvariant)]
        public static partial Regex ColoredTextRegex();

        /// <summary>
        /// 路徑檔名「(領土ID) 名稱.json」。領土 ID 原本寫死 3~4 位,但載入端
        /// <c>FileHelper.TryGetTerritoryType</c> 用的是 <c>uint.TryParse</c>、位數不限 ⇒ 兩邊不一致,
        /// 通得過載入端的檔名可能在這裡比對失敗。台服 7.20 的 TerritoryType 最大 row id 是 1333、
        /// 可建路徑的副本 territory 落在 142~1303,所以 4 位的上限目前沒有實害,
        /// 但也沒有任何理由留著它,改成位數不限、與載入端對齊。
        /// 擷取群組編號不變(2=領土ID、4=W2W、5=名稱),使用端 ContentPathsManager 照舊。
        /// </summary>
        [GeneratedRegex($@"(\()([0-9]+)(\))( {PathIdentifiers.W2W})?(.*)(\.json)", RegexOptions.CultureInvariant)]
        public static partial Regex PathFileRegex();

        [GeneratedRegex(@"([0-9]{3,})", RegexOptions.CultureInvariant)]
        public static partial Regex ObjectIdRegex();

        [GeneratedRegex(@"""([^""]+)""|\S+", RegexOptions.CultureInvariant)]
        public static partial Regex ArgumentParserRegex();
    }

    public static class PathIdentifiers
    {
        public const string W2W = @"「W2W-まとめ」";
    }
}
