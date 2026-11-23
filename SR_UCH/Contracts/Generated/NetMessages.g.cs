// <net-decl:begin> 由 tools/spec_engine.py 重写，勿手改。
// 注意：消息号只引用 NetMsgId 常量，禁止在此写裸数字。
using System;
using System.Collections.Generic;
namespace SR_UCH {
    internal static class SrNetDecl {
        internal sealed class Message {
            internal string Name;
            internal short Id;
            internal string Authority;
            internal string Payload;
            internal Message(string name, short id, string authority, string payload) {
                Name = name; Id = id; Authority = authority; Payload = payload;
            }
        }
        internal static readonly Message[] All = new Message[] {
            new Message("ChaosRule", NetMsgId.ChaosRule, "host", "rule"),
            new Message("ChaosCard", NetMsgId.ChaosCard, "host", "card"),
            new Message("ChaosHello", NetMsgId.ChaosHello, "any", "hello"),
            new Message("ChaosKill", NetMsgId.ChaosKill, "host", "clear"),
        };
        internal static int Count { get { return All.Length; } }
        internal static IList<string> Conflicts() {
            List<string> bad = new List<string>();
            for (int i = 0; i < All.Length; i++)
                for (int j = i + 1; j < All.Length; j++)
                    if (All[i].Id == All[j].Id) bad.Add(All[i].Name + " 与 " + All[j].Name + " 撞号");
            return bad;
        }
    }
}
// <net-decl:end>
