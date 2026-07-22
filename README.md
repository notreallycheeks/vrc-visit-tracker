# Visit Tracker

A drop-in VRChat world prefab that persistently tracks, per player:

- **Days visited** — how many distinct days a player has joined your world.
  Disconnecting and rejoining on the same day still counts as one visit.
- **Time in world** — total accumulated playtime across all sessions.
- **Session log** — join/leave timestamps of past sessions (up to a configurable
  cap, default 1000), readable per player for attendance and moderation tools.

Built on VRChat's [Persistence](https://creators.vrchat.com/worlds/udon/persistence/)
system (PlayerData). No configuration required: drop the prefab into your scene and
it works. An optional TextMeshPro display shows the local player's stats, and a small
read API lets your own Udon scripts use the data.

## Installation

Add my VPM registry to the VRChat Creator Companion — one click at
[cheeksy.dev/vpm](https://cheeksy.dev/vpm/) or add
`https://vpm.cheeksy.dev/index.json` manually — then add **Visit Tracker** to your
world project.

Requires **VRChat Worlds SDK 3.7.4+** (the first release with Persistence) and
Unity 2022.3.

## Usage

1. Drag `Packages/Visit Tracker/Prefabs/VisitTracker.prefab` into your scene.
2. That's it. The prefab includes a world-space stats display; delete the `Display`
   child (or clear the **Stats Text** field) if you only want the tracking.

Place **exactly one** VisitTracker in the scene — a second instance would
double-count playtime.

### Inspector settings

| Field | Default | Meaning |
|---|---|---|
| Save Interval Seconds | 30 | How often playtime is flushed to PlayerData. Every write sends the player's entire PlayerData blob, so keep this modest. |
| Stats Text | prefab display | Optional TextMeshProUGUI showing the local player's stats. |
| Stats Format | `Days visited: {days}\nTime in world: {time}` | `{days}` and `{time}` (h:mm:ss) are substituted. |

## Reading the data from your own scripts

Grab a reference to the `VisitTracker` component and call:

```csharp
int days      = visitTracker.GetDaysVisited(player);     // any player in the instance
double secs   = visitTracker.GetPlaytimeSeconds(player); // live-updated for the local player
bool hasData  = visitTracker.HasData(player);

// Session log (join/leave history):
byte[] log    = visitTracker.GetSessionData(player);     // fetch once, then decode:
int count     = visitTracker.GetSessionCount(log);
uint joined   = visitTracker.GetSessionJoin(log, 0);     // Unix epoch seconds, 0 = oldest
uint left     = visitTracker.GetSessionLeave(log, 0);
uint current  = visitTracker.GetCurrentSessionStart(player);
uint lastSeen = visitTracker.GetLastSeen(player);
```

Or read the PlayerData keys directly (e.g. from Udon Graph):

| Key | Type | Meaning |
|---|---|---|
| `cvt_daysVisited` | Int | Distinct days the player has joined, including today |
| `cvt_lastVisitDay` | Int | UTC day number of the most recent counted visit |
| `cvt_playtimeSeconds` | Double | Total accumulated seconds in the world |
| `cvt_sessions` | Bytes | Completed sessions, 8 bytes each: big-endian uint32 join + leave epoch seconds, oldest first |
| `cvt_sessionStart` | UInt | Start of the current (unfinished) session, epoch seconds |
| `cvt_lastSeen` | UInt | Heartbeat written every save interval, epoch seconds |

Remote players' values are readable once their data has been restored (after their
`OnPlayerRestored` fires locally). Players who aren't in the instance can't be read —
that's a VRChat Persistence limitation.

## Behavior notes

- **Day boundary is UTC**, so "a new day" flips at midnight UTC for everyone,
  regardless of local timezone. If a player stays in the world across midnight UTC,
  the new day is counted too.
- Playtime is flushed every save interval; the final partial interval before a
  disconnect (at most `saveIntervalSeconds`) can be lost. VRChat cannot save
  persistence data during `OnPlayerLeft`, so this is inherent to the platform.
- **Leave times are heartbeat-based.** A session's leave timestamp is the last
  heartbeat flushed before disconnect (accurate to `saveIntervalSeconds`), and the
  session is finalized into the log on the player's *next* join — so a player's
  most recent session appears "open" until they return.
- Timestamps and day boundaries use the instance server's clock
  (`Networking.GetNetworkDateTime`), not the player's local clock, so skewed
  client clocks can't farm daily visits or distort recorded times.
- Data is per-world and counts toward VRChat's 100 KB PlayerData budget per player.
  The counters use well under 100 bytes; each logged session adds 8 bytes
  (1000 sessions — the default cap — is 8 KB).

## License

MIT — see [LICENSE](LICENSE).
