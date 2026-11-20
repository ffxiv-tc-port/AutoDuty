using AutoDuty.Helpers;
using AutoDuty.IPC;
using global::AutoDuty.Multibox;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using ECommons.LanguageHelpers;
using ECommons.MathHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;
using Dalamud.Bindings.ImGui;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using static AutoDuty.Helpers.RepairNPCHelper;
using static AutoDuty.Windows.ConfigTab;

namespace AutoDuty.Windows;

using Data;
using ECommons.Configuration;
using ECommons.ExcelServices;
using ECommons.UIHelpers.AddonMasterImplementations;
using ECommons.UIHelpers.AtkReaderImplementations;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using Properties;
using System.IO;
using System.Numerics;
using System.Text;
using ReflectionHelper = Helpers.ReflectionHelper;
using Vector2 = FFXIVClientStructs.FFXIV.Common.Math.Vector2;

[JsonObject(MemberSerialization.OptIn)]
public class ConfigurationMain : IEzConfig
{
    public const string CONFIGNAME_BARE = "Bare";

    public static ConfigurationMain Instance;

    [JsonProperty]
    public string DefaultConfigName = CONFIGNAME_BARE;

    [JsonProperty]
    private string activeProfileName = CONFIGNAME_BARE;
    
    public  string ActiveProfileName => this.activeProfileName;

    [JsonProperty]
    private readonly HashSet<ProfileData> profileData = [];

    private readonly Dictionary<string, ProfileData> profileByName = [];
    private readonly Dictionary<ulong, string> profileByCID = [];

    [JsonProperty]
    public readonly Dictionary<ulong, CharData> charByCID = [];

    [JsonObject(MemberSerialization.OptOut)]
    public struct CharData
    {
        public required ulong  CID;
        public          string Name;
        public          string World;

        public string GetName() => this.Name.Any() ? $"{this.Name}@{this.World}" : CID.ToString();

        public override int GetHashCode() => this.CID.GetHashCode();
    }

    /// <summary>
    /// 已儲存的具名排程播放清單。跨設定檔共用,存在 ConfigurationMain 這一層。
    /// </summary>
    /// <remarks>
    /// 🔴 <see cref="ConfigurationMain"/> 是 <c>MemberSerialization.OptIn</c>,
    /// 少了 <c>[JsonProperty]</c> 這個欄位會靜默不進存檔。
    /// </remarks>
    [JsonProperty]
    public List<Playlist> Playlists { get; set; } = [];

    [JsonProperty]
    //Dev Options
    internal bool updatePathsOnStartup = true;
    public bool UpdatePathsOnStartup
    {
        get => !Plugin.isDev || this.updatePathsOnStartup;
        set => this.updatePathsOnStartup = value;
    }

    // 多開協調(Multibox)設定。
    // 🔴 裡面的 MultiBox 開關本身標了 [JsonIgnore],**不會**存進設定檔 ⇒ 每次載入外掛
    // 都是關的,沒有「上次開著這次自動接上」的路徑。這裡存下來的只有連線參數
    // (管道名稱/位址/埠號)與是否為主機端。
    [JsonProperty]
    public MultiboxUtility.MultiboxConfiguration multibox = new();

    public IEnumerable<string> ConfigNames => this.profileByName.Keys;
     
    public ProfileData GetCurrentProfile
    {
        get
        {
            if (!this.profileByName.TryGetValue(this.ActiveProfileName, out ProfileData? profiles))
            {
                this.SetProfileToDefault();
                return this.GetCurrentProfile;
            }

            return profiles;
        }
    }

    public Configuration GetCurrentConfig => this.GetCurrentProfile.Config;

