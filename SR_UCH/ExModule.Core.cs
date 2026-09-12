using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using SR_UCH.Tweaks; //SR / Loc

namespace SR_UCH_EX {
public partial class ExModule {

// ==== 分区：Core（配置 / 状态 / Initialize / 自身状态 Harmony 补丁 / 通用助手）====

        private static ConfigEntry<bool> _invincible;
        private static ConfigEntry<bool> _fly;
        private static ConfigEntry<PointBlock.pointBlockType> _scoreType;
        private static ConfigEntry<int> _coinAmount;
        private static ConfigEntry<int> _livesAmount;
        private static ConfigEntry<GameState.LevelName> _targetLevel;
        private static ConfigEntry<bool> _crouchMove;
        //自定义快捷键（EX 页对应按钮右键设置，再右键删除；按下即切换功能）
        private static ConfigEntry<KeyCode> _invincibleKey;
        private static ConfigEntry<KeyCode> _flyKey;
        private static ConfigEntry<KeyCode> _crouchKey;
        //操作快捷键（EX 页对应按钮右键设置，再右键删除；按下即触发该操作）
        private static ConfigEntry<KeyCode> _kickKey;
        private static ConfigEntry<KeyCode> _scoreKey;
        private static ConfigEntry<KeyCode> _coinKey;
        private static ConfigEntry<KeyCode> _respawnKey;
        private static ConfigEntry<KeyCode> _winKey;
        private static ConfigEntry<KeyCode> _endRoundKey;
        private static ConfigEntry<KeyCode> _livesKey;
        private static ConfigEntry<KeyCode> _forceLevelKey;
        private static ConfigEntry<KeyCode> _clearMapKey; //清除地图对象（玩家放置的道具）快捷键
        private static ConfigEntry<KeyCode> _killKey;     //杀死目标快捷键
        private static ConfigEntry<KeyCode> _clearPartyBoxKey; //清空派对盒道具快捷键
        private static ConfigEntry<KeyCode> _ignoreHostKey;    //无视房主限制开关快捷键
        //允许客户端删除（本模块功能）：开关 + 快捷键；SR 侧通过 DestroyBlocks.SetAllowClientsProvider 读取
        private static ConfigEntry<bool> _allowClients;
        private static ConfigEntry<KeyCode> _allowClientsKey;
        public static bool AllowClientsOn { get { return _allowClients != null && _allowClients.Value; } }
        public static void ToggleAllowClients() { if (_allowClients != null) _allowClients.Value = !_allowClients.Value; }
        //无视模式限制 / 冻结角色（也是本模块的开关）：配置在 EX 段，SR 侧只读取（SetExToggles）
        private static ConfigEntry<bool> _ignoreModeLimitEntry;
        private static ConfigEntry<KeyCode> _ignoreModeLimitKey;
        private static ConfigEntry<bool> _freezeCharEntry;
        private static ConfigEntry<KeyCode> _freezeCharKey;
        //无视模式限制：勾选后所有"仅自由模式"的门控（视野/地图/重生/关卡等）都放行
        public static bool IgnoreModeLimit {
            get { return _ignoreModeLimitEntry != null && _ignoreModeLimitEntry.Value; }
            set { if (_ignoreModeLimitEntry != null) _ignoreModeLimitEntry.Value = value; }
        }
        //冻结角色：打开面板/地图时冻结自己的角色（其他角色照常移动）
        public static bool PauseGame {
            get { return _freezeCharEntry != null && _freezeCharEntry.Value; }
            set { if (_freezeCharEntry != null) _freezeCharEntry.Value = value; }
        }
        //当前目标：存 **networkNumber**，不是列表下标——玩家进出会让 OnlineNumbers 重排，
        //存下标会在重排后「下标仍有效但指向别人」，导致踢人/加分/复活作用到错误目标（提示词 P0-2）。
        private static int _targetNumber = -1;
        private static float _pendingEndRoundAt = -1f; //加分/金币后延迟检查是否达获胜分（服务器端）
        private static System.Reflection.FieldInfo _invincibleField; //invincibleTimer 字段缓存
        private static System.Reflection.FieldInfo _crouchField;     //crouchingDown 字段缓存（原每帧 AccessTools.Field，与 _invincibleField 的写法自相矛盾 — P0-1）
        private static System.Reflection.FieldInfo _scoreKeeperInstanceField; //ScoreKeeper.instance 字段缓存（P1-5）
        //OnlineNumbers 每帧缓存：原来每次调用都 new List，而 TargetName/CurrentTargetNumber×2/
        //EnsureDefaultTarget/PlayerTable/CheckPendingEndRound 一帧会调 ≥5 次（P1-8）。
        private static List<int> _onlineCache;
        private static int _onlineCacheFrame = -1;
        //Rigidbody2D 按角色缓存：ApplyFly/KeepCrouch 每物理帧都 GetComponent（50Hz×N，P1-6）。
        //角色销毁后引用会变成“伪 null”，下次自动重新取；超过上限整体清空防 instanceID 复用泄漏。
        private static readonly Dictionary<int, Rigidbody2D> _rbCache = new Dictionary<int, Rigidbody2D>();
        //BlockDeath 深渊分支的重入放行标志（不依赖 prefix 首行顺序，见 BlockDeath 注释 — #12）
        private static bool _deathBypass;
        private const string CauseFalling = "Falling"; //原因字符串提常量（原来散落 2 处 — #19）

