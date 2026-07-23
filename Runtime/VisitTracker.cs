using System;
using TMPro;
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Persistence;
using VRC.SDKBase;

namespace Cheeks.VisitTracker
{
    /// <summary>
    /// Drop-in persistent visit tracking for any VRChat world.
    /// Counts how many distinct days a player has visited (multiple joins on the
    /// same UTC day count once), accumulates total time spent in the world, and
    /// keeps a per-player log of past sessions as join/leave timestamps.
    /// Place exactly ONE instance in the scene.
    /// </summary>
    [AddComponentMenu("Cheeks/Visit Tracker")]
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class VisitTracker : UdonSharpBehaviour
    {
        // PlayerData keys. Public so other scripts can read the same data directly.
        // Kept short: every PlayerData write sends the player's full blob, keys included.
        public const string KeyDaysVisited = "cvt_daysVisited";
        public const string KeyLastVisitDay = "cvt_lastVisitDay";
        public const string KeyPlaytimeSeconds = "cvt_playtimeSeconds";
        public const string KeySessions = "cvt_sessions";
        public const string KeySessionStart = "cvt_sessionStart";
        public const string KeyLastSeen = "cvt_lastSeen";

        // DateTime ticks at 1970-01-01 00:00:00 UTC (Unix epoch).
        private const long EpochTicks = 621355968000000000L;

        [Header("Saving")]
        [Tooltip("How often accumulated playtime and the last-seen heartbeat are written to PlayerData, in seconds. Every write sends the player's entire PlayerData blob, so keep this modest. Minimum 5. This is also the accuracy of recorded leave times.")]
        public float saveIntervalSeconds = 30f;

        [Header("Session log")]
        [Tooltip("Maximum completed sessions kept per player, oldest dropped first. Each session costs 8 bytes of the player's PlayerData; 1000 sessions = 8 KB out of VRChat's 100 KB budget.")]
        public int maxStoredSessions = 1000;

        [Header("Display (optional)")]
        [Tooltip("Optional TextMeshPro text showing the local player's stats. Leave empty if you only want the tracking + API.")]
        public TextMeshProUGUI statsText;

        [Tooltip("Template for the stats text. {days} = days visited, {time} = total time as h:mm:ss.")]
        [TextArea]
        public string statsFormat = "Days visited: {days}\nTime in world: {time}";

        private VRCPlayerApi _localPlayer;
        private bool _restored;
        private int _today;              // UTC day number (ticks / ticks-per-day)
        private int _daysVisited;
        private double _savedPlaytime;   // seconds already persisted
        private double _unsavedPlaytime; // seconds accumulated since last save
        private float _lastAccumulate;   // Time.realtimeSinceStartup at last accumulation

        private void Start()
        {
            _localPlayer = Networking.LocalPlayer;
            if (statsText != null)
                statsText.text = "";
        }

        public override void OnPlayerRestored(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player) || !player.isLocal || _restored)
                return;

            _restored = true;
            _today = UtcDayNumber();

            // Close out the previous session (its end time is the last heartbeat
            // that made it to PlayerData before the player left) and open a new one.
            uint nowEpoch = NowEpochSeconds();
            uint prevStart = 0;
            PlayerData.TryGetUInt(player, KeySessionStart, out prevStart);
            if (prevStart > 0)
            {
                uint prevEnd = 0;
                PlayerData.TryGetUInt(player, KeyLastSeen, out prevEnd);
                if (prevEnd < prevStart)
                    prevEnd = prevStart;
                AppendSession(player, prevStart, prevEnd);
            }
            PlayerData.SetUInt(KeySessionStart, nowEpoch);
            PlayerData.SetUInt(KeyLastSeen, nowEpoch);

            _daysVisited = 0;
            PlayerData.TryGetInt(player, KeyDaysVisited, out _daysVisited);

            int lastDay = -1;
            PlayerData.TryGetInt(player, KeyLastVisitDay, out lastDay);

            _savedPlaytime = 0;
            PlayerData.TryGetDouble(player, KeyPlaytimeSeconds, out _savedPlaytime);

            if (lastDay != _today)
            {
                _daysVisited++;
                PlayerData.SetInt(KeyDaysVisited, _daysVisited);
                PlayerData.SetInt(KeyLastVisitDay, _today);
            }