    public void Init()
    {
        if (this.profileData.Count == 0)
        {
            if (Svc.PluginInterface.ConfigFile.Exists)
            {
                Configuration? configuration = EzConfig.DefaultSerializationFactory.Deserialize<Configuration>(File.ReadAllText(Svc.PluginInterface.ConfigFile.FullName, Encoding.UTF8));
                if (configuration != null)
                {
                    this.CreateProfile("Migrated", configuration);
                    this.SetProfileAsDefault();
                }
            }
        }

        void RegisterProfileData(ProfileData profile)
        {
            if (profile.CIDs.Any())
                foreach (ulong cid in profile.CIDs)
                    this.profileByCID[cid] = profile.Name;
            this.profileByName[profile.Name] = profile;

            if(profile.Config.LootMethodEnum == LootMethod.RotationSolver) //RSR removed
                profile.Config.LootMethodEnum = LootMethod.All;
        }

        foreach (ProfileData profile in this.profileData)
            if(profile.Name != CONFIGNAME_BARE)
                RegisterProfileData(profile);

        RegisterProfileData(new ProfileData
                            {
                                Name = CONFIGNAME_BARE,
                                Config = new Configuration
                                         {
                                             EnablePreLoopActions     = false,
                                             EnableBetweenLoopActions = false,
                                             EnableTerminationActions = false,
                                             LootTreasure             = false
                                         }
                            });

        this.SetProfileToDefault();
    }

    public bool SetProfile(string name)
    {
        DebugLog("Changing profile to: " + name);
        if (this.profileByName.ContainsKey(name))
        {
            this.activeProfileName = name;
            EzConfig.Save();
            return true;
        }
        return false;
    }

    public void SetProfileAsDefault()
    {
        if (this.profileByName.ContainsKey(this.ActiveProfileName))
        {
            this.DefaultConfigName = this.ActiveProfileName;
            EzConfig.Save();
        }
    }

    public void SetProfileToDefault()
    {
        this.SetProfile(CONFIGNAME_BARE);
        Svc.Framework.RunOnTick(() =>
        {
            DebugLog($"Setting to default profile for {Player.Name} ({Player.CID}) {PlayerHelper.IsValid}");

            if (Player.Available && this.profileByCID.TryGetValue(Player.CID, out string? charProfile))
                if (this.SetProfile(charProfile))
                    return;
            DebugLog("No char default found. Using general default");
            if (!this.SetProfile(this.DefaultConfigName))
            {
                DebugLog("Fallback, using bare");
                this.DefaultConfigName = CONFIGNAME_BARE;
                this.SetProfile(CONFIGNAME_BARE);
            }
        });
    }

    public void CreateNewProfile() => 
        this.CreateProfile("Profile" + (this.profileByName.Count - 1).ToString(CultureInfo.InvariantCulture));

    public void CreateProfile(string name) => 
        this.CreateProfile(name, new Configuration());

    public void CreateProfile(string name, Configuration config)
    {
        DebugLog($"Creating new Profile: {name}");

        ProfileData profile = new()
                           {
                               Name   = name,
                               Config = config
                           };

        this.profileData.Add(profile);
        this.profileByName.Add(name, profile);
        this.SetProfile(name);
    }

    public void DuplicateCurrentProfile()
    {
        string name;
        int    counter = 0;

        string templateName = this.ActiveProfileName.EndsWith("_Copy") ? this.ActiveProfileName : $"{this.ActiveProfileName}_Copy";

        do
            name = counter++ > 0 ? $"{templateName}{counter}" : templateName;
        while (this.profileByName.ContainsKey(name));

        string?        oldConfig = EzConfig.DefaultSerializationFactory.Serialize(this.GetCurrentConfig);
        if(oldConfig != null)
        {
            Configuration? newConfig = EzConfig.DefaultSerializationFactory.Deserialize<Configuration>(oldConfig);
            if(newConfig != null)
                this.CreateProfile(name, newConfig);
        }
    }

    public void RemoveCurrentProfile()
    {
        DebugLog("Removing " + this.ActiveProfileName);
        this.profileData.Remove(this.GetCurrentProfile);
        this.profileByName.Remove(this.ActiveProfileName);
        this.SetProfileToDefault();
    }

    public bool RenameCurrentProfile(string newName)
    {
        if (this.profileByName.ContainsKey(newName))
            return false;

        ProfileData config = this.GetCurrentProfile;
        this.profileByName.Remove(this.ActiveProfileName);
        this.profileByName[newName] = config;
        config.Name                 = newName;
        this.activeProfileName      = newName;

        EzConfig.Save();

        return true;
    }

    public ProfileData? GetProfile(string name) => 
        this.profileByName.GetValueOrDefault(name);

