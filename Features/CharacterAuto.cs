// 角色：进入树屋时自动选一次角色（指定 / 随机空闲 / 智能），以及自动时装（随机整套）。
// 精简版：不再有延迟、重试间隔、已选不覆盖、外观记录、AutoDress 相关内容。
using System;
using System.Collections.Generic;
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

        private static readonly string[] ModeVals = { "target", "random", "smart" };
        private static bool _comboOpenMode;

        // 每进一次房间只自动选一次：场景切换时复位。
        private static bool _triedThisRoom;
        private static Character _dressedFor;          // 已经随机过时装的角色实例
        private static float _nextTryAt;

        private static string ModeLabel(string v) {
            if (v == "random") return SRApi.T("随机空闲", "Random free");
            if (v == "smart") return SRApi.T("智能选择", "Smart pick");
            return SRApi.T("指定角色", "Specific animal");
        }

        // 可用动物：优先读存档的解锁表（与实际解锁一致）；读不到时退回枚举全集。
        private static readonly List<string> Animals = new List<string>();
        private static readonly List<string> AnimalLabels = new List<string>();
        private static void EnsureAnimalList() {
            if (Animals.Count > 0) return;
            bool[] unlocked = UnlockedAnimals();
            foreach (Character.Animals a in Enum.GetValues(typeof(Character.Animals))) {
                if (a == Character.Animals.NONE) continue;
                try {
                    if (unlocked != null && (int)a < unlocked.Length && !unlocked[(int)a]) continue;   // 只列已解锁
                } catch (Exception __u) { SRApi.Guard.Log("角色/解锁过滤", __u); }
                Animals.Add(a.ToString());
                AnimalLabels.Add(AnimalZh(a.ToString()));
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

        private static string AnimalZh(string en) {
            switch (en) {
                case "CHICKEN": return SRApi.T("鸡", "Chicken");
                case "HORSE": return SRApi.T("马", "Horse");
                case "SHEEP": return SRApi.T("羊", "Sheep");
                case "RACCOON": return SRApi.T("浣熊", "Raccoon");
                case "CHAMELEON": return SRApi.T("变色龙", "Chameleon");
                case "SQUIRREL": return SRApi.T("松鼠", "Squirrel");
                case "ROBOT": return SRApi.T("机器人", "Robot");
                case "ELEPHANT": return SRApi.T("大象", "Elephant");
                case "MONKEY": return SRApi.T("猴子", "Monkey");
                case "SNAKE": return SRApi.T("蛇", "Snake");
                case "HIPPO": return SRApi.T("河马", "Hippo");
                case "TURTLE": return SRApi.T("乌龟", "Turtle");
                case "PANDA": return SRApi.T("熊猫", "Panda");
                case "FOX": return SRApi.T("狐狸", "Fox");
                case "PLATYPUS": return SRApi.T("鸭嘴兽", "Platypus");
                default: return en;
            }
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
            SRApi.LocKey(Sec, "Outfit Enabled", "自动时装", null);
            SRApi.LocDesc(Sec, "Outfit Enabled", "自动时装\n角色选定后自动随机穿一套外观\n只使用已解锁的外观", "Auto outfit\napplies a random look once an animal is picked\nonly unlocked outfits are used");
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _enabled = plugin.Config.Bind(Sec, "Enabled", false, "进入树屋/房间时自动选一次角色。");
                _mode = plugin.Config.Bind(Sec, "Mode", "target", "选择方式：target 指定 / random 随机空闲 / smart 智能。");
                _animal = plugin.Config.Bind(Sec, "Animal", "CHICKEN", "指定方式下要挑的动物（枚举名）。");
                _outfitOn = plugin.Config.Bind(Sec, "Outfit Enabled", false, "自动时装：角色选定后随机穿一套外观。");
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
                // 传原始枚举值：以前传 ModeLabel(...)（本地化显示名），与 ModeVals 永远不相等，
                // 于是 cur 恒 0，下拉永远显示第一项（配置其实已改）。
                RowCombo(SRApi.T("选择方式", "Pick mode"), (_mode != null ? _mode.Value : "target"),
                    ModeVals, new string[] { ModeLabel("target"), ModeLabel("random"), ModeLabel("smart") }, ref _comboOpenMode, _mode);
                // 结构固定：非「指定」时置灰，不变更控件数量（否则 IMGUI 会报 LayoutGroup 不匹配）。
                bool target = (_mode == null || string.IsNullOrEmpty(_mode.Value) || _mode.Value == "target");
                bool oldEnabled = GUI.enabled;
                try {
                    GUI.enabled = oldEnabled && target;
                    RowAnimal(SRApi.T("指定动物", "Target animal"), _animal);
                } finally { GUI.enabled = oldEnabled; }
                SRApi.Ctl.LabelAuto(BuildStatus());
            } catch (Exception __ex) { SRApi.Guard.Log("角色/渲染自动选择页", __ex); }
        }

        private static void RenderOutfit() {
            try {
                SRApi.Ctl.SectionTitle(SRApi.T("- 自动时装 -", "- Auto outfit -"));
                SRApi.RenderSectionEntries(Sec, delegate (ConfigEntryBase e) { return e.Definition.Key == "Outfit Enabled"; });
                GUILayout.Space(SRApi.Ctl.Sc(4f));
                GUILayout.BeginHorizontal();
                try {
                    if (GUILayout.Button(SRApi.T("随机整套", "Randomize all"), SRApi.Ctl.Btn,
                            GUILayout.Width(SRApi.Ctl.Sc(120f)), GUILayout.Height(SRApi.Ctl.Sc(26f))))
                        RandomizeCurrent();
                    GUILayout.FlexibleSpace();
                } finally { GUILayout.EndHorizontal(); }
                SRApi.Ctl.LabelAuto(SRApi.T("角色选定后会自动随机一套；也可以手动点上面的按钮重摇。", "A random look is applied once an animal is picked; use the button to reroll."));
            } catch (Exception __ex) { SRApi.Guard.Log("角色/渲染自动时装页", __ex); }
        }

        private static void RowCombo(string label, string current, string[] vals, string[] labels, ref bool open, ConfigEntry<string> entry) {
            GUILayout.BeginHorizontal();
            try {
                GUILayout.Label(label, SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(96f)), GUILayout.Height(SRApi.Ctl.Sc(24f)));
                int cur = 0;
                for (int i = 0; i < vals.Length; i++) if (vals[i] == current) { cur = i; break; }
                int sel = SRApi.Ctl.ComboBox(entry, labels[cur], vals, labels, ref open, SRApi.Ctl.Sc(160f));
                if (sel >= 0 && sel < vals.Length && entry != null && entry.Value != vals[sel]) entry.Value = vals[sel];
                GUILayout.FlexibleSpace();
            } finally { GUILayout.EndHorizontal(); }
        }

        // 指定动物：直接排成按钮网格，全部可见（下拉框装不下时会被裁掉）。
        private static void RowAnimal(string label, ConfigEntry<string> entry) {
            EnsureAnimalList();
            GUILayout.BeginHorizontal();
            try {
                GUILayout.Label(label, SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(96f)), GUILayout.Height(SRApi.Ctl.Sc(24f)));
                string cur = entry != null ? entry.Value : "";
                for (int i = 0; i < Animals.Count; i++) {
                    bool on = Animals[i] == cur;
                    if (GUILayout.Button(AnimalLabels[i], on ? SRApi.Ctl.SelItem : SRApi.Ctl.Btn,
                            GUILayout.Width(SRApi.Ctl.Sc(78f)), GUILayout.Height(SRApi.Ctl.Sc(24f)))) {
                        if (entry != null) entry.Value = Animals[i];
                    }
                    if (i % 6 == 5) { GUILayout.FlexibleSpace(); GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); GUILayout.Space(SRApi.Ctl.Sc(96f)); }
                }
                GUILayout.FlexibleSpace();
            } finally { GUILayout.EndHorizontal(); }
        }

        // ---------------- 逻辑 ----------------
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
            return ParseAnimal(_animal != null ? _animal.Value : "");
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
        private static void TryRandomOutfit(LobbyPlayer lp) {
            try {
                Character ch = lp.CharacterInstance;
                if (ch == null || ReferenceEquals(ch, _dressedFor)) return;
                Outfit[] all = ch.GetComponentsInChildren<Outfit>(true);
                if (all == null || all.Length == 0) return;
                Outfit.OutfitType[] types = { Outfit.OutfitType.Accessory, Outfit.OutfitType.Body, Outfit.OutfitType.Head, Outfit.OutfitType.Skin };
                for (int t = 0; t < types.Length; t++) ApplyRandom(all, types[t]);
                try { ch.OnOutfitsUpdated(all); } catch (Exception __uo) { SRApi.Guard.Log("角色/时装刷新", __uo); }
                lp.CallCmdSetOutfitsFromArray(ch.GetOutfitsAsArray());   // 同步给全场
                _dressedFor = ch;
                SRApi.LogInfo("[角色] 已随机穿好外观");
            } catch (Exception __ex) { SRApi.Guard.Log("角色/随机时装", __ex); }
        }

        private static void RandomizeCurrent() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm == null) return;
                LobbyPlayer lp = LocalLobbyPlayer(lm);
                if (lp == null || lp.CharacterInstance == null) { SRApi.Notify(SRApi.T("还没选角色", "Pick an animal first")); return; }
                _dressedFor = null;
                TryRandomOutfit(lp);
                SRApi.Notify(SRApi.T("已随机整套外观", "Random look applied"));
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
