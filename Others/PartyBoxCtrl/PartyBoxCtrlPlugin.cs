// ============================================================================
// File   : PartyBoxCtrlPlugin.cs
// Assembly: PartyBoxCtrl.dll
// Purpose: Standalone BepInEx plugin (English only). Adds four hotkeys:
//            Object   / Drop Held Piece   (default Delete)
//            PartyBox / Refresh Party Box (default F9,  host only)
//            PartyBox / Refresh Pieces    (default F10, host only)
//            PartyBox / Close Party Box   (default F11, host only)
// ============================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.Networking;

namespace PartyBoxCtrl {
    [BepInPlugin(Guid, "PartyBoxCtrl", "1.0.0")]
    public class PartyBoxCtrlPlugin : BaseUnityPlugin {
        public const string Guid = "com.rs.PartyBoxCtrl";

        private ConfigEntry<KeyCode> _dropPiece;
        private ConfigEntry<KeyCode> _refreshBox;
        private ConfigEntry<KeyCode> _refreshPieces;
        private ConfigEntry<KeyCode> _closeBox;

        private static PartyBoxCtrlPlugin _self;
        private static ChatDisplay _chat;   // cached: it is DontDestroyOnLoad, so once is enough

        private void Awake() {
            _self = this;
            _dropPiece = Config.Bind("Object", "Drop Held Piece", KeyCode.Delete,
                "Drop the piece you are holding during the queue phase. Uses the game's own channel, so everyone sees it. None = unbound.");
            _refreshBox = Config.Bind("PartyBox", "Refresh Party Box", KeyCode.F9,
                "Host only. Reroll the items with this round's weights and reopen the box so everyone can pick again.");
            _refreshPieces = Config.Bind("PartyBox", "Refresh Pieces", KeyCode.F10,
                "Host only. Reroll only the items in the party box; no reopen and no phase change.");
            _closeBox = Config.Bind("PartyBox", "Close Party Box", KeyCode.F11,
                "Host only. End the party-box selection and enter the play phase (native phase change, synced).");
        }

        private void Update() {
            try {
                if (IsTyping()) return;
                if (Down(_dropPiece)) Actions.DropHeldPiece();
                if (Down(_refreshBox)) Actions.RefreshPartyBox();
                if (Down(_refreshPieces)) Actions.RefreshPieces();
                if (Down(_closeBox)) Actions.ClosePartyBox();
            } catch (Exception e) { Log("hotkey check failed: " + e.Message); }
        }

        // True while the game's chat input is active: nothing should fire there,
        // otherwise Delete would drop a piece while editing text.
        private static bool IsTyping() {
            try {
                if (_chat == null) _chat = UnityEngine.Object.FindObjectOfType<ChatDisplay>();
                return _chat != null && _chat.ChatMode;
            } catch { return false; }
        }

        private static bool Down(ConfigEntry<KeyCode> entry) {
            if (entry == null) return false;
            KeyCode k = entry.Value;
            return k != KeyCode.None && Input.GetKeyDown(k);
        }

        // Corner message, same call the game uses for its own toasts.
        internal static void Tip(string text) {
            Log(text);
            try {
                UserMessageManager m = UserMessageManager.Instance;
                if (m != null) m.UserMessage(text, false);
            } catch { }
        }

        internal static void Log(string text) {
            try { if (_self != null) _self.Logger.LogInfo("[PartyBoxCtrl] " + text); } catch { }
        }

        internal static void Warn(string text) {
            try { if (_self != null) _self.Logger.LogWarning("[PartyBoxCtrl] " + text); } catch { }
        }

        // The three party-box actions only exist on the server (item spawning and
        // destruction are broadcast from there), so they are host-only by nature.
        internal static bool HostOnly() {
            if (UnityEngine.Networking.NetworkServer.active) return true;
            Tip("Host only");
            return false;
        }
    }

    // The four actions. Logic mirrors what the game itself does; private game
    // members are reached with Harmony's AccessTools.
    internal static class Actions {
        private static MethodInfo M(Type owner, string name) {
            try { return HarmonyLib.AccessTools.Method(owner, name); } catch { return null; }
        }
        private static FieldInfo F(Type owner, string name) {
            try { return HarmonyLib.AccessTools.Field(owner, name); } catch { return null; }
        }