            _lastAccumulate = Time.realtimeSinceStartup;
            SendCustomEventDelayedSeconds(nameof(_SaveTick), SaveInterval());
            _DisplayTick();
        }

        /// <summary>Periodic flush of accumulated playtime and the last-seen
        /// heartbeat to PlayerData. Public only so SendCustomEventDelayedSeconds
        /// can reach it; the underscore prefix keeps it out of reach of remote
        /// SendCustomEvent calls.</summary>
        public void _SaveTick()
        {
            if (!_restored)
                return;

            Accumulate();
            CheckDayRollover();

            if (_unsavedPlaytime > 0)
            {
                _savedPlaytime += _unsavedPlaytime;
                _unsavedPlaytime = 0;
                PlayerData.SetDouble(KeyPlaytimeSeconds, _savedPlaytime);
            }

            // Heartbeat: becomes this session's leave time if the player
            // disconnects before the next tick (nothing can be saved during
            // OnPlayerLeft, so leave times are accurate to the save interval).
            PlayerData.SetUInt(KeyLastSeen, NowEpochSeconds());

            SendCustomEventDelayedSeconds(nameof(_SaveTick), SaveInterval());
        }

        /// <summary>Once-a-second refresh of the optional stats text. No PlayerData
        /// writes happen here.</summary>
        public void _DisplayTick()
        {
            if (statsText == null)
                return;

            Accumulate();
            statsText.text = FormatStats();
            SendCustomEventDelayedSeconds(nameof(_DisplayTick), 1f);
        }

        // ------------------------------------------------------------------
        // Public read API — usable by any other UdonSharp script in the world.
        // Values for remote players are valid once that player's data has been
        // restored (their OnPlayerRestored has fired locally).
        // ------------------------------------------------------------------

        /// <summary>True once the player has any tracked data available to read.</summary>
        public bool HasData(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player))
                return false;
            return PlayerData.HasKey(player, KeyDaysVisited);
        }

        /// <summary>Number of distinct UTC days the player has joined this world,
        /// including today. 0 if unknown.</summary>
        public int GetDaysVisited(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player))
                return 0;
            int days = 0;
            PlayerData.TryGetInt(player, KeyDaysVisited, out days);
            return days;
        }

        /// <summary>Total seconds the player has spent in this world. For the local
        /// player this includes time not yet flushed to PlayerData.</summary>
        public double GetPlaytimeSeconds(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player))
                return 0;

            if (player.isLocal && _restored)
            {
                Accumulate();
                return _savedPlaytime + _unsavedPlaytime;
            }

            double seconds = 0;
            PlayerData.TryGetDouble(player, KeyPlaytimeSeconds, out seconds);
            return seconds;
        }

        /// <summary>Raw session log for a player: 8 bytes per completed session
        /// (big-endian uint32 join epoch, big-endian uint32 leave epoch), oldest
        /// first. Null if the player has no completed sessions yet. Decode with
        /// GetSessionCount / GetSessionJoin / GetSessionLeave. Fetch once and
        /// reuse — every call copies the blob out of PlayerData.</summary>
        public byte[] GetSessionData(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player))
                return null;
            byte[] data = null;
            PlayerData.TryGetBytes(player, KeySessions, out data);
            return data;
        }

        /// <summary>Number of completed sessions in a blob from GetSessionData.</summary>
        public int GetSessionCount(byte[] sessionData)
        {
            if (sessionData == null)
                return 0;
            return sessionData.Length / 8;
        }

        /// <summary>Join time (Unix epoch seconds, UTC) of session `index`
        /// (0 = oldest) in a blob from GetSessionData. 0 if out of range.</summary>
        public uint GetSessionJoin(byte[] sessionData, int index)
        {
            return ReadUInt(sessionData, index * 8);
        }

        /// <summary>Leave time (Unix epoch seconds, UTC) of session `index`
        /// (0 = oldest) in a blob from GetSessionData. Accurate to the save
        /// interval. 0 if out of range.</summary>
        public uint GetSessionLeave(byte[] sessionData, int index)
        {
            return ReadUInt(sessionData, index * 8 + 4);
        }

        /// <summary>Start of the player's current session (Unix epoch seconds,
        /// UTC). For players in the instance this is their ongoing session; it is
        /// only finalized into the session log on their NEXT join. 0 if unknown.</summary>
        public uint GetCurrentSessionStart(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player))
                return 0;
            uint value = 0;
            PlayerData.TryGetUInt(player, KeySessionStart, out value);
            return value;
        }

        /// <summary>The player's most recent heartbeat (Unix epoch seconds, UTC),
        /// written every save interval while they are in the world. 0 if unknown.</summary>
        public uint GetLastSeen(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player))
                return 0;
            uint value = 0;
            PlayerData.TryGetUInt(player, KeyLastSeen, out value);
            return value;
        }

        /// <summary>Current Unix epoch seconds (UTC) from the instance server's
        /// clock, so all clients agree on recorded times.</summary>
        public uint NowEpochSeconds()
        {
            long ticks = Networking.GetNetworkDateTime().Ticks - EpochTicks;
            if (ticks < 0)
                return 0;
            return (uint)(ticks / TimeSpan.TicksPerSecond);
        }

        // ------------------------------------------------------------------

        /// <summary>Appends one completed session to the local player's session
        /// log, dropping oldest entries beyond maxStoredSessions.</summary>
        private void AppendSession(VRCPlayerApi player, uint start, uint end)
        {
            byte[] old = null;
            PlayerData.TryGetBytes(player, KeySessions, out old);
            int oldCount = old == null ? 0 : old.Length / 8;

            int cap = maxStoredSessions < 1 ? 1 : maxStoredSessions;
            int keep = oldCount;
            if (keep > cap - 1)
                keep = cap - 1;

            byte[] data = new byte[(keep + 1) * 8];
            int srcOffset = (oldCount - keep) * 8;
            for (int i = 0; i < keep * 8; i++)
                data[i] = old[srcOffset + i];

            int o = keep * 8;
            WriteUInt(data, o, start);
            WriteUInt(data, o + 4, end);
            PlayerData.SetBytes(KeySessions, data);
        }

        private void WriteUInt(byte[] buffer, int offset, uint value)
        {
            // The & 0xFF masks are load-bearing: Udon's numeric casts go through
            // System.Convert, which throws on overflow instead of truncating.
            buffer[offset] = (byte)((value >> 24) & 0xFFu);
            buffer[offset + 1] = (byte)((value >> 16) & 0xFFu);
            buffer[offset + 2] = (byte)((value >> 8) & 0xFFu);
            buffer[offset + 3] = (byte)(value & 0xFFu);
        }

        private uint ReadUInt(byte[] buffer, int offset)
        {
            if (buffer == null || offset < 0 || offset + 4 > buffer.Length)
                return 0;
            return ((uint)buffer[offset] << 24)
                | ((uint)buffer[offset + 1] << 16)
                | ((uint)buffer[offset + 2] << 8)
                | buffer[offset + 3];
        }

        /// <summary>Moves elapsed real time since the last call into the unsaved
        /// playtime bucket. Safe to call from multiple places.</summary>
        private void Accumulate()
        {
            if (!_restored)
                return;
            float now = Time.realtimeSinceStartup;
            float delta = now - _lastAccumulate;
            _lastAccumulate = now;
            if (delta > 0)
                _unsavedPlaytime += delta;
        }

        /// <summary>If the UTC date changed while the player stayed in the world,
        /// count the new day as a visit.</summary>
        private void CheckDayRollover()
        {
            int newToday = UtcDayNumber();
            if (newToday == _today)
                return;
            _today = newToday;
            _daysVisited++;
            PlayerData.SetInt(KeyDaysVisited, _daysVisited);
            PlayerData.SetInt(KeyLastVisitDay, _today);
        }

        private float SaveInterval()
        {
            return Mathf.Max(5f, saveIntervalSeconds);
        }

        private int UtcDayNumber()
        {
            // Server clock, not the client's, so skewed local clocks can't farm
            // extra daily visits or distort recorded times.
            return (int)(Networking.GetNetworkDateTime().Ticks / TimeSpan.TicksPerDay);
        }

        private string FormatStats()
        {
            int total = (int)(_savedPlaytime + _unsavedPlaytime);
            int hours = total / 3600;
            int minutes = (total % 3600) / 60;
            int seconds = total % 60;

            string time = hours + ":" +
                (minutes < 10 ? "0" : "") + minutes + ":" +
                (seconds < 10 ? "0" : "") + seconds;

            string text = statsFormat;
            text = text.Replace("{days}", _daysVisited.ToString());
            text = text.Replace("{time}", time);
            return text;
        }
    }
}