        //config entries exposed for the console's inline controls (dropdown / number box)
        public static ConfigEntry<PointBlock.pointBlockType> ScoreTypeEntry { get { return _scoreType; } }
        public static ConfigEntry<int> CoinAmountEntry { get { return _coinAmount; } }
        public static ConfigEntry<int> LivesAmountEntry { get { return _livesAmount; } }
        public static ConfigEntry<GameState.LevelName> TargetLevelEntry { get { return _targetLevel; } }

        public static bool CrouchMoveOn { get { return _crouchMove != null && _crouchMove.Value; } }
        public static void ToggleCrouchMove() {
            if (_crouchMove == null) return;
            _crouchMove.Value = !_crouchMove.Value;
            Notify("蹲移：" + (_crouchMove.Value ? "开" : "关"));
        }
        //快捷键配置暴露给 SR_UCH（EX 页按钮右键设置/删除）
        public static ConfigEntry<KeyCode> InvincibleKeyEntry { get { return _invincibleKey; } }
        public static ConfigEntry<KeyCode> FlyKeyEntry { get { return _flyKey; } }
        public static ConfigEntry<KeyCode> CrouchMoveKeyEntry { get { return _crouchKey; } }
        //操作快捷键配置暴露给 SR_UCH（EX 页对应按钮右键设置/删除）
        public static ConfigEntry<KeyCode> KickKeyEntry { get { return _kickKey; } }
        public static ConfigEntry<KeyCode> ScoreKeyEntry { get { return _scoreKey; } }
        public static ConfigEntry<KeyCode> CoinKeyEntry { get { return _coinKey; } }
        public static ConfigEntry<KeyCode> RespawnKeyEntry { get { return _respawnKey; } }
        public static ConfigEntry<KeyCode> WinKeyEntry { get { return _winKey; } }
        public static ConfigEntry<KeyCode> EndRoundKeyEntry { get { return _endRoundKey; } }
        public static ConfigEntry<KeyCode> LivesKeyEntry { get { return _livesKey; } }
        public static ConfigEntry<KeyCode> ForceLevelKeyEntry { get { return _forceLevelKey; } }
        public static ConfigEntry<KeyCode> ClearMapObjectsKeyEntry { get { return _clearMapKey; } }
        public static ConfigEntry<KeyCode> KillKeyEntry { get { return _killKey; } }
        public static ConfigEntry<KeyCode> ClearPartyBoxKeyEntry { get { return _clearPartyBoxKey; } }
        public static ConfigEntry<KeyCode> IgnoreHostLimitKeyEntry { get { return _ignoreHostKey; } }

        //diagnostic: which players are still loading the level snapshot (whole lobby waits
        //for this list to empty - "加载不进去" happens when someone never reports done)
        public static string LoadingState() {
            try {
                GameControl gc = null;
                if (LobbyManager.instance != null) {
                    try { gc = LobbyManager.instance.CurrentGameController as GameControl; } catch { }
                }
                if (gc == null) gc = UnityEngine.Object.FindObjectOfType<GameControl>();
                if (gc == null) return "";
                System.Reflection.FieldInfo f = HarmonyLib.AccessTools.Field(typeof(GameControl), "playerNumbersStillLoadingSnapshot");
                if (f == null) return "";
                object raw = f.GetValue(gc);
                if (raw == null) return "";
                System.Collections.IList list = (System.Collections.IList)raw;
                if (list != null && list.Count > 0) {
                    string s = "";
                    foreach (object n in list) { if (s.Length > 0) s += ","; s += n; }
                    return SR.T("等待加载: 玩家[", "Loading: players[") + s + "]";
                }
            } catch { }
            return "";
        }