    public void SetCharacterDefault()
    {
        Svc.Framework.RunOnTick(() =>
                          {

                              if (!PlayerHelper.IsValid)
                                  return;

                              ulong cid = Player.CID;

                              if (this.profileByCID.TryGetValue(cid, out string? oldProfile))
                                  this.profileByName[oldProfile].CIDs.Remove(cid);

                              this.GetCurrentProfile.CIDs.Add(cid);
                              this.profileByCID.Add(cid, this.ActiveProfileName);
                              this.charByCID[cid] = new CharData
                                                    {
                                                        CID  = cid,
                                                        Name = Player.Name,
                                                        World = Player.CurrentWorld
                              };

                              EzConfig.Save();
                          });
    }

    public void RemoveCharacterDefault()
    {
        Svc.Framework.RunOnTick(() =>
                                {
                                    if (!PlayerHelper.IsValid)
                                        return;

                                    ulong cid = Player.CID;

                                    this.profileByName[this.ActiveProfileName].CIDs.Remove(cid);
                                    this.profileByCID.Remove(cid);

                                    EzConfig.Save();
                                });
    }

    // 每次登入都重跑同一串（Bare → 角色預設 → 一般預設），所以同一句只有本次載入第一次記 Debug，之後記 Verbose。
    private static readonly HashSet<string> loggedDebugMessages = [];

    public static void DebugLog(string message)
    {
        bool first;
        lock (loggedDebugMessages)
            first = loggedDebugMessages.Add(message);
        if (first)
            Svc.Log.Debug($"Configuration Main: {message}");
        else
            Svc.Log.Verbose($"Configuration Main: {message}");
    }
}

[JsonObject(MemberSerialization.OptOut)]
public class ProfileData
{
    public required string         Name;
    public          HashSet<ulong> CIDs = [];
    public required Configuration  Config;
}

[Serializable]
public class PlannerItem
{
    /// <summary>
    /// TerritoryType key used by ContentPathsManager/ContentHelper.
    /// </summary>
    public uint TerritoryType;

    /// <summary>
    /// How many successful completions to run this duty for.
    /// </summary>
    public int TargetRuns = 1;

    /// <summary>
    /// How many successful completions have been recorded for this duty in the current plan cycle.
    /// </summary>
    public int CompletedRuns;

    /// <summary>
    /// Selected route file name (DutyPath.FileName). Null means auto-select.
    /// </summary>
    public string? PathFileName;
}

/// <summary>
/// 一份具名的排程快照。使用者可以把目前的排程清單存成播放清單,之後載入重跑。
/// </summary>
/// <remarks>
/// 🔴 這只是<b>排程資料</b>。載入播放清單不會啟動任何跑本流程,
/// 使用者仍然要自己按「執行排程」,既有守衛一條都沒有繞過。
/// </remarks>
[JsonObject(MemberSerialization.OptOut)]
public class Playlist
{
    /// <summary>播放清單名稱,在清單集合裡唯一(比對時忽略大小寫)。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>清單內容。存的是排程項目的快照,不含執行進度。</summary>
    public List<PlannerItem> Entries { get; set; } = [];
}

public class AutoDutySerializationFactory : DefaultSerializationFactory, ISerializationFactory
{
    public override string DefaultConfigFileName { get; } = "AutoDutyConfig.json";

    /// <summary>
    /// 🔴 <b>存檔真正走的序列化出口就是這一支</b>,不是 <see cref="SerializeAsBin"/>。
    /// </summary>
    /// <remarks>
    /// <c>EzConfig.SaveConfiguration</c> 依 <c>IsBinary</c> 二選一,而
    /// <c>DefaultSerializationFactory.IsBinary</c> 是 <b>false</b> 且本類別沒有覆寫它
    /// ⇒ 走的是 <c>serializationFactory.Serialize(Configuration)</c> 這條。
    /// 本類別的基底清單重新列出了 <c>ISerializationFactory</c>,所以這支 <c>new</c> 方法會接走介面派送
    /// (不重列介面的話會落回基底)⇒「IPC 覆寫值不可以被寫進設定檔」掛在這裡才有效。
    /// ⚠️ 這裡只做序列化(純 CPU):檔案是 <c>SaveConfiguration</c> 拿到字串之後才寫的,<c>WithUserValues</c> 持鎖的期間不含檔案 I/O。
    /// </remarks>
    public new string Serialize(object config) =>
        ConfigOverrideHelper.WithUserValues(() => base.Serialize(config, true));

