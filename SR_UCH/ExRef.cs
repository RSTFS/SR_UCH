using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace SR_UCH.Tweaks {
    //附加模块代理：SR_UCH 不直接引用 EX 模块（避免反向依赖），
    //所有附加功能调用通过反射转发到 SR_UCH_EX.ExModule 的 static 成员。
    //附加模块未安装时，所有成员返回安全默认值/空操作，EX 页自动隐藏。
    public static class ExRef {
        private static Type _type;
        private static bool _resolved;
        //反射缓存：避免每次访问都 GetMethod/GetProperty（EX 页每帧渲染会高频调用）
        private static readonly Dictionary<string, MethodInfo> _methodCache = new Dictionary<string, MethodInfo>();
        private static readonly Dictionary<string, PropertyInfo> _propCache = new Dictionary<string, PropertyInfo>();

        private static Type T {
            get {
                if (!_resolved) {
                    _resolved = true;
                    try {
                        //先按程序集名解析（Assembly.LoadFile 已把模块加载进 AppDomain）
                        _type = Type.GetType("SR_UCH_EX.ExModule, SR_UCH_EX");
                        if (_type == null) {
                            //兜底：遍历已加载程序集（LoadFile 的程序集不参与默认解析）
                            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) {
                                if (a.GetName().Name == "SR_UCH_EX") {
                                    _type = a.GetType("SR_UCH_EX.ExModule");
                                    break;
                                }
                            }
                        }
                    } catch { }
                }
                return _type;
            }
        }

        public static bool Loaded { get { return T != null; } }

        private static MethodInfo M(string name, params Type[] argTypes) {
            string key = name;
            if (argTypes != null && argTypes.Length > 0) key = name + "(" + string.Join(",", Array.ConvertAll(argTypes, t => t.FullName)) + ")";
            MethodInfo mi;
            if (_methodCache.TryGetValue(key, out mi)) return mi;
            try {
                if (T == null) return null;
                mi = T.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, argTypes ?? Type.EmptyTypes, null);
            } catch { mi = null; }
            _methodCache[key] = mi;
            return mi;
        }

        private static PropertyInfo P(string name) {
            PropertyInfo pi;
            if (_propCache.TryGetValue(name, out pi)) return pi;
            try {
                if (T == null) return null;
                pi = T.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            } catch { pi = null; }
            _propCache[name] = pi;
            return pi;
        }

        private static TVal Get<TVal>(string name) {
            try {
                PropertyInfo p = P(name);
                if (p == null) return default(TVal);
                return (TVal)p.GetValue(null, null);
            } catch { return default(TVal); }
        }

        private static void Set(string name, object value) {
            try {
                PropertyInfo p = P(name);
                if (p != null) p.SetValue(null, value, null);
            } catch { }
        }

        private static object Call(string name, params object[] args) {
            try {
                //按实际参数类型查找（SelectTargetByIndex(int) 等带参方法）
                Type[] argTypes = null;
                if (args != null && args.Length > 0) {
                    argTypes = new Type[args.Length];
                    for (int i = 0; i < args.Length; i++) {
                        argTypes[i] = args[i] != null ? args[i].GetType() : typeof(object);
                    }
                }
                MethodInfo m = M(name, argTypes ?? Type.EmptyTypes);
                if (m == null) return null;
                return m.Invoke(null, args);
            } catch { return null; }
        }

        //--- properties ---
        public static bool Enabled { get { return Get<bool>("Enabled"); } set { Set("Enabled", value); } }
        //附加功能的"无视房主房客限制"开关（树屋问号等房主限制功能读取它）
        public static bool IgnoreHostLimit { get { return Get<bool>("IgnoreHostLimit"); } set { Set("IgnoreHostLimit", value); } }
        public static bool InvincibleOn { get { return Get<bool>("InvincibleOn"); } }
        public static bool FlyOn { get { return Get<bool>("FlyOn"); } }
        public static bool CrouchMoveOn { get { return Get<bool>("CrouchMoveOn"); } }
        public static bool AntiKickOn { get { return Get<bool>("AntiKickOn"); } }

        public static ConfigEntry<PointBlock.pointBlockType> ScoreTypeEntry { get { return Get<ConfigEntry<PointBlock.pointBlockType>>("ScoreTypeEntry"); } }
        public static ConfigEntry<int> CoinAmountEntry { get { return Get<ConfigEntry<int>>("CoinAmountEntry"); } }
        public static ConfigEntry<int> LivesAmountEntry { get { return Get<ConfigEntry<int>>("LivesAmountEntry"); } }
        public static ConfigEntry<GameState.LevelName> TargetLevelEntry { get { return Get<ConfigEntry<GameState.LevelName>>("TargetLevelEntry"); } }

        //--- methods ---
        public static void ToggleInvincible() { Call("ToggleInvincible"); }
        public static void ToggleFly() { Call("ToggleFly"); }
        public static void ToggleCrouchMove() { Call("ToggleCrouchMove"); }
        public static void ToggleAntiKick() { Call("ToggleAntiKick"); }
        public static void SelectTargetByIndex(int index) { Call("SelectTargetByIndex", index); }
        public static bool IsSelf(int number) { object r = Call("IsSelf", number); return r is bool b && b; }

        public static string TargetName() { return (string)Call("TargetName") ?? "（未安装附加功能）"; }
        public static int CurrentTargetNumber() { object r = Call("CurrentTargetNumber"); return r is int i ? i : -1; }
        public static string Positions() { return (string)Call("Positions") ?? ""; }
        public static string LoadingState() { return (string)Call("LoadingState") ?? ""; }

        public static List<object> PlayerTable() {
            List<object> rows = new List<object>();
            try {
                object r = Call("PlayerTable");
                if (r == null) return rows;
                foreach (object row in (System.Collections.IEnumerable)r) rows.Add(row);
            } catch { }
            return rows;
        }
        //row fields (number / animal / score) via reflection —— 字段缓存（EX 页逐行读取高频）
        private static readonly Dictionary<string, FieldInfo> _rowFieldCache = new Dictionary<string, FieldInfo>();
        private static object RowField(object row, string field) {
            try {
                if (row == null) return null;
                FieldInfo fi;
                if (!_rowFieldCache.TryGetValue(field, out fi)) {
                    fi = row.GetType().GetField(field);
                    _rowFieldCache[field] = fi;
                }
                return fi != null ? fi.GetValue(row) : null;
            } catch { return null; }
        }
        public static int RowNumber(object row) { object v = RowField(row, "number"); return v is int i ? i : 0; }
        public static string RowAnimal(object row) { object v = RowField(row, "animal"); return v is string s ? s : "?"; }
        public static int RowScore(object row) { object v = RowField(row, "score"); return v is int i ? i : 0; }

        public static void KickTarget() { Call("KickTarget"); }
        public static void DisbandMatch() { Call("DisbandMatch"); }
        public static void NotifyCultivation(string text) { Call("NotifyCultivation", text); }
        public static void AddScore() { Call("AddScore"); }
        public static void AddCoin() { Call("AddCoin"); }
        public static void WinTarget() { Call("WinTarget"); }
        public static void RespawnTarget() { Call("RespawnTarget"); }
        public static void KillTarget() { Call("KillTarget"); }
        public static void AddLives() { Call("AddLives"); }
        public static void ForceLevel() { Call("ForceLevel"); }
        public static void EndRound() { Call("EndRound"); }
    }
}
