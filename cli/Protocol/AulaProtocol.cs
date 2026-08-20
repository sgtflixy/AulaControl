using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AulaControl.Hid;

namespace AulaControl.Protocol
{
    public sealed class DeviceInfo
    {
        public string Model = "";
        public string BuildDate = "";
    }

    /// <summary>
    /// High-level command builders for the confirmed parts of the protocol.
    /// Anything not listed here is either unconfirmed or read-only telemetry —
    /// see re/protocol.md for the full reverse-engineering log and open items.
    /// </summary>
    public sealed class AulaProtocol
    {
        private readonly AulaDevice _dev;

        public AulaProtocol(AulaDevice dev) => _dev = dev;

        // ---- cmd 0x0d: device info strings ----------------------------------

        private static readonly string[] MonthNames =
        {
            "Jan", "Feb", "Mar", "Apr", "May", "Jun",
            "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"
        };

        public DeviceInfo? GetDeviceInfo()
        {
            // A single 0x0d query returns TWO queued packets (model, then build
            // date) — not one packet per query. Read both from one request.
            var a = _dev.SendRaw(new byte[] { 0x0d, 0x00, 0x00, 0x00 });
            var b = _dev.ReadNext();
            if (a == null) return null;

            string sa = DecodeAsciiTail(a);
            string sb = b != null ? DecodeAsciiTail(b) : "";

            bool aIsDate = MonthNames.Any(m => sa.StartsWith(m, StringComparison.Ordinal));
            return new DeviceInfo
            {
                Model = aIsDate ? sb : sa,
                BuildDate = aIsDate ? sa : sb
            };
        }

        private static string DecodeAsciiTail(byte[] resp)
        {
            // response = [CMD][SUBCMD][00][IDX][LEN][ascii bytes...][zero padding]
            if (resp.Length <= 5) return "";
            int end = 5;
            while (end < resp.Length && resp[end] != 0) end++;
            return Encoding.ASCII.GetString(resp, 5, end - 5);
        }

        // ---- cmd 0x07/0x08: global lighting (mode/speed/brightness/RGB) ----

        /// <summary>
        /// All 16 lighting effects available on the WIN60HE, with their real
        /// wire mode IDs — recovered directly from the site's own JS state
        /// (curDevice.light.mode) by clicking through every effect button and
        /// reading back the live value. NOT a guess. MusicalRhythm (id 100)
        /// uses the mic and isn't wired up here (no color/speed params apply).
        /// </summary>
        public enum LightMode : byte
        {
            Static = 0,
            Breath = 1,
            Wave = 2,
            Neon = 3,
            Radar = 4,
            Reactive = 6,
            Aurora = 7,
            Ripple = 8,
            Twinkle = 9,
            Custom = 10,
            Cross = 11,
            SpeedRespond = 12,
            AutoRipple = 14,
            Striation = 15,
            Fireworks = 16,
        }

        /// <summary>
        /// Sets the whole-keyboard lighting effect. speed/brightness are raw
        /// 0-4. Confirmed format (from the site's own setLightValue()):
        /// [cmd=7(main)/8(side)] 00 00 00 [len=0x0e] [mode] [speed] [brightness]
        /// [fgR][fgG][fgB] [bgR][bgG][bgB] [direction] [fullColor] [00].
        /// The trailing byte is always 0 in every real capture — an earlier
        /// version of this guessed it meant "power on" (0x01) and sent that,
        /// which made the device silently ignore the whole command (found by
        /// testing the app's Apply button vs. a known-working raw payload).
        /// fullColor confirmed on real hardware: 0 = use fgR/fgG/fgB as a
        /// fixed color for the effect, 1 = ignore fgR/fgG/fgB and auto-cycle
        /// through all hues ("rainbow" look) instead.
        /// </summary>
        public bool SetGlobalLighting(
            LightMode mode, byte speed0to4, byte brightness0to4,
            byte fgR, byte fgG, byte fgB,
            byte bgR = 0, byte bgG = 0, byte bgB = 0,
            byte direction = 0, byte fullColor = 0, bool sideLight = false)
        {
            if (speed0to4 > 4) throw new ArgumentOutOfRangeException(nameof(speed0to4));
            if (brightness0to4 > 4) throw new ArgumentOutOfRangeException(nameof(brightness0to4));

            var payload = new byte[]
            {
                (byte)(sideLight ? 0x08 : 0x07), 0x00, 0x00, 0x00,
                0x0e, (byte)mode, speed0to4, brightness0to4,
                fgR, fgG, fgB,
                bgR, bgG, bgB,
                direction, fullColor, 0x00
            };
            var resp = _dev.SendRaw(payload);
            return resp != null;
        }

