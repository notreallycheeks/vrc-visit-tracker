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
    /// same UTC day count once) and accumulates total time spent in the world.
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

        [Header("Saving")]
        [Tooltip("How often accumulated playtime is written to PlayerData, in seconds. Every write sends the player's entire PlayerData blob, so keep this modest. Minimum 5.")]
        public float saveIntervalSeconds = 30f;

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

        /// <summary>Periodic flush of accumulated playtime to PlayerData. Public only
        /// so SendCustomEventDelayedSeconds can reach it; the underscore prefix keeps
        /// it out of reach of remote SendCustomEvent calls.</summary>
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

        // ------------------------------------------------------------------

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
            return (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerDay);
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