        private static ConfigEntry<bool> _enabledEntry;
        private static ConfigEntry<bool> _ignoreHostLimitEntry;
        //EX 反射接口版本：SR_UCH 通过 ExRef 反射调用本模块，双方靠这个值做握手。
        //SR_UCH 侧要求 >= ExRef.RequiredApiVersion；缺失/过旧时 EX 页会给出明确提示
        //（提示词第 7 节“闭源 EX 通道：加 ApiVersion 握手”）。改动 ExModule 的公开成员时递增。
        public static int ApiVersion { get { return 1; } }
        //无视房主房客限制：开启后房客也能执行房主限制的操作（如树屋问号等）
        public static bool IgnoreHostLimit {
            get { return _ignoreHostLimitEntry != null && _ignoreHostLimitEntry.Value; }
            set { if (_ignoreHostLimitEntry != null) _ignoreHostLimitEntry.Value = value; }
        }
        //runtime toggle (also controlled by the in-game manager / console master switch)
        //persisted in the config so the last state survives restarts; defaults to OFF.
        public static bool Enabled {
            get { return _enabledEntry != null && _enabledEntry.Value; }
            set { if (_enabledEntry != null) _enabledEntry.Value = value; }
        }

        //--- console-facing state (the EX console page reads these) ---
        public static bool InvincibleOn { get { return _invincible != null && _invincible.Value; } }
        public static bool FlyOn { get { return _fly != null && _fly.Value; } }
        public static void ToggleInvincible() {
            if (_invincible == null) return;
            _invincible.Value = !_invincible.Value;
            Notify("无敌：" + (_invincible.Value ? "开" : "关"));
        }
        public static void ToggleFly() {
            if (_fly == null) return;
            _fly.Value = !_fly.Value;
            Notify("飞天：" + (_fly.Value ? "开" : "关"));
        }

        //清空当前派对盒里的可选道具。
        //同步说明：派对盒道具是服务器 NetworkServer.Spawn 出来的网络对象，销毁必须由服务器走
        //NetworkServer.Destroy 才会广播到客户端。原来直接调用游戏私有的 ClearPieces()（内部是
        //本地 UnityEngine.Object.Destroy），只有服务器端执行才有效；房客端执行只删掉自己那份
        //本地副本，房主/其他人都看不到，反而造成本地与服务器不同步。
        public static void ClearPartyBox() {
            try {
                PartyBox pb = UnityEngine.Object.FindObjectOfType<PartyBox>();
                if (pb == null) { Notify("未找到派对盒"); return; }
                System.Reflection.FieldInfo f = HarmonyLib.AccessTools.Field(typeof(PartyBox), "pieces");
                System.Collections.IList list = f != null ? f.GetValue(pb) as System.Collections.IList : null;
                if (list != null && UnityEngine.Networking.NetworkServer.active) {
                    //服务器端：逐个 NetworkServer.Destroy（广播销毁）；先收集再销毁，避免边遍历边改
                    List<UnityEngine.GameObject> dead = new List<UnityEngine.GameObject>();
                    foreach (object o in list) {
                        UnityEngine.Component c = o as UnityEngine.Component;
                        if (c != null && c.gameObject != null) dead.Add(c.gameObject);
                    }
                    for (int i = 0; i < dead.Count; i++) {
                        try { UnityEngine.Networking.NetworkServer.Destroy(dead[i]); }
                        catch (Exception __ex) { SR_UCH.MainPlugin.ModLogger.LogWarning("清空派对盒(销毁)失败: " + __ex.Message); }
                    }
                    list.Clear();
                    Notify("已清空派对盒道具（全员）");
                } else {
                    //房客端/无服务器：退回游戏私有 ClearPieces（仅本地生效，别人看不到）
                    var m = HarmonyLib.AccessTools.Method(typeof(PartyBox), "ClearPieces");
                    if (m != null) m.Invoke(pb, null);
                    else if (list != null) list.Clear();
                    Notify("已清空派对盒道具（仅本地，房客端别人看不到）");
                }
            } catch (System.Exception e) { SR_UCH.MainPlugin.ModLogger.LogWarning("清空派对盒失败: " + e.Message); }
        }