        /// <summary>Convenience wrapper for the common case: static whole-keyboard color.</summary>
        public bool SetGlobalStaticColor(byte brightness0to4, byte r, byte g, byte b) =>
            SetGlobalLighting(LightMode.Static, speed0to4: 4, brightness0to4, r, g, b);

        public sealed class GlobalLightingState
        {
            public LightMode Mode;
            public byte Brightness;
            public byte Speed;
            public byte FgR, FgG, FgB;
            public byte BgR, BgG, BgB;
            public byte Direction;
            public byte FullColor;
        }

        /// <summary>
        /// Reads the keyboard's currently active lighting state. Recovered
        /// from the site's own initLightValue()/initSideLightValue() (query
        /// `07 01` for main / `08 01` for side). The response field order
        /// matches the write frame's [mode][speed][brightness] — confirmed
        /// with a write-then-read round trip using distinct speed/brightness
        /// values on real hardware, since an initial attempt at reading this
        /// straight from the minified source's property-name deobfuscation
        /// (0x1687 string table) had speed and brightness swapped.
        /// Frame: `07 01` (zero-padded to 63 bytes) → response
        /// `07 .. .. .. [len] [mode][speed][brightness][fgR][fgG][fgB]
        /// [bgR][bgG][bgB][direction][fullColor][power]`.
        /// </summary>
        public GlobalLightingState? ReadGlobalLighting(bool sideLight = false)
        {
            var payload = new byte[63];
            payload[0] = (byte)(sideLight ? 0x08 : 0x07);
            payload[1] = 0x01;
            var resp = _dev.SendRaw(payload);
            if (resp == null) return null;

            int len = resp[4];
            const int off = 5;
            if (len < 12 || resp.Length < off + 12) return null;

            return new GlobalLightingState
            {
                Mode = (LightMode)resp[off + 0],
                Speed = resp[off + 1],
                Brightness = resp[off + 2],
                FgR = resp[off + 3], FgG = resp[off + 4], FgB = resp[off + 5],
                BgR = resp[off + 6], BgG = resp[off + 7], BgB = resp[off + 8],
                Direction = resp[off + 9],
                FullColor = resp[off + 10],
            };
        }

        // ---- cmd 0x09: per-key custom RGB (Lighting > Custom mode) -----------

