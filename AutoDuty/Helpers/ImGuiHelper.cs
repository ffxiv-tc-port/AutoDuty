using Dalamud.Utility;
using Dalamud.Bindings.ImGui;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AutoDuty.Helpers
{
    using Dalamud.Interface;
    using Dalamud.Interface.Utility.Raii;
    using System.Numerics;
    using static Dalamud.Interface.Utility.Raii.ImRaii;

    internal static class ImGuiHelper
    {
        public static readonly Vector4 ExperimentalColor  = new(1, 0, 1, 1);
        public static readonly Vector4 ExperimentalColor2 = new(1, 0.6f, 0, 1);
        public static readonly Vector4 VersionColor       = new(0, 1, 1, 1);
        public static readonly Vector4 LinkColor          = new(0, 200, 238, 1);

        public static readonly Vector4 White = new(1, 1, 1, 1);
        public static readonly Vector4 MaxLevelColor = new(0.5f, 1, 0.5f, 1);

        public static readonly Vector4 RoleTankColor       = new(0, 0.8f, 1, 1);
        public static readonly Vector4 RoleHealerColor     = new(0, 1, 0, 1);
        public static readonly Vector4 RoleDPSColor        = new(1, 0, 0, 1);
        public static readonly Vector4 RoleAllRounderColor = new(1, 1, 0.5f, 1);


        public const string idColor               = "<0.5,0.5,1>";
        public const string dutyColor             = "<0,1,0>";
        public const string pathFileColor         = "<0.8,0.8,0.8>";
        public const string pathFileColorNoUpdate = "<0,1,1>";

        public static void ColoredText(string text)
        {
            Match regex = RegexHelper.ColoredTextRegex().Match(text);
            ColoredText(regex, text);
        }

        public static void ColoredText(Match regex, string backupText)
        {
            void SameLine() => ImGui.SameLine(0, 0);

            if (regex.Success)
            {
                bool first = true;

                do
                {
                    bool nonColoredSet = false;

                    //Svc.Log.Debug(string.Join(" | ", regex.Groups.Values.Select(g=> g.Value)));

                    string nonColored = regex.Groups[1].Value;
                    if (!nonColored.IsNullOrEmpty())
                    {
                        if (!first)
                            SameLine();

                        first = false;
                        ImGui.Text(nonColored);
                        nonColoredSet = true;
                        //Svc.Log.Debug("non colored: " + nonColored);
                    }

                    string colorText = regex.Groups[2].Value;
                    string coloredText = regex.Groups[3].Value;
                    if (!colorText.IsNullOrEmpty() && !coloredText.IsNullOrEmpty())
                    {
                        string[] split = colorText.Split(',');
                        if (split.Length >= 3)
                        {
                            if (float.TryParse(split[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out float r))
                                if (float.TryParse(split[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out float g))
                                    if (float.TryParse(split[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out float b))
                                    {
                                        float a = 1;
                                        if (split.Length == 4 && float.TryParse(split[3], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out a))
                                        {
                                        }

                                        if (nonColoredSet)
                                            SameLine();
                                        else if (!first)
                                            SameLine();

                                        first = false;

                                        Vector4 color = new(r, g, b, a);
                                        ImGui.TextColored(color, coloredText);

                                        //Svc.Log.Debug("colored: " + coloredText + " in: " + color);
                                    }
                        }
                    }
                    regex = regex.NextMatch();
                } while (regex.Success);
            }
            else
            {
                ImGui.Text(backupText);
            }
        }

        internal static void CenterNextElement(float percentWidth, float xIndent = 0)
        {
            var buttonWidth = ImGui.GetContentRegionAvail().X * percentWidth;
            ImGui.SetCursorPosX(xIndent + (ImGui.GetContentRegionAvail().X - buttonWidth) / 2f);
        }

        internal static void CenterNextElement(float width)
        {
            ImGui.SetCursorPosX((ImGui.GetContentRegionAvail().X - width) / 2f);
        }

        internal static bool CenteredButton(string label, float percentWidth, float xIndent = 0)
        {
            var buttonWidth = ImGui.GetContentRegionAvail().X * percentWidth;
            ImGui.SetCursorPosX(xIndent + (ImGui.GetContentRegionAvail().X - buttonWidth) / 2f);
            return ImGui.Button(label, new(buttonWidth, 35f));
        }
        internal static void DrawIcon(FontAwesomeIcon icon)
        {
            using ImRaii.Font font = ImRaii.PushFont(UiBuilder.IconFont);
            ImGui.SetItemAllowOverlap();
            ImGui.Text(icon.ToIconString());
            ImGui.SameLine();
        }

        /// <summary>
        /// 外觀與 Dalamud <c>ImGuiComponents.HelpMarker</c> 相同的說明圖示，差別是圖示停用時也看得到說明。
        /// </summary>
        /// <remarks>
        /// Dalamud 版判 hover 不帶 <c>AllowWhenDisabled</c>，停用區段裡永遠不顯示。停用區段又把 <c>Style.Alpha</c>
        /// 乘上 <c>DisabledAlpha</c>，所以只在圖示停用時把說明框推回 Alpha 1；沒停用時與 Dalamud 版逐項相同。
        /// </remarks>
        internal static void HelpMarkerAllowWhenDisabled(string helpText)
        {
            ImGui.SameLine();

            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextDisabled(FontAwesomeIcon.InfoCircle.ToIconString());

            if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                return;

            // 預設旗標判不到 hover ⇒ 圖示是停用的。
            bool iconDisabled = !ImGui.IsItemHovered(ImGuiHoveredFlags.None);
            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 1f, iconDisabled))
            using (ImRaii.Tooltip())
            using (ImRaii.TextWrapPos(ImGui.GetFontSize() * 35.0f))
                ImGui.Text(helpText);
        }

        /// <summary>
        /// 分節標題（置中、點一下切換展開的 <c>Selectable</c>）：外層整段停用時標題照樣能展開／收合，標題之後的內容停用層數不變。
        /// </summary>
        /// <remarks>
        /// <c>ImRaii.Enabled()</c> 暫時結束的只有 <c>ImRaii.Disabled</c> 推的層，離開時原數重開；
        /// 直接呼叫 <c>ImGui.BeginDisabled</c> 的層它解不開。展開狀態照舊存在呼叫端傳進來的欄位。
        /// </remarks>
        internal static void SectionHeaderAlwaysEnabled(string label, ref bool open)
        {
            using (ImRaii.Enabled())
            {
                bool clicked;
                using (ImRaii.PushStyle(ImGuiStyleVar.SelectableTextAlign, new Vector2(0.5f, 0.5f)))
                    clicked = ImGui.Selectable(label, open, ImGuiSelectableFlags.DontClosePopups);

                if (ImGui.IsItemHovered())
                    ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

                if (clicked)
                    open = !open;
            }
        }

        /// <summary>
        /// <see cref="SectionHeaderAlwaysEnabled"/> 的 <c>ImGui.CollapsingHeader</c> 版；展開狀態照舊存在 ImGui 自己的 storage。
        /// </summary>
        internal static bool CollapsingHeaderAlwaysEnabled(string label, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
        {
            using (ImRaii.Enabled())
                return ImGui.CollapsingHeader(label, flags);
        }
    }
}