        //清除地图对象：清空**玩家放置过**的全部道具（关卡自带布局不算，依据 DestroyBlocks 的
        //_placements 记录集）。走方块破坏同一条网络通道：对每个方块广播 PieceDestroyed
        //（房客发送的这条消息由服务器转发全员）并本地销毁 → **全员可见，且房客也能用**。
        public static void ClearMapObjects() {
            try {
                int n = DestroyBlocks.ClearAllPlayerPlacements();
                Notify(n > 0 ? ("已清除地图对象: " + n + " 个") : "地图上没有玩家放置的道具");
            } catch (System.Exception e) {
                SR_UCH.MainPlugin.ModLogger.LogWarning("清除地图对象失败: " + e.Message);
            }
        }

        public void Initialize(BepInEx.BaseUnityPlugin plugin) {
            _enabledEntry = plugin.Config.Bind("EX", "Enabled", false,
                "附加功能总开关（默认关闭，切换后自动保存）");
            _ignoreHostLimitEntry = plugin.Config.Bind("EX", "Ignore Host Limit", false,
                "无视房主房客限制：开启后房客也能执行房主限制的操作（如树屋问号添加/删除等）");
            _invincible = plugin.Config.Bind("EX", "Invincible", false,
                "无敌：免疫所有非强制死亡（陷阱/子弹/掉坑/拳击等）");
            _fly = plugin.Config.Bind("EX", "Fly", false,
                "飞天：方向键自由飞行（上/下升降，左/右平移，按住 Shift 加速，不按键则悬浮空中）");
            _scoreType = plugin.Config.Bind("EX", "Score Type", PointBlock.pointBlockType.win,
                "加分的分数类型（获胜/陷阱击杀/第一/金币等），按该类型的标准分值走游戏原生消息全员可见");
            _coinAmount = plugin.Config.Bind("EX", "Coin Amount", 1,
                "加金币数量（一次给目标 N 个金币，全员可见金币分块）");
            _livesAmount = plugin.Config.Bind("EX", "Lives Amount", 1,
                "加生命数量（正数加命，负数扣命；只对自己生效，本地显示）");
            _targetLevel = plugin.Config.Bind("EX", "Target Level", GameState.LevelName.FARM,
                "指定关卡（树屋大厅直接开始；仅单机/本地派对/房主有效）");
            _crouchMove = plugin.Config.Bind("EX", "Crouch Move", false,
                "蹲移：蹲下状态下也能左右移动（A/D 或方向键）");
            _invincibleKey = plugin.Config.Bind("EX", "Invincible Key", KeyCode.None,
                "无敌快捷键（EX 页「无敌」按钮右键设置，再右键删除；按下即切换无敌）");
            _flyKey = plugin.Config.Bind("EX", "Fly Key", KeyCode.None,
                "飞天快捷键（EX 页「飞天」按钮右键设置，再右键删除；按下即切换飞天）");
            _crouchKey = plugin.Config.Bind("EX", "Crouch Move Key", KeyCode.None,
                "蹲移快捷键（EX 页「蹲移」按钮右键设置，再右键删除；按下即切换蹲移）");
            _kickKey = plugin.Config.Bind("EX", "Kick Key", KeyCode.None,
                "踢出目标快捷键（EX 页「踢出目标」按钮右键设置，再右键删除；按下即触发）");
            _scoreKey = plugin.Config.Bind("EX", "Score Key", KeyCode.None,
                "加分快捷键（EX 页「加分」按钮右键设置，再右键删除；按下即触发）");
            _coinKey = plugin.Config.Bind("EX", "Coin Key", KeyCode.None,
                "加金币快捷键（EX 页「加金币」按钮右键设置，再右键删除；按下即触发）");
            _respawnKey = plugin.Config.Bind("EX", "Respawn Key", KeyCode.None,
                "复活快捷键（EX 页「复活」按钮右键设置，再右键删除；按下即触发）");
            _winKey = plugin.Config.Bind("EX", "Win Key", KeyCode.None,
                "获胜快捷键（EX 页「获胜」按钮右键设置，再右键删除；按下即触发）");
            _endRoundKey = plugin.Config.Bind("EX", "End Round Key", KeyCode.None,
                "结束对局快捷键（EX 页「结束对局」按钮右键设置，再右键删除；按下即触发）");
            _livesKey = plugin.Config.Bind("EX", "Lives Key", KeyCode.None,
                "加生命快捷键（EX 页「加生命」按钮右键设置，再右键删除；按下即触发）");
            _forceLevelKey = plugin.Config.Bind("EX", "Force Level Key", KeyCode.None,
                "指定关卡快捷键（EX 页「指定关卡」按钮右键设置，再右键删除；按下即触发）");
            _clearMapKey = plugin.Config.Bind("EX", "Clear Map Objects Key", KeyCode.None,
                "清除地图对象快捷键（EX 页「清除地图对象」按钮右键设置，再右键删除；按下即触发）");
            _killKey = plugin.Config.Bind("EX", "Kill Key", KeyCode.None,
                "杀死目标快捷键（EX 页「杀死目标」按钮右键设置，再右键删除；按下即触发）");
            _clearPartyBoxKey = plugin.Config.Bind("EX", "Clear Party Box Key", KeyCode.None,
                "清空派对盒道具快捷键（EX 页「清空派对盒道具」按钮右键设置，再右键删除；按下即触发）");
            _ignoreHostKey = plugin.Config.Bind("EX", "Ignore Host Limit Key", KeyCode.None, "「无视房主限制」开关的快捷键（组合键：点按钮后按住 Shift/Ctrl/Alt 再按主键）");
            //允许客户端删除：开关 + 快捷键（本功能的配置/状态/快捷键都在 EX；SR 只提供"是否允许"的接点）
            _allowClients = plugin.Config.Bind("EX", "Allow Clients", false,
                "允许客户端删除：非房主玩家也能删除方块（由房主同步；需要游戏内已开启方块破坏）");
            _allowClientsKey = plugin.Config.Bind("EX", "Allow Clients Key", KeyCode.None,
                "「允许客户端删除」开关的快捷键（组合键：点按钮后按住 Shift/Ctrl/Alt 再按主键）");
            SR.RegisterKey("EX-允许客户端删除", _allowClientsKey, "press");
            DestroyBlocks.SetAllowClientsProvider(() => AllowClientsOn);
            //无视模式限制 / 冻结角色：配置 + 快捷键都在本模块（段名仍是 EX，老配置无需迁移）
            _ignoreModeLimitEntry = plugin.Config.Bind("EX", "Ignore Mode Limit", false,
                "无视模式限制：附加功能/视野/地图/重生在任何模式下都可用");
            _ignoreModeLimitKey = plugin.Config.Bind("EX", "Ignore Mode Limit Key", KeyCode.None,
                "「无视模式限制」开关的快捷键（组合键：点按钮后按住 Shift/Ctrl/Alt 再按主键）");
            SR.RegisterKey("EX-无视模式限制", _ignoreModeLimitKey, "press");
            _freezeCharEntry = plugin.Config.Bind("EX", "Freeze Character", false,
                "冻结角色：打开面板/地图时冻结自己的角色（其他角色照常移动；默认关 = 打开面板/地图时自己也能动）");
            _freezeCharKey = plugin.Config.Bind("EX", "Freeze Character Key", KeyCode.None,
                "「冻结角色」开关的快捷键（组合键：点按钮后按住 Shift/Ctrl/Alt 再按主键）");
            SR.RegisterKey("EX-冻结角色", _freezeCharKey, "press");
            //把这两个开关的取值交给 SR（SR 自己只读，不持有配置）
            SR.SetExToggles(() => IgnoreModeLimit, () => PauseGame);
            //register every EX Harmony patch (自身无敌/飞天/蹲移)
            Harmony.CreateAndPatchAll(typeof(ExModule));
            //全局快捷键检测组件（任意场景：关卡/树屋都生效）
            GameObject go = new GameObject("SR_UCH_EXHotkeys");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<ExHotkeyComponent>();
        }