    /// <summary>存檔路徑不會走到這裡(見 <see cref="Serialize(object)"/>),它轉呼叫上面那支,所以一樣被包住。</summary>
    public override byte[] SerializeAsBin(object config) =>
        Encoding.UTF8.GetBytes(this.Serialize(config));
}



[Serializable]
public class Configuration
{
    //Meta
    public HashSet<string>                                    DoNotUpdatePathFiles = [];
    public Dictionary<uint, Dictionary<string, JobWithRole>?> PathSelectionsByPath = [];

    //LogOptions
    public bool AutoScroll = true;
    public LogEventLevel LogEventLevel = LogEventLevel.Debug;

    //General Options
    public int LoopTimes = 1;

    //Planner Options (fixed duty sequence)
    public bool PlannerEnabled = false;
    public bool PlannerRepeat  = false;
    public bool PlannerPaused  = false;
    public List<PlannerItem> PlannerItems = [];
    public int PlannerCurrentIndex = 0;

    /// <summary>
    /// 目前載入中的播放清單名稱,空字串代表沒有載入任何清單(既有使用者的預設值)。
    /// 只是一個標示,不影響排程怎麼跑。
    /// </summary>
    public string PlannerPlaylistName = string.Empty;

    internal DutyMode dutyModeEnum = DutyMode.None;
    public DutyMode DutyModeEnum
    {
        get => dutyModeEnum;
        set
        {
            dutyModeEnum = value;
            Plugin.CurrentTerritoryContent = null;
            MainTab.DutySelected = null;
            Plugin.LevelingModeEnum = LevelingMode.None;
        }
    }
    
    public bool Unsynced                       = false;
    public bool HideUnavailableDuties          = false;
    public bool PreferTrustOverSupportLeveling = false;

    public bool ShowMainWindowOnStartup = false;

    //Overlay Config Options
    internal bool showOverlay = true;
    public bool ShowOverlay
    {
        get => showOverlay;
        set
        {
            showOverlay = value;
            if (Plugin.Overlay != null)
                Plugin.Overlay.IsOpen = value;
        }
    }
    internal bool hideOverlayWhenStopped = false;
    public bool HideOverlayWhenStopped
    {
        get => hideOverlayWhenStopped;
        set 
        {
            hideOverlayWhenStopped = value;
            if (Plugin.Overlay != null)
            {
                SchedulerHelper.ScheduleAction("LockOverlaySetter", () => Plugin.Overlay.IsOpen = !value || Plugin.States.HasFlag(PluginState.Looping) || Plugin.States.HasFlag(PluginState.Navigating), () => Plugin.Overlay != null);
            }
        }
    }
    internal bool lockOverlay = false;
    public bool LockOverlay
    {
        get => lockOverlay;
        set 
        {
            lockOverlay = value;
            if (value)
                SchedulerHelper.ScheduleAction("LockOverlaySetter", () => { if (!Plugin.Overlay.Flags.HasFlag(ImGuiWindowFlags.NoMove)) Plugin.Overlay.Flags |= ImGuiWindowFlags.NoMove; }, () => Plugin.Overlay != null);
            else
                SchedulerHelper.ScheduleAction("LockOverlaySetter", () => { if (Plugin.Overlay.Flags.HasFlag(ImGuiWindowFlags.NoMove)) Plugin.Overlay.Flags -= ImGuiWindowFlags.NoMove; }, () => Plugin.Overlay != null);
        }
    }
    internal bool overlayNoBG = false;
    public bool OverlayNoBG
    {
        get => overlayNoBG;
        set
        {
            overlayNoBG = value;
            if (value)
                SchedulerHelper.ScheduleAction("OverlayNoBGSetter", () => { if (!Plugin.Overlay.Flags.HasFlag(ImGuiWindowFlags.NoBackground)) Plugin.Overlay.Flags |= ImGuiWindowFlags.NoBackground; }, () => Plugin.Overlay != null);
            else
                SchedulerHelper.ScheduleAction("OverlayNoBGSetter", () => { if (Plugin.Overlay.Flags.HasFlag(ImGuiWindowFlags.NoBackground)) Plugin.Overlay.Flags -= ImGuiWindowFlags.NoBackground; }, () => Plugin.Overlay != null);
        }
    }
    public bool ShowDutyLoopText       = true;
    public bool ShowActionText         = true;
    public bool UseSliderInputs        = false;
    public bool OverrideOverlayButtons = true;
    public bool GotoButton             = true;
    public bool TurninButton           = true;
    public bool DesynthButton          = true;
    public bool ExtractButton          = true;
    public bool RepairButton           = true;
    public bool EquipButton            = true;
    public bool CofferButton           = true;
    public bool TTButton               = true;


