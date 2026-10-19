using System.Text.RegularExpressions;

namespace AutoDuty.Helpers
{
    public static partial class RegexHelper
    {
        [GeneratedRegex(@"([^<]*)?(?><?([0-9\. ]*\,[0-9\. ]*\,[0-9\. ]*)>([^<]*)<\/>)?", RegexOptions.CultureInvariant)]
        public static partial Regex ColoredTextRegex();

        /// <summary>
        /// 路徑檔名「(領土ID) 名稱.json」。領土 ID 位數不限,與載入端
        /// <c>FileHelper.TryGetTerritoryType</c> 的 <c>uint.TryParse</c> 對齊(寫死位數會讓通得過載入端的檔名在這裡比對失敗)。
        /// 擷取群組編號:2=領土ID、4=W2W、5=名稱,使用端是 ContentPathsManager。
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