        [HarmonyPatch(typeof(Character), "KillCharacter")]
        [HarmonyPrefix]
        static bool BlockDeath(Character __instance, string cause, bool deathFreezeOn, int causedByPlayerNumber, bool force) {
            if (force) return true; //match the game: forced deaths still kill
            if (!Enabled) return true;
            if (_deathBypass) return true; //本次是我们自己发起的深渊强制死亡：直接放行
            try {
                if (__instance == null) return true;
                //无敌开启：拦截普通死亡；但深渊（Falling）强制死亡，像重生无敌那样会死
                if (_invincible != null && _invincible.Value && __instance.hasAuthority) {
                    if (cause == CauseFalling) {
                        //深渊：绕过无敌。用**显式重入标志**放行，而不是依赖 prefix 首行的
                        //`if (force) return true` —— 那样两行顺序一改就会无限递归（提示词 #12）
                        _deathBypass = true;
                        try { __instance.KillCharacter(cause, deathFreezeOn, causedByPlayerNumber, force: true); }
                        finally { _deathBypass = false; }
                        return false; //阻止原调用（避免重复）
                    }
                    return false; //普通死亡：拦截
                }
            } catch { }
            return true;
        }

        //无敌 (second gate): deaths can also arrive as RpcSetupDeath -> setupDeath (the host
        //judges and broadcasts), which bypasses KillCharacter - block that too
        [HarmonyPatch(typeof(Character), "setupDeath")]
        [HarmonyPrefix]
        static bool BlockSetupDeath(Character __instance, string cause) {
            if (!Enabled) return true;
            try {
                if (__instance == null) return true;
                //无敌开启：拦截普通 setupDeath；深渊（Falling）不拦截（会死，像重生无敌）
                if (_invincible != null && _invincible.Value && __instance.hasAuthority) {
                    if (cause == CauseFalling) return true; //深渊允许死亡
                    return false; //普通死亡：拦截
                }
            } catch { }
            return true;
        }