        /// <summary>
        /// Sets per-key colors for Custom lighting mode. Recovered directly
        /// from the site's own setCustomLight() source — not a guess, and
        /// verified byte-for-byte against a real capture (predicted payload
        /// offset 27 for key "Q" [index 45]; actual capture matched exactly).
        ///
        /// The device holds one big virtual buffer of 3 bytes per key,
        /// indexed by the SAME "index" used for actuation (index = row*22+col
        /// in our KeyboardLayout terms): masterBuffer[3*index+0/1/2] = R/G/B.
        /// That buffer is chunked into 54-byte pieces and sent as separate
        /// `09 [slot] [chunkHi] [chunkLo] [len] ...54 bytes...` frames (8
        /// chunks total for ~132 key slots; the last chunk is 18 bytes).
        ///
        /// IMPORTANT: this sends the FULL buffer each call — there is no
        /// per-key-only update, so any key not present in
        /// <paramref name="keyColors"/> goes to (0,0,0) (off) for this call.
        /// Callers should pass every key they want lit, every time (the app
        /// keeps a local dictionary of "current" per-key colors for this).
        /// See <see cref="ReadCustomKeyColors"/> for the read-back counterpart
        /// (an earlier version of this doc wrongly said no read existed).
        /// </summary>
        public bool SetCustomKeyColors(IReadOnlyDictionary<int, (byte r, byte g, byte b)> keyColors, byte slot = 0)
        {
            const int bufferLen = 396; // 0x18c, matches the site's ArrayBuffer(0x18c)
            const int chunkSize = 54;  // 0x36

            var master = new byte[bufferLen];
            foreach (var (index, color) in keyColors)
            {
                int off = 3 * index;
                if (off + 2 >= bufferLen) continue;
                master[off] = color.r;
                master[off + 1] = color.g;
                master[off + 2] = color.b;
            }

            int chunkCount = (int)Math.Ceiling(bufferLen / (double)chunkSize);
            bool allOk = true;
            for (int chunk = 0; chunk < chunkCount; chunk++)
            {
                int start = chunk * chunkSize;
                int len = Math.Min(chunkSize, bufferLen - start);

                var payload = new byte[5 + len];
                payload[0] = 0x09;
                payload[1] = slot;
                payload[2] = (byte)((chunk >> 8) & 0xff);
                payload[3] = (byte)(chunk & 0xff);
                payload[4] = (byte)len;
                Array.Copy(master, start, payload, 5, len);

                var resp = _dev.SendRaw(payload);
                if (resp == null) allOk = false;
            }
            return allOk;
        }

        /// <summary>
        /// Reads back the per-key Custom-mode colors currently stored on the
        /// keyboard. Recovered from the site's own initCustomLightValue():
        /// query `09 80` (slot 0) or `09 81` (slot 1), then — same FIFO-pop
        /// quirk as the 0x0d device-info and 0x24 SOCD reads — the device
        /// pushes 8 response packets (chunk indices 0-7) from that one query.
        /// Each response: [09][80 or 81][chunkHi][chunkLo][len]
        /// &lt;len/3 RGB triplets&gt;, where key index = 18*chunk + triplet
        /// position (18 = 54-byte chunk / 3 bytes-per-key, matching the
        /// chunking in SetCustomKeyColors). Keys never painted come back as
        /// (0,0,0) — callers should treat that as "off", not a real color.
        /// </summary>
        public Dictionary<int, (byte r, byte g, byte b)> ReadCustomKeyColors(byte slot = 0)
        {
            var result = new Dictionary<int, (byte, byte, byte)>();
            var payload = new byte[63];
            payload[0] = 0x09;
            payload[1] = (byte)(slot == 0 ? 0x80 : 0x81);

            var first = _dev.SendRaw(payload);
            if (first == null) return result;
            ParseCustomLightChunk(first, result);

            for (int i = 0; i < 7; i++)
            {
                var next = _dev.ReadNext();
                if (next == null) break;
                ParseCustomLightChunk(next, result);
            }
            return result;
        }

        private static void ParseCustomLightChunk(byte[] resp, Dictionary<int, (byte, byte, byte)> into)
        {
            if (resp.Length < 5 || resp[0] != 0x09) return;
            int chunk = (resp[2] << 8) | resp[3];
            int len = resp[4];
            if (resp.Length < 5 + len) return;

            int count = len / 3;
            for (int i = 0; i < count; i++)
            {
                int off = 5 + 3 * i;
                into[18 * chunk + i] = (resp[off], resp[off + 1], resp[off + 2]);
            }
        }

        // ---- cmd 0x24: SOCD ("Prcs" internally — confirmed via language.json:
        //      prcsTip1 = "Please create SOCD key") --------------------------

