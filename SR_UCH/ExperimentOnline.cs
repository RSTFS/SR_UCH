using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SR_UCH.Tweaks {
    //「联机」栏目（侧栏 rank 105，显示名从「联机列表」缩短为「联机」）：
    //  · 回家：退出联机、返回主界面（走游戏自己的 Matchmaker.ReturnToMainMenu，带看门狗兜底）
    //  · 房间列表：筛选（全下拉框，同时只开一个）/ 可加入的排最上面 / 表格（固定表头 + 可拖动列宽/列顺序）
    //表格里的文字尽量用**游戏自己的本地化**（区域 = AvailableRegion.LocalizedShortName，
    //模式 = GameState.GetLocalizedGameModeName，标签 = GameSettings.ConvertLobbyTag，
    //大厅中/AFK/轮数/无限制 = I2 术语），这样跟游戏内列表的中文完全一致。
    public class ExpOnline : ITweak {

        private static MainPlugin _plugin;

        // ---- 按键 ----
        private static ConfigEntry<KeyCode> _keyRefresh, _keyDisband, _keyMainMenu;
        // ---- 筛选（全部下拉框，存字符串；空 = 全部/默认）----
        private static ConfigEntry<string> _fRegion, _fMode, _fTag, _fMinPlayers, _fAfk;
        // ---- 表格列宽（像素，逗号分隔；表头分隔线可拖动）/ 表格高度（0 = 自动）----
        private static ConfigEntry<string> _colWidths;
        private static ConfigEntry<int> _tableH;
        private static ConfigEntry<bool> _showQualityCoeff;    //「游戏质量」列是否附带技能匹配质量系数（表头点击切换）
        private static ConfigEntry<string> _skillMode;         //「技能系数」列取 最大/平均/最小（表头点击切换）

        // ---- 运行时状态 ----
        private static readonly List<Matchmaker.LobbyListInfo> _lobbies = new List<Matchmaker.LobbyListInfo>();
        private static readonly HashSet<string> _regions = new HashSet<string>();
        private static readonly HashSet<string> _hidden = new HashSet<string>();   //右键隐藏的房间 id
        private static bool _lobbySearching;
        private static float _lobbySearchAt = -999f;
        private static string _lobbyMsg = "";
        private static Vector2 _scrollRows, _scrollCols;
        private static string _openCombo = "";              //同时只允许一个下拉框展开
        private static float _abortDeadline, _forceDeadline;
        private static Texture2D _px;                       //1x1 白点（画表格线）
        private static float[] _cw;                         //当前列宽
        private static int _dragCol = -1;                   //正在拖动的列
        private static float _dragX0, _dragW0;
        private static bool _dragH;                         //正在拖表格高度
        private static bool _scrollFix;                     //本帧下拉框导致内容变化：需要还原外层滚动位置
        private static float _dragHY0, _dragH0;
        // ---- 对局内点「加入」= 先退出当前房间，退出后立刻加入 ----
        private static string _pendingJoin = "";
        private static bool _pendingUseCode;                 //true = 按房间码加入
        private static float _pendingReadyAt, _pendingGiveUpAt;
        private static string _joinCode = "";                //按钮行右边的房间码输入框（4 个大写字母）

        //LevelSelectController.FadeOut 是 private 序列化字段（场景内遮罩）：FadeToLevel 等的就是它
        private static readonly FieldInfo _fLsFadeOut =
            typeof(LevelSelectController).GetField("FadeOut", BindingFlags.Instance | BindingFlags.NonPublic);

        private const string SecOnline = "Online";
        private const string ColWidthsKey = "Column Widths 3";   //v3：新增「平均技能」列，老值不再套用
        private const string TableHKey = "Table Height";
        private const string ColOrderKey = "Column Order";
        private const string HiddenColsKey = "Hidden Columns";

        //列宽（默认值）：区域 人数 游戏质量 平均技能 进度 游戏模式 标签 房主 限制 标记 操作
        private static readonly float[] DefWidths = { 62f, 44f, 76f, 62f, 66f, 80f, 50f, 86f, 78f, 50f, 70f };

        public void Initialize(MainPlugin plugin) {
            _plugin = plugin;
            SR.LocSec(SecOnline, "联机", "Online");
            SR.Nav(SecOnline, 85);   //侧栏顺序 85：排在「实验」(90) 上面
            SR.RegisterPage(SecOnline, RenderOnlinePage);

            _keyRefresh = plugin.Config.Bind(SecOnline, "Refresh Lobbies Key", KeyCode.None, "刷新列表 快捷键");
            _keyDisband = plugin.Config.Bind(SecOnline, "Disband Key", KeyCode.None, "退出联机 快捷键");
            _keyMainMenu = plugin.Config.Bind(SecOnline, "Main Menu Key", KeyCode.None, "返回主界面 快捷键");
            SR.LocKey(SecOnline, "Refresh Lobbies Key", "刷新列表键", null);
            SR.LocDesc(SecOnline, "Refresh Lobbies Key", "快捷键：同「刷新列表」按钮（右键可绑）。", null);
            SR.LocKey(SecOnline, "Disband Key", "返回主界面键", null);
            SR.LocDesc(SecOnline, "Disband Key", "快捷键：同「返回主界面」按钮（右键可绑）。", null);
            SR.LocKey(SecOnline, "Main Menu Key", "返回主界面键 2", null);
            SR.LocDesc(SecOnline, "Main Menu Key", "快捷键：和「返回主界面」同一个动作。", null);

            _fRegion = plugin.Config.Bind(SecOnline, "Filter Region", "", "筛选：区域（下拉）");
            _fMode = plugin.Config.Bind(SecOnline, "Filter Mode", "", "筛选：游戏模式（下拉）");
            _fTag = plugin.Config.Bind(SecOnline, "Filter Tag", "", "筛选：房间标签（下拉）");
            _fMinPlayers = plugin.Config.Bind(SecOnline, "Filter Min Players", "", "筛选：人数（空 = 全部；1/2/3 = ≥N；lt4 = 少于 4 人）");
            _fAfk = plugin.Config.Bind(SecOnline, "Filter AFK", "hide", "筛选：房主 AFK（空 = 全部；hide = 隐藏；only = 仅 AFK）");
            _colWidths = plugin.Config.Bind(SecOnline, ColWidthsKey, "",
                "表格列宽（像素，逗号分隔）。在表头分隔线上按住左键拖动改列宽；Shift+右键表格 = 还原列宽/高度并显示被隐藏的房间。");
            _tableH = plugin.Config.Bind(SecOnline, TableHKey, 0,
                new ConfigDescription("表格高度（像素，0 = 随窗口自动）。拖动表格底边也能改。", new AcceptableValueRange<int>(0, 1000)));
            _colOrder = plugin.Config.Bind(SecOnline, ColOrderKey, "", "列顺序（列索引，逗号分隔；空 = 默认顺序）");
            _hiddenColsEntry = plugin.Config.Bind(SecOnline, HiddenColsKey, "", "被隐藏的列（列索引，逗号分隔；右键表头隐藏，Shift+右键还原）");
            //表头点击切换（见 HeaderClick）：游戏质量列 = 是否附带"技能匹配质量"系数；技能系数列 = 最大/平均/最小
            _showQualityCoeff = plugin.Config.Bind(SecOnline, "Show Quality Coeff", false,
                "「游戏质量」列是否再显示技能匹配质量系数（点击该列表头切换，默认关）");
            _skillMode = plugin.Config.Bind(SecOnline, "Skill Mode", "avg",
                "「技能系数」列显示哪个值：max = 最大技能 / avg = 平均技能 / min = 最小技能（点击该列表头切换，默认 avg）");
            SR.LocKey(SecOnline, "Show Quality Coeff", "显示技能匹配质量", null);
            SR.LocDesc(SecOnline, "Show Quality Coeff", "打开后「游戏质量」列会在 ✓ 后面再显示技能匹配质量系数（0~1）。点击表头即可切换。", null);
            SR.LocKey(SecOnline, "Skill Mode", "技能系数取值", null);
            SR.LocDesc(SecOnline, "Skill Mode", "「技能系数」列显示 最大技能 / 平均技能 / 最小技能 里的哪一个。点击表头即可切换。", null);
        }

        //================ 回家（解散 / 退出 / 返回主界面） ================
        private static void Notes(string zh, string en) {
            try { Experiments.NotifyExp(SR.T(zh, en)); } catch { }
        }

        private static void ArmWatchdog() {
            float t = Time.unscaledTime;
            _abortDeadline = t + 5f;   //第 1 级：放出被等待的遮罩
            _forceDeadline = t + 8f;   //第 2 级：自己收尾 + 强制加载主界面
        }

        //游戏自带的「解散/离开 → 回主界面」完整流程（反编译确认）：
        //  Matchmaker.ReturnToMainMenu(reason)
        //   ├─ LobbyManagerManager.SetAbortReason(reason)
        //   ├─ 房主：SendToAll(MsgHostEndedGame)（通知所有房客）+ CleanUpPlayers()
        //   └─ LobbyManagerManager.AbortGameInProgressGracefully(reason) → 置 abortGameRequested
        //        → LobbyManagerManager.Update() → AbortGame()
        //             · LobbyManager.Disconnect(reason)（房主 StopHost+DisconnectAll+LeaveLobby；房客 StopClient+LeaveLobby）
        //             · Destroy(LobbyManager)（NetworkManager 一起销毁，避免带进主界面）
        //             · StartCoroutine(FadeOutToLoad()) → 等 LoadingInterstitialSplash.**Instance**.State == SHOW
        //             · SceneManagerWrapper.DoGentleSceneLoad("MainMenu")
        //两个坑（之前卡加载动画的原因）：
        //  1) 自己先 LobbyManager.Disconnect：房主 StopHost 后，树屋里本机客户端的 FadeToLevel("MainMenu", local:false)
        //     要等服务器切场景，而服务器已经没了 → 永远停在加载动画；
        //  2) LevelSelectController.BackToMainMenu：它等的是 LevelSelectController 自己那个**场景内**的 FadeOut 遮罩
        //     （LoadingInterstitialSplash.Awake 会把非首个实例 Destroy，那个 State 之后不再更新）→ 协程永远等不到 SHOW。
        //所以统一走游戏自己的 ReturnToMainMenu，收尾全部交给 LobbyManagerManager。
        public static void GoMainMenu() {
            Notes("正在返回主界面…", "Returning to the main menu…");
            try { Matchmaker.ReturnToMainMenu("SR_UCH: back to menu"); }
            catch (Exception e) { MainPlugin.ModLogger.LogWarning("返回主界面失败: " + e.Message); }
            ArmWatchdog();
        }

        public static void DisbandOrLeave() {
            try {
                LobbyManager lm = LobbyManager.instance;
                bool host = false;
                try { host = lm != null && lm.IsHost; } catch { }
                Notes(host ? "正在解散联机…" : "正在退出房间…", host ? "Disbanding the session…" : "Leaving the session…");
            } catch { }
            //房主：ReturnToMainMenu 会先广播 HostEndedGame → 全员一起回主界面（=解散）
            //房客：同一条路只退自己（AbortGame 里走 client 分支）
            GoMainMenu();
        }

        private static void SetShow(UISplashScreen s) {
            try {
                if (ReferenceEquals(s, null)) return;             //已销毁的场景内遮罩也要改（协程读的是托管字段）
                if (s.State != UISplashScreen.STATE.SHOW) s.State = UISplashScreen.STATE.SHOW;
            } catch { }
        }

        //把「被 FadeToLevel 等待的遮罩」推到 SHOW，让卡住的协程继续往下走
        private static void NudgeSplash() {
            try { SetShow(LoadingInterstitialSplash.Instance); } catch { }
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.CurrentLevelSelectController != null && _fLsFadeOut != null) {
                    SetShow(_fLsFadeOut.GetValue(lm.CurrentLevelSelectController) as UISplashScreen);
                }
            } catch { }
        }

        //看门狗（只在玩家点了「退出联机/返回主界面」之后启动）：
        //  5 秒还没回主界面 → 放出遮罩（可能是上面第 2 个坑，或 abortGameRequested 已被别处置位导致这次请求被忽略）；
        //  8 秒还没回主界面 → 自己 Disconnect + 销毁 LobbyManager，然后强制加载 MainMenu。
        private static void TickWatchdog() {
            float now = Time.unscaledTime;
            if (_abortDeadline > 0f && now >= _abortDeadline) {
                _abortDeadline = 0f;
                string sc = SceneManager.GetActiveScene().name;
                if (sc == "MainMenu") { _forceDeadline = 0f; return; }
                MainPlugin.ModLogger.LogWarning("[联机列表] 回家超时（场景 " + sc + "）→ 放出加载遮罩让流程继续");
                NudgeSplash();
            }
            if (_forceDeadline > 0f && now >= _forceDeadline) {
                _forceDeadline = 0f;
                if (SceneManager.GetActiveScene().name == "MainMenu") return;
                MainPlugin.ModLogger.LogWarning("[联机列表] 仍卡在加载动画 → 强制回主界面");
                try {
                    LobbyManager lm = LobbyManager.instance;
                    if (lm != null) {
                        try { lm.Disconnect("SR_UCH watchdog"); } catch { }
                        try { UnityEngine.Object.Destroy(lm.gameObject); } catch { }
                    }
                } catch { }
                try { SceneManagerWrapper.LoadScene("MainMenu"); }
                catch (Exception e) { MainPlugin.ModLogger.LogWarning("强制加载主界面失败: " + e.Message); }
            }
        }

        //================ 房间列表 ================
        public static void RefreshLobbies() {
            try {
                Matchmaker mm = Matchmaker.Instance;
                if (mm == null) { _lobbyMsg = SR.T("匹配器不可用", "Matchmaker unavailable"); return; }
                _lobbies.Clear();
                _regions.Clear();
                _lobbySearching = true;
                _lobbyMsg = SR.T("搜索中…", "Searching…");
                _lobbySearchAt = Time.unscaledTime;
                mm.FindLobbies(new Matchmaker.LobbyListingCallback(OnLobbyInfo));
            } catch (Exception e) {
                _lobbySearching = false;
                _lobbyMsg = SR.T("搜索失败: ", "Search failed: ") + e.Message;
                MainPlugin.ModLogger.LogWarning("刷新房间列表失败: " + e.Message);
            }
        }

        private static void OnLobbyInfo(Matchmaker.LobbyListInfo info) {
            try {
                if (info == null) return;
                if (info.error) { _lobbySearching = false; _lobbyMsg = SR.T("搜索出错", "Search error"); return; }
                _lobbies.Add(info);
                string rn = RegionCode(info);
                if (!string.IsNullOrEmpty(rn)) _regions.Add(rn);
                _lobbyMsg = string.Format(SR.T("共 {0} 个房间，筛选后 {1} 个", "{0} lobbies, {1} after filters"), _lobbies.Count, CountShown());
            } catch (Exception __ex) { SR.Guard.Log("房间列表回调", __ex); }
        }

        //点「加入」：不在房间里就直接加；**在房里/对局里则先退出当前房间，一退出就立刻加入（不等回主界面）**
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

        public static void JoinLobby(string id, bool useCode) {
            if (string.IsNullOrEmpty(id)) return;
            if (InSession() || Matchmaker.CurrentMatchmakingLobby != null) {
                _pendingJoin = id;
                _pendingUseCode = useCode;
                _pendingReadyAt = 0f;
                _pendingGiveUpAt = Time.unscaledTime + 30f;
                Notes("正在退出当前房间，退出后立刻加入…", "Leaving the current session; joining as soon as it is gone…");
                //只做"离开当前房间"这一件事，不等主界面：不调用 ReturnToMainMenu（那要多走一趟场景加载）
                LeaveCurrentForJoin();
                return;
            }
            DoJoin(id, useCode);
        }

        //直接离开当前联机（游戏自己会走 AbortGame 的收尾）；退出干净后 TickPendingJoin 只发一次加入请求
        private static void LeaveCurrentForJoin() {
            try {
                Matchmaker mm = Matchmaker.Instance;
                if (mm != null) mm.LeaveLobby("SR_UCH: switch lobby", UserMessageManager.UserMsgPriority.hi);
            } catch (Exception e) { MainPlugin.ModLogger.LogWarning("切换房间前退出失败: " + e.Message); }
            ArmWatchdog();
        }

        //是否正在联机/对局中（决定要不要先退出）
        private static bool InSession() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && (lm.IsHost || lm.IsInOnlineGame)) return true;
                if (Matchmaker.CurrentMatchmakingLobby != null) return true;
            } catch { }
            return false;
        }

        private static void DoJoin(string id, bool useCode) {
            try {
                Matchmaker mm = Matchmaker.Instance;
                if (mm == null) {
                    Notes("匹配器不可用", "Matchmaker unavailable");
                    _pendingJoin = "";
                    return;
                }
                Notes(useCode ? "正在按房间码加入…" : "正在加入房间…", useCode ? "Joining by code…" : "Joining the lobby…");
                //失败就失败：不重试（重试会让玩家看到连续几次加入请求，也可能把已经满/已开的房间重试出怪状态）
                mm.JoinLobby(id, useCode, new UnityEngine.Events.UnityAction<bool>(ok => {
                    try {
                        _pendingJoin = "";
                        Notes(ok ? "已发送加入请求" : "加入失败", ok ? "Join request sent" : "Join failed");
                    } catch { }
                }));
            } catch (Exception e) {
                MainPlugin.ModLogger.LogWarning("加入房间失败: " + e.Message);
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
                    Notes("加入超时，已取消", "Join timed out");
                    return;
                }
                if (now < _pendingReadyAt) return;
                //还没退出干净（还在房里/还在联机中）→ 继续等，不算重试
                if (InSession() || Matchmaker.CurrentMatchmakingLobby != null) { _pendingReadyAt = now + 0.4f; return; }
                string id = _pendingJoin;
                bool useCode = _pendingUseCode;
                DoJoin(id, useCode);
                _pendingReadyAt = now + 2f;   //失败时 DoJoin 会把这个时间推后
            } catch (Exception __ex) { SR.Guard.Log("延时加入房间", __ex); }
        }

        public static void CheckHotkeys() {
            TickWatchdog();
            TickPendingJoin();
            if (SR.UiOpen && SR.BlockInput) return;
            if (SR.ComboKeyDown(_keyRefresh)) RefreshLobbies();
            if (SR.ComboKeyDown(_keyDisband)) DisbandOrLeave();
            if (SR.ComboKeyDown(_keyMainMenu)) DisbandOrLeave();   //与「退出联机」同一个动作（按钮已合并）
        }

        //================ 游戏本地化小工具 ================
        //用游戏自己的 I2 术语表（找不到/取不到就退回默认值），保证与游戏内列表用词一致
        private static string Tr(string key, string fallback) {
            try {
                string s = I2.Loc.LocalizationManager.GetTranslation(key);
                if (!string.IsNullOrEmpty(s)) return s;
            } catch { }
            return fallback;
        }

        //SR 界面语言是 English 时不能用游戏术语（游戏语言可能是中文，术语只会给中文）→ 用我们自己的英文
        private static bool En { get { return SR.Ctl.LangEn; } }

        private static string G(string i2Key, string zh, string en) {
            if (En) return en;
            return Tr(i2Key, zh);
        }

        //区域：先取游戏自己的本地化短名（AvailableRegion.LocalizedShortName），再压成三档。
        //筛选里存的是**语言无关的代码**（AP/EU/US/原始名），显示时才翻译 → 切界面语言不会让筛选失效。
        //游戏原始值形如 region_shortname_ap，英文下要显示成 AP（不是那串 key）。
        private static string RegionCode(Matchmaker.LobbyListInfo li) {
            string loc = "", raw = "";
            try {
                object r = li.UnityServerRegion;
                if (r != null) {
                    Type t = r.GetType();
                    PropertyInfo p = t.GetProperty("LocalizedShortName");
                    if (p != null) loc = p.GetValue(r, null) as string;
                    FieldInfo f = t.GetField("shortName");
                    if (f != null) raw = f.GetValue(r) as string;
                    if (string.IsNullOrEmpty(raw)) { f = t.GetField("name"); if (f != null) raw = f.GetValue(r) as string; }
                    if (string.IsNullOrEmpty(raw)) { f = t.GetField("id"); if (f != null) raw = f.GetValue(r) as string; }
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
            if (code == "AP") return En ? "AP" : ZhTerm("Network/region_shortname_ap", SR.T("亚太", "AP"));
            if (code == "EU") return En ? "EU" : ZhTerm("Network/region_shortname_eu", SR.T("欧盟", "EU"));
            if (code == "US") return En ? "US" : ZhTerm("Network/region_shortname_us", SR.T("美国", "US"));
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

        //按钮/提示文字：把「为什么不能进」说清楚
        private static string JoinText(Matchmaker.LobbyListInfo li) {
            if (IsJoinable(li)) return SR.T("加入", "Join");
            if (li.error || !li.InfoReceived) return SR.T("信息不全", "no info");
            if (li.matchProgress != 0) return SR.T("对局中", "in match");
            if (li.isAFK) return SR.T("房主AFK", "host AFK");
            if (IsFull(li)) return SR.T("人满", "full");
            return SR.T("不可加入", "closed");
        }

        //对局进度：游戏自己的写法（0 → 大厅中 / 房主AFK；否则 100-matchProgress %）
        private static string ProgressText(Matchmaker.LobbyListInfo li) {
            try {
                if (li.matchProgress == 0) {
                    return li.isAFK ? G("Network/MatchProgressAFK", "房主AFK", "Host AFK")
                                    : G("Network/MatchProgressLobby", "大厅中", "In lobby");
                }
                return (100 - li.matchProgress) + "%";
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

        //游戏模式：默认规则集 → 游戏本地化的模式名（中文时）；自定义规则集 → 规则集名字
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
                if (v.Length > 0 && li.gameMode.ToString() != v) return false;
                v = Val(_fTag);
                if (v.Length > 0 && li.lobbyTag.ToString() != v) return false;
                v = Val(_fMinPlayers);
                if (v == "lt4") { if (li.Players >= 4) return false; }
                else {
                    int min;
                    if (v.Length > 0 && int.TryParse(v, out min) && li.Players < min) return false;
                }
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

        //排序：**可加入的永远排最上面**，再按进度；最后用 sLobbyID 兜底（List.Sort 不稳定，键相同会每帧互换 → 表格闪）
        //（次级的"人数/区域"排序筛选项已按需求移除：固定用游戏自己的 matchProgress）
        private static void SortLobbies() {
            try {
                _lobbies.Sort((a, b) => {
                    if (ReferenceEquals(a, b)) return 0;
                    if (a == null) return 1;
                    if (b == null) return -1;
                    bool ja = IsJoinable(a), jb = IsJoinable(b);
                    if (ja != jb) return ja ? -1 : 1;
                    int c = a.matchProgress.CompareTo(b.matchProgress);
                    if (c != 0) return c;
                    return string.Compare(a.sLobbyID, b.sLobbyID, StringComparison.Ordinal);
                });
            } catch (Exception __ex) { SR.Guard.Log("房间排序", __ex); }
        }

        //================ 页面：联机 ================
        private static void RenderOnlinePage() {
            //本页不需要页面级滚动：表头/筛选必须一直可见，房间只在下面的框里滚
            SR.PageScroll = Vector2.zero;
            SR.NoPageScrollBars = true;   //页面级横向/纵向滚动条都不要（原来那两个既用不到、又因为 Scroll 被写回而拖不动）
            _scrollFix = false;
            Vector2 scrollBefore = SR.PageScroll;
            if (Event.current != null && Event.current.type == EventType.MouseUp) {
                if (_dragCol >= 0) { _dragCol = -1; SR.BlockWindowDrag = false; SaveWidths(); }
                if (_dragH) { _dragH = false; SR.BlockWindowDrag = false; SaveTableH(); }
            }
            EnsureWidths();
            //页面内容在**布局空间**里的起点 y（零高度探针）：用来算"表格上方用掉多少高度"。
            //（必须用同一个坐标系里的差值；ContentArea 是屏幕坐标，混着算会让表格框大出一截 —— 见下面 availW/availH）
            Rect topProbe = GUILayoutUtility.GetRect(0f, 0f, GUIStyle.none);
            if (Event.current != null && Event.current.type == EventType.Layout) _pageTopY = topProbe.y;

            //按钮顺序：刷新列表 / 退出联机（= 原来的「返回主界面」，两者本来就是同一条游戏流程）/ 房间码
            GUILayout.BeginHorizontal();
            if (SR.Ctl.HotkeyButton("cc.lobbies", SR.T("刷新列表", "Refresh"),
                SR.T("向匹配服务查询公开房间（可加入的排最上面，表格里点「加入」）。\n对局/房间里点「加入」会先退出当前房间，退出后立刻自动加入新房间。",
                     "Query the public lobby list (joinable lobbies on top; click Join in the table).\nClicking Join while in a session leaves it first and joins the new lobby right away."),
                RefreshLobbies, SR.Ctl.Sc(140), SR.Ctl.Sc(30))) RefreshLobbies();
            if (SR.Ctl.HotkeyButton("cc.disband", SR.T("返回主界面", "Back to main menu"),
                SR.T("回主界面（= 游戏自己的「离开房间」流程）。房主会先广播 HostEndedGame，全员一起回主界面；房客只退自己。",
                     "Back to the main menu (the game's own leave-lobby flow). Host: broadcasts HostEndedGame, everyone returns; guest: leaves only yourself."),
                DisbandOrLeave, SR.Ctl.Sc(140), SR.Ctl.Sc(30))) DisbandOrLeave();
            //房间码加入（游戏本身支持按码加入：Matchmaker.JoinLobby(code, useCode:true)）
            GUILayout.Space(SR.Ctl.Sc(14));
            GUILayout.Label(new GUIContent(SR.T("房间码", "Lobby code"),
                SR.T("输入 4 个大写字母的房间码直接加入（和游戏里「按码加入」一样；在对局内会先退出当前房间）。",
                     "Enter the 4-letter lobby code to join directly (same as the game's join-by-code; while in a session it leaves the current one first).")),
                SR.Ctl.Label, GUILayout.Width(SR.Ctl.Sc(CodeLabelW)), GUILayout.Height(SR.Ctl.Sc(30)));
            string typed = GUILayout.TextField(_joinCode ?? "", 4, SR.Ctl.SearchBox,
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

            if (_lobbySearching && Time.unscaledTime - _lobbySearchAt > 6f) _lobbySearching = false;
            SortLobbies();
            string hint = SR.T("（点「刷新列表」查询）表头：拖分隔线＝调列宽（↔），拖表头＝换列位置（✥），点「游戏质量」＝显示/隐藏匹配质量系数，点「技能系数」＝最大/平均/最小，右键＝隐藏该列，Shift+右键＝还原；右键某行＝隐藏该房间；滚轮＝上下，Ctrl+滚轮＝左右；表格底边可拖高度",
                               "(click 'Refresh' to query) Header: drag a separator = resize (↔), drag a header = reorder (✥), click 'Quality' = show/hide the skill-match coefficient, click 'Skill' = max/avg/min, right-click = hide that column, Shift+right-click = reset; right-click a row = hide that lobby; wheel = up/down, Ctrl+wheel = left/right; drag the table's bottom edge to resize height");
            GUILayout.Label(string.IsNullOrEmpty(_lobbyMsg) ? hint : _lobbyMsg, SR.Ctl.Label);
            if (_hidden.Count > 0) {
                GUILayout.Label(string.Format(SR.T("已隐藏 {0} 个房间（Shift+右键表格可恢复）", "{0} lobbies hidden (Shift+right-click the table to restore)"), _hidden.Count), SR.Ctl.Label);
            }

            // ---- 筛选：第一行 区域/模式/标签，第二行 人数/AFK（下拉框固定宽度，同时只开一个）----
            //（次级的「排序」下拉已按需求移除：可加入的永远在最上面，其余按进度）
            float cellW = Mathf.Max(SR.Ctl.Sc(120), (SR.Ctl.WinWidth - SR.Ctl.SidebarWidth() - SR.Ctl.Sc(40)) / 3f - SR.Ctl.Sc(10));
            GUILayout.Space(SR.Ctl.Sc(2));
            GUILayout.BeginHorizontal();
            DropFilter("区域", "Region", "按服务器区域筛选（亚太/欧盟/美国）。", "Filter by server region (AP / EU / US).", "Filter Region", ToArray(_regions), cellW);
            DropFilter("模式", "Mode", "按游戏模式筛选。", "Filter by game mode.", "Filter Mode", ModeVals, cellW);
            DropFilter("标签", "Tag", "按房间标签筛选。", "Filter by lobby tag.", "Filter Tag", TagVals, cellW);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            DropFilter("人数", "Players", "按人数筛选：≥N 或「＜4」（= 还有空位）。", "Filter by players: at least N, or '< 4' (has a free slot).", "Filter Min Players",
                new[] { "1", "2", "3", "lt4" }, cellW);
            DropFilter("AFK", "AFK", "房主 AFK 筛选（游戏自己的 isAFK 字段）。", "Filter by host AFK (the game's own isAFK field).", "Filter AFK",
                new[] { "hide", "only" }, cellW);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            // ---- 表格：自绘（表头固定、只有房间行滚动；滚动条自绘 → 跟随界面缩放、贴右边界、拖到列表外也不断）----
            GUILayout.Space(SR.Ctl.Sc(3));
            Rect last = GUILayoutUtility.GetLastRect();
            float footerH = SR.FooterHeight > 0f ? SR.FooterHeight : SR.Ctl.Sc(30);
            //★宽度必须用**纯尺寸**算：布局矩形和屏幕矩形（ContentArea）不在同一个坐标系里，
            //  原来写 ContentArea.xMax - last.x 会把"屏幕右边界"减掉一个布局坐标 → 表格框比可视区宽一大截，
            //  最右边的「操作」列被推到屏幕外（"操作那一栏怎么没了"）、右边界滚动条也看不见。
            float availW = Mathf.Max(SR.Ctl.Sc(220), SR.Ctl.WinWidth - SR.Ctl.SidebarWidth() - SR.Ctl.Sc(12));
            //高度同理：用"页面可视高度（屏幕量） - 表格上方已用高度（布局量之差）"
            float pageH = SR.ContentArea.height - SR.Ctl.Sc(26) - SR.Ctl.Sc(2) - footerH;
            float usedAbove = _pageTopY > 0f ? Mathf.Max(0f, last.yMax - _pageTopY) : 0f;
            float availH = Mathf.Max(SR.Ctl.Sc(140), pageH - usedAbove - SR.Ctl.Sc(8));
            float boxH = _tableH != null && _tableH.Value > 0
                ? Mathf.Clamp(SR.Ctl.Sc(_tableH.Value), SR.Ctl.Sc(120), availH)
                : availH;
            List<int> vis = VisibleCols();
            Rect box = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                GUILayout.Width(availW), GUILayout.Height(boxH));
            TableDraw(box, vis);
            TableInput(box, vis, Event.current);
            HeightGrip();   //表格底边：拖动改高度
            if (_scrollFix) SR.PageScroll = scrollBefore;   //下拉框引起的滚动跳动：还原
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
                SR.BlockWindowDrag = true;
                e.Use();
            } else if (e.type == EventType.MouseDrag && _dragH) {
                int v = Mathf.Clamp(Mathf.RoundToInt(_dragH0 + (e.mousePosition.y - _dragHY0)), 120, 1000);
                if (_tableH != null) _tableH.Value = v;
                SR.BlockWindowDrag = true;
                e.Use();
            }
        }

        private static void SaveTableH() { try { if (_tableH != null) _tableH.ConfigFile.Save(); } catch { } }

        private static void SaveEntry(ConfigEntryBase e) { try { if (e != null) e.ConfigFile.Save(); } catch { } }

        //================ 表格（自绘：固定表头 + 房间行 + 自绘竖向滚动条） ================
        //为什么自绘：嵌套 ScrollView 会同时冒出"页面竖条 + 表格横条"两个用不到的滚动条，
        //它们的宽度不随界面缩放（和 SR 界面不匹配，拖动一离开列表就断）。
        //这里几何全部是绝对矩形：表头/房间行按 _scrollCols.x 手动偏移，行再按 _scrollRows.y 偏移，
        //竖向滚动条自己画自己拖 —— 拖动状态跨帧保持，鼠标离开列表照样跟手，松手才结束。
        private const int HostCol = 7;                 //「房主」列：靠左显示（其余列居中）
        private const int QualityCol = 2;              //「游戏质量」列：单击表头 = 显示/隐藏技能匹配质量系数
        private const int SkillCol = 3;                //「技能系数」列：单击表头 = 最大/平均/最小
        private const float CodeLabelW = 58f;          //「房间码」标签宽度（46 太窄，3 个汉字显示不全）

        //单击表头（没有拖动）= 该列自己的切换动作
        private static void HeaderClick(int ci) {
            try {
                if (ci == QualityCol) {
                    bool v = _showQualityCoeff != null && !_showQualityCoeff.Value;
                    if (_showQualityCoeff != null) {
                        _showQualityCoeff.Value = v;
                        SaveEntry(_showQualityCoeff);
                    }
                    Notes(v ? "游戏质量：显示技能匹配质量系数" : "游戏质量：只显示 ✓（不显示系数）",
                          v ? "Quality: showing the skill-match coefficient" : "Quality: marks only");
                } else if (ci == SkillCol) {
                    string cur = SkillModeCfg();
                    string next = cur == "max" ? "avg" : cur == "avg" ? "min" : "max";   //最大 → 平均 → 最小
                    if (_skillMode != null) {
                        _skillMode.Value = next;
                        SaveEntry(_skillMode);
                    }
                    Notes(SR.T("技能系数：", "Skill: ") + SkillModeName(next), "Skill: " + SkillModeName(next));
                }
            } catch (Exception __ex) { SR.Guard.Log("表头点击", __ex); }
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
        private static readonly List<int> _headerCols = new List<int>();          //上表每个单元格 → 列索引
        private static readonly List<Rect> _rowRectsPage = new List<Rect>();      //房间行（页面坐标，已按可视区裁剪）
        private static readonly List<int> _rowRows = new List<int>();             //房间行 → _shownLobbies 下标
        private static readonly List<Rect> _joinRectsPage = new List<Rect>();     //「加入」按钮（页面坐标，钉在右边）
        private static readonly List<int> _joinRows = new List<int>();            //加入按钮 → _shownLobbies 下标
        private static Rect _rowsClipPage, _sbTrackPage, _sbThumbPage, _restoreColsPage, _joinHeaderPage;
        private static bool _restoreColsShown;
        private static bool _sbDragging;                //正在拖竖向滚动条
        private static float _sbGrabY;                  //按下时鼠标相对滑块顶部的偏移
        private static float _tableHeaderH;
        private static float _tableMaxX, _tableMaxY;
        private static float _joinW, _stripW, _joinXPage;  //钉在右边的「操作」列几何
        private static float _pageTopY;                 //页面内容在布局空间里的起点 y（见 RenderOnlinePage）
        private static int _joinDownRow = -1;           //「加入」按钮按下的行
        private static float _dragWatchdog;             //拖拽兜底：太久没有鼠标事件就复位（不用 Input.GetMouseButton，见 TableInput）
        private static int _clickFrame = -1;            //表头单击一帧只处理一次

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

        private static string[] CellTexts(Matchmaker.LobbyListInfo li) {
            return new[] {
                RegionShort(li),
                li.Players + "/4",
                QualityText(li),
                SkillText(li),
                ProgressText(li),
                ModeText(li),
                TagText(li),
                string.IsNullOrEmpty(li.LobbyOwner) ? "?" : li.LobbyOwner,
                LimitText(li),
                FlagsText(li),
                ""
            };
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
            //「操作」列钉在表格右边缘：不参与横向滚动 → 加入按钮永远可见、可点
            //（原来它是最后一列，列宽总和超出可视宽度时它整个被裁在视野外，文字就"没碰到边线却消失"）
            float joinW = 0f;
            for (int i = 0; i < vis.Count; i++) if (vis[i] == JoinCol) joinW = SR.Ctl.Sc(_cw[JoinCol]);
            float stripW = Mathf.Max(SR.Ctl.Sc(40), viewW - joinW);     //可横向滚动的列区
            float totalW = 0f;
            for (int i = 0; i < vis.Count; i++) if (vis[i] != JoinCol) totalW += SR.Ctl.Sc(_cw[vis[i]]);
            _joinW = joinW;
            _stripW = stripW;
            _joinXPage = box.x + viewW - joinW;
            _tableHeaderH = headerH;
            _rowsClipPage = new Rect(box.x, box.y + headerH, stripW, rowsH);
            _tableMaxX = Mathf.Max(0f, totalW - stripW);
            _tableMaxY = Mathf.Max(0f, contentH - rowsH);
            if (_dragCol >= 0 || _moveCol >= 0) _scrollCols = _scrollColsAtDrag;   //拖列期间锁住横向滚动
            _scrollCols.x = Mathf.Clamp(_scrollCols.x, 0f, _tableMaxX);
            _scrollRows.y = Mathf.Clamp(_scrollRows.y, 0f, _tableMaxY);
            if (e == null) return;
            if (e.type == EventType.Repaint) GUI.Box(box, GUIContent.none, SR.Ctl.Footer);
            DrawTableHeader(vis, box, headerH, viewW, stripW, mp);
            DrawTableRows(vis, box, headerH, rowH, stripW, rowsH, viewW, mp);
            DrawTableScrollbar(box, viewW, rowsH, sbW, contentH, mp);
        }

        private static void DrawTableHeader(List<int> vis, Rect box, float headerH, float viewW, float stripW, Vector2 mp) {
            Event e = Event.current;
            _headerCellsPage.Clear();
            _headerCols.Clear();
            _joinHeaderPage = new Rect(0f, 0f, 0f, 0f);
            //—— 可横向滚动的列 ——
            GUI.BeginClip(new Rect(box.x, box.y, stripW, headerH));
            float x = -_scrollCols.x;
            for (int k = 0; k < vis.Count; k++) {
                int ci = vis[k];
                if (ci == JoinCol) continue;                       //钉住的列单独画
                float w = SR.Ctl.Sc(_cw[ci]);
                _headerCellsPage.Add(new Rect(box.x + x, box.y, w, headerH));
                _headerCols.Add(ci);
                if (x + w > -SR.Ctl.Sc(4) && x < stripW + SR.Ctl.Sc(4)) {      //只画看得见的
                    //正在被拖动的列：整格高亮（松手时才真正换位）
                    if (_moveCol == ci) DrawRect(new Rect(x, 0f, w, headerH), 0.22f);
                    SR.Ctl.SecHeaderCenter.Draw(new Rect(x, 0f, w, headerH),
                        new GUIContent(SR.T(Titles[ci], TitlesEn[ci])), false, false, false, false);
                    DrawLine(new Rect(x + w - 1f, 0f, 1f, headerH));
                }
                x += w;
            }
            //拖动中：在目标位置画竖线（在裁剪空间里画，x 要减掉裁剪原点 box.x）
            if (_moveCol >= 0) {
                for (int k = 0; k < _headerCellsPage.Count; k++) {
                    Rect c = _headerCellsPage[k];
                    if (mp.x < c.xMax) {
                        DrawRect(new Rect(c.x - box.x - 1f, 0f, SR.Ctl.Sc(3), headerH), 0.9f);
                        break;
                    }
                }
            }
            GUI.EndClip();
            //—— 钉住的「操作」列（贴表格右边缘，不随横向滚动）——
            if (_joinW > 0f) {
                _joinHeaderPage = new Rect(_joinXPage, box.y, _joinW, headerH);
                GUI.BeginClip(new Rect(_joinXPage, box.y, _joinW, headerH));
                DrawRect(new Rect(0f, 0f, _joinW, headerH), 0.10f);
                SR.Ctl.SecHeaderCenter.Draw(new Rect(0f, 0f, _joinW, headerH),
                    new GUIContent(SR.T(Titles[JoinCol], TitlesEn[JoinCol])), false, false, false, false);
                GUI.EndClip();
                DrawLine(new Rect(_joinXPage - 1f, box.y, 1f, headerH));
            }
            DrawLine(new Rect(box.x, box.y + headerH - 1f, viewW, 1f));
            //被隐藏的列：可滚动区右端一个「＋N」按钮一键恢复
            _restoreColsShown = _hiddenCols.Count > 0;
            if (_restoreColsShown) {
                float bw = SR.Ctl.Sc(52);
                //贴在"可滚动区"右端（再往右就是钉住的「操作」列了）
                _restoreColsPage = new Rect(box.x + stripW - bw - SR.Ctl.Sc(2), box.y + SR.Ctl.Sc(1), bw, headerH - SR.Ctl.Sc(2));
                bool rh = _restoreColsPage.Contains(mp);
                SR.Ctl.BtnClip.Draw(_restoreColsPage, new GUIContent("＋" + _hiddenCols.Count), rh, rh && Input.GetMouseButton(0), false, false);
            } else {
                _restoreColsPage = new Rect(0f, 0f, 0f, 0f);
            }
        }

        private static void DrawTableRows(List<int> vis, Rect box, float headerH, float rowH, float stripW, float rowsH, float viewW, Vector2 mp) {
            //★ _rowRows 必须和 _rowRectsPage 一起清空：它俩是"行矩形 → _shownLobbies 下标"的一一对应，
            //   只清矩形不清下标的话，下标会跨帧累积 → 右键命中的是上一帧（甚至更早）那一行 →
            //   表现就是"对着某个房间右键，隐藏的是上面那一个房间"。
            _rowRectsPage.Clear(); _rowRows.Clear();
            _joinRectsPage.Clear(); _joinRows.Clear();
            //—— 可横向滚动的列 ——
            GUI.BeginClip(new Rect(box.x, box.y + headerH, stripW, rowsH));
            for (int i = 0; i < _shownLobbies.Count; i++) {
                float y = i * rowH - _scrollRows.y;
                if (y > rowsH || y + rowH < 0f) continue;             //不在可视区就不画
                Matchmaker.LobbyListInfo li = _shownLobbies[i];
                bool can = IsJoinable(li);
                string[] cells = CellTexts(li);
                float ry = box.y + headerH + y;
                //命中矩形按可视区裁一下（半截露在表头/框外的行不该被点到）
                float hy0 = Mathf.Max(ry, _rowsClipPage.y);
                float hy1 = Mathf.Min(ry + rowH, _rowsClipPage.yMax);
                _rowRectsPage.Add(new Rect(box.x, hy0, stripW, Mathf.Max(0f, hy1 - hy0)));
                _rowRows.Add(i);
                Color prev = GUI.color;
                if (!can) GUI.color = new Color(1f, 1f, 1f, 0.55f);   //不可加入的整行压暗
                float x = -_scrollCols.x;
                for (int k = 0; k < vis.Count; k++) {
                    int ci = vis[k];
                    if (ci == JoinCol) continue;                      //钉住的列单独画
                    float w = SR.Ctl.Sc(_cw[ci]);
                    Rect r = new Rect(x, y, w, rowH);
                    //房主列靠左，其余列居中
                    GUIStyle st = ci == HostCol ? SR.Ctl.LabelClip : SR.Ctl.LabelClipCenter;
                    st.Draw(r, new GUIContent(cells[ci]), false, false, false, false);
                    DrawLine(new Rect(r.xMax - 1f, r.y, 1f, r.height));
                    x += w;
                }
                GUI.color = prev;
                DrawLine(new Rect(0f, y + rowH - 1f, stripW, 1f));
            }
            if (_shownLobbies.Count == 0) {
                SR.Ctl.LabelClip.Draw(new Rect(SR.Ctl.Sc(4), 0f, Mathf.Max(SR.Ctl.Sc(60), stripW - SR.Ctl.Sc(8)), rowH),
                    new GUIContent(SR.T("（没有符合筛选的房间）", "(no lobbies match the filters)")), false, false, false, false);
            }
            GUI.EndClip();
            //—— 钉住的「操作」列：加入按钮（永远在表格右边缘、永远可点）——
            if (_joinW > 0f) {
                GUI.BeginClip(new Rect(_joinXPage, box.y + headerH, _joinW, rowsH));
                for (int i = 0; i < _shownLobbies.Count; i++) {
                    float y = i * rowH - _scrollRows.y;
                    if (y > rowsH || y + rowH < 0f) continue;
                    Matchmaker.LobbyListInfo li = _shownLobbies[i];
                    bool can = IsJoinable(li);
                    float ry = box.y + headerH + y;
                    float hy0 = Mathf.Max(ry, box.y + headerH);
                    float hy1 = Mathf.Min(ry + rowH, box.y + headerH + rowsH);
                    Rect rp = new Rect(_joinXPage + SR.Ctl.Sc(2), hy0, _joinW - SR.Ctl.Sc(4), Mathf.Max(0f, hy1 - hy0));
                    _joinRectsPage.Add(rp);
                    _joinRows.Add(i);
                    bool hov = can && rp.Contains(mp);
                    Color pc = GUI.color;
                    if (!can) GUI.color = new Color(1f, 1f, 1f, 0.5f);
                    //窄格子按钮：内边距小的样式，宽度够就不该把文字裁掉
                    SR.Ctl.BtnClip.Draw(new Rect(SR.Ctl.Sc(2), y + SR.Ctl.Sc(1), _joinW - SR.Ctl.Sc(4), rowH - SR.Ctl.Sc(2)),
                        new GUIContent(JoinText(li)), hov, hov && _joinDownRow == i, false, false);
                    GUI.color = pc;
                }
                GUI.EndClip();
                DrawLine(new Rect(_joinXPage - 1f, box.y + headerH, 1f, rowsH));
            }
        }

        //自绘竖向滚动条：Sc() 缩放（跟 SR 界面一致）、贴在表格框右边界（也就是靠近窗口边框）
        private static void DrawTableScrollbar(Rect box, float viewW, float rowsH, float sbW, float contentH, Vector2 mp) {
            _sbTrackPage = new Rect(0f, 0f, 0f, 0f);
            _sbThumbPage = new Rect(0f, 0f, 0f, 0f);
            if (sbW <= 0f) { _sbDragging = false; return; }
            Rect track = new Rect(box.x + viewW, box.y + _tableHeaderH, sbW, rowsH);
            float thumbH = Mathf.Clamp(rowsH * rowsH / Mathf.Max(1f, contentH), SR.Ctl.Sc(18), rowsH);
            float ty = _tableMaxY <= 0f ? 0f : (_scrollRows.y / _tableMaxY) * (rowsH - thumbH);
            _sbTrackPage = track;
            _sbThumbPage = new Rect(track.x, track.y + ty, track.width, thumbH);
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            Color prev = GUI.color;
            GUI.color = new Color(0.1f, 0.1f, 0.12f, 1f);
            GUI.DrawTexture(track, Px());
            GUI.color = (_sbDragging || _sbThumbPage.Contains(mp)) ? new Color(0.58f, 0.66f, 0.84f, 1f) : new Color(0.4f, 0.46f, 0.58f, 1f);
            GUI.DrawTexture(new Rect(_sbThumbPage.x + SR.Ctl.Sc(2), _sbThumbPage.y,
                Mathf.Max(SR.Ctl.Sc(2), _sbThumbPage.width - SR.Ctl.Sc(4)), _sbThumbPage.height), Px());
            GUI.color = prev;
        }

        private static void TableInput(Rect box, List<int> vis, Event e) {
            if (e == null) return;
            Vector2 mp = e.mousePosition;
            //拖拽兜底复位：**不能**用 Input.GetMouseButton(0) 判断（它和 IMGUI 的事件流不一定同步，
            //UI 打开时可能一直是 false → 一按下就被这里清掉，表现就是"表头拖不动 / 点了没反应"）。
            //改成"超过 1.2 秒没有任何鼠标事件"才复位（只有真的漏收 MouseUp 时才会触发）。
            try {
                if (_dragWatchdog > 0f && Time.unscaledTime > _dragWatchdog) {
                    if (_sbDragging) { _sbDragging = false; }
                    if (_moveCol >= 0) { _moveCol = -1; _moveDx = 0f; }
                    if (_dragCol >= 0) { _dragCol = -1; SaveWidths(); }
                    if (_dragH) { _dragH = false; SaveTableH(); }
                    _joinDownRow = -1;
                    SR.BlockWindowDrag = false;
                    _dragWatchdog = 0f;
                }
                if (e.type == EventType.MouseDown || e.type == EventType.MouseDrag) {
                    if (_moveCol >= 0 || _dragCol >= 0 || _sbDragging || _dragH) _dragWatchdog = Time.unscaledTime + 1.2f;
                } else if (e.type == EventType.MouseUp) {
                    _dragWatchdog = 0f;
                }
            } catch { }
            //---- 竖向滚动条：手动拖拽（离开列表也继续跟手，松手才结束）----
            if (_sbDragging) {
                if (e.type == EventType.MouseDrag || e.type == EventType.MouseUp) {
                    float thumbH = _sbThumbPage.height;
                    float t = Mathf.Clamp01((mp.y - _sbTrackPage.y - _sbGrabY) / Mathf.Max(1f, _sbTrackPage.height - thumbH));
                    _scrollRows.y = t * _tableMaxY;
                    SR.BlockWindowDrag = true;
                    if (e.type == EventType.MouseUp) { _sbDragging = false; SR.BlockWindowDrag = false; }
                    e.Use();
                    return;
                }
            } else if (e.type == EventType.MouseDown && e.button == 0 && _sbTrackPage.width > 0f && _sbTrackPage.Contains(mp)) {
                float thumbH = _sbThumbPage.height;
                bool onThumb = _sbThumbPage.Contains(mp);
                _sbGrabY = onThumb ? (mp.y - _sbThumbPage.y) : thumbH * 0.5f;
                _sbDragging = true;
                SR.BlockWindowDrag = true;
                if (!onThumb) {      //点轨道：直接跳到该位置
                    float t = Mathf.Clamp01((mp.y - _sbTrackPage.y - _sbGrabY) / Mathf.Max(1f, _sbTrackPage.height - thumbH));
                    _scrollRows.y = t * _tableMaxY;
                }
                e.Use();
                return;
            }
            //---- 滚轮：默认纵向；Ctrl = 横向；没有纵向余量时自动变横向 ----
            //（Shift+滚轮也走纵向：Shift 不该让滚轮失效。Windows 上按 Shift 时系统常常只给 delta.x，
            //  所以两个轴哪个非零就用哪个 —— 否则"按住 Shift 滚轮完全没反应"。）
            if (e.type == EventType.ScrollWheel && box.Contains(mp)) {
                float amt = Mathf.Abs(e.delta.y) > 0.001f ? e.delta.y : e.delta.x;
                if (e.control && _tableMaxX > 0f) {
                    _scrollCols.x = Mathf.Clamp(_scrollCols.x + amt * SR.Ctl.Sc(60), 0f, _tableMaxX);
                    e.Use();
                } else if (_tableMaxY > 0f) {
                    _scrollRows.y = Mathf.Clamp(_scrollRows.y + amt * SR.Ctl.Sc(40), 0f, _tableMaxY);
                    e.Use();
                } else if (_tableMaxX > 0f) {
                    _scrollCols.x = Mathf.Clamp(_scrollCols.x + amt * SR.Ctl.Sc(60), 0f, _tableMaxX);
                    e.Use();
                }
            }
            //---- 表头：分隔线调宽（↔）/ 表头换位（✥）/ 单击切换 / 右键隐藏 / Shift+右键还原 ----
            bool hoverSep = false, hoverCell = false;
            bool inBox = box.Contains(mp);   //横向滚出去的列会落到框外，判定要塞回框内
            for (int k = 0; k < _headerCellsPage.Count; k++) {
                int ci = _headerCols[k];
                Rect cell = _headerCellsPage[k];
                Rect sep = new Rect(cell.xMax - SR.Ctl.Sc(3), cell.y, SR.Ctl.Sc(7), cell.height);
                bool onSep = inBox && sep.Contains(mp);
                if (onSep) hoverSep = true;
                else if (inBox && cell.Contains(mp)) hoverCell = true;
                HandleColDrag(e, cell, ci, mp, inBox);
                HandleColMove(e, cell, ci, k, _headerCols, mp, inBox);
            }
            SetMouseCursor(hoverSep || _dragCol >= 0, hoverCell || _moveCol >= 0);
            //—— 悬停在表头：显示该列说明（GUI.tooltip 在 OnGUI 末尾统一画）
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
            //---- 列恢复按钮 ----
            if (_restoreColsShown && _restoreColsPage.width > 0f && _restoreColsPage.Contains(mp)
                && e.type == EventType.MouseDown && e.button == 0) {
                _hiddenCols.Clear();
                _visCache = null;
                SaveCols();
                Notes("已显示全部列", "All columns shown");
                e.Use();
            }
            //---- 右键：表头 = 隐藏该列 / Shift = 还原；房间行 = 隐藏该房间 ----
            if (e.type == EventType.MouseDown && e.button == 1 && box.Contains(mp)) {
                if (e.shift) {
                    ResetTable();
                    e.Use();
                } else {
                    bool handled = false;
                    for (int k = 0; k < _headerCellsPage.Count; k++) {
                        if (!_headerCellsPage[k].Contains(mp)) continue;
                        int ci = _headerCols[k];
                        if (_headerCols.Count > 2) {      //至少留 2 列（「操作」列是钉住的，不参与）
                            _hiddenCols.Add(ci);
                            _visCache = null;
                            SaveCols();
                            string nm = SR.T(Titles[ci], TitlesEn[ci]);
                            Notes(SR.T("已隐藏列：", "Hidden column: ") + nm, SR.T("已隐藏列：", "Hidden column: ") + nm);
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
                                Notes("已隐藏：" + (string.IsNullOrEmpty(li.LobbyOwner) ? li.sLobbyID : li.LobbyOwner),
                                      "Hidden: " + (string.IsNullOrEmpty(li.LobbyOwner) ? li.sLobbyID : li.LobbyOwner));
                            }
                            break;
                        }
                    }
                    e.Use();
                }
            }
            //---- 「加入」按钮：手动点击 ----
            for (int i = 0; i < _joinRectsPage.Count; i++) {
                if (!_joinRectsPage[i].Contains(mp)) continue;
                int si = _joinRows[i];
                Matchmaker.LobbyListInfo li = si >= 0 && si < _shownLobbies.Count ? _shownLobbies[si] : null;
                if (li == null || !IsJoinable(li)) break;
                SR.BlockWindowDrag = true;
                if (e.type == EventType.MouseDown && e.button == 0) { _joinDownRow = si; e.Use(); }
                else if (e.type == EventType.MouseUp && e.button == 0 && _joinDownRow == si) { _joinDownRow = -1; JoinLobby(li.sLobbyID); e.Use(); }
                break;
            }
            if (e.type == EventType.MouseUp) _joinDownRow = -1;
        }

        //Shift+右键 = 还原列宽/表格高度/列顺序 + 取消所有隐藏（房间与列）
        private static void ResetTable() {
            try {
                _cw = (float[])DefWidths.Clone();
                _order = new int[ColCount];
                for (int i = 0; i < ColCount; i++) _order[i] = i;
                _hiddenCols.Clear();
                _visCache = null;
                if (_tableH != null) _tableH.Value = 0;
                _hidden.Clear();
                SaveCols();
                Notes("已还原列宽/顺序/高度并显示全部房间与列", "Widths/order/height reset; all lobbies and columns restored");
            } catch { }
        }

        private static readonly string[] ModeVals = { "FREEPLAY", "CREATIVE", "PARTY", "CHALLENGE" };
        private static readonly string[] TagVals = { "Fun", "Competitive", "Beginner", "CustomLevels" };

        //下拉显示名（内部值 → 界面名）：选项列表与"当前选中项"都用它，保证两处永不脱节
        private static string DispName(string key, string v) {
            if (string.IsNullOrEmpty(v)) return SR.T("全部", "All");
            if (key == "Filter Region") return DispRegion(v);
            if (key == "Filter Mode") return SR.T(v == "FREEPLAY" ? "自由" : v == "CREATIVE" ? "创意" : v == "PARTY" ? "派对" : "挑战",
                                                  v == "FREEPLAY" ? "Freeplay" : v == "CREATIVE" ? "Creative" : v == "PARTY" ? "Party" : "Challenge");
            if (key == "Filter Tag") return SR.T(v == "Fun" ? "好玩" : v == "Competitive" ? "竞技" : v == "Beginner" ? "新手" : "自定义关卡",
                                                 v == "Fun" ? "Fun" : v == "Competitive" ? "Competitive" : v == "Beginner" ? "Beginner" : "Custom levels");
            if (key == "Filter Min Players") return v == "lt4" ? "＜4" : "≥" + v;
            if (key == "Filter AFK") return v == "hide" ? SR.T("隐藏AFK", "hide AFK") : SR.T("仅AFK", "only AFK");
            return v; //区域：值本身就是本地化短名
        }

        //================ 下拉筛选控件 ================
        //标签用**固定宽度**（RestoreLabel 会按文字实际宽度收缩，CJK「人数」和 ASCII「AFK」不一样宽 →
        //第二行下拉框会比第一行靠左，看着不齐）；下拉框本身也用固定宽度（不要随窗口变得很宽）。
        private const float FilterLabelW = 46f;
        private const float FilterComboW = 104f;
        private static void DropFilter(string labelZh, string labelEn, string tipZh, string tipEn, string key, string[] values, float cellW) {
            try {
                ConfigEntryBase e = SR.Ctl.FindEntry(SecOnline, key);
                float labelW = SR.Ctl.Sc(FilterLabelW);
                float comboW = SR.Ctl.Sc(FilterComboW);
                GUILayout.BeginHorizontal(GUILayout.Width(labelW + comboW + SR.Ctl.Sc(14)));
                GUILayout.Label(new GUIContent(SR.T(labelZh, labelEn), SR.T(tipZh, tipEn)), SR.Ctl.Label,
                    GUILayout.Width(labelW), GUILayout.Height(SR.Ctl.Sc(26)));
                //滚轮/点击改值时不要带着页面滚动条一起动（外层滚动位置在 RenderOnlinePage 里还原）
                List<string> opts = new List<string>();
                opts.Add(SR.T("全部", "All"));
                if (values != null) {
                    for (int i = 0; i < values.Length; i++) opts.Add(DispName(key, values[i]));
                }
                //同时只允许一个下拉框展开
                bool open = _openCombo == key;
                int sel = SR.Ctl.ComboBox(e, DispName(key, FindStr(key)), null, opts.ToArray(), ref open, comboW);
                if (open) {
                    if (_openCombo != key) { _openCombo = key; _scrollFix = true; }
                } else if (_openCombo == key) {
                    _openCombo = "";
                    _scrollFix = true;
                }
                if (sel >= 0) {
                    SetStr(key, sel == 0 ? "" : (values != null && sel - 1 < values.Length ? values[sel - 1] : ""));
                    _scrollFix = true;
                }
                GUILayout.EndHorizontal();
            } catch (Exception __ex) { SR.Guard.Log("房间筛选下拉", __ex); }
        }

        private static string FindStr(string key) {
            try {
                ConfigEntryBase e = SR.Ctl.FindEntry(SecOnline, key);
                return e != null ? (e.BoxedValue as string ?? "") : "";
            } catch { return ""; }
        }

        private static void SetStr(string key, string value) {
            try {
                ConfigEntryBase e = SR.Ctl.FindEntry(SecOnline, key);
                if (e != null) SR.Ctl.SetValue(e, value);
            } catch (Exception __ex) { SR.Guard.Log("设置房间筛选", __ex); }
        }

        //================ 列宽 / 列顺序 / 隐藏列（可拖动 + 持久化） ================
        private static void EnsureWidths() {
            if (_cw != null) return;
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
            } catch { }
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
            } catch { }
        }

        private static void EnsureHiddenCols() {
            try {
                string v = _hiddenColsEntry != null ? _hiddenColsEntry.Value : null;
                if (string.IsNullOrEmpty(v)) return;
                foreach (string s in v.Split(',')) {
                    int k;
                    if (int.TryParse(s, out k) && k >= 0 && k < ColCount && k != JoinCol) _hiddenCols.Add(k);
                }
            } catch { }
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
                if (_colWidths != null) _colWidths.ConfigFile.Save();
            } catch (Exception __ex) { SR.Guard.Log("保存列设置", __ex); }
        }

        private static void SaveWidths() { SaveCols(); }

        //================ 表格绘制（固定表头 + 细线分隔 + 列可拖动换位/隐藏/调宽） ================
        private const int ColCount = 11;
        private const int JoinCol = 10;          //「操作」列：不可隐藏（否则没法加入）
        private static readonly string[] Titles = { "区域", "人数", "游戏质量", "技能系数", "进度", "游戏模式", "标签", "房主", "限制", "标记", "操作" };
        private static readonly string[] TitlesEn = { "Region", "Players", "Quality", "Skill", "Progress", "Mode", "Tag", "Host", "Limit", "Flags", "Operation" };
        private static readonly string[] TitleTips = {
            "服务器区域（游戏自己的本地化短名，显示为 亚太/欧盟/美国）",
            "当前人数 / 4（房间最多 4 人，人满不可加入）",
            "✓ = 房主质量分达到 OkMatchScore，✓✓ = 达到 GoodMatchScore，再一个 ✓ = 匹配质量系数达到 SkillMatchQualityThreshold。\n系数 = CalculatedSkillMatchQuality（0~1）：游戏用大厅玩家技能串算出的「技能匹配质量」，越接近 1 = 房里玩家水平和本地玩家越接近（匹配越好）。\n**单击本列表头**＝显示/隐藏这个系数（默认不显示，只显示 ✓）。",
            "这个房间里玩家技能值的 最大 / 平均 / 最小（游戏自己的 PlayerSkills 串，格式 数量,技能,权重,...）。数值越高 = 水平越高。\n**单击本列表头**＝在 最大技能 → 平均技能 → 最小技能 之间切换（默认平均技能）。",
            "游戏自己的对局进度：大厅中 / 房主AFK / 剩余百分比",
            "游戏模式（默认规则集）；自定义规则集显示规则集名",
            "房间标签（游戏本地化）",
            "房主名字",
            "制胜积分 / 长度限制（轮数、时间或 无限制）",
            "模组 / 房主AFK",
            "点「加入」进房（在对局内会先退出当前房间，回到界面就自动加入）；不可加入的显示原因并压暗",
        };
        private static readonly string[] TitleTipsEn = {
            "Server region (game-localized short name, shown as AP/EU/US)",
            "Players / 4 (a lobby holds at most 4; a full lobby cannot be joined)",
            "✓ = host quality reaches OkMatchScore, ✓✓ = reaches GoodMatchScore, plus another ✓ when the skill-match coefficient reaches SkillMatchQualityThreshold.\nCoefficient = CalculatedSkillMatchQuality (0-1): the game's skill-match quality computed from the lobby's player skills; closer to 1 = the players' skill is closer to yours.\n**Click this header** to show/hide that coefficient (hidden by default, marks only).",
            "Max / average / min skill value among this lobby's players (the game's own PlayerSkills string: count,skill,weight,...). Higher = a stronger lobby.\n**Click this header** to cycle max → average → min (default: average).",
            "The game's own match progress: in lobby / host AFK / remaining percent",
            "Game mode (default ruleset); a custom ruleset shows the ruleset name",
            "Lobby tag (game-localized)",
            "Host name",
            "Win points / length limit (rounds, time or no limit)",
            "Mods / host AFK",
            "Click Join to enter. While in a session it leaves the current one first and joins automatically. Unjoinable rows are dimmed",
        };

        //列顺序 / 被隐藏的列 / 光标贴图
        private static int[] _order;
        private static readonly HashSet<int> _hiddenCols = new HashSet<int>();
        private static ConfigEntry<string> _colOrder, _hiddenColsEntry;
        private static List<int> _visCache;
        private static int _visFrame = -1;
        private static Texture2D _curResize, _curMove;
        //拖列换位状态
        private static int _moveCol = -1;
        private static float _moveX0;
        private static float _moveDx;
        private static Vector2 _scrollColsAtDrag;           //按下时的横向滚动位置（拖列期间锁住它）

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

        private static Texture2D Px() {
            if (_px == null) {
                _px = new Texture2D(1, 1);
                _px.SetPixel(0, 0, Color.white);
                _px.Apply();
            }
            return _px;
        }

        private static void DrawLine(Rect r) {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.22f);
            GUI.DrawTexture(r, Px());
            GUI.color = old;
        }

        private static void DrawRect(Rect r, float a) {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            Color old = GUI.color;
            GUI.color = new Color(0.55f, 0.78f, 1f, a);
            GUI.DrawTexture(r, Px());
            GUI.color = old;
        }

        //光标贴图（32x32，运行时生成）：↔ = 调列宽，✥ = 拖动换位
        private static void MakeCursorTex() {
            try {
                if (_curResize == null) {
                    _curResize = new Texture2D(32, 32, TextureFormat.RGBA32, false);
                    Color32[] p = new Color32[32 * 32];
                    for (int y = 0; y < 32; y++) {
                        for (int x = 0; x < 32; x++) {
                            int d = Mathf.Abs(y - 16);
                            bool body = (d <= 1 && x >= 6 && x <= 25);                                  //横杆
                            bool lt = (x >= 4 && x <= 11 && d <= (11 - x)) || (x >= 4 && x <= 11 && y >= 16 - (x - 4) && y <= 16 + (x - 4));  //左箭头
                            bool rt = (x <= 27 && x >= 20 && d <= (x - 20)) || (x <= 27 && x >= 20 && y >= 16 - (27 - x) && y <= 16 + (27 - x));
                            p[y * 32 + x] = (body || lt || rt) ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
                        }
                    }
                    _curResize.SetPixels32(p);
                    _curResize.Apply();
                }
                if (_curMove == null) {
                    _curMove = new Texture2D(32, 32, TextureFormat.RGBA32, false);
                    Color32[] p = new Color32[32 * 32];
                    for (int y = 0; y < 32; y++) {
                        for (int x = 0; x < 32; x++) {
                            bool cross = (Mathf.Abs(x - 16) <= 1 && y >= 5 && y <= 26) || (Mathf.Abs(y - 16) <= 1 && x >= 5 && x <= 26);
                            p[y * 32 + x] = cross ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
                        }
                    }
                    _curMove.SetPixels32(p);
                    _curMove.Apply();
                }
            } catch (Exception __ex) { SR.Guard.Log("生成光标贴图", __ex); }
        }

        //注意：UCH 自己有 Cursor 类，这里必须写全 UnityEngine.Cursor
        //光标的"强制显示"必须由**每帧末统一收尾**：页面只在悬停可拖拽区时声明想要的形状，
        //真正的 SetCursor/visible 由 SR.Window 每帧末尾（EndFrameCursor）应用并复位。
        //这样面板一关、或切到别的栏目（本页不再渲染）→ 声明自然变成 0 → 系统光标被还原，
        //不会出现"关掉界面后 ↔/✥ 光标还留在屏幕上"。
        private static int _cursorWant;                  //0 = 普通，1 = ↔ 调宽，2 = ✥ 换位
        private static bool _cursorForced;
        private static bool _cursorWasVisible = true;

        private static void SetMouseCursor(bool resize, bool move) {
            _cursorWant = resize ? 1 : (move ? 2 : 0);
        }

        //由 SR.Window 在每帧末尾调用（含"面板已经关了"的提前返回分支）
        public static void EndFrameCursor() {
            try {
                bool force = _cursorWant != 0;
                if (force) {
                    if (!_cursorForced) { _cursorForced = true; _cursorWasVisible = UnityEngine.Cursor.visible; }
                    UnityEngine.Cursor.visible = true;
                } else if (_cursorForced) {
                    _cursorForced = false;
                    UnityEngine.Cursor.visible = _cursorWasVisible;
                }
                Texture2D tex = null;
                if (_cursorWant != 0) {
                    MakeCursorTex();
                    tex = _cursorWant == 1 ? _curResize : _curMove;
                }
                if (tex != null) UnityEngine.Cursor.SetCursor(tex, new Vector2(16f, 16f), CursorMode.ForceSoftware);
                else if (_cursorWant == 0) UnityEngine.Cursor.SetCursor(null, Vector2.zero, CursorMode.Auto);
            } catch { } finally { _cursorWant = 0; }
        }

        //表头分隔线上按住左键左右拖动 = 改列宽
        private static void HandleColDrag(Event e, Rect cell, int ci, Vector2 mp, bool inBox) {
            if (e == null) return;
            Rect sep = new Rect(cell.xMax - SR.Ctl.Sc(3), cell.y, SR.Ctl.Sc(7), cell.height);
            if (e.type == EventType.MouseDown && e.button == 0 && inBox && sep.Contains(mp)) {
                _dragCol = ci;
                _dragX0 = mp.x;
                _dragW0 = _cw[ci];
                _scrollColsAtDrag = _scrollCols;   //拖列时锁住横向滚动，免得一起被拖走
                SR.BlockWindowDrag = true;   //别让 SR 主窗口跟着一起被拖走
                e.Use();
            } else if (e.type == EventType.MouseDrag && _dragCol == ci) {
                //拖动中鼠标离开列表也继续跟手（只认状态，不要求还在格子里）
                _cw[ci] = Mathf.Clamp(_dragW0 + (mp.x - _dragX0), 28f, 600f);
                _scrollCols = _scrollColsAtDrag;
                SR.BlockWindowDrag = true;
                e.Use();
            }
        }

        //表头空白处按住左键左右拖动 = 换列位置；没移动（含 1~2px 抖动）= 单击（见表头切换动作）
        private static void HandleColMove(Event e, Rect cell, int ci, int k, List<int> cols, Vector2 mp, bool inBox) {
            if (e == null) return;
            Rect sep = new Rect(cell.xMax - SR.Ctl.Sc(3), cell.y, SR.Ctl.Sc(7), cell.height);
            Rect body = new Rect(cell.x, cell.y, cell.width - SR.Ctl.Sc(4), cell.height);
            if (e.type == EventType.MouseDown && e.button == 0 && inBox && body.Contains(mp) && !sep.Contains(mp)) {
                _moveCol = ci;
                _moveX0 = mp.x;
                _moveDx = 0f;
                _scrollColsAtDrag = _scrollCols;
                SR.BlockWindowDrag = true;
                e.Use();
            } else if (e.type == EventType.MouseDrag && _moveCol == ci) {
                _moveDx = mp.x - _moveX0;      //离开列表也继续跟手
                _scrollCols = _scrollColsAtDrag;
                SR.BlockWindowDrag = true;
                e.Use();
            } else if (e.type == EventType.MouseUp && _moveCol == ci) {
                //1~3px 的手抖不该算"拖动"（原来用 == 0，鼠标稍微动一下点击就失效了）
                bool dragged = Mathf.Abs(_moveDx) > SR.Ctl.Sc(4);
                int target = k;
                if (dragged && _headerCellsPage.Count > 0) {
                    for (int j = 0; j < _headerCellsPage.Count; j++) {
                        if (mp.x < _headerCellsPage[j].xMax) { target = j; break; }
                        target = _headerCellsPage.Count - 1;
                    }
                }
                if (dragged && target != k && target >= 0 && target < cols.Count) {
                    int from = -1, to = -1;
                    for (int i = 0; i < _order.Length; i++) {
                        if (_order[i] == ci) from = i;
                        if (_order[i] == cols[target]) to = i;
                    }
                    if (from >= 0 && to >= 0 && from != to) {
                        int moved = _order[from];
                        List<int> l = new List<int>(_order);
                        l.RemoveAt(from);
                        l.Insert(to, moved);
                        _order = l.ToArray();
                        _visCache = null;
                        SaveCols();
                        Notes("列顺序已保存", "Column order saved");
                    }
                } else if (!dragged && cell.Contains(mp)) {
                    //单击表头：按列自己的动作（游戏质量 = 显示/隐藏匹配质量系数；技能系数 = 最大/平均/最小）
                    HeaderClickOnce(ci);
                }
                _moveCol = -1;
                _moveDx = 0f;
                SR.BlockWindowDrag = false;
                e.Use();
            }
        }

        //单击一帧只处理一次（万一有多条命中路径同时命中，不会来回切两次 = 看起来没反应）
        private static void HeaderClickOnce(int ci) {
            if (_clickFrame == Time.frameCount) return;
            _clickFrame = Time.frameCount;
            HeaderClick(ci);
        }

        //技能系数：PlayerSkills = "数量,技能,权重,技能,权重,..."（游戏 readableSkill 的格式）
        //按 _skillMode 取 最大 / 平均 / 最小（默认平均；单击本列表头切换）
        private static string SkillText(Matchmaker.LobbyListInfo li) {
            try {
                string s = li.PlayerSkills;
                if (string.IsNullOrEmpty(s)) return "-";
                string[] p = s.Split(',');
                int n;
                if (p.Length < 3 || !int.TryParse(p[0], out n) || n <= 0) return "-";
                if (p.Length - 1 < 2 * n) n = (p.Length - 1) / 2;
                if (n <= 0) return "-";
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
                if (ok == 0) return "-";
                double val = mode == "max" ? max : mode == "min" ? min : sum / ok;
                return val.ToString("F0", CultureInfo.InvariantCulture);
            } catch { return "-"; }
        }

        //本地玩家自己的技能系数（游戏自己的存档字段；游戏在调试文本里也是拿它当"我的技能"）
        private static string MySkillText() {
            try {
                StatTracker st = StatTracker.Instance;
                SaveFileData d = st != null ? st.GetSaveFileDataForMainUser() : null;
                if (d == null) return null;
                return d.SkillMean.ToString("F0", CultureInfo.InvariantCulture);
            } catch { return null; }
        }

        private static string[] ToArray(HashSet<string> set) {
            List<string> l = new List<string>(set);
            l.Sort(StringComparer.OrdinalIgnoreCase);
            return l.ToArray();
        }
    }
}