        //飞天 + 蹲移 + 无敌刷新：合并为一个 Character.FixedUpdate postfix（减少每角色每帧的 Harmony 调用）
        [HarmonyPatch(typeof(Character), "FixedUpdate")]
        [HarmonyPostfix]
        static void ApplyFlyAndCrouch(Character __instance) {
            ApplyFly(__instance);
            KeepCrouch(__instance);
            ApplyInvincibleTick(__instance);
        }

        //自定义快捷键：按下即切换/触发 EX 功能（EX 页按钮右键设置快捷键）。
        //在全局 Update（ExHotkeyComponent）里检测，任意场景（关卡/树屋）都可靠触发。
        //**组合键**：必须走 SR.ComboKeyDown（= 修饰键按住 + 主键按下），不能用 Input.GetKeyDown(主键)——
        //旧写法完全忽略修饰键，导致绑了 Shift+P 之后按 P 也会误触发、Shift+P 也照触发，
        //用户侧表现就是"不支持组合键"。
        static void CheckAllHotkeys() {
            if (!Enabled) return;
            TrackLobbyHome(); //树屋安全复活用：记录各角色进大厅时的位置（树屋没有 LevelLayout.StartPoint）
            if (SR.UiOpen && SR.BlockInput) return; //EX 页打开时由按钮处理，避免误触
            if (SR.ComboKeyDown(_invincibleKey)) ToggleInvincible();
            if (SR.ComboKeyDown(_flyKey)) ToggleFly();
            if (SR.ComboKeyDown(_crouchKey)) ToggleCrouchMove();
            if (SR.ComboKeyDown(_kickKey)) KickTarget();
            if (SR.ComboKeyDown(_scoreKey)) AddScore();
            if (SR.ComboKeyDown(_coinKey)) AddCoin();
            if (SR.ComboKeyDown(_respawnKey)) RespawnTarget();
            if (SR.ComboKeyDown(_winKey)) WinTarget();
            if (SR.ComboKeyDown(_endRoundKey)) EndRound();
            if (SR.ComboKeyDown(_livesKey)) AddLives();
            if (SR.ComboKeyDown(_forceLevelKey)) ForceLevel();
            if (SR.ComboKeyDown(_clearMapKey)) ClearMapObjects();
            if (SR.ComboKeyDown(_killKey)) KillTarget();
            if (SR.ComboKeyDown(_clearPartyBoxKey)) ClearPartyBox();
            if (SR.ComboKeyDown(_ignoreHostKey)) IgnoreHostLimit = !IgnoreHostLimit;
            if (SR.ComboKeyDown(_allowClientsKey)) ToggleAllowClients();
            if (SR.ComboKeyDown(_ignoreModeLimitKey)) IgnoreModeLimit = !IgnoreModeLimit;
            if (SR.ComboKeyDown(_freezeCharKey)) PauseGame = !PauseGame;
        }

        //全局 Update 组件：任意场景检测 EX 快捷键
        private class ExHotkeyComponent : MonoBehaviour {
            void Update() {
                try { CheckAllHotkeys(); } catch { }
            }
        }

