global using static AutoDuty.Data.Enums;
global using static AutoDuty.Data.Extensions;
global using static AutoDuty.Data.Classes;
global using static AutoDuty.AutoDuty;
global using AutoDuty.Managers;
global using ECommons.GameHelpers;
using System;
using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using System.Collections.Generic;
using System.IO;
using ECommons;
using ECommons.DalamudServices;
using ECommons.LanguageHelpers;
using AutoDuty.Windows;
using AutoDuty.IPC;
using AutoDuty.External;
using AutoDuty.Helpers;
using ECommons.Throttlers;
using Dalamud.Game.ClientState.Objects.Types;
using System.Linq;
using ECommons.GameFunctions;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Client.Game;
using Dalamud.Bindings.ImGui;
using ECommons.ExcelServices;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Dalamud.IoC;
using System.Diagnostics;
using Dalamud.Game.ClientState.Conditions;
using AutoDuty.Properties;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using AutoDuty.Updater;

namespace AutoDuty;

using System.Text.RegularExpressions;
using Dalamud.Utility.Numerics;
using Data;
using ECommons.Configuration;
using ECommons.SimpleGui;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Lumina.Excel.Sheets;
using Pictomancy;
using static Data.Classes;
using TaskManager = ECommons.Automation.LegacyTaskManager.TaskManager;

// TODO:
// Scrapped interable list, going to implement an internal list that when a interactable step end in fail, the Dataid gets add to the list and is scanned for from there on out, if found we goto it and get it, then remove from list.
// Need to expand AutoRepair to include check for level and stuff to see if you are eligible for self repair. and check for dark matter
// make config saving per character
// drap drop on build is jacked when theres scrolling

// WISHLIST for VBM:
// Generic (Non Module) jousting respects navmesh out of bounds (or dynamically just adds forbiddenzones as Obstacles using Detour) (or at very least, vbm NavigationDecision can use ClosestPointonMesh in it's decision making) (or just spit balling here as no idea if even possible, add Everywhere non tiled as ForbiddenZones /shrug)

