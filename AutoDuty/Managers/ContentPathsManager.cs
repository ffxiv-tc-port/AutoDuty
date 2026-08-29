using AutoDuty.Helpers;
using AutoDuty.Windows;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.GameFunctions;
using ECommons.Schedulers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoDuty.Managers
{
    using Data;
    using static Data.Classes;

    internal static class ContentPathsManager
    {
        internal static Dictionary<uint, ContentPathContainer> DictionaryPaths = [];

        private static bool invalidCleanupQueued;

        /// <summary>
        /// 排定移除解析失敗的 path。
        /// 舊寫法是在 PathFile getter 的 catch 裡直接 Paths.Remove(this)，而 PathsTab.Draw 當下
        /// 正在 foreach 同一個 list ⇒ 只要有任何一個 path json 壞掉，路徑分頁就會丟
        /// InvalidOperationException（集合已被修改），整份路徑清單當場畫不出來。
        /// 改成先標記，等下一個 tick（不在繪製迴圈裡）再統一移除。
        /// </summary>
        internal static void QueueInvalidPathCleanup()
        {
            if (invalidCleanupQueued)
                return;

            invalidCleanupQueued = true;
            _ = new TickScheduler(RemoveInvalidPaths);
        }

        private static void RemoveInvalidPaths()
        {
            invalidCleanupQueued = false;

            foreach (ContentPathContainer container in DictionaryPaths.Values.ToArray())
                container.Paths.RemoveAll(dutyPath => dutyPath.Invalid);
        }

        private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

        /// <summary>
        /// 組出「新路徑檔」的預設完整路徑,沿用既有路徑檔的命名慣例:「(領土ID) 副本名稱.json」。
        /// 對照 <c>RegexHelper.PathFileRegex()</c> —— 載入時真正被解析的只有括號裡的領土 ID,
        /// 後面那段名稱純粹給人看,所以命名慣例的重點是前綴而不是名稱用哪種語言。
        /// </summary>
        /// <remarks>
        /// 🔴 台服注意:呼叫端傳進來的 <c>Content.EnglishName</c> 在台服**不是英文**。
        /// 它在 <c>ContentHelper.PopulateDuties()</c> 裡是用
        /// <c>GetExcelSheet&lt;ContentFinderCondition&gt;(Language.English)</c> 取的,但本艦隊的
        /// Lumina fork 在 <c>ExcelModule.GetRawSheetCore</c> 開頭就把參數覆寫掉
        /// (<c>language = Language;</c>),語言參數是死參數 ⇒ 台服拿到的仍是繁中表,
        /// <c>EnglishName</c> 的值等同 <c>Name</c>。台服客戶端本身也沒有英文 sqpack 可讀,
        /// 「英文檔名」在台服無法達成,因此這裡刻意保留當地語言的副本名稱,
        /// 只把命名慣例(前綴)與檔名合法性做穩。
        /// 已離線核對台服 ContentFinderCondition 中 353 筆可建路徑的副本名稱:
        /// 沒有任何 Windows 保留字元、也沒有結尾的句點或空白。
        /// </remarks>
        internal static string BuildDefaultPathFilePath(uint territoryType, string? dutyName)
        {
            string name = SanitizeFileNamePart(dutyName);

            if (name.Length == 0)
                name = $"Territory {territoryType}";

            return Path.Combine(Plugin.PathsDirectory.FullName, $"({territoryType}) {name}.json");
        }

        /// <summary>
        /// 把副本名稱清成合法檔名。原本只做 <c>.Replace(":", "")</c>,其餘 Windows 保留字元
        /// (? * " &lt; &gt; | / \)會讓存檔時的 <c>File.WriteAllText</c> 直接擲例外,
        /// 而 BuildTab 的存檔按鈕把例外吞掉只寫 log ⇒ 對使用者表現成「按了存檔沒反應」。
        /// </summary>
        private static string SanitizeFileNamePart(string? dutyName)
        {
            if (string.IsNullOrWhiteSpace(dutyName))
                return string.Empty;

            StringBuilder builder = new(dutyName.Length);

            foreach (char c in dutyName)
            {
                if (c == ':' || char.IsControl(c) || InvalidFileNameChars.Contains(c))
                    continue;

                builder.Append(c);
            }

            // Windows 不接受結尾的句點與空白。
            return builder.ToString().TrimEnd('.', ' ');
        }

        internal class ContentPathContainer
        {
            public ContentPathContainer(Content content)
            {
                Content = content;
                id      = content.TerritoryType;

                ColoredNameString = $"({ImGuiHelper.idColor}{this.id}</>) {ImGuiHelper.dutyColor}{this.Content!.Name}</>";
                ColoredNameRegex  = RegexHelper.ColoredTextRegex().Match(this.ColoredNameString);
            }

            public uint id { get; }

            public Content Content { get; }

            public List<DutyPath> Paths { get; } = [];

            public string ColoredNameString { get; }

            public Match ColoredNameRegex { get; private set; }

            public DutyPath? SelectPath(out int pathIndex, Job? job = null)
            {
                job ??= PlayerHelper.GetJob();

                DutyPath defaultPath = this.Paths[0];

                if (job == null)
                {
                    pathIndex = 0;
                    return defaultPath;
                }

                if (this.Paths.Count > 1)
                {
                    if (Plugin.Configuration.PathSelectionsByPath.TryGetValue(this.Content.TerritoryType, out Dictionary<string, JobWithRole>? jobConfig))
                    {
                        foreach ((string? pathName, JobWithRole pathJobs) in jobConfig)
                        {
                            if (pathJobs.HasJob((Job)job))
                            {
                                int pInx = this.Paths.IndexOf(dp => dp.FileName.Equals(pathName));

                                if (pInx < this.Paths.Count)
                                {
                                    pathIndex = pInx;
                                    return this.Paths[pathIndex];
                                }
                            }
                        }
                    }

                    //temporary while w2w gets integrated
                    if (!defaultPath.W2WFound && Plugin.Configuration.W2WJobs.HasJob(job.Value))
                    {
                        for (int index = 0; index < this.Paths.Count; index++)
                        {
                            string curPath = this.Paths[index].Name;
                            if (curPath.Contains(PathIdentifiers.W2W))
                            {
                                pathIndex = index;
                                return this.Paths[index];
                            }
                        }
                    }
                }

                pathIndex = 0;
                return defaultPath;
            }

            public void AddPath(string name)
            {
                this.Paths.Add(new DutyPath(name, this));
            }
        }

        internal class DutyPath
        {
            public DutyPath(string filePath, ContentPathContainer container)
            {
                FilePath  = filePath;
                FileName  = Path.GetFileName(filePath);
                Name      = FileName.Replace(".json", string.Empty);
                this.container = container;


                UpdateColoredNames();
            }

            public void UpdateColoredNames()
            {
                Match pathMatch = RegexHelper.PathFileRegex().Match(FileName);

                string pathFileColor = Plugin.Configuration.DoNotUpdatePathFiles.Contains(FileName) ? ImGuiHelper.pathFileColorNoUpdate : ImGuiHelper.pathFileColor;
                id = uint.Parse(pathMatch.Groups[2].Value);
                ColoredNameString = pathMatch.Success ?
                                             $"<0.8,0.8,1>{pathMatch.Groups[4]}</>{pathFileColor}{pathMatch.Groups[5]}</>" :
                                             FileName;
                ColoredNameRegex = RegexHelper.ColoredTextRegex().Match(ColoredNameString);
            }

            public readonly ContentPathContainer container;

            public uint id;

            public string Name     { get; }
            public string FileName { get; }
            public string FilePath { get; }

            public  string ColoredNameString { get; private set; } = null!;

            public  Match ColoredNameRegex { get; private set; } = null!;

            private PathFile? pathFile = null;
            public PathFile PathFile
            {
                get
                {
                    if (pathFile == null)
                    {
                        try
                        {
                            RevivalFound = false;
                            W2WFound     = false;

                            string json;

                            using (StreamReader streamReader = new(FilePath, Encoding.UTF8))
                                json = streamReader.ReadToEnd();


                            pathFile = JsonSerializer.Deserialize<PathFile>(json, BuildTab.jsonSerializerOptions);

                            RevivalFound = PathFile.Actions.Any(x => x.Tag.HasFlag(ActionTag.Revival));
                            W2WFound     = PathFile.Actions.Any(x => x.Tag.HasFlag(ActionTag.W2W));
                            
                            /*
                            if (this.pathFile.Meta.LastUpdatedVersion < 189)
                            {

                                pathFile.Meta.Changelog.Add(new PathFileChangelogEntry
                                                            {
                                                                Version = 189,
                                                                Change  = "Adjusted tags to string values"
                                                            });

                                json = JsonSerializer.Serialize(pathFile, BuildTab.jsonSerializerOptions);
                                File.WriteAllText(FilePath, json);
                            }*/
                        }
                        catch (Exception ex)
                        {
                            Svc.Log.Info($"{FilePath} is not a valid duty path: {ex}");
                            MarkInvalid();
                        }
                    }

                    return pathFile!;
                }
            }

            private PathFileMetaData? metaCache;

            /// <summary>
            /// 只給 UI 顯示用的中繼資料（版本、備註）。由背景執行緒預讀，尚未讀到時為 null。
            /// 讀這個屬性不會觸發 <see cref="PathFile"/> 的延遲載入（讀檔 + 反序列化）。
            /// </summary>
            public PathFileMetaData? Meta => this.pathFile?.Meta ?? this.metaCache;

            /// <summary>path json 解析失敗，等 <see cref="QueueInvalidPathCleanup"/> 在繪製迴圈外把它移除。</summary>
            public bool Invalid { get; private set; }

            /// <summary>
            /// 在背景預讀 Meta。只留下中繼資料，Actions 讀完就丟，
            /// 避免為了顯示版本號而把全部 271 個 path 常駐在記憶體裡。
            /// </summary>
            public void PreloadMeta()
            {
                if (this.Invalid || this.pathFile != null || this.metaCache != null)
                    return;

                try
                {
                    string json;

                    using (StreamReader streamReader = new(FilePath, Encoding.UTF8))
                        json = streamReader.ReadToEnd();

                    this.metaCache = JsonSerializer.Deserialize<PathFile>(json, BuildTab.jsonSerializerOptions)?.Meta;
                }
                catch (Exception ex)
                {
                    Svc.Log.Info($"{FilePath} is not a valid duty path: {ex}");
                    MarkInvalid();
                }
            }

            private void MarkInvalid()
            {
                this.Invalid = true;
                QueueInvalidPathCleanup();
            }

            public List<PathAction> Actions      => PathFile.Actions;
            public bool             RevivalFound { get; private set; }
            public bool             W2WFound { get; private set; }
        }
    }

    internal static class ContentPathContainerExtensions
    {
        public static bool IsFirstPath(this ContentPathsManager.ContentPathContainer container, ContentPathsManager.DutyPath dp) => 
            container.Paths[0] == dp;
    }
}