        //无敌（原理同重生无敌）：开启时每帧保持 invincibleTimer>0（Invincible 恒 true，死亡被免疫），
        //关闭时清零 invincibleTimer → 立即恢复可死亡。避免用 return false 永久拦截导致关闭后残留无敌。
        static void ApplyInvincibleTick(Character c) {
            try {
                if (c == null || !c.hasAuthority) return;
                if (!Enabled) return;
                if (_invincibleField == null) {
                    _invincibleField = HarmonyLib.AccessTools.Field(typeof(Character), "invincibleTimer");
                }
                bool on = _invincible != null && _invincible.Value;
                if (on) {
                    //仅在计时器偏低时续期：原来每物理帧无条件 StartInvincibleTimer(999f)，
                    //50Hz 白调（P1-7）。999 秒的计时器只要 >500 就无需再刷新。
                    float t = 0f;
                    if (_invincibleField != null) { try { t = (float)_invincibleField.GetValue(c); } catch { t = 0f; } }
                    if (t < 500f) c.StartInvincibleTimer(999f); //保持无敌
                } else {
                    //关闭：清零 invincibleTimer，立即恢复可死亡
                    if (_invincibleField != null && c.Invincible) {
                        _invincibleField.SetValue(c, 0f);
                    }
                }
            } catch { }
        }

        //飞天: after the game's own movement physics ran (FixedUpdate postfix), take over the
        //velocity entirely - ARROW KEYS move freely in the air, no key = hover; hold Shift to
        //sprint (double speed). Applies to the local player when the self switch is on.
        static void ApplyFly(Character __instance) {
            if (!Enabled) return;
            if (__instance == null) return;
            try {
                if (!(__instance.hasAuthority && _fly != null && _fly.Value)) return;
                //死亡/倒地的角色实体也允许飞天（原检查 Dead/Dying 会拦截尸体飞天）
                if (__instance.Success) return;
                Rigidbody2D rb = GetRb(__instance);
                if (rb == null) return;
                float mul = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? 2f : 1f;
                float vx = 0f, vy = 0f;
                if (Input.GetKey(KeyCode.UpArrow)) vy = 9f * mul;
                else if (Input.GetKey(KeyCode.DownArrow)) vy = -9f * mul;
                if (Input.GetKey(KeyCode.LeftArrow)) vx = -7f * mul;
                else if (Input.GetKey(KeyCode.RightArrow)) vx = 7f * mul;
                rb.velocity = new Vector2(vx, vy);
            } catch { }
        }

