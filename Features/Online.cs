// 联机栏目。回家、房间列表（筛选 + 可拖列宽 / 换序 / 隐藏列的固定表头表格）。
// 表头绘制和列拖拽换序都交给核心的 TableDraw，本页只提供列定义和点击回调。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using GameEvent;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SR_UCH.Tweaks {
    //联机栏目（成熟功能，侧栏顺序 90，排在会话(80)之后、实验(100)之前；不再是实验模块的一部分）：
    //  · 回家：返回主界面（走游戏自己的 Matchmaker.ReturnToMainMenu，带看门狗兜底）, 房主顺带解散全员
    //  · 房间列表：下拉筛选（同时只开一个）/ 可加入的排最上面 / 固定表头表格（可拖列宽、换列序、隐藏列，
    //    操作列钉在右边缘，滚轮上下 / Ctrl+滚轮左右，Shift+右键还原）
    //  · 房间码加入：4 个大写字母，走游戏自己的按码加入通道
    //表格里的文字尽量用**游戏自己的本地化**（区域 = AvailableRegion.LocalizedShortName，
    //模式 = GameState.GetLocalizedGameModeName，标签 = GameSettings.ConvertLobbyTag，
    //大厅中/AFK/轮数/无限制 = I2 术语），这样跟游戏内列表的中文完全一致。
    public class Online : ITweak {


        // 按键
        //（快捷键条目已统一到 [Hotkeys] 的 online.lobbies / online.disband，不再有本段的 KeyCode 条目）
        // 筛选（全部下拉框，存字符串；空 = 全部/默认）
        private static ConfigEntry<string> _fState;   //筛选：房间状态（"" = 全部；join/full/playing）
        private static ConfigEntry<string> _fRegion, _fMode, _fTag, _fMinPlayers, _fAfk;
        // **可配**：房间搜索超时（原来硬编码 15 秒）。弱网 / 后端慢时调大，避免列表过早显示"搜索结束"。
        private static ConfigEntry<int> _searchTimeout;
        private static float SearchTimeout() {
            try { if (_searchTimeout != null) return (float)_searchTimeout.Value; } catch { }
            return 15f;
        }
        // 表格列宽（像素，逗号分隔；表头分隔线可拖动）/ 表格高度（0 = 自动）
        private static ConfigEntry<string> _colWidths;
        private static ConfigEntry<int> _tableH;
        private static ConfigEntry<bool> _showQualityCoeff;    //游戏质量列是否附带技能匹配质量系数（Shift+表头点击切换）
        private static ConfigEntry<string> _skillMode;         //技能系数列取 最大/平均/最小（Shift+表头点击切换）
        //多列排序（左键点表头切换）：-1 = 不按列排序 -> 回到默认顺序（可加入优先 -> 健康分降序 -> 进度 -> id）
        private static ConfigEntry<int> _sortColEntry;
        private static ConfigEntry<bool> _sortDescEntry;
        private static int _sortCol = -1;
        private static bool _sortDesc;

        // 运行时状态
        private static readonly List<Matchmaker.LobbyListInfo> _lobbies = new List<Matchmaker.LobbyListInfo>();
        private static readonly HashSet<string> _hidden = new HashSet<string>();   //右键隐藏的房间 id
        private static bool _lobbySearching;
        private static float _lobbySearchAt = -999f;
        private static string _lobbyMsg = "";
        // **错误提示要"粘住"**：搜索失败/匹配器不可用这类 _lobbyMsg 不能随后被统计文案覆盖
        //  （原来 1 秒兜底无条件 UpdateLobbyMsg，"搜索失败: xxx" 一闪就变成"共 0 个房间"，
        //   玩家根本看不到失败原因，还以为真的搜完了、没有房间）。
        private static bool _lobbyErr;
        //房间列表/可见性变了才重排 + 重算统计（原来每帧都排：IMGUI 一帧 Layout+Repaint 两趟，
        //100 个房间 ≈ 每秒上百次全量排序，比较器里还带 IsJoinable）
        private static bool _lobbiesDirty;
        private static float _lobbySortAt = -999f;
        //自己的技能系数按帧缓存：悬浮在技能系数表头时每帧都要它（原来每帧读一次存档）
        private static int _mySkillFrame = -1;
        private static string _mySkillText;
        private static Vector2 _scrollRows, _scrollCols;
        //（原 _scrollColsAtDrag 已删除：它从未被赋值，唯一读取点只会把横向滚动归零, 详见 TableInput 内的说明）
        private static string _openCombo = "";              //同时只允许一个下拉框展开
        private static float _abortDeadline, _forceDeadline, _forceRawDeadline;   //回家看门狗的三级时限
        private static float[] _cw;                         //当前列宽
        private static bool _widthsLoaded;                  //S22 列宽是否已初始化(替代原先拿 _cw == null 当哨兵)
        private static bool _dragH;                         //正在拖表格高度
        private static float _dragHY0, _dragH0;
        private static float _dragHTmp;                      //S20 拖表格高度期间的临时值(拖动中不写 _tableH.Value)
        // 对局内点加入= 先退出当前房间，退出后立刻加入
        private static string _pendingJoin = "";
        private static bool _pendingUseCode;                 //true = 按房间码加入
        private static float _pendingReadyAt, _pendingGiveUpAt;
        private static string _joinCode = "";                //按钮行右边的房间码输入框（4 个大写字母）

        //LevelSelectController.FadeOut 是 private 序列化字段（场景内遮罩）：FadeToLevel 等的就是它
        private static readonly FieldInfo _fLsFadeOut =
            typeof(LevelSelectController).GetField("FadeOut", BindingFlags.Instance | BindingFlags.NonPublic);

        private const string SecOnline = "Online";
        private const string ColWidthsKey = "Column Widths 4";   //v4：新增综合分列，老值不再套用（沿用 v3 的加列即换键惯例）
        private const string TableHKey = "Table Height";
        private const string ColOrderKey = "Column Order";
        private const string HiddenColsKey = "Hidden Columns 4";   //v4：新增综合分列（默认隐藏 -> 新键带默认值才生效）
        //S22: 默认隐藏列的列索引字符串. 原来在 Bind 时用 CombinedCol.ToString() 现算, 而读取侧
        //(EnsureHiddenCols)只做 Split + TryParse, 两边没有共享常量, 改列数时容易只改一边.
        //这里的值必须与 CombinedCol 保持一致.
        private const string DefaultHiddenCol = "10";

        //列宽（默认值）：区域 人数 游戏质量 平均技能 进度 游戏模式 标签 房主 限制 标记 综合分 操作
        private static readonly float[] DefWidths = { 62f, 44f, 76f, 62f, 66f, 80f, 50f, 86f, 78f, 50f, 54f, 70f };

        public void Initialize(IFeatureHost plugin) {
            SR.LocSec(SecOnline, "联机", "Online");
            SR.Nav(SecOnline, 90);   //侧栏默认顺序 90：会话(80) 之后、实验(100) 之前
            SR.RegisterPage(SecOnline, RenderOnlinePage);
            SR.RegisterTick("联机看门狗", CheckHotkeys); //每帧：回家看门狗 + 延时加入（由 SR.Tick 独立 try/catch 调用）
            //加入流程本身要用 EventSystem：禁用它会卡在"正在进入"（见 SR.Input.ApplyEventSystemGate 的说明）。
            //加入期间登记"门控挂起"，让核心不去禁用 EventSystem。
            SR.RegisterInputGateHold(() => !string.IsNullOrEmpty(_pendingJoin));
            // **预登记本页自动快捷键**：联机页是自绘页，不打开就渲染不到 HotkeyButton，
            //  而刷新列表 / 返回主界面恰恰是"人在房间里、不打开面板就想用"的键, 必须启动即生效。
            SR.HotkeyAction("online.lobbies", "刷新列表", RefreshLobbies);
            SR.HotkeyAction("online.disband", "返回主界面", DisbandOrLeave);

            // 反射成员登记（进 SR 的启动自检）：主程序集里 **Online.cs 的反射同样要登记**,
            //  否则游戏更新改名后是**静默失效**：反射悄悄返回 null，功能装了没反应，
            //  日志里连 [SR.Ref] 的失效计数都不会变。（此前只有建造/聊天/关卡/快速调整登记过，这里是盲区。）
            // 但只能登记**能用简单名解析到类型**的成员：SR.RefType() 走 Assembly.GetType(名字)，
            //  按第一个点切分 ownerName，因此要求类型在**全局命名空间**。
            //  带命名空间的类型（如 UCHServices.AvailableRegion）解析不到 -> 登记了反而变成**误报失效**，
            //  所以那类一律不登记（下面注明）。
            try {
                SR.RefOverride("LevelSelectController.FadeOut", "联机：强制返回主界面（放出加载遮罩）");
                SR.RefOverride("SteamMatchmaker.JoinSteamLobby", "联机：按 Steam 房间号加入");
                SR.RefOverride("UnityMatchmaker.inPlatformLobbyNoRelay", "联机：判断是否在平台房间里");
                SR.RefOverride("UnityMatchmaker.LeaveLobby", "联机：切换房间前退出当前房间");
                //有意不登记（不是遗漏）：
                //  · Matchmaker.FindLobbies / JoinLobby, 代码里是**直接虚调用**（mm.FindLobbies(...)、
                //    mm.JoinLobby(...)），不是反射。游戏改名会直接编译不过，用不着运行时自检。
                //  · AvailableRegion.shortName / name / id, 类型是 UCHServices.AvailableRegion，
                //    带命名空间，RefType 解析不到 -> 登记只会报"失效"。（取成员已按类型缓存，见 RegionMembers。）
                //  · LobbyListInfo.UnityServerRegion, 代码是 li.UnityServerRegion 强类型访问，不是反射。
            } catch (Exception __ro) { SR.Guard.Log("联机反射登记", __ro); }

            // 房主解散感知（订阅游戏的网络消息事件总线）：房主点返回主界面会 SendToAll(HostEndedGame)
            //  （反编译 44861），房客侧游戏自己会切主菜单，但 **SR 此前完全没订阅这条**,
            //  房客被解散时 SR 的待加入队列与回家看门狗还挂着，会白等一轮超时才报错。
            //  与 DestroyBlocks/PlayerTracker 同一套写法（ChangeListener + IGameEventListener）。
            try { GameEventManager.ChangeListener<NetworkMessageReceivedEvent>(new HostEndedListener(), true); }
            catch (Exception __he) { SR.Guard.Log("联机.房主解散监听", __he); }

            //快捷键只有一套：[Hotkeys] 段的自动快捷键 online.lobbies / online.disband（见下面按钮的 HotkeyButton）。
            //原来这里还另有 [Online] Refresh Lobbies Key / Disband Key / Main Menu Key 三条 KeyCode 条目，
            //由 CheckHotkeys 自己轮询, 但它们在本界面没有任何渲染入口（联机页是自绘页，不列通用条目），
            //属于隐形遗留；且 Disband Key 与 Main Menu Key 调的是同一个 DisbandOrLeave()。
            //已删除：老配置的绑定值由 ConfigMigration 搬进 [Hotkeys] 对应 id，不丢用户设置。

            _fRegion = plugin.Config.Bind(SecOnline, "Filter Region", "", "按区域挑房间, 从下拉里选一个.");
            _fMode = plugin.Config.Bind(SecOnline, "Filter Mode", "", "按游戏模式挑房间, 从下拉里选一个.");
            _fTag = plugin.Config.Bind(SecOnline, "Filter Tag", "", "按房间标签挑房间, 从下拉里选一个.");
            _fMinPlayers = plugin.Config.Bind(SecOnline, "Filter Min Players", "", "按人数挑房间. 留空是全部, 填 1 到 3 表示人数不少于这个数, 填 lt4 表示少于 4 人.");
            _fState = plugin.Config.Bind(SecOnline, "Filter State", "", "按房间状态挑. 留空是全部, join 只看还能加入的, full 只看人已满的, playing 只看正在打的.");
            _fAfk = plugin.Config.Bind(SecOnline, "Filter AFK", "hide", "按房主有没有挂机来挑. 留空是全部, hide 把挂机的藏起来, only 只看挂机的.");
            // **可配**：搜索超时（原来硬编码 15 秒）
            _searchTimeout = plugin.Config.Bind(SecOnline, "Search Timeout", 15,
                new ConfigDescription("搜房间最多等多少秒, 5 到 60. 到点还没收到搜索结束的信号就不等了. 网慢或者房间多的时候可以调大.",
                    new AcceptableValueRange<int>(5, 60)));
            _colWidths = plugin.Config.Bind(SecOnline, ColWidthsKey, "",
                "表格每一列多宽, 单位像素, 用逗号隔开. 在表头的分隔线上按住左键拖就能改. 按住 Shift 右键表格可以还原列宽和高度, 并把藏起来的房间放出来.");
            _tableH = plugin.Config.Bind(SecOnline, TableHKey, 0,
                new ConfigDescription("表格有多高, 单位像素, 填 0 让它跟着窗口走. 拖表格底边也能改.", new AcceptableValueRange<int>(0, 1000)));
            _colOrder = plugin.Config.Bind(SecOnline, ColOrderKey, "", "列的先后顺序, 填列号, 用逗号隔开. 留空就是默认顺序.");
            _hiddenColsEntry = plugin.Config.Bind(SecOnline, HiddenColsKey, DefaultHiddenCol,
                "藏起来的列, 填列号, 用逗号隔开. 右键表头可以藏一列, 按住 Shift 右键还原. 默认只藏了综合分那一列.");
            //表头点击切换（见 HeaderClick）：游戏质量列 = 是否附带"技能匹配质量"系数；技能系数列 = 最大/平均/最小
            _showQualityCoeff = plugin.Config.Bind(SecOnline, "Show Quality Coeff", false,
                "游戏质量那一列要不要多显示一个技能匹配质量系数. 点这一列的表头可以切换, 默认关.");
            _skillMode = plugin.Config.Bind(SecOnline, "Skill Mode", "avg",
                "技能系数那一列显示哪个值: max 是最大技能, avg 是平均技能, min 是最小技能. 按住 Shift 点这一列的表头可以切换, 默认 avg.");
            //多列排序：左键点表头 = 按该列排序（升 -> 降 -> 默认），见 CycleSort
            _sortColEntry = plugin.Config.Bind(SecOnline, "Sort Column", -1,
                "房间列表按哪一列排序, 填列号, -1 表示默认排法: 能加入的排前面, 其余按综合分从高到低. 左键点表头就能换.");
            _sortDescEntry = plugin.Config.Bind(SecOnline, "Sort Descending", false, "排序是从大到小还是从小到大.");
            try {
                _sortCol = _sortColEntry.Value;
                _sortDesc = _sortDescEntry.Value;
                //防御：配置被手改成越界值 / 指向操作列 -> 当作不排序
                if (_sortCol < 0 || _sortCol >= ColCount || _sortCol == JoinCol) _sortCol = -1;
            } catch { _sortCol = -1; _sortDesc = false; }
            SR.LocKey(SecOnline, "Show Quality Coeff", "显示技能匹配质量", null);
            SR.LocDesc(SecOnline, "Show Quality Coeff", "打开后, 游戏质量那一列的勾后面会多出一个 0 到 1 的匹配系数. 按住 Shift 点一下这一列的表头就能开关.", null);
            SR.LocKey(SecOnline, "Skill Mode", "技能系数取值", null);
            SR.LocDesc(SecOnline, "Skill Mode", "技能系数那一列显示最大值, 平均值还是最小值. 按住 Shift 点这一列的表头就能换.", null);
        }

        // 回家（解散 / 退出 / 返回主界面）
        private static void Notes(string zh, string en) {
            SR.Notify(SR.T(zh, en));
        }

        private static void ArmWatchdog() {
            float t = Time.unscaledTime;
            //三级全部重置：第三级 _forceRawDeadline 是唯一"不看场景是否已换成功、到点就硬切 MainMenu"
            //的一级，若上一轮兜底残留着它，重新武装时不归零，玩家改主意去加入别的房间后仍会被切走。
            _abortDeadline = t + 5f;    //第 1 级：放出被等待的遮罩
            _forceDeadline = t + 8f;    //第 2 级：自己收尾 + 强制加载主界面
            _forceRawDeadline = 0f;     //第 3 级：本次不预置，由 ForceMainMenu / TickWatchdog 到点才设
        }

        //解除看门狗：换房间（LeaveCurrentForJoin）路径在真正发出加入请求后必须调一次。
        //看门狗的逃生条件是场景已变成 MainMenu，而换房间成功后场景是 Lobby/TreeHouseLobby，
        //永远不满足 -> 8 秒后会把刚加进的新房间 Disconnect + 销毁 LobbyManager + 强制加载主界面。
        private static void DisarmWatchdog() {
            _abortDeadline = 0f;
            _forceDeadline = 0f;
            //第三级（温和加载 2 秒后再硬切场景）也要一起解除：它是唯一"不看场景是否已换成功、
            //到点就 SceneManager.LoadScene(MainMenu)"的一级。若换房间成功时它还挂着，
            //2 秒后会把刚加进的新房间直接切走（场景是 Lobby 而不是 MainMenu，逃生条件不成立）。
            _forceRawDeadline = 0f;
        }
        // 回家了：统一走游戏自己的 Matchmaker.ReturnToMainMenu(reason)，收尾全交给 LobbyManagerManager
        // （它内部 SetAbortReason -> 房主 SendToAll(MsgHostEndedGame) + CleanUpPlayers
        //   -> AbortGameInProgressGracefully -> Disconnect + Destroy(LobbyManager) + FadeOutToLoad -> 加载 MainMenu）。
        // 两个坑（之前"卡在加载动画"的原因），别再踩：
        //   1) 自己先 LobbyManager.Disconnect：房主 StopHost 之后，本机客户端那步 FadeToLevel 要等服务器切场景，
        //      可服务器已经没了 -> 永远停在加载动画；
        //   2) LevelSelectController.BackToMainMenu：它等的是**场景内**那个 FadeOut 遮罩的状态，而
        //      LoadingInterstitialSplash.Awake 会把非首个实例 Destroy，那个 State 之后不再更新 -> 协程永远等不到。
        public static void GoMainMenu() {
            Notes("正在返回主界面", "Returning to the main menu");
            // S2 修复：点返回主界面=**取消待加入**。
            //  TickPendingJoin 是每帧轮询的：一旦退出完成（InSession 变 false）就会 DoJoin(_pendingJoin)。
            //  原来这里不清 -> "点加入 B 房 -> 反悔点返回主界面"的结果是**退出后仍被拉进 B 房**，
            //  而且 DoJoin 之后还会 DisarmWatchdog() 把刚为回家武装的看门狗解除。
            //  （OnHostEnded 里本来就清了它，这条只是把收尾路径补全。）
            _pendingJoin = "";
            // reason 不能传自定义串：游戏会把它当成**玩家可见提示**弹到屏幕上
            //  （ReturnToMainMenu -> SetAbortReason -> AbortGame 里 UserMessage(reason, 5f, lo)）。
            //  传 null 则整段跳过，也**不会覆盖游戏自己设的 abortReason**,
            //  诊断改由下面这条 LogInfo 承担（日志里照样看得见）。
            try { SR.LogInfo("[联机] 请求返回主界面（reason 传 null，避免把调试串弹给玩家）"); } catch { }
            try { Matchmaker.ReturnToMainMenu(null); }
            catch (Exception e) { SR.LogWarn("返回主界面失败: " + e.Message); }
            ArmWatchdog();
        }

        public static void DisbandOrLeave() {
            //已经点过一次、还在回主界面的倒计时里 -> 再点一次 = 强制立即返回（不等游戏自己的加载流程）
            if (_abortDeadline > 0f || _forceDeadline > 0f || _forceRawDeadline > 0f) { ForceMainMenu(); return; }
            try {
                LobbyManager lm = LobbyManager.instance;
                bool host = false;
                //房主判定失败会退回"房客"分支（只退自己、不广播 HostEndedGame）-> 必须留痕
                try { host = lm != null && lm.IsHost; } catch (Exception __ex) { SR.Guard.Log("判定是否房主", __ex); }
                Notes(host ? "正在解散房间" : "正在退出房间", host ? "Disbanding the session" : "Leaving the session");
            } catch (Exception __ex) { SR.Guard.Log("退出联机提示", __ex); }
            //房主：ReturnToMainMenu 会先广播 HostEndedGame -> 全员一起回主界面（=解散）
            //房客：同一条路只退自己（AbortGame 里走 client 分支）
            GoMainMenu();
        }

        //强制返回主界面：不等游戏自己的加载流程（有时候会卡在加载动画上）。
        //每一步都独立 try，任何一步失败都继续往下：
        //  1) NudgeSplash：放出被等待的加载遮罩, FadeToLevel/FadeOutToLoad 这类协程等的就是
        //     LoadingInterstitialSplash.State == SHOW，不放的话后面切场景可能还卡在同一处；
        //  2) 断开网络 + 销毁 LobbyManager（收尾，别把联机状态带进主界面）；
        //  3) 收掉 SR 自己的 UI 状态（面板/地图）；
        //  4) SceneManagerWrapper.LoadScene("MainMenu")（游戏自己的温和加载）；
        //  5) 2 秒后还没进主界面 -> SceneManager.LoadScene("MainMenu") 直接切（最后手段，见 TickWatchdog）。
        public static void ForceMainMenu() {
            //已经在主界面：不要重复加载（否则等于把主界面又刷一遍）
            if (SceneManager.GetActiveScene().name == "MainMenu") {
                _abortDeadline = 0f; _forceDeadline = 0f; _forceRawDeadline = 0f;
                Notes("已在主界面", "Already in the main menu");
                return;
            }
            Notes("正在强制返回主界面", "Force-returning to the main menu");
            _pendingJoin = "";   // **S2 同上**：强制回主界面同样要取消待加入，否则退出后仍会被拉进目标房
            _abortDeadline = 0f;
            _forceDeadline = 0f;
            SR.LogWarn("[联机] 强制返回主界面（手动）");
            NudgeSplash();
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null) {
                    //每一步独立 try、失败继续往下（有意设计）；但异常必须记下来，
                    //否则"卡在加载动画"时看日志根本不知道是哪一步没成（Guard.Log 内部有去重，不会刷屏）
                    try { lm.Disconnect("SR_UCH force"); } catch (Exception __ex) { SR.Guard.Log("强制返回主界面：断开连接", __ex); }
                    try { UnityEngine.Object.Destroy(lm.gameObject); } catch (Exception __ex) { SR.Guard.Log("强制返回主界面：销毁 LobbyManager", __ex); }
                }
            } catch (Exception __ex) { SR.Guard.Log("强制返回主界面：收尾联机状态", __ex); }
            try { SR.ForceResetUiState(); } catch (Exception __ex) { SR.Guard.Log("强制返回主界面：重置界面状态", __ex); }
            try { SceneManagerWrapper.LoadScene("MainMenu"); }
            catch (Exception e) { SR.LogWarn("强制加载主界面失败: " + e.Message); }
            _forceRawDeadline = Time.unscaledTime + 2f;   //5) 二级兜底
        }

        private static void SetShow(UISplashScreen s) {
            try {
                if (ReferenceEquals(s, null)) return;             //已销毁的场景内遮罩也要改（协程读的是托管字段）
                if (s.State != UISplashScreen.STATE.SHOW) s.State = UISplashScreen.STATE.SHOW;
            } catch (Exception __ex) { SR.Guard.Log("放出加载遮罩", __ex); }
        }

        //把被 FadeToLevel 等待的遮罩推到 SHOW，让卡住的协程继续往下走
        private static void NudgeSplash() {
            try { SetShow(LoadingInterstitialSplash.Instance); } catch (Exception __ex) { SR.Guard.Log("放出加载遮罩(Instance)", __ex); }
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.CurrentLevelSelectController != null && _fLsFadeOut != null) {
                    SetShow(_fLsFadeOut.GetValue(lm.CurrentLevelSelectController) as UISplashScreen);
                }
            } catch (Exception __ex) { SR.Guard.Log("放出加载遮罩(关卡选择)", __ex); }
        }

        //看门狗（只在玩家点了退出联机/返回主界面之后启动）：
        //  5 秒还没回主界面 -> 放出遮罩（可能是上面第 2 个坑，或 abortGameRequested 已被别处置位导致这次请求被忽略）；
        //  8 秒还没回主界面 -> 自己 Disconnect + 销毁 LobbyManager，然后强制加载 MainMenu；
        //  再 2 秒还没回主界面 -> 直接 SceneManager.LoadScene（温和加载也没成，最后一招）。
        private static void TickWatchdog() {
            float now = Time.unscaledTime;
            //已经回到主界面：看门狗使命结束，时限清掉（免得残留时限让"再点一次返回主界面"误触发强制返回）
            if ((_abortDeadline > 0f || _forceDeadline > 0f || _forceRawDeadline > 0f)
                && SceneManager.GetActiveScene().name == "MainMenu") {
                _abortDeadline = 0f; _forceDeadline = 0f; _forceRawDeadline = 0f;
            }
            if (_abortDeadline > 0f && now >= _abortDeadline) {
                _abortDeadline = 0f;
                string sc = SceneManager.GetActiveScene().name;
                if (sc == "MainMenu") { _forceDeadline = 0f; return; }
                SR.LogWarn("[联机] 回家超时（场景 " + sc + "）-> 放出加载遮罩让流程继续");
                NudgeSplash();
            }
            if (_forceDeadline > 0f && now >= _forceDeadline) {
                _forceDeadline = 0f;
                if (SceneManager.GetActiveScene().name == "MainMenu") return;
                SR.LogWarn("[联机] 仍卡在加载动画 -> 强制回主界面");
                NudgeSplash();   //先放出被等待的遮罩，再收尾（少了这步有可能又卡在同一处）
                try {
                    LobbyManager lm = LobbyManager.instance;
                    if (lm != null) {
                        //同 ForceMainMenu：结构不变（每步独立 try），但失败要留痕
                        try { lm.Disconnect("SR_UCH watchdog"); } catch (Exception __ex) { SR.Guard.Log("超时兜底：断开连接", __ex); }
                        try { UnityEngine.Object.Destroy(lm.gameObject); } catch (Exception __ex) { SR.Guard.Log("超时兜底：销毁 LobbyManager", __ex); }
                    }
                } catch (Exception __ex) { SR.Guard.Log("超时兜底：收尾联机状态", __ex); }
                try { SceneManagerWrapper.LoadScene("MainMenu"); }
                catch (Exception e) { SR.LogWarn("强制加载主界面失败: " + e.Message); }
                _forceRawDeadline = now + 2f;
            }
            //二级兜底：温和加载过了 2 秒还没进主界面 -> 直接切（最后手段）
            if (_forceRawDeadline > 0f && now >= _forceRawDeadline) {
                _forceRawDeadline = 0f;
                if (SceneManager.GetActiveScene().name == "MainMenu") return;
                SR.LogWarn("[联机] 温和加载仍未进主界面 -> 直接切场景 MainMenu");
                try { SceneManager.LoadScene("MainMenu"); }
                catch (Exception e) { SR.LogWarn("直接切主界面失败: " + e.Message); }
            }
        }

        // 房主解散感知
        //游戏房主点返回主界面时会 SendToAll(HostEndedGame)（反编译 44861），房客侧由
        //LevelSelectController 收到后自己切主菜单（136118-136121）。但那条只处理**场景切换**，
        //SR 这边的待加入队列回家看门狗不在它的管辖内, 不订阅就会出现
        //"房主都走了，SR 还在等加入超时"。这里补上。
        public class HostEndedListener : IGameEventListener {
            public void handleEvent(GameEvent.GameEvent e) {
                try {
                    NetworkMessageReceivedEvent nm = e as NetworkMessageReceivedEvent;
                    if (nm == null || nm.Message == null) return;
                    if (nm.Message.msgType != NetMsgTypes.HostEndedGame) return;
                    OnHostEnded();
                } catch (Exception __ex) { SR.Guard.Log("房主解散感知", __ex); }
            }
        }

        private static void OnHostEnded() {
            try {
                //1) 取消还没完成的加入：房主都没了，继续等只会超时
                bool wasPending = !string.IsNullOrEmpty(_pendingJoin);
                _pendingJoin = "";
                //2) 解除回家看门狗：它的超时兜底会强断连接 / 销毁 LobbyManager，这里不该再触发
                DisarmWatchdog();
                //3) 如实告诉玩家（界面收尾交给游戏自己的场景切换，SR 不抢）
                if (wasPending) Notes("房主已解散房间，已取消加入", "Host disbanded; join cancelled");
                else Notes("房主已解散房间", "Host disbanded");
                SR.LogInfo("[联机] 收到 HostEndedGame：已清待加入队列并解除看门狗");
            } catch (Exception __ex) { SR.Guard.Log("处理房主解散", __ex); }
        }

        // 房间列表
        // 取匹配器的正确姿势：先用 IsInstantiated **问**，再决定要不要碰 Instance。
        //  原因：Matchmaker.Instance 是**惰性创建**, 没实例化时访问它会顺带 new 一个 SteamMatchmaker
        //  （并触发 SetupSteam / findExternalIP，见反编译 48038 / 48919）。
        //  所以 mm == null 这个判断在直接写 Matchmaker.Instance 时永远为 false，
        //  原来那句"匹配器不可用"是死分支、永远显示不出来。
        //  游戏自己也是先问再取（SteamManager.PostDestroy 78553：if (Matchmaker.IsInstantiated) Matchmaker.Instance...）。
        //  Cecil 已核：IsInstantiated 是 public static bool，可直接调。
        private static Matchmaker MatchmakerOrNull() {
            try { return Matchmaker.IsInstantiated ? Matchmaker.Instance : null; }
            catch { return null; }
        }

        public static void RefreshLobbies() {
            if (!SR.GateMaster) return;   // **#1 与其它功能一致**：受 SR 总开关限制
            try {
                Matchmaker mm = MatchmakerOrNull();
                // **判空必须放最前**：下面 _pendingSteamId 分支会立刻用 mm.GetType()，
                //  原来那句判空写在它**之后**（原 310 行）,  既是死分支，位置也错：真为 null 时先崩在上面。
                if (mm == null) {
                    _lobbyMsg = SR.T("联机未初始化：请先进入游戏的联机界面一次", "Online not initialised: open the game's online screen once first");
                    _lobbyErr = true;
                    return;
                }
                // S1 修复：Steam 通道分支**已从这里移走**。
                //  原来它驻留在 RefreshLobbies 里、靠 _pendingSteamId 残留传递, 后果是：
                //  在列表点过加入之后该字段一直非零，下一次点刷新列表一进来就命中它、
                //  直接 JoinSteamLobby(旧id) 并 return -> **列表完全不搜索，反而去加入上一间房**。
                //  现在判定归位到真正执行加入的 JoinFlow（两条加入路径的汇聚点）。
                _lobbies.Clear();
                //G8-c: 原来的 _regions.Clear() + _regionsVersion++ 已删除. _regions 是个只写不读的
                //HashSet(全文件没有任何读取点), 而区域下拉选项早已改从 RelayConstants.AVAILABLE_REGIONS
                //出(见 RegionVals), 与房间列表无关, 不再需要按刷新次数发版本戳.
                _regionCodeCache.Clear();
                _lobbySearching = true;
                _lobbyErr = false;          //这次搜索开始了：清掉上一次的错误提示
                _lobbyMsg = SR.T("搜索中...", "Searching...");
                _lobbySearchAt = Time.unscaledTime;
                mm.FindLobbies(new Matchmaker.LobbyListingCallback(OnLobbyInfo));
            } catch (Exception e) {
                _lobbySearching = false;
                _lobbyErr = true;
                _lobbyMsg = SR.T("搜索失败: ", "Search failed: ") + e.Message;
                SR.LogWarn("刷新房间列表失败: " + e.Message);
            }
        }

        private static void OnLobbyInfo(Matchmaker.LobbyListInfo info) {
            try {
                if (info == null) return;
                if (info.error) { _lobbySearching = false; _lobbyErr = true; _lobbyMsg = SR.T("搜索出错", "Search error"); return; }
                //同一 sLobbyID 的回调可能重复到达（游戏自身用 Dictionary 去重）：去重/替换，
                //否则会画出重复房间行、计数虚高，且排序时相同 sLobbyID 反复换位导致行闪。
                int exist = -1;
                for (int i = 0; i < _lobbies.Count; i++) {
                    if (_lobbies[i] != null && _lobbies[i].sLobbyID == info.sLobbyID) { exist = i; break; }
                }
                if (exist >= 0) _lobbies[exist] = info; else _lobbies.Add(info);
                //G8-c: 原来的 _regions.Add(rn) / _regionsVersion++ 随 _regions 一起删除.
                //RegionCode(info) 的调用要保留: 它会按房间 id 填 _regionCodeCache(预热),
                //返回值原来只喂给 _regions, 现在不需要了.
                RegionCode(info);
                //只标记"变了"：排序与统计都等渲染时做一次（原来每个房间回调都全量 CountShown 一次 -> O(n²)）
                _lobbiesDirty = true;
            } catch (Exception __ex) { SR.Guard.Log("房间列表回调", __ex); }
        }

        //点加入：不在房间里就直接加；**在房里/对局里则先退出当前房间，一退出就立刻加入（不等回主界面）**
        public static void JoinLobby(string id) { JoinLobby(id, false); }

        //按房间码加入（游戏自己的 Matchmaker.JoinLobby(code, useCode:true) 就是干这个的）
        public static void JoinByCode(string code) {
            if (string.IsNullOrEmpty(code)) return;
            JoinLobby(code.Trim(), true);
        }

        //房间码输入过滤：只保留大写英文字母、最多 4 个（游戏房间码就是 4 个大写字母）
        private static string CodeOnly(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(4);
            for (int i = 0; i < s.Length && sb.Length < 4; i++) {
                char c = char.ToUpperInvariant(s[i]);
                if (c >= 'A' && c <= 'Z') sb.Append(c);
            }
            return sb.ToString();
        }

        // **加入流程**：先离开当前房间 -> 轮询等待离开完成 -> 再加入目标房间（"无缝切换"）。
        //  inPlatformLobbyNoRelay 是私有字段 -> 反射读取。
        // 必须走 mm.GetType()（运行时真实类型），不能用 typeof(Matchmaker)：
        //  Cecil 读元数据确认, Matchmaker 类**本身并不声明** inPlatformLobbyNoRelay
        //  （它的 15 个字段里没有），该字段声明在子类 UnityMatchmaker 上。
        //  所以查基类恒为 null，那句 ?? AccessTools.Field(typeof(Matchmaker), ...) 是恒 null 的死回退，
        //  已删除：留着会让人误以为"还有第二道保险"，将来左半失效时就会漏判。
        //  （AccessTools.Field 会沿继承链找，所以左半用运行时类型能正常命中。）
        private static System.Collections.IEnumerator JoinFlow(string id, bool useCode, Matchmaker mm) {
            System.Reflection.FieldInfo inLobby = HarmonyLib.AccessTools.Field(mm.GetType(), "inPlatformLobbyNoRelay");
            bool InLobby() { try { return inLobby != null && (bool)inLobby.GetValue(mm); } catch { return false; } }
            if (InLobby()) {
                try { SR.LogInfo("[联机] 已在房间中，先离开..."); } catch { }
                try {
                    System.Reflection.MethodInfo lv = mm.GetType().GetMethod("LeaveLobby", new System.Type[] { typeof(string), typeof(UserMessageManager.UserMsgPriority) })
                        ?? mm.GetType().GetMethod("LeaveLobby", new System.Type[] { typeof(string) });
                    if (lv != null) {
                        object[] args = lv.GetParameters().Length == 2
                            ? new object[] { null, UserMessageManager.UserMsgPriority.lo } : new object[] { null };
                        lv.Invoke(mm, args);
                    }
                } catch (Exception __lv) { SR.Guard.Log("联机.离开房间", __lv); }
                float t = 0f;
                while (InLobby() && t < 8f) { t += Time.unscaledDeltaTime; yield return null; }
                try { SR.LogInfo("[联机] 离开完成(inLobby=" + InLobby() + ")，开始加入"); } catch { }
            }
            // S1 修复：Steam 通道判定归位到**加入路径**（原来错误地放在 RefreshLobbies 里，
            //  靠 _pendingSteamId 残留传递 -> "点过加入后，下次点刷新列表不搜索、反而加入旧房间"）。
            //  放在 JoinFlow 是因为它是两条加入路径（JoinLobby 直接加入 / TickPendingJoin 延时加入）的汇聚点。
            //  sLobbyID 在 Steam 平台是 GUID，真正的 Steam 房间号在 UnityMatchID。
            if (_pendingSteamId != 0) {
                ulong sid = _pendingSteamId;
                _pendingSteamId = 0;   //无论下面走不走得通都必须清零，否则又会污染下一次刷新
                try {
                    System.Reflection.MethodInfo jsu = mm.GetType().GetMethod("JoinSteamLobby", new System.Type[] { typeof(ulong) });
                    if (jsu != null) {
                        SR.LogInfo("[联机] 走 JoinSteamLobby(ulong) " + sid);
                        jsu.Invoke(mm, new object[] { sid });
                        yield break;   //已按 Steam 通道发出加入请求，不再走通用 JoinLobby
                    }
                    SR.LogWarn("[联机] 无 JoinSteamLobby(ulong)，退回通用流程");
                } catch (Exception __jsl) { SR.Guard.Log("联机.JoinSteamLobby", __jsl); }
            }
            try { mm.JoinLobby(id, useCode, null); } catch (Exception __jl) { SR.Guard.Log("联机.加入", __jl); }
        }


        public static void JoinLobby(string id, bool useCode) {
            if (string.IsNullOrEmpty(id)) return;
            //玩家点了加入=放弃"回主界面"：看门狗必须在这里解除，两条路径都覆盖。
            //原来只有"在房里 -> TickPendingJoin 发请求后"那条路径解除，不在房里时直接 DoJoin 不解除 ->
            //先点返回主界面再改主意加入，进房后 8 秒（或第三级到点）会被旧看门狗踢出去
            //（逃生条件是场景变成 MainMenu，而加入成功后场景是 Lobby，永远不成立）。
            DisarmWatchdog();
            if (InSession() || Matchmaker.CurrentMatchmakingLobby != null) {
                _pendingJoin = id;
                _pendingUseCode = useCode;
                _pendingReadyAt = 0f;
                _pendingGiveUpAt = Time.unscaledTime + 30f;
                Notes("正在退出当前房间并在退出后加入", "Leaving the current session, then joining");
                //只做"离开当前房间"这一件事，不等主界面：不调用 ReturnToMainMenu（那要多走一趟场景加载）
                LeaveCurrentForJoin();
                return;
            }
            DoJoin(id, useCode);
        }

        //直接离开当前联机（游戏自己会走 AbortGame 的收尾）；退出干净后 TickPendingJoin 只发一次加入请求
        private static void LeaveCurrentForJoin() {
            try {
                Matchmaker mm = MatchmakerOrNull();
                if (mm != null) {
                    // reason 同样不能传自定义串：LeaveLobby 会把它弹成 3 秒**高优先级**提示
                    //  （LeaveLobbySubModule 49077 UserMessage(reason, 3f, priority)）。
                    //  传 null 时 NullOrEmpty 判定为真 -> 整段跳过，不弹任何提示。
                    // **必须保留第二个参数**：Matchmaker.LeaveLobby 只有 (string, UserMsgPriority)
                    //  这一个重载（Cecil 已核），写成 LeaveLobby(null) 会编译不过。
                    try { SR.LogInfo("[联机] 离开当前房间以切换（reason 传 null，不弹提示）"); } catch { }
                    mm.LeaveLobby(null, UserMessageManager.UserMsgPriority.hi);
                }
            } catch (Exception e) { SR.LogWarn("切换房间前退出失败: " + e.Message); }
            ArmWatchdog();
        }

        //是否正在联机/对局中（决定要不要先退出）
        private static bool InSession() {
            try {
                LobbyManager lm = LobbyManager.instance;
                // 游戏 LobbyManager.IsInOnlineGame 在**非房主分支恒返回 true**（反编译 38692）,
                //  只要 LobbyManager 对象还在就算"在联机"。刚被踢/刚断线、对象还没销毁时，
                //  会因此多做一次多余的 LeaveLobby。这里补一层"客户端是否真的连着"（与 SR.Env.Online 同款判据）。
                //  注意：真的在别人房里时，下面的 CurrentMatchmakingLobby 非 null 仍会兜住 -> 不会漏判。
                bool connected = false;
                try { connected = lm != null && lm.client != null && lm.client.isConnected; } catch { }
                if (lm != null && (lm.IsHost || (connected && lm.IsInOnlineGame))) return true;
                if (Matchmaker.CurrentMatchmakingLobby != null) return true;
            } catch (Exception __ex) { SR.Guard.Log("判定是否在联机中", __ex); }
            return false;
        }

        private static void DoJoin(string id, bool useCode) {
            try {
                Matchmaker mm = MatchmakerOrNull();
                if (mm == null) {
                    Notes("联机功能未初始化，请先进入一次游戏的联机界面", "Online is not initialised; open the in-game online screen once");
                    _pendingJoin = "";
                    return;
                }
                Notes(useCode ? "正在按房间码加入" : "正在加入房间", useCode ? "Joining by code" : "Joining the session");
                //失败就失败：不重试（重试会让玩家看到连续几次加入请求，也可能把已经满/已开的房间重试出怪状态）
                // 加入房间：**必须先离开当前房间**再加入。反编译 35360-35384：若 inPlatformLobbyNoRelay 为真，
                //  JoinLobby 会直接回 false（"[Net] Cant join a lobby because this client is already in one"）。
                //  玩家一开始就在自己的树屋房间里 -> 所以点加入会无反应/报中继失败。流程：离开 -> 等离开完成 -> 加入。
                // **加入前必须把"房主"标志关掉**：游戏自己的 JoinGame()（反编译 54590）在加入前会执行
                //  GameSettings.StartAsHost = false；否则 Steam 侧会把这次请求当成"创建房间"->
                //  表现就是"点加入却创建了自己的房间"。
                try {
                    GameSettings.GetInstance().StartAsHost = false;
                    SR.LogInfo("[联机] StartAsHost=false（加入而非创建）");
                } catch (Exception __sa) { SR.Guard.Log("联机.StartAsHost", __sa); }
                try { SR.LogInfo("[联机] 加入请求 id=" + id + " mm=" + mm.GetType().FullName + " useCode=" + useCode); } catch { }
                // **必须在这里清掉待加入队列**：它是每帧轮询的（见 TickPendingJoin），不清就会每帧重复发起
                //  同一次加入（日志里那次出现 105 次、最后超时退房，就是这里 return 时漏清导致的）。
                _pendingJoin = "";
                try { mm.StartCoroutine(JoinFlow(id, useCode, mm)); return; } catch (Exception __jf) { SR.Guard.Log("联机.加入流程", __jf); }
                try { SR.LogInfo("[联机] 协程启动失败，退回直接 JoinLobby"); } catch { }
                mm.JoinLobby(id, useCode, new UnityEngine.Events.UnityAction<bool>(ok => {
                    try {
                        _pendingJoin = "";
                        Notes(ok ? "已发送加入请求" : "加入失败", ok ? "Join request sent" : "Join failed");
                    } catch (Exception __ex) { SR.Guard.Log("加入房间回调", __ex); }
                }));
                //发出即算完成：这里立刻清空（原来只在回调里清，回调没触发就会被 TickPendingJoin
                //每 2 秒重发一次，30 秒内最多约 15 次, 与上方不重试的注释正好相反）。
                _pendingJoin = "";
            } catch (Exception e) {
                SR.LogWarn("加入房间失败: " + e.Message);
                _pendingJoin = "";
            }
        }

        //对局内加入：等"已经退出当前房间"之后再发一次加入请求（只发一次，失败不再重试）
        private static void TickPendingJoin() {
            if (string.IsNullOrEmpty(_pendingJoin)) return;
            try {
                float now = Time.unscaledTime;
                if (now > _pendingGiveUpAt) {
                    _pendingJoin = "";
                    Notes("加入超时，已取消", "Join timed out and was cancelled");
                    return;
                }
                if (now < _pendingReadyAt) return;
                //还没退出干净（还在房里/还在联机中）-> 继续等，不算重试
                if (InSession() || Matchmaker.CurrentMatchmakingLobby != null) { _pendingReadyAt = now + 0.4f; return; }
                string id = _pendingJoin;
                bool useCode = _pendingUseCode;
                DoJoin(id, useCode);
                //已退出旧房间并发出加入请求：看门狗的兜底使命结束，必须解除（见 DisarmWatchdog 注释）。
                //未退出干净时不会走到这里，看门狗保持武装 -> 离开卡住仍然会被兜底。
                DisarmWatchdog();
            } catch (Exception __ex) { SR.Guard.Log("延时加入房间", __ex); }
        }

        public static void CheckHotkeys() {
            TickWatchdog();
            TickPendingJoin();
            //按键轮询已删除：刷新列表 / 返回主界面 统一走 [Hotkeys] 段的自动快捷键
            //（online.lobbies / online.disband，由 SR 通用快捷键分发触发，见页面按钮的 HotkeyButton）。
        }

        // 游戏本地化小工具
        //用游戏自己的 I2 术语表（找不到/取不到就退回默认值），保证与游戏内列表用词一致
        private static string Tr(string key, string fallback) {
            try {
                string s = I2.Loc.LocalizationManager.GetTranslation(key);
                if (!string.IsNullOrEmpty(s)) return s;
            } catch { }
            return fallback;
        }

        //SR 界面语言是 English 时不能用游戏术语（游戏语言可能是中文，术语只会给中文）-> 用我们自己的英文。
        //走 SR.Ctl.LangEn（= UseEn）而不是自己判 _langEn：装了外部语言包且 FallbackId="English" 时
        //_langEn 恒为 false，写死它会让这十来处文案跑去读**游戏自己的术语表**（游戏是中文就显示中文），
        //跟语言包语言对不上。
        private static bool En { get { return SR.Ctl.LangEn; } }

        //语言包优先：T() 内部会查外部语言包，命中就是译文；没收录时按英文系/中文回落。
        //（原来直接 return en，装了语言包却永远翻不了这类词）
        private static string G(string i2Key, string zh, string en) {
            string own = SR.T(zh, en);
            if (En) return own;                 //英文系：我们自己的英文（或语言包译文）
            return Tr(i2Key, own);              //中文系：优先游戏自己的术语，取不到再用我们自己的中文
        }

        //区域：先取游戏自己的本地化短名（AvailableRegion.LocalizedShortName），再压成三档。
        //筛选里存的是**语言无关的代码**（AP/EU/US/原始名），显示时才翻译 -> 切界面语言不会让筛选失效。
        //游戏原始值形如 region_shortname_ap，英文下要显示成 AP（不是那串 key）。
        //
        //性能：这块以前每次调用都要 GetProperty/GetField 现查一遍成员（可视行每帧 ~14 次；
        //开了区域筛选时 PassFilter->CollectShown 会按"房间数 ×4"次/帧）。现在两级缓存：
        //  1) 按区域对象的**类型**缓存成员信息（PropertyInfo/FieldInfo 只查一次）；
        //  2) 按房间 id 缓存算出来的代码（区域在房间存活期间不变；重新搜索房间列表时清空）。
        private sealed class RegionMember { public PropertyInfo Loc; public FieldInfo Short, Name, Id; }
        private static readonly Dictionary<Type, RegionMember> _regionMembers = new Dictionary<Type, RegionMember>();
        private static readonly Dictionary<string, string> _regionCodeCache = new Dictionary<string, string>();

        private static RegionMember RegionMembers(Type t) {
            RegionMember m;
            if (_regionMembers.TryGetValue(t, out m)) return m;
            m = new RegionMember();
            try { m.Loc = t.GetProperty("LocalizedShortName"); } catch { }
            try { m.Short = t.GetField("shortName"); } catch { }
            try { m.Name = t.GetField("name"); } catch { }
            try { m.Id = t.GetField("id"); } catch { }
            _regionMembers[t] = m;
            return m;
        }

        private static string RegionCode(Matchmaker.LobbyListInfo li) {
            if (li == null) return "";
            string id = li.sLobbyID;
            string cached;
            if (!string.IsNullOrEmpty(id) && _regionCodeCache.TryGetValue(id, out cached)) return cached;
            string code = RegionCodeUncached(li);
            if (!string.IsNullOrEmpty(id)) _regionCodeCache[id] = code;
            return code;
        }

        private static string RegionCodeUncached(Matchmaker.LobbyListInfo li) {
            // **R397 根因修复**：UnityServerRegion 可能是 null，不是"认不出来"。
            // 这个字段由 FindRegionById 取，而 List.Find 找不到就返回 null 且不抛异常（外面那个 try/catch 兜不住），
            // 房主没写 unityRelayRegion 时整段还被跳过 -> 连游戏自己的房间列表都在这种房间上崩空引用。
            // 原实现这里返回 ""，而区域过滤是严格相等 -> 筛选停在 AP/EU/US 任一档时这些房间被静默过滤掉
            // （"我开的亚太房怎么找不到"）；默认配置是空=全部，所以平时看不出来。
            // 修法：null 时给一个独立代码（跟任何真实区域都不相等）—— 列表照常显示、区域列显示"未知"。
            if (li != null && li.UnityServerRegion == null) return NoneCode;
            try { return RegionCodeOf(li != null ? li.UnityServerRegion : null); } catch { return NoneCode; }
        }

        //"拿不到区域对象"的专用代码。**刻意取一个不可能与真实区域相等的值**：
        //  真实值只可能是 "AP"/"EU"/"US"/AVAILABLE_REGIONS 里某个 shortName/name/id，
        //  而下面这个串带空格和书名号，任何真实区域都匹配不上。
        private const string NoneCode = "（无区域）";

        //把一个"区域对象"压成语言无关的代码（AP/EU/US/原始名/""）。
        // **抽成独立方法的理由**：RegionVals() 也要用同一套映射（枚举 RelayConstants.AVAILABLE_REGIONS），
        //  两处各写一份必然漂移, 现在只有这一份规则。
        private static string RegionCodeOf(object r) {
            string loc = "", raw = "";
            try {
                if (r != null) {
                    RegionMember m = RegionMembers(r.GetType());
                    if (m.Loc != null) loc = m.Loc.GetValue(r, null) as string;
                    if (m.Short != null) raw = m.Short.GetValue(r) as string;
                    if (string.IsNullOrEmpty(raw) && m.Name != null) raw = m.Name.GetValue(r) as string;
                    if (string.IsNullOrEmpty(raw) && m.Id != null) raw = m.Id.GetValue(r) as string;
                }
            } catch { }
            string s = ((loc ?? "") + " " + (raw ?? "")).ToLowerInvariant();
            //按词/后缀判断：region_shortname_ap / ap-southeast / asia... 都归亚太
            if (s.Contains("europe") || s.Contains("_eu") || s.Contains("-eu") || s.Contains(" eu")) return "EU";
            if (s.Contains("asia") || s.Contains("_ap") || s.Contains("-ap") || s.Contains("_as") || s.Contains(" ap")) return "AP";
            if (s.Contains("america") || s.Contains("_us") || s.Contains("-us") || s.Contains("_na") || s.Contains(" us")) return "US";
            if (!string.IsNullOrEmpty(raw)) return raw;   //认不出来：用原始名（与语言无关）
            if (!string.IsNullOrEmpty(loc) && loc.IndexOf("Network/") < 0) return loc;
            return "";
        }

        //术语表查不到时 I2 会把 key 原样返回，这里过滤掉
        private static string ZhTerm(string key, string zh) {
            try {
                string s = I2.Loc.LocalizationManager.GetTranslation(key);
                if (!string.IsNullOrEmpty(s) && s.IndexOf("Network/") < 0 && s.IndexOf("region_") < 0 && s.IndexOf('/') < 0) return s;
            } catch { }
            return zh;
        }

        private static string DispRegion(string code) {
            //先过 T()：语言包可以覆盖"亚太/欧盟/美国"，也可以直接覆盖 "AP"/"EU"/"US" 这些英文原名
            //（原来英文系直接 return 硬编码的 "AP"，语言包拿它没办法）
            if (code == "AP") { string ap = SR.T("亚太", "AP"); return En ? ap : ZhTerm("Network/region_shortname_ap", ap); }
            if (code == "EU") { string eu = SR.T("欧盟", "EU"); return En ? eu : ZhTerm("Network/region_shortname_eu", eu); }
            if (code == "US") { string us = SR.T("美国", "US"); return En ? us : ZhTerm("Network/region_shortname_us", us); }
            //R397：区域对象为 null（游戏没给这个字段 / id 对不上）。说清楚是"取不到"而不是"某个区"，
            //免得玩家以为这是第四个区域、或者以为筛选漏了。措辞带上成因，方便对照游戏侧的问题。
            if (code == NoneCode) return SR.T("未知(无区域数据)", "unknown (no region)");
            if (string.IsNullOrEmpty(code)) return SR.T("未知", "unknown");
            return code;
        }

        private static string RegionShort(Matchmaker.LobbyListInfo li) { return DispRegion(RegionCode(li)); }

        //房间满 4 人就进不去了（游戏自己的列表也是 Players"/4"）
        private static bool IsFull(Matchmaker.LobbyListInfo li) { return li.Players >= 4; }

        private static bool IsJoinable(Matchmaker.LobbyListInfo li) {
            try {
                if (li.error || !li.InfoReceived) return false;
                if (li.matchProgress != 0) return false; //进度 != 0 = 已开局，进去会卡加载
                if (li.isAFK) return false;
                if (IsFull(li)) return false;
                return true;
            } catch { return false; }
        }

        //按钮/提示文字：把为什么不能进说清楚
        private static string JoinText(Matchmaker.LobbyListInfo li) {
            // "房间已空"必须在 IsJoinable **之前**判：游戏 CheckHostConnectivity 的 #2 就是
            //  playerCount == 0 -> 判房主已走（反编译 49152）。而 SR 的 IsJoinable 只看 Players >= 4，
            //  0 人反而算"可加入" -> 原来会在按钮上显示"加入"、点下去必然失败。
            //  提到前面才说得清楚（纯文案，不阻止点击）。
            if (li.Players == 0) return SR.T("房间已空", "empty");
            if (IsJoinable(li)) return SR.T("加入", "Join");
            if (li.error || !li.InfoReceived) return SR.T("信息不全", "no info");
            if (li.matchProgress != 0) return SR.T("对局中", "in match");
            if (li.isAFK) return SR.T("房主AFK", "host AFK");
            if (IsFull(li)) return SR.T("人满", "full");
            return SR.T("不可加入", "closed");
        }

        //房主是否已"失联"（僵尸房间）：游戏里房主**每 10 秒**上报一次心跳（反编译 49457），
        //房客侧也拿 lastHeartbeat + 10 < serverTime 判超时（49143）。
        //心跳断 10 秒 = 房间实际已死，但列表里仍显示"大厅中"，点进去必然失败, 提前标出来。
        //
        // **只用于显示标记，不参与 IsJoinable / 筛选**：lastHearbeatTime 的单位（反编译 36738 由毫秒除 1000
        //  = 秒）与 GetServerTime() 是否一致未经实机确认。万一不一致，最坏也只是标记不准，
        //  不会把好房间误判成不可加入。确认无误后再考虑加筛选（方案 D 的过滤层）。
        private static bool IsStale(Matchmaker.LobbyListInfo li) {
            try {
                if (li == null || li.lastHearbeatTime == 0u) return false;   //没上报过 / 字段缺：不猜
                uint now = 0u;
                var cl = Matchmaker.CurrentMatchmakingLobby;
                if (cl != null) now = cl.GetServerTime();
                if (now == 0u) return false;                                  //拿不到服务器时间：不猜
                return now > li.lastHearbeatTime + 10u;
            } catch { return false; }
        }

        //对局进度：游戏自己的写法（0 -> 大厅中 / 房主AFK；否则 100-matchProgress %）
        private static string ProgressText(Matchmaker.LobbyListInfo li) {
            try {
                string txt;
                if (li.matchProgress == 0) {
                    txt = li.isAFK ? G("Network/MatchProgressAFK", "房主AFK", "Host AFK")
                                   : G("Network/MatchProgressLobby", "大厅中", "In lobby");
                } else {
                    txt = (100 - li.matchProgress) + "%";
                }
                if (IsStale(li)) txt = " " + txt;   // **心跳已断**：仍显示"大厅中"但实际点不进去
                return txt;
            } catch { return "?"; }
        }

        //游戏质量：游戏自己的判定（LobbyHealthNum 对比 Good/OkMatchScore）；
        //技能匹配质量系数（0~1）默认**不显示**，单击本列表头才带上（见 HeaderClick）
        private static string QualityText(Matchmaker.LobbyListInfo li) {
            try {
                GameSettings gs = GameSettings.GetInstance();
                string s = "";
                if (gs != null) {
                    if (li.LobbyHealthNum >= gs.GoodMatchScore) s = "✓✓";
                    else if (li.LobbyHealthNum >= gs.OkMatchScore) s = "✓";
                    if (li.CalculatedSkillMatchQuality >= gs.SkillMatchQualityThreshold) s += "✓";
                }
                if (_showQualityCoeff == null || !_showQualityCoeff.Value) return s.Length > 0 ? s : "-";
                string num = li.CalculatedSkillMatchQuality.ToString("F2", CultureInfo.InvariantCulture);
                return (s.Length > 0 ? s + " · " : "") + num;
            } catch { return "?"; }
        }

        //游戏模式：默认规则集 -> 游戏本地化的模式名（中文时）；自定义规则集 -> 规则集名字
        private static string ModeText(Matchmaker.LobbyListInfo li) {
            try {
                GameSettings gs = GameSettings.GetInstance();
                bool custom = gs != null && gs.DefaultRuleset != null && li.rulePreset != gs.DefaultRuleset.Name;
                if (custom) return En ? li.rulePreset : Tr("RuleBook/Presets/" + li.rulePreset, li.rulePreset);
                if (!En) {
                    string s = GameState.GetLocalizedGameModeName(li.gameMode);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                return DispName("Filter Mode", li.gameMode.ToString());
            } catch { return li.gameMode.ToString(); }
        }

        private static string TagText(Matchmaker.LobbyListInfo li) {
            if (!En) {
                try {
                    string s = GameSettings.ConvertLobbyTag(li.lobbyTag);
                    if (!string.IsNullOrEmpty(s)) return s;
                } catch { }
            }
            return DispName("Filter Tag", li.lobbyTag.ToString());
        }

        //限制：先制胜积分（游戏列表里 pointLimit/50 的那个数）再长度限制（轮数/时间/无限制）
        private static string LimitText(Matchmaker.LobbyListInfo li) {
            try {
                bool noLimitMode = li.gameMode == GameState.GameMode.FREEPLAY || li.gameMode == GameState.GameMode.CHALLENGE;
                string win = noLimitMode ? "--" : (li.pointLimit / 50).ToString();
                string len;
                if (noLimitMode) len = G("RuleBook/NoLimit", "无限制", "no limit");
                else if (li.limitType == GameLimitType.TIME) len = (li.limitAmount / 60) + G("RuleBook/minutesAbbreviation", "分", " min");
                else if (li.limitType == GameLimitType.ROUNDS) len = li.limitAmount + G("RuleBook/Rounds", "轮", " rd");
                else len = G("RuleBook/NoLimit", "无限制", "no limit");
                return win + " / " + len;
            } catch { return "?"; }
        }

        private static string FlagsText(Matchmaker.LobbyListInfo li) {
            string s = li.usingMods ? SR.T("模组", "mods") : "";
            if (li.isAFK) s += (s.Length > 0 ? "+" : "") + "AFK";
            return s;
        }

        private static string Val(ConfigEntry<string> e) { return e != null ? (e.Value ?? "") : ""; }

        private static bool PassFilter(Matchmaker.LobbyListInfo li) {
            try {
                string v;
                v = Val(_fRegion);
                if (v.Length > 0 && RegionCode(li) != v) return false;
                v = Val(_fMode);
                if (v.Length > 0) {
                string __gm = li.gameMode.ToString();
                if (v == "OTHER") { if (__gm == "FREEPLAY" || __gm == "CREATIVE" || __gm == "PARTY" || __gm == "CHALLENGE") return false; } // **#4 其它=四种已知模式之外**
                else if (__gm != v) return false;
            }
                v = Val(_fTag);
                if (v.Length > 0 && li.lobbyTag.ToString() != v) return false;
                v = Val(_fMinPlayers);
                if (v == "lt4") { if (li.Players >= 4) return false; }
                else {
                    int min;
                    if (v.Length > 0 && int.TryParse(v, out min) && li.Players < min) return false;
                }
                v = Val(_fState);
                if (v == "join") { if (!IsJoinable(li)) return false; }        //可加入：游戏自己的"能不能进"判据
                else if (v == "full") { if (!IsFull(li)) return false; }        //人满
                else if (v == "playing") { if (li.matchProgress == 0) return false; } //对局中：进度 != 0
                v = Val(_fAfk);
                if (v == "hide" && li.isAFK) return false;
                if (v == "only" && !li.isAFK) return false;
                return true;
            } catch { return true; }
        }

        private static int CountShown() {
            int n = 0;
            for (int i = 0; i < _lobbies.Count; i++) {
                Matchmaker.LobbyListInfo li = _lobbies[i];
                if (li == null || !PassFilter(li)) continue;
                if (_hidden.Contains(li.sLobbyID)) continue;
                n++;
            }
            return n;
        }

        //排序：默认 = **可加入的永远排最上面**，其内部按房间健康分降序（与游戏自带列表一致）；
        //其余按进度；最后用 sLobbyID 兜底（List.Sort 不稳定，键相同会每帧互换 -> 表格闪）。
        // 用户在表头选的排序列（_sortCol >= 0）**优先**，此时不再套用"可加入优先",
        //  否则排序意图会被打乱（比如按人数降序，可加入的却仍被硬拉到最前面）。
        private static void SortLobbies() {
            try {
                _lobbies.Sort((a, b) => {
                    if (ReferenceEquals(a, b)) return 0;
                    if (a == null) return 1;
                    if (b == null) return -1;
                    if (_sortCol >= 0) {
                        int r = CompareCol(_sortCol, a, b);
                        if (r != 0) return _sortDesc ? -r : r;
                        return string.Compare(a.sLobbyID, b.sLobbyID, StringComparison.Ordinal);
                    }
                    bool ja = IsJoinable(a), jb = IsJoinable(b);
                    if (ja != jb) return ja ? -1 : 1;
                    // 可加入组**内部**按游戏自己的 CombinedHealthSkill 降序（反编译 47865）。
                    //  该值后端返回时就已算好（35624 = LobbyHealthNum + LobbySkillNum），直接用，零成本。
                    //  原来这里直接比 matchProgress, 而可加入的房间 matchProgress 恒为 0，
                    //  于是比较键恒等、实际退化成按 sLobbyID 排，对玩家等于**随机顺序**；
                    //  游戏则是按房间质量从好到坏排的。
                    //  只在"两边都可加入"时套用，避免打乱满员/对局中两组的既有次序。
                    if (ja) {
                        int h = b.CombinedHealthSkill.CompareTo(a.CombinedHealthSkill);   //降序
                        if (h != 0) return h;
                    }
                    int c = a.matchProgress.CompareTo(b.matchProgress);
                    if (c != 0) return c;
                    return string.Compare(a.sLobbyID, b.sLobbyID, StringComparison.Ordinal);
                });
            } catch (Exception __ex) { SR.Guard.Log("房间排序", __ex); }
        }

        //按某一列比较两个房间。 数值列必须用**真实数值**，不能用显示文本（文本下 "10" < "2"）；
        //其余列（区域/模式/标签/房主/限制/标记）用显示文本比较即可。
        private static int CompareCol(int ci, Matchmaker.LobbyListInfo a, Matchmaker.LobbyListInfo b) {
            switch (ci) {
                case 1: return a.Players.CompareTo(b.Players);
                case 2: return a.LobbyHealthNum.CompareTo(b.LobbyHealthNum);
                case 3: return SkillValue(a).CompareTo(SkillValue(b));
                case 4: return a.matchProgress.CompareTo(b.matchProgress);
                case CombinedCol: return a.CombinedHealthSkill.CompareTo(b.CombinedHealthSkill);
                default: {
                    string[] ca = CellTexts(a), cb = CellTexts(b);
                    string ta = ci < ca.Length ? ca[ci] : null, tb = ci < cb.Length ? cb[ci] : null;
                    return string.Compare(ta ?? "", tb ?? "", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        //刷新统计文案（原来在每个房间回调里各算一次 CountShown，100 个房间 = 1 万次过滤）
        //已隐藏房间数：只数"当前列表里还存在"的。
        //_hidden 跨刷新累积（旧房间的 id 一直留在集合里），直接用 _hidden.Count 会把
        //已经不存在的房间也报进"已隐藏 N 个" -> 数字偏大且对不上界面上真实少掉的那些。
        private static int CountHidden() {
            if (_hidden.Count == 0) return 0;   //绝大多数时候没隐藏过任何房间：不必每帧遍历整个列表
            int n = 0;
            try {
                for (int i = 0; i < _lobbies.Count; i++) {
                    Matchmaker.LobbyListInfo li = _lobbies[i];
                    if (li != null && _hidden.Contains(li.sLobbyID)) n++;
                }
            } catch { }
            return n;
        }

        //S19: _hidden 只增不减(全文件唯一清理点是 ResetTable 里的 _hidden.Clear()), 而 sLobbyID
        //在 Steam 是 GUID 字符串, 频繁刷新遇到新房间就可能新增一条, 长期挂机或频繁刷新会一直累积,
        //CountShown / CollectShown 里的 _hidden.Contains 也会随集合变大线性变慢.
        //在 搜索结束 的那一帧做一次交集裁剪: 当前列表里还存在的 id 才留下.
        //C# 7.3 / net472 的 HashSet 没有 RemoveWhere, 用 IntersectWith(原地求交集, 不额外分配).
        private static void PruneHidden() {
            if (_hidden.Count == 0) return;
            try {
                HashSet<string> alive = new HashSet<string>();
                for (int i = 0; i < _lobbies.Count; i++) {
                    Matchmaker.LobbyListInfo li = _lobbies[i];
                    if (li != null && !string.IsNullOrEmpty(li.sLobbyID)) alive.Add(li.sLobbyID);
                }
                _hidden.IntersectWith(alive);
            } catch (Exception __ex) { SR.Guard.Log("裁剪已隐藏房间集合", __ex); }
        }

        private static void UpdateLobbyMsg() {
            _lobbyMsg = string.Format(SR.T("共 {0} 个房间，筛选后 {1} 个", "{0} lobbies, {1} after filters"), _lobbies.Count, CountShown());
        }

        // 页面：联机
        private static void RenderOnlinePage() {
            //本页不需要页面级滚动：表头/筛选必须一直可见，房间只在下面的框里滚
            SR.PageScroll = Vector2.zero;
            SR.NoPageScrollBars = true;   //页面级横向/纵向滚动条都不要（原来那两个既用不到、又因为 Scroll 被写回而拖不动）
            if (Event.current != null && Event.current.type == EventType.MouseUp) {
                if (_tableArgs.dragCol >= 0) { _tableArgs.dragCol = -1; SR.BlockWindowDrag = false; SaveWidths(); }
                if (_dragH) { _dragH = false; SR.BlockWindowDrag = false; SaveTableH(); }
            }
            EnsureWidths();
            //页面内容在**布局空间**里的起点 y（零高度探针）：用来算"表格上方用掉多少高度"。
            //（必须用同一个坐标系里的差值；ContentArea 是屏幕坐标，混着算会让表格框大出一截, 见下面 availW/availH）
            Rect topProbe = GUILayoutUtility.GetRect(0f, 0f, GUIStyle.none);
            if (Event.current != null && Event.current.type == EventType.Layout) _pageTopY = topProbe.y;

            //按钮顺序：刷新列表 / 退出联机（= 原来的返回主界面，两者本来就是同一条游戏流程）/ 房间码
            GUILayout.BeginHorizontal();
            if (SR.Ctl.HotkeyButton("online.lobbies", SR.T("刷新列表", "Refresh"),
                SR.T("向匹配服务查一次公开房间, 能加入的排在表格最上面, 点里面的加入就行.\n如果你正待在某个房间里, 点加入会先退出当前房间, 退完立刻自动进新的.",
                     "Query the public lobby list, joinable lobbies on top. Click Join in the table.\nIf you are already in a lobby, clicking Join leaves it first and joins the new one right away."),
                RefreshLobbies, SR.Ctl.Sc(140), SR.Ctl.Sc(30))) RefreshLobbies();
            if (SR.Ctl.HotkeyButton("online.disband", SR.T("返回主界面", "Back to main menu"),
                SR.T("回主界面, 走的就是游戏自己的离开房间流程. 房主会先广播 HostEndedGame, 全员一起回去; 房客只退自己.\n卡在加载动画上的话, 再点一次这个按钮就是强制立刻回去, 不等游戏自己的加载流程. 不点的话会自己兜底: 5 秒放遮罩, 8 秒硬收尾, 再过 2 秒直接切场景.",
                     "Back to the main menu, using the game's own leave-lobby flow. Host: broadcasts HostEndedGame and everyone returns; guest: only you leave.\nStuck on the loading animation? Click this button again to force an immediate return. Otherwise it self-recovers at 5s / 8s / 10s."),
                DisbandOrLeave, SR.Ctl.Sc(140), SR.Ctl.Sc(30))) DisbandOrLeave();
            //房间码加入（游戏本身支持按码加入：Matchmaker.JoinLobby(code, useCode:true)）
            GUILayout.Space(SR.Ctl.Sc(14));
            GUILayout.Label(new GUIContent(SR.T("房间码", "Lobby code"),
                SR.T("输入 4 个大写字母的房间码直接加入（和游戏里按码加入一样；在对局内会先退出当前房间）。",
                     "Enter the 4-letter lobby code to join directly (same as the game's join-by-code; while in a session it leaves the current one first).")),
                SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(CodeLabelW)), GUILayout.Height(SR.Ctl.Sc(30)));
            string typed = SR.ClearableTextField(_joinCode ?? "", 4, SR.Ctl.SearchBox,
                GUILayout.Width(SR.Ctl.Sc(62)), GUILayout.Height(SR.Ctl.Sc(24)));
            _joinCode = CodeOnly(typed);   //只收大写英文字母，最多 4 个
            bool enter = Event.current != null && Event.current.type == EventType.KeyDown
                         && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                         && _joinCode.Length == 4;
            bool oldEnabled = GUI.enabled;
            GUI.enabled = _joinCode.Length == 4;   //不足 4 位不让点（避免把半个码发出去）
            bool clickJoin = GUILayout.Button(SR.T("加入", "Join"), SR.Ctl.Btn, GUILayout.Width(SR.Ctl.Sc(46)), GUILayout.Height(SR.Ctl.Sc(24)));
            GUI.enabled = oldEnabled;
            if (_joinCode.Length == 4 && (clickJoin || enter)) JoinByCode(_joinCode);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // 搜索结束的**权威信号**是 Matchmaker.Searching：游戏在最后一条房间回调之后才置 false（反编译 35627）。
            //  原来只用 6 秒超时猜, 后端慢于 6 秒时会提前宣布"搜索结束"，之后陆续到达的房间**静默插入**
            //  （玩家看到列表自己变长）；快于 6 秒时"搜索中..."又多挂几秒才消失。
            //  现在以游戏自己的标志为准，超时只作兜底（匹配器异常时不至于永远卡在"搜索中..."），故放宽到 15 秒。
            //  Cecil 已核：Searching 是 Matchmaker 的**实例**属性（public get / protected set），必须经实例读；
            //  先用 IsInstantiated 问一下，避免读属性顺带触发 .Instance 的惰性创建。
            if (_lobbySearching) {
                bool gameDone = false;
                try { if (Matchmaker.IsInstantiated) gameDone = !Matchmaker.Instance.Searching; } catch { }
                if (gameDone || Time.unscaledTime - _lobbySearchAt > SearchTimeout()) {
                    _lobbySearching = false;
                    PruneHidden();   //S19 就在 搜索结束 这一帧裁剪一次
                }
            }
            //列表/可见性变了就重排并刷新统计；否则最多每 1 秒重排一次（房间字段可能在后台被异步填充）
            if (_lobbiesDirty || Time.unscaledTime - _lobbySortAt >= 1f) {
                //统计文案只在"列表真的变了"或"搜索已结束"时才刷新：房间是**逐条**回调的，
                //纯 1 秒兜底时往往一条都还没到, 无条件刷新会把"搜索中..."覆盖成"共 0 个房间，筛选后 0 个"，
                //玩家会以为搜索已结束且没有房间。（搜索是否结束以上面的权威信号为准。）
                //错误提示粘住：_lobbyErr 时不覆盖 _lobbyMsg（见字段注释）
                bool refreshMsg = !_lobbyErr && (_lobbiesDirty || !_lobbySearching);
                _lobbiesDirty = false;
                _lobbySortAt = Time.unscaledTime;
                SortLobbies();
                if (refreshMsg) UpdateLobbyMsg();
            }
            string hint = SR.T("（点刷新列表查询）表头：左键点=按该列排序（升->降->默认），拖分隔线=调列宽（<->），拖表头=换列位置（+），Shift+点游戏质量=显示/隐藏匹配质量系数，Shift+点技能系数=最大/平均/最小，右键=隐藏该列，Shift+右键=还原；右键某行=隐藏该房间；滚轮=上下，Ctrl+滚轮=左右；表格底边可拖高度",
                               "(click 'Refresh' to query) Header: left-click = sort by that column (asc/desc/default), drag a separator = resize (<->), drag a header = reorder (+), Shift+click 'Quality' = show/hide the skill-match coefficient, Shift+click 'Skill' = max/avg/min, right-click = hide that column, Shift+right-click = reset; right-click a row = hide that lobby; wheel = up/down, Ctrl+wheel = left/right; drag the table's bottom edge to resize height");
            GUILayout.Label(string.IsNullOrEmpty(_lobbyMsg) ? hint : _lobbyMsg, SR.Ctl.Label);
            int hiddenNow = CountHidden();   //见 CountHidden：只数当前列表里还存在的
            if (hiddenNow > 0) {
                GUILayout.Label(string.Format(SR.T("已隐藏 {0} 个房间（Shift+右键表格可恢复）", "{0} lobbies hidden (Shift+right-click the table to restore)"), hiddenNow), SR.Ctl.Label);
            }

            // 筛选：第一行 区域/模式/标签，第二行 人数/AFK（下拉框固定宽度，同时只开一个）
            //（次级的排序下拉已按需求移除：可加入的永远在最上面，其余按进度）
            float cellW = Mathf.Max(SR.Ctl.Sc(120), (SR.Ctl.WinWidth - SR.Ctl.SidebarWidth() - SR.Ctl.Sc(40)) / 3f - SR.Ctl.Sc(10));
            GUILayout.Space(SR.Ctl.Sc(2));
            GUILayout.BeginHorizontal();
            DropFilter("区域", "Region", "按服务器区域筛选（亚太/欧盟/美国）。", "Filter by server region (AP / EU / US).", "Filter Region", RegionVals(), cellW);
            DropFilter("模式", "Mode", "按游戏模式筛选。", "Filter by game mode.", "Filter Mode", ModeVals, cellW);
            DropFilter("标签", "Tag", "按房间标签筛选。", "Filter by lobby tag.", "Filter Tag", TagVals, cellW);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            DropFilter("人数", "Players", "按人数筛选：≥N 或<4（= 还有空位）。", "Filter by players: at least N, or '< 4' (has a free slot).", "Filter Min Players",
                PlayerVals, cellW);
            DropFilter("AFK", "AFK", "房主 AFK 筛选（游戏自己的 isAFK 字段）。", "Filter by host AFK (the game's own isAFK field).", "Filter AFK",
                AfkVals, cellW);
            DropFilter("状态", "State", "按房间状态筛选：可加入 / 人满 / 对局中。", "Filter by lobby state: joinable / full / in match.", "Filter State", StateVals, cellW);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // 表格：自绘（表头固定、只有房间行滚动；滚动条自绘 -> 跟随界面缩放、贴右边界、拖到列表外也不断）
            GUILayout.Space(SR.Ctl.Sc(3));
            Rect last = GUILayoutUtility.GetLastRect();
            float footerH = SR.FooterHeight > 0f ? SR.FooterHeight : SR.Ctl.Sc(30);
            // 宽度必须用**纯尺寸**算：布局矩形和屏幕矩形（ContentArea）不在同一个坐标系里，
            //  原来写 ContentArea.xMax - last.x 会把"屏幕右边界"减掉一个布局坐标 -> 表格框比可视区宽一大截，
            //  最右边的操作列被推到屏幕外（"操作那一栏怎么没了"）、右边界滚动条也看不见。
            float availW = Mathf.Max(SR.Ctl.Sc(220), SR.Ctl.WinWidth - SR.Ctl.SidebarWidth() - SR.Ctl.Sc(12));
            //高度同理：用"页面可视高度（屏幕量） - 表格上方已用高度（布局量之差）"
            float pageH = SR.ContentArea.height - SR.Ctl.Sc(26) - SR.Ctl.Sc(2) - footerH;
            float usedAbove = _pageTopY > 0f ? Mathf.Max(0f, last.yMax - _pageTopY) : 0f;
            float availH = Mathf.Max(SR.Ctl.Sc(140), pageH - usedAbove - SR.Ctl.Sc(8));
            //S20: 拖动进行中时 _tableH.Value 还是旧值(拖动只写 _dragHTmp), 这里必须读临时值,
            //否则表格高度不会跟手变化. 非拖动时读配置, 行为与原来一致.
            float hPx = _dragH ? _dragHTmp : (_tableH != null ? _tableH.Value : 0);
            float boxH = hPx > 0f
                ? Mathf.Clamp(SR.Ctl.Sc(hPx), SR.Ctl.Sc(120), availH)
                : availH;
            List<int> vis = VisibleCols();
            Rect box = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.Width(availW), GUILayout.Height(boxH));
            TableDraw(box, vis);
            TableInput(box, vis, Event.current);
            HeightGrip();   //表格底边：拖动改高度
        }

        //表格底边拖动条 + Shift+右键还原（列宽/高度/隐藏）
        private static void HeightGrip() {
            Event e = Event.current;
            Rect r = GUILayoutUtility.GetLastRect();
            GUILayout.Space(SR.Ctl.Sc(2));
            GUILayout.Box(GUIContent.none, SR.Ctl.Footer, GUILayout.Height(SR.Ctl.Sc(4)), GUILayout.ExpandWidth(true));
            Rect grip = GUILayoutUtility.GetLastRect();
            if (e == null) return;
            if (e.type == EventType.MouseDown && e.button == 0 && grip.Contains(e.mousePosition)) {
                _dragH = true;
                _dragHY0 = e.mousePosition.y;
                _dragH0 = _tableH != null && _tableH.Value > 0 ? _tableH.Value : (r.height / Mathf.Max(0.0001f, SR.Ctl.Sc(1f)));
                //S20: 临时值保持 0 表示 本次按下后还没有真正的 MouseDrag. 这样只点一下不拖就松手时,
                //SaveTableH 不会把 自动高度(配置值 0) 误写成固定高度, 与改动前行为一致.
                _dragHTmp = _tableH != null && _tableH.Value > 0 ? _tableH.Value : 0f;
                SR.BlockWindowDrag = true;
                e.Use();
            } else if (e.type == EventType.MouseDrag && _dragH) {
                //同列宽拖动：_dragH0 是设计单位（上面初始化时已经除过一次 Sc(1f)），
                //而鼠标位移是屏幕像素, 这里也必须再除一次，否则表格高度长得比鼠标快。
                int v = Mathf.Clamp(Mathf.RoundToInt(_dragH0 + (e.mousePosition.y - _dragHY0) / Mathf.Max(0.0001f, SR.Ctl.Sc(1f))), 120, 1000);
                //S20: 拖动期间不写 _tableH.Value(每帧写会走 setter -> SettingChanged 事件 + 范围校验),
                //只累加到 _dragHTmp, 松手那一帧由 SaveTableH 一次性落进配置.
                _dragHTmp = v;
                SR.BlockWindowDrag = true;
                e.Use();
            }
        }

        //落盘统一交给 SR.Ctl.MarkConfigDirty（-> FlushConfig 每秒最多一次 + 失败有 Warning）。
        //原来是 ConfigFile.Save() + catch { }：写盘失败（权限/磁盘满/文件被占用）时用户改了表高/列宽，
        //重启发现没保存，日志一行都没有, 正是 SR.Window 里"配置写盘失败以前是静默的"那条规矩要避免的。
        //这里都是离散操作（拖拽松手、点击表头），经节流器落盘仍即时够用。
        //S20: 拖动期间高度只写 _dragHTmp, 到落盘这一帧才同步给 _tableH.Value.
        //放在这里而不是 MouseUp 分支里, 是因为核心的拖拽看门狗(TableDragWatchdog 复位 _dragH 时
        //同样会调 SaveTableH)也能把拖出来的值补写进配置, 不会丢.
        private static void SaveTableH() {
            if (_tableH != null) {
                if (_dragHTmp > 0f) {
                    int h = Mathf.RoundToInt(_dragHTmp);
                    if (h != _tableH.Value) _tableH.Value = h;
                }
                SR.Ctl.MarkConfigDirty(_tableH.ConfigFile);
            }
        }

        private static void SaveEntry(ConfigEntryBase e) { if (e != null) SR.Ctl.MarkConfigDirty(e.ConfigFile); }

        // 表格（自绘：固定表头 + 房间行 + 自绘竖向滚动条）
        //为什么自绘：嵌套 ScrollView 会同时冒出"页面竖条 + 表格横条"两个用不到的滚动条，
        //它们的宽度不随界面缩放（和 SR 界面不匹配，拖动一离开列表就断）。
        //这里几何全部是绝对矩形：表头/房间行按 _scrollCols.x 手动偏移，行再按 _scrollRows.y 偏移，
        //竖向滚动条自己画自己拖, 拖动状态跨帧保持，鼠标离开列表照样跟手，松手才结束。
        private const int HostCol = 7;                 //房主列：靠左显示（其余列居中）
        private const int QualityCol = 2;              //游戏质量列：单击表头 = 显示/隐藏技能匹配质量系数
        private const int SkillCol = 3;                //技能系数列：单击表头 = 最大/平均/最小
        private const float CodeLabelW = 58f;          //房间码标签宽度（46 太窄，3 个汉字显示不全）

        //单击表头（没有拖动）= 该列自己的切换动作
        //左键点表头 = 按该列排序（升 -> 降 -> 默认，三态循环，见 CycleSort）。
        // 与原来两个"列专属开关"的冲突：那套原本也挂在左键上，现改为 **Shift+左键**，
        //  把左键让给排序（更符合表格的通用直觉，也是优化方案 N 的目标）。
        private static void HeaderClick(int ci) {
            try {
                bool shift = false;
                try { shift = Event.current != null && Event.current.shift; } catch { }
                if (shift) {
                    if (ci == QualityCol) {
                        bool v = _showQualityCoeff != null && !_showQualityCoeff.Value;
                        if (_showQualityCoeff != null) {
                            _showQualityCoeff.Value = v;
                            SaveEntry(_showQualityCoeff);
                        }
                        Notes(v ? "游戏质量：显示技能匹配系数" : "游戏质量：仅显示 ✓",
                              v ? "Quality: showing the skill-match coefficient" : "Quality: marks only");
                        return;
                    }
                    if (ci == SkillCol) {
                        string cur = SkillModeCfg();
                        string next = cur == "max" ? "avg" : cur == "avg" ? "min" : "max";   //最大 -> 平均 -> 最小
                        if (_skillMode != null) {
                            _skillMode.Value = next;
                            SaveEntry(_skillMode);
                        }
                        //Notes(zh, en) 两个参数都会求值 -> 先取一次名字，别让 SkillModeName 跑两遍
                        string skillName = SkillModeName(next);
                        Notes(SR.T("技能系数：", "Skill coefficient: ") + skillName, "Skill coefficient: " + skillName);
                        return;
                    }
                }
                CycleSort(ci);
            } catch (Exception __ex) { SR.Guard.Log("表头点击", __ex); }
        }

        //列排序三态循环：升序 -> 降序 -> 默认（回到游戏自己的顺序）
        private static void CycleSort(int ci) {
            try {
                if (ci < 0 || ci >= ColCount || ci == JoinCol) return;   //操作列没有可排序语义
                if (_sortCol != ci) { _sortCol = ci; _sortDesc = false; }
                else if (!_sortDesc) { _sortDesc = true; }
                else { _sortCol = -1; _sortDesc = false; }
                if (_sortColEntry != null) { _sortColEntry.Value = _sortCol; SaveEntry(_sortColEntry); }
                if (_sortDescEntry != null) { _sortDescEntry.Value = _sortDesc; SaveEntry(_sortDescEntry); }
                SortLobbies();
                //Notes(zh, en) 两个实参都会求值 -> 先拼一次再传
                string nm = SR.T(Titles[ci], TitlesEn[ci]);
                string state = _sortCol < 0 ? SR.T("默认排序（可加入优先）", "default order (joinable first)")
                             : (_sortDesc ? SR.T("降序", "descending") : SR.T("升序", "ascending"));
                string msg = nm + " · " + state;
                Notes(msg, msg);
            } catch (Exception __ex) { SR.Guard.Log("列排序切换", __ex); }
        }

        private static string SkillModeCfg() {
            string v = _skillMode != null ? (_skillMode.Value ?? "avg") : "avg";
            return v == "max" || v == "min" ? v : "avg";
        }

        private static string SkillModeName(string mode) {
            if (mode == "max") return SR.T("最大技能", "max");
            if (mode == "min") return SR.T("最小技能", "min");
            return SR.T("平均技能", "average");
        }
        private static readonly List<Matchmaker.LobbyListInfo> _shownLobbies = new List<Matchmaker.LobbyListInfo>();
        private static readonly List<Rect> _headerCellsPage = new List<Rect>();   //可滚动列的表头单元格（页面坐标）
        private static readonly List<int> _headerCols = new List<int>();          //上表每个单元格 -> 列索引
        private static readonly List<Rect> _rowRectsPage = new List<Rect>();      //房间行（页面坐标，已按可视区裁剪）
        private static readonly List<int> _rowRows = new List<int>();             //房间行 -> _shownLobbies 下标
        private static readonly List<Rect> _joinRectsPage = new List<Rect>();     //加入按钮（页面坐标，钉在右边）
        private static readonly List<int> _joinRows = new List<int>();            //加入按钮 -> _shownLobbies 下标
        private static bool _sbDragging;                //正在拖竖向滚动条
        private static float _sbGrabY;                  //按下时鼠标相对滑块顶部的偏移
        private static float _tableMaxX, _tableMaxY;
        private static float _joinW, _stripW, _joinXPage;  //钉在右边的操作列几何
        private static float _pageTopY;                 //页面内容在布局空间里的起点 y（见 RenderOnlinePage）
        private static int _joinDownRow = -1;           //加入按钮按下的行（仅用于高亮）
        private static string _joinDownId;              //按下时该行的 sLobbyID（松开时按它定位，避免重排点错房）
        private static ulong _pendingSteamId; // **Steam 平台**：要加入的房间号（LobbyListInfo.UnityMatchID，反编译 44607）
        private static float _dragWatchdog;             //拖拽兜底：太久没有鼠标事件就复位（不用 Input.GetMouseButton，见 TableInput）
        private static int _clickFrame = -1;            //表头单击一帧只处理一次
        private static readonly SR.TableArgs _tableArgs = new SR.TableArgs(); //统一入口参数（复用，避免每帧分配）

        //当前会显示的房间（筛选 + 未隐藏），顺序已由 SortLobbies 排好
        private static void CollectShown() {
            _shownLobbies.Clear();
            for (int i = 0; i < _lobbies.Count; i++) {
                Matchmaker.LobbyListInfo li = _lobbies[i];
                if (li == null || !PassFilter(li)) continue;
                if (_hidden.Contains(li.sLobbyID)) continue;
                _shownLobbies.Add(li);
            }
        }

        //可视行的单元格文本数组。
        // **P1-13**：原来是"每帧对每个可见房间 _cellBuf.Clone()"——滚动/刷新时每帧 N 次数组分配。
        //  改成**按房间跨帧复用**同一份数组：只在某房间第一次出现时分配，之后原地覆盖 11 个槽位。
        //  仍然保持"按帧缓存"的语义（同一帧内同一房间返回同一实例，IMGUI 的 Layout/Repaint/输入
        //  多次调用只算一遍），只是不再每帧重新分配。
        //  上限兜底：房间对象是游戏每次刷新时重建的，长期不清会把已废弃对象强引用住 -> 超上限整体清空。
        private static readonly System.Collections.Generic.Dictionary<Matchmaker.LobbyListInfo, string[]> _cellStore =
            new System.Collections.Generic.Dictionary<Matchmaker.LobbyListInfo, string[]>();
        private const int CellStoreMax = 256;
        //按帧缓存每个房间的单元格文本：IMGUI 一帧有 Layout+Repaint+N 个输入事件，
        //原来每次都重跑 PlayerSkills 拆分 / I2 术语查询 / 字符串格式化。
        private static readonly System.Collections.Generic.Dictionary<Matchmaker.LobbyListInfo, string[]> _cellCache =
            new System.Collections.Generic.Dictionary<Matchmaker.LobbyListInfo, string[]>();
        private static int _cellCacheFrame = -1;
        private static string[] CellTexts(Matchmaker.LobbyListInfo li) {
            if (_cellCacheFrame != Time.frameCount) { _cellCacheFrame = Time.frameCount; _cellCache.Clear(); }
            string[] hit;
            if (_cellCache.TryGetValue(li, out hit)) return hit;
            string[] buf;
            if (!_cellStore.TryGetValue(li, out buf) || buf == null || buf.Length != ColCount) {
                if (_cellStore.Count >= CellStoreMax) _cellStore.Clear();
                buf = new string[ColCount];
                _cellStore[li] = buf;
            }
            buf[0] = RegionShort(li);
            buf[1] = li.Players + "/4";
            buf[2] = QualityText(li);
            buf[3] = SkillText(li);
            buf[4] = ProgressText(li);
            buf[5] = ModeText(li);
            buf[6] = TagText(li);
            buf[7] = string.IsNullOrEmpty(li.LobbyOwner) ? "?" : li.LobbyOwner;
            buf[8] = LimitText(li);
            buf[9] = FlagsText(li);
            buf[CombinedCol] = li.CombinedHealthSkill.ToString(CultureInfo.InvariantCulture);
            buf[JoinCol] = "";
            _cellCache[li] = buf;
            return buf;
        }

        private static void TableDraw(Rect box, List<int> vis) {
            Event e = Event.current;
            Vector2 mp = e != null ? e.mousePosition : Vector2.zero;
            CollectShown();
            float rowH = SR.Ctl.Sc(26), headerH = SR.Ctl.Sc(24);
            float rowsH = Mathf.Max(SR.Ctl.Sc(20), box.height - headerH);
            float contentH = _shownLobbies.Count * rowH;
            bool overflow = contentH > rowsH + 0.5f;                    //只有真的超出才画滚动条
            float sbW = overflow ? SR.Ctl.Sc(13) : 0f;
            float viewW = Mathf.Max(SR.Ctl.Sc(60), box.width - sbW);
            //操作列钉在表格右边缘：不参与横向滚动 -> 加入按钮永远可见、可点
            //（原来它是最后一列，列宽总和超出可视宽度时它整个被裁在视野外，文字就"没碰到边线却消失"）
            float joinW = 0f;
            for (int i = 0; i < vis.Count; i++) if (vis[i] == JoinCol) joinW = SR.Ctl.Sc(_cw[JoinCol]);
            float stripW = Mathf.Max(SR.Ctl.Sc(40), viewW - joinW);     //可横向滚动的列区
            float totalW = 0f;
            for (int i = 0; i < vis.Count; i++) if (vis[i] != JoinCol) totalW += SR.Ctl.Sc(_cw[vis[i]]);
            _joinW = joinW;
            _stripW = stripW;
            _joinXPage = box.x + viewW - joinW;
            _rowsClipPage = new Rect(box.x, box.y + headerH, stripW, rowsH);
            _tableMaxX = Mathf.Max(0f, totalW - stripW);
            _tableMaxY = Mathf.Max(0f, contentH - rowsH);
            // **G1 修复**：这里原来有一行 _scrollCols = _scrollColsAtDrag;（注释称"拖列期间锁住横向滚动"）。
            //  但 _scrollColsAtDrag 这个字段全文件从未被赋值（声明处只给了 Vector2.zero 初值）->
            //  它实际做的是"拖列期间把横向滚动强制归零"，表现是表格一拖就跳回最左，松手后也停在最左。
            //  拖动期间不去写 _scrollCols 本身就已经是"锁住"了（没有别的代码在拖列时改它），故直接删除。
            _scrollCols.x = Mathf.Clamp(_scrollCols.x, 0f, _tableMaxX);
            _scrollRows.y = Mathf.Clamp(_scrollRows.y, 0f, _tableMaxY);
            if (e == null) return;
            if (e.type == EventType.Repaint) GUI.Box(box, GUIContent.none, SR.Ctl.Footer);
                // **修复**：行绘制调用之前丢失了 -> 表头在、数据在，却一行都不画（"刷新后没有数据"的真因）
                DrawTableRows(vis, box, headerH, rowH, stripW, rowsH, viewW, mp);
        }


        private static void DrawTableRows(List<int> vis, Rect box, float headerH, float rowH, float stripW, float rowsH, float viewW, Vector2 mp) {
            // _rowRows 必须和 _rowRectsPage 一起清空：它俩是"行矩形 -> _shownLobbies 下标"的一一对应，
            //   只清矩形不清下标的话，下标会跨帧累积 -> 右键命中的是上一帧（甚至更早）那一行 ->
            //   表现就是"对着某个房间右键，隐藏的是上面那一个房间"。
            //表头/行命中/行绘制统一交给核心入口（表头在别处画 -> drawHeader=false）
            _tableArgs.box = box; _tableArgs.rowsClip = _rowsClipPage;
            _tableArgs.headerH = headerH; _tableArgs.rowH = rowH; _tableArgs.rowsH = rowsH; _tableArgs.stripW = stripW;
            _tableArgs.colW = _cw; _tableArgs.vis = vis; _tableArgs.rowCount = _shownLobbies.Count;
            _tableArgs.pinCol = JoinCol; _tableArgs.alignLeftCol = HostCol;
            _tableArgs.scrollX = _scrollCols.x; _tableArgs.scrollY = _scrollRows.y; _tableArgs.mp = mp;
            _tableArgs.pinW = _joinW; _tableArgs.pinX = _joinXPage; _tableArgs.drawHeader = true;   // **#2 表头交核心画**
            // #2 核心画表头需要列标题回调（原来只有本页自绘时有）-> 补上，否则表头空白
            _tableArgs.title = delegate(int ci) { return SR.T(Titles[ci], TitlesEn[ci]); };
            _tableArgs.pinCol = JoinCol;
            _tableArgs.pinTitle = SR.T(Titles[JoinCol], TitlesEn[JoinCol]);
        // **#6 隐藏列能力接到核心**：核心的 TableDraw 负责"右键隐藏 / Shift+右键还原全部 / 表头右端 +N 恢复"
        _tableArgs.hidden = _hiddenCols; _tableArgs.hideable = true;
        // 列宽拖动 / 列头换位**全部交核心**（TableColResize / TableColMove）：本页只提供列定义与回调。
        //  状态（dragCol / moveCol）也由核心持有，本页不再自己存一份、更不镜像回去（镜像会清掉核心状态）。
        _tableArgs.resizable = true;
        _tableArgs.reorderable = true;
        // **#2 交给核心换位/单击排序**：把本页的列序源与回调注入核心
        _tableArgs.order = _order;
        _tableArgs.applyOrder = delegate(int[] o) { _order = o; _visCache = null; SaveCols(); };
            // #4 Shift+右键=还原全部：原来这里**没有 resetAll** -> 房间列表的 Shift+右键没有任何东西可还原
            //  （核心只清内存里的 hidden、并按 defaultColW 还原；而 Online 两者都不是持久源）-> 接到本表自己的还原例程。
            _tableArgs.resetAll = delegate { ResetTable(); };
        _tableArgs.click = HeaderClickOnce;
            _tableArgs.rowCells = delegate(int r) { return CellTexts(_shownLobbies[r]); };
            _tableArgs.rowAlpha = delegate(int r) { return IsJoinable(_shownLobbies[r]) ? 1f : 0.55f; };
            _tableArgs.headerCells = _headerCellsPage; _tableArgs.headerCols = _headerCols;
            _tableArgs.rowRects = _rowRectsPage; _tableArgs.rowRows = _rowRows;
            _tableArgs.pinRects = _joinRectsPage; _tableArgs.pinRows = _joinRows;
            _tableArgs.emptyHint = delegate { return SR.T("（没有符合筛选的房间）", "(no lobbies match the filters)"); };
            SR.TableDraw(_tableArgs);
            // #1 空提示改由核心的 emptyHint 画（在表格行区裁剪内）-> 打开筛选下拉框时不会再错位
            // 钉住的操作列：加入按钮（永远在表格右边缘、永远可点）
            if (_joinW > 0f) {
                int vk = 0;
                GUI.BeginClip(new Rect(_joinXPage, box.y + headerH, _joinW, rowsH));
                try {
                for (int i = 0; i < _shownLobbies.Count; i++) {
                    float y = i * rowH - _scrollRows.y;
                    if (y > rowsH || y + rowH < 0f) continue;
                    Matchmaker.LobbyListInfo li = _shownLobbies[i];
                    bool can = IsJoinable(li);
                    Rect rp = vk < _joinRectsPage.Count ? _joinRectsPage[vk] : new Rect(0f, 0f, 0f, 0f);
                    vk++;
                    bool hov = can && rp.Contains(mp);
                    Color pc = GUI.color;
                    if (!can) GUI.color = new Color(1f, 1f, 1f, 0.5f);
                    //窄格子按钮：内边距小的样式，宽度够就不该把文字裁掉
                    SR.Ctl.BtnClip.Draw(new Rect(SR.Ctl.Sc(2), y + SR.Ctl.Sc(1), _joinW - SR.Ctl.Sc(4), rowH - SR.Ctl.Sc(2)),
                        new GUIContent(JoinText(li)), hov, hov && _joinDownRow == i, false, false);
                    GUI.color = pc;
                }
                } finally { GUI.EndClip(); }
                SR.DrawLine(new Rect(_joinXPage - 1f, box.y + headerH, 1f, rowsH), SR.TableLineStrong);   //钉住列分隔线：与表头/外框同用强线色
            }
            // **R411**：竖向滚动条（R410-A1）。几何字段（_sbTrackPage/_sbThumbPage）声明后从未被赋值，
            //  核心 TableScrollbarInput 首个判据 track.width > 0 永不成立 -> 滑块拖不动、点轨道不跳页（只有滚轮），
            //  而布局那边（sbW）在溢出时照旧扣掉 13px -> 右侧一条**空白竖槽**。这里按 EX 同款公式把几何算出来并画出来
            //  （颜色对齐 ImGui 的 ScrollbarBg / Grab / GrabHovered / GrabActive）。
            //  TableInput 在本方法**之前**跑（同一趟），用的是上一趟的矩形 -> 布局帧间稳定，没问题。
            if (_shownLobbies.Count * rowH > rowsH + 0.5f) {
                Rect __track = new Rect(box.x + viewW + SR.Ctl.Sc(1), box.y + headerH, SR.Ctl.Sc(13) - SR.Ctl.Sc(2), rowsH);
                float __contentH = _shownLobbies.Count * rowH;
                float __thumbH = Mathf.Clamp(rowsH * rowsH / Mathf.Max(1f, __contentH), SR.Ctl.Sc(18), rowsH);
                float __ty = __track.y + (_tableMaxY > 0f ? Mathf.Clamp01(_scrollRows.y / _tableMaxY) : 0f) * (rowsH - __thumbH);
                Rect __thumb = new Rect(__track.x, __ty, __track.width, __thumbH);
                _sbTrackPage = __track; _sbThumbPage = __thumb;
                SR.DrawFill(__track, new Color(0.06f, 0.06f, 0.07f, 0.94f));                    //ImGui ScrollbarBg
                SR.DrawFill(__thumb, _sbDragging ? new Color(0.51f, 0.51f, 0.53f, 1f)           //ImGui GrabActive
                                    : __thumb.Contains(mp) ? new Color(0.41f, 0.41f, 0.44f, 1f) //ImGui GrabHovered
                                    : new Color(0.31f, 0.31f, 0.34f, 1f));                      //ImGui Grab
            } else {
                _sbTrackPage = new Rect(0f, 0f, 0f, 0f);
                _sbThumbPage = new Rect(0f, 0f, 0f, 0f);
            }
        }
        private static void TableInput(Rect box, List<int> vis, Event e) {
            if (e == null) return;
            Vector2 mp = e.mousePosition;
            //拖拽兜底复位：**不能**用 Input.GetMouseButton(0) 判断（它和 IMGUI 的事件流不一定同步，
            //UI 打开时可能一直是 false -> 一按下就被这里清掉，表现就是"表头拖不动 / 点了没反应"）。
            //改成"超过 1.2 秒没有任何鼠标事件"才复位（只有真的漏收 MouseUp 时才会触发）。
            try {
                SR.TableDragWatchdog(e, ref _dragWatchdog, ref _sbDragging, ref _tableArgs.moveCol, ref _tableArgs.moveDx, ref _tableArgs.dragCol, ref _dragH,
                    SaveWidths, SaveTableH, delegate { _joinDownRow = -1; });
            } catch (Exception __ex) { SR.Guard.Log("联机表格输入处理", __ex); } //兜底：表头拖拽/列宽/加入点击全在这段里
            //竖向滚动条拖拽 / 点轨道 -> 核心实现
            if (SR.TableScrollbarInput(e, mp, _sbTrackPage, _sbThumbPage, _tableMaxY, ref _sbDragging, ref _sbGrabY, ref _scrollRows.y)) return;
            //滚轮（纵向 / Ctrl 横向 / 无纵向余量转横向）-> 核心实现
            SR.TableWheelInput(e, box, mp, _tableMaxX, _tableMaxY, ref _scrollCols, ref _scrollRows);
            // 表头：分隔线调宽（<->）/ 表头换位（+）/ 单击切换 / 右键隐藏 / Shift+右键还原
            bool hoverSep = false, hoverCell = false;
            bool inBox = box.Contains(mp);   //横向滚出去的列会落到框外，判定要塞回框内
            for (int k = 0; k < _headerCellsPage.Count; k++) {
                int ci = _headerCols[k];
                Rect cell = _headerCellsPage[k];
                Rect sep = new Rect(cell.xMax - SR.Ctl.Sc(3), cell.y, SR.Ctl.Sc(7), cell.height);
                bool onSep = inBox && sep.Contains(mp);
                if (onSep) hoverSep = true;
                else if (inBox && cell.Contains(mp)) hoverCell = true;
                // **恢复联机页自己的实现**：核心那条路在联机页（drawHeader=false + 自己画表头）的时序下
                //  没能生效（表头拖不动），所以列宽/换位仍由这里负责，核心侧关闭（见下面的 resizable/reorderable=false）。
                // **#1 核心化**：列宽拖动交核心（TableResize）
                // **#1 核心化**：列头换位交核心（TableMove）
                // #2 把本页拖动状态镜像给核心参数 -> 核心的"跟手浮动块 + 插入线"动画据此绘制
            }
            SetMouseCursor(hoverSep || _tableArgs.dragCol >= 0, hoverCell || _tableArgs.moveCol >= 0);
            // 悬停在表头：显示该列说明（GUI.tooltip 在 OnGUI 末尾统一画）
            if (inBox && !_sbDragging && !hoverSep) {
                int hitCol = -1;
                for (int k = 0; k < _headerCellsPage.Count; k++) {
                    if (!_headerCellsPage[k].Contains(mp)) continue;
                    hitCol = _headerCols[k];
                    break;
                }
                if (hitCol < 0 && _joinHeaderPage.width > 0f && _joinHeaderPage.Contains(mp)) hitCol = JoinCol;
                if (hitCol >= 0) {
                    string tip = SR.T(TitleTips[hitCol], TitleTipsEn[hitCol]);
                    if (hitCol == SkillCol) {
                        //悬浮里带上"自己的技能系数"（游戏存档里的 SkillMean，和游戏调试文本用的是同一个值）
                        string mine = MySkillText();
                        tip += "\n" + SR.T("你自己的技能系数：", "your own skill: ") + (mine != null ? mine : "?");
                        tip += "\n" + SR.T("当前显示：", "showing: ") + SkillModeName(SkillModeCfg());
                    } else if (hitCol == QualityCol) {
                        bool on = _showQualityCoeff != null && _showQualityCoeff.Value;
                        tip += "\n" + SR.T(on ? "当前：显示匹配质量系数" : "当前：不显示匹配质量系数（点一下打开）",
                                             on ? "now: coefficient shown" : "now: coefficient hidden (click to show)");
                    }
                    GUI.tooltip = tip;
                }
            }
            // 右键：表头 = 隐藏该列 / Shift = 还原；房间行 = 隐藏该房间
            if (e.type == EventType.MouseDown && e.button == 1 && box.Contains(mp)) {
                if (e.shift) {
                    ResetTable();
                    e.Use();
                } else {
                    bool handled = false;
                    for (int k = 0; k < _headerCellsPage.Count; k++) {
                        if (!_headerCellsPage[k].Contains(mp)) continue;
                        int ci = _headerCols[k];
                        if (_headerCols.Count > 2) {      //至少留 2 列（操作列是钉住的，不参与）
                            _hiddenCols.Add(ci);
                            _visCache = null;
                            SaveCols();
                            string nm = SR.T(Titles[ci], TitlesEn[ci]);
                            //SR.T(zh, en) 的两个实参都会被求值，而这里两参完全相同 -> 先拼一次再传，省一次查表
                            string hiddenColMsg = SR.T("已隐藏列：", "Hidden column: ") + nm;
                            Notes(hiddenColMsg, hiddenColMsg);
                        }
                        handled = true;
                        break;
                    }
                    if (!handled) {
                        for (int i = 0; i < _rowRectsPage.Count; i++) {
                            if (!_rowRectsPage[i].Contains(mp)) continue;
                            int si = _rowRows[i];
                            Matchmaker.LobbyListInfo li = si >= 0 && si < _shownLobbies.Count ? _shownLobbies[si] : null;
                            if (li != null) {
                                _hidden.Add(li.sLobbyID);
                                _lobbiesDirty = true;   //可见集合变了：重算"筛选后 N 个"
                                Notes("已隐藏房间：" + (string.IsNullOrEmpty(li.LobbyOwner) ? li.sLobbyID : li.LobbyOwner),
                                      "Hidden: " + (string.IsNullOrEmpty(li.LobbyOwner) ? li.sLobbyID : li.LobbyOwner));
                            }
                            break;
                        }
                    }
                    e.Use();
                }
            }
            // 加入按钮：手动点击
            for (int i = 0; i < _joinRectsPage.Count; i++) {
                if (!_joinRectsPage[i].Contains(mp)) continue;
                int si = _joinRows[i];
                Matchmaker.LobbyListInfo li = si >= 0 && si < _shownLobbies.Count ? _shownLobbies[si] : null;
                if (li == null || !IsJoinable(li)) break;
                // 只在按下 -> 松开之间阻止窗口拖动：原来无条件设 true，鼠标只要**悬停**在按钮上
                //  （没按键、也没有 MouseUp 来复位）BlockWindowDrag 就一直是 true ->
                //  在加入按钮上按住想拖 SR 窗口时会拖不动（SR.Window 只在 MouseUp 才全局复位）。
                if (e.type == EventType.MouseDown && e.button == 0) { _joinDownRow = si; _joinDownId = li.sLobbyID; SR.BlockWindowDrag = true; e.Use(); }
                else if (e.type == EventType.MouseUp && e.button == 0 && !string.IsNullOrEmpty(_joinDownId) && _joinDownId == li.sLobbyID) {
                    try { SR.LogInfo("[联机] 列表项 sLobbyID=" + li.sLobbyID + " UnityMatchID=" + li.UnityMatchID + " (UnityMatchID=0 表示该列表不是 Steam 大厅数据)"); } catch { }
                    _joinDownRow = -1; _joinDownId = null; SR.BlockWindowDrag = false; _pendingSteamId = li.UnityMatchID; JoinLobby(li.sLobbyID); e.Use();
                }
                break;
            }
            if (e.type == EventType.MouseUp) { _joinDownRow = -1; _joinDownId = null; }
        }

        //Shift+右键 = 还原列宽/表格高度/列顺序 + 取消所有隐藏（房间与列）
        private static void ResetTable() {
            try {
                _cw = (float[])DefWidths.Clone();
                //S22 还原后列宽已就位, 同步把初始化标记置上(与旧代码 _cw != null 哨兵等价)
                _widthsLoaded = true;
                _order = new int[ColCount];
                for (int i = 0; i < ColCount; i++) _order[i] = i;
                _hiddenCols.Clear();
                _visCache = null;
                if (_tableH != null) _tableH.Value = 0;
                _hidden.Clear();
                _lobbiesDirty = true;
                SaveCols();
                Notes("已还原列宽、顺序与高度，并显示全部房间与列", "Widths, order and height reset; all sessions and columns restored");
            } catch (Exception __ex) { SR.Guard.Log("还原联机表格列设置", __ex); }
        }

        // **#1 按要求的顺序**：全部(空) -> 派对 -> 创意 -> 挑战 -> 自由 -> 其它（"全部"由下拉框首项提供）
        private static readonly string[] ModeVals = { "PARTY", "CREATIVE", "CHALLENGE", "FREEPLAY", "OTHER" };
        private static readonly string[] TagVals = { "Fun", "Competitive", "Beginner", "CustomLevels" };

        //下拉显示名（内部值 -> 界面名）：选项列表与"当前选中项"都用它，保证两处永不脱节
        private static string DispName(string key, string v) {
            if (string.IsNullOrEmpty(v)) return SR.T("全部", "All");
            if (key == "Filter Region") return DispRegion(v);
            if (key == "Filter Mode") return SR.T(v == "OTHER" ? "其它" : v == "FREEPLAY" ? "自由" : v == "CREATIVE" ? "创意" : v == "PARTY" ? "派对" : "挑战",
                                                  v == "FREEPLAY" ? "Freeplay" : v == "CREATIVE" ? "Creative" : v == "PARTY" ? "Party" : "Challenge");
            if (key == "Filter Tag") return SR.T(v == "Fun" ? "好玩" : v == "Competitive" ? "竞技" : v == "Beginner" ? "新手" : "自定义关卡",
                                                 v == "Fun" ? "Fun" : v == "Competitive" ? "Competitive" : v == "Beginner" ? "Beginner" : "Custom levels");
            if (key == "Filter Min Players") return v == "lt4" ? "<4" : "≥" + v;
            if (key == "Filter AFK") return v == "hide" ? SR.T("隐藏AFK", "hide AFK") : SR.T("仅AFK", "only AFK");
            if (key == "Filter State") return v == "join" ? SR.T("可加入", "joinable") : v == "full" ? SR.T("人满", "full") : v == "playing" ? SR.T("对局中", "in match") : SR.T("全部", "all");
            return v; //区域：值本身就是本地化短名
        }

        // 下拉筛选控件
        //标签用**固定宽度**（RestoreLabel 会按文字实际宽度收缩，CJK人数和 ASCIIAFK不一样宽 ->
        //第二行下拉框会比第一行靠左，看着不齐）；下拉框本身也用固定宽度（不要随窗口变得很宽）。
        private const float FilterLabelW = 46f;
        private const float FilterComboW = 104f;
        //固定选项值用静态数组（原来每帧在调用处 new[]{...}，纯浪费）
        private static readonly string[] PlayerVals = { "1", "2", "3", "lt4" };
        private static readonly string[] AfkVals = { "hide", "only" };
        private static readonly string[] StateVals = { "join", "full", "playing" };   //#4 状态筛选

        //下拉选项缓存：原来每帧给 5 个下拉框各建一个 List<string> + ToArray()（每帧约 10 次小分配）。
        //选项只跟语言 和 该 key 的值列表长度 有关，用版本戳缓存，变了才重建。
        //（区域那一项的动态内容由 RegionVals 自己的静态缓存负责，见 RegionLangStamp；
        //  旧的 _regionsVersion 已随 _regions 一起删除: 它的数据源早已换成
        //  RelayConstants.AVAILABLE_REGIONS，与房间列表无关，拿它当版本戳只会白白让缓存失效）
        private static readonly Dictionary<string, string[]> _optCache = new Dictionary<string, string[]>();
        private static readonly Dictionary<string, string> _optStamp = new Dictionary<string, string>();
        // #4 区域选项固定给出，不再依赖"已经刷新到的房间数据"，没刷新时也能选。
        // **原实现恒失败**：想反射拿 UnityServerRegion 的字段类型当"区域枚举"来枚举，但那个类型 IsEnum = False
        // （Cecil 已核）-> 永远落到硬编码 {"AP","EU","US"} 兜底，游戏将来新增区域时 SR 永远发现不了。
        // 改用权威来源 RelayConstants.AVAILABLE_REGIONS（游戏自己的区域筛选读的就是它，反编译 47840），
        // 逐个过 RegionCodeOf 归并成三大区；认不出来的保留原名，跟房间行的区域列同一套规则。
        // S18：RenderOnlinePage 每帧把 RegionVals() 当实参传给 DropFilter，C# 实参先求值 -> 版本戳缓存拦不住它。
        private static string[] _regionValsCache;
        private static int _regionValsVer = -1;

        //版本戳: 语言 id + 语言版本的短 hash. 区域代码经 RegionCodeOf 依赖 AvailableRegion 的
        //本地化短名, 切界面语言或换语言包后必须重建. 不用已删除的 _regionsVersion: 那个字段在
        //数据源改成 RelayConstants.AVAILABLE_REGIONS 之后就已经和真实数据源脱钩了.
        //再叠上数据源规模: AVAILABLE_REGIONS 是游戏的静态 List, 可能在本页第一次渲染之后才被填好,
        //那会儿缓存到的就是 AP/EU/US 兜底值; 规模一变就重建, 免得把兜底值缓存死.
        private static int RegionLangStamp() {
            unchecked {
                int h = 17;
                string s = SR.CurrentLanguageId + "|" + SR.LangVer;
                for (int i = 0; i < s.Length; i++) h = h * 31 + s[i];
                int n = -1;
                try { if (RelayConstants.AVAILABLE_REGIONS != null) n = RelayConstants.AVAILABLE_REGIONS.Count; } catch { }
                return h * 31 + n;
            }
        }

        private static string[] RegionVals() {
            int ver = RegionLangStamp();
            if (_regionValsCache != null && _regionValsVer == ver) return _regionValsCache;
            string[] built;
            try {
                System.Collections.Generic.List<string> vs = new System.Collections.Generic.List<string>();
                System.Collections.IList regs = RelayConstants.AVAILABLE_REGIONS;
                if (regs != null) {
                    for (int i = 0; i < regs.Count; i++) {
                        object r = regs[i];
                        if (r == null) continue;
                        string code = RegionCodeOf(r);
                        if (string.IsNullOrEmpty(code)) continue;
                        if (!vs.Contains(code)) vs.Add(code);   //按 AVAILABLE_REGIONS 的原始次序去重
                    }
                }
                //R397：补上"无区域数据"这一档。缺了它，那些 UnityServerRegion 为 null 的房间
                //（含玩家自己开的房）在下拉里**没有对应选项** -> 选了任何一档都看不见它们、
                //只能靠"全部"碰上，等于没法定位问题。放在最后，不影响真实区域的排序。
                if (!vs.Contains(NoneCode)) vs.Add(NoneCode);
                built = vs.Count > 0 ? vs.ToArray() : new[] { "AP", "EU", "US", NoneCode };
            } catch { built = new[] { "AP", "EU", "US" }; }
            _regionValsCache = built;
            _regionValsVer = ver;
            return built;
        }

        private static string[] FilterOptions(string key, string[] values) {
            //语言戳不能只写 "en"/"zh"：装了多个语言包时 En 恒为 false，从语言包 A 切到 B 时戳不变
            //-> 下拉选项会停留在旧语言的译文上。带语言 id + 版本号才是唯一标识。
            string stamp = SR.CurrentLanguageId + SR.LangVer + "|" + (values == null ? "-" : values.Length.ToString());
            string[] cached;
            string old;
            if (_optCache.TryGetValue(key, out cached) && _optStamp.TryGetValue(key, out old) && old == stamp) return cached;
            List<string> opts = new List<string>(1 + (values != null ? values.Length : 0));
            opts.Add(SR.T("全部", "All"));
            if (values != null) {
                for (int i = 0; i < values.Length; i++) opts.Add(DispName(key, values[i]));
            }
            cached = opts.ToArray();
            _optCache[key] = cached;
            _optStamp[key] = stamp;
            return cached;
        }

        // **R412**：与 FilterOptions 一一对应的"值"数组（vals[0]="全部"对应空串）。选中由浮层直接 SetValue。
        private static readonly Dictionary<string, string[]> _optValsCache = new Dictionary<string, string[]>();
        private static string[] FilterVals(string key, string[] values) {
            string stamp = SR.CurrentLanguageId + SR.LangVer + "|" + (values == null ? "-" : values.Length.ToString());
            string[] cached;
            string old;
            if (_optValsCache.TryGetValue(key, out cached) && _optStamp.TryGetValue(key, out old) && old == stamp) return cached;
            List<string> vs = new List<string>(1 + (values != null ? values.Length : 0));
            vs.Add("");
            if (values != null) for (int i = 0; i < values.Length; i++) vs.Add(values[i]);
            cached = vs.ToArray();
            _optValsCache[key] = cached;
            _optStamp[key] = stamp;
            return cached;
        }

        //筛选行标签宽度：按当前语言里最长的一个标签实测，取统一值,
        //既不再截断（原来固定 FilterLabelW 太小），又保证同一列下拉框左右对齐。
        private static float _filterLabelW = -1f;
        private static string _filterLabelLKey;
        private static float FilterLabelWidth() {
            string lk = SR.CurrentLanguageId + "|" + SR.LangVer;
            if (_filterLabelW >= 0f && _filterLabelLKey == lk) return _filterLabelW;
            float w = 0f;
            string[] ls = { SR.T("区域", "Region"), SR.T("模式", "Mode"), SR.T("标签", "Tag"), SR.T("人数", "Players"), SR.T("AFK", "AFK") };
            for (int i = 0; i < ls.Length; i++) w = Mathf.Max(w, SR.Ctl.Label.CalcSize(new GUIContent(ls[i])).x);
            _filterLabelW = Mathf.Max(SR.Ctl.Sc(FilterLabelW), w + SR.Ctl.Sc(8));
            _filterLabelLKey = lk;
            return _filterLabelW;
        }

        private static void DropFilter(string labelZh, string labelEn, string tipZh, string tipEn, string key, string[] values, float cellW) {
            try {
                //S17: 这个 entry 一帧只查一次, 当前值与写回都复用它(见下面 FindStr(e) / SetStr(e))
                ConfigEntryBase e = SR.Ctl.FindEntry(SecOnline, key);
                float labelW = FilterLabelWidth();
                float comboW = SR.Ctl.Sc(FilterComboW);
                GUILayout.BeginHorizontal(GUILayout.Width(labelW + comboW + SR.Ctl.Sc(14)));
                GUILayout.Label(new GUIContent(SR.T(labelZh, labelEn), SR.T(tipZh, tipEn)), SR.Ctl.Label,
                    GUILayout.Width(labelW), GUILayout.Height(SR.Ctl.Sc(26)));
                //滚轮/点击改值时不要带着页面滚动条一起动（外层滚动位置在 RenderOnlinePage 里还原）
                //选项走缓存（见 FilterOptions）：不再每帧新建 List + ToArray
                string[] opts = FilterOptions(key, values);
                string[] vals = FilterVals(key, values);   // **R414**：与 opts 一一对应，选中后按下标写回
                //同时只允许一个下拉框展开
                bool open = _openCombo == key;
                int dsel = SR.Ctl.ComboBox(e, DispName(key, FindStr(e)), vals, opts, ref open, comboW);
                if (dsel >= 0) {
                    SetStr(e, dsel == 0 ? "" : (values != null && dsel - 1 < values.Length ? values[dsel - 1] : ""));
                    _openCombo = "";
                    open = false;
                }
                if (open) {
                    if (_openCombo != key) _openCombo = key;
                } else if (_openCombo == key) {
                    _openCombo = "";
                }
                GUILayout.EndHorizontal();
            } catch (Exception __ex) { SR.Guard.Log("房间筛选下拉", __ex); }
        }

        //按 key 查的入口保留(对外行为不变), 内部转给按 entry 的重载.
        private static string FindStr(string key) {
            try { return FindStr(SR.Ctl.FindEntry(SecOnline, key)); }
            catch { return ""; }
        }

        //S17: DropFilter 一帧已经查过一次 FindEntry, 当前值直接从 entry 上读, 不再查第二遍.
        private static string FindStr(ConfigEntryBase e) {
            try { return e != null ? (e.BoxedValue as string ?? "") : ""; }
            catch { return ""; }
        }

        //S17: 改为接收已经查好的 entry, 不再自己查一次 FindEntry.
        private static void SetStr(ConfigEntryBase e, string value) {
            try {
                if (e != null) {
                    SR.Ctl.SetValue(e, value);
                    //筛选条件改了 -> 可见集合立刻变。不标脏的话"筛选后 N 个"要等 1 秒兜底才更新。
                    _lobbiesDirty = true;
                }
            } catch (Exception __ex) { SR.Guard.Log("设置房间筛选", __ex); }
        }

        // 列宽 / 列顺序 / 隐藏列（可拖动 + 持久化）
        //S22: 原来拿 _cw == null 当 一次性初始化 哨兵, 而 ResetTable 也会给 _cw 赋值,
        //两个写点共用一个判据, 语义含混. 改成显式的 _widthsLoaded 布尔哨兵.
        private static void EnsureWidths() {
            if (_widthsLoaded) return;
            _widthsLoaded = true;   //与旧代码同义: _cw 一旦被填上就不再重进
            _cw = (float[])DefWidths.Clone();
            _order = new int[ColCount];
            for (int i = 0; i < ColCount; i++) _order[i] = i;
            try {
                string v = _colWidths != null ? _colWidths.Value : null;
                if (!string.IsNullOrEmpty(v)) {
                    string[] p = v.Split(',');
                    if (p.Length == _cw.Length) {
                        for (int i = 0; i < p.Length; i++) {
                            float f;
                            if (float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out f) && f >= 28f && f <= 600f) _cw[i] = f;
                        }
                    }
                }
            } catch (Exception __ex) { SR.Guard.Log("读取联机表格列宽", __ex); } //一次性：读不到就用默认列宽，留痕便于排查"列宽每次都被重置"
            EnsureColOrder();
            EnsureHiddenCols();
        }

        //列顺序：必须是 0..ColCount-1 的一个排列，否则忽略（防止手改配置把表格搞崩）
        private static void EnsureColOrder() {
            try {
                string v = _colOrder != null ? _colOrder.Value : null;
                if (string.IsNullOrEmpty(v)) return;
                string[] p = v.Split(',');
                if (p.Length != ColCount) return;
                bool[] seen = new bool[ColCount];
                int[] o = new int[ColCount];
                for (int i = 0; i < ColCount; i++) {
                    int k;
                    if (!int.TryParse(p[i], out k) || k < 0 || k >= ColCount || seen[k]) return;
                    seen[k] = true;
                    o[i] = k;
                }
                _order = o;
            } catch (Exception __ex) { SR.Guard.Log("读取联机列顺序", __ex); }
        }

        private static void EnsureHiddenCols() {
            try {
                string v = _hiddenColsEntry != null ? _hiddenColsEntry.Value : null;
                if (string.IsNullOrEmpty(v)) return;
                foreach (string s in v.Split(',')) {
                    int k;
                    if (int.TryParse(s, out k) && k >= 0 && k < ColCount && k != JoinCol) _hiddenCols.Add(k);
                }
            } catch (Exception __ex) { SR.Guard.Log("读取联机隐藏列", __ex); }
        }

        private static void SaveCols() {
            try {
                if (_colWidths != null && _cw != null) {
                    string[] p = new string[_cw.Length];
                    for (int i = 0; i < _cw.Length; i++) p[i] = Mathf.RoundToInt(_cw[i]).ToString(CultureInfo.InvariantCulture);
                    _colWidths.Value = string.Join(",", p);
                }
                if (_colOrder != null && _order != null) {
                    string[] p = new string[_order.Length];
                    for (int i = 0; i < _order.Length; i++) p[i] = _order[i].ToString(CultureInfo.InvariantCulture);
                    _colOrder.Value = string.Join(",", p);
                }
                if (_hiddenColsEntry != null) {
                    List<string> l = new List<string>();
                    foreach (int k in _hiddenCols) l.Add(k.ToString(CultureInfo.InvariantCulture));
                    _hiddenColsEntry.Value = string.Join(",", l.ToArray());
                }
                if (_colWidths != null) SR.Ctl.MarkConfigDirty(_colWidths.ConfigFile); //落盘走节流器（失败有 Warning）
            } catch (Exception __ex) { SR.Guard.Log("保存列设置", __ex); }
        }

        private static void SaveWidths() { SaveCols(); }

        // 表格绘制（固定表头 + 细线分隔 + 列可拖动换位/隐藏/调宽）
        private const int ColCount = 12;
        private const int CombinedCol = 10;      //综合分列（= LobbyHealthNum + LobbySkillNum，即游戏排序用的房间质量分）
        private const int JoinCol = 11;          //操作列：不可隐藏（否则没法加入）
        private static readonly string[] Titles = { "区域", "人数", "游戏质量", "技能系数", "进度", "游戏模式", "标签", "房主", "限制", "标记", "综合分", "操作" };
        private static readonly string[] TitlesEn = { "Region", "Players", "Quality", "Skill", "Progress", "Mode", "Tag", "Host", "Limit", "Flags", "Combined", "Operation" };
        private static readonly string[] TitleTips = {
            "服务器区域（游戏自己的本地化短名，显示为 亚太/欧盟/美国）",
            "当前人数 / 4（房间最多 4 人，人满不可加入）",
            "✓ = 房主质量分达到 OkMatchScore，✓✓ = 达到 GoodMatchScore，再一个 ✓ = 匹配质量系数达到 SkillMatchQualityThreshold。\n系数 = CalculatedSkillMatchQuality（0~1）：游戏用大厅玩家技能串算出的技能匹配质量，越接近 1 = 房里玩家水平和本地玩家越接近（匹配越好）。\nShift+单击本列表头=显示/隐藏这个系数（默认不显示，只显示 ✓）。",
            "这个房间里玩家技能值的 最大 / 平均 / 最小（游戏自己的 PlayerSkills 串，格式 数量,技能,权重,...）。数值越高 = 水平越高。\nShift+单击本列表头=在 最大技能 -> 平均技能 -> 最小技能 之间切换（默认平均技能）。",
            "游戏自己的对局进度：大厅中 / 房主AFK / 剩余百分比",
            "游戏模式（默认规则集）；自定义规则集显示规则集名",
            "房间标签（游戏本地化）",
            "房主名字",
            "制胜积分 / 长度限制（轮数、时间或 无限制）",
            "模组 / 房主AFK",
            "房间综合分 = 房主质量分 + 技能换算分（游戏自己排序用的房间质量分，越大越靠前）。默认隐藏：右键表头综合分即可让它常驻显示。",
            "点加入进房（在对局内会先退出当前房间，回到界面就自动加入）；不可加入的显示原因并压暗",
        };
        private static readonly string[] TitleTipsEn = {
            "Server region (game-localized short name, shown as AP/EU/US)",
            "Players / 4 (a lobby holds at most 4; a full lobby cannot be joined)",
            "✓ = host quality reaches OkMatchScore, ✓✓ = reaches GoodMatchScore, plus another ✓ when the skill-match coefficient reaches SkillMatchQualityThreshold.\nCoefficient = CalculatedSkillMatchQuality (0-1): the game's skill-match quality computed from the lobby's player skills; closer to 1 = the players' skill is closer to yours.\nShift+click this header to show/hide that coefficient (hidden by default, marks only).",
            "Max / average / min skill value among this lobby's players (the game's own PlayerSkills string: count,skill,weight,...). Higher = a stronger lobby.\nShift+click this header to cycle max -> average -> min (default: average).",
            "The game's own match progress: in lobby / host AFK / remaining percent",
            "Game mode (default ruleset); a custom ruleset shows the ruleset name",
            "Lobby tag (game-localized)",
            "Host name",
            "Win points / length limit (rounds, time or no limit)",
            "Mods / host AFK",
            "Combined score = host quality score + skill-derived score (the game's own ordering key; higher ranks first). Hidden by default: right-click this header to keep it visible.",
            "Click Join to enter. While in a session it leaves the current one first and joins automatically. Unjoinable rows are dimmed",
        };

        //列顺序 / 被隐藏的列 / 光标贴图
        private static int[] _order;
        private static readonly HashSet<int> _hiddenCols = new HashSet<int>();
        private static ConfigEntry<string> _colOrder, _hiddenColsEntry;
        //表头裁剪框 / 表头行裁剪框 / 滚动条的矩形
        private static Rect _rowsClipPage, _joinHeaderPage;
        private static Rect _sbTrackPage = new Rect(0f, 0f, 0f, 0f), _sbThumbPage = new Rect(0f, 0f, 0f, 0f);
        private static List<int> _visCache;
        private static int _visFrame = -1;
        //拖列换位状态：**由核心 TableDraw 持有**（_tableArgs.moveCol / _tableArgs.dragCol）。
        //本页原来还留了一份 _moveCol/_dragCol 并每帧镜像回去，但那份从来没有被赋值（恒 -1），
        //镜像的结果就是把核心刚设好的拖拽状态清掉 -> 列宽拖不动、列头换位失效。已删除。

        private static List<int> VisibleCols() {
            if (_visCache != null && _visFrame == Time.frameCount) return _visCache;
            _visFrame = Time.frameCount;
            List<int> v = new List<int>(ColCount);
            for (int i = 0; i < ColCount; i++) {
                int ci = _order != null && i < _order.Length ? _order[i] : i;
                if (ci == JoinCol || !_hiddenCols.Contains(ci)) v.Add(ci);
            }
            _visCache = v;
            return v;
        }

        //绘制原语（Px/DrawLine/DrawRect）已抽到核心 SR，联机页只保留房间列表的数据与布局。

        //悬停在表头分隔线/单元格时声明特殊光标：由 SR.Window 每帧统一绘制并复位，
        //与侧栏拖拽用同一套贴图（<-> / +），不再用系统光标（避免两套光标打架、显示异常）。
        private static void SetMouseCursor(bool resize, bool move) {
            SR.CursorResize = resize;
            SR.CursorMove = move;
        }

        //单击一帧只处理一次（万一有多条命中路径同时命中，不会来回切两次 = 看起来没反应）
        private static void HeaderClickOnce(int ci) {
            if (_clickFrame == Time.frameCount) return;
            _clickFrame = Time.frameCount;
            HeaderClick(ci);
        }

        //技能系数：PlayerSkills = "数量,技能,权重,技能,权重,..."（游戏 readableSkill 的格式）
        //按 _skillMode 取 最大 / 平均 / 最小（默认平均；Shift+单击本列表头切换）。取不到返回 NaN。
        // 数值与文本拆开：排序（CompareCol）要用**数值**, 拿显示文本比会让 "10" 小于 "2"。
        private static double SkillValue(Matchmaker.LobbyListInfo li) {
            try {
                string s = li != null ? li.PlayerSkills : null;
                if (string.IsNullOrEmpty(s)) return double.NaN;
                string[] p = s.Split(',');
                int n;
                if (p.Length < 3 || !int.TryParse(p[0], out n) || n <= 0) return double.NaN;
                if (p.Length - 1 < 2 * n) n = (p.Length - 1) / 2;
                if (n <= 0) return double.NaN;
                string mode = SkillModeCfg();
                double sum = 0, max = double.MinValue, min = double.MaxValue;
                int ok = 0;
                for (int i = 0; i < n; i++) {
                    double v;
                    if (double.TryParse(p[2 * i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) {
                        sum += v; ok++;
                        if (v > max) max = v;
                        if (v < min) min = v;
                    }
                }
                if (ok == 0) return double.NaN;
                return mode == "max" ? max : mode == "min" ? min : sum / ok;
            } catch { return double.NaN; }
        }

        private static string SkillText(Matchmaker.LobbyListInfo li) {
            try {
                double v = SkillValue(li);
                return double.IsNaN(v) ? "-" : v.ToString("F0", CultureInfo.InvariantCulture);
            } catch { return "-"; }
        }

        //本地玩家自己的技能系数（游戏自己的存档字段；游戏在调试文本里也是拿它当"我的技能"）
        private static string MySkillText() {
            //悬浮在技能系数表头时每帧都会问一次；同一帧内取一次就够（技能值帧内不会变）
            int f = Time.frameCount;
            if (_mySkillFrame == f) return _mySkillText;
            _mySkillFrame = f;
            _mySkillText = null;
            try {
                StatTracker st = StatTracker.Instance;
                SaveFileData d = st != null ? st.GetSaveFileDataForMainUser() : null;
                if (d != null) _mySkillText = d.SkillMean.ToString("F0", CultureInfo.InvariantCulture);
            } catch { }
            return _mySkillText;
        }

    }
}
