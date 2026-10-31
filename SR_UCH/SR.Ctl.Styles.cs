// 控件样式门面分片：从 SR.Ctl.cs 拆出的纯 getter（评审 P2：SR.Ctl.cs 偏大）。
// 只做转发，不含逻辑；样式对象本身仍由 SR.Styles.cs 创建。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace SR_UCH.Tweaks {
public partial class SR {
    public static partial class Ctl {

        public static GUIStyle Label { get { return _label; } }
        public static GUIStyle LabelClip { get { return _labelClip; } }
        public static GUIStyle LabelClipCenter { get { return _labelClipCenter; } }
        public static GUIStyle SecHeaderCenter { get { return _secHeaderCenter; } }
        public static GUIStyle LabelWrap { get { return _labelWrap; } }
        public static GUIStyle ChatLabel { get { return _chatLabel; } }
        public static GUIStyle ChatArea { get { return _chatArea; } }
        public static GUIStyle SecHeader { get { return _secHeader; } }
        public static GUIStyle SelItem { get { return _selItem; } }
        public static GUIStyle Btn { get { return _btn; } }
        public static GUIStyle BtnClip { get { return _btnClip; } }
        public static GUIStyle CheckOn { get { return _checkOn; } }
        public static GUIStyle CheckOff { get { return _checkOff; } }
        public static GUIStyle SearchBox { get { return _searchBox; } }
        public static GUIStyle Footer { get { return _footer; } }

    }
}
}
