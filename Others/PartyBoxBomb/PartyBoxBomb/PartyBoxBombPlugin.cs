using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;

namespace PartyBoxBomb {
    //============================================================
    // Party Box Bomb -- standalone plugin (English only, no SR_UCH
    // dependency). Host-only.
    //  When EVERY member of the lobby has sent the "Bomb!" quick-phrase
    //  (recognized by the EmoteMeanings.EMOTE_Bomb code, so it is
    //  language-independent), the host spawns one pickable bomb into the
    //  current party box.
    //  Bomb type: 0 = Small (99abombmini) / 1 = Big (99bomb) / 2 = Mega.
    //  Configure in the BepInEx config file / Config Manager (F1).
    //  Do NOT run together with SR_UCH's built-in party-box bomb.
    //============================================================
    [BepInPlugin("com.gamingbeast.partybox_bomb", "Party Box Bomb", "1.0.0")]
    public class PartyBoxBombPlugin : BaseUnityPlugin {
        public static ManualLogSource ModLogger;

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<int> _bombType;
        // members that already sent the Bomb quick-phrase
        private static readonly HashSet<int> _sent = new HashSet<int>();

        private void Awake() {
            ModLogger = Logger;
            _enabled = Config.Bind(
                "Party Box Bomb", "Enabled", false,
                "Host only: when EVERY member sends the \"Bomb!\" quick-phrase, spawn a bomb in the current party box.");
            _bombType = Config.Bind(
                "Party Box Bomb", "Bomb Type", 0,
                "Bomb size to spawn: 0 = Small (99abombmini), 1 = Big (99bomb), 2 = Mega (99bbombmega).");
            Harmony.CreateAndPatchAll(typeof(PartyBoxBombPlugin));
            ModLogger.LogInfo("[Party Box Bomb] loaded");
        }

        static bool Enabled { get { return _enabled != null && _enabled.Value; } }
        static int BombType { get { return _bombType != null ? Mathf.Clamp(_bombType.Value, 0, 2) : 0; } }

        //Quick-phrases arrive via ChatDisplay.DisplayNewMessage; EmoteType keeps the enum code.
        [HarmonyPatch(typeof(ChatDisplay), "DisplayNewMessage", new Type[] { typeof(ChatMessageDetails) })]
        [HarmonyPrefix]
        static void OnEmote(ChatMessageDetails chatMessageDetails) {
            try {
                if (!Enabled) return;
                if (chatMessageDetails.EmoteType != EmoteMeanings.EMOTE_Bomb) return;
                if (!NetworkServer.active) return; //host only
                List<int> online = OnlineNumbers();
                if (online.Count == 0) return;
                _sent.Add(chatMessageDetails.NetworkNumber);
                bool all = true;
                foreach (int n in online) { if (!_sent.Contains(n)) { all = false; break; } }
                if (!all) {
                    ModLogger.LogInfo("[Party Box Bomb] " + _sent.Count + "/" + online.Count + " members sent Bomb");
                    return;
                }
                _sent.Clear();
                ModLogger.LogInfo("[Party Box Bomb] all members sent Bomb -> spawn bomb (type " + BombType + ")");
                AddBombToPartyBox();
            } catch (Exception e) {
                ModLogger.LogWarning("[Party Box Bomb] detect error: " + e.Message);
            }
        }

        static List<int> OnlineNumbers() {
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
            if (list.Count == 0) {
                try {
                    foreach (Player p in PlayerManager.GetInstance()) {
                        if (p == null || p.PlayerCharacter == null) continue;
                        int n = p.PlayerCharacter.networkNumber;
                        if (!list.Contains(n)) list.Add(n);
                    }
                } catch { }
            }
            return list;
        }

        static void AddBombToPartyBox() {
            try {
                PartyBox pb = UnityEngine.Object.FindObjectOfType<PartyBox>();
                if (pb == null) { ModLogger.LogWarning("[Party Box Bomb] no party box"); return; }
                if (pb.BombPrefab == null || pb.BombPrefab.Length == 0) { ModLogger.LogWarning("[Party Box Bomb] no bomb prefabs"); return; }
                Placeable bomb = PickBombPlaceable(pb, BombType);
                if (bomb == null || bomb.PickableBlock == null) { ModLogger.LogWarning("[Party Box Bomb] no PickableBlock"); return; }

                var f = HarmonyLib.AccessTools.Field(typeof(PartyBox), "pieces");
                if (f == null) return;
                IList pieces = f.GetValue(pb) as IList;
                if (pieces == null) return;

                PickableBlock piece = UnityEngine.Object.Instantiate(bomb.PickableBlock);
                pieces.Add(piece);
                piece.transform.SetParent(pb.transform, false);
                try { piece.ChangeArtLayer("Background 2"); } catch { }
                foreach (Transform t in piece.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 5;
                float ang = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float rad = Mathf.Max(0.1f, pb.PlacementRadius * 0.8f);
                piece.transform.localPosition = new Vector3(Mathf.Cos(ang) * rad, Mathf.Sin(ang) * rad, 0f);
                piece.NetworkUseStartPosition = true;
                piece.FindPartyBox = true;
                try { piece.setInitialScale(GameSettings.GetInstance().partyBoxItemScale); } catch { }
                piece.InPartybox = true;
                piece.Enable();
                NetworkServer.Spawn(piece.gameObject);
                ModLogger.LogInfo("[Party Box Bomb] spawned " + piece.name);
            } catch (Exception e) {
                ModLogger.LogWarning("[Party Box Bomb] spawn error: " + e.Message);
            }
        }

        static Placeable PickBombPlaceable(PartyBox pb, int size) {
            Placeable[] arr = pb.BombPrefab;
            for (int i = 0; i < arr.Length; i++) {
                if (arr[i] == null) continue;
                string n = arr[i].name.ToLowerInvariant();
                if (size == 0 && n.Contains("mini")) return arr[i];
                if (size == 2 && n.Contains("mega")) return arr[i];
            }
            for (int i = 0; i < arr.Length; i++) {
                if (arr[i] == null) continue;
                string n = arr[i].name.ToLowerInvariant();
                if (size == 1 && n.Contains("bomb") && !n.Contains("mini") && !n.Contains("mega")) return arr[i];
            }
            int idx = Mathf.Clamp(size, 0, arr.Length - 1);
            return arr[idx];
        }
    }
}
