// 角色：进入房间后自动选择角色（动物）与自动搭配时装。
// 走游戏自己的挑选通道：LobbyPlayer.RequestPickCharacter（服务端校验动物是否被占）与
// LobbyPlayer.CallCmdSetOutfitsFromArray（把时装索引同步给全场）。
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;

namespace SR_UCH.Tweaks {
    [SrFeature("sr.character", "Character", "1.0")]
    public class CharacterAuto : ITweak {
        private const string Sec = "Character";
        private static IFeatureHost _mp;

        // ---------------- 配置 ----------------
        private static ConfigEntry<bool> _enabled;          // 自动选择总开关
        private static ConfigEntry<string> _mode;           // target / random / slot
        private static ConfigEntry<string> _animal;         // 指定动物
        private static ConfigEntry<string> _slot1, _slot2, _slot3, _slot4;   // 按槽位
        private static ConfigEntry<float> _delay;           // 进房后延迟
        private static ConfigEntry<float> _retry;           // 失败重试间隔
        private static ConfigEntry<bool> _onlyUnpicked;     // 已选角色则不覆盖
        private static ConfigEntry<bool> _outfitOn;         // 自动时装总开关
        private static ConfigEntry<string> _oAcc, _oBody, _oHead, _oSkin;
        private static ConfigEntry<bool> _oSavedUse;   // 优先使用"按角色保存的外观"
        private static ConfigEntry<string> _oSaved;    // 记录：动物=配饰,身体,头部,皮肤|...

        // ---------------- 运行态 ----------------
        private static float _nextAt;
        private static Character _outfitApplied;            // 已套用时装的角色（换角色/改配置后清空）
        private static bool _comboOpenMode;

        private static readonly string[] ModeVals = { "target", "random", "smart", "slot" };
        private static readonly string[] OutfitVals = { "", "random" };   // "" = 不干预，random = 随机

        private static string ModeLabel(string v) {
            if (v == "random") return SRApi.T("随机空闲", "Random free");
            if (v == "smart") return SRApi.T("智能选择", "Smart pick");
            if (v == "slot") return SRApi.T("按玩家槽位", "Per player slot");
            return SRApi.T("指定角色", "Specific animal");
        }
        private static string OutfitLabel(string v) {
            if (v == "random") return SRApi.T("随机", "Random");
            if (string.IsNullOrEmpty(v)) return SRApi.T("不干预", "Leave alone");
            return SRApi.T("第 ", "Slot ") + v;
        }

        // 动物名列表（去掉 NONE），值与显示同名（枚举名，落配置稳定）
        private static readonly List<string> Animals = new List<string>();
        private static readonly List<string> AnimalLabels = new List<string>();
        private static void EnsureAnimalList() {
            if (Animals.Count > 0) return;
            foreach (Character.Animals a in Enum.GetValues(typeof(Character.Animals))) {
                if (a == Character.Animals.NONE) continue;
                Animals.Add(a.ToString());
                string zh = AnimalZh(a.ToString());
                AnimalLabels.Add(string.IsNullOrEmpty(zh) ? a.ToString() : zh);
            }
        }

