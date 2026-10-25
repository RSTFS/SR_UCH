// 会话 · 对话内容页：全量聊天记录、过滤、文本与高度缓存、顶部工具行、底部发送行。
// 栏目总览见 Chat.cs。

using System;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace SR_UCH.Tweaks {
[SrFeature("sr.chat", "Chat", "1.0")]
public partial class Chat : ITweak {
		public class ChatEntry {
			public string time;
			public string sender;
			public Color color;
			public string text;
			public bool isQuick; 
		}

		private static readonly List<ChatEntry> _log = new List<ChatEntry>();

		public static List<ChatEntry> Entries { get { return _log; } }

		private static ConfigEntry<bool> _chatFilterQuickEntry;
		private static bool _chatFilterQuick;   //过滤快捷消息（表情/预设消息）
		private static ConfigEntry<bool> _chatShowTimeEntry;
		// 聊天记录保留条数，超出后从最旧的开始淘汰。
		private static ConfigEntry<int> _chatLogLimitEntry;
		// 取值必须夹紧在 100-5000：AcceptableValueRange 只约束 UI，配置文件被手改成越界值时会导致聊天记录恒被清空。
		private static int LogLimit() {
			try { if (_chatLogLimitEntry != null) return Mathf.Clamp(_chatLogLimitEntry.Value, 100, 5000); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
			return 500;
		}
		private static bool _chatShowTime = true; //每条消息前显示具体时间
		private static string _chatTextCache;   //消息文本缓存（每秒重建一次，避免每帧拼接）
		// 文本是否已构建过：全被过滤时 BuildChatText 返回 null，不能用"缓存为 null"表示尚未构建。
		private static bool _chatBuilt;
		// 高度缓存：CalcHeight 是对全部消息的全量排版测量，文本每秒才重建一次，无需每帧重算。
		// _chatHeightW 记录计算时所用宽度，宽度变化即失效重算。
		private static float _chatHeightCache;
		private static float _chatHeightW = -1f;
		private static float _chatTextTimer;
		private static int _lastChatCount;      //上次渲染的条目数（新消息检测，见自动滚动状态机）
		private static int _chatTickFrame = -1; //计时器上次递减的帧号（按帧，不按 OnGUI 事件）
		private static bool _chatCacheFilter, _chatCacheShowTime;
		private static string _chatInputText = ""; //底部发送框内容

		// ---- 对话内容页自动滚动（⑥）----
		// 页面级滚动位置由 SR 的页面滚动区持有（SRApi.PageScroll / SRApi.Ctl.ScrollToBottom），
		// 这里只读位置、必要时请求滚到底，不取用滚轮事件（滚轮仍归页面滚动区，不吞其它控件的滚轮）。
		private static bool _autoScroll = true;   //跟随最新消息（初始 true）
		private static bool _hasNew;              //回看状态下来过新消息（显示提示条）
		private static int _newCount;             //回看期间累计的新消息条数（提示条用）
		// 底部基准：请求滚到底后，下一帧滚动区会把 y 夹紧成真实底部，记下当时的
		// (滚动值, 内容高度, 内容区高度, 提示条高度)；之后内容变多 / 窗口变矮 / 提示条出现时按差值推算新底部。
		private static bool _hasBottom;
		private static float _bottomY, _bottomH, _bottomAreaH, _bottomHintH;
		private static bool _forcePrev;           //上一帧请求过滚到底，本帧读回夹紧值作为底部基准
		private static float _lastContentH = -1f, _lastAreaH = -1f, _lastY = -1f, _lastHintH = -1f;
		private static float _hintRowH;           //提示条占用高度（画在滚动区下方，会让可视区变矮）
		private static int _scrollFrame = -1;     //滚动状态机上次运行帧（每帧只跑一次）
		private static int _pageFrame = -1;       //本页上次渲染帧（换页 / 重开检测）
		private static int _wheelFrame = -1;      //最近一次落在本页的滚轮事件帧
		private static bool _wheelUp;             //该滚轮是否向上（Unity delta.y 向下滚为正）
		private static float _scaleSeen = -1f;    //界面缩放快照（缩放变化时底部基准失效）
		private static bool _scrollErr;           //异常只记一次日志，避免每帧刷日志

		public static void Clear() {
			_log.Clear();
			ChatFollowReset();   //清空后回到跟随态：下一条新消息会立刻贴底
		}

		// 从本页发送聊天，走游戏原生 ChatSent 消息。
		public static void SendChatText(string text) {
			if (string.IsNullOrEmpty(text)) return;
			try {
				int num = LocalNetworkNumber();
				if (num < 0) return; 
				if (GameState.ChatSystem != null) {
					GameState.ChatSystem.NewChatMessage(text, EmoteMeanings.CHAT_Text, num);
					// 自己刚发的消息：即使正在回看也直接回到底部；仅在对话内容页可见时动页面滚动，
					// 避免把别的页面/栏目的页面滚动位置也改掉。
					if (SRApi.SelectedPageId("Chat") == "ChatLog") { _autoScroll = true; ChatScrollBottom(); }
				}
			} catch (Exception e) {
				SRApi.LogWarn("Chat: send failed: " + e.Message);
			}
		}

		private static int LocalNetworkNumber() {
			try {
				if (LobbyManager.instance == null) return -1;
				NetworkLobbyPlayer[] slots = LobbyManager.instance.lobbySlots;
				if (slots == null) return -1;
				for (int i = 0; i < slots.Length; i++) {
					LobbyPlayer lp = slots[i] as LobbyPlayer;
					if (lp != null && lp.LocalPlayer != null) return lp.networkNumber;
				}
			} catch (Exception __ex) { SRApi.Guard.Log("查找本地玩家 networkNumber", __ex); }
			return -1;
		}

		internal static void SelfRegLog() {
			SRApi.RegisterPage("Chat", "ChatLog", "对话内容", "ChatLog", 20, Render); //会话栏目第 2 页
			// 顶部工具行与底部输入行必须画在滚动区之外（不随内容滚动），故注册为页面外框部件。
			SRApi.RegisterPageChrome("Chat", RenderToolbar, RenderInputRow);
			// 两个开关的本地化文案（配置在本文件 Bind 到 Chat 段）。
			SRApi.LocKey("Chat", "Filter Quick Msgs", "过滤快捷消息", null);
			SRApi.LocDesc("Chat", "Filter Quick Msgs", "快捷消息过滤开关\n从聊天记录中隐藏表情与预设消息，只保留玩家输入\n仅影响聊天记录显示，不改变游戏聊天内容。", "quick-message filter\nhides emotes and preset messages from the chat log, keeping player-typed text only\ndisplay only; the game chat content is unchanged");
			SRApi.LocKey("Chat", "Show Time", "显示时间", null);
			SRApi.LocDesc("Chat", "Show Time", "时间戳显示开关\n在每条聊天记录前显示时间\n仅作用于本模组的聊天记录面板", "timestamp switch\nshows the time before each message in the chat log\napplies to this mod chat log panel only");
			// 预登记本页自动快捷键：不预登记则重启后在首次打开本页前按键无效。
			SRApi.HotkeyAction("chat.clear", "清空", () => { Clear(); _chatTextCache = null; _chatBuilt = false; });
			SRApi.HotkeyAction("chat.copy", "复制", () => { try { GUIUtility.systemCopyBuffer = BuildChatText(Entries) ?? ""; } catch (System.Exception __ex) { SRApi.Guard.Log("复制聊天记录", __ex); } });
			SRApi.HotkeyAction("chat.send", "发送", () => { SendChatText(_chatInputText); _chatInputText = ""; });
		}

		private static string BuildChatText(List<ChatEntry> entries) {
			StringBuilder sb = new StringBuilder();
			bool any = false;
			for (int i = 0; i < entries.Count; i++) {
				ChatEntry ce = entries[i];
				if (_chatFilterQuick && ce.isQuick) continue;
				any = true;
				if (_chatShowTime) {
					sb.Append(ce.time).Append(' ');
				}
				// 名字与正文之间用不换行空格，避免宽度不足时在 ">" 后折行。
				sb.Append('<').Append(ce.sender).Append(">\u00A0").Append(ce.text).Append('\n');
			}
			return any ? sb.ToString() : null;
		}

		public static void Render() {
			// 记录本帧滚轮方向：只观察不取用 —— 滚轮仍由 SR 的页面滚动区处理，避免吃掉其它控件的滚轮。
			Event ev = Event.current;
			if (ev != null && ev.type == EventType.ScrollWheel) {
				_wheelFrame = Time.frameCount;
				_wheelUp = (Mathf.Abs(ev.delta.y) > 0.001f ? ev.delta.y : ev.delta.x) < 0f; //Unity delta.y 向下滚为正
			}
			// 大型滚动区显示全部记录；文本每秒重建一次跟随新消息，避免每帧拼接字符串。
			List<ChatEntry> entries = Entries;
			// Render 每帧被 Layout/Repaint 及输入事件调用多次，计时器必须按帧递减，否则每秒会重建多次。
			if (_chatTickFrame != Time.frameCount) {
				_chatTickFrame = Time.frameCount;
				_chatTextTimer -= Time.unscaledDeltaTime;
			}
			if (_chatTextTimer <= 0f || !_chatBuilt
				|| _chatCacheFilter != _chatFilterQuick || _chatCacheShowTime != _chatShowTime) {
				_chatTextTimer = 1f;
				_chatCacheFilter = _chatFilterQuick;
				_chatCacheShowTime = _chatShowTime;
				_chatTextCache = BuildChatText(entries);
				_chatBuilt = true;
				_chatHeightW = -1f; //文本变了，高度作废
			}
			if (entries.Count == 0) {
				ChatFollowReset(); //无内容：回到跟随态
				GUILayout.Label(SRApi.T("（暂无消息）", "(no messages)"), SRApi.Ctl.Label);
			} else if (string.IsNullOrEmpty(_chatTextCache)) {
				ChatFollowReset(); //全部被过滤：没有可滚动内容
				GUILayout.Label(SRApi.T("（已过滤全部消息）", "(all messages filtered)"), SRApi.Ctl.Label);
			} else {
				float w = Mathf.Max(SRApi.Ctl.Sc(140), SRApi.Ctl.WinWidth - SRApi.Ctl.SidebarWidth() - SRApi.Ctl.Sc(36));
				float innerW = w - SRApi.Ctl.Sc(20);
				// 文本直接放外层滚动区，避免文本框与外层内容区双滚动条。
				// 高度按文本 + 宽度缓存，其余帧复用。
				if (_chatHeightW != innerW || _chatHeightCache <= 0f) {
					_chatHeightW = innerW;
					_chatHeightCache = SRApi.Ctl.ChatLabel.CalcHeight(new GUIContent(_chatTextCache), innerW) + SRApi.Ctl.Sc(8);
				}
				float th = Mathf.Max(SRApi.Ctl.Sc(200), _chatHeightCache);
				// 自动滚动状态机：跟随态贴底、回看态保持位置（须在绘制前判定，见 ChatAutoScrollTick）。
				ChatAutoScrollTick(entries.Count, th);
				// 只读编辑框：借助 TextArea 获得文本选中与 Ctrl+A / Ctrl+C 复制（游戏本身没有文本选择功能）。
				// 忽略 TextArea 返回的编辑结果，不回写。
				GUILayout.TextArea(_chatTextCache, SRApi.Ctl.ChatArea, GUILayout.Width(innerW), GUILayout.Height(th));
			}
		}

		// 无内容 / 清空时复位：回到跟随态并丢弃底部基准。
		private static void ChatFollowReset() {
			_autoScroll = true;
			_hasNew = false;
			_newCount = 0;
			_hasBottom = false;
			_forcePrev = false;
			_lastContentH = -1f;
			_lastAreaH = -1f;
			_lastY = -1f;
			_lastHintH = -1f;
			_hintRowH = 0f;
			_lastChatCount = 0;
		}

		// 请求滚到底：先把滚动区位置设成最大值，真实底部由滚动区在下一帧夹紧后读回。
		private static void ChatScrollBottom() {
			_hasNew = false;
			_newCount = 0;
			_forcePrev = true;
			try { SRApi.Ctl.ScrollToBottom(); } catch { /* 静默：非关键路径，失败可直接忽略 */ }
		}

		// 对话列表自动滚动状态机（每帧一次）：
		//   跟随态（_autoScroll）→ 新消息 / 内容变化时贴底；用户向上滚离底部 → 转回看态；
		//   回看态 → 新消息不改滚动位置，只亮提示条；用户滚回底部 → 恢复跟随。
		// 滚动值来自页面滚动区，底部靠"请求贴底 → 下一帧读回夹紧值"学习，不依赖滚动区尺寸。
		private static void ChatAutoScrollTick(int count, float contentH) {
			if (_scrollFrame == Time.frameCount) return;
			_scrollFrame = Time.frameCount;
			try {
				float areaH = SRApi.ContentArea.height;
				float y = SRApi.PageScroll.y;
				// 换页 / 关面板后重开：滚动区里留的是上一页的位置，跟随态直接贴底。
				bool reenter = _pageFrame < 0 || Time.frameCount - _pageFrame > 1;
				_pageFrame = Time.frameCount;
				if (reenter) _lastY = -1f;
				// 界面缩放变化：内容与可视区不是同比例缩放，旧底部基准作废（贴底后重建）。
				float scale = SRApi.Ctl.Sc(100f);
				if (_scaleSeen < 0f) _scaleSeen = scale;
				else if (Mathf.Abs(scale - _scaleSeen) > 0.01f) { _scaleSeen = scale; _hasBottom = false; }
				// 上一帧请求过贴底：本帧读回的夹紧值就是真实底部，连同当时的内容高度一起记住。
				// 布局趟可能还没夹紧（返回一个极大的值），这种情况不算学到，保留请求下一帧再读。
				if (_forcePrev) {
					if (y < 1000000f) {
						_forcePrev = false;
						_bottomY = y; _bottomH = contentH; _bottomAreaH = areaH; _bottomHintH = _hintRowH;
						_hasBottom = true;
					}
				}
				// 布局趟的极大滚动值不参与"比上帧更靠上"的判断，否则会被误判成用户上滚。
				if (y >= 1000000f) _lastY = -1f;
				bool contentChanged = _lastContentH >= 0f && Mathf.Abs(contentH - _lastContentH) > 0.5f;
				bool areaChanged = _lastAreaH >= 0f && Mathf.Abs(areaH - _lastAreaH) > 0.5f;
				bool hintChanged = _lastHintH >= 0f && Mathf.Abs(_hintRowH - _lastHintH) > 0.5f;
				_lastContentH = contentH; _lastAreaH = areaH; _lastHintH = _hintRowH;
				bool newMsg = count > _lastChatCount;
				_lastChatCount = count;
				// 底部推算：内容变高 / 窗口内容区变矮 / 提示条占位都会让底部下移。
				float bottom = -1f;
				if (_hasBottom)
					bottom = Mathf.Max(0f, _bottomY + (contentH - _bottomH) - (areaH - _bottomAreaH) + (_hintRowH - _bottomHintH));
				bool layoutChanged = contentChanged || areaChanged || hintChanged;
				bool wheel = _wheelFrame >= Time.frameCount - 1;       //本帧或上一帧有滚轮落在本页
				bool dropped = _lastY >= 0f && y < _lastY - 0.5f;      //比上帧更靠上（用户上滚或布局回弹）
				if (_autoScroll) {
					_hasNew = false; _newCount = 0;
					if (reenter || newMsg || layoutChanged || !_hasBottom) {
						ChatScrollBottom(); //跟随：贴底（首帧 / 新消息 / 尺寸变化 / 首次学习）
					} else if (dropped) {
						_autoScroll = false; //用户向上滚离底部：转回看，保持当前位置
						// 新消息计数由 OnMessage 累加；这里只保证提示条亮起。
						if (newMsg) { _hasNew = true; if (_newCount < 1) _newCount = 1; }
					}
				} else {
					if (newMsg) { _hasNew = true; if (_newCount < 1) _newCount = 1; }
					bool reached;
					if (reenter) reached = false; //刚回到本页：滚动值是别的页面留下的，不据此恢复跟随
					else if (bottom >= 0f) reached = y >= bottom - 1.5f;
					else reached = wheel && !_wheelUp && !dropped && !layoutChanged; //兜底：向下滚但位置不动 = 已在底部
					if (reached) {
						_autoScroll = true;
						ChatScrollBottom();
					}
				}
				_lastY = y >= 1000000f ? -1f : y; //布局趟的极大值不记录，避免下一帧误判成用户上滚
			} catch (Exception __ex) {
				if (!_scrollErr) { _scrollErr = true; SRApi.Guard.Log("聊天记录自动滚动", __ex); }
			}
		}

		// 回看状态下有新消息：滚动区下方显示提示条，点击回到底部并恢复跟随。
		private static void RenderNewMessageHint() {
			if (_autoScroll || !_hasNew) { _hintRowH = 0f; return; }
			float h = SRApi.Ctl.Sc(24);
			_hintRowH = h + SRApi.Ctl.Sc(4);
			GUILayout.Space(SRApi.Ctl.Sc(4));
			GUILayout.BeginHorizontal();
			string hint = SRApi.T("有新消息 ↓ 点击跳到底部", "New messages ↓ click to jump to the bottom");
			if (_newCount > 1) hint += " (" + _newCount + ")";
			if (GUILayout.Button(hint, SRApi.Ctl.Btn, GUILayout.Height(h))) {
				_autoScroll = true;
				ChatScrollBottom();
			}
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
		}

		public static void RenderToolbar() {
			if (SRApi.SelectedPageId("Chat") != "ChatLog") return; //顶栏只属于对话内容页
			GUILayout.BeginHorizontal();
			//清空（可右键绑定快捷键）
			if (SRApi.Ctl.HotkeyButton("chat.clear", SRApi.T("清空", "Clear"), SRApi.T("清空聊天记录", "Clear the chat log"),
				() => { Clear(); _chatTextCache = null; _chatBuilt = false; },
				SRApi.Ctl.Sc(56), SRApi.Ctl.Sc(26))) {
				Clear();
				_chatTextCache = null;
				_chatBuilt = false; //强制重建，不等下一个计时点
			}
			GUILayout.Space(SRApi.Ctl.Sc(4));
			// 复制当前显示的聊天记录（已应用过滤）到剪贴板。
			if (SRApi.Ctl.HotkeyButton("chat.copy", SRApi.T("复制", "Copy"),
				SRApi.T("把当前显示的聊天记录复制到剪贴板（游戏里没有文本选择功能，用这个代替）", "Copy the currently shown chat log to the clipboard"),
				() => { try { GUIUtility.systemCopyBuffer = BuildChatText(Entries) ?? ""; } catch (Exception __ex) { SRApi.Guard.Log("复制聊天记录", __ex); } },
				SRApi.Ctl.Sc(56), SRApi.Ctl.Sc(26))) {
				try { GUIUtility.systemCopyBuffer = BuildChatText(Entries) ?? ""; } catch (Exception __ex) { SRApi.Guard.Log("复制聊天记录", __ex); }
			}
			GUILayout.Space(SRApi.Ctl.Sc(8));
			// 两个开关：标签（左键恢复默认 / 右键绑快捷键）+ 复选框。
			SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T("过滤快捷消息", "Hide quick msgs"), SRApi.T("过滤快捷消息（表情/预设消息不显示）", "Hide quick msgs (emotes/presets)")),
				_chatFilterQuickEntry, SRApi.Ctl.Sc(120), SRApi.Ctl.Sc(26));
			if (_chatFilterQuickEntry != null) SRApi.Ctl.RenderControl(_chatFilterQuickEntry);
			GUILayout.Space(SRApi.Ctl.Sc(16));
			SRApi.Ctl.RestoreLabel(new GUIContent(SRApi.T("显示具体时间", "Show time"), SRApi.T("每条消息前显示具体时间", "Show a timestamp before each message")),
				_chatShowTimeEntry, SRApi.Ctl.Sc(110), SRApi.Ctl.Sc(26));
			if (_chatShowTimeEntry != null) SRApi.Ctl.RenderControl(_chatShowTimeEntry);
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			GUILayout.Space(SRApi.Ctl.Sc(4));
		}

		//底部发送行（固定在滚动区外）：Enter 发送。
		public static void RenderInputRow() {
			if (SRApi.SelectedPageId("Chat") != "ChatLog") return; //输入行只属于对话内容页
			RenderNewMessageHint(); //回看状态有新消息：在输入行上方提示
			GUILayout.Space(SRApi.Ctl.Sc(10));
			GUILayout.BeginHorizontal();
			// Enter / 小键盘 Enter 必须在 TextField 绘制前拦截并 Use()：IMGUI 单行 TextField 会消费
			// KeyDown Return 导致提交并失焦；空文本不发送。
			if (Event.current.type == EventType.KeyDown
				&& (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
				&& _chatInputText.Length > 0) {
				SendChatText(_chatInputText);
				_chatInputText = "";
				Event.current.Use();
			}
			GUI.SetNextControlName("SRUCH_ChatInput");
			_chatInputText = SRApi.ClearableTextField(_chatInputText, SRApi.Ctl.SearchBox, GUILayout.Height(SRApi.Ctl.Sc(26)));
			if (SRApi.Ctl.HotkeyButton("chat.send", SRApi.T("发送", "Send"), SRApi.T("把输入内容作为聊天消息发到游戏聊天里", "Send the text as a chat message"),
				() => { SendChatText(_chatInputText); _chatInputText = ""; },
				SRApi.Ctl.Sc(56), SRApi.Ctl.Sc(26))) {
				SendChatText(_chatInputText);
				_chatInputText = "";
			}
			GUILayout.EndHorizontal();
			GUILayout.Space(SRApi.Ctl.Sc(4));
		}

		internal static void InitLog(IFeatureHost plugin) {
			SelfRegLog();
			// 本功能的配置随本功能绑在 Chat 段。
			_chatFilterQuickEntry = plugin.Config.Bind("Chat", "Filter Quick Msgs", false, "会话内容页：过滤快捷消息（表情/预设消息不显示）");
			_chatFilterQuick = _chatFilterQuickEntry.Value;
			_chatFilterQuickEntry.SettingChanged += (s, e) => _chatFilterQuick = _chatFilterQuickEntry.Value;
			_chatShowTimeEntry = plugin.Config.Bind("Chat", "Show Time", true, "会话内容页：每条消息前显示具体时间。默认开启。");
			// 保留条数可配，默认 500。
			_chatLogLimitEntry = plugin.Config.Bind("Chat", "Log Limit", 500,
				new ConfigDescription("会话内容页保留多少条消息（100-5000，超出后滚动淘汰最旧的）。调大可翻更久的历史。",
					new AcceptableValueRange<int>(100, 5000)));
			_chatShowTime = _chatShowTimeEntry.Value;
			_chatShowTimeEntry.SettingChanged += (s, e) => _chatShowTime = _chatShowTimeEntry.Value;
		}

		[HarmonyPatch(typeof(ChatDisplay), "DisplayNewMessage")]
		[HarmonyPostfix]
		static void OnMessage(object[] __args) {
			// Harmony 补丁不在 SR 页面的 try/catch 覆盖范围内，异常会穿到游戏的网络消息接收路径，故整段兜住只记日志。
			try {
				if (!SRApi.GateMaster) return;
				if (__args == null || __args.Length == 0) return;
				if (!(__args[0] is ChatMessageDetails)) return;
				ChatMessageDetails details = (ChatMessageDetails)__args[0];
				// 按游戏自己的判据区分玩家消息与系统消息：isChatMessage 为假的是系统事件/系统提示。
				if (!details.isChatMessage) return;
				// 系统事件常常没有发送者名（如 PlayerVotedToKick），同样不记录。
				if (string.IsNullOrEmpty(details.UserName)) return;
				bool isQuick = false;
				string text = details.Message;
				if (string.IsNullOrEmpty(text)) {
					if (details.EmoteType == EmoteMeanings.CHAT_Text) return; //空文字消息不记录
					// 快捷消息取游戏内气泡的同一段文字（EmoteConverter -> I2 本地化，随游戏语言变化）。
					text = EmoteSystem.EmoteConverter(details.EmoteType);
					isQuick = true;
				}
				_log.Add(new ChatEntry {
					time = DateTime.Now.ToString("HH:mm:ss"),
					sender = string.IsNullOrEmpty(details.UserName) ? SRApi.T("未知", "Unknown") : details.UserName,
					color = details.UserNameColor,
					text = text,
					isQuick = isQuick
				});
				if (_log.Count > LogLimit()) _log.RemoveAt(0); //保留最近 N 条（条数可配，滚动淘汰）
				// 回看状态下新消息只累计提示条（本页可能没在绘制）；跟随状态由渲染时自动贴底处理。
				if (!_autoScroll) { _hasNew = true; if (_newCount < 999) _newCount++; }
			} catch (Exception __ex) { SRApi.Guard.Log("聊天记录回调", __ex); }
		}
}
}