    //Duty Config Options
    public   bool AutoExitDuty                  = true;
    public   bool OnlyExitWhenDutyDone          = false;
    public   bool AutoManageRotationPluginState = true;
    internal bool autoManageBossModAISettings   = true;
    public bool AutoManageBossModAISettings
    {
        get => autoManageBossModAISettings;
        set
        {
            autoManageBossModAISettings = value;
            HideBossModAIConfig = !value;
        }
    }
    // 王戰中把目標切到 BMR 首領模組標記的限時必殺小怪、打掉後切回王；與上面兩格無關，BMR 沒開 AI 也照做。
    public bool       BossTargetUrgentAdds           = true;
    // 冒險者小隊小怪路段：設玩家目標前先對該敵人下「號令：戰鬥開始」，分隊跟上或逾時才設（Helpers/SquadronOpener）。
    public bool       SquadronOpener                 = true;
    public float      SquadronOpenerMaxWaitSeconds   = 2.5f;
    public bool       AutoManageVnavAlignCamera      = true;
    public bool       LootTreasure                   = true;
    public LootMethod LootMethodEnum                 = LootMethod.AutoDuty;
    public bool       LootBossTreasureOnly           = false;
    public int        TreasureCofferScanDistance     = 25;
    public bool       RebuildNavmeshOnStuck          = true;
    public byte       RebuildNavmeshAfterStuckXTimes = 5;
    public int        MinStuckTime                   = 500;

    // 卡住升級階梯。預設開著就是新的行為(使用者裁決):實機證據顯示重建網格對貼牆卡死零效果,
    // 所以先跳、再側移、再跳過本步,重建被排到最後。
    public bool  StuckEscalationEnabled   = true;
    public int   StuckJumpAfterXTimes     = 2;
    public int   StuckSidestepAfterXTimes = 4;
    public int   StuckSkipStepAfterXTimes = 6;
    public float StuckSidestepDistance    = 2.5f;

    /// <summary>同一場副本玩家死到第幾次就停下 AutoDuty 並請塔塔露求助；0＝不限（原本的行為）。</summary>
    /// <remarks>預設 2 是使用者裁決的「預設開」；舊設定檔沒有這個鍵時也拿到 2。</remarks>
    public int StopAfterDeathsInDuty = 2;

    public bool PathDrawEnabled   = false;
    public int  PathDrawStepCount = 5;

    public bool       OverridePartyValidation        = false;
    public bool       UsingAlternativeRotationPlugin = false;
    public bool       UsingAlternativeMovementPlugin = false;
    public bool       UsingAlternativeBossPlugin     = false;

    public bool        TreatUnsyncAsW2W = true;
    public JobWithRole W2WJobs          = JobWithRole.Tanks;

    /// <summary>
    /// 解限模式下,交戰中不要停下來,繼續走到下一個定點(技能沿路交給輪替外掛放)。
    /// 預設關 —— 開了等於放棄「停下來把這一團清乾淨」的保證。
    /// </summary>
    public bool UnsyncedKeepMovingInCombat = false;

    /// <summary>
    /// 「解除限制」這個開關現在是不是真的生效中。
    /// 判斷式與 <see cref="IsW2W"/> 和 AutoDuty.StageReadingPath 裡那份完全一致:
    /// 解限只有在隨機任務/討伐戰/大型任務這三種模式下才有意義。
    /// </summary>
    public bool IsUnsyncActive() => this.Unsynced && this.DutyModeEnum is DutyMode.Raid or DutyMode.Regular or DutyMode.Trial;

