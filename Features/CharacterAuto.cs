// 角色：进入树屋时自动选一次角色（指定 / 随机空闲 / 智能），以及自动时装（随机整套）。
// 精简版：不再有延迟、重试间隔、已选不覆盖、外观记录、AutoDress 相关内容。
using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.character", "Character", "1.0")]
    public class CharacterAuto : ITweak {
        private const string Sec = "Character";

        // ---------------- 自动选择 ----------------
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<string> _mode;      // target / random / smart
        private static ConfigEntry<string> _animal;    // 指定动物
        // ---------------- 自动时装 ----------------
        private static ConfigEntry<bool> _outfitOn;    // 自动时装（随机整套）
        private static ConfigEntry<string> _outfitData; // 每个角色一套：CHICKEN=0,1,2,3|HORSE=...

        private static readonly string[] ModeVals = { "smart", "target", "random" };   // 顺序：智能 / 指定 / 随机

        // 每进一次房间只自动选一次：场景切换时复位。
        private static bool _triedThisRoom;
        private static Character _dressedFor;          // 已经成功套用时装的角色实例
        private static int _dressRetry;
        private static ConfigEntry<KeyCode> _dressKey;   // 原地换装快捷键（通用条目，右键绑定）
        private static OutfitController _dressCtrl;   // 我们打开的换装界面（用于关闭后自救）
        private static Character _dressChar;                // 套用失败的重试计数（角色还没初始化完时会失败）
        private static float _nextTryAt;

        private static string ModeLabel(string v) {
            if (v == "random") return SRApi.T("随机空闲", "Random free");
            if (v == "smart") return SRApi.T("智能选择", "Smart pick");
            return SRApi.T("指定角色", "Specific animal");
        }

        // 可用动物：优先读存档的解锁表（与实际解锁一致）；读不到时退回枚举全集。
        private static readonly List<string> Animals = new List<string>();
        private static readonly List<string> AnimalLabels = new List<string>();
        private static string _animalLang;   // 上次构建列表时的游戏语言
        private static string CurrentLang() {
            try { return global::I2.Loc.LocalizationManager.CurrentLanguageCode ?? ""; } catch { return ""; }
        }
        private static void EnsureAnimalList() {
            // 语言变了就重建：否则切语言后动物列表仍是旧语言的文字
            string lang = CurrentLang();
            if (Animals.Count > 0 && lang == _animalLang) return;
            _animalLang = lang;
            _animalNameCache.Clear();   // 换语言：角色名要重查
            Animals.Clear(); AnimalLabels.Clear();
            bool[] unlocked = UnlockedAnimals();
            foreach (Character.Animals a in Enum.GetValues(typeof(Character.Animals))) {
                if (a == Character.Animals.NONE) continue;
                bool ok = true;
                try { if (unlocked != null && (int)a < unlocked.Length) ok = unlocked[(int)a]; }
                catch (Exception __u) { SRApi.Guard.Log("角色/解锁过滤", __u); ok = true; }
                Animals.Add(a.ToString());
                // 全量列出（解锁表可能缺项/DLC 不同步，以前过滤会把角色漏掉）；未解锁的加后缀
                AnimalLabels.Add(AnimalZh(a.ToString()) + (ok ? "" : SRApi.T("（未解锁）", " (locked)")));
            }
            if (Animals.Count == 0) { Animals.Add("CHICKEN"); AnimalLabels.Add(AnimalZh("CHICKEN")); }
        }

        // 存档里的解锁表（CharactersUnlocked，StatBoolArray，按枚举下标）。
        private static bool[] UnlockedAnimals() {
            try {
                SaveFileData data = StatTracker.Instance.GetSaveFileDataForMainUser();
                if (data == null) return null;
                StatBoolArray st = data.GetStat<StatBoolArray>("CharactersUnlocked");
                return st != null ? st.values : null;
            } catch (Exception __ex) { SRApi.Guard.Log("角色/读取解锁表", __ex); return null; }
        }

        // 角色名直接用游戏自己的本地化表（ScriptLocalization.CharacterNames）；
        // 取不到时退回枚举名，保证列表永远可用。
        // 角色名一律查游戏的本地化表（CharacterNames/<PascalCase>），不写死映射：
        // 写死会漏掉新角色/DLC（枚举有值但没有对应 case 时只能显示英文枚举名，看起来像"列表不全"）。
        private static readonly Dictionary<string, string> _animalNameCache = new Dictionary<string, string>(StringComparer.Ordinal);
        private static string AnimalZh(string en) {
            try {
                if (string.IsNullOrEmpty(en)) return "";
                string hit;
                if (_animalNameCache.TryGetValue(en, out hit)) return hit;
                string pascal = Pascal(en);
                string name = null;
                try { name = global::I2.Loc.LocalizationManager.GetTranslation("CharacterNames/" + pascal); }
                catch (Exception __gt) { SRApi.Guard.Log("角色/游戏角色名", __gt); }
                if (string.IsNullOrEmpty(name) || name == "CharacterNames/" + pascal) name = pascal;
                _animalNameCache[en] = name;
                return name;
            } catch (Exception __ex) { SRApi.Guard.Log("角色/角色名", __ex); return en; }
        }

        // CHICKEN -> Chicken，REDPANDA -> RedPanda，REALBUNNY -> RealBunny
        private static string Pascal(string en) {
            try {
                StringBuilder sb = new StringBuilder(en.Length);
                bool up = true;
                for (int i = 0; i < en.Length; i++) {
                    char c = en[i];
                    if (c == '_') { up = true; continue; }
                    sb.Append(up ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
                    up = false;
                }
                return sb.ToString();
            } catch { return en; }
        }



        private static Character.Animals ParseAnimal(string s) {
            try { if (!string.IsNullOrEmpty(s)) return (Character.Animals)Enum.Parse(typeof(Character.Animals), s); }
            catch (Exception __pa) { SRApi.Guard.Log("角色/解析动物名", __pa); }
            return Character.Animals.NONE;
        }

        // ---------------- 注册 ----------------
        private static void SelfReg() {
            SRApi.LocSec(Sec, "角色", null);
            SRApi.Nav(Sec, 35);
            SRApi.RegisterPage(Sec, "auto", "自动选择", "Auto pick", 10, RenderPick);
            SRApi.RegisterPage(Sec, "outfit", "自动时装", "Auto outfit", 20, RenderOutfit);

            SRApi.LocKey(Sec, "Enabled", "自动选择角色", null);
            SRApi.LocDesc(Sec, "Enabled", "自动选择角色\n进入树屋/房间时自动挑一次动物\n已经有角色就不动，整局只处理一次", "Auto pick animal\npicks once when you enter the treehouse or a room\ndoes nothing while you already have an animal");
            SRApi.LocKey(Sec, "Mode", "选择方式", null);
            SRApi.LocDesc(Sec, "Mode", "选择方式\n指定 = 每次挑同一只\n随机 = 在已解锁的空闲动物里随机；智能 = 优先你用得最多的", "Pick mode\nSpecific = always the same animal\nRandom = any unlocked free one; Smart = your most used");
            SRApi.LocKey(Sec, "Animal", "指定动物", null);
            SRApi.LocDesc(Sec, "Animal", "指定动物\n「指定角色」方式下要挑的动物\n只列出存档里已解锁的动物", "Target animal\nthe animal used by the Specific mode\nonly unlocked animals are listed");
            SRApi.LocKey(Sec, "Dress Key", "原地换装快捷键", null);
            SRApi.LocDesc(Sec, "Dress Key", "原地换装快捷键\n按一下在角色当前位置打开游戏原生换装界面\n需要先选好角色；右键条目可改绑", "Dress-here hotkey\nopens the game's own dresser at your character\npick an animal first; right-click the row to rebind");
            SRApi.LocKey(Sec, "Outfit Enabled", "自动时装", null);
            SRApi.LocDesc(Sec, "Outfit Enabled", "自动时装\n每个角色第一次随机一套并记住，之后固定\n只使用已解锁的外观", "Auto outfit\neach animal rolls one look the first time and keeps it\nonly unlocked outfits are used");
            SRApi.LocKey(Sec, "Outfit Data", "外观记录", null);
            SRApi.LocDesc(Sec, "Outfit Data", "外观记录\n内部状态：每个角色记住的那一套外观\n由本功能自动写入，勿手动修改", "Outfit records\ninternal state: the look remembered per animal\nwritten automatically; do not edit");
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _enabled = plugin.Config.Bind(Sec, "Enabled", false, "进入树屋/房间时自动选一次角色。");
                _mode = plugin.Config.Bind(Sec, "Mode", "target", "选择方式：target 指定 / random 随机空闲 / smart 智能。");
                _animal = plugin.Config.Bind(Sec, "Animal", "CHICKEN", "指定方式下要挑的动物（枚举名）。");
                _outfitOn = plugin.Config.Bind(Sec, "Outfit Enabled", false, "自动时装：角色选定后随机穿一套外观。");
                _dressKey = plugin.Config.Bind(Sec, "Dress Key", KeyCode.None, "原地换装快捷键：在角色当前位置打开游戏原生换装界面（None = 不绑）。");
                SRApi.RegisterKey("角色-原地换装", _dressKey, "press");
                SRApi.RegisterTick("KEY_角色原地换装", TickDressKey);
                _outfitData = plugin.Config.Bind(Sec, "Outfit Data", "",
                    "内部状态：每个角色第一次随机到的那套外观（键=动物枚举名，值=四类下标）。勿手动修改。");
                SRApi.RegisterTick("角色-自动选择", Tick);
                SRApi.RegisterSceneHook(delegate { _triedThisRoom = false; _dressedFor = null; _nextTryAt = 0f; });
            } catch (Exception __ex) { SRApi.Guard.Log("角色/初始化", __ex); }
        }

        // ---------------- 页面 ----------------
        private static void RenderPick() {
            try {
                SRApi.Ctl.SectionTitle(SRApi.T("- 自动选择 -", "- Auto pick -"));
                SRApi.RenderSectionEntries(Sec, delegate (ConfigEntryBase e) { return e.Definition.Key == "Enabled"; });
                EnsureAnimalList();
                // 统一控件：核心的 RowCombo 用配置条目当稳定 ID 管理展开态，选中即写值并收起。
                // 注意传原始枚举值（以前传本地化显示名，与 ModeVals 永不相等 → 永远显示第一项）。
                if (_mode != null)
                    SRApi.Ctl.RowCombo(SRApi.T("选择方式", "Pick mode"),
                        SRApi.T("选择方式\n指定 = 每次挑同一只\n随机 = 已解锁空闲里随机；智能 = 优先你用得最多的",
                                "Pick mode\nSpecific = always the same animal\nRandom = any unlocked free one; Smart = your most used"),
                        _mode, ModeVals, new string[] { ModeLabel("smart"), ModeLabel("target"), ModeLabel("random") }, 160f, 96f);
                // 结构固定：非「指定」时置灰，不变更控件数量（否则 IMGUI 会报 LayoutGroup 不匹配）。
                bool target = (_mode == null || string.IsNullOrEmpty(_mode.Value) || _mode.Value == "target");
                bool oldEnabled = GUI.enabled;
                try {
                    GUI.enabled = oldEnabled && target;
                EnsureAnimalList();
                // 全部角色直接一次列出来（不用下拉、不滚动），按宽度自动换行；本房间没有的照样列出，
                // 只是在「自动挑选」时会被跳过（不会选中一个拿不到的角色）。
                if (_animal != null) RowAnimalGrid(SRApi.T("指定动物", "Target animal"), _animal);
                } finally { GUI.enabled = oldEnabled; }
            } catch (Exception __ex) { SRApi.Guard.Log("角色/渲染自动选择页", __ex); }
        }

        // ================= 指定动物：自绘下拉（全量列出 + 滚轮滚动 + 不可用标记） =================




        // 全量角色按钮网格：一次显示全部，按可用宽度换行；列宽=最长名字，列对齐。
        private static void RowAnimalGrid(string label, ConfigEntry<string> entry) {
            try {
                EnsureAnimalList();
                GUILayout.BeginHorizontal();
                try {
                    float lw = SRApi.Ctl.LabelColumnWidth(label);
                    GUILayout.Label(SRApi.Ctl.Content(label,
                        SRApi.T("自动选择的目标角色\n直接列出全部角色，按宽度换行\n只有「选择方式 = 指定角色」时生效",
                                "the animal auto-pick targets\nlists every animal in a wrapped grid\nonly used when pick mode is Specific")),
                        SRApi.Ctl.Label, GUILayout.Width(lw), GUILayout.Height(SRApi.Ctl.Sc(26)));
                    GUILayout.Space(SRApi.Ctl.Sc(6));
                    float bw = SRApi.Ctl.Sc(56f);
                    for (int i = 0; i < AnimalLabels.Count; i++) {
                        float w = SRApi.Ctl.Btn.CalcSize(new GUIContent(AnimalLabels[i])).x + SRApi.Ctl.Sc(18);
                        if (w > bw) bw = w;
                    }
                    float avail = Mathf.Max(bw, SRApi.Ctl.WinWidth - SRApi.Ctl.SidebarWidth() - lw - SRApi.Ctl.Sc(90));
                    int cols = Mathf.Clamp(Mathf.FloorToInt(avail / (bw + SRApi.Ctl.Sc(4))), 2, 12);
                    string cur = entry != null ? entry.Value : "";
                    for (int i = 0; i < Animals.Count; i++) {
                        if (GUILayout.Button(AnimalLabels[i], Animals[i] == cur ? SRApi.Ctl.SelItem : SRApi.Ctl.Btn,
                                GUILayout.Width(bw), GUILayout.Height(SRApi.Ctl.Sc(24)))) {
                            if (entry != null) entry.Value = Animals[i];
                        }
                        if ((i + 1) % cols == 0) {
                            GUILayout.FlexibleSpace();
                            GUILayout.EndHorizontal();
                            GUILayout.BeginHorizontal();
                            GUILayout.Space(lw + SRApi.Ctl.Sc(6));
                        }
                    }
                    GUILayout.FlexibleSpace();
                } finally { GUILayout.EndHorizontal(); }
            } catch (Exception __ex) { SRApi.Guard.Log("角色/角色网格", __ex); }
        }

        private static void RenderOutfit() {
            try {
                SRApi.Ctl.SectionTitle(SRApi.T("- 自动时装 -", "- Auto outfit -"));
                SRApi.RenderSectionEntries(Sec, delegate (ConfigEntryBase e) { return e.Definition.Key == "Outfit Enabled"; });
                GUILayout.Space(SRApi.Ctl.Sc(4f));
                GUILayout.BeginHorizontal();
                try {
                    if (SRApi.Ctl.Button(SRApi.T("随机整套", "Randomize all"),
                            SRApi.T("只重抽当前角色的一套外观并覆盖它的记录\n其它角色不受影响", "rerolls only the current animal's look and overwrites its record\nother animals are untouched"), 120f, 26f))
                        RandomizeCurrent();
                    GUILayout.Space(SRApi.Ctl.Sc(8));
                    if (SRApi.Ctl.Button(SRApi.T("清空时装", "Clear outfit"),
                            SRApi.T("关闭当前角色的全部外观并标记为「不要自动穿」\n想恢复随机请点「随机整套」", "turns off every outfit on the current animal and marks it as do-not-auto-dress\nuse Randomize all to roll again"), 110f, 26f))
                        ClearCurrentOutfit();
                    GUILayout.Space(SRApi.Ctl.Sc(8));
                    // 通用条目：键位框本身支持右键绑定/解绑；左键点按钮打开界面
                    if (_dressKey != null) {
                        SRApi.Ctl.RestoreLabel(SRApi.Ctl.Content(SRApi.T("原地换装", "Dress here"),
                            SRApi.T("原地换装\n在角色当前位置打开游戏原生换装界面\n需要先选好角色；右键键位框可改绑", "dress here\nopens the game's own dresser at your character\npick an animal first; right-click the key box to rebind")),
                            _dressKey, SRApi.Ctl.Sc(120), SRApi.Ctl.Sc(26));
                        SRApi.Ctl.RenderControl(_dressKey);
                        if (SRApi.Ctl.Button(SRApi.T("打开", "Open"), SRApi.T("立即打开换装界面（等同按上面的快捷键）", "open the dresser now (same as pressing the hotkey)"), 72f, 26f))
                            OpenNativeDress();
                    }
                    GUILayout.FlexibleSpace();
                } finally { GUILayout.EndHorizontal(); }
            } catch (Exception __ex) { SRApi.Guard.Log("角色/渲染自动时装页", __ex); }
        }



        // 指定动物：直接排成按钮网格，全部可见（下拉框装不下时会被裁掉）。


        // ---------------- 逻辑 ----------------
        // 原地换装快捷键：按键轮询放在输入类钩子里（预算降频不会漏帧）
        private static void TickDressKey() {
            try {
                if (!SRApi.GateMaster || _dressKey == null || _dressKey.Value == KeyCode.None) return;
                if (SRApi.Typing) return;
                if (SRApi.ComboKeyDown(_dressKey)) OpenNativeDress();
            } catch (Exception __ex) { SRApi.Guard.Log("角色/换装快捷键", __ex); }
        }

        private static void Tick() {
            try {
                bool pick = _enabled != null && _enabled.Value;
                bool outfit = _outfitOn != null && _outfitOn.Value;
                if ((!pick && !outfit) || !SRApi.GateMaster) return;
                if (Time.unscaledTime < _nextTryAt) return;
                _nextTryAt = Time.unscaledTime + 0.1f;

                LobbyManager lm = LobbyManager.instance;
                if (lm == null) return;
                LobbyPlayer lp = LocalLobbyPlayer(lm);
                if (lp == null) return;

                if (pick && !_triedThisRoom) TryPickOnce(lp, lm);
                if (outfit) TryRandomOutfit(lp);
                UnstickDresser();
            } catch (Exception __ex) { SRApi.Guard.Log("角色/轮询", __ex); }
        }

        private static LobbyPlayer LocalLobbyPlayer(LobbyManager lm) {
            try {
                NetworkLobbyPlayer[] slots = lm.lobbySlots;
                if (slots == null) return null;
                for (int i = 0; i < slots.Length; i++) {
                    LobbyPlayer p = slots[i] as LobbyPlayer;
                    if (p != null && p.isLocalPlayer) return p;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("角色/查找本地玩家", __ex); }
            return null;
        }

        // 一次房间只尝试一次：已经有角色就结束；没有就等到能挑的时机再挑一次。
        private static void TryPickOnce(LobbyPlayer lp, LobbyManager lm) {
            try {
                if (lp.CharacterInstance != null) { _triedThisRoom = true; return; }   // 已经有角色：不动
                LevelSelectController lsc = lm.CurrentLevelSelectController;
                if (lsc == null) return;                                               // 还不在树屋里，下一帧再看
                Character.Animals want = ChooseAnimal(lsc);
                if (want == Character.Animals.NONE) { _triedThisRoom = true; return; }
                Character target = FindFreeCharacter(lsc, want);
                if (target == null) return;                                            // 被占了：下一帧再试（同一房间内仍只算一次）
                if (lp.CursorInstance == null) { lp.CallCmdSwitchToCursor(); return; }  // 先切光标，下一帧再挑
                if (lp.RequestPickCharacter(target)) {
                    _triedThisRoom = true;
                    SRApi.LogInfo("[角色] 已请求挑选 " + want);
                }
            } catch (Exception __ex) { SRApi.Guard.Log("角色/自动挑选", __ex); }
        }

        private static Character.Animals ChooseAnimal(LevelSelectController lsc) {
            string mode = _mode != null ? _mode.Value : "target";
            if (mode == "smart") {
                Character.Animals smart = SmartAnimal(lsc);
                if (smart != Character.Animals.NONE) return smart;
                mode = "random";
            }
            if (mode == "random") {
                EnsureAnimalList();
                List<Character.Animals> free = new List<Character.Animals>();
                for (int i = 0; i < Animals.Count; i++) {
                    Character.Animals en = ParseAnimal(Animals[i]);
                    if (en == Character.Animals.NONE) continue;
                    bool taken = false;
                    try { taken = lsc.IsCharacterTaken(en); } catch (Exception __t) { SRApi.Guard.Log("角色/占用判定", __t); taken = true; }
                    if (!taken) free.Add(en);
                }
                if (free.Count == 0) return Character.Animals.NONE;
                return free[UnityEngine.Random.Range(0, free.Count)];
            }
            // 指定角色：本房间没有这只就不选（避免"点了没反应"却把光标抢过去）
            Character.Animals target = ParseAnimal(_animal != null ? _animal.Value : "");
            if (target != Character.Animals.NONE) {
                try {
                    if (lsc != null && lsc.IsCharacterTaken(target)) return Character.Animals.NONE;
                } catch (Exception __t2) { SRApi.Guard.Log("角色/指定占用判定", __t2); }
            }
            return target;
        }

        private static Character.Animals SmartAnimal(LevelSelectController lsc) {
            try {
                SaveFileData data = StatTracker.Instance.GetSaveFileDataForMainUser();
                if (data == null) return Character.Animals.NONE;
                StatCountArray wins = data.GetStat<StatCountArray>("CharacterWins");
                StatCountArray succ = data.GetStat<StatCountArray>("CharacterSuccess");
                if (wins == null && succ == null) return Character.Animals.NONE;
                EnsureAnimalList();
                Character.Animals best = Character.Animals.NONE;
                int bestScore = 0;
                for (int i = 0; i < Animals.Count; i++) {
                    Character.Animals a = ParseAnimal(Animals[i]);
                    if (a == Character.Animals.NONE) continue;
                    int idx = (int)a;
                    int score = 0;
                    try { if (wins != null && wins.values != null && idx < wins.values.Length) score += wins.values[idx] * 3; } catch (Exception __w) { SRApi.Guard.Log("角色/读胜场", __w); }
                    try { if (succ != null && succ.values != null && idx < succ.values.Length) score += succ.values[idx]; } catch (Exception __s) { SRApi.Guard.Log("角色/读通关", __s); }
                    if (score <= bestScore) continue;
                    bool taken = false;
                    try { taken = lsc.IsCharacterTaken(a); } catch (Exception __t) { SRApi.Guard.Log("角色/智能占用判定", __t); taken = true; }
                    if (taken) continue;
                    bestScore = score; best = a;
                }
                return best;
            } catch (Exception __ex) { SRApi.Guard.Log("角色/智能选择", __ex); return Character.Animals.NONE; }
        }

        private static Character FindFreeCharacter(LevelSelectController lsc, Character.Animals animal) {
            try {
                List<LobbyStartPoint> pts = lsc.StartingPoints;
                if (pts != null) {
                    for (int i = 0; i < pts.Count; i++) {
                        LobbyStartPoint sp = pts[i];
                        if (sp == null) continue;
                        Character c = sp.GetComponentInChildren<Character>(true);
                        if (c != null && !c.Picked && c.CharacterSprite == animal) return c;
                    }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("角色/起点查找", __ex); }
            return null;
        }

        // ---------------- 自动时装（随机整套） ----------------
        // 换装界面关闭后自救：游戏原生 Close 有一条路径是 Release(unFreeze:false)（Esc/Start/记分板），
        // 它不会把 InMenu 置回 false、也不会 Unfreeze —— 于是角色关掉界面后动不了。
        // 这里检测"界面已不可见但角色还卡在 InMenu"，把它还原。
        private static void UnstickDresser() {
            try {
                if (_dressCtrl == null || _dressChar == null) return;
                bool uiAlive = false;
                try { uiAlive = _dressCtrl != null && _dressCtrl.gameObject != null && _dressCtrl.gameObject.activeInHierarchy; }
                catch { uiAlive = false; }
                if (uiAlive) return;                       // 界面还开着：别动
                Character ch = _dressChar;
                _dressCtrl = null; _dressChar = null;
                if (ch == null) return;
                if (ch.InMenu) {
                    ch.InMenu = false;
                    try { ch.Unfreeze(); } catch (Exception __uf) { SRApi.Guard.Log("角色/换装后解冻", __uf); }
                    SRApi.LogInfo("[角色] 换装界面已关闭：已还原角色可移动状态");
                }
            } catch (Exception __ex) { SRApi.Guard.Log("角色/换装自救", __ex); }
        }

        private static void TryRandomOutfit(LobbyPlayer lp) {
            try {
                Character ch = lp.CharacterInstance;
                if (ch == null) { _dressedFor = null; _dressRetry = 0; return; }
                if (ReferenceEquals(ch, _dressedFor)) return;
                if (ApplyOutfitFor(ch, false)) {
                    lp.CallCmdSetOutfitsFromArray(ch.GetOutfitsAsArray());   // 同步给全场
                    _dressedFor = ch;
                    _dressRetry = 0;
                } else if (++_dressRetry > 1) {
                    // 重试 1 次仍拿不到外观组件：放弃这个角色，换角色/换场景会重置。
                    _dressedFor = ch;
                    _dressRetry = 0;
                    SRApi.LogWarn("[角色] 该角色的外观组件一直没就绪，已跳过自动时装");
                }
            } catch (Exception __ex) { SRApi.Guard.Log("角色/自动时装", __ex); }
        }

        // 原地换装：完全走游戏原生链路 —— 调场景里 OutfitChangeProp.Use()（它会置 InMenu、Freeze、
        // 记录 usedInputKey、实例化并 Show OutfitController，之后 Accept/Back 走原生 Release），
        // 唯一的额外处理是：Use 结束后把界面和角色位置都还原到角色原来的位置（原生会把人拉到衣柜前）。
        private static void OpenNativeDress() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm == null) return;
                LobbyPlayer lp = LocalLobbyPlayer(lm);
                if (lp == null || lp.CharacterInstance == null) { SRApi.Notify(SRApi.T("先选一个角色", "Pick an animal first")); return; }
                OutfitChangeProp prop = UnityEngine.Object.FindObjectOfType<OutfitChangeProp>();
                if (prop == null || prop.OutfitManager == null) { SRApi.Notify(SRApi.T("当前场景没有换装对象", "No dresser in this scene")); return; }
                // 原生 Show 依赖解锁表；表没建好会 KeyNotFound（原生 UI 打不开的常见原因）
                try {
                    if (prop.OutfitManager.characterOutfitsUnlocked == null || prop.OutfitManager.characterOutfitsUnlocked.Count == 0)
                        prop.OutfitManager.RebuildDatabase();
                } catch (Exception __rd) { SRApi.Guard.Log("角色/换装解锁表", __rd); }
                Character ch = lp.CharacterInstance;
                Vector3 keep = ch.transform.position;
                if (!prop.Use(lp, InputEvent.InputKey.Accept)) { SRApi.Notify(SRApi.T("游戏当前不允许换装", "The game does not allow dressing right now")); return; }
                OutfitController ctrl;
                if (OutfitChangeProp.OutfitControllers.TryGetValue(ch, out ctrl) && ctrl != null) {
                    ctrl.transform.position = keep;    // 界面留在原地
                    ch.transform.position = keep;      // 角色不被传送到衣柜前
                    _dressCtrl = ctrl;
                    _dressChar = ch;
                }
                SRApi.Notify(SRApi.T("已打开换装界面（Accept/Back 关闭）", "Dresser opened (Accept/Back closes)"));
            } catch (Exception __ex) { SRApi.Guard.Log("角色/原地换装", __ex); }
        }



        // 清空当前角色的时装：四类全部关闭 + 删掉它的记录（下次会重新随机）。
        private static void ClearCurrentOutfit() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm == null) return;
                LobbyPlayer lp = LocalLobbyPlayer(lm);
                if (lp == null || lp.CharacterInstance == null) { SRApi.Notify(SRApi.T("还没选角色", "Pick an animal first")); return; }
                Character ch = lp.CharacterInstance;
                Outfit[] all = ch.GetComponentsInChildren<Outfit>(true);
                if (all != null) {
                    for (int i = 0; i < all.Length; i++) {
                        Outfit o = all[i];
                        if (o != null && o.followThisOutfit == null) o.on = false;
                    }
                    try { ch.OnOutfitsUpdated(all); } catch (Exception __uo) { SRApi.Guard.Log("角色/清空时装刷新", __uo); }
                    lp.CallCmdSetOutfitsFromArray(ch.GetOutfitsAsArray());
                }
                // 标记为 none：清空后不要自动重穿（否则下一帧又会随机一套，看起来"清空没生效"）
                SetOutfitRecord(ch.CharacterSprite.ToString(), "none");
                _dressedFor = ch;
                SRApi.Notify(SRApi.T("已清空该角色的时装", "Cleared this animal's outfit"));
            } catch (Exception __ex) { SRApi.Guard.Log("角色/清空时装", __ex); }
        }

        // 按角色应用：有记录就用记录，没有就随机一套并写进记录（每个角色各一套）。
        private static bool ApplyOutfitFor(Character ch, bool reroll) {
            try {
                if (ch == null) return false;
                Outfit[] all = ch.GetComponentsInChildren<Outfit>(true);
                // 角色刚生成时外观组件可能还没挂好：返回失败，让调用方下一帧重试（快速换角色时的"概率无效"就是这个）
                if (all == null || all.Length == 0) return false;
                string animal = ch.CharacterSprite.ToString();
                string rec = reroll ? null : GetOutfitRecord(animal);
                // "none" = 用户点过「清空时装」：不要自动再穿，等他手动随机
                if (rec == "none") return true;
                if (string.IsNullOrEmpty(rec)) {
                    rec = MakeRandomRecord(all);
                    SetOutfitRecord(animal, rec);
                    SRApi.LogInfo("[角色] 已为 " + animal + " 记住一套外观");
                }
                ApplyRecord(all, rec);
                try { ch.OnOutfitsUpdated(all); } catch (Exception __uo) { SRApi.Guard.Log("角色/时装刷新", __uo); }
                return true;
            } catch (Exception __ex) { SRApi.Guard.Log("角色/应用外观", __ex); return false; }
        }

        // 记录格式：动物=配饰,身体,头部,皮肤|动物=...
        private static string GetOutfitRecord(string animal) {
            try {
                string raw = _outfitData != null ? (_outfitData.Value ?? "") : "";
                string[] parts = raw.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++) {
                    int eq = parts[i].IndexOf('=');
                    if (eq > 0 && parts[i].Substring(0, eq) == animal) return parts[i].Substring(eq + 1);
                }
            } catch (Exception __ex) { SRApi.Guard.Log("角色/读取外观记录", __ex); }
            return null;
        }

        private static void SetOutfitRecord(string animal, string rec) {
            try {
                if (_outfitData == null || string.IsNullOrEmpty(animal)) return;
                Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
                string raw = _outfitData.Value ?? "";
                string[] parts = raw.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++) {
                    int eq = parts[i].IndexOf('=');
                    if (eq > 0) map[parts[i].Substring(0, eq)] = parts[i].Substring(eq + 1);
                }
                map[animal] = rec;
                List<string> keep = new List<string>();
                foreach (KeyValuePair<string, string> kv in map) keep.Add(kv.Key + "=" + kv.Value);
                _outfitData.Value = string.Join("|", keep.ToArray());
            } catch (Exception __ex) { SRApi.Guard.Log("角色/写入外观记录", __ex); }
        }

        private static readonly Outfit.OutfitType[] Types = { Outfit.OutfitType.Accessory, Outfit.OutfitType.Body, Outfit.OutfitType.Head, Outfit.OutfitType.Skin };

        // 生成一套随机记录：每类取"候选列表下标"，没有候选写 -1。
        private static string MakeRandomRecord(Outfit[] all) {
            int[] pick = new int[Types.Length];
            for (int t = 0; t < Types.Length; t++) {
                List<Outfit> cand = Candidates(all, Types[t]);
                pick[t] = cand.Count > 0 ? UnityEngine.Random.Range(0, cand.Count) : -1;
            }
            return pick[0] + "," + pick[1] + "," + pick[2] + "," + pick[3];
        }

        // 按记录应用：-1 = 该类别不干预（保持游戏默认）。
        private static void ApplyRecord(Outfit[] all, string rec) {
            try {
                if (string.IsNullOrEmpty(rec)) return;
                string[] parts = rec.Split(',');
                for (int t = 0; t < Types.Length && t < parts.Length; t++) {
                    int idx;
                    if (!int.TryParse(parts[t], out idx) || idx < 0) continue;
                    List<Outfit> cand = Candidates(all, Types[t]);
                    if (cand.Count == 0) continue;
                    Outfit sel = cand[Mathf.Clamp(idx, 0, cand.Count - 1)];
                    for (int i = 0; i < all.Length; i++) {
                        Outfit o = all[i];
                        if (o != null && o.outfitType == Types[t] && o.followThisOutfit == null) o.on = ReferenceEquals(o, sel);
                    }
                }
            } catch (Exception __ex) { SRApi.Guard.Log("角色/应用外观记录", __ex); }
        }

        private static List<Outfit> Candidates(Outfit[] all, Outfit.OutfitType type) {
            List<Outfit> cand = new List<Outfit>();
            for (int i = 0; i < all.Length; i++) {
                Outfit o = all[i];
                if (o == null || o.outfitType != type || o.followThisOutfit != null) continue;
                if (!o.Unlocked && !o.TempUnlocked) continue;
                cand.Add(o);
            }
            cand.Sort(delegate (Outfit a, Outfit b) { return string.Compare(a.name, b.name, StringComparison.Ordinal); });
            return cand;
        }

        // 按钮：只重抽"当前角色"的那一套，并覆盖它的记录（其它角色不受影响）。
        private static void RandomizeCurrent() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm == null) return;
                LobbyPlayer lp = LocalLobbyPlayer(lm);
                if (lp == null || lp.CharacterInstance == null) { SRApi.Notify(SRApi.T("还没选角色", "Pick an animal first")); return; }
                Character ch = lp.CharacterInstance;
                ApplyOutfitFor(ch, true);                       // reroll：重新随机并写记录
                lp.CallCmdSetOutfitsFromArray(ch.GetOutfitsAsArray());
                _dressedFor = ch;
                SRApi.Notify(SRApi.T("已重抽该角色的外观", "Rerolled this animal's look"));
            } catch (Exception __ex) { SRApi.Guard.Log("角色/随机整套", __ex); }
        }

        private static void ApplyRandom(Outfit[] all, Outfit.OutfitType type) {
            List<Outfit> cand = new List<Outfit>();
            for (int i = 0; i < all.Length; i++) {
                Outfit o = all[i];
                if (o == null || o.outfitType != type || o.followThisOutfit != null) continue;
                if (!o.Unlocked && !o.TempUnlocked) continue;
                cand.Add(o);
            }
            if (cand.Count == 0) return;
            cand.Sort(delegate (Outfit a, Outfit b) { return string.Compare(a.name, b.name, StringComparison.Ordinal); });
            Outfit sel = cand[UnityEngine.Random.Range(0, cand.Count)];
            for (int i = 0; i < all.Length; i++) {
                Outfit o = all[i];
                if (o != null && o.outfitType == type && o.followThisOutfit == null) o.on = ReferenceEquals(o, sel);
            }
        }

        private static string BuildStatus() {
            try {
                LobbyManager lm = LobbyManager.instance;
                LobbyPlayer lp = lm != null ? LocalLobbyPlayer(lm) : null;
                string cur = lp != null && lp.CharacterInstance != null ? lp.CharacterInstance.CharacterSprite.ToString() : SRApi.T("未选角色", "no animal");
                string on = (_enabled != null && _enabled.Value) ? SRApi.T("自动选择开", "auto pick on") : SRApi.T("自动选择关", "auto pick off");
                return SRApi.T("当前：", "now: ") + cur + "   ·   " + on;
            } catch (Exception __ex) { SRApi.Guard.Log("角色/状态行", __ex); return ""; }
        }
    }
}
