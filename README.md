# AulaControl

A native Windows driver for AULA Hall-Effect keyboards (WIN60HE / WIN75HE), built by
reverse-engineering AULA's official web driver ([hed.aulacn.com](https://hed.aulacn.com/)) —
because AULA doesn't ship a native app, only a browser-based one using WebHID.

No official SDK, no documentation from AULA — every command in this app was recovered by
mirroring the web driver locally and reading its own JavaScript source, then verifying each
one against real hardware. The full write-up of what was found is in
[`re/protocol.md`](re/protocol.md).

## Features

- **Lighting** — all 16 built-in effects (Static, Breath, Wave, Neon, Radar, Reactive, Aurora,
  Ripple, Twinkle, Cross, SpeedRespond, AutoRipple, Striation, Fireworks, plus Custom), a
  saturation/value + hue color picker, speed/brightness control, and rainbow mode.
- **Individual key lighting** — paint each key its own color (Custom mode). Click, drag across
  keys, or Ctrl+click to multi-select, then paint the selection at once.
- **Actuation (Hall-Effect travel)** — read and write per-key trigger travel in mm, with the
  same drag/Ctrl+click multi-select for applying a value to several keys at once.
- **SOCD (Snap Tap)** — configure up to 20 key pairs with any of the 4 resolution models, plus
  a global on/off switch. Existing pairs already stored on the keyboard are read back and shown
  automatically on connect.
- **Raw console** — send arbitrary hex payloads directly to the device for further protocol
  exploration.

## Screenshots

<!-- Drop screenshots in a `docs/` or `screenshots/` folder and swap these placeholders,
     e.g. ![Lighting page](docs/lighting.png) -->

| Lighting | Actuation |
|---|---|
| _screenshot here_ | _screenshot here_ |

| SOCD | Individual Key Colors |
|---|---|
| _screenshot here_ | _screenshot here_ |

## Supported hardware

Currently confirmed against the **WIN60HE** (VID `0x2E3C`, PID `0xC365`). WIN75HE support is
planned — same protocol family, just needs its key matrix mapped (see
[`re/protocol.md`](re/protocol.md#open-questions--next-captures-needed)).

## Getting started

**Requirements:** Windows, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
git clone https://github.com/sgtflixy/AulaControl.git
cd AulaControl/app
dotnet run
```

Close AULA's own web driver tab (or any other app holding the HID device) before connecting —
only one process can talk to the keyboard at a time.

### Building a standalone .exe

```bash
cd app
dotnet publish -c Release -r win-x64 --self-contained false
```

The output lands in `app/bin/Release/net8.0-windows/win-x64/publish/`.

## Repository layout

```
app/    WPF desktop application (the actual driver UI)
cli/    Command-line tool used to test protocol commands against real hardware
re/     Reverse-engineering notes, HID captures, and the mirrored-site research log
```

## How this was built

Short version: AULA's web driver is a WebHID app, so the entire protocol lives in its
client-side JavaScript. Mirroring the site locally and reading that source directly (rather
than guessing from packet captures) is what made most of this possible — see
[`re/protocol.md`](re/protocol.md) for the full log of what was tried, what worked, and what
turned out to be wrong along the way.

## Contributing

Pull requests are welcome — WIN75HE key-matrix mapping, macro/keymap support, and Profile
switching are the biggest open gaps (see the checklist at the bottom of
[`re/protocol.md`](re/protocol.md)).

## Disclaimer

Not affiliated with or endorsed by AULA. Built independently by reverse-engineering AULA's own
publicly-accessible web driver for personal/community use. Use at your own risk — writing raw
HID commands to your keyboard's firmware always carries some risk, however small.

## License

Apache License 2.0 — see [LICENSE](LICENSE).
