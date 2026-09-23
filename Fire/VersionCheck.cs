using System.Collections.Generic;
using FireFront.Utils;
using UnityEngine;

namespace FireFront.Fire
{
    /// <summary>
    /// The connect-time version exchange (0.24). Before it, nothing put FireFront's version on the
    /// wire, and a client and server on different builds failed silently: ignition doing nothing,
    /// fire the server simulated that the client never drew, a payload read with the wrong layout.
    ///
    /// One routed RPC, FireFront_Version(string version, int protocol), in both directions. A client
    /// sends it once the routed channel can reach the server (the same moment the join snapshot is
    /// asked for) and again every 10 s until answered, three times at most. The server answers
    /// every one and compares. Each side then says what it found, in the log and, on a client, to
    /// the player:
    ///  - same version: one Info line;
    ///  - same protocol, different version: a Warn, a console line, a top-left message. They work
    ///    together;
    ///  - different protocol: a Warn, a console line, a centre-screen message. They do not.
    ///
    /// On-screen notices wait for the player's character. The reply arrives within one round trip
    /// of the connection, several seconds before the character spawns (Game.FindSpawnPoint waits
    /// for the area to load, 8 s for a returning character), and a top-left message needs the
    /// local Player to exist while a centre message shown under the loading screen is gone before
    /// anyone sees it. So the log and console lines are written at once and the on-screen notice is
    /// held until the local player exists, plus <see cref="ShowAfterSpawnSeconds"/>. Found by the
    /// 0.24 review before it shipped.
    ///
    /// Two cases the exchange cannot reach from the new side, handled anyway:
    ///  - a server on 0.23 or older has no handler, so the client hears nothing; after three
    ///    unanswered tries it tells the player the server's FireFront is older or missing;
    ///  - a client on 0.23 or older never sends. The server gives every ready peer 60 s, then
    ///    logs it and, once that player's character has spawned (ZNetPeer.m_characterID, set by
    ///    vanilla's CharacterID RPC), tells that client through two channels vanilla registers on
    ///    every client:
    ///    the per-connection "RemotePrint" (a console line) and the routed "ShowMessage" (centre
    ///    screen, MessageHud). No FireFront code is needed on the far side for either.
    ///
    /// <see cref="Plugin.WireProtocol"/> is the compatibility number. It changes when any
    /// FireFront RPC's name, argument list or payload layout changes, and only then.
    /// </summary>
    public static class VersionCheck
    {
        private const int HelloTries = 3;
        private const float HelloRetrySeconds = 10f;
        private const float ServerGraceSeconds = 60f;
        private const float ServerScanSeconds = 5f;
        private const int MaxVersionLength = 32;
        private const float ShowAfterSpawnSeconds = 3f;

        // Client side: the one on-screen notice waiting for the character (see the class doc).
        private static string _pendingText;
        private static bool _pendingCenter;
        private static float _pendingShowAt = -1f;

        // Client side, one connection at a time.
        private static int _helloSent;
        private static float _nextHelloAt;
        private static bool _answered;
        private static bool _silenceReported;

        // Server side: every peer seen since this server started, until it disconnects.
        private sealed class PeerState
        {
            public float FirstSeen;
            public bool Answered;
            public bool Reported; // logged on the server
            public bool Told;     // told on the client's screen, after its character spawned
        }

        private static readonly Dictionary<long, PeerState> _peers = new Dictionary<long, PeerState>();
        private static readonly List<long> _scratch = new List<long>();
        private static float _nextServerScan;

        /// <summary>A fresh ZRoutedRpc means a fresh connection or a fresh server: forget both sides.</summary>
        public static void ResetForNewConnection()
        {
            _helloSent = 0;
            _nextHelloAt = 0f;
            _answered = false;
            _silenceReported = false;
            _pendingText = null;
            _pendingShowAt = -1f;
            _peers.Clear();
            _nextServerScan = 0f;
        }

        /// <summary>Client: every frame. Cheap until the channel exists, then three sends at most.</summary>
        public static void ClientTick()
        {
            FlushPendingNotice();
            if (_answered || ValheimBridge.IsServer() || !ValheimBridge.CanReachServer()) return;

            float now = Time.realtimeSinceStartup;
            if (now < _nextHelloAt) return;

            if (_helloSent < HelloTries)
            {
                _helloSent++;
                _nextHelloAt = now + HelloRetrySeconds;
                ValheimBridge.SendVersionToServer(Plugin.VERSION, Plugin.WireProtocol);
                FireLogger.Debug($"[VERSION] sent FireFront {Plugin.VERSION} (protocol {Plugin.WireProtocol}) to the server, try {_helloSent}/{HelloTries}.");
                return;
            }

            if (_silenceReported) return;
            _silenceReported = true;
            TellPlayer(center: true, warn: true,
                "FireFront: the server did not answer the version check. Its FireFront is 0.23 or older, or it has none. " +
                $"This game runs {Plugin.VERSION}. Fire will not show or sync correctly until both run the same version.");
        }

