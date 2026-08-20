using System.Collections.Generic;
using System.IO;
using System.Linq;
using AulaControl.Hid;
using AulaControl.Protocol;

var dev = new AulaDevice();
Console.WriteLine("Connecting...");
if (!dev.Connect())
{
    Console.WriteLine("FAILED to connect. Is the keyboard plugged in and not held open by another app (e.g. a browser tab with WebHID)?");
    return 1;
}
Console.WriteLine($"Connected: {dev.DevicePath}");

var proto = new AulaProtocol(dev);
var info = proto.GetDeviceInfo();
if (info == null)
{
    Console.WriteLine("Connected but GetDeviceInfo() returned null.");
}
else
{
    Console.WriteLine($"Model: {info.Model}");
    Console.WriteLine($"Build date: {info.BuildDate}");
}

if (args.Length > 0 && args[0] == "lighting-roundtrip")
{
    // distinct speed vs brightness to confirm the read response's field order
    Console.WriteLine("Writing Breath, speed=1, brightness=3, color=(10,20,30)...");
    proto.SetGlobalLighting(AulaProtocol.LightMode.Breath, speed0to4: 1, brightness0to4: 3, 10, 20, 30);
    var state = proto.ReadGlobalLighting();
    Console.WriteLine(state == null ? "No response." :
        $"read back: mode={state.Mode} brightness={state.Brightness} speed={state.Speed} fg=({state.FgR},{state.FgG},{state.FgB})");
}

if (args.Length > 0 && args[0] == "lighting-read")
{
    var state = proto.ReadGlobalLighting();
    if (state == null) Console.WriteLine("No response.");
    else Console.WriteLine($"mode={state.Mode} brightness={state.Brightness} speed={state.Speed} " +
        $"fg=({state.FgR},{state.FgG},{state.FgB}) bg=({state.BgR},{state.BgG},{state.BgB}) " +
        $"direction={state.Direction} fullColor={state.FullColor}");
}

if (args.Length > 0 && args[0] == "socd-read")
{
    var enabled = proto.ReadSocdEnabled();
    Console.WriteLine("SOCD enabled: " + (enabled?.ToString() ?? "(no response)"));
    var pairs = proto.ReadSocdPairs();
    Console.WriteLine($"Pairs ({pairs.Count}):");
    foreach (var p in pairs)
        Console.WriteLine($"  hid1={p.Key1HidCode} hid2={p.Key2HidCode} model={p.Model}");
}

if (args.Length > 0 && args[0] == "socd-test")
{
    // A/D as SOCD pair, Model1 (matches the pre-existing SOCD1 config we saw earlier)
    Console.WriteLine("Setting SOCD pair A(hid=4) <-> D(hid=7), Model1...");
    var pairs = new List<(byte, byte, AulaProtocol.SocdModel)>
    {
        (4, 7, AulaProtocol.SocdModel.FirstInterrupted),
    };
    bool ok = proto.SetSocdPairs(pairs);
    Console.WriteLine(ok ? "OK (both packets acked)." : "No ack on at least one packet — check the SOCD tab.");
}

if (args.Length > 0 && args[0] == "custom-multi")
{
    // Spread across multiple chunks: Esc(22, low index), G(72), Space(116, high index)
    Console.WriteLine("Setting Custom mode, then Esc=red, G=green, Space=blue...");
    proto.SetGlobalLighting(AulaProtocol.LightMode.Custom, 4, 4, 0, 0, 0);
    var colors = new Dictionary<int, (byte, byte, byte)>
    {
        [22] = (255, 0, 0),
        [72] = (0, 255, 0),
        [116] = (0, 0, 255),
    };
    bool ok = proto.SetCustomKeyColors(colors);
    Console.WriteLine(ok ? "OK (all chunks acked)." : "Some chunk(s) had no ack — check the keyboard.");
}

if (args.Length > 0 && args[0] == "custom-key")
{
    int index = int.Parse(args[1]);
    byte r = byte.Parse(args[2]), g = byte.Parse(args[3]), b = byte.Parse(args[4]);
    Console.WriteLine($"Setting Custom mode, then key index={index} to ({r},{g},{b})...");
    proto.SetGlobalLighting(AulaProtocol.LightMode.Custom, 4, 4, 0, 0, 0);
    var colors = new Dictionary<int, (byte, byte, byte)> { [index] = (r, g, b) };
    bool ok = proto.SetCustomKeyColors(colors);
    Console.WriteLine(ok ? "OK (all chunks acked)." : "Some chunk(s) had no ack — check the keyboard.");
}

