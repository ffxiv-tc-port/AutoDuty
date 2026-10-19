# AutoDuty

[AutoDuty](https://github.com/erdelf/AutoDuty) 的台服移植版。副本路徑自動巡覽插件：讓角色自動沿著預先規劃好的路線通過迷宮挑戰與各類
即時內容，可用劇情輔助器、親信戰友、冒險者分隊、多變迷宮或一般模式，戰鬥交給其他戰鬥插件執行。

## 功能

- **路徑巡覽**：已支援的副本內建路線，自動移動、開啟寶箱並清怪。
- **路徑製作**：內建 `Build` 分頁可自行錄製與編輯巡覽路線。
- **自動升等**：依路徑品質、頭目支援度與等級自動挑選合適的副本循環練等，
  可搭配自動裝備最佳裝備一路全程放置練等；也可用同樣方式練等劇情輔助器
  夥伴，並自動挑選練等對象。
- **排程器**：可排多個副本依序跑，並把目前的排程存成具名播放清單、之後載入重跑。
- **執行前**：自動移動到旅店／房屋／公會房屋、自行或委託 NPC 修理裝備、
  執行指令（例如 SND 腳本）、自動使用道具。
- **執行中**：自動拾取寶箱戰利品（可設定只撿王的寶箱）、依需求切換其他插件的開關狀態；
  卡住時依序嘗試重排移動、跳躍、側移、跳過本步。
- **輪次之間**：自動精製魔晶石、自動分解、自動籌備稀有品繳交（大國防聯軍）、觸發
  AutoRetainer 處理僱員、自動更換最佳裝備。
- **結束條件**：達到指定等級停止、休息經驗值耗盡停止、持有指定數量道具停止、開啟 AutoRetainer
  多角色模式、完成後自動關機。
- **多開同步（Multibox）**：多個遊戲用戶端一起跑時，主機邀請組隊、讓其他用戶端跟著接受任務搜尋器配對並回報死亡；
  這個開關不會存檔，每次都要在每個用戶端手動勾選。

## 指令

- `/ad` 或 `/autoduty`：開啟主視窗。
- `/ad config`：開啟設定。
- `/ad start`／`stop`／`pause`／`resume`：開始、停止、暫停、繼續目前的路徑。
- `/ad skipstep`：跳過目前步驟。
- `/ad run <模式> <TerritoryType 編號> <次數>`：以指定模式跑指定副本（`/ad tt <地名>` 可查編號）。
- `/ad repair`、`desynth`、`extract`、`turnin`、`equiprec`：單獨執行修理、分解、精製魔晶石、籌備稀有品繳交、裝備推薦裝備。
- `/ad queue`、`exitduty`：排入任務搜尋器、退出副本。
- 完整清單見遊戲內 `/xlhelp` 的 `/autoduty` 說明。

## 台服版的修改

- 介面繁體中文化。
- 同一場副本死亡達到設定次數（預設 2 次）時自動停下並在聊天視窗留訊息，不再復活後一直衝回去（「副本設定」可改，0＝不限）。
- 自己停下來時發出通知；有裝 TataruPraise 時可請塔塔露喊一句（使用者主動按停止不會喊）。
- 新增「跳過目前步驟」按鈕：跳過等待時會把已等的時間寫回路徑檔。
- 新增「解限模式：交戰中繼續走到定點」選項（預設關）。
- 新增大型任務「水晶塔 希爾科斯塔」路徑；佐拉加殲滅戰改成王戰步驟；補齊多個副本的互動步驟並移除殘缺路徑。
- 停止後不再自己恢復動作（傳送、跳躍、死亡中按停止都會真的停）；自己沒在跑時打完副本，不再關掉你自己開的循環插件與 BMR 預設。
- 與其他插件共用的開關改成「租用、用完歸還」：YesAlready、vnavmesh 的設定不再被單向蓋掉，使用者刻意關著的 YesAlready／Pandora 功能不會被重新打開。
- 多開同步當主機時只監聽設定的位址（預設只收同一台電腦的連線）。
- 灰掉的按鈕與設定，滑鼠移上去也會顯示說明（包括為什麼現在不能用）。
- 多處降低記憶體用量。

## 需要搭配的插件

- 必要：
  - 導航：[vnavmesh](https://github.com/ffxiv-tc-port/vnavmesh)
  - 頭目機制應對：[BossModReborn](https://github.com/ffxiv-tc-port/BossmodReborn)
  - 戰鬥循環（擇一）：[Wrath Combo](https://github.com/ffxiv-tc-port/WrathCombo)、Rotation Solver Reborn，或 BossModReborn 的自動循環
- 選用：
  - [AutoRetainer](https://github.com/ffxiv-tc-port/AutoRetainer)：輪次之間處理僱員；自動籌備稀有品繳交也需要它
  - [Gearsetter](https://github.com/ffxiv-tc-port/Gearsetter)：自動更換最佳裝備時納入兵裝庫以外的道具
  - DiscardHelper：自動丟棄道具
  - [TataruPraise](https://github.com/ffxiv-tc-port/TataruPraise)：自動停下時的塔塔露語音

原作者：Herculezz（目前上游維護者：erdelf）