    public bool IsW2W(Job? job = null, bool? unsync = null)
    {
        job ??= PlayerHelper.GetJob();

        if (this.W2WJobs.HasJob(job.Value))
            return true;

        unsync ??= this.IsUnsyncActive();

        return unsync.Value && this.TreatUnsyncAsW2W;
    }


    //PreLoop Config Options
    public bool                                       EnablePreLoopActions     = true;
    public bool                                       ExecuteCommandsPreLoop   = false;
    public List<string>                               CustomCommandsPreLoop    = [];
    public bool                                       RetireMode               = false;
    public RetireLocation                             RetireLocationEnum       = RetireLocation.Inn;
    public List<System.Numerics.Vector3>              PersonalHomeEntrancePath = [];
    public List<System.Numerics.Vector3>              FCEstateEntrancePath     = [];
    public bool                                       AutoEquipRecommendedGear;
    public bool                                       AutoEquipRecommendedGearGearsetter;
    public bool                                       AutoEquipRecommendedGearGearsetterOldToInventory;
    public bool                                       AutoRepair              = false;
    public uint                                       AutoRepairPct           = 50;
    public bool                                       AutoRepairSelf          = false;
    public RepairNpcData?                             PreferredRepairNPC      = null;
    public bool                                       AutoConsume             = false;
    public bool                                       AutoConsumeIgnoreStatus = false;
    public int                                        AutoConsumeTime         = 29;
    public List<KeyValuePair<ushort, ConsumableItem>> AutoConsumeItemsList    = [];

    //Between Loop Config Options
    public bool         EnableBetweenLoopActions         = true;
    public bool         ExecuteBetweenLoopActionLastLoop = false;
    public int          WaitTimeBeforeAfterLoopActions   = 0;
    public bool         ExecuteCommandsBetweenLoop       = false;
    public List<string> CustomCommandsBetweenLoop        = [];
    public bool         AutoExtract                      = false;

    public bool                     AutoOpenCoffers = false;
    public byte?                    AutoOpenCoffersGearset;
    public bool                     AutoOpenCoffersBlacklistUse;
    public Dictionary<uint, string> AutoOpenCoffersBlacklist = [];

    internal bool autoExtractAll = false;
    public bool AutoExtractAll
    {
        get => autoExtractAll;
        set => autoExtractAll = value;
    }
    internal bool autoDesynth = false;
    public bool AutoDesynth
    {
        get => autoDesynth;
        set
        {
            autoDesynth = value;
            if (value && !AutoDesynthSkillUp)
                AutoGCTurnin = false;
        }
    }
    internal bool autoDesynthSkillUp = false;
    public bool AutoDesynthSkillUp
    {
        get => autoDesynthSkillUp;
        set
        {
            autoDesynthSkillUp = value;
            if (!value && AutoGCTurnin)
                AutoDesynth = false;
        }
    }
    public int AutoDesynthSkillUpLimit = 50;
    internal bool autoGCTurnin = false;
    public bool AutoGCTurnin
    {
        get => autoGCTurnin;
        set
        {
            autoGCTurnin = value;
            if (value && !AutoDesynthSkillUp)
                AutoDesynth = false;
        }
    }
    public int AutoGCTurninSlotsLeft = 5;
    public bool AutoGCTurninSlotsLeftBool = false;
    public bool AutoGCTurninUseTicket = false;

    public bool TripleTriadEnabled;
    public bool TripleTriadRegister;
    public bool TripleTriadSell;

    public bool DiscardItems;