if (args.Length > 0 && args[0] == "lighting-effect")
{
    var mode = Enum.Parse<AulaProtocol.LightMode>(args[1], true);
    Console.WriteLine($"Setting effect {mode} via AulaProtocol.SetGlobalLighting...");
    byte fullColor = args.Length > 2 ? byte.Parse(args[2]) : (byte)0;
    bool ok = proto.SetGlobalLighting(mode, speed0to4: 4, brightness0to4: 4, 255, 0, 0, fullColor: fullColor);
    Console.WriteLine(ok ? "OK (ack received)." : "No ack (may still have worked — check the keyboard).");
}

if (args.Length > 0 && args[0] == "lighting-red")
{
    Console.WriteLine("Setting global static color to red, brightness 4...");
    bool ok = proto.SetGlobalStaticColor(4, 255, 0, 0);
    Console.WriteLine(ok ? "OK — check the keyboard." : "Write failed / no response.");
}

if (args.Length > 0 && args[0] == "readtable")
{
    byte cmd = Convert.ToByte(args[1], 16);
    byte sub = Convert.ToByte(args[2], 16);
    int maxIdx = args.Length > 3 ? int.Parse(args[3]) : 40;
    var first = dev.SendRaw(new byte[] { cmd, sub, 0x00, 0x00 });
    Console.WriteLine("idx=  0 -> " + (first == null ? "(no response)" : string.Join(" ", first.Select(b => b.ToString("x2")))));
    for (int i = 1; i < maxIdx; i++)
    {
        var resp = dev.ReadNext();
        Console.WriteLine($"idx={i,3} -> " + (resp == null ? "(no response)" : string.Join(" ", resp.Select(b => b.ToString("x2")))));
    }
}

if (args.Length > 0 && args[0] == "write-then-read")
{
    byte col = byte.Parse(args[1]);
    byte row = byte.Parse(args[2]);
    double mm = double.Parse(args[3]);
    byte triggerRaw = (byte)Math.Round(mm * 100);
    var wpayload = new byte[63];
    wpayload[0] = 0x21; wpayload[4] = 0x18; wpayload[5] = 0x0c;
    wpayload[6 + col] = (byte)(1 << row);
    wpayload[28] = triggerRaw; wpayload[29] = triggerRaw;
    Console.WriteLine("Writing " + mm + "mm to col=" + col + " row=" + row + "...");
    var wresp = dev.SendRaw(wpayload);
    Console.WriteLine("Write RX: " + (wresp == null ? "(none)" : string.Join(" ", wresp.Select(b => b.ToString("x2")))));

    var rpayload = new byte[] { 0x21, 0x00, 0x00, 0x00, 0x18, 0x05, row, col };
    var rresp = dev.SendRaw(rpayload);
    Console.WriteLine("Read RX:  " + (rresp == null ? "(none)" : string.Join(" ", rresp.Select(b => b.ToString("x2")))));
    if (rresp != null && rresp.Length > 11)
    {
        int mode = rresp[6];
        int travel = (rresp[11] << 8) | rresp[7];
        Console.WriteLine($"mode={mode} travelRaw={travel} travelMm={travel/100.0:0.00}");
    }
}

if (args.Length > 0 && args[0] == "read-trigger")
{
    byte row = byte.Parse(args[1]);
    byte col = byte.Parse(args[2]);
    var payload = new byte[] { 0x21, 0x00, 0x00, 0x00, 0x18, 0x05, row, col };
    var resp = dev.SendRaw(payload);
    Console.WriteLine("RX1: " + (resp == null ? "(none)" : string.Join(" ", resp.Select(b => b.ToString("x2")))));
    for (int i = 0; i < 3; i++)
    {
        var next = dev.ReadNext(300);
        Console.WriteLine($"RX{i+2}: " + (next == null ? "(none)" : string.Join(" ", next.Select(b => b.ToString("x2")))));
    }
}

if (args.Length > 0 && args[0] == "actuation-bitmask")
{
    byte col = byte.Parse(args[1]);
    byte row = byte.Parse(args[2]);
    double mm = double.Parse(args[3]);
    byte triggerRaw = (byte)Math.Round(mm * 100);
    var payload = new byte[63];
    payload[0] = 0x21; payload[4] = 0x18; payload[5] = 0x0c;
    payload[6 + col] = (byte)(1 << row);
    payload[28] = triggerRaw; payload[29] = triggerRaw;
    Console.WriteLine("TX: " + string.Join(" ", payload.Select(b => b.ToString("x2"))));
    var resp = dev.SendRaw(payload);
    Console.WriteLine("RX: " + (resp == null ? "(no response)" : string.Join(" ", resp.Select(b => b.ToString("x2")))));
}

dev.Dispose();
return 0;
