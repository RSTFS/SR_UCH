// 自定义网络消息号集中登记；避免与其他 BepInEx 插件撞号。
// 注意：新增号必须走 Register，禁止在功能里写裸数字。
using System;
using System.Collections.Generic;

namespace SR_UCH {

    public static class NetMsgId {
        public const short ChaosRule   = 771;
        public const short ChaosCard   = 772;
        public const short ChaosHello  = 773;
        public const short ChaosKill   = 774;

        private static readonly Dictionary<short, string> _owners = new Dictionary<short, string>();
        private static readonly List<string> _conflicts = new List<string>();

        public static void Register(string owner, short id) {
            string prev;
            if (_owners.TryGetValue(id, out prev) && prev != owner) {
                string s = owner + " 与 " + prev + " 撞号 " + id;
                if (!_conflicts.Contains(s)) _conflicts.Add(s);
            } else _owners[id] = owner;
        }
        public static IList<string> Conflicts { get { return new List<string>(_conflicts); } }
        public static int Count { get { return _owners.Count; } }
        public static void Reset() { _owners.Clear(); _conflicts.Clear(); }
    }
}