        /// <summary>Server: every frame, scanning the peer list every few seconds.</summary>
        public static void ServerTick()
        {
            if (!ValheimBridge.IsServer()) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextServerScan) return;
            _nextServerScan = now + ServerScanSeconds;

            _scratch.Clear();
            ValheimBridge.CollectReadyPeerIds(_scratch);

            // Forget peers that have left, so a reconnect under the same id starts fresh.
            if (_peers.Count > 0)
            {
                var gone = new List<long>();
                foreach (long id in _peers.Keys) if (!_scratch.Contains(id)) gone.Add(id);
                foreach (long id in gone) _peers.Remove(id);
            }

            foreach (long id in _scratch)
            {
                if (!_peers.TryGetValue(id, out PeerState st))
                {
                    _peers[id] = new PeerState { FirstSeen = now };
                    continue;
                }
                if (st.Answered || st.Told || now - st.FirstSeen < ServerGraceSeconds) continue;

                if (!st.Reported)
                {
                    st.Reported = true;
                    string host = ValheimBridge.PeerHostName(id) ?? "?";
                    FireLogger.Warn($"[VERSION] peer {id} ({host}) has not answered FireFront's version check in {ServerGraceSeconds:F0} s: " +
                                    $"its FireFront is 0.23 or older, or it has none. This server runs {Plugin.VERSION} (protocol {Plugin.WireProtocol}).");
                }

                // The on-screen half waits for that player's character, for the reason in the class doc.
                if (!ValheimBridge.PeerHasCharacter(id)) continue;
                st.Told = true;
                ValheimBridge.TellPeerDirectly(id,
                    $"FireFront: this server runs FireFront {Plugin.VERSION}. Your game has an older FireFront, or none. " +
                    "Fire will not show or sync correctly until you install the same version as the server.");
            }
        }

        /// <summary>The RPC handler, both sides.</summary>
        public static void OnVersion(long sender, string version, int protocol)
        {
            version = Clean(version);

            if (ValheimBridge.IsServer())
            {
                ValheimBridge.SendVersionTo(sender, Plugin.VERSION, Plugin.WireProtocol);

                if (!_peers.TryGetValue(sender, out PeerState st))
                {
                    st = new PeerState { FirstSeen = Time.realtimeSinceStartup };
                    _peers[sender] = st;
                }
                if (st.Answered) return; // a client repeats until it hears back; say it once
                st.Answered = true;

                string host = ValheimBridge.PeerHostName(sender) ?? "?";
                if (version == Plugin.VERSION && protocol == Plugin.WireProtocol)
                    FireLogger.Info($"[VERSION] peer {sender} ({host}) runs FireFront {version}, same as this server.");
                else if (protocol == Plugin.WireProtocol)
                    FireLogger.Warn($"[VERSION] peer {sender} ({host}) runs FireFront {version}; this server runs {Plugin.VERSION}. " +
                                    $"Same protocol ({protocol}), so they work together.");
                else
                    FireLogger.Warn($"[VERSION] peer {sender} ({host}) runs FireFront {version} (protocol {protocol}); this server runs " +
                                    $"{Plugin.VERSION} (protocol {Plugin.WireProtocol}). They do NOT work together: fire will not sync correctly for that player.");
                return;
            }

            if (!ValheimBridge.IsFromServer(sender)) return;
            if (_answered) return;
            _answered = true;

            if (version == Plugin.VERSION && protocol == Plugin.WireProtocol)
            {
                FireLogger.Info($"[VERSION] the server runs FireFront {version}, same as this game.");
            }
            else if (protocol == Plugin.WireProtocol)
            {
                TellPlayer(center: false, warn: true,
                    $"FireFront: the server runs {version} and this game runs {Plugin.VERSION}. They work together; matching versions are still best.");
            }
            else
            {
                TellPlayer(center: true, warn: true,
                    $"FireFront: the server runs {version} and this game runs {Plugin.VERSION}. They do not work together: " +
                    "fire will not show or sync correctly until both run the same version.");
            }
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "?";
            if (s.Length > MaxVersionLength) s = s.Substring(0, MaxVersionLength);
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsControl(c) ? '?' : c);
            return sb.ToString();
        }

        private static void TellPlayer(bool center, bool warn, string text)
        {
            if (warn) FireLogger.Warn("[VERSION] " + text);
            else FireLogger.Info("[VERSION] " + text);
            Console.instance?.AddString(text);
            _pendingText = text;
            _pendingCenter = center;
            _pendingShowAt = -1f;
        }

        /// <summary>Shows the held notice once the local player has existed for a few seconds.</summary>
        private static void FlushPendingNotice()
        {
            if (_pendingText == null) return;
            if (Player.m_localPlayer == null) { _pendingShowAt = -1f; return; }
            float now = Time.realtimeSinceStartup;
            if (_pendingShowAt < 0f) { _pendingShowAt = now + ShowAfterSpawnSeconds; return; }
            if (now < _pendingShowAt) return;

            string text = _pendingText;
            _pendingText = null;
            if (_pendingCenter) MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, text);
            else ValheimBridge.ShowPlayerMessage(text);
        }
    }
}
