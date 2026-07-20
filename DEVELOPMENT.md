# Development notes (not shipped in the VPM zip)

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

## Releasing an update

Same procedure as documented in `vpm.cheeksy.dev/CLAUDE.md`: bump `version` in
`package.json`, commit, `git archive --format=zip -o dev.cheeksy.visittracker-X.Y.Z.zip HEAD`,
sha256, `gh release create vX.Y.Z`, add the version entry to the registry
index.json (+ mirror), update both landing pages. This file and `.gitattributes`
are export-ignored, so they never end up in the shipped zip.