        //蹲移: the game normally locks horizontal movement while crouching - this lets the
        //player walk left/right while ducked (only when already crouching).
        static void KeepCrouch(Character __instance) {
            if (!Enabled || _crouchMove == null || !_crouchMove.Value) return;
            //飞天开启时不介入（提示词 §8.3 已确认「飞天优先」）：飞天已完全接管 velocity（含水平），
            //蹲移再覆盖 X 会变成「垂直在飞 + 水平在走路」的混合怪态，且 Shift 加速会失效；
            //飞行时人在空中，蹲移的地面语义本也不适用。
            if (_fly != null && _fly.Value) return;
            if (__instance == null || !__instance.hasAuthority) return;
            try {
                //crouchingDown 反射结果提为静态缓存：原来每次调用都 AccessTools.Field（50Hz×N，P0-1）
                if (_crouchField == null) _crouchField = HarmonyLib.AccessTools.Field(typeof(Character), "crouchingDown");
                if (_crouchField == null) return;
                bool crouching = (bool)_crouchField.GetValue(__instance);
                if (!crouching) return;
                Rigidbody2D rb = GetRb(__instance);
                if (rb == null) return;
                float h = 0f;
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) h = -1f;
                else if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) h = 1f;
                float speed = __instance.RunSpeed * 0.9f;
                rb.velocity = new Vector2(h * speed, rb.velocity.y);
            } catch { }
        }

        //对局开始: force-reset the manager UI so a stuck manager/map can never block the
        //snapshot-loading handshake; also clear the mod's coin display counter (new round = fresh).
        [HarmonyPatch(typeof(GameControl), "SetupStart")]
        [HarmonyPostfix]
        static void OnGameStartResetUi() {
            try { SR.ForceResetUiState(); } catch { }
            ResetCoinDisplayCounts(); //新回合 EX 加金币显示计数清零
            //新回合：清掉按帧/按角色的缓存，避免跨场景残留（角色销毁后 Rigidbody2D 引用失效）
            try { _rbCache.Clear(); _onlineCacheFrame = -1; } catch { }
        }

        public static void ResetCoinDisplayCounts() {
            try { _coinCounts.Clear(); } catch { }
        }

        //角色 Rigidbody2D 缓存（P1-6）：避免 ApplyFly/KeepCrouch 每物理帧 GetComponent。
        private static Rigidbody2D GetRb(Character c) {
            try {
                if (c == null) return null;
                int id = c.GetInstanceID();
                Rigidbody2D rb;
                if (_rbCache.TryGetValue(id, out rb) && rb != null) return rb;
                rb = c.GetComponent<Rigidbody2D>();
                if (_rbCache.Count > 64) _rbCache.Clear(); //防角色销毁后 instanceID 复用导致缓存膨胀
                _rbCache[id] = rb;
                return rb;
            } catch { return null; }
        }

        //每帧：加分/金币后若达获胜分则立即结算（服务器端）
        [HarmonyPatch(typeof(GameControl), "Update")]
        [HarmonyPostfix]
        static void OnGameControlUpdate() {
            try { CheckPendingEndRound(); } catch { }
            try { PruneCoinCounts(); } catch { } //#14：低频清理已离开玩家的金币显示计数
        }

        private static List<int> OnlineNumbers() {
            //每帧缓存（P1-8）：同一帧内多处调用共享同一份结果。调用方只读，不要修改返回的列表。
            if (_onlineCache != null && _onlineCacheFrame == Time.frameCount) return _onlineCache;
            List<int> list = new List<int>();
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.PlayerTracker != null) {
                    for (int i = 0; i < lm.PlayerTracker.NumPlayers; i++) {
                        NetworkPlayerTracker.NetPlayerInfo info = lm.PlayerTracker.GetPlayerInfoByIndex(i);
                        list.Add(info.NetworkNumber);
                    }
                }
            } catch { }
            //fallback: collect from live characters
            if (list.Count == 0) {
                try {
                    foreach (Player p in PlayerManager.GetInstance()) {
                        if (p == null || p.PlayerCharacter == null) continue;
                        int n = p.PlayerCharacter.networkNumber;
                        if (!list.Contains(n)) list.Add(n);
                    }
                } catch { }
            }
            _onlineCache = list;
            _onlineCacheFrame = Time.frameCount;
            return list;
        }

        private static int LocalNumber() {
            try {
                foreach (Player p in PlayerManager.GetInstance()) {
                    if (p == null) continue;
                    Character c = p.PlayerCharacter;
                    if (c != null && c.hasAuthority) return c.networkNumber;
                }
            } catch { }
            return -1;
        }

        //EX 页目标未选中时，默认选中本地玩家（"最初自动选择自己"）。已在表格选中其他目标时不动。
        //目标改存 networkNumber 后，这里只需判断「当前目标是否仍在线」即可（P0-2）。
        public static void EnsureDefaultTarget() {
            try {
                List<int> nums = OnlineNumbers();
                if (nums.Count == 0) return;
                if (_targetNumber >= 0 && nums.Contains(_targetNumber)) return; //已有有效目标
                int me = LocalNumber();
                if (me >= 0 && nums.Contains(me)) { _targetNumber = me; return; } //优先选自己
                _targetNumber = nums[0]; //找不到自己（树屋等）则选第一个
            } catch { }
        }

        //the selected target's network number, or -1

        private static string PlayerName(int number) {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.PlayerTracker != null) {
                    LobbyPlayer lp = lm.PlayerTracker.GetLobbyPlayer(number);
                    if (lp != null && !string.IsNullOrEmpty(lp.playerName)) return lp.playerName;
                }
            } catch { }
            return "玩家#" + number;
        }

        private static void Notify(string text) {
            try {
                UserMessageManager.Instance.UserMessage(text, false);
            } catch { }
            SR_UCH.MainPlugin.ModLogger.LogInfo("[EX] " + text);
        }

        private static Character GetCharacter(int number) {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.PlayerTracker != null) {
                    GamePlayer gp = lm.PlayerTracker.GetGamePlayer(number);
                    if (gp != null && gp.CharacterInstance != null) return gp.CharacterInstance;
                }
            } catch { }
            try {
                foreach (Player p in PlayerManager.GetInstance()) {
                    if (p == null || p.PlayerCharacter == null) continue;
                    if (p.PlayerCharacter.networkNumber == number) return p.PlayerCharacter;
                }
                foreach (Character c in UnityEngine.Object.FindObjectsOfType<Character>()) {
                    if (c != null && c.networkNumber == number) return c;
                }
            } catch { }
            return null;
        }

	}
}
