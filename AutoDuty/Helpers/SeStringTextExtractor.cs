using System.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

namespace AutoDuty.Helpers
{
    /// <summary>
    /// 把原生記憶體裡讀來的 <see cref="SeString"/> 攤成「畫面上看得到的那串字」。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>為什麼不能直接 ToString()</b>:那條路(<c>CStringPointer.ToString()</c>／
    /// <c>AtkValue.GetValueAsString()</c>)是把整段位元組當 UTF-8 硬解,SeString 的控制序列
    /// 會原樣留在字串裡 —— 拿去比對名稱永遠對不上,拿去餵「含 U+FFFD 就當視窗記憶體變動中」
    /// 的守衛更會直接誤判成壞掉。
    /// <para>
    /// 🔴 <b>為什麼也不能用 ECommons 的 GetText()</b>:那支<b>只</b>收 <see cref="TextPayload"/>
    /// (外加一個 <c>02 1D 01 03</c> 的特例),會把 <c>02 1F 01 03</c>(<see cref="SeHyphenPayload"/>)
    /// 整個丟掉 —— 而遊戲把副本名裡的破折號就是用這個 payload 送的。
    /// </para>
    /// <para>
    /// 📌 <b>收哪些</b>:<see cref="TextPayload"/> 取其文字;<see cref="SeHyphenPayload"/> 產出
    /// 它自己的 <c>Text</c>(U+2013 EN DASH)。其餘 payload(斜體開關 <c>02 1A …</c>、圖示、顏色、
    /// 自動翻譯…)一律不產出字元。
    /// 這與 QueueHelper 原本那條 <c>Replace</c> 鏈對「純文字／斜體開關／SeHyphen」的結果逐字相同。
    /// </para>
    /// <para>
    /// ⚠️ 這不是 <c>SeString.TextValue</c>:那支會連 <c>NewLinePayload</c> 與
    /// <c>AutoTranslatePayload</c> 一起收進來,對「與既有比對基準逐字相同」是多出來的行為。
    /// </para>
    /// </remarks>
    internal static class SeStringTextExtractor
    {
        /// <summary>把 <paramref name="seString"/> 攤成純文字;<see langword="null"/> 回空字串。</summary>
        internal static string ExtractDisplayText(SeString? seString)
        {
            if (seString == null)
                return string.Empty;

            StringBuilder builder = new();
            foreach (Payload payload in seString.Payloads)
            {
                switch (payload)
                {
                    case TextPayload textPayload:
                        builder.Append(textPayload.Text);
                        break;

                    // 遊戲用這個 payload 送名稱裡的破折號(位元組 02 1F 01 03),它的 Text 就是 U+2013。
                    case SeHyphenPayload hyphenPayload:
                        builder.Append(hyphenPayload.Text);
                        break;
                }
            }

            return builder.ToString();
        }
    }
}
