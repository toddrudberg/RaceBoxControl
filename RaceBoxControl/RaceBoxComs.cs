using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace Undaunted.AirRacing.IO
{
  public sealed class RaceboxCsvWriter : IDisposable
  {
    private readonly StreamWriter _sw;
    private bool _wroteHeader;
    private readonly object _lock = new();

    public RaceboxCsvWriter(string path)
    {
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      _sw = new StreamWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.Read));
      _sw.NewLine = "\n";
    }

    public void Append(byte[] rec80)
    {
      if (rec80 is null || rec80.Length != 80) return;
      bool justTheHex = true;
      if (!_wroteHeader)
      {
        if (!justTheHex)
        {
          _sw.WriteLine(string.Join(",",
            "iTOW_ms", "UTC", "fixType", "numSV",
            "lat_deg", "lon_deg", "height_m",
            "hAcc_m", "vAcc_m",
            "gSpeed_mps", "head_deg",
            "raw80_hex"));
        }
        _wroteHeader = true;
      }

      // Field map (UBX-NAV-PVT–like layout used by RaceBox live/history):
      // iTOW U4 @0, fixType U1 @20, numSV U1 @23,
      // lon I4 1e-7 deg @24, lat I4 1e-7 deg @28, height I4 mm @32,
      // hAcc U4 mm @40, vAcc U4 mm @44,
      // gSpeed I4 mm/s @60, headMot I4 1e-5 deg @68
      uint iTOW = ReadU32(rec80, 0);
      byte fix = rec80[20];
      byte numSV = rec80[23];
      int lonE7 = ReadI32(rec80, 24);
      int latE7 = ReadI32(rec80, 28);
      int hMm = ReadI32(rec80, 32);
      uint hAccMm = ReadU32(rec80, 40);
      uint vAccMm = ReadU32(rec80, 44);
      int gSpMmS = ReadI32(rec80, 60);
      int hdgE5 = ReadI32(rec80, 68);
      var (tsOk, tsUtc) = DecodeUtcTimestamp(rec80);
      string tsSec = "";
      if (tsOk)
      {
        var unixSeconds = (tsUtc - DateTimeOffset.UnixEpoch).TotalSeconds;
        tsSec = unixSeconds.ToString("F3", CultureInfo.InvariantCulture);
      }

      double lat = latE7 / 1e7;
      double lon = lonE7 / 1e7;
      double hM = hMm / 1000.0;
      double hAcc = hAccMm / 1000.0;
      double vAcc = vAccMm / 1000.0;
      double gSp = gSpMmS / 1000.0;
      double hdg = hdgE5 / 1e5;

      var rawHex = BitConverter.ToString(rec80).Replace("-", "");
      lock (_lock)
      {
        if (justTheHex)
        {
          _sw.WriteLine(string.Join(",", rawHex));
        }
        else
        {
          _sw.WriteLine(string.Join(",",
            iTOW.ToString(CultureInfo.InvariantCulture),
            tsSec,
            fix.ToString(CultureInfo.InvariantCulture),
            numSV.ToString(CultureInfo.InvariantCulture),
            lat.ToString("G17", CultureInfo.InvariantCulture),
            lon.ToString("G17", CultureInfo.InvariantCulture),
            hM.ToString("G17", CultureInfo.InvariantCulture),
            hAcc.ToString("G17", CultureInfo.InvariantCulture),
            vAcc.ToString("G17", CultureInfo.InvariantCulture),
            gSp.ToString("G17", CultureInfo.InvariantCulture),
            hdg.ToString("G17", CultureInfo.InvariantCulture),
            rawHex));
        }
      }
    }

    private static (bool ok, DateTimeOffset utc) DecodeUtcTimestamp(byte[] rec80)
    {
      // UBX-NAV-PVT layout offsets:
      // year U2@4, month U1@6, day U1@7, hour U1@8, min U1@9, sec U1@10, valid U1@11,
      // tAcc U4@12, nano I4@16
      ushort year = ReadU16(rec80, 4);
      byte month = rec80[6];
      byte day = rec80[7];
      byte hour = rec80[8];
      byte minute = rec80[9];
      byte second = rec80[10];
      byte valid = rec80[11]; // bit0: validDate, bit1: validTime, bit2: fully resolved (leap secs)
      int nano = ReadI32(rec80, 16); // signed ns, may be negative

      bool hasDate = (valid & 0x01) != 0;
      bool hasTime = (valid & 0x02) != 0;

      if (!hasDate || !hasTime)
        return (false, default); // timestamp not valid for this fix

      // Normalize nano so it’s within [0, 1e9) relative to the given second.
      // .NET ticks are 100 ns.
      long ticks = nano / 100; // ns -> ticks; negative values okay
                               // Build base second-aligned UTC time
      var baseUtc = new DateTime(year, month, day, hour, minute, Math.Clamp(second, (byte)0, (byte)59), DateTimeKind.Utc);
      var dt = baseUtc.AddTicks(ticks);
      return (true, new DateTimeOffset(dt));
    }

    public void Dispose()
    {
      _sw.Flush();
      _sw.Dispose();
    }
    private static ushort ReadU16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
    private static uint ReadU32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    private static int ReadI32(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
  }
  public sealed class RaceBoxComs : IAsyncDisposable
  {
    // Nordic UART UUIDs
    private static readonly Guid UartService = Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    private static readonly Guid RxCharGuid = Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E"); // write
    private static readonly Guid TxCharGuid = Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E"); // notify

    private BluetoothLEDevice? _device;
    private GattCharacteristic? _rx; // write w/o response
    private GattCharacteristic? _tx; // notify
    private GattSession? _session;   // service session (dispose to force link down)
    private GattDeviceService? _svc;

    private readonly List<byte> _buf = new(8192);
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    public event Action<string>? Log;
    public event Action<RaceboxStatus>? StatusReceived;
    public event Action<RaceboxAck>? AckReceived;
    public event Action<RaceboxNack>? NackReceived;
    public event Action<byte[]>? HistoryRecord80B;
    public event Action<int, int>? DownloadProgress;
    public event Action? DownloadCompleted;

    public string? DeviceName { get; private set; }
    public ulong BluetoothAddress { get; private set; }

    private volatile bool _downloading = false;
    private int _expected = 0, _received = 0;
    private long _lastDataTick = 0;

    // ---------------- Scan ----------------
    public async Task<IReadOnlyList<(ulong addr, string name)>> ListRaceboxesAsync(TimeSpan timeout)
    {
      var results = new Dictionary<ulong, string>();
      var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

      using var cts = new CancellationTokenSource(timeout);
      var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };

      watcher.Received += (_, e) =>
      {
        var name = e.Advertisement.LocalName ?? "";
        if (name.StartsWith("RaceBox", StringComparison.OrdinalIgnoreCase))
        {
          results[e.BluetoothAddress] = name;
          Log?.Invoke($"BLE: {name} [{e.BluetoothAddress:X}] RSSI={e.RawSignalStrengthInDBm} dBm");
        }
      };
      watcher.Stopped += (_, __) => done.TrySetResult(true);

      watcher.Start();
      try { await Task.Delay(timeout, cts.Token); } catch { /* use timeout as stop */ }
      watcher.Stop();
      await done.Task;

      if (results.Count == 0) Log?.Invoke("Scan complete: no RaceBox found.");
      return results.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    public async Task ScanAndConnectAsync(TimeSpan timeout)
    {
      var found = await ListRaceboxesAsync(timeout);
      if (found.Count == 0) throw new TimeoutException("No RaceBox found.");
      await ConnectAsync(found[0].addr, found[0].name);
    }

    // ---------------- Connect ----------------
    public async Task ConnectAsync(ulong bluetoothAddress, string? knownName = null)
    {
      await _connectGate.WaitAsync().ConfigureAwait(false);
      try
      {
        // If switching devices, hard disconnect first
        if (_device != null && BluetoothAddress != 0 && BluetoothAddress != bluetoothAddress)
          await DisconnectAsync().ConfigureAwait(false);

        // If already connected to the same device, no-op
        if (_device != null && BluetoothAddress == bluetoothAddress)
          return;

        _device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress)
                  ?? throw new Exception("Failed to open device");
        DeviceName = knownName ?? _device.Name;
        BluetoothAddress = bluetoothAddress;

        _device.ConnectionStatusChanged += (_, __) =>
          Log?.Invoke($"BLE status: {_device.ConnectionStatus}");

        var svcRes = await _device.GetGattServicesForUuidAsync(UartService, BluetoothCacheMode.Uncached);
        var svc = svcRes?.Services?.FirstOrDefault() ?? throw new Exception("UART service not found");

        _session = svc.Session;                // hold the session so we can dispose it
        _session.MaintainConnection = false;   // we don't want the OS pinning the link
        _svc = svc;

        var chars = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        _rx = chars.Characteristics.FirstOrDefault(c => c.Uuid == RxCharGuid) ?? throw new Exception("RX not found");
        _tx = chars.Characteristics.FirstOrDefault(c => c.Uuid == TxCharGuid) ?? throw new Exception("TX not found");

        var st = await _tx.WriteClientCharacteristicConfigurationDescriptorAsync(
                   GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (st != GattCommunicationStatus.Success)
          throw new Exception("Failed to enable notifications");

        _tx.ValueChanged += TxOnValueChanged;

        Log?.Invoke($"Connected: {DeviceName} [{BluetoothAddress:X}]");
      }
      finally
      {
        _connectGate.Release();
      }
    }

    // ---------------- Commands ----------------
    public Task QueryStandaloneStatusAsync() =>
      WriteUbxAsync(BuildUbx(0xFF, 0x22, ReadOnlySpan<byte>.Empty));

    public async Task BeginDownloadAsync()
    {
      _downloading = true;
      _expected = 0;
      _received = 0;
      _lastDataTick = Environment.TickCount64;
      await WriteUbxAsync(BuildUbx(0xFF, 0x23, ReadOnlySpan<byte>.Empty));
    }

    public Task CancelDownloadAsync() =>
      WriteUbxAsync(BuildUbx(0xFF, 0x23, stackalloc byte[] { 0xFF }));

    public Task UnlockMemoryAsync(uint code)
    {
      Span<byte> pl = stackalloc byte[4];
      BitConverter.TryWriteBytes(pl, code);
      return WriteUbxAsync(BuildUbx(0xFF, 0x30, pl));
    }

    public Task SetStandaloneRecordingAsync(bool enable, byte dataRateHzCode = 0, byte flags = 0x1F,
                                            ushort stationarySpeedMmPs = 1389, ushort stationarySec = 30,
                                            ushort noFixSec = 30, ushort autoShutdownSec = 300)
    {
      Span<byte> pl = stackalloc byte[12];
      pl[0] = (byte)(enable ? 1 : 0);
      pl[1] = dataRateHzCode; pl[2] = flags; pl[3] = 0;
      BitConverter.TryWriteBytes(pl.Slice(4, 2), stationarySpeedMmPs);
      BitConverter.TryWriteBytes(pl.Slice(6, 2), stationarySec);
      BitConverter.TryWriteBytes(pl.Slice(8, 2), noFixSec);
      BitConverter.TryWriteBytes(pl.Slice(10, 2), autoShutdownSec);
      return WriteUbxAsync(BuildUbx(0xFF, 0x25, pl));
    }

    // ---------------- Low level ----------------
    private async Task WriteUbxAsync(byte[] packet)
    {
      if (_rx is null) throw new InvalidOperationException("Not connected");
      await _rx.WriteValueAsync(packet.AsBuffer(), GattWriteOption.WriteWithoutResponse);
    }

    private void TxOnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
      var data = args.CharacteristicValue.ToArray();
      lock (_buf) { _buf.AddRange(data); }
      ParseBuffer();
    }

    private void ParseBuffer()
    {
      while (true)
      {
        int start = -1;

        lock (_buf)
        {
          // find sync
          for (int i = 0; i < _buf.Count - 1; i++)
          {
            if (_buf[i] == 0xB5 && _buf[i + 1] == 0x62) { start = i; break; }
          }

          if (start < 0)
          {
            if (_buf.Count > 1) _buf.RemoveRange(0, _buf.Count - 1);
            return;
          }

          if (_buf.Count - start < 6) return; // need header

          byte cls = _buf[start + 2];
          byte id = _buf[start + 3];
          int len = _buf[start + 4] | (_buf[start + 5] << 8);
          int frameLen = 6 + len + 2;

          if (_buf.Count - start < frameLen) return; // wait more

          byte ckA = 0, ckB = 0;
          for (int i = start + 2; i < start + 6 + len; i++) { ckA += _buf[i]; ckB += ckA; }
          byte expA = _buf[start + 6 + len];
          byte expB = _buf[start + 6 + len + 1];

          if (ckA != expA || ckB != expB)
          {
            _buf.RemoveRange(0, start + 2); // resync
            continue;
          }

          var payload = new byte[len];
          _buf.CopyTo(start + 6, payload, 0, len);
          _buf.RemoveRange(0, start + frameLen);

          HandleFrame(cls, id, payload);
        }
      }
    }

    private void HandleFrame(byte cls, byte id, byte[] payload)
    {
      if (_downloading) _lastDataTick = Environment.TickCount64;

      if (cls == 0xFF && id == 0x02 && payload.Length == 2) // ACK
      {
        AckReceived?.Invoke(new RaceboxAck(payload[0], payload[1]));
        if (_downloading && payload[0] == 0xFF && payload[1] == 0x23)
        {
          _downloading = false;
          DownloadCompleted?.Invoke();
        }
        return;
      }

      if (cls == 0xFF && id == 0x03 && payload.Length == 2) // NACK
      {
        NackReceived?.Invoke(new RaceboxNack(payload[0], payload[1]));
        if (_downloading && payload[0] == 0xFF && payload[1] == 0x23)
        {
          _downloading = false;
          DownloadCompleted?.Invoke();
        }
        return;
      }

      if (cls == 0xFF && id == 0x23 && payload.Length == 4)
      {
        _expected = BitConverter.ToInt32(payload, 0);
        _received = 0;
        DownloadProgress?.Invoke(_received, _expected);
        return;
      }

      if (cls == 0xFF && id == 0x21 && payload.Length == 80)
      {
        _received++;
        HistoryRecord80B?.Invoke(payload);
        if (_expected > 0) DownloadProgress?.Invoke(_received, _expected);

        if (_downloading && _expected > 0 && _received >= _expected)
        {
          _downloading = false;
          DownloadCompleted?.Invoke();
        }
        return;
      }

      if (cls == 0xFF && id == 0x22 && payload.Length == 12)
      {
        var st = new RaceboxStatus
        {
          Recording = payload[0] != 0,
          MemoryPercent = payload[1],
          SecurityEnabled = (payload[2] & 0x01) != 0,
          SecurityUnlocked = (payload[2] & 0x02) != 0,
          StoredRecords = BitConverter.ToUInt32(payload, 4),
          TotalCapacity = BitConverter.ToUInt32(payload, 8)
        };
        StatusReceived?.Invoke(st);
        return;
      }

      // 0xFF/0x26 (optional state change) ignored here
    }

    private static byte[] BuildUbx(byte cls, byte id, ReadOnlySpan<byte> payload)
    {
      int len = payload.Length;
      var buf = new byte[6 + len + 2];
      buf[0] = 0xB5; buf[1] = 0x62; buf[2] = cls; buf[3] = id;
      buf[4] = (byte)(len & 0xFF); buf[5] = (byte)((len >> 8) & 0xFF);
      payload.CopyTo(buf.AsSpan(6));
      byte ckA = 0, ckB = 0; for (int i = 2; i < buf.Length - 2; i++) { ckA += buf[i]; ckB += ckA; }
      buf[^2] = ckA; buf[^1] = ckB; return buf;
    }

    // ---------------- Teardown ----------------
    public async Task DisconnectAsync()
    {
      try
      {
        if (_downloading)
        {
          try { await CancelDownloadAsync(); } catch { }
          _downloading = false;
        }

        // turn off notifications first (retry once helps if mid-flight)
        if (_tx != null)
        {
          try
          {
            var st = await _tx.WriteClientCharacteristicConfigurationDescriptorAsync(
                       GattClientCharacteristicConfigurationDescriptorValue.None);
            if (st != GattCommunicationStatus.Success)
            {
              await Task.Delay(100);
              await _tx.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.None);
            }
          }
          catch { }

          _tx.ValueChanged -= TxOnValueChanged;
          //try { _tx.Dispose(); } catch { }
          try { _tx.Service?.Dispose(); } catch { }
          _tx = null;
        }

        // RX is write-only (NUS); dispose and clear
        if (_rx != null)
        {
          //try { _rx.Dispose(); } catch { }
          try { _rx.Service?.Dispose(); } catch { }
          _rx = null;
        }

        if (_session != null)
        {
          try { _session.Dispose(); } catch { }
          _session = null;
        }

        if (_device != null)
        {
          try { _device.Dispose(); } catch { }
          _device = null;
        }

        // clear identity and buffers
        DeviceName = null;
        BluetoothAddress = 0;
        lock (_buf) _buf.Clear();
        _expected = 0; _received = 0; _lastDataTick = 0;
      }
      catch
      {
        // best-effort
      }
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());
  }

  // -------- helper models you referenced --------
  public sealed class RaceboxStatus
  {
    public bool Recording { get; set; }
    public byte MemoryPercent { get; set; }
    public bool SecurityEnabled { get; set; }
    public bool SecurityUnlocked { get; set; }
    public uint StoredRecords { get; set; }
    public uint TotalCapacity { get; set; }
  }

  public readonly record struct RaceboxAck(byte Cls, byte Id);
  public readonly record struct RaceboxNack(byte Cls, byte Id);
}