        /// <summary>
        /// Wire values are 0-indexed — confirmed from the site's own model
        /// radio-button handler in wmIndex.min.js: 'prcsModel1' sets
        /// curPrcs.model = 0x0, 'prcsModel2' = 0x1, 'prcsModel3' = 0x2,
        /// 'prcsModel4' = 0x3. An earlier version of this enum used 1-4
        /// (assumed from the read path without checking the write-side
        /// mapping), which sent every model one value higher than intended —
        /// e.g. "FirstInterrupted" wrote wire value 1, which the device
        /// actually treats as Model2 (Key1InterruptsKey2). Confirmed against
        /// the live web driver: selecting "FirstInterrupted" here showed up
        /// as "Model2" there before this fix.
        /// </summary>
        public enum SocdModel : byte
        {
            /// <summary>The key that triggered first is interrupted.</summary>
            FirstInterrupted = 0,
            /// <summary>Key1 interrupts the triggering of Key2.</summary>
            Key1InterruptsKey2 = 1,
            /// <summary>Key2 interrupts the triggering of Key1.</summary>
            Key2InterruptsKey1 = 2,
            /// <summary>Later-triggered key is not executed; only the first triggered key can interrupt.</summary>
            LaterIgnored = 3,
        }

        /// <summary>
        /// Writes the full SOCD pair list (replaces whatever's currently set —
        /// there's no per-pair update, same as per-key lighting). Recovered
        /// directly from the site's setPrcsData(): up to 20 pairs, 10 per
        /// packet across 2 packets, 4 bytes each: [enabled=1][model][key1
        /// hidCode][key2 hidCode]. Pass an empty list to clear all pairs.
        /// </summary>
        public bool SetSocdPairs(IReadOnlyList<(byte key1HidCode, byte key2HidCode, SocdModel model)> pairs)
        {
            if (pairs.Count > 20) throw new ArgumentOutOfRangeException(nameof(pairs), "Max 20 SOCD pairs.");

            bool allOk = true;
            for (int packet = 0; packet < 2; packet++)
            {
                var payload = new byte[63];
                payload[0] = 0x24;
                payload[1] = 0x00;
                payload[2] = 0x00;
                payload[3] = (byte)packet;
                payload[4] = 0x28; // 40 = 10 slots * 4 bytes

                for (int slot = 0; slot < 10; slot++)
                {
                    int i = 10 * packet + slot;
                    if (i >= pairs.Count) continue;
                    var (k1, k2, model) = pairs[i];
                    int off = 4 * slot + 5;
                    payload[off] = 0x01;
                    payload[off + 1] = (byte)model;
                    payload[off + 2] = k1;
                    payload[off + 3] = k2;
                }

                var resp = _dev.SendRaw(payload);
                if (resp == null) allOk = false;
            }
            return allOk;
        }

        /// <summary>Global SOCD on/off switch (the "Switch" toggle on the SOCD page). Frame: 24 03 00 00 01 [0/1].</summary>
        public bool SetSocdEnabled(bool enabled)
        {
            var payload = new byte[] { 0x24, 0x03, 0x00, 0x00, 0x01, (byte)(enabled ? 1 : 0) };
            var resp = _dev.SendRaw(payload);
            return resp != null;
        }

        public readonly struct SocdPairRaw
        {
            public readonly byte Key1HidCode;
            public readonly byte Key2HidCode;
            public readonly SocdModel Model;
            public SocdPairRaw(byte key1, byte key2, SocdModel model)
            {
                Key1HidCode = key1; Key2HidCode = key2; Model = model;
            }
        }

        /// <summary>
        /// Reads the SOCD pairs currently stored on the keyboard. Recovered
        /// from the site's initPrcsData() / inputreport handler: query with
        /// `24 01`, then (same FIFO-pop quirk as the 0x0d device-info read)
        /// the device pushes TWO response packets back-to-back — one query,
        /// two reads. Each response: [24][01][idxHi][idxLo][len=0x28]
        /// &lt;10 slots x 4 bytes: [enabled][model][key1 hid][key2 hid]&gt;.
        /// A slot is empty when enabled==0, or the legacy marker
        /// (7,0xff,0xff,0xff) — both skipped, matching the site's own check.
        /// </summary>
        public List<SocdPairRaw> ReadSocdPairs()
        {
            var result = new List<SocdPairRaw>();
            var payload = new byte[63];
            payload[0] = 0x24;
            payload[1] = 0x01;

            var first = _dev.SendRaw(payload);
            if (first == null) return result;
            ParseSocdPairsPacket(first, result);

            var second = _dev.ReadNext();
            if (second != null) ParseSocdPairsPacket(second, result);

            return result;
        }

