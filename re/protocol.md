# AULA WIN60HE / WIN75HE HID Protocol Notes

Reverse-engineered from `hed.aulacn.com` (the official WebHID web driver) via a
JS shim around `HIDDevice.prototype.sendReport/sendFeatureReport/receiveFeatureReport`
and the `inputreport` event. See `capture-instrumentation.js` to redeploy it.

## Device identity

- WIN60HE: VendorID `0x2e3c`, ProductID `0xc365`, usagePage `0xff1b` (65307), usage `0x91` (145)
- Connection is Output/Input reports (not Feature reports) — Report ID **1**, 63-byte payload
  (`sendReport(1, data)` / `inputreport` event), despite the HID descriptor also defining a
  519-byte Feature report on some AULA boards (F75) — WIN60HE uses the simpler 63-byte report.
- Frame shape (payload bytes, report ID stripped):
  `[CMD] [SUBCMD] [00] [IDX] [payload...]` — byte[2] observed always 0x00 so far;
  byte[3] is a per-item index (key #, param #) for commands that enumerate a table.
  Device echoes the same CMD/SUBCMD/IDX back in its response with the answer appended.

## Identified commands (WIN60HE, firmware V3.17.07/V3.17.08)

| CMD  | Meaning | Notes |
|------|---------|-------|
| `0d` | Get device info strings | idx 0 → `"W669,34,KB,SI,SI2825KZHEARGB,V3.17.07"` (model/variant/firmware); idx 1 → build date `"Apr 15 2026,11:20:35"` |
| `01` | Get/poll main status block | Response: `11 00 00 00 00 00 00 00 00 00 01 00 00 7f 00 01 01 ...` — polled repeatedly (looks like a heartbeat/current-state read, byte meanings TBD) |
| `0e` | Get some fixed config block | `16 02 22 0e 0a 02 02 22 12 1e 3e 0e 2e 3e 20 20 20` — unclear, constant |
| `19 80..89` | Read per-profile-group actuation table | idx 0..4, values `3a 3a 3a 3a 18` (=58,58,58,58,24 raw units) — 10 sub-tables (80-89), likely one per key-group or per-parameter (actuation pt / RT down / RT up / dead zone / etc across profiles or key groups) |
| `22 80` | Read per-key single-byte table | idx 0x00..0x27 (40 keys), all `0x28` (=40) by default — likely per-key actuation point in raw units (0.01mm steps? 0x28=40 → 0.40mm, matches typical HE default) |
| `24 01/02` | Apply/commit? | Sent right after full table read; `24 02` RX → `06 01 00 00 00 fa e5`; `24 01` RX → `28 01 00 04 07 01 00 1a 16 01 00 e0` (profile slot summary?) |
| `18 80/82` | Read 10-entry table | idx 0..9, values mostly `0x38`=56 except last `0x18`=24 — another per-group parameter |
| `0a 00` | Read config | `12 02 01 03 09 04 00 0e 0f 06 07 0b` — possibly key-group ordering / SOCD pairing table |
| `02 00` | Read | `04 a5 5a 04 04` — `a5 5a` looks like a magic/sync marker |
| **`07`** | **Set global lighting state (write)** | **DECODED**: `07 00 00 00 [LEN=0e] [00] [MODE] [BRIGHTNESS] [R] [G] [B] 00...` — confirmed `MODE=04`+`BRIGHTNESS=03`+`R=ff,G=00,B=00` when setting Static/Red. `07 01 ...` (read variant) RX gives `0c 00 04 03 ff ff ff` at boot (mode 4, brightness 3, color ff ff ff = white default) |
| `08 01` | Read secondary lighting param | RX `0c 02 04 03 ff 00 00 00 00 00 00 01` |
| `09 20/80/81` | Read per-key-group RGB-ish table | idx 0..7, values `36`/`12`, some tails show `00 00 ff 00 00 ff 00 00 ff` (three BGR/RGB triplets = per-zone color?) |
| `21 00` | **Row/col-addressed per-key read AND write** — the core per-key command | See dedicated section below. |
| `25 00/02` | Read | `25 00` → `06 00 04 01 02 03 05`; `25 02` idx 0..2 → `3a`/`3a`/`10` |
| `26 00` | Read 5-entry table | idx 0..4 → `3a 3a 3a 3a 20` (same shape as `19 8x` tables) |

## Confirmed write commands (verified against real hardware from a native C# app, not just the web driver)

- **`07`** — set global lighting (mode, brightness, RGB). Verified: keyboard visibly turned red.
- **`21`** (row/col write) — set per-key actuation travel. Verified: official driver's
  Actuation tab reflected the new trigger/release values after a write from the native app.
- Response to `0d` (device info) has a length byte before the ASCII text: layout is
  `[CMD][SUBCMD][00][IDX][LEN][ascii...]`, not `[...][IDX][ascii...]` as first assumed.
- `0d` does **not** honor `idx` for random access — repeating the identical `idx=0`
  request pops an internal FIFO cursor between the model string and the build-date
  string. Which one comes back first depends on where a *previous* session left the
  cursor (not necessarily model-first), so the app classifies by content (does the
  string start with a month abbreviation?) rather than assuming call order.

### `21 00` — row/col-addressed per-key command (read + write)

This is the single most important command: **every per-physical-key property**
(matrix identity, actuation travel, likely per-key RGB and remap) goes through
this one opcode, addressed by `[ROW] [COL]`, not by a linear key index.

**Read** (used to enumerate the whole matrix at connect):
```
TX: 21 00 00 00 18 [ROW] [COL]
RX: 21 00 00 00 0c 05 0c 19 19 18 18 00 00 00 00 [ROW] [COL] [payload...]
```
`0c 05 0c 19 19 18 18` after the length byte look like fixed board-identity/limits
constants (same on every read), not part of the per-key payload.

**Write** (confirmed via Actuation → Travel → key "A" → Key Trigger Travel 0.25→0.26mm):
```
TX: 21 00 00 00 18 [ROW] [COL] 00 [FIELD=08] 00×19 [1a 1a 18 18] 00...
RX: 21 00 00 00 01 00 00...   (1-byte ack)
```
- `ROW=0x0c COL=0x00` → key "A" (confirms row/col are physical matrix coords, not scancode)
- `FIELD=0x08` immediately after row/col — hypothesis: a field-select byte
  picking which per-key sub-block this write targests (actuation vs RGB vs
  remap target). **Needs confirmation** by capturing a Custom-RGB write and a
  Keymap-remap write on the same key and diffing this byte.
- Payload `1a 1a 18 18` = `0x1a`=26→0.26mm (Key Trigger Travel, the value we
  set), `0x18`=24→0.24mm (Press/Release, unchanged default) — appears twice
  each, likely `[trigger_actuate, trigger_reset, press_something, release_something]`
  or normal-mode/RT-mode pairs. Needs a capture with the two travel sliders set
  to *different* values to disambiguate which byte is which.
- The long run of zero bytes before the payload is unexplained — likely reserved
  space for a bigger per-key struct (RGB bytes, remap keycode, SOCD flags, etc.
  all in the same fixed-size record, with `FIELD` selecting which sub-range the
  device should actually apply from this write).

## Capture workflow

1. Open `https://hed.aulacn.com/` in real Chrome/Brave (WebHID needs real USB access —
   the sandboxed preview browser can't see physical devices).
2. Paste `capture-instrumentation.js` into DevTools console (or have Claude inject it via
   the Claude-in-Chrome extension) **before** clicking "Allow browser access".
3. Grant device access in the native picker.
4. Before each isolated test: `window.__hidClear()`.
5. Perform exactly one action in the UI (e.g. one color click, one actuation slider drag).
6. Dump: paste into console —
   ```js
   const blob = new Blob([JSON.stringify(window.__hidLog, null, 1)], {type:'application/json'});
   const a = document.createElement('a'); a.href = URL.createObjectURL(blob);
   a.download = 'aula_NN_description.json'; a.click();
   ```
7. File lands in Downloads; copy into `re/captures/` and run `python analyze.py captures/FILE.json`.

### `09 00` — per-key Custom-mode RGB write (grouped, NOT row/col addressed)

Unlike actuation, per-key RGB uses **linear group indexing**, not row/col:
```
TX: 09 00 00 [GROUP=0..7] [LEN=0x36 or 0x12] [payload: RGB triplets]
RX: 09 00 00 00 (ack, all-zero payload)
```
- 8 groups (`idx` 0x00-0x07) × 8 keys/group = 64 keys total (matches WIN60HE key count)
- `LEN=0x36`(54) for full groups, `0x12`(18, =6 keys×3) for the last partial group (only 8 groups but board apparently has some groups with fewer live keys, or group 7 is smaller — needs confirmation)
- Each key = 3 bytes **RGB** (not BGR), in-order within the group, at a fixed
  byte offset window inside the 58-byte payload (observed base offset 44 for
  group idx=3, i.e. home-row group A/S/D/F/G/H/J/K) — exact offset formula per
  group still TBD, but stride is confirmed **3 bytes/key, R-G-B order**.
- Verified: setting key "F" (4th key in home-row group) to pure blue produced
  `[00 00 ff]` at the 4th triplet slot in group 3's payload, while A/S/D
  (already red from a saved preset) showed `[ff 00 00]` at slots 1-3.
- After any per-key write, the driver also re-sends **`07`** with a different
  `idx` (`0x0a` seen, vs `0x00` for Static mode) carrying `[mode=04][brightness][R G B]`
  of the *last edited* key — this looks like an "apply/notify" call, probably
  telling the firmware which lighting mode is active (Static vs Custom) rather
  than setting a real color; needs a capture with Custom mode + a full-keyboard
  paint to confirm group boundaries and the `07 idx` meaning.

## Actuation write has (at least) two formats — important gotcha

The `21 00 00 00 18 ...` write command has TWO different payload layouts depending
on keyboard state, and they are easy to conflate:

1. **Simple single-key format** (what `AulaProtocol.SetKeyActuation` uses today,
   confirmed end-to-end against real hardware for key "A" at row=12,col=0):
   `18 [row] [col] 00 08 <19 zero bytes> [triggerRaw][triggerRaw][releaseRaw][releaseRaw]`
   This is what you get with Rapid Trigger OFF.

2. **Bitmask format** (discovered while mapping more keys — NOT yet wired into the
   app): once "Rapid Trigger" + "Separate press/release sensitivity" are enabled
   in the web driver (which happened automatically partway through a capture
   session), single-key writes change shape entirely — the key identity becomes
   a single set bit somewhere in a ~22-byte field (`data[6]` through `data[27]`),
   e.g.:
   - Esc: `data[6] = 0x02` (bit 1)
   - LCtrl: `data[6] = 0x20` (bit 5)
   - Z: `data[8] = 0x10` (bit 4)
   - P: `data[16] = 0x04` (bit 2)
   - RShift: `data[18] = 0x10` (bit 4)
   - Enter: `data[20] = 0x08` (bit 3)
   - Back: `data[20] = 0x02` (bit 1)
   - Space: `data[12] = 0x20` (bit 5)

   This looks like `(byte_offset, bit_index)` = physical matrix (row, col), which
   would actually be a *cleaner* addressing scheme than format #1 if fully mapped
   — but doing so requires capturing every key in this bitmask mode specifically
   (Rapid Trigger + separate press/release both ON), not mixing modes like the
   `05_keymap_pass1.json` / `06_keymap_pass2.json` captures did (contaminated —
   don't trust the row/col values logged there for anything but Esc/LCtrl/A).

**Before mapping more keys**: pick ONE format, force the toggle state
deliberately at the start of the session (check the two checkboxes' state before
every capture), and don't let the driver auto-enable it mid-session.

## BREAKTHROUGH: full key map recovered from the site's own source (2026-08-20)

Mirrored `hed.aulacn.com` locally (`re/external/hed-mirror/`) and found the site
fetches `config/keys.json` and `config/device.json` (device.json confirms our
unit: vid `0x2E3C`, pid `0xC365`, product `SI2825KZHEARGB`, name "WIN 60 HE").
Neither JSON has the per-key matrix table though — that's built at runtime
into a `curDevice.keys` array, readable directly from DevTools console on the
live site (with keyboard connected):
```js
copy(JSON.stringify(curDevice.keys, null, 1))
```
Each key has an `index` field. **The bitmask (col, rowBit) address for
`SetKeyActuationBitmask` is simply `row = index/22` (integer division),
`col = index%22`**, found by reading the minified source directly
(`agreement.min.js`, function `openTriggerTest`, does exactly this divmod
when building the same 22-byte bitmask we reverse-engineered by hand).
This formula was cross-verified against every value we'd already confirmed by
hardware capture (Esc, 1, 6, Back, P, Enter, Z, RShift, LCtrl, Space): 100%
match. Then it filled in the entire rest of the board, including catching
that our earlier LWin/LAlt *guess* (col 2/4, interpolated) was wrong: real
values are col 1/2.

The full table lives in `re/external/hed-mirror/keys_win60he.json` and
`KeyboardLayout.cs` is now generated from it — every key on the WIN60HE is
mapped and writable. Also incidentally confirms `payload[5] = 0x0c` in the
bitmask write frame is the **trigger mode** field (12 = whatever mode the
site was in when we captured — not a mystery/noise byte as first assumed).

Also found in the same source dump: `resetTrigger()` sends
`21 00 00 00 18 0c 00 00 06` — resets ALL keys to firmware default in one
shot. Not yet wired into the app.

**Live per-key READ — confirmed, implemented (`AulaProtocol.ReadKeyActuationMm`)**:
found via `readTriggerData()` in the source. Uses row/col directly (NOT the
write's bitmask), subop `0x05` instead of the write's `0x0c`:
```
TX: 21 00 00 00 18 05 [row] [col]
RX: 21 00 00 00 0c 05 [mode] [travelLo] ... [travelHi at offset 11] ...
    travelRaw = resp[11]<<8 | resp[7]   (0.01mm units)
```
Verified with a write-then-read-back round trip on real hardware (wrote
0.77mm, read back exactly 77 raw / 0.77mm). Now wired into the app: reads
every mapped key automatically on Connect, plus a manual "Refresh from
Keyboard" button. First isolated single-shot tests (fresh connect, one read,
no write immediately before) returned identical stale-looking data for two
different keys — turned out to just be genuinely stale leftover state from
much earlier in the session, not a protocol bug; a clean write-then-read in
the same session resolved it immediately.

Bulk multi-key write (`setAnyTriggerValue`) is visible in the source too but
not yet decoded/needed — see Open questions.

**Repos that got us here**: HenryMDB/win60heupdate, HenryMDB/win60he,
caioalonso/win-68-he-tool, veysiemrah/aula-rgb-controller (user-supplied)
turned out to target different hardware (see "Investigated dead ends" below).
But the *idea* of mirroring the site and reading its JS directly is what
cracked this. Should have done this from the start.

## Lighting effects — all 16 confirmed (2026-08-20)

Same technique as the key map: clicked every Light Mode button on the live
site and read `curDevice.light.mode` after each (also cross-checked one via
raw `sendReport` capture). Real wire mode IDs, NOT sequential/guessed:

| Effect | mode | Effect | mode | Effect | mode |
|---|---|---|---|---|---|
| Static | 0 | Reactive | 6 | AutoRipple | 14 |
| Breath | 1 | Aurora | 7 | Striation | 15 |
| Wave | 2 | Ripple | 8 | Fireworks | 16 |
| Neon | 3 | Twinkle | 9 | MusicalRhythm | 100 (mic-driven, not wired up) |
| Radar | 4 | Custom | 10 | | |
| Cross | 11 | SpeedRespond | 12 | | |

Modes 5 and 13 aren't used by any UI button (reserved/unused).

Full `setLightValue()` frame (from source, matches our original "static red"
capture once correctly re-indexed — we'd mislabeled which byte was mode
vs speed early on, both happened to be harmless for Static specifically):
```
[cmd=7 main / 8 side] 00 00 00 [len=0x0e]
[mode] [speed] [brightness]
[fgR][fgG][fgB] [bgR][bgG][bgB]
[direction] [fullColor] [power?]
```
Implemented as `AulaProtocol.SetGlobalLighting(mode, speed, brightness, fg, bg, ...)`.
Note: this command sometimes doesn't return an ack within our read window even
though the write visibly succeeds (confirmed by eye on real hardware) — don't
treat a null response from this specific command as a hard failure.

## Reading back the active lighting state — DECODED (2026-08-20)

Connecting previously always showed Static/red in the app regardless of what the keyboard was
actually doing, because nothing read the live state back. Recovered from the site's own
`initLightValue()`/`initSideLightValue()`: query `07 01` for main lighting, `08 01` for side
lighting (63-byte zero-padded payload, same as every other command here).

```
TX: 07 01 (zero-padded)
RX: 07 .. .. .. [len] [mode][speed][brightness][fgR][fgG][fgB][bgR][bgG][bgB][direction][fullColor][power]
```

**Correction while implementing this**: my first pass at decoding the response, based on the
deobfuscated property-name assignment order in the minified source, had `speed` and
`brightness` swapped (read as `[mode][brightness][speed]`). A write-then-read round trip on
real hardware with deliberately distinct values (wrote speed=1, brightness=3) came back
speed=3, brightness=1 under that layout, proving it wrong immediately. The response's field
order actually matches the write frame's `[mode][speed][brightness]` exactly. Fixed in
`AulaProtocol.ReadGlobalLighting` before it ever shipped. Lesson: for anything where a
round-trip test is possible, run it, don't trust the deobfuscated variable names alone — the
string table can decode a property name to the wrong constant.

Implemented as `AulaProtocol.ReadGlobalLighting()`, wired into the app's Connect flow (and a
manual "Refresh from Keyboard" button on the Lighting page) so the UI reflects whatever effect,
speed, brightness, and color is actually active on the keyboard instead of resetting the view
to defaults on every connect.

## Per-key custom lighting (mode 10) — DECODED (2026-08-20)

Resolved by reading `setCustomLight()` directly in `agreement.min.js` instead
of continuing to guess from captures (the earlier "sparse/planar encoding"
theory below was wrong — the G-key capture was just mid-transition state from
a prior test, not evidence of a non-linear layout).

The real layout is a flat **396-byte virtual buffer** (`0x18c` = 3 bytes ×
132 max key slots), addressed by each key's `index` (the same site-defined
`index` used for the row/col bitmask formula, `row = index/22, col =
index%22`):
```
masterBuffer[3*index + 0] = R
masterBuffer[3*index + 1] = G
masterBuffer[3*index + 2] = B
```
This predicted the exact captured byte offset (27) for the Q key's write —
confirmed correct. The buffer is chunked into 54-byte (`0x36`) pieces and
sent as 8 packets:
```
TX: 09 [slot] [chunkHi] [chunkLo] [len] <chunk bytes>
```
7 full 54-byte chunks + 1 final 18-byte chunk (396 = 7×54 + 18). There is no
known per-key-only update or read — **every call sends the full buffer**, so
any key not explicitly included in a given call goes to (0,0,0) (off).
Implemented as `AulaProtocol.SetCustomKeyColors(keyColors, slot)`. Verified
against real hardware: single-key write and a multi-key write spanning
several chunks (Esc/G/Space, deliberately chosen to land in different
chunks) both acked and painted the correct keys.

## SOCD — DECODED (2026-08-20), corrects the earlier wrong dead-end below

**Earlier conclusion in this file was wrong.** The `0x24` traffic seen during
SOCD "Apply" clicks was dismissed as an unrelated `setPrcsPower`/`setPrcsData`
feature purely because "Prcs" didn't obviously read as "SOCD". Reading
`config/language.json`'s English strings settled it: `prcsTip1` = "Please
create SOCD key", `alertPrcsWarning` = "SOCD can only add up to 20!", and
`prcsModel1..4` are exactly the four SOCD resolution modes. **Prcs *is* SOCD**
— it was the right command all along, just misread once from the function
name alone instead of checking the UI strings.

Extracted the exact byte layout from `setPrcsData()`/`setPrcsPower()`:

**Pairs write** — 2 packets, 10 pair-slots per packet (max 20 pairs), 4 bytes/slot:
```
TX (×2, packet i=0,1): 24 00 00 [i] 0x28 <10 × [enabled=1][model][key1.hidCode][key2.hidCode]>
```
- `model` is **0-indexed on the wire** — confirmed directly from the site's
  model radio-button handler in `wmIndex.min.js`: `'prcsModel1'===id` sets
  `curPrcs.model = 0x0`, `'prcsModel2'` → `0x1`, `'prcsModel3'` → `0x2`,
  `'prcsModel4'` → `0x3`. An earlier version of this doc (and
  `AulaProtocol.SocdModel`) used 1-4, guessed from the read path without
  checking the write side — that shipped every model one value too high
  (selecting "FirstInterrupted" in the app showed up as "Model2" on the real
  web driver). Fixed in both places:
  `0`=FirstInterrupted ("the key that triggered first is interrupted"),
  `1`=Key1InterruptsKey2, `2`=Key2InterruptsKey1,
  `3`=LaterIgnored ("later-triggered key not executed, only first can interrupt").
- Unused slots left zeroed.

**Global on/off switch** — separate, single packet:
```
TX: 24 03 00 00 01 [0 or 1]
```

Implemented as `AulaProtocol.SetSocdPairs(pairs)` / `SetSocdEnabled(bool)`,
using each key's standard USB HID keycode (`KeyDef.HidCode`, sourced from the
live `curDevice.keys` dump). CLI-verified: re-applied the device's existing
real-world pairing (A↔D, Model1) via `socd-test` — both packets acked. Now
wired into the app's SOCD page (Global Switch card, Add Pair card, Pairs
list) in `MainWindow.xaml`/`.xaml.cs`.