        // Drop the piece in hand. The game's own path is:
        //   CallCmdClearPiece (public [Command], client -> server) -> CmdClearPiece
        //   -> RpcClearPiece (everyone: Disable + DestroySelf + SetPiece(null)).
        // RpcClearPiece alone is NOT enough: the native "skip" message
        // MsgPiecePlaced{PieceID = 0} is what zeroes RemainingPlacements on the
        // server and hides the cursor. Without it the match stays stuck in the
        // place phase, which looks like "the selection cursor never goes away".
        public static void DropHeldPiece() {
            try {
                PiecePlacementCursor c = FindCursor();
                if (c == null) { PartyBoxCtrlPlugin.Tip("Placement cursor not found"); return; }
                if (c.Piece == null) { PartyBoxCtrlPlugin.Tip("No piece in hand"); return; }
                int myNumber = c.networkNumber;

                bool sent = false;
                MethodInfo cmd = M(typeof(PiecePlacementCursor), "CallCmdClearPiece");
                if (cmd != null) {
                    try { cmd.Invoke(c, NoArgs(cmd)); sent = true; }
                    catch (Exception e) { PartyBoxCtrlPlugin.Warn("CallCmdClearPiece failed: " + e.Message); }
                }
                if (!sent) {
                    // Fallback for host / offline: run the local half of the chain.
                    MethodInfo clear = M(typeof(PiecePlacementCursor), "ClearCurrentPiece");
                    if (clear == null) { PartyBoxCtrlPlugin.Tip("Failed to drop the piece"); return; }
                    try { clear.Invoke(c, NoArgs(clear)); }
                    catch (Exception e) { PartyBoxCtrlPlugin.Warn("ClearCurrentPiece failed: " + e.Message); return; }
                }

                try {
                    MsgPiecePlaced skip = new MsgPiecePlaced();
                    skip.PlayerNumber = myNumber;
                    skip.PiecePosition = Vector3.zero;
                    skip.PieceScale = Vector3.zero;
                    skip.PieceRotation = Quaternion.identity;
                    skip.PieceID = 0;
                    LobbyManager.instance.client.Send(NetMsgTypes.PiecePlaced, skip);
                } catch (Exception e) { PartyBoxCtrlPlugin.Warn("skip report failed: " + e.Message); }

                PartyBoxCtrlPlugin.Tip("Piece dropped");
            } catch (Exception e) { PartyBoxCtrlPlugin.Warn("DropHeldPiece failed: " + e.Message); }
        }

        // The local placement cursor. CursorInstance lives on LobbyPlayer (not on
        // Player), so go through LobbyManager.lobbySlots and pick the local one.
        private static PiecePlacementCursor FindCursor() {
            try {
                LobbyManager lm = LobbyManager.instance;
                if (lm != null && lm.lobbySlots != null) {
                    UnityEngine.Networking.NetworkLobbyPlayer[] slots = lm.lobbySlots;
                    for (int i = 0; i < slots.Length; i++) {
                        LobbyPlayer lp = slots[i] as LobbyPlayer;
                        if (lp == null || !lp.IsLocalPlayer) continue;
                        PiecePlacementCursor ppc = lp.CursorInstance as PiecePlacementCursor;
                        if (ppc != null) return ppc;
                    }
                }
                PiecePlacementCursor[] all = UnityEngine.Object.FindObjectsOfType<PiecePlacementCursor>();
                if (all != null && all.Length > 0) return all[0];
            } catch (Exception e) { PartyBoxCtrlPlugin.Warn("FindCursor failed: " + e.Message); }
            return null;
        }

        private static object[] NoArgs(MethodInfo m) {
            try { return m.GetParameters().Length == 0 ? new object[0] : new object[m.GetParameters().Length]; }
            catch { return new object[0]; }
        }

