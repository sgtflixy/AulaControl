using System;
using System.Linq;
using HidSharp;

namespace AulaControl.Hid
{
    public enum ConnectResult
    {
        Success,
        NotFound,       // no matching VID/PID on the system at all
        AccessDenied,   // found it, but an interface is locked by another process
        NoResponse,     // opened an interface but nothing answered the probe
    }

    /// <summary>
    /// Raw transport for the AULA WIN60HE/WIN75HE HID protocol.
    /// Report ID 1, 63-byte Output/Input reports (not Feature reports).
    /// Frame shape (payload, report ID stripped): [CMD][SUBCMD][00][IDX][...].
    /// See re/protocol.md in the repo root for the reverse-engineering notes.
    /// </summary>
    public sealed class AulaDevice : IDisposable
    {
        public const int VendorId = 0x2e3c;
        public const int ProductId = 0xc365;
        private const byte ReportId = 1;
        public const int PayloadLength = 63;

        private HidStream? _stream;

        public bool IsConnected => _stream != null;
        public string? DevicePath { get; private set; }

        /// <summary>Set by Connect()/TryConnect() describing why connection failed, or null on success.</summary>
        public string? LastError { get; private set; }

        public bool Connect() => TryConnect() == ConnectResult.Success;

        /// <summary>
        /// Tries every matching HID interface on the device (it exposes several
        /// collections) until one responds to a device-info query — that's the
        /// vendor-defined control interface the web driver talks to.
        /// Distinguishes "device not plugged in" from "device found but its
        /// interface is locked by another process" so the UI can explain which
        /// one happened instead of just saying "not found".
        /// </summary>
        public ConnectResult TryConnect()
        {
            Disconnect();
            LastError = null;

            var candidates = DeviceList.Local
                .GetHidDevices(VendorId, ProductId)
                .ToList();

            if (candidates.Count == 0)
            {
                LastError = "No AULA keyboard found on this vendor/product ID. Is it plugged in?";
                return ConnectResult.NotFound;
            }

            bool sawAccessError = false;

            foreach (var dev in candidates)
            {
                HidStream? stream = null;
                try
                {
                    if (!dev.TryOpen(out stream) || stream == null)
                    {
                        // TryOpen returning false without throwing usually means
                        // the OS denied access — most commonly because another
                        // process (a browser tab with WebHID, another instance
                        // of this app, the official web driver) already has an
                        // exclusive handle open on this interface.
                        sawAccessError = true;
                        continue;
                    }

                    stream.ReadTimeout = 500;
                    stream.WriteTimeout = 500;
                    _stream = stream;

                    // Sanity probe: cmd 0x0d idx 0 -> device model string.
                    var resp = SendRaw(new byte[] { 0x0d, 0x00, 0x00, 0x00 });
                    if (resp != null && resp.Length > 0)
                    {
                        DevicePath = dev.DevicePath;
                        return ConnectResult.Success;
                    }

                    stream.Dispose();
                    _stream = null;
                }
                catch (UnauthorizedAccessException)
                {
                    sawAccessError = true;
                    stream?.Dispose();
                    _stream = null;
                }
                catch
                {
                    stream?.Dispose();
                    _stream = null;
                }
            }

            if (sawAccessError)
            {
                LastError = "Found the keyboard, but its control interface is already open elsewhere " +
                             "(another instance of this app, a browser tab with the web driver open, " +
                             "or the official AULA software). Close that first, then try again.";
                return ConnectResult.AccessDenied;
            }

            LastError = "Found the keyboard's USB device, but no interface responded to a device-info query.";
            return ConnectResult.NoResponse;
        }

        public void Disconnect()
        {
            _stream?.Dispose();
            _stream = null;
            DevicePath = null;
        }

        /// <summary>
        /// Sends one 63-byte payload (report ID prepended automatically) and
        /// reads back the device's response. Returns the raw 63-byte payload
        /// (report ID stripped), or null on timeout.
        /// </summary>
        public byte[]? SendRaw(byte[] data, int timeoutMs = 500)
        {
            if (_stream == null)
                throw new InvalidOperationException("Not connected.");
            if (data.Length > PayloadLength)
                throw new ArgumentException($"Payload must be <= {PayloadLength} bytes.");

            var outBuf = new byte[PayloadLength + 1];
            outBuf[0] = ReportId;
            Array.Copy(data, 0, outBuf, 1, data.Length);

            lock (_stream)
            {
                FlushStaleReports();

                _stream.Write(outBuf);

                var inBuf = new byte[PayloadLength + 1];
                _stream.ReadTimeout = timeoutMs;
                try
                {
                    int n = _stream.Read(inBuf);
                    if (n <= 1) return null;
                    // strip report id byte
                    var result = new byte[n - 1];
                    Array.Copy(inBuf, 1, result, 0, n - 1);
                    return result;
                }
                catch (TimeoutException)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Reads one more queued input report without writing anything first.
        /// Some commands (e.g. 0x0d device info) reply with more than one
        /// packet per query; call this to pick up the next one.
        /// </summary>
        public byte[]? ReadNext(int timeoutMs = 500)
        {
            if (_stream == null)
                throw new InvalidOperationException("Not connected.");
            lock (_stream)
            {
                var inBuf = new byte[PayloadLength + 1];
                _stream.ReadTimeout = timeoutMs;
                try
                {
                    int n = _stream.Read(inBuf);
                    if (n <= 1) return null;
                    var result = new byte[n - 1];
                    Array.Copy(inBuf, 1, result, 0, n - 1);
                    return result;
                }
                catch (TimeoutException)
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// Drains any input reports already sitting in the OS's HID queue from
        /// before this call (e.g. a stale response to a previous transaction,
        /// or an unsolicited push report) so the next Read() after our Write()
        /// picks up the fresh response instead of an old one.
        /// </summary>
        private void FlushStaleReports()
        {
            if (_stream == null) return;
            var scratch = new byte[PayloadLength + 1];
            var previousTimeout = _stream.ReadTimeout;
            _stream.ReadTimeout = 5;
            try
            {
                while (true)
                {
                    int n = _stream.Read(scratch);
                    if (n <= 0) break;
                }
            }
            catch (TimeoutException)
            {
                // queue empty — expected exit path
            }
            finally
            {
                _stream.ReadTimeout = previousTimeout;
            }
        }

        public void Dispose() => Disconnect();
    }
}