        private static void ParseSocdPairsPacket(byte[] resp, List<SocdPairRaw> into)
        {
            if (resp.Length < 45 || resp[0] != 0x24 || resp[1] != 0x01) return;
            for (int slot = 0; slot < 10; slot++)
            {
                int off = 5 + 4 * slot;
                byte enabled = resp[off], model = resp[off + 1], k1 = resp[off + 2], k2 = resp[off + 3];
                bool isLegacyEmptyMarker = enabled == 7 && model == 0xff && k1 == 0xff && k2 == 0xff;
                if (enabled == 0 || isLegacyEmptyMarker) continue;
                into.Add(new SocdPairRaw(k1, k2, (SocdModel)model));
            }
        }

        /// <summary>Reads the global SOCD on/off switch. Frame: 24 02 (query), response byte[5] = 0/1.</summary>
        public bool? ReadSocdEnabled()
        {
            var payload = new byte[63];
            payload[0] = 0x24;
            payload[1] = 0x02;
            var resp = _dev.SendRaw(payload);
            if (resp == null || resp.Length < 6) return null;
            return resp[5] == 1;
        }

        // ---- cmd 0x21: bitmask-addressed per-key actuation -------------------

        /// <summary>
        /// Sets actuation travel for a key addressed by (col, rowBit). This is
        /// the site's own addressing scheme, recovered directly from the
        /// official driver's live per-key state (DevTools: curDevice.keys),
        /// where each key has an "index" and: row = index/22, col = index%22.
        /// Confirmed against real hardware for every key on the board — see
        /// re/protocol.md and re/external/hed-mirror/keys_win60he.json for the
        /// full index table this project's KeyboardLayout.cs is built from.
        /// Frame: 21 00 00 00 18 [0c] &lt;zeros&gt; [bit at 6+col] &lt;zeros&gt; [triggerRaw][triggerRaw]
        /// bit = 1 &lt;&lt; rowBit1to5.
        /// </summary>
        public bool SetKeyActuationBitmask(byte col, byte rowBit1to5, double triggerMm)
        {
            if (rowBit1to5 < 1 || rowBit1to5 > 5) throw new ArgumentOutOfRangeException(nameof(rowBit1to5));
            byte triggerRaw = (byte)Math.Round(triggerMm * 100);

            var payload = new byte[63];
            payload[0] = 0x21;
            payload[1] = 0x00;
            payload[2] = 0x00;
            payload[3] = 0x00;
            payload[4] = 0x18;
            payload[5] = 0x0c;
            payload[6 + col] = (byte)(1 << rowBit1to5);
            payload[28] = triggerRaw;
            payload[29] = triggerRaw;

            var resp = _dev.SendRaw(payload);
            return resp != null;
        }

        /// <summary>
        /// Reads a key's current actuation trigger travel in mm, addressed by
        /// the same (row, col) = (index/22, index%22) scheme as the bitmask
        /// write — but the read request itself uses row/col directly (not a
        /// bitmask), with subop 0x05 instead of the write's 0x0c. Recovered
        /// from the site's own `readTriggerData()` and confirmed against real
        /// hardware with a write-then-read-back round trip.
        /// Frame: 21 00 00 00 18 05 [row] [col]
        /// Response: mode = resp[6], travelRaw = resp[11]&lt;&lt;8 | resp[7] (raw
        /// 0.01mm units, so divide by 100 for mm).
        /// </summary>
        public double? ReadKeyActuationMm(byte row, byte col)
        {
            var resp = _dev.SendRaw(new byte[] { 0x21, 0x00, 0x00, 0x00, 0x18, 0x05, row, col });
            if (resp == null || resp.Length <= 11) return null;
            int travelRaw = (resp[11] << 8) | resp[7];
            return travelRaw / 100.0;
        }
    }
}