        // -------------------------------------------------------------- PartyBox
        // Full reroll: walk the game's own party-box flow again.
        //   showPartyBox() = SetupPartyBoxForRound(true) + ShowBox(IsSecondBox)  (private)
        //   CallRpcShowPartyBox() makes every client run the same local half.
        public static void RefreshPartyBox() {
            try {
                VersusControl vc = UnityEngine.Object.FindObjectOfType<VersusControl>();
                if (vc == null) { PartyBoxCtrlPlugin.Tip("Not in a party match"); return; }
                PartyBox pb = UnityEngine.Object.FindObjectOfType<PartyBox>();
                if (pb == null) { PartyBoxCtrlPlugin.Tip("Party box not found"); return; }
                if (!PartyBoxCtrlPlugin.HostOnly()) return;

                int oldPieces = DestroyPieces(pb);
                MethodInfo show = M(typeof(VersusControl), "showPartyBox");
                if (show != null) {
                    show.Invoke(vc, NoArgs(show));
                    try { vc.CallRpcShowPartyBox(); }
                    catch (Exception e) { PartyBoxCtrlPlugin.Warn("CallRpcShowPartyBox failed: " + e.Message); }
                    PartyBoxCtrlPlugin.Tip("Rerolled the items and reopened the box");
                } else {
                    RerollFallback(pb, oldPieces);
                }
                ForcePlacePhase(vc);
            } catch (Exception e) { PartyBoxCtrlPlugin.Warn("RefreshPartyBox failed: " + e.Message); }
        }

        // Reroll only the items; the box stays open and the phase is untouched.
        public static void RefreshPieces() {
            try {
                VersusControl vc = UnityEngine.Object.FindObjectOfType<VersusControl>();
                if (vc == null) { PartyBoxCtrlPlugin.Tip("Not in a party match"); return; }
                PartyBox pb = UnityEngine.Object.FindObjectOfType<PartyBox>();
                if (pb == null) { PartyBoxCtrlPlugin.Tip("Party box not found"); return; }
                if (!PartyBoxCtrlPlugin.HostOnly()) return;

                int oldPieces = DestroyPieces(pb);
                int amount = oldPieces > 0 ? oldPieces : 1;
                MethodInfo choose = M(typeof(PartyBox), "ChoosePieces");
                if (choose != null) {
                    choose.Invoke(pb, new object[] { amount, null });
                    PartyBoxCtrlPlugin.Tip("Rerolled the party box items (" + amount + ")");
                } else {
                    PartyBoxCtrlPlugin.Tip("Reroll failed");
                    PartyBoxCtrlPlugin.Warn("ChoosePieces not available");
                }
            } catch (Exception e) { PartyBoxCtrlPlugin.Warn("RefreshPieces failed: " + e.Message); }
        }

