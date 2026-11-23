// 反射成员缓存：同一 "Type.Member" 只解析一次，游戏更新后可整体失效。
// 注意：解析失败返回 default(T)，调用方须自带降级；不得把异常冒到 UI 帧。
using System;
using System.Collections.Generic;
using System.Reflection;

namespace SR_UCH {

    public static class RefCache {
        private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Instance | BindingFlags.Static;
        private static readonly Dictionary<string, FieldInfo> _fields = new Dictionary<string, FieldInfo>();
        private static readonly Dictionary<string, MethodInfo> _methods = new Dictionary<string, MethodInfo>();
        private static readonly Dictionary<string, PropertyInfo> _props = new Dictionary<string, PropertyInfo>();
        private static readonly Dictionary<string, bool> _declared = new Dictionary<string, bool>();
        private static readonly List<string> _misses = new List<string>();
        private static int _version;

        // 与 SRApi.Ref.Override 同源：登记审计 key，同时作为失效表的索引。
        public static void Declare(string key) {
            if (!string.IsNullOrEmpty(key)) _declared[key] = true;
        }
        public static int DeclaredCount { get { return _declared.Count; } }
        public static IList<string> Misses { get { return new List<string>(_misses); } }
        public static int Version { get { return _version; } }

        public static void InvalidateAll() {
            _fields.Clear(); _methods.Clear(); _props.Clear(); _misses.Clear(); _version++;
        }

        public static FieldInfo Field(string typeName, string member) {
            string key = typeName + "." + member;
            FieldInfo fi;
            if (_fields.TryGetValue(key, out fi)) return fi;
            Type t = Resolve(typeName);
            if (t != null) {
                for (Type cur = t; cur != null && fi == null; cur = cur.BaseType)
                    fi = cur.GetField(member, F | BindingFlags.DeclaredOnly);
            }
            if (fi == null && !_misses.Contains(key)) _misses.Add(key);
            _fields[key] = fi;
            return fi;
        }

        public static MethodInfo Method(string typeName, string member) {
            string key = typeName + "." + member;
            MethodInfo mi;
            if (_methods.TryGetValue(key, out mi)) return mi;
            Type t = Resolve(typeName);
            if (t != null) {
                for (Type cur = t; cur != null && mi == null; cur = cur.BaseType) {
                    MethodInfo[] ms = cur.GetMethods(F | BindingFlags.DeclaredOnly);
                    for (int i = 0; i < ms.Length; i++) if (ms[i].Name == member) { mi = ms[i]; break; }
                }
            }
            if (mi == null && !_misses.Contains(key)) _misses.Add(key);
            _methods[key] = mi;
            return mi;
        }

        // 读静态/实例字段；失败返回 fallback，不抛。
        public static T Read<T>(string typeName, string member, object instance, T fallback) {
            try {
                FieldInfo fi = Field(typeName, member);
                if (fi == null) return fallback;
                object v = fi.GetValue(fi.IsStatic ? null : instance);
                return v is T ? (T)v : fallback;
            } catch (Exception) { return fallback; }
        }


        // 按运行时 Type 取值：缓存键含参数签名，重载互不串；失败进 Misses，调用方自带降级。
        public static FieldInfo FieldOf(Type t, string member) {
            return t == null ? null : Field(t.FullName, member);
        }
        public static PropertyInfo PropertyOf(Type t, string member) {
            string key = (t == null ? "?" : t.FullName) + "." + member;
            PropertyInfo pi;
            if (_props.TryGetValue(key, out pi)) return pi;
            if (t != null) {
                for (Type cur = t; cur != null && pi == null; cur = cur.BaseType)
                    pi = cur.GetProperty(member, F | BindingFlags.DeclaredOnly);
            }
            if (pi == null && !_misses.Contains(key)) _misses.Add(key);
            _props[key] = pi;
            return pi;
        }
        public static MethodInfo MethodOf(Type t, string member, params Type[] args) {
            string key = (t == null ? "?" : t.FullName) + "." + member
                       + (args == null ? "" : "(" + string.Join(",", Array.ConvertAll(args, a => a == null ? "?" : a.FullName)) + ")");
            MethodInfo mi;
            if (_methods.TryGetValue(key, out mi)) return mi;
            if (t != null) {
                try { mi = args == null ? t.GetMethod(member, F) : t.GetMethod(member, F, null, args, null); }
                catch (AmbiguousMatchException) {
                    MethodInfo[] ms = t.GetMethods(F);
                    for (int i = 0; i < ms.Length; i++) if (ms[i].Name == member) { mi = ms[i]; break; }
                }
            }
            if (mi == null && !_misses.Contains(key)) _misses.Add(key);
            _methods[key] = mi;
            return mi;
        }

        private static Type Resolve(string typeName) {
            if (string.IsNullOrEmpty(typeName)) return null;
            Type t = Type.GetType(typeName);
            if (t != null) return t;
            Assembly[] asm = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asm.Length; i++) {
                t = asm[i].GetType(typeName);
                if (t != null) return t;
            }
            return null;
        }
    }
}
