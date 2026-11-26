# Board

<!-- Ticket shape is RFC § 1.2's, exactly: a checkbox, the T-### id, a
     description, then only the fields that apply, space-pipe separated.
     Shown here WITHOUT its leading "- " on purpose (see below):

       [ ] T-001 short description | verify: pytest -q

     Other legal fields (RFC § 1.2): the dependency one, taking a
     comma-separated list of T-### this ticket waits on; owner and
     claim_time for claims (§ 1.4); blocker for facts + dead ends; verify as
     shown above. Named rather than shown here on purpose -- see below.

     A real line starts with "- ". Checkbox: [ ] open, [/] in progress
     (## DOING), [x] done (## DONE). A status change MOVES the line between
     sections -- cut and paste, never copy, or the same id ends up under two
     headings. All four headings below are required, even while empty.

     Why the example is de-fanged: neither validator skips HTML comments, so
     anything ticket-shaped in here is read as a real ticket on a brand-new,
     untouched board. Two separate traps, both hit for real while writing
     this very file: a full checkbox line parses as a live ticket, and the
     dependency field followed by an id is flagged as a dangling reference
     even without a leading dash -- tests/validate.sh scans for that field
     across the whole file, not only ticket lines, making it stricter here
     than tools/validate.py. So: no leading dash on any example, and never
     write that field name next to a concrete id anywhere in this file. -->

## DOING
- [/] T-18 [P3] correct final-artifact performance evidence in ACCEPTANCE/CHANGELOG and note host-load variance | verify: the 0.0.5 final artifact is measured cold and warm on the same host and the docs state the observed range and staging differences | owner: saipen | claim_time: 2026-11-26T13:35:12Z

## TODO
- [ ] T-17 [P3] protect History index from cross-session lost updates (Local single-instance mutex allows two sessions; HistoryStore is process-local) | verify: two stores/processes add different entries and a reload contains both, or the second save is refused with a surfaced conflict; reproduce the current one-entry loss first
- [ ] T-16 [P2] Package.ps1 must verify the produced ZIP entries and hashes against publish-manifest.json | verify: corrupt or omit one staged file and verify Package.ps1 fails after re-opening the archive; a good package passes entry-by-entry name/size/SHA-256 comparison
- [ ] T-15 [P2] recovery lease must never delete a pre-existing user file named .fastvrecorder-recovery.lock | verify: create Recordings/.fastvrecorder-recovery.lock with user bytes, run the recovery scan, and verify the file and bytes survive while a stale owned lease is still handled safely
- [ ] T-14 [P2] saipen ship release parity gate demands absent README mirrors and a README version marker; make the gate project-scoped or complete for this repository without fabricated translations | verify: saipen ship on a clean checkout without README.ee/ded/ja either completes the release or refuses with a project-scoped waiver; no dummy mirrors are created

## DONE
- [x] T-13 [P2] SAIHANDOFF-4 FastVRecorder final stabilization and release readiness | verify: the requested change is present and demonstrated against the user own description of it | request_witness: model_supplied | source_receipts: SRC-010 | owner: saipen | claim_time: 2026-11-26T13:04:48Z | closure_mode: own_patch | publication_state: pending
- [x] T-12 [P2] FastVRecorder SAIHANDOFF-3 MODE 2 final reliability audit: P0 publish manifest path containment, P0/P1 safe package update transaction, P1 deferred finalizat... | verify: the requested change is present and demonstrated against the user own description of it | request_witness: model_supplied | source_receipts: SRC-009 | owner: saipen | claim_time: 2026-11-26T10:26:56Z | closure_mode: own_patch | publication_state: pending
- [x] T-11 [P1] fix review findings: queue-overflow must be PartialSaved with error cue/note, unreadable settings must not reset/overwrite, MP3 finalization I/O must not strand Finalizing, recovery marker must verify output, manifest must own all shipped assets | verify: Dropped queue bytes yield PartialSaved + history note + error cue; locked settings load is Unreadable/refuses overwrite; MP3 I/O failure completes with PartialSaved; missing recovered output is re-recovered; republish updates omitted manifest assets | owner: saipen | claim_time: 2026-11-26T07:49:16Z | closure_mode: own_patch | publication_state: pending
- [x] T-10 [P2] refresh localized settings screenshots at 0.0.2 after real runtime verification | verify: the requested change is present and demonstrated against the user own description of it | request_witness: model_supplied | source_receipts: SRC-008 | owner: saipen | claim_time: 2026-11-26T07:35:46Z | closure_mode: own_patch | publication_state: pending
- [x] T-9 [P2] ensure FastVRecorder persists settings and hotkey assignments across restarts | verify: the requested change is present and demonstrated against the user own description of it | request_witness: model_supplied | source_receipts: SRC-007 | owner: saipen | claim_time: 2026-11-26T07:23:45Z | closure_mode: own_patch | publication_state: pending
- [x] T-8 [P2] harden FastVRecorder data safety, recovery, writer ownership and release per SAIHANDOFF-2 | verify: the requested change is present and demonstrated against the user own description of it | request_witness: model_supplied | source_receipts: SRC-006 | owner: saipen | claim_time: 2026-11-26T07:06:32Z | closure_mode: own_patch | publication_state: pending
- [x] T-7 [P2] add a default global hotkey to open and play the last recording | verify: the requested change is present and demonstrated against the user own description of it | request_witness: model_supplied | source_receipts: SRC-005 | owner: saipen | claim_time: 2026-11-26T05:56:08Z | closure_mode: own_patch | publication_state: pending
- [x] T-6 [P2] fix global Show window hotkey Shift+Alt+0 | verify: the requested change is present and demonstrated against the user own description of it | request_witness: model_supplied | source_receipts: SRC-004 | owner: saipen | claim_time: 2026-11-26T05:50:32Z | closure_mode: own_patch | publication_state: pending
- [x] T-5 [P2] publish FastVRecorder to https://github.com/vacterro/FastVRecorder and include media/screenshots | verify: the requested change is present and demonstrated against the user own description of it | request_witness: model_supplied | source_receipts: SRC-003 | owner: saipen | claim_time: 2026-11-26T05:45:37Z | closure_mode: own_patch | publication_state: pending
- [x] T-3 [P2] Settings: allow typing a recording folder path manually | verify: Settings -> Files: type an absolute path, blur/Enter applies it, the path is saved and used for the next recording | owner: saipen | claim_time: 2026-11-26T05:42:38Z | closure_mode: own_patch | publication_state: pending
- [x] T-4 [P0] Audio: fix silent recordings (bounded queue) and notification output selection | verify: Live microphone recording contains non-zero signal; notification cue can be routed to a selected output endpoint and previews without error | owner: saipen | claim_time: 2026-11-26T05:42:04Z | closure_mode: own_patch | publication_state: pending
- [x] T-2 [P2] FastVRecorder: expand UI localization to 33 languages with runtime switching and RTL support | verify: the requested change is present and demonstrated against the user own description of it | request_witness: model_supplied | source_receipts: SRC-002 | owner: saipen | claim_time: 2026-11-26T05:41:20Z | closure_mode: own_patch | publication_state: pending
- [x] T-1 [P2] # SAIHANDOFF — FastVRecorder | verify: the requested change is present and demonstrated against the user own description of it | request_witness: model_supplied | source_receipts: SRC-001 | owner: saipen | claim_time: 2026-11-26T05:07:06Z | closure_mode: own_patch | publication_state: pending

## BLOCKED