        // Close the box by replaying the native chain, never by touching nextPhase:
        //   broadcast PartyBoxOpen{false} -> Hide() -> the box's own close event
        //   leads to SetupPlacementCursors (that is what puts pickers into the
        //   place phase). Hide() never disables the in-box pick cursors, so the
        //   ones that were never used have to be closed here.
        public static void ClosePartyBox() {
            try {
                VersusControl vc = UnityEngine.Object.FindObjectOfType<VersusControl>();
                if (vc == null) { PartyBoxCtrlPlugin.Tip("Not in a party match"); return; }
                PartyBox pb = UnityEngine.Object.FindObjectOfType<PartyBox>();
                if (pb == null) { PartyBoxCtrlPlugin.Tip("Party box not found"); return; }
                if (!PartyBoxCtrlPlugin.HostOnly()) return;

                try {
                    MsgPartyBoxOpen m = new MsgPartyBoxOpen();
                    m.IsOpen = false;
                    UnityEngine.Networking.NetworkServer.SendToAll(NetMsgTypes.PartyBoxOpen, m);
                } catch (Exception e) { PartyBoxCtrlPlugin.Warn("broadcast close failed: " + e.Message); }

                MethodInfo hide = M(typeof(PartyBox), "Hide");
                if (hide != null) {
                    try { hide.Invoke(pb, new object[] { false }); }
                    catch (Exception e) { PartyBoxCtrlPlugin.Warn("Hide failed: " + e.Message); }
                } else {
                    try {
                        PropertyInfo vis = typeof(UIGraphic).GetProperty("Visible",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                        if (vis != null && vis.CanWrite) vis.SetValue(pb, false, null);
                        else {
                            FieldInfo vf = typeof(UIGraphic).GetField("visible",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                            if (vf != null) vf.SetValue(pb, false);
                        }
                    } catch (Exception e) { PartyBoxCtrlPlugin.Warn("hide fallback failed: " + e.Message); }
                    try { if (pb.boxAnimator != null) pb.boxAnimator.SetBool("BoxOpen", false); }
                    catch (Exception e) { PartyBoxCtrlPlugin.Warn("animator fallback failed: " + e.Message); }
                }

                int closed = ClosePickCursors(pb);
                if (closed > 0) PartyBoxCtrlPlugin.Log("closed " + closed + " unused pick cursor(s)");

                // If nobody ever picked a piece there is no place phase to enter;
                // calling SetupPlacementCursors would spawn cursors out of nowhere.
                if (!AnyoneHasPiece()) {
                    PartyBoxCtrlPlugin.Tip("Party box closed (nobody picked a piece)");
                    return;
                }
                // Pass false: true would run WaitForForcedPiecesAndSetupCursors,
                // which waits for forced pieces that do not exist here and spins forever.
                MethodInfo rpc = M(typeof(VersusControl), "CallRpcSetupPlacementCursors");
                if (rpc != null) {
                    try { rpc.Invoke(vc, new object[] { false }); }
                    catch (Exception e) { PartyBoxCtrlPlugin.Warn("SetupPlacementCursors failed: " + e.Message); }
                } else {
                    PartyBoxCtrlPlugin.Warn("CallRpcSetupPlacementCursors not available, local only");
                }
                PartyBoxCtrlPlugin.Tip("Party box closed");
            } catch (Exception e) { PartyBoxCtrlPlugin.Warn("ClosePartyBox failed: " + e.Message); }
        }

        // Shared helpers --------------------------------------------------------

        // PartyBox.Hide() never disables the in-box PartyPickCursor objects (only
        // the "player left" path does), so closing early leaves them on screen.
        private static int ClosePickCursors(PartyBox pb) {
            int n = 0;
            try {
                FieldInfo cf = F(typeof(PartyBox), "cursors");
                IList arr = cf != null ? cf.GetValue(pb) as IList : null;
                if (arr != null) {
                    for (int i = 0; i < arr.Count; i++) {
                        try {
                            PartyPickCursor pc = arr[i] as PartyPickCursor;
                            if (pc != null && pc.Enabled) { pc.Disable(); n++; }
                        } catch { }
                    }
                }
                if (n == 0) {
                    PartyPickCursor[] all = UnityEngine.Object.FindObjectsOfType<PartyPickCursor>();
                    if (all != null) {
                        for (int i = 0; i < all.Length; i++) {
                            try { if (all[i] != null && all[i].Enabled) { all[i].Disable(); n++; } } catch { }
                        }
                    }
                }
            } catch (Exception e) { PartyBoxCtrlPlugin.Warn("ClosePickCursors failed: " + e.Message); }
            return n;
        }

        private static bool AnyoneHasPiece() {
            try {
                PiecePlacementCursor[] all = UnityEngine.Object.FindObjectsOfType<PiecePlacementCursor>();
                if (all == null) return false;
                for (int i = 0; i < all.Length; i++) {
                    if (all[i] != null && all[i].Piece != null) return true;
                }
            } catch (Exception e) { PartyBoxCtrlPlugin.Warn("AnyoneHasPiece failed: " + e.Message); }
            return false;
        }

        // Old items must be destroyed through the server, otherwise clients keep
        // their stale copies. Returns how many were removed.
        private static int DestroyPieces(PartyBox pb) {
            int count = 0;
            try {
                FieldInfo f = F(typeof(PartyBox), "pieces");
                IList list = f != null ? f.GetValue(pb) as IList : null;
                if (list == null) return 0;
                List<GameObject> dead = new List<GameObject>();
                foreach (object o in list) {
                    Component c = o as Component;
                    if (c != null && c.gameObject != null) dead.Add(c.gameObject);
                }
                for (int i = 0; i < dead.Count; i++) {
                    try { UnityEngine.Networking.NetworkServer.Destroy(dead[i]); }
                    catch (Exception e) { PartyBoxCtrlPlugin.Warn("destroy old item failed: " + e.Message); }
                }
                list.Clear();
                count = dead.Count;
            } catch (Exception e) { PartyBoxCtrlPlugin.Warn("DestroyPieces failed: " + e.Message); }
            return count;     
        }

        // Fallback when showPartyBox is not found (game update): do the same two
        // steps by hand - reroll by count, then reopen + broadcast.
        private static void RerollFallback(PartyBox pb, int amount) {
            try {
                if (amount <= 0) amount = 1;
                MethodInfo choose = M(typeof(PartyBox), "ChoosePieces");
                if (choose != null) choose.Invoke(pb, new object[] { amount, null });
                ReopenPartyBox(pb);
                PartyBoxCtrlPlugin.Tip("Rerolled the party box items");
            } catch (Exception e) { PartyBoxCtrlPlugin.Warn("RerollFallback failed: " + e.Message); }
        }

        private static void ReopenPartyBox(PartyBox pb) {
            try {
                if (pb == null) return;
                bool extra = false;
                try {
                    VersusControl vc = UnityEngine.Object.FindObjectOfType<VersusControl>();
                    FieldInfo f = vc != null ? F(typeof(VersusControl), "IsSecondBox") : null;
                    if (f != null) extra = (bool)f.GetValue(vc);
                } catch { }
                try { pb.ShowBox(extra); }
                catch (Exception e) { PartyBoxCtrlPlugin.Warn("ShowBox failed: " + e.Message); }
                try {
                    if (UnityEngine.Networking.NetworkServer.active) {
                        MsgPartyBoxOpen msg = new MsgPartyBoxOpen();
                        msg.IsOpen = true;
                        msg.isExtraBox = extra;
                        UnityEngine.Networking.NetworkServer.SendToAll(NetMsgTypes.PartyBoxOpen, msg);
                    }
                } catch (Exception e) { PartyBoxCtrlPlugin.Warn("broadcast reopen failed: " + e.Message); }
            } catch (Exception e) { PartyBoxCtrlPlugin.Warn("ReopenPartyBox failed: " + e.Message); }
        }

        // Opening the box while the game is not in the place phase leaves the items
        // selectable but unplaceable, so nudge the game into its own place phase
        // (ToPlaceMode carries the phase event and the RPCs, so guests follow).
        private static void ForcePlacePhase(VersusControl vc) {
            try {
                if (vc == null) return;
                MethodInfo toPlace = M(typeof(VersusControl), "ToPlaceMode");
                if (toPlace != null) {
                    try { toPlace.Invoke(vc, NoArgs(toPlace)); return; }
                    catch (Exception e) { PartyBoxCtrlPlugin.Warn("ToPlaceMode failed, falling back: " + e.Message); }
                }
                FieldInfo np = F(typeof(GameControl), "nextPhase");
                if (np != null) {
                    object cur = np.GetValue(vc);
                    if (cur == null || cur.ToString() != "PLACE") np.SetValue(vc, Enum.Parse(np.FieldType, "PLACE"));
                }
                try {
                    MethodInfo setup = M(typeof(VersusControl), "SetupPlacementCursors");
                    if (setup != null) setup.Invoke(vc, NoArgs(setup));
                    MethodInfo rpc = M(typeof(VersusControl), "CallRpcSetupPlacementCursors");
                    if (rpc != null) {
                        ParameterInfo[] ps = rpc.GetParameters();
                        object[] args = ps.Length == 1
                            ? new object[] { ps[0].ParameterType.IsValueType ? Activator.CreateInstance(ps[0].ParameterType) : null }
                            : new object[0];
                        rpc.Invoke(vc, args);
                    }
                } catch (Exception e) { PartyBoxCtrlPlugin.Warn("cursor sync failed: " + e.Message); }
            } catch (Exception e) { PartyBoxCtrlPlugin.Warn("ForcePlacePhase failed: " + e.Message); }
        }
    }
}