    public bool EnableAutoRetainer = false;
    public SummoningBellLocations PreferredSummoningBellEnum = 0;
    //Termination Config Options
    public bool EnableTerminationActions = true;
    public bool StopLevel = false;
    public int StopLevelInt = 1;
    public bool StopNoRestedXP = false;
    public bool StopItemQty = false;
    public bool StopItemAll = false;
    public Dictionary<uint, KeyValuePair<string, int>> StopItemQtyItemDictionary = [];
    public int StopItemQtyInt = 1;
    public bool ExecuteCommandsTermination = false;
    public List<string> CustomCommandsTermination = [];
    public bool PlayEndSound = false;
    public bool CustomSound = false;
    public float CustomSoundVolume = 0.5f;
    public Sounds SoundEnum = Sounds.None;
    public string SoundPath = "";
    public TerminationMode TerminationMethodEnum = TerminationMode.Do_Nothing;
    public bool TerminationKeepActive = true;
    // 「自動化不是被你停掉、是自己停了」通知。預設關 —— 既有使用者的 JSON 已經有這個鍵之前
    // 都吃不到任何行為變化,要開必須自己到設定裡勾。
    public bool NotifyWhenStoppedItself = false;

    /// <summary>
    /// AutoDuty 自己停下來時，透過 IPC 請 TataruPraise（塔塔露誇獎）念一句。
    /// </summary>
    /// <remarks>
    /// 📌 預設 <c>true</c>：TataruPraise 沒安裝時整條路是靜默 no-op（IPC 擲
    /// <c>IpcNotReadyError</c> 被吃掉），而 TataruPraise 自己的總開關（預設關）與冷卻也還在，
    /// 所以預設開<b>不會</b>讓任何人多聽到聲音。
    /// ⚠️ 與 <see cref="NotifyWhenStoppedItself"/> 刻意分成兩個旗標：那個是桌面通知，
    /// 這個是語音。想聽聲音的人不必連帶被彈通知，反之亦然。
    /// </remarks>
    public bool TataruPraiseOnStoppedItself = true;
    
    //BMAI Config Options
    public bool HideBossModAIConfig           = false;
    public bool BM_UpdatePresetsAutomatically = true;

    /// <summary>
    /// 非王步驟時要不要請 BossMod 的 AI 跟著隊伍裡的坦克走(StayCloseToPartyRole)。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>預設關,等於本欄位加入之前的行為</b> —— 這個功能本 fork 從來沒有過(上游有),
    /// 開了會讓角色在戰鬥中多一個「靠向坦克」的目標區,與既有的 StayCloseToTarget 疊加。
    /// 路徑步驟可以用 <see cref="PathActionFlags.NoPartyCoherency"/> 單獨關掉某一步;
    /// 王步驟(Name == "Boss")一律不跟。
    /// </remarks>
    public bool PartyCoherency = false;


    internal bool maxDistanceToTargetRoleBased = true;
    public bool MaxDistanceToTargetRoleBased
    {
        get => maxDistanceToTargetRoleBased;
        set
        {
            maxDistanceToTargetRoleBased = value;
            if (value)
                SchedulerHelper.ScheduleAction("MaxDistanceToTargetRoleBasedBMRoleChecks", () => Plugin.BMRoleChecks(), () => PlayerHelper.IsReady);
        }
    }
    public float MaxDistanceToTargetFloat = 2.6f;
    public float MaxDistanceToTargetAoEFloat = 12;
    
    internal bool positionalRoleBased = true;
    public bool PositionalRoleBased
    {
        get => positionalRoleBased;
        set
        {
            positionalRoleBased = value;
            if (value)
                SchedulerHelper.ScheduleAction("PositionalRoleBasedBMRoleChecks", () => Plugin.BMRoleChecks(), () => PlayerHelper.IsReady);
        }
    }
    public float MaxDistanceToTargetRoleMelee  = 2.6f;
    public float MaxDistanceToTargetRoleRanged = 10f;


    internal bool       positionalAvarice = true;
    public   Positional PositionalEnum    = Positional.Any;

    #region Wrath

    public   bool                                                       Wrath_AutoSetupJobs { get; set; } = true;
    public Wrath_IPCSubscriber.DPSRotationMode    Wrath_TargetingTank    = Wrath_IPCSubscriber.DPSRotationMode.Highest_Max;
    public Wrath_IPCSubscriber.DPSRotationMode    Wrath_TargetingNonTank = Wrath_IPCSubscriber.DPSRotationMode.Lowest_Current;


    #endregion


    public void Save()
    {
        EzConfig.Save();
    }

    public TrustMemberName?[] SelectedTrustMembers = new TrustMemberName?[3];
}