public sealed partial class AutoDuty : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    internal List<PathAction> Actions { get; set; } = [];
    internal int CurrentLoop = 0;
    internal KeyValuePair<ushort, Job?> CurrentPlayerItemLevelandClassJob = new(0, null);
    private Content? currentTerritoryContent = null;
    internal Content? CurrentTerritoryContent
    {
        get => currentTerritoryContent;
        set
        {
            CurrentPlayerItemLevelandClassJob = PlayerHelper.IsValid ? new(InventoryHelper.CurrentItemLevel, Player.Job) : new(0, null);
            currentTerritoryContent = value;
        }
    }
    internal uint CurrentTerritoryType = 0;
    internal int CurrentPath = -1;

    internal bool SupportLevelingEnabled => LevelingModeEnum == LevelingMode.Support;
    internal bool TrustLevelingEnabled => LevelingModeEnum == LevelingMode.Trust;
    internal bool LevelingEnabled => LevelingModeEnum == LevelingMode.Support || LevelingModeEnum == LevelingMode.Trust;

    internal static string Name => "AutoDuty";
    internal static AutoDuty Plugin { get; private set; }
    internal bool StopForCombat = true;
    internal DirectoryInfo PathsDirectory;
    internal FileInfo AssemblyFileInfo;
    internal FileInfo ConfigFile;
    internal DirectoryInfo? DalamudDirectory;
    internal DirectoryInfo? AssemblyDirectoryInfo;

    internal Configuration Configuration => ConfigurationMain.Instance.GetCurrentConfig;

    public int EffectiveLoopTimes => GetEffectiveLoopTimes();
    public bool PlannerActive => PlannerEnabled;
    internal RunContext? ActiveRunContext = null;
    internal WindowSystem WindowSystem = new("AutoDuty");

    public   int   Version { get; set; }
    internal Stage PreviousStage = Stage.Stopped;
    internal Stage Stage
    {
        get => _stage;
        set
        {
            switch (value)
            {
                case Stage.Stopped:
                    // 必須在 StopAndResetALL() 之前:它結尾會把 Action 清空,
                    // 而通知要拿 Action 當「停在哪一步」的佐證。
                    NotifyIfAutomationStoppedItself();
                    StopAndResetALL();
                    break;
                case Stage.Paused:
                    // 已在暫停中再暫停一次：PreviousStage 會被記成 Paused、繼續之後永遠停在暫停。
                    if (_stage == Stage.Paused)
                        break;
                    PreviousStage = Stage;
                    StuckEscalationHelper.Reset();
                    // Path.Stop 只清已在走的路徑點;還在算的那一筆算完會照樣開走,所以先取消尋路。
                    // 閒置時(States 為 None)尋路與路徑都是別的插件的,暫停不碰。
                    bool ownsNavigation = States != PluginState.None;
                    if (ownsNavigation)
                    {
                        VNavmesh_IPCSubscriber.Nav_PathfindCancelAll();
                        if (VNavmesh_IPCSubscriber.Path_NumWaypoints() > 0)
                            VNavmesh_IPCSubscriber.Path_Stop();
                    }
                    FollowHelper.SetFollow(null);
                    TaskManager.SetStepMode(true);
                    States |= PluginState.Paused;
                    break;
                case Stage.Action:
                    ActionInvoke();
                    break;
                case Stage.Condition:
                {
                    Action = $"ConditionChange";
                    // 傳送途中停過（即使又重新開始）的那一筆，傳送結束時不可再把 Stage 改回 Reading_Path。
                    int generation = StopGeneration;
                    SchedulerHelper.ScheduleAction("ConditionChangeStageReadingPath", () => { if (generation == StopGeneration) _stage = Stage.Reading_Path; }, () => !Svc.Condition[ConditionFlag.BetweenAreas] && !Svc.Condition[ConditionFlag.BetweenAreas51] && !Svc.Condition[ConditionFlag.Jumping61]);
                    break;
                }
                case Stage.Waiting_For_Combat:
                    BossMod_IPCSubscriber.SetRange(Plugin.Configuration.MaxDistanceToTargetFloat);
                    break;
            }
            _stage = value;
            Svc.Log.Debug($"Stage={_stage.ToCustomString()}");
        }
    }

    /// <summary>從暫停回到原本的流程；所有「繼續」入口都走這裡，不在暫停中時不做任何事。</summary>
    /// <remarks>
    /// 暫停時已取消尋路並停下路徑，原本在 Moving 的話直接回 Moving 會因為沒有路徑點被當成「已走到」
    /// （跳過這一步或在原地執行動作）⇒ 改回 Reading_Path、Indexer 不動，從目前位置重送同一步。
    /// </remarks>
    internal void Resume()
    {
        if (Stage != Stage.Paused)
            return;

        Stage resumeTo = PreviousStage == Stage.Moving ? Stage.Reading_Path : PreviousStage;
        if (resumeTo != PreviousStage)
            Svc.Log.Information($"[繼續] 暫停前正在移動：從目前位置重送第 {Indexer} 步（{(Indexer >= 0 && Indexer < Actions.Count ? Actions[Indexer].Name : "?")}）。");

        TaskManager.SetStepMode(false);
        Stage  =  resumeTo;
        States &= ~PluginState.Paused;
    }
    internal LevelingMode LevelingModeEnum
    {
        get => levelingModeEnum;
        set
        {
            if (value == LevelingMode.Manual)
            {
                // Manual mode should not auto-pick a duty.
                levelingModeEnum = LevelingMode.Manual;
                MainTab.DutySelected = null;
                MainTab.SelectedDuty = null;
                MainTab.SelectedPath = -1;
                MainListClicked = false;
                CurrentTerritoryContent = null;
                CurrentPath = -1;
                return;
            }

            if (value != LevelingMode.None)
            {
                Svc.Log.Debug($"Setting Leveling mode to {value}");
                Content? duty = LevelingHelper.SelectHighestLevelingRelevantDuty(value == LevelingMode.Trust);

                if (duty != null)
                {
                    levelingModeEnum = value;
                    MainTab.DutySelected = ContentPathsManager.DictionaryPaths[duty.TerritoryType];
                    CurrentTerritoryContent = duty;
                    MainTab.DutySelected.SelectPath(out CurrentPath);
                    // Keep manual selection state isolated from auto-leveling selection.
                    MainTab.SelectedDuty = null;
                    MainTab.SelectedPath = -1;
                    Svc.Log.Debug($"Leveling Mode: Setting duty to {duty.Name}");
                }
                else
                {
                    MainTab.DutySelected = null;
                    MainTab.SelectedDuty = null;
                    MainTab.SelectedPath = -1;
                    MainListClicked = false;
                    CurrentTerritoryContent = null;
                    CurrentPath = -1;
                    levelingModeEnum = LevelingMode.None;
                    Svc.Log.Debug($"Leveling Mode: No appropriate leveling duty found");
                }
            }
            else
            {
                MainTab.DutySelected = null;
                MainTab.SelectedDuty = null;
                MainTab.SelectedPath = -1;
                MainListClicked = false;
                CurrentTerritoryContent = null;
                CurrentPath = -1;
                levelingModeEnum = LevelingMode.None;
            }
        }
    }
    internal PluginState States = PluginState.None;
    internal int Indexer = -1;
    internal bool MainListClicked = false;
    // ⚠️ 不要把 IGameObject 存進欄位跨幀用:包裝是共用的、位址會被就地改寫,IsValid() 只檢查有沒有登入。
    // BossObject 會跨很多幀存活(ActionsManager.BossMoveCheck 是 TaskManager 的檢查式,會反覆重跑),
    // 王在分階段消失/重生、團滅或離開副本時就是攔不到的 AccessViolation。
    // 所以只存 GameObjectId、每次讀取時重查物件表:查不到就走 null 分支而不是崩潰。
    private ulong? bossObjectId = null;

    internal IBattleChara? BossObject
    {
        get => this.bossObjectId is null ? null : Svc.Objects.SearchById(this.bossObjectId.Value) as IBattleChara;
        set => this.bossObjectId = value?.GameObjectId;
    }
    internal static IGameObject? ClosestObject => Svc.Objects.Where(o => o.IsTargetable && o.ObjectKind.EqualsAny(Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj, Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)).OrderBy(ObjectHelper.GetDistanceToPlayer).TryGetFirst(out var gameObject) ? gameObject : null;
    internal OverrideCamera OverrideCamera;
    internal MainWindow MainWindow { get; init; }
    internal Overlay Overlay { get; init; }
    internal bool InDungeon => ContentHelper.DictionaryContent.ContainsKey(Svc.ClientState.TerritoryType);
    internal bool SkipTreasureCoffer = false;

    /// <summary>
    /// 多變迷宮(Variant Dungeon)目前走到的分歧編號,給
    /// <see cref="Data.PathActionConditionVariantPath"/> 判斷步驟該不該執行。
    /// 目前沒有任何程式碼會改變它:上游由 VariantVote 動作在投票時寫入,而我方刻意不移植 ——
    /// 它的唯一消費者 (1315) The Merchant's Tale 台服沒有。所以它恆為 0,只有 pathIndices 含 0 的條件會成立。
    /// 等台服實裝該副本(判準:CFC 表出現指向 territory 1315 的列)再移植。
    /// </summary>
    internal byte VariantPath = 0;

    internal string Action = "";
    internal string PathFile = "";

    /// <summary>
    /// 目前這次 Wait 等待的結束時刻(<see cref="Environment.TickCount64"/> 基準)。0 = 沒有在等。
    /// 由 <c>ActionsManager.Wait</c> 寫入 —— 為什麼不直接用 EzThrottler 反推,見那裡的說明。
    /// </summary>
    internal long WaitStepEndTick;

    /// <summary>目前這次 Wait 等待原本設定的毫秒數。0 = 沒有在等。</summary>
    internal int WaitStepDurationMs;

    internal void ClearWaitStepTiming()
    {
        WaitStepEndTick    = 0;
        WaitStepDurationMs = 0;
    }

    internal TaskManager TaskManager;
    internal Job JobLastKnown;
    internal DutyState DutyState = DutyState.None;
    internal PathAction PathAction = new();
    internal List<Data.Classes.LogMessage> DalamudLogEntries = [];
    private LevelingMode levelingModeEnum = LevelingMode.None;
    private Stage _stage = Stage.Stopped;

    /// <summary>
    /// 下一次 Stage 被設成 Stage.Stopped 時的「自己停下來」原因。
    /// null 代表那次停止是使用者主動要求的(按鈕 / /autoduty stop / 外部 IPC),不通知。
    /// 這是白名單:只有 MarkSelfStop 會填它。將來新增停止點若忘了呼叫,
    /// 預設結果是「不通知」而不是「誤報」。
    /// </summary>
    private string? _pendingStopReason;

    /// <summary>
    /// 那次停止的完成音效是不是已經由既有的終止流程(LoopsCompleteActions)排過了。
    /// 用來避免正常跑完的路徑響兩次。
    /// </summary>
    private bool _pendingStopSoundHandled;

    /// <summary>那次自己停下是「卡住、要人來處理」：塔塔露念「需要幫忙」而不是「跑本停止」。</summary>
    private bool _pendingStopNeedsHelp;

    /// <summary>這一場副本玩家已死幾次。只在框架執行緒讀寫；歸零點是 Run() 與 StartNavigation()。</summary>
    private int _deathsThisDuty;

    /// <summary>每停下一次（StopAndResetALL 第一行）加一；延遲回呼捕捉它，觸發時對不上＝停下之前排的，不可再動作。只在框架執行緒讀寫。</summary>
    internal int StopGeneration;
    private const string CommandName = "/autoduty";
    private readonly DirectoryInfo _configDirectory;
    private readonly ActionsManager _actions;

    /// <summary>
    /// 解限模式「交戰中繼續走到定點」目前有沒有把 BossMod 的自動移動關掉。
    /// 只在進出戰鬥的那一次送 IPC,不要每幀送。
    /// </summary>
    private bool _unsyncedKeepMovingArmed;

    /// <summary>戰鬥等待中「這次脫戰」開始的時刻（TickCount64）；null＝還在戰鬥中。</summary>
    private long? _combatCalmSince;

    /// <summary>戰鬥旗標連打時會短暫掉下，要連續脫戰這麼久才算打完。</summary>
    private const long CombatSettleMs = 2000;
    private readonly SquadronManager _squadronManager;
    private readonly VariantManager _variantManager;
    private readonly OverrideAFK _overrideAFK;
    private readonly IPCProvider _ipcProvider;
    private IGameObject? treasureCofferGameObject = null;
    private         bool           _recentlyWatchedCutscene = false;
    private         bool           _lootTreasure;
    private         SettingsActive _settingsActive         = SettingsActive.None;
    private         SettingsActive _bareModeSettingsActive = SettingsActive.None;
    private static readonly string[] BareModeFieldNames =
        [nameof(Configuration.EnablePreLoopActions), nameof(Configuration.EnableBetweenLoopActions), nameof(Configuration.EnableTerminationActions)];
    private         DateTime       _lastRotationSetTime    = DateTime.MinValue;
    /// <summary>最後一次過了設定閘門的 <see cref="SetRotationPluginSettings"/> 是「開」：輪替插件可能是 AutoDuty 打開的。</summary>
    private         bool           _rotationEngaged;
    public readonly bool           isDev;

    private const string PandoraChestsFeature = "Automatically Open Chests";
    /// <summary>本次開跑第一次寫入前 PandorasBox「自動開寶箱」的值；null＝本次沒寫過。停止時讀一次就清。</summary>
    private bool? _pandoraChestsOriginal;
    private bool  _pandoraChestsLastWritten;

    /// <summary><see cref="Dispose"/> 開始後為真；讓停止收尾分辨「卸載」與一般停止。</summary>
    internal bool Disposing { get; private set; }

    public AutoDuty()
    {
        try
        {
            Plugin = this;
            ECommonsMain.Init(PluginInterface, Plugin, Module.DalamudReflector, Module.ObjectFunctions);
            // 讓「呼叫了對方沒有的 IPC 方法」不再完全靜默。
            // 訂閱越早越好：事件只在 IPC **呼叫**當下才被查閱，在這裡訂閱就涵蓋往後所有呼叫。
            EzIpcFailureLog.Enable();
            IPCSubscriber_Common.WatchInstalledPlugins();

            // ECommons.IPC 的 IPCBase 預設 wrapper 是 SafeWrapper.None(例外直接往外擲)，
            // 而且它是 lazy 單例、在第一次存取當下就烘死。AutoDuty 的門面類一律自己 new 並
            // 把 wrapper 當建構式參數傳進去,所以不依賴這個值;這裡設成我方最常用的
            // IPCException,是為了「萬一有人改用 ECommonsIPC.X 單例」時不會退回會擲例外的 None。
            ECommons.IPC.Subscribers.IPCBase.DefaultWrapper = ECommons.EzIpcManager.SafeWrapper.IPCException;

            // WrathCombo.API 若沒初始化,第一次呼叫會擲 UninitializedException;它也能自己從
            // ECommons 反射拿 PluginInterface,但那條路失敗時是靜默回 null,所以明確傳進去。
            // 🔴 不加任何 ErrorType 抑制:讓它照常擲例外,由 Wrath_IPCSubscriber.WrathSafe
            // 接住並轉交 EzIpcFailureLog —— 抑制掉的話 Wrath IPC 失敗會完全沒有 log。
            WrathCombo.API.WrathIPCWrapper.Init(PluginInterface);

            ECommons.LanguageHelpers.Localization.Init("ChineseTraditional");
            PictoService.Initialize(PluginInterface);

            this.isDev = PluginInterface.IsDev;

            //EzConfig.Init<ConfigurationMain>();
            EzConfig.DefaultSerializationFactory = new AutoDutySerializationFactory();
            (ConfigurationMain.Instance = EzConfig.Init<ConfigurationMain>()).Init();



            //Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            
            ConfigTab.BuildManuals();
            _configDirectory = PluginInterface.ConfigDirectory;
            ConfigFile = PluginInterface.ConfigFile;
            DalamudDirectory = ConfigFile.Directory?.Parent;
            PathsDirectory = new(_configDirectory.FullName + "/paths");
            AssemblyFileInfo = PluginInterface.AssemblyLocation;
            AssemblyDirectoryInfo = AssemblyFileInfo.Directory;
            
            Version =
                ((PluginInterface.IsTesting
                      ? PluginInterface.Manifest.TestingAssemblyVersion ?? PluginInterface.Manifest.AssemblyVersion
                      : PluginInterface.Manifest.AssemblyVersion)!).Revision;

            if (!_configDirectory.Exists)
                _configDirectory.Create();
            if (!PathsDirectory.Exists)
                PathsDirectory.Create();

            TaskManager = new()
            {
                AbortOnTimeout = false,

                // 🔴 刻意 false:true 時 ECommons 的 LegacyTaskManager 把逾時訊息從 Warning 導去 Verbose(實機 log 收不到),
                //    配上 AbortOnTimeout=false(逾時不中止、直接放行往下跑),症狀就是王戰整段被跳過、寶箱沒撿,
                //    而使用者看不到任何訊息。TaskTimeoutWatcher 另外在 UI 上累計次數。
                TimeoutSilently = false
            };

            TrustHelper.PopulateTrustMembers();
            Svc.Data.GameData.Options.PanicOnSheetChecksumMismatch = false; // TODO: remove - temporary workaround until lumina is updated
            ContentHelper.PopulateDuties();
            Svc.Data.GameData.Options.PanicOnSheetChecksumMismatch = true; // TODO: remove - temporary workaround until lumina is updated
            RepairNPCHelper.PopulateRepairNPCs();
            FileHelper.Init();
            Patcher.Patch(startup: true);

            _overrideAFK = new();
            _ipcProvider = new();
            _squadronManager = new(TaskManager);
            _variantManager = new(TaskManager);
            _actions = new(Plugin, TaskManager);
            BuildTab.ActionsList = _actions.ActionsList;
            OverrideCamera = new();
            Overlay = new();
            MainWindow = new();
            WindowSystem.AddWindow(MainWindow);
            WindowSystem.AddWindow(Overlay);

            if (Svc.ClientState.IsLoggedIn) 
                this.ClientStateOnLogin();
             
            Svc.Commands.AddHandler("/ad", new CommandInfo(OnCommand) { });
            Svc.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "\n/autoduty or /ad -> opens main window\n".Loc() +
                "/autoduty or /ad config or cfg -> opens config window / modifies config\n".Loc() +
                "/autoduty or /ad start -> starts autoduty when in a Duty\n".Loc() +
                "/autoduty or /ad stop -> stops everything\n".Loc() +
                "/autoduty or /ad pause -> pause route\n".Loc() +
                "/autoduty or /ad resume -> resume route\n".Loc() +
                "/autoduty or /ad turnin -> GC Turnin\n".Loc() +
                "/autoduty or /ad desynth -> Desynth's your inventory\n".Loc() +
                "/autoduty or /ad repair -> Repairs your gear\n".Loc() +
                "/autoduty or /ad equiprec-> Equips recommended gear\n".Loc() +
                "/autoduty or /ad extract -> Extract's materia from equipment\n".Loc() +
                "/autoduty or /ad turnin -> GC Turnin\n".Loc() +
                "/autoduty or /ad goto -> goes to\n".Loc() +
                "/autoduty or /ad dataid -> Logs and copies your target's dataid to clipboard\n".Loc() +
                "/autoduty or /ad exitduty -> exits duty\n".Loc() +
                "/autoduty or /ad queue -> queues duty\n".Loc() +
                "/autoduty or /ad moveto -> move's to territorytype and location sent\n".Loc() +
                "/autoduty or /ad overlay -> opens overlay\n".Loc() +
                "/autoduty or /ad overlay lock-> toggles locking the overlay\n".Loc() +
                "/autoduty or /ad overlay nobg-> toggles the overlay's background\n".Loc() +
                "/autoduty or /ad movetoflag -> moves to the flag map marker\n".Loc() +
                "/autoduty or /ad run -> starts auto duty in territory type specified\n".Loc() +
                "/autoduty or /ad tt -> logs and copies to clipboard the Territory Type number for duty specified\n".Loc()
            });

            PluginInterface.UiBuilder.Draw += DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUI;
            PluginInterface.UiBuilder.OpenMainUi += OpenMainUI;

            Svc.Framework.Update += Framework_Update;
            Svc.Framework.Update += SchedulerHelper.ScheduleInvoker;
            Svc.ClientState.TerritoryChanged += ClientState_TerritoryChanged;
            Svc.ClientState.Login += ClientStateOnLogin;
            Svc.Condition.ConditionChange += Condition_ConditionChange;
            Svc.DutyState.DutyStarted += DutyState_DutyStarted;
            Svc.DutyState.DutyWiped += DutyState_DutyWiped;
            Svc.DutyState.DutyRecommenced += DutyState_DutyRecommenced;
            Svc.DutyState.DutyCompleted += DutyState_DutyCompleted;
            // 刻意不在這裡設外掛自訂記錄等級(設成 Debug 沒有實際作用);
            // 要看 Debug 內容要到 /xllog 調整全域等級。
            PluginInterface.UiBuilder.Draw += UiBuilderOnDraw;
        }
        catch (Exception e)
        {
            Svc.Log.Info($"Failed loading plugin\n{e}");
        }
    }

    private void ClientStateOnLogin()
    {
        ConfigurationMain.Instance.SetProfileToDefault();

        Svc.Framework.RunOnTick(() =>
                                {
                                    if (Configuration.ShowOverlay && (!Configuration.HideOverlayWhenStopped || States.HasFlag(PluginState.Looping) || States.HasFlag(PluginState.Navigating)))
                                        SchedulerHelper.ScheduleAction("ShowOverlay", () => Overlay.IsOpen = true, () => PlayerHelper.IsReady);

                                    if (Configuration.ShowMainWindowOnStartup)
                                        SchedulerHelper.ScheduleAction("ShowMainWindowOnStartup", () => OpenMainUI(), () => PlayerHelper.IsReady);
                                });
    }

    private void UiBuilderOnDraw()
    {
        if (PlayerHelper.IsValid)
        {
            // 沒有東西要畫就不開 Pictomancy 的繪製框：開了即使什麼都沒加，
            // 它也會跑完整輪離屏繪製、再疊一張全透明的全螢幕貼圖。
            if (!BuildTab.DrawHelperWanted && !this.PathDrawWanted())
                return;

            using PctDrawList? drawList = PictoService.Draw();

            if (drawList != null)
            {
                BuildTab.DrawHelper(drawList);

                if (this.PathDrawWanted())
                {
                    Vector3 lastPos         = Player.Position;
                    float   stepCountFactor = (1f / this.Configuration.PathDrawStepCount);

                    for (int index = Math.Clamp(this.Indexer, 0, this.Actions.Count-1); index < this.Actions.Count; index++)
                    {
                        PathAction action = this.Actions[index];
                        if (action.Position.LengthSquared() > 1)
                        {
                            float alpha = MathF.Max(0f, 1f - (index - this.Indexer) * stepCountFactor);

                            if (alpha > 0)
                            {
                                drawList.AddCircle(action.Position, 3, ImGui.GetColorU32(new Vector4(1f, 0.2f, 0f, alpha)), 0, 3);

                                if (index > 0)
                                    drawList.AddLine(lastPos, action.Position, 0f, ImGui.GetColorU32(new Vector4(0.8f, 0.8f, 0.8f, alpha)));
                                if (index == this.Indexer)
                                    drawList.AddLine(Player.Position, action.Position, 0, 0x00FFFFFF);

                                drawList.AddText(action.Position, ImGui.GetColorU32(new Vector4(alpha + 0.25f)), index.ToString(), 20f);
                            }

                            lastPos = action.Position;
                        }
                    }
                }
            }
        }
    }

    /// <summary>這一幀要不要畫「接下來幾步」的路線提示。只讀狀態、沒有副作用，一幀內重複呼叫結果相同。</summary>
    private bool PathDrawWanted() =>
        Plugin.Configuration.PathDrawEnabled && CurrentTerritoryContent?.TerritoryType == Svc.ClientState.TerritoryType && this.Actions.Any() && (this.Indexer < 0 || this.Indexer >= this.Actions.Count || !this.Actions[this.Indexer].Name.Equals("Boss") || Stage != Stage.Action);

    private void DutyState_DutyStarted(object? sender, ushort e) => DutyState = DutyState.DutyStarted;
    private void DutyState_DutyWiped(object? sender, ushort e) => DutyState = DutyState.DutyWiped;
    private void DutyState_DutyRecommenced(object? sender, ushort e) => DutyState = DutyState.DutyRecommenced;
    private void DutyState_DutyCompleted(object? sender, ushort e)
    {
        DutyState = DutyState.DutyComplete;
        // 已停止＝使用者自己打完的副本：不收尾、不碰別的插件。只記下副本狀態，之後若在副本裡按開始仍讀得到。
        if (Stage == Stage.Stopped)
            return;
        PlannerOnDutyCompleted();
        this.CheckFinishing();
    }

    private DateTime _plannerLastDutyCompletedAtUtc = DateTime.MinValue;
    private uint _plannerLastDutyCompletedTerritoryType;

    internal void ExitDuty() => _actions.ExitDuty(new());

    internal void LoadPath()
    {
        try
        {
            if (CurrentTerritoryContent == null || (CurrentTerritoryContent != null && CurrentTerritoryContent.TerritoryType != Svc.ClientState.TerritoryType))
            {
                if (ContentHelper.DictionaryContent.TryGetValue(Svc.ClientState.TerritoryType, out var content))
                    CurrentTerritoryContent = content;
                else
                {
                    Actions.Clear();
                    PathFile = "";
                    return;
                }
            }

            Actions.Clear();
            if (!ContentPathsManager.DictionaryPaths.TryGetValue(Svc.ClientState.TerritoryType, out ContentPathsManager.ContentPathContainer? container))
            {
                PathFile = ContentPathsManager.BuildDefaultPathFilePath(Svc.ClientState.TerritoryType, CurrentTerritoryContent?.EnglishName);
                return;
            }

            // container.Paths 可能是空的,CurrentPath 也可能指到已經被 RemoveInvalidPaths 拿掉的索引。
            // 索引無效時就重新選一次:SelectPath 會一併把有效的索引寫回 CurrentPath,容器是空的時候它回 null
            // 並把 CurrentPath 設成 -1,也就是這個檔案既有的「沒有路徑」值(見 RunContext.PathIndex 與 MainTab)。
            ContentPathsManager.DutyPath? path = CurrentPath >= 0 && CurrentPath < container.Paths.Count ?
                                                     container.Paths[CurrentPath] :
                                                     container.SelectPath(out CurrentPath);

            PathFile = path?.FilePath ?? "";
            // path 可能是 null(SelectPath 在容器沒有任何可用路徑時回 null):空路徑要退回空清單,不對 null 做展開。
            Actions = path == null ? [] : [.. path.Actions];
            //Svc.Log.Info($"Loading Path: {CurrentPath} {ListBoxPOSText.Count}");
        }
        catch (Exception e)
        {
            Svc.Log.Error(e.ToString());
            //throw;
        }
    }

    private unsafe bool StopLoop
    {
        get
        {
            // AgentHUD.Instance() 在 UIModule/代理人尚未建立時會回 null。
            // 取不到就讓「無休息經驗」這條停止條件不成立 —— 不能拿未知資料去觸發「停止循環」。
            AgentHUD* agentHud = AgentHUD.Instance();

            return Configuration.EnableTerminationActions &&
                                        (CurrentTerritoryContent == null ||
                                        (Configuration.StopLevel && Player.Level >= Configuration.StopLevelInt) ||
                                        (Configuration.StopNoRestedXP && agentHud != null && agentHud->ExpRestedExperience == 0) ||
                                        (Configuration.StopItemQty && (Configuration.StopItemAll
                                            ? Configuration.StopItemQtyItemDictionary.All(x => InventoryManager.Instance()->GetInventoryItemCount(x.Key) >= x.Value.Value)
                                            : Configuration.StopItemQtyItemDictionary.Any(x => InventoryManager.Instance()->GetInventoryItemCount(x.Key) >= x.Value.Value))));
        }
    }

    private void ClientState_TerritoryChanged(ushort t)
    {
        if (Stage == Stage.Stopped) return;

        BossMod_IPCSubscriber.RestoreBmrAIEnabled(t);

        Svc.Log.Debug($"ClientState_TerritoryChanged: t={t}");

        CurrentTerritoryType         = t;
        MainListClicked              = false;
        this.Framework_Update_InDuty = _ => { };
        if (t == 0)
            return;
        CurrentPath = -1;

        LoadPath();

        if (!States.HasFlag(PluginState.Looping) || GCTurninHelper.State == ActionState.Running || RepairHelper.State == ActionState.Running || GotoHelper.State == ActionState.Running || GotoInnHelper.State == ActionState.Running || GotoBarracksHelper.State == ActionState.Running || GotoHousingHelper.State == ActionState.Running || CurrentTerritoryContent == null)
        {
            Svc.Log.Debug("We Changed Territories but are doing after loop actions or not running at all or in a Territory not supported by AutoDuty");
            return;
        }

        if (Configuration.ShowOverlay && Configuration.HideOverlayWhenStopped && !States.HasFlag(PluginState.Looping))
        {
            Overlay.IsOpen = false;
            MainWindow.IsOpen = true;
        }

        Action = "";

        if (t != CurrentTerritoryContent.TerritoryType)
        {
            if (CurrentLoop < GetEffectiveLoopTimes())
            {
                TaskManager.Abort();
                TaskManager.Enqueue(() => Svc.Log.Debug($"Loop {CurrentLoop} of {GetEffectiveLoopTimes()}"), "Loop-Debug");
                TaskManager.Enqueue(() => { Stage = Stage.Looping; }, "Loop-SetStage=99");
                TaskManager.Enqueue(() => { States &= ~PluginState.Navigating; }, "Loop-RemoveNavigationState");
                TaskManager.Enqueue(() => PlayerHelper.IsReady, int.MaxValue, "Loop-WaitPlayerReady");
                if (Configuration.EnableBetweenLoopActions)
                {
                    TaskManager.Enqueue(() => { Action = $"Waiting {Configuration.WaitTimeBeforeAfterLoopActions}s"; }, "Loop-WaitTimeBeforeAfterLoopActionsActionSet");
                    TaskManager.Enqueue(() => EzThrottler.Throttle("Loop-WaitTimeBeforeAfterLoopActions", Configuration.WaitTimeBeforeAfterLoopActions * 1000), "Loop-WaitTimeBeforeAfterLoopActionsThrottle");
                    TaskManager.Enqueue(() => EzThrottler.Check("Loop-WaitTimeBeforeAfterLoopActions"), Configuration.WaitTimeBeforeAfterLoopActions * 1000 + ActionsManager.ThrottleTimeoutMarginMs, "Loop-WaitTimeBeforeAfterLoopActionsCheck");
                    TaskManager.Enqueue(() => { Action = $"After Loop Actions"; }, "Loop-AfterLoopActionsSetAction");
                }

                TrustLeveling();

                TaskManager.Enqueue(() =>
                {
                    if (StopLoop)
                    {
                        TaskManager.Enqueue(() => Svc.Log.Info($"Loop Stop Condition Encountered, Stopping Loop"));
                        LoopsCompleteActions();
                    }
                    else
                        LoopTasks();
                }, "Loop-CheckStopLoop");

            }
            else
            {
                TaskManager.Enqueue(() => Svc.Log.Debug($"Loops Done"),                                                                                         "Loop-Debug");
                TaskManager.Enqueue(() => { States &= ~PluginState.Navigating; },                                                                               "Loop-RemoveNavigationState");
                TaskManager.Enqueue(() => PlayerHelper.IsReady,                                                                                                 int.MaxValue, "Loop-WaitPlayerReady");
                TaskManager.Enqueue(() => Svc.Log.Debug($"Loop {CurrentLoop} == {GetEffectiveLoopTimes()} we are done Looping, Invoking LoopsCompleteActions"), "Loop-Debug");
                TaskManager.Enqueue(() =>
                                    {
                                        if (this.Configuration.ExecuteBetweenLoopActionLastLoop)
                                            this.LoopTasks(false);
                                        else
                                            this.LoopsCompleteActions();
                                    },     "Loop-LoopCompleteActions");
            }
        }
    }

    private unsafe void Condition_ConditionChange(ConditionFlag flag, bool value)
    {
        if (Stage == Stage.Stopped) return;

        if (flag == ConditionFlag.Unconscious)
        {
            if (value && (Stage != Stage.Dead || DeathHelper.DeathState != PlayerLifeState.Dead))
            {
                // 要在設 DeathState 之前：那會排程 OnDeath（變體/解限時會按確認框），停下之後不該再按。
                if (CountDeathAndStopIfLimitReached())
                    return;
                Svc.Log.Debug($"We Died, Setting Stage to Dead");
                DeathHelper.DeathState = PlayerLifeState.Dead;
                Stage = Stage.Dead;
            }
            else if (!value && (Stage != Stage.Revived || DeathHelper.DeathState != PlayerLifeState.Revived))
            {
                Svc.Log.Debug($"We Revived, Setting Stage to Revived");
                DeathHelper.DeathState = PlayerLifeState.Revived;
                Stage = Stage.Revived;
            }
            return;
        }
        //Svc.Log.Debug($"{flag} : {value}");
        if (Stage != Stage.Dead && Stage != Stage.Revived && !_recentlyWatchedCutscene && !Conditions.Instance()->WatchingCutscene && flag != ConditionFlag.WatchingCutscene && flag != ConditionFlag.WatchingCutscene78 && flag != ConditionFlag.OccupiedInCutSceneEvent && Stage != Stage.Action && Stage != Stage.Condition && value && States.HasFlag(PluginState.Navigating) && (flag == ConditionFlag.BetweenAreas || flag == ConditionFlag.BetweenAreas51 || flag == ConditionFlag.Jumping61))
        {
            Svc.Log.Info($"Condition_ConditionChange: Indexer Increase and Change Stage to Condition");
            Indexer++;
            VNavmesh_IPCSubscriber.Path_Stop();
            Stage = Stage.Condition;
        }
        if (Conditions.Instance()->WatchingCutscene || flag == ConditionFlag.WatchingCutscene || flag == ConditionFlag.WatchingCutscene78 || flag == ConditionFlag.OccupiedInCutSceneEvent)
        {
            _recentlyWatchedCutscene = true;
            SchedulerHelper.ScheduleAction("RecentlyWatchedCutsceneTimer", () => _recentlyWatchedCutscene = false, 5000);
        }
    }

    private int _untilHoldIndex = -1;

    /// <summary>目前這一步（<see cref="_untilHoldIndex"/>）離開條件連續不成立的次數；換步、成立、停止都歸零。</summary>
    private int _untilFailures;

    // CheckFinishing 底下那個「等 Action／路徑真的跑完」的守衛自己沒有逾時：如果戰後的
    // Interactable 步驟卡在無止境重試（例如撿寶那條 InteractableCheck 迴圈永遠到不了結束條件），
    // Action 就永遠不會變回空字串，CheckFinishing-WaitAction 會每 500ms 把自己重新排一次、
    // 永遠退不出副本。這個欄位記錄「目前這一段等待」從什麼時候開始，超過上限就放棄等待直接退本，
    // 不要無限期掛著。
    private DateTime? _checkFinishingWaitSince;

    // 🔴 逃生口的長度。
    //    ⚠️ 它與 ActionsManager.Interactable 的 90 秒等待有張力：極火龍那種「個人化剝取物件」
    //    實測要等 40 幾秒才會變成 IsTargetable，正常情況塞得進 60 秒，但若伺服器更慢，
    //    這個逃生口會先到期把人拉出副本。兩個數字都沒有台服實機驗證，要調就一起調。
    private const int CheckFinishingWaitTimeoutSeconds = 60;

    private void GetJobAndLevelingCheck()
    {
        Job curJob = Player.Object.GetJob();
        if (curJob != JobLastKnown)
        {
            if (LevelingEnabled)
            {
                Svc.Log.Info($"{(Configuration.DutyModeEnum == DutyMode.Support || Configuration.DutyModeEnum == DutyMode.Trust) && (Configuration.DutyModeEnum == DutyMode.Support || SupportLevelingEnabled) && (Configuration.DutyModeEnum != DutyMode.Trust || TrustLevelingEnabled)} ({Configuration.DutyModeEnum == DutyMode.Support} || {Configuration.DutyModeEnum == DutyMode.Trust}) && ({Configuration.DutyModeEnum == DutyMode.Support} || {SupportLevelingEnabled}) && ({Configuration.DutyModeEnum != DutyMode.Trust} || {TrustLevelingEnabled})");
                // Re-apply current auto leveling mode through a single code path
                // so duty/path/container state stays consistent.
                LevelingModeEnum = LevelingModeEnum;
                MainListClicked = true;
            }
        }

        JobLastKnown = curJob;
    }

    private void CheckRetainerWindow()
    {
        // AutoBot(AutoMarket)/AutoRetainer沒裝時就不要打這兩支 IPC。SafeWrapper 雖然會吃掉
        // IpcNotReadyError 並回 false，但每一次失敗都會走進 EzIpcFailureLog 的例外路徑。
        if (AutoRetainerHelper.State == ActionState.Running || (AutoRetainer_IPCSubscriber.IsEnabled && AutoRetainer_IPCSubscriber.IsBusy()) || (AM_IPCSubscriber.IsEnabled && AM_IPCSubscriber.IsRunning()) || Stage == Stage.Paused)
            return;

        // 每幀只關一次:關閉中的窗同一幀仍可見、TryBeginClose 又擋第二次 Close,同幀迴圈不會結束。
        if (Svc.Condition[ConditionFlag.OccupiedSummoningBell])
            AutoRetainerHelper.Instance.CloseAddons();
    }

    private void PreStageChecks()
    {
        if (Stage == Stage.Stopped)
            return;

        CheckRetainerWindow();

        if (EzThrottler.Throttle("OverrideAFK") && States.HasFlag(PluginState.Navigating) && PlayerHelper.IsValid)
            _overrideAFK.ResetTimers();

        if (!Player.Available) return;

        if (!InDungeon && CurrentTerritoryContent != null)
            GetJobAndLevelingCheck();

        if (!PlayerHelper.IsValid || !BossMod_IPCSubscriber.IsEnabled || !VNavmesh_IPCSubscriber.IsEnabled) return;

        if (!ReflectionHelper.RotationSolver_Reflection.RotationSolverEnabled && !BossMod_IPCSubscriber.IsEnabled && !Configuration.UsingAlternativeRotationPlugin) return;

        if (CurrentTerritoryType == 0 && Svc.ClientState.TerritoryType != 0 && InDungeon)
            ClientState_TerritoryChanged(Svc.ClientState.TerritoryType);

        if (this.States.HasFlag(PluginState.Navigating) && this.Configuration.LootTreasure && (!this.Configuration.LootBossTreasureOnly || (this.PathAction?.Name == "Boss" && this.Stage == Stage.Action)) &&
            (this.treasureCofferGameObject = ObjectHelper.GetObjectsByObjectKind(Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Treasure)
                                                        ?.FirstOrDefault(x => ObjectHelper.GetDistanceToPlayer(x) < 2)) != null)
        {
            BossMod_IPCSubscriber.SetRange(30f);
            ObjectHelper.InteractWithObject(this.treasureCofferGameObject, false);
        }

        if (Indexer >= Actions.Count && Actions.Count > 0 && States.HasFlag(PluginState.Navigating))
            DoneNavigating();

        if (Stage > Stage.Condition && !States.HasFlag(PluginState.Other))
            Action = Stage.ToCustomString();
    }

    public void Framework_Update(IFramework framework)
    {
        // 從別的執行緒打進來的 IPC 指令(Run／Start／Stop)與多開訊息,照排入的先後在這裡排乾。
        // 🔴 放在最前面:同一格排進來的指令要在這一格的 Stage 判斷之前生效,
        //    否則會白白多等一格,而 Stop 多等一格代表多跑一格的自動化。
        //    細節見 Helpers/FrameworkWorkQueue.cs。
        FrameworkWorkQueue.Drain();

        // 🔴 YesAlready 壓制租約的續約心跳（內部自行節流，沒在壓制時是一個布林判斷就返回）。
        // 一輪多本可以跑好幾個小時，而租約上限只有 5 分鐘 —— 不續約的話 YesAlready
        // 會在副本跑到一半自己醒過來搶按窗。
        // (LeaseMilliseconds = 300_000 = YesAlready SuppressionLeases.MaxLeaseMilliseconds; provider clamps, it does not reject)
        YesAlready_IPCSubscriber.Tick();

        // 🔴 vnavmesh 路徑容許值租約的續約心跳＋閒置放約(內部自行節流;
        //    沒有租約時是一個欄位判斷就返回)。租約上限 5 分鐘,不續約的話
        //    容許值會在副本跑到一半跳回使用者的值。
        VnavmeshToleranceLease.Tick();

        // 任務逾時的觀察哨。必須跑在 TaskManager 自己的 Tick 之後 —— 這是成立的:
        // 建構子先 TaskManager = new()(它在自己的建構子裡掛上 Svc.Framework.Update),
        // 之後才 Svc.Framework.Update += Framework_Update,多播委派照訂閱順序呼叫。
        TaskTimeoutWatcher.Tick();

        PreStageChecks();

        this.Framework_Update_InDuty(framework);

        ManualMoveYield.Tick();
        if (ManualMoveYield.TakeResume() && Stage == Stage.Moving)
            Stage = Stage.Reading_Path;

        // 在 Stage 判斷之前：先發放手時設的玩家目標，這一幀的 Waiting_For_Combat 就看得到。
        SquadronOpener.Tick();

        switch (Stage)
        {
            case Stage.Reading_Path:
                StageReadingPath();
                break;
            case Stage.Moving:
                StageMoving();
                break;
            case Stage.Action:
                StageAction();
                break;
            case Stage.Waiting_For_Combat:
                StageWaitingForCombat();
                break;
            default:
                break;
        }
    }

    public event IFramework.OnUpdateDelegate Framework_Update_InDuty = _ => {};

    /// <summary>卸載時等多開連線迴圈結束的上限;Dispose 在框架執行緒上,不可無限期等待。</summary>
    private const int MultiboxShutdownTimeoutMs = 1000;

    public void Dispose()
    {
        // 每一步各自隔離:前一步擲例外,後面的退訂與釋放照做。順序是契約,不要重排。
        // 多開放第一步,也必須在 ECommonsMain.Dispose() 之前:連線迴圈收尾時還會寫 Svc.Log。
        DisposeStep("MultiboxUtility.Shutdown", () => global::AutoDuty.Multibox.MultiboxUtility.Shutdown(MultiboxShutdownTimeoutMs));
        Disposing = true;
        // 在釋放 HttpClient 之前叫路徑更新停手,免得剩下的檔一個個撞已釋放的用戶端。
        DisposeStep("Patcher.CancelForUnload", Patcher.CancelForUnload);
        DisposeStep("GitHubHelper.Dispose", GitHubHelper.Dispose);
        DisposeStep("StopAndResetALL", StopAndResetALL);
        DisposeStep("Framework.Update -= Framework_Update", () => Svc.Framework.Update -= Framework_Update);
        DisposeStep("Framework.Update -= ScheduleInvoker", () => Svc.Framework.Update -= SchedulerHelper.ScheduleInvoker);
        DisposeStep("FileSystemWatcher.Dispose", () => FileHelper.FileSystemWatcher.Dispose());
        DisposeStep("FileWatcher.Dispose", () => FileHelper.FileWatcher.Dispose());
        DisposeStep("WindowSystem.RemoveAllWindows", () => WindowSystem.RemoveAllWindows());
        // 🔴 要在 ECommonsMain.Dispose() 之前拆:AddonPressGuard 的解除封鎖監聽器掛在
        //    Svc.AddonLifecycle 上,ECommons 收掉服務之後就沒有東西可以 Unregister 了,
        //    留著的委派會指向已卸載的組件。
        DisposeStep("AddonPressGuard.ForceTeardown", AddonPressGuard.ForceTeardown);
        // 🔴 拆掉所有 *_IPCSubscriber，要在 ECommonsMain.Dispose() 之前：拆除動作用得到
        //    Svc 的服務。放在 EzIpcFailureLog.Disable() 之前，拆的過程若有 IPC 失敗才記得到 log。
        DisposeStep("DisposeAllSubscribers", IPCSubscriber_Common.DisposeAllSubscribers);
        DisposeStep("EzIpcFailureLog.Disable", EzIpcFailureLog.Disable);
        DisposeStep("ECommonsMain.Dispose", ECommonsMain.Dispose);
        DisposeStep("MainWindow.Dispose", () => MainWindow.Dispose());
        DisposeStep("OverrideCamera.Dispose", () => OverrideCamera.Dispose());
        DisposeStep("TerritoryChanged -=", () => Svc.ClientState.TerritoryChanged -= ClientState_TerritoryChanged);
        DisposeStep("ConditionChange -=", () => Svc.Condition.ConditionChange -= Condition_ConditionChange);
        DisposeStep("PictoService.Dispose", PictoService.Dispose);
        DisposeStep("UiBuilder.Draw -=", () => PluginInterface.UiBuilder.Draw -= UiBuilderOnDraw);
        DisposeStep("Commands.RemoveHandler", () => Svc.Commands.RemoveHandler(CommandName));
    }

    private static void DisposeStep(string name, System.Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[AutoDuty] Dispose step '{name}' failed; continuing with the remaining steps");
        }
    }

    private void DrawUI() => WindowSystem.Draw();

    public void OpenConfigUI()
    {
        if (MainWindow != null)
        {
            MainWindow.IsOpen = true;
            MainWindow.OpenTab("Config");
        }
    }

    public void OpenMainUI()
    {
        if (MainWindow != null)
            MainWindow.IsOpen = true;
    }
}
