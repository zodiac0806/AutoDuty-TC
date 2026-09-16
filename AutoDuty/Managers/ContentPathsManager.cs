using AutoDuty.Helpers;
using AutoDuty.Windows;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.GameFunctions;
using ECommons.Schedulers;
using System;
using System.Collections.Concurrent;
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
        /// <summary>
        /// 全部路徑檔依領土 ID 分桶的結果。<b>發布之後就不再就地改動</b>:重載時由
        /// <c>Updater/FileHelper.Update</c> 在區域變數裡建好一份新的,再整份替換掉這個參照。
        /// </summary>
        /// <remarks>
        /// 🔴 <c>volatile</c> 不是裝飾:讀取端有二十來處而且<b>一律不上鎖</b>,其中
        /// <c>IPC.IPCProvider.ContentHasPath</c> 跑在<b>呼叫端外掛的執行緒</b>上,寫入端則是
        /// framework 執行緒。整份替換保證讀到的字典內部是完整的,<c>volatile</c> 再保證
        /// 「看得到新參照」與「看得到那份字典已經建好的內容」這兩件事不會被重排。
        /// ⚠️ <b>不可以</b>改回就地 <c>Clear()</c>／<c>Add()</c> —— 讀取端會撞見清到一半的字典,
        /// 失敗形式不是「拿到舊值」而是字典本身壞掉。
        /// </remarks>
        internal static volatile Dictionary<uint, ContentPathContainer> DictionaryPaths = [];

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
                // Paths 可能是空的:RemoveInvalidPaths() 會把解析失敗的 path 從容器裡拿掉,
                // 但**不會**把因此空掉的容器從 DictionaryPaths 移除 ⇒ 某個副本的路徑檔全部
                // 壞掉時,舊寫法的 this.Paths[0] 會擲 ArgumentOutOfRangeException。
                // 回傳型別本來就是 DutyPath?,這裡把「沒有可選路徑」表達成 null + pathIndex = -1
                // (-1 就是呼叫端既有的「沒有路徑」值,見 AutoDuty.cs 的 RunContext 與 MainTab)。
                if (this.Paths.Count == 0)
                {
                    pathIndex = -1;
                    return null;
                }

                job ??= PlayerHelper.GetJob();

                // 同一副本可能有多個路徑檔(例如舊檔案還沒清掉),this.Paths 的順序來自
                // FileHelper.Update() 掃磁碟的順序(檔案系統列舉順序),跟版本號無關。
                // 沒有 job 專屬設定可套用時,「預設路徑」該選版號最大的那份,而不是掃到的第一個。
                DutyPath defaultPath = GetHighestVersionPath();

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

            /// <summary>
            /// 挑 Paths 裡 Meta.LastUpdatedVersion 最大的那份。呼叫 PreloadMeta() 確保版本號
            /// 讀得到(背景預讀通常已經跑完,但 SelectPath 沒有保證晚於它,所以這裡不能假設
            /// Meta 已經有值);版本號讀不到(檔案壞掉)時當 0,不會蓋過其他正常檔案。
            /// </summary>
            private DutyPath GetHighestVersionPath()
            {
                DutyPath best = this.Paths[0];
                int bestVersion = GetVersion(best);

                for (int i = 1; i < this.Paths.Count; i++)
                {
                    DutyPath candidate = this.Paths[i];
                    int candidateVersion = GetVersion(candidate);

                    if (candidateVersion > bestVersion)
                    {
                        best        = candidate;
                        bestVersion = candidateVersion;
                    }
                }

                return best;

                static int GetVersion(DutyPath path)
                {
                    path.PreloadMeta();
                    return path.Meta?.LastUpdatedVersion ?? 0;
                }
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

            /// <summary>檔名不合慣例時每個檔名只記一行,避免 FileSystemWatcher 連續觸發時洗 log。</summary>
            private static readonly ConcurrentDictionary<string, byte> UnconventionalFileNamesLogged = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// 舊寫法是先 <c>uint.Parse(pathMatch.Groups[2].Value)</c>、之後才看 <c>pathMatch.Success</c>,
            /// 而比對失敗時 <c>Groups[2].Value</c> 是空字串 ⇒ FormatException 直接從 DutyPath 的建構式擲出來,
            /// 把整輪 <c>FileHelper.Update()</c> 打斷 —— 壞掉的不是那一個檔,是整份路徑清單。
            /// 這條路徑真的走得到:載入端 <c>FileHelper.TryGetTerritoryType</c> 只要求「(數字)」開頭、位數不限,
            /// 使用者自己改出來的檔名可以通過載入端的閘門,卻在這裡比對失敗。
            /// 現在改成先判 Success:不合慣例的檔名退回顯示原始檔名,領土 ID 用容器的
            /// (那就是載入端從同一個檔名解出來、用來分桶的值,不會是錯的),並寫一行 Information。
            /// </summary>
            public void UpdateColoredNames()
            {
                Match pathMatch = RegexHelper.PathFileRegex().Match(FileName);

                string pathFileColor = Plugin.Configuration.DoNotUpdatePathFiles.Contains(FileName) ? ImGuiHelper.pathFileColorNoUpdate : ImGuiHelper.pathFileColor;

                if (pathMatch.Success && uint.TryParse(pathMatch.Groups[2].Value, out uint parsedId))
                {
                    id                = parsedId;
                    ColoredNameString = $"<0.8,0.8,1>{pathMatch.Groups[4]}</>{pathFileColor}{pathMatch.Groups[5]}</>";
                }
                else
                {
                    id                = this.container.id;
                    ColoredNameString = FileName;

                    if (UnconventionalFileNamesLogged.TryAdd(FileName, 0))
                        Svc.Log.Information($"AutoDuty: path file '{FileName}' does not match the '(territoryId) name.json' naming convention; using territory {this.container.id} and showing the raw file name.");
                }

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

                    // 解析失敗時 pathFile 仍然是 null —— 舊寫法的 `pathFile!` 只是用驚嘆號把
                    // 「這裡不會是 null」宣告掉,實際回傳的就是 null,於是
                    // Actions => PathFile.Actions 以及呼叫端的 PathFile.Meta 全部直接 NRE。
                    // 改成退回一個空的 PathFile(Actions 是空清單、Meta 有預設值):
                    // 讀的人拿到「這個路徑沒有動作」而不是崩潰,而這個 DutyPath 已在上面被
                    // MarkInvalid() 標記,下一個 tick 就會被 RemoveInvalidPaths 移除。
                    // 🔴 刻意每次都 new 一個,不共用靜態實例:BuildTab 的存檔流程會拿到這個物件
                    //    並就地寫入 Actions(pathFile.Actions = [.. Plugin.Actions]),
                    //    共用實例會被寫髒並汙染其他路徑。
                    return pathFile ?? new Classes.PathFile();
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
        /// <summary>
        /// 容器可能是空的(RemoveInvalidPaths 把解析失敗的 path 拿掉之後,空掉的容器不會從
        /// DictionaryPaths 移除),舊寫法的 Paths[0] 會擲 ArgumentOutOfRangeException。
        /// 🔴 唯一的呼叫點 PathsTab.Draw() 在該檔既有 try/catch 的**範圍之外**,例外會一路
        ///    逃到 Dalamud 的 Window.Draw() —— 10 秒內兩次就會被自動重試永久關掉,
        ///    使用者看到的是「路徑分頁整個消失」。
        /// 空容器沒有「第一個路徑」,回 false 是正確答案。
        /// </summary>
        public static bool IsFirstPath(this ContentPathsManager.ContentPathContainer container, ContentPathsManager.DutyPath dp) =>
            container.Paths.Count > 0 && container.Paths[0] == dp;
    }
}
