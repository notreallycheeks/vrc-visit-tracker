# Development notes (not shipped in the VPM zip)

**Packaging rule:** release zips must contain no `.md` files. `README.md` (and its
`.meta`), `DEVELOPMENT.md`, and `.gitattributes` are all export-ignored; check any
new docs get the same treatment before tagging a release.

This package was authored entirely outside Unity. The Unity-side assets
(program asset, prefab, metas) were hand-written against known-good templates
(VideoTXL / AudioLink package layouts, attach-to-me Udon wiring), so the first
Unity import needs a verification pass.

## First-import checklist (VCC → add package → open project)

1. **U# compiles**: `Runtime/VisitTracker.cs` should compile into the
   `Cheeks.VisitTracker` assembly with no errors. Most likely failure points:
   an API not exposed to Udon (`DateTime.UtcNow.Ticks`, `TimeSpan.TicksPerDay`
   const, `string.Replace`, ternaries — all believed safe) or a missing asmdef
   reference.
2. **Program asset links**: `Runtime/VisitTracker.asset` ships with
   `serializedUdonProgramAsset: {fileID: 0}` and `compiledVersion: 0` (the
   "freshly created, not yet compiled" state). UdonSharp should compile and
   assign the serialized program on import. If the asset shows an error, select
   it and let U# recompile (VRChat SDK → Udon Sharp → Compile All).
3. **Prefab wiring**: `Prefabs/VisitTracker.prefab` contains the UdonBehaviour
   (version-marker-only variable blob, `serializedProgramAsset: {fileID: 0}`)
   plus the U# proxy component with `_udonSharpBackingUdonBehaviour` pointing at
   it and inspector values (`saveIntervalSeconds`, `statsText`, `statsFormat`)
   serialized on the proxy. If U# complains about the prefab, open it, let U#
   run its upgrade pass, and save. Worst case: delete the two components off the
   root, re-add the VisitTracker U# component, re-assign StatsText, save.
4. **TMP display**: the StatsText uses the classic LiberationSans SDF GUID
   (`8f586378…`). If TMP Essentials aren't imported the text falls back or shows
   missing-font; import TMP Essentials.
5. **Play-mode test** (ClientSim): enter play mode; after the simulated player's
   `OnPlayerRestored`, the display should show `Days visited: 1` and a counting
   timer. Note: ClientSim/Build & Test persistence only lasts while the client
   stays open — full persistence verification needs an uploaded world and two
   sessions.

## Behavior test matrix

- Join once → daysVisited 1; rejoin same UTC day → still 1; next UTC day → 2.
- Playtime accrues ~1:1 with wall clock, flushed every `saveIntervalSeconds`.
- Stay across midnight UTC → day count increments in-session.
- Second client: remote player's values readable via `GetDaysVisited(player)`
  after their restore.

## First-import verification — DONE 2026-07-22 (testbed project)

Verified in `D:\repositories\cheeks\vrc-vpm-testbed` (SDK 3.10.4, Unity
2022.3.22f1, ClientSim play mode; both packages junctioned into Packages/):
U# compile clean, prefab wiring intact, persistence restore/write across play
sessions, session finalize-on-next-join with heartbeat leave time, byte-pack
round trip, in-session UTC day rollover, terminal roster/detail/back flow.
`PlayerData.SetUInt/TryGetUInt/SetBytes/TryGetBytes` and
`Networking.GetNetworkDateTime()` all exist and are exposed.

Hard-won UdonSharp facts (do not regress):

1. Every U# assembly REQUIRES an `UdonSharpAssemblyDefinition` asset next to its
   asmdef (`*.USharp.asset`, script guid 5136146375e9a0a498a72a0091b40cc1) —
   without it U# refuses to compile the scripts at all.
2. `%` (modulus) is NOT exposed for `uint` (it is for `int`). Keep duration math
   in int/long.
3. Numeric casts compile to `System.Convert.*`, which THROWS on overflow instead
   of truncating — `(byte)(someUint >> 16)` crashes at runtime. Mask first:
   `(byte)((v >> 16) & 0xFFu)`.
4. U# rewrites the program `.asset` files in place after compiling (links
   `serializedUdonProgramAsset`, `compiledVersion: 2`, field defs). VideoTXL
   ships this compiled state, so commit it.

Still outstanding: real-client Build & Test (VRCUiShape laser/desktop clicks were
not exercised — ClientSim handlers were invoked directly), a 2-client remote-read
test, `maxStoredSessions` cap trim, and the registry release of 0.2.0 + terminal
0.1.0.

## Companion package: vrc-visit-terminal (dev.cheeksy.visitterminal)

The in-world moderation terminal lives in the sibling repo `vrc-visit-terminal`
(same hand-authoring pattern, depends on this package `^0.2.0`). That repo
deliberately ships no docs, so its first-import checklist lives here:

1. `Cheeks.VisitTerminal` asmdef resolves its `Cheeks.VisitTracker` reference and
   both scripts compile (they call the 0.2.0 session API).
2. Program assets `VisitTerminal.asset` / `VisitTerminalRow.asset` compile and
   link, same as items 2–3 of the first-import checklist above.
3. `Prefabs/VisitTerminal.prefab`: canvas shows roster; the row template's Button
   targets the ROW UdonBehaviour (`_OnRowClick`), the Back button targets the
   TERMINAL UdonBehaviour (`_Back`) — verify both persistent calls survived the
   U# import pass.
4. Udon exposure gambles to confirm: `Instantiate(GameObject)` +
   `Transform.SetParent(t, false)` + `GetComponent<VisitTerminalRow>()` (row
   spawning), `RectTransform.anchoredPosition`/`sizeDelta` setters (list layout),
   `DateTime.MinValue.AddTicks(...).AddSeconds(...)` (timestamp formatting),
   `VRCPlayerApi.GetPlayers` / `GetPlayerById`, `player.isInstanceOwner`.
5. Scene wiring: drop in a VisitTracker prefab + a VisitTerminal prefab, assign
   the terminal's **Visit Tracker** field, enter play mode → roster lists the
   ClientSim player; click row → detail view shows "This session"; Back returns.
6. Access gating: with `allowEveryone` off, the ClientSim local player is master
   so the canvas stays on; untick `allowInstanceMaster` too → canvas hides.

## Releasing an update

Same procedure as documented in `vpm.cheeksy.dev/CLAUDE.md`: bump `version` in
`package.json`, commit, `git archive --format=zip -o dev.cheeksy.visittracker-X.Y.Z.zip HEAD`,
sha256, `gh release create vX.Y.Z`, add the version entry to the registry
index.json (+ mirror), update both landing pages. This file and `.gitattributes`
are export-ignored, so they never end up in the shipped zip.
