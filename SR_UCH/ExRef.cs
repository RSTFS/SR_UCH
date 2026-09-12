using System;
using System.Collections.Generic;
using System.Reflection;

namespace SR_UCH.Tweaks {
    //附加模块（SR_UCH_EX.dll）桥：SR_UCH 不直接引用 EX，只按名字反射读取它需要的少量成员；
    //EX 未安装时返回安全默认值。EX 的界面与功能全在 EX 模块内部（它通过 SR 的 public 注册 API 挂页面）。
    public static class ExRef {
        private static Type _type;
        private static bool _resolved;
        private static readonly Dictionary<string, PropertyInfo> _propCache = new Dictionary<string, PropertyInfo>();

        private static Type T {
            get {
                if (!_resolved) {
                    _resolved = true;
                    try {
                        _type = Type.GetType("SR_UCH_EX.ExModule, SR_UCH_EX");
                        if (_type == null) {
                            //Assembly.LoadFile 加载的程序集不参与默认解析，兜底遍历
                            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies()) {
                                if (a.GetName().Name == "SR_UCH_EX") { _type = a.GetType("SR_UCH_EX.ExModule"); break; }
                            }
                        }
                    } catch { }
                }
                return _type;
            }
        }

        public static bool Loaded { get { return T != null; } }

        //接口版本握手：EX 太旧（无该属性）→ VersionOk=false，提示用户更新 DLL
        public const int RequiredApiVersion = 1;
        private static bool _versionChecked;
        private static int _apiVersion;
        public static int ExApiVersion { get { EnsureVersionChecked(); return _apiVersion; } }
        public static bool VersionOk { get { return Loaded && ExApiVersion >= RequiredApiVersion; } }
        private static void EnsureVersionChecked() {
            if (_versionChecked) return;
            _versionChecked = true;
            try {
                _apiVersion = Loaded ? Get<int>("ApiVersion") : 0;
                if (Loaded && _apiVersion < RequiredApiVersion) {
                    MainPlugin.ModLogger.LogWarning("[EX] 附加模块接口版本不匹配：EX=" + _apiVersion
                        + "，需要 >= " + RequiredApiVersion + "。部分 EX 功能可能不可用，请更新 SR_UCH_EX.dll。");
                }
            } catch { _apiVersion = 0; }
        }

        private static PropertyInfo P(string name) {
            PropertyInfo pi;
            if (_propCache.TryGetValue(name, out pi)) return pi;
            try {
                pi = T == null ? null : T.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
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

        //「无视房主房客限制」：EX 页开关，树屋问号等房主限制功能读它（EX 未装 = false）
        public static bool IgnoreHostLimit { get { return Get<bool>("IgnoreHostLimit"); } }
    }
}
