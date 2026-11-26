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

## TODO

## DONE
- [x] T-7 [P2] add a default global hotkey to open and play the last recording | verify: the requested change is present and demonstrated against the user own description of it | request_witness: model_supplied | source_receipts: SRC-005 | owner: saipen | claim_time: 2026-11-26T05:56:08Z | closure_mode: own_patch | publication_state: pending
- [x] T-6 [P2] fix global Show window hotkey Shift+Alt+0 | verify: the requested change is present and demonstrated against the user own description of it | request_witness: model_supplied | source_receipts: SRC-004 | owner: saipen | claim_time: 2026-11-26T05:50:32Z | closure_mode: own_patch | publication_state: pending
- [x] T-5 [P2] publish FastVRecorder to https://github.com/vacterro/FastVRecorder and include media/screenshots | verify: the requested change is present and demonstrated against the user own description of it | request_witness: model_supplied | source_receipts: SRC-003 | owner: saipen | claim_time: 2026-11-26T05:45:37Z | closure_mode: own_patch | publication_state: pending
- [x] T-3 [P2] Settings: allow typing a recording folder path manually | verify: Settings -> Files: type an absolute path, blur/Enter applies it, the path is saved and used for the next recording | owner: saipen | claim_time: 2026-11-26T05:42:38Z | closure_mode: own_patch | publication_state: pending
- [x] T-4 [P0] Audio: fix silent recordings (bounded queue) and notification output selection | verify: Live microphone recording contains non-zero signal; notification cue can be routed to a selected output endpoint and previews without error | owner: saipen | claim_time: 2026-11-26T05:42:04Z | closure_mode: own_patch | publication_state: pending
- [x] T-2 [P2] FastVRecorder: expand UI localization to 33 languages with runtime switching and RTL support | verify: the requested change is present and demonstrated against the user own description of it | request_witness: model_supplied | source_receipts: SRC-002 | owner: saipen | claim_time: 2026-11-26T05:41:20Z | closure_mode: own_patch | publication_state: pending
- [x] T-1 [P2] # SAIHANDOFF — FastVRecorder | verify: the requested change is present and demonstrated against the user own description of it | request_witness: model_supplied | source_receipts: SRC-001 | owner: saipen | claim_time: 2026-11-26T05:07:06Z | closure_mode: own_patch | publication_state: pending

## BLOCKED
