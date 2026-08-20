using System.Collections.Generic;

namespace AulaControl.Protocol
{
    public enum ActuationFormat
    {
        /// <summary>Column byte-offset + row-as-bit addressing — see AulaProtocol.SetKeyActuationBitmask.</summary>
        Bitmask,
    }

    public sealed class KeyDef
    {
        // NOTE: these must be properties, not plain fields — WPF's {Binding}
        // engine (used by the SOCD Key1/Key2 combo ItemTemplates) silently
        // resolves to nothing against a plain public field, which was the
        // real cause of the blank dropdown text (not a styling/template bug).
        public string Label { get; set; } = "";
        public double Width { get; set; } = 1.0; // in 1u keycap units
        public byte? Row { get; set; }           // bitmask row-bit (1-5)
        public byte? Col { get; set; }           // bitmask column byte-offset
        public ActuationFormat? Format { get; set; }

        /// <summary>Standard USB HID keycode (from the site's curDevice.keys). Used for SOCD pairs.</summary>
        public byte? HidCode { get; set; }

        /// <summary>
        /// The last actuation trigger value this app itself wrote to this key,
        /// in mm. Not a live hardware read (no confirmed read command yet) —
        /// null until this app applies a value here.
        /// </summary>
        public double? LastKnownTriggerMm { get; set; }

        public bool IsMapped => Row.HasValue && Col.HasValue && Format.HasValue;

        /// <summary>
        /// Reconstructs the site's own per-key "index" (row = index/22, col =
        /// index%22, so index = row*22+col) — used for per-key custom RGB
        /// addressing (AulaProtocol.SetCustomKeyColors), which is keyed by
        /// this same index rather than the bitmask (row,col) pair.
        /// </summary>
        public int? Index => IsMapped ? Row!.Value * 22 + Col!.Value : null;

        public override string ToString() => Label;
    }

    /// <summary>
    /// Standard 60% ANSI layout matching the WIN60HE on-screen keyboard in the
    /// official web driver. Row/Col come from the site's own per-key "index"
    /// field (read live from a connected device's `curDevice.keys` via
    /// DevTools: row = index/22, col = index%22 — this is the site's actual
    /// formula, confirmed to match every value we'd independently verified by
    /// hardware capture). HidCode values are the standard USB HID keycodes
    /// from that same live dump. See re/protocol.md for the derivation and
    /// the full index table (re/external/hed-mirror/keys_win60he.json).
    /// </summary>
    public static class KeyboardLayout
    {
        private static KeyDef K(string label, double width, int index, int hidCode) => new()
        {
            Label = label,
            Width = width,
            Row = (byte)(index / 22),
            Col = (byte)(index % 22),
            Format = ActuationFormat.Bitmask,
            HidCode = (byte)hidCode,
        };

        public static readonly List<List<KeyDef>> Win60Rows = new()
        {
            new()
            {
                K("Esc", 1, 22, 41),
                K("1", 1, 23, 30), K("2", 1, 24, 31), K("3", 1, 25, 32), K("4", 1, 26, 33),
                K("5", 1, 27, 34), K("6", 1, 28, 35), K("7", 1, 29, 36), K("8", 1, 30, 37),
                K("9", 1, 31, 38), K("0", 1, 32, 39), K("-", 1, 33, 45), K("=", 1, 34, 46),
                K("Back", 2, 36, 42),
            },
            new()
            {
                K("Tab", 1.5, 44, 43),
                K("Q", 1, 45, 20), K("W", 1, 46, 26), K("E", 1, 47, 8), K("R", 1, 48, 21),
                K("T", 1, 49, 23), K("Y", 1, 50, 28), K("U", 1, 51, 24), K("I", 1, 52, 12),
                K("O", 1, 53, 18), K("P", 1, 54, 19),
                K("[", 1, 55, 47), K("]", 1, 56, 48), K("\\", 1.5, 58, 49),
            },
            new()
            {
                K("Caps", 1.75, 66, 57),
                K("A", 1, 68, 4), K("S", 1, 69, 22), K("D", 1, 70, 7), K("F", 1, 71, 9),
                K("G", 1, 72, 10), K("H", 1, 73, 11), K("J", 1, 74, 13), K("K", 1, 75, 14),
                K("L", 1, 76, 15), K(";", 1, 77, 51), K("'", 1, 78, 52),
                K("Enter", 2.25, 80, 40),
            },
            new()
            {
                K("Shift", 2.25, 88, 225),
                K("Z", 1, 90, 29), K("X", 1, 91, 27), K("C", 1, 92, 6), K("V", 1, 93, 25),
                K("B", 1, 94, 5), K("N", 1, 95, 17), K("M", 1, 96, 16), K(",", 1, 97, 54),
                K(".", 1, 98, 55), K("/", 1, 99, 56),
                K("Shift", 2.75, 100, 229),
            },
            new()
            {
                K("Ctrl", 1.25, 110, 224), K("Win", 1.25, 111, 227), K("Alt", 1.25, 112, 226),
                K("Space", 6.25, 116, 44),
                K("Alt", 1.25, 119, 230), K("Menu", 1, 120, 101), K("Ctrl", 1.25, 121, 228),
                K("Fn", 0.75, 122, 250),
            },
        };
    }
}