        // 动物中文名：游戏本地化键不稳定，这里内置一份常用表，未收录的显示枚举名（可读性足够）。
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
                case "BUNNY": return SRApi.T("兔子", "Bunny");
                case "PENGUIN": return SRApi.T("企鹅", "Penguin");
                case "CAT": return SRApi.T("猫", "Cat");
                case "DOG": return SRApi.T("狗", "Dog");
                case "BEE": return SRApi.T("蜜蜂", "Bee");
                case "GOAT": return SRApi.T("山羊", "Goat");
                case "OWL": return SRApi.T("猫头鹰", "Owl");
                default: return en;
            }
        }

        private static Character.Animals ParseAnimal(string s) {
            try {
                if (!string.IsNullOrEmpty(s)) return (Character.Animals)Enum.Parse(typeof(Character.Animals), s);
            } catch (Exception __pa) { SRApi.Guard.Log("角色/解析动物名", __pa); }
            return Character.Animals.NONE;
        }

        // ---------------- 注册 ----------------
        private static void SelfReg() {
            SRApi.LocSec(Sec, "角色", null);
            SRApi.Nav(Sec, 30);
            SRApi.RegisterPage(Sec, "auto", "自动选择", "Auto pick", 10, RenderPick);
            SRApi.RegisterPage(Sec, "outfit", "自动时装", "Auto outfit", 20, RenderOutfit);

            SRApi.LocKey(Sec, "Enabled", "自动选择角色", null);
            SRApi.LocDesc(Sec, "Enabled", "自动选择角色\n进入房间后自动挑选动物\n已选好角色时默认不覆盖", "Auto pick animal\nautomatically picks an animal on entering a room\ndoes not override a pick you already made");
            SRApi.LocKey(Sec, "Mode", "选择方式", null);
            SRApi.LocDesc(Sec, "Mode", "选择方式\n指定/随机/智能（按存档里最常用的角色）/按槽位\n智能会优先选你用得最多且当前空闲的动物", "Pick mode\nSpecific / Random / Smart (most used in your save) / Per slot\nSmart prefers your most-used animal that is still free");
            SRApi.LocKey(Sec, "Animal", "指定动物", null);
            SRApi.LocDesc(Sec, "Animal", "指定动物\n“指定角色”方式下要挑的动物\n被占用时会按重试间隔再试", "Target animal\nthe animal used by the Specific mode\nif it is taken the pick is retried");
            SRApi.LocKey(Sec, "Slot 1", "玩家 1", null);
            SRApi.LocDesc(Sec, "Slot 1", "玩家 1 的动物\n“按槽位”方式下第 1 位玩家用这只\n留空 = 该槽位不指定", "Animal for player 1\nused by the Per-slot mode for the first player\nempty = leave this slot alone");
            SRApi.LocKey(Sec, "Slot 2", "玩家 2", null);
            SRApi.LocDesc(Sec, "Slot 2", "玩家 2 的动物\n“按槽位”方式下第 2 位玩家用这只\n留空 = 该槽位不指定", "Animal for player 2\nused by the Per-slot mode for the second player\nempty = leave this slot alone");
            SRApi.LocKey(Sec, "Slot 3", "玩家 3", null);
            SRApi.LocDesc(Sec, "Slot 3", "玩家 3 的动物\n“按槽位”方式下第 3 位玩家用这只\n留空 = 该槽位不指定", "Animal for player 3\nused by the Per-slot mode for the third player\nempty = leave this slot alone");
            SRApi.LocKey(Sec, "Slot 4", "玩家 4", null);
            SRApi.LocDesc(Sec, "Slot 4", "玩家 4 的动物\n“按槽位”方式下第 4 位玩家用这只\n留空 = 该槽位不指定", "Animal for player 4\nused by the Per-slot mode for the fourth player\nempty = leave this slot alone");
            SRApi.LocKey(Sec, "Delay", "进入后延迟", null);
            SRApi.LocDesc(Sec, "Delay", "进入后延迟\n进房后等多少秒再挑选\n给游戏留出分配角色的时间", "Delay after entering\nhow long to wait before picking\nleaves the game time to assign players");
            SRApi.LocKey(Sec, "Retry", "重试间隔", null);
            SRApi.LocDesc(Sec, "Retry", "重试间隔\n没挑到（被占用/还没轮到你）多久再试\n范围 0.5-10 秒", "Retry interval\nhow long to wait before trying again when the animal is taken\nrange 0.5-10 seconds");
            SRApi.LocKey(Sec, "Only When Unpicked", "已选则不覆盖", null);
            SRApi.LocDesc(Sec, "Only When Unpicked", "已选则不覆盖\n开着：已经选好角色就不再自动挑\n关掉：按配置自动换角色（先走原生放弃，再挑目标）", "Only when unpicked\non: skip auto picking once you have an animal\noff: switches automatically (native unpick, then pick)");
            SRApi.LocKey(Sec, "Outfit Enabled", "自动时装", null);
            SRApi.LocDesc(Sec, "Outfit Enabled", "自动时装\n选定角色后自动穿上指定外观\n只使用已解锁的外观", "Auto outfit\napplies the chosen outfits once an animal is picked\nonly unlocked outfits are used");
            SRApi.LocKey(Sec, "Use Saved Outfits", "优先用保存记录", null);
            SRApi.LocDesc(Sec, "Use Saved Outfits", "优先用保存记录\n该角色有保存的外观时按记录套用\n没有记录才用下面四类规则", "Prefer saved looks\nuse the per-animal record when it exists\notherwise fall back to the four rules below");
            SRApi.LocKey(Sec, "Accessory", "配件", null);
            SRApi.LocDesc(Sec, "Accessory", "配件外观\n不干预=保持游戏当前；随机=已解锁里随机\n第 N 个=按列表序号指定", "Accessory outfit\nLeave alone = keep the game state; Random = pick from unlocked\nSlot N = a fixed entry from the list");
            SRApi.LocKey(Sec, "Body", "身体", null);
            SRApi.LocDesc(Sec, "Body", "身体外观\n不干预=保持游戏当前；随机=已解锁里随机\n第 N 个=按列表序号指定", "Body outfit\nLeave alone = keep the game state; Random = pick from unlocked\nSlot N = a fixed entry from the list");
            SRApi.LocKey(Sec, "Head", "头部", null);
            SRApi.LocDesc(Sec, "Head", "头部外观\n不干预=保持游戏当前；随机=已解锁里随机\n第 N 个=按列表序号指定", "Head outfit\nLeave alone = keep the game state; Random = pick from unlocked\nSlot N = a fixed entry from the list");
            SRApi.LocKey(Sec, "Skin", "皮肤", null);
            SRApi.LocDesc(Sec, "Skin", "皮肤外观\n不干预=保持游戏当前；随机=已解锁里随机\n第 N 个=按列表序号指定", "Skin outfit\nLeave alone = keep the game state; Random = pick from unlocked\nSlot N = a fixed entry from the list");
        }

        public void Initialize(IFeatureHost plugin) {
            try {
                SelfReg();
                _mp = plugin;
                _enabled = plugin.Config.Bind(Sec, "Enabled", false, "自动选择角色总开关");
                _mode = plugin.Config.Bind(Sec, "Mode", "target", "选择方式：target 指定 / random 随机空闲 / slot 按槽位");
                _animal = plugin.Config.Bind(Sec, "Animal", "CHICKEN", "指定方式下要挑的动物（枚举名）");
                _slot1 = plugin.Config.Bind(Sec, "Slot 1", "", "玩家 1 的动物（空 = 不指定）");
                _slot2 = plugin.Config.Bind(Sec, "Slot 2", "", "玩家 2 的动物（空 = 不指定）");
                _slot3 = plugin.Config.Bind(Sec, "Slot 3", "", "玩家 3 的动物（空 = 不指定）");
                _slot4 = plugin.Config.Bind(Sec, "Slot 4", "", "玩家 4 的动物（空 = 不指定）");
                _delay = plugin.Config.Bind(Sec, "Delay", 1.5f, new ConfigDescription("进入后延迟（0-10 秒）", new AcceptableValueRange<float>(0f, 10f)));
                _retry = plugin.Config.Bind(Sec, "Retry", 1.5f, new ConfigDescription("重试间隔（0.5-10 秒）", new AcceptableValueRange<float>(0.5f, 10f)));
                _onlyUnpicked = plugin.Config.Bind(Sec, "Only When Unpicked", true,
                    "已选则不覆盖：开着时已有角色就不再自动挑；关掉时允许自动换角色（先放弃再挑）。");
                _outfitOn = plugin.Config.Bind(Sec, "Outfit Enabled", false, "自动时装总开关");
                _oAcc = plugin.Config.Bind(Sec, "Accessory", "", "配件：空=不干预，random=随机，数字=序号");
                _oBody = plugin.Config.Bind(Sec, "Body", "", "身体：空=不干预，random=随机，数字=序号");
                _oHead = plugin.Config.Bind(Sec, "Head", "", "头部：空=不干预，random=随机，数字=序号");
                _oSkin = plugin.Config.Bind(Sec, "Skin", "", "皮肤：空=不干预，random=随机，数字=序号");
                _oSavedUse = plugin.Config.Bind(Sec, "Use Saved Outfits", true, "优先使用按角色保存的外观记录");
                _oSaved = plugin.Config.Bind(Sec, "Saved Outfits", "", "内部状态：按角色保存的外观记录（勿手动修改）");
                if (_oSavedUse != null) _oSavedUse.SettingChanged += delegate { _outfitApplied = null; };
                if (_oSaved != null) _oSaved.SettingChanged += delegate { _outfitApplied = null; };
                // 外观配置变了要重新套用（_outfitApplied 是"已套用"哨兵）
                ConfigEntry<string>[] outfitEntries = { _oAcc, _oBody, _oHead, _oSkin };
                for (int i = 0; i < outfitEntries.Length; i++) {
                    if (outfitEntries[i] != null) outfitEntries[i].SettingChanged += delegate { _outfitApplied = null; };
                }
                if (_outfitOn != null) _outfitOn.SettingChanged += delegate { _outfitApplied = null; };

                SRApi.RegisterTick("角色-自动选择", Tick);
                SRApi.RegisterSceneHook(delegate { _outfitApplied = null; _nextAt = 0f; _pickedAt = -1f; });   // 换场景重新来一遍
            } catch (Exception __ex) { SRApi.Guard.Log("角色/初始化", __ex); }
        }

        // ---------------- 页面 ----------------
        private static void RenderPick() {
            try {
                SRApi.Ctl.SectionTitle(SRApi.T("- 自动选择 -", "- Auto pick -"));
                SRApi.RenderSectionEntries(Sec, delegate (ConfigEntryBase e) {
                    string k = e.Definition.Key;
                    if (k == "Mode" || k == "Animal" || k == "Slot 1" || k == "Slot 2" || k == "Slot 3" || k == "Slot 4") return false;
                    // 时装页的条目不要出现在选择页
                    return k != "Outfit Enabled" && k != "Accessory" && k != "Body" && k != "Head" && k != "Skin";
                });
                EnsureAnimalList();
                // 选择方式
                RowCombo(SRApi.T("选择方式", "Pick mode"), ModeLabel(_mode != null ? _mode.Value : "target"),
                    ModeVals,
                    new string[] { ModeLabel("target"), ModeLabel("random"), ModeLabel("smart"), ModeLabel("slot") },
                    ref _comboOpenMode, _mode);
                if ((_mode != null ? _mode.Value : "target") == "target") {
                    RowAnimal(SRApi.T("指定动物", "Target animal"), _animal);
                } else if ((_mode != null ? _mode.Value : "") == "slot") {
                    RowAnimal(SRApi.T("玩家 1", "Player 1"), _slot1);
                    RowAnimal(SRApi.T("玩家 2", "Player 2"), _slot2);
                    RowAnimal(SRApi.T("玩家 3", "Player 3"), _slot3);
                    RowAnimal(SRApi.T("玩家 4", "Player 4"), _slot4);
                }
                // 立即执行 + 手动放弃：方便验证当前配置，也能在角色被占时手动重来。
                GUILayout.Space(SRApi.Ctl.Sc(4f));
                GUILayout.BeginHorizontal();
                try {
                    if (GUILayout.Button(SRApi.T("立即挑选", "Pick now"), SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Sc(110f)), GUILayout.Height(SRApi.Ctl.Sc(26f))))
                        RunPickNow();
                    if (GUILayout.Button(SRApi.T("放弃当前角色", "Unpick"), SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Sc(140f)), GUILayout.Height(SRApi.Ctl.Sc(26f))))
                        UnpickNow();
                    GUILayout.FlexibleSpace();
                } finally { GUILayout.EndHorizontal(); }
                SRApi.Ctl.LabelAuto(BuildStatus());
            } catch (Exception __ex) { SRApi.Guard.Log("角色/渲染自动选择页", __ex); }
        }

        private static void RenderOutfit() {
            try {
                SRApi.Ctl.SectionTitle(SRApi.T("- 自动时装 -", "- Auto outfit -"));
                SRApi.RenderSectionEntries(Sec, delegate (ConfigEntryBase e) { return e.Definition.Key == "Outfit Enabled"; });
                RowOutfit(SRApi.T("配件", "Accessory"), _oAcc, 0);
                RowOutfit(SRApi.T("身体", "Body"), _oBody, 1);
                RowOutfit(SRApi.T("头部", "Head"), _oHead, 2);
                RowOutfit(SRApi.T("皮肤", "Skin"), _oSkin, 3);
                // 记录区：按角色保存一套外观，之后自动套用（可随时清除）
                GUILayout.Space(SRApi.Ctl.Sc(4f));
                SRApi.Ctl.SectionTitle(SRApi.T("- 外观记录 -", "- Saved looks -"));
                GUILayout.BeginHorizontal();
                try {
                    if (GUILayout.Button(SRApi.T("保存当前外观", "Save current look"), SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Sc(140f)), GUILayout.Height(SRApi.Ctl.Sc(26f))))
                        SaveCurrentLook();
                    if (GUILayout.Button(SRApi.T("立即套用", "Apply now"), SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Sc(110f)), GUILayout.Height(SRApi.Ctl.Sc(26f))))
                        _outfitApplied = null;
                    if (GUILayout.Button(SRApi.T("清除当前角色记录", "Clear this animal"), SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Sc(160f)), GUILayout.Height(SRApi.Ctl.Sc(26f))))
                        ClearCurrentLook();
                    if (GUILayout.Button(SRApi.T("随机整套", "Randomize all"), SRApi.Ctl.Btn, GUILayout.Width(SRApi.Ctl.Sc(120f)), GUILayout.Height(SRApi.Ctl.Sc(26f))))
                        RandomizeAll();
                    GUILayout.FlexibleSpace();
                } finally { GUILayout.EndHorizontal(); }
                string curAnimal = CurrentAnimalName();
                string rec = curAnimal != null ? GetSavedLook(curAnimal) : null;
                SRApi.Ctl.LabelAuto(rec != null
                    ? SRApi.T("当前角色记录：", "saved for this animal: ") + rec
                    : SRApi.T(string.IsNullOrEmpty(curAnimal) ? "（尚未选定角色）" : "（该角色还没有保存外观）", string.IsNullOrEmpty(curAnimal) ? "(no animal picked yet)" : "(no saved look for this animal)"));
                SRApi.Ctl.LabelAuto(SRApi.T("提示：只列已解锁外观；“第 N 个”按游戏内的外观顺序。", "Only unlocked outfits are listed; Slot N follows the in-game order."));
            } catch (Exception __ex) { SRApi.Guard.Log("角色/渲染自动时装页", __ex); }
        }

        private static void RowCombo(string label, string current, string[] vals, string[] labels, ref bool open, ConfigEntry<string> entry) {
            GUILayout.BeginHorizontal();
            try {
                GUILayout.Label(label, SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(96f)), GUILayout.Height(SRApi.Ctl.Sc(24f)));
                int cur = 0;
                for (int i = 0; i < vals.Length; i++) if (vals[i] == current) { cur = i; break; }
                int sel = SRApi.Ctl.ComboBox(null, labels[cur], vals, labels, ref open, SRApi.Ctl.Sc(160f));
                if (sel >= 0 && sel < vals.Length && entry != null && entry.Value != vals[sel]) entry.Value = vals[sel];
                GUILayout.FlexibleSpace();
            } finally { GUILayout.EndHorizontal(); }
        }

        private static bool _comboOpenAnimalRow;
        private static void RowAnimal(string label, ConfigEntry<string> entry) {
            EnsureAnimalList();
            string cur = entry != null ? entry.Value : "";
            RowCombo(label, cur, Animals.ToArray(), AnimalLabels.ToArray(), ref _comboOpenAnimalRow, entry);
        }

        private static void RowOutfit(string label, ConfigEntry<string> entry, int idx) {
            GUILayout.BeginHorizontal();
            try {
                GUILayout.Label(label, SRApi.Ctl.Label, GUILayout.Width(SRApi.Ctl.Sc(96f)), GUILayout.Height(SRApi.Ctl.Sc(24f)));
                string cur = entry != null ? entry.Value : "";
                // 外观条目是动态的（随角色/解锁变化），用文本更"高度自定义"：空 / random / 数字
                string typed = SRApi.ClearableTextField(cur ?? "", 12, SRApi.Ctl.SearchBox,
                    GUILayout.Width(SRApi.Ctl.Sc(120f)), GUILayout.Height(SRApi.Ctl.Sc(24f)));
                if (typed != null && entry != null && typed != cur) entry.Value = typed;
                GUILayout.Label(SRApi.T("空=不干预  random=随机  数字=序号", "empty = leave alone  random = random  number = index"),
                    SRApi.Ctl.Label);
                GUILayout.FlexibleSpace();
            } finally { GUILayout.EndHorizontal(); }
        }

        // 立即挑选：跳过延迟立刻跑一次（失败会把原因写进状态行）。
        private static void RunPickNow() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm == null) { SRApi.Notify(SRApi.T("现在不在房间里", "Not in a room")); return; }
                LevelSelectController lsc = lm.CurrentLevelSelectController;
                LobbyPlayer lp = LocalLobbyPlayer(lm);
                if (lsc == null || lp == null) { SRApi.Notify(SRApi.T("找不到本地玩家或房间", "No local player or lobby")); return; }
                _pickedAt = Time.unscaledTime - 99f;   // 绕过延迟
                TryPick(lp, lsc);
                SRApi.Notify(SRApi.T("已请求挑选", "Pick requested"));
            } catch (Exception __ex) { SRApi.Guard.Log("角色/立即挑选", __ex); }
        }

        private static void UnpickNow() {
            try {
                LobbyManager lm = LobbyManager.instance; if (lm == null) return;
                LobbyPlayer lp = LocalLobbyPlayer(lm); if (lp == null) return;
                if (lp.CharacterInstance == null) { SRApi.Notify(SRApi.T("当前没有角色", "No animal picked")); return; }
                if (lp.CursorInstance != null) { lp.UnpickCharacter(); SRApi.Notify(SRApi.T("已放弃当前角色", "Animal unpicked")); }
                else lp.CallCmdSwitchToCursor();
            } catch (Exception __ex) { SRApi.Guard.Log("角色/放弃当前角色", __ex); }
        }

        // 随机整套：四类外观全部随机（不受保存记录影响），用于"今天想换一身"。
        private static void RandomizeAll() {
            try {
                LobbyManager lm = LobbyManager.instance; if (lm == null) return;
                LobbyPlayer lp = LocalLobbyPlayer(lm); if (lp == null) return;
                Character ch = lp.CharacterInstance;
                if (ch == null) { SRApi.Notify(SRApi.T("还没选角色", "Pick an animal first")); return; }
                Outfit[] all = ch.GetComponentsInChildren<Outfit>(true);
                Outfit.OutfitType[] types = { Outfit.OutfitType.Accessory, Outfit.OutfitType.Body, Outfit.OutfitType.Head, Outfit.OutfitType.Skin };
                for (int t = 0; t < types.Length; t++) ApplyRandom(all, types[t]);
                try { ch.OnOutfitsUpdated(all); } catch (Exception __uo) { SRApi.Guard.Log("角色/随机整套刷新", __uo); }
                lp.CallCmdSetOutfitsFromArray(ch.GetOutfitsAsArray());
                _outfitApplied = ch;
                SRApi.Notify(SRApi.T("已随机整套外观", "Random look applied"));
            } catch (Exception __ex) { SRApi.Guard.Log("角色/随机整套", __ex); }
        }

        private static void ApplyRandom(Outfit[] all, Outfit.OutfitType type) {
            List<Outfit> cand = Candidates(all, type);
            if (cand.Count == 0) return;
            Outfit sel = cand[UnityEngine.Random.Range(0, cand.Count)];
            for (int i = 0; i < all.Length; i++) {
                Outfit o = all[i];
                if (o != null && o.outfitType == type && o.followThisOutfit == null) o.on = ReferenceEquals(o, sel);
            }
        }

        // 状态行：当前角色 / 是否启用 / 已保存记录数量，一眼能看出配置是否生效。
        private static string BuildStatus() {
            try {
                string animal = CurrentAnimalName();
                int looks = ParseSavedLooks().Count;
                string pick = (_enabled != null && _enabled.Value) ? SRApi.T("自动选择开", "auto pick on") : SRApi.T("自动选择关", "auto pick off");
                string outfit = (_outfitOn != null && _outfitOn.Value) ? SRApi.T("自动时装开", "auto outfit on") : SRApi.T("自动时装关", "auto outfit off");
                string cur = string.IsNullOrEmpty(animal) ? SRApi.T("未选角色", "no animal") : animal;
                return SRApi.T("当前：", "now: ") + cur + "   ·   " + pick + " / " + outfit + "   ·   " + SRApi.T("记录 ", "looks ") + looks;
            } catch (Exception __ex) { SRApi.Guard.Log("角色/状态行", __ex); return ""; }
        }

        // ---------------- 外观记录（按角色） ----------------
        // 格式："动物=配饰,身体,头部,皮肤|动物=..."，数值是候选列表下标，-1 表示不干预，-2 表示随机。
        private static Dictionary<string, string> ParseSavedLooks() {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
            try {
                string raw = _oSaved != null ? (_oSaved.Value ?? "") : "";
                string[] parts = raw.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++) {
                    int eq = parts[i].IndexOf('=');
                    if (eq <= 0) continue;
                    map[parts[i].Substring(0, eq)] = parts[i].Substring(eq + 1);
                }
            } catch (Exception __ex) { SRApi.Guard.Log("角色/解析外观记录", __ex); }
            return map;
        }

        private static string GetSavedLook(string animal) {
            string v;
            return ParseSavedLooks().TryGetValue(animal, out v) ? v : null;
        }

        private static void SetSavedLook(string animal, string packed) {
            try {
                if (_oSaved == null || string.IsNullOrEmpty(animal)) return;
                Dictionary<string, string> map = ParseSavedLooks();
                if (string.IsNullOrEmpty(packed)) map.Remove(animal); else map[animal] = packed;
                List<string> parts = new List<string>();
                foreach (KeyValuePair<string, string> kv in map) parts.Add(kv.Key + "=" + kv.Value);
                _oSaved.Value = string.Join("|", parts.ToArray());
                _outfitApplied = null;
                SRApi.LogInfo("[角色] 外观记录已更新：" + animal);
            } catch (Exception __ex) { SRApi.Guard.Log("角色/写入外观记录", __ex); }
        }

        private static string CurrentAnimalName() {
            try {
                LobbyManager lm = LobbyManager.instance; if (lm == null) return null;
                LobbyPlayer lp = LocalLobbyPlayer(lm); if (lp == null) return null;
                Character ch = lp.CharacterInstance; if (ch == null) return null;
                return ch.CharacterSprite.ToString();
            } catch (Exception __ex) { SRApi.Guard.Log("角色/读取当前动物", __ex); return null; }
        }

        // 把当前角色穿的外观按"候选列表下标"记录下来（-1 = 不干预）。
        private static void SaveCurrentLook() {
            try {
                LobbyManager lm = LobbyManager.instance; if (lm == null) return;
                LobbyPlayer lp = LocalLobbyPlayer(lm); if (lp == null) return;
                Character ch = lp.CharacterInstance; if (ch == null) { SRApi.Notify(SRApi.T("还没选角色，无法保存外观", "Pick an animal first")); return; }
                Outfit[] all = ch.GetComponentsInChildren<Outfit>(true);
                int[] idx = new int[4];
                Outfit.OutfitType[] types = { Outfit.OutfitType.Accessory, Outfit.OutfitType.Body, Outfit.OutfitType.Head, Outfit.OutfitType.Skin };
                for (int t = 0; t < types.Length; t++) {
                    List<Outfit> cand = Candidates(all, types[t]);
                    idx[t] = -1;
                    for (int i = 0; i < cand.Count; i++) { if (cand[i] != null && cand[i].on) { idx[t] = i; break; } }
                }
                SetSavedLook(ch.CharacterSprite.ToString(), idx[0] + "," + idx[1] + "," + idx[2] + "," + idx[3]);
                SRApi.Notify(SRApi.T("已保存当前外观", "Current look saved"));
            } catch (Exception __ex) { SRApi.Guard.Log("角色/保存当前外观", __ex); }
        }

        private static void ClearCurrentLook() {
            try {
                string a = CurrentAnimalName();
                if (string.IsNullOrEmpty(a)) return;
                SetSavedLook(a, null);
                SRApi.Notify(SRApi.T("已清除该角色的外观记录", "Saved look cleared"));
            } catch (Exception __ex) { SRApi.Guard.Log("角色/清除外观记录", __ex); }
        }

        private static List<Outfit> Candidates(Outfit[] all, Outfit.OutfitType type) {
            List<Outfit> cand = new List<Outfit>();
            if (all == null) return cand;
            for (int i = 0; i < all.Length; i++) {
                Outfit o = all[i];
                if (o == null || o.outfitType != type || o.followThisOutfit != null) continue;
                if (!o.Unlocked && !o.TempUnlocked) continue;
                cand.Add(o);
            }
            cand.Sort(delegate (Outfit a, Outfit b) { return string.Compare(a.name, b.name, StringComparison.Ordinal); });
            return cand;
        }

        // ---------------- 逻辑 ----------------
        private static void Tick() {
            try {
                bool pick = _enabled != null && _enabled.Value;
                bool outfit = _outfitOn != null && _outfitOn.Value;
                if (!pick && !outfit) return;
                if (!SRApi.GateMaster) return;
                if (Time.unscaledTime < _nextAt) return;
                float retry = _retry != null ? Mathf.Clamp(_retry.Value, 0.5f, 10f) : 1.5f;
                _nextAt = Time.unscaledTime + retry;

                LobbyManager lm = LobbyManager.instance;
                if (lm == null) return;
                LevelSelectController lsc = lm.CurrentLevelSelectController;
                if (lsc == null) return;
                LobbyPlayer lp = LocalLobbyPlayer(lm);
                if (lp == null) return;

                if (pick) TryPick(lp, lsc);
                if (outfit) TryOutfits(lp);
            } catch (Exception __ex) { SRApi.Guard.Log("角色/自动选择", __ex); }
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

        private static void TryPick(LobbyPlayer lp, LevelSelectController lsc) {
            try {
                // 已经选好角色：默认不覆盖；关掉「已选则不覆盖」时允许自动换角色——
                // 走游戏原生的 UnpickCharacter（放弃角色回到光标），下一次轮询再挑，不会硬改同步状态。
                if (lp.CharacterInstance != null) {
                    if (_onlyUnpicked == null || _onlyUnpicked.Value) return;
                    Character.Animals want = ChooseAnimal(lsc, lp);
                    if (want == Character.Animals.NONE || want == lp.CharacterInstance.CharacterSprite) return;
                    if (Time.unscaledTime - _unpickAt < 1f) return;   // 别每轮都发放弃请求
                    _unpickAt = Time.unscaledTime;
                    try {
                        if (lp.CursorInstance != null) { lp.UnpickCharacter(); SRApi.LogInfo("[角色] 已放弃当前角色，准备换 " + want); }
                        else lp.CallCmdSwitchToCursor();
                    } catch (Exception __up) { SRApi.Guard.Log("角色/放弃当前角色", __up); }
                    return;
                }
                float delay = _delay != null ? Mathf.Clamp(_delay.Value, 0f, 10f) : 1.5f;
                if (_pickedAt <= 0f) { _pickedAt = Time.unscaledTime; return; }             // 进房第一帧只记时间
                if (Time.unscaledTime - _pickedAt < delay) return;

                Character.Animals animal = ChooseAnimal(lsc, lp);
                if (animal == Character.Animals.NONE) return;
                Character target = FindFreeCharacter(lsc, animal);
                if (target == null) return;                                                 // 被占用：下个重试周期再来
                // 游戏在"确认挑选"的 Rpc 里会把 CursorInstance 当 LobbyCursor 用；
                // 没有光标时先切到光标状态，下一次 tick 再挑。
                if (lp.CursorInstance == null) { lp.CallCmdSwitchToCursor(); return; }
                if (lp.RequestPickCharacter(target)) SRApi.LogInfo("[角色] 已请求挑选 " + animal);
            } catch (Exception __ex) { SRApi.Guard.Log("角色/自动挑选", __ex); }
        }

        private static float _pickedAt = -1f;
        private static float _unpickAt = -999f;

        private static Character.Animals ChooseAnimal(LevelSelectController lsc, LobbyPlayer lp) {
            string mode = _mode != null ? _mode.Value : "target";
            if (mode == "slot") {
                int slot = Mathf.Clamp(lp.localNumber, 1, 4);
                ConfigEntry<string> e = slot == 1 ? _slot1 : slot == 2 ? _slot2 : slot == 3 ? _slot3 : _slot4;
                return ParseAnimal(e != null ? e.Value : "");
            }
            if (mode == "smart") {
                Character.Animals best = SmartAnimal(lsc);
                if (best != Character.Animals.NONE) return best;
                // 智能拿不到（没有存档统计）时退化成随机空闲
                mode = "random";
            }
            if (mode == "random") {
                List<Character.Animals> free = new List<Character.Animals>();
                foreach (string a in Animals) {
                    Character.Animals en = ParseAnimal(a);
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

        // 智能选择：存档里的 CharacterWins / CharacterSuccess 按动物索引累计，
        // 取"用得最多且当前空闲"的那只；统计为空时返回 NONE（调用方退化为随机）。
        private static Character.Animals SmartAnimal(LevelSelectController lsc) {
            try {
                SaveFileData data = StatTracker.Instance.GetSaveFileDataForMainUser();
                if (data == null) return Character.Animals.NONE;
                StatCountArray wins = data.GetStat<StatCountArray>("CharacterWins");
                StatCountArray succ = data.GetStat<StatCountArray>("CharacterSuccess");
                if (wins == null && succ == null) return Character.Animals.NONE;
                Character.Animals best = Character.Animals.NONE;
                int bestScore = 0;
                foreach (string name in Animals) {
                    Character.Animals a = ParseAnimal(name);
                    if (a == Character.Animals.NONE) continue;
                    int idx = (int)a;
                    int score = 0;
                    try { if (wins != null && wins.values != null && idx < wins.values.Length) score += wins.values[idx] * 3; } catch (Exception __w) { SRApi.Guard.Log("角色/读胜场统计", __w); }
                    try { if (succ != null && succ.values != null && idx < succ.values.Length) score += succ.values[idx]; } catch (Exception __s2) { SRApi.Guard.Log("角色/读通关统计", __s2); }
                    if (score <= bestScore) continue;
                    bool taken = false;
                    try { taken = lsc.IsCharacterTaken(a); } catch (Exception __t) { SRApi.Guard.Log("角色/占用判定", __t); taken = true; }
                    if (taken) continue;
                    bestScore = score; best = a;
                }
                if (best != Character.Animals.NONE) SRApi.LogInfo("[角色] 智能选择：" + best + "（分数 " + bestScore + "）");
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
            try {
                Character[] all = UnityEngine.Object.FindObjectsOfType<Character>();
                for (int i = 0; i < all.Length; i++) {
                    Character c = all[i];
                    if (c != null && !c.Picked && c.CharacterSprite == animal) return c;
                }
            } catch (Exception __ex) { SRApi.Guard.Log("角色/全场景查找", __ex); }
            return null;
        }

        private static void TryOutfits(LobbyPlayer lp) {
            try {
                Character ch = lp.CharacterInstance;
                if (ch == null) return;
                if (ReferenceEquals(ch, _outfitApplied)) return;      // 同一个角色只套一次
                Outfit[] all = ch.GetComponentsInChildren<Outfit>(true);
                if (all == null || all.Length == 0) return;

                ApplyType(ch, all, Outfit.OutfitType.Accessory, _oAcc);
                ApplyType(ch, all, Outfit.OutfitType.Body, _oBody);
                ApplyType(ch, all, Outfit.OutfitType.Head, _oHead);
                ApplyType(ch, all, Outfit.OutfitType.Skin, _oSkin);
                try { ch.OnOutfitsUpdated(all); } catch (Exception __uo) { SRApi.Guard.Log("角色/刷新外观", __uo); }
                lp.CallCmdSetOutfitsFromArray(ch.GetOutfitsAsArray());   // 同步给全场
                _outfitApplied = ch;
                SRApi.LogInfo("[角色] 已套用自动时装");
            } catch (Exception __ex) { SRApi.Guard.Log("角色/自动时装", __ex); }
        }

        private static void ApplyType(Character ch, Outfit[] all, Outfit.OutfitType type, ConfigEntry<string> entry) {
            string policy = entry != null ? (entry.Value ?? "") : "";
            // 保存记录优先：该角色有记录就按记录里这一类的值走（-1 不干预 / -2 随机 / N 序号）。
            if (_oSavedUse == null || _oSavedUse.Value) {
                string rec = GetSavedLook(ch.CharacterSprite.ToString());
                if (!string.IsNullOrEmpty(rec)) {
                    string[] parts = rec.Split(',');
                    int ti = (int)type;
                    if (ti >= 0 && ti < parts.Length) {
                        string v = parts[ti];
                        int sv;
                        if (int.TryParse(v, out sv) && sv >= 0) policy = sv.ToString();
                        else if (v == "-2") policy = "random";
                    }
                }
            }
            if (policy == "") return;                                  // 不干预
            List<Outfit> cand = Candidates(all, type);
            if (cand.Count == 0) return;
            Outfit sel;
            if (policy == "random") sel = cand[UnityEngine.Random.Range(0, cand.Count)];
            else {
                int idx;
                if (!int.TryParse(policy, out idx)) return;
                idx = Mathf.Clamp(idx, 0, cand.Count - 1);
                sel = cand[idx];
            }
            for (int i = 0; i < all.Length; i++) {
                Outfit o = all[i];
                if (o != null && o.outfitType == type && o.followThisOutfit == null) o.on = ReferenceEquals(o, sel);
            }
        }
    }
}