## Investigated dead ends

- **`22 80` bulk table is NOT a live actuation readout.** It streams 40 linear-
  indexed entries in response to one query (confirmed: request `22 80 00 00`,
  then `ReadNext()` 39 more times), but every entry stays `0x28` (default)
  regardless of what's actually been written via the confirmed `21`-family
  write commands. It's some other static/default config table, not per-key
  current state. No confirmed read command exists yet for either actuation
  write format — the app only tracks "last value this app itself wrote."
- **Community repos checked for a shortcut (2026-08-20)**: HenryMDB/win60he,
  HenryMDB/win60heupdate, caioalonso/win-68-he-tool are mirrors of AULA's
  *newer* web driver (`win.aulacn.com`), but target different VIDs entirely
  (7247, 7330, 7331, 7333, 6790 — none match our device's `0x2e3c`) and use a
  completely different wire format (`[head=0x5c][len][cmd][checksum][data]`
  with checksums, vs. our device's flat `[cmd][subcmd][00][idx][...]`, no
  checksum). AULA ships multiple unrelated controller generations under the
  same product name — these don't apply to this specific unit. Similarly,
  veysiemrah/aula-rgb-controller targets the AULA F87 TK (VID `0x258A`),
  a different protocol family (HID Feature Reports, 520-byte packets).
- **Row width does not reliably predict matrix column-slot count.** Row 1's
  formula (1 slot/key, wide keys consume more) does NOT extend to row 2: `Tab`
  (1.5u) empirically only consumes 1 slot, while `LShift` (2.25u) consumes 2 —
  same-ish width, different behavior. Don't extrapolate further rows from
  width alone; get a second real per-row anchor first.

## Open questions / next captures needed

- [x] Per-key custom RGB (Light Mode → Custom) — decoded, see section above; implemented + verified
- [x] SOCD pairing — decoded, see section above; implemented + verified
- [ ] Actuation point per key (Actuation tab slider) — expect a write to `22 80` family or similar, need isolated capture
- [ ] Rapid trigger on/off + sensitivity — Actuation tab
- [ ] Key remap (Keymap → Remap Key) — expect real `21`-family or different CMD write, distinct from the read-only matrix scan
- [ ] Macro record/playback — Keymap → Macro tab
- [ ] Key Combination tab
- [ ] Profile switch (Profile 1-4) — likely a single-byte "active profile" write
- [ ] WIN75HE — repeat handshake capture once WIN60HE feature set is fully mapped; likely same protocol family, different key count/matrix size
