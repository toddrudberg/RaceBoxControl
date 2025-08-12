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

      if (!_wroteHeader)
      {
        _sw.WriteLine(string.Join(",",
          "iTOW_ms", "fixType", "numSV",
          "lat_deg", "lon_deg", "height_m",
          "hAcc_m", "vAcc_m",
          "gSpeed_mps", "head_deg",
          "raw80_hex"));
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
        _sw.WriteLine(string.Join(",",
          iTOW.ToString(CultureInfo.InvariantCulture),
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

    public void Dispose()
    {
      _sw.Flush();
      _sw.Dispose();
    }

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
    private GattCharacteristic? _rx, _tx;
    private readonly ConcurrentQueue<byte> _fifo = new();

    public event Action<string>? Log;
    public event Action<RaceboxStatus>? StatusReceived;
    public event Action<RaceboxAck>? AckReceived;
    public event Action<RaceboxNack>? NackReceived;
    public event Action<byte[]>? HistoryRecord80B;
    public event Action<int, int>? DownloadProgress;
    public event Action? DownloadCompleted;

    public string? DeviceName { get; private set; }
    public ulong BluetoothAddress { get; private set; }

    private readonly List<byte> _buf = new(8192);

    // --------- Scan (Windows watcher) ----------
    public async Task<IReadOnlyList<(ulong addr, string name)>> ListRaceboxesAsync(TimeSpan timeout)
    {
      var results = new Dictionary<ulong, string>();
      var tcs = new TaskCompletionSource<bool>();
      using var cts = new CancellationTokenSource(timeout);

      var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
      watcher.Received += (_, e) =>
      {
        var name = e.Advertisement.LocalName ?? "";
        if (name.StartsWith("RaceBox", StringComparison.OrdinalIgnoreCase))
        {
          results[e.BluetoothAddress] = name;
          Log?.Invoke($"BLE: {name} [{e.BluetoothAddress:X}]");
        }
      };
      watcher.Stopped += (_, __) => tcs.TrySetResult(true);

      watcher.Start();
      try
      {
        await Task.Delay(timeout, cts.Token);
      }
      catch { /* timeout used as stop signal */ }
      watcher.Stop();
      await tcs.Task;

      if (results.Count == 0) Log?.Invoke("Scan timeout with no RaceBox found.");
      return results.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    public async Task ScanAndConnectAsync(TimeSpan timeout)
    {
      var found = await ListRaceboxesAsync(timeout);
      if (found.Count == 0) throw new TimeoutException("No RaceBox found.");
      await ConnectAsync(found[0].addr, found[0].name);
    }

    // --------- Connect ----------
    public async Task ConnectAsync(ulong bluetoothAddress, string? knownName = null)
    {
      _device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress)
                ?? throw new Exception("Failed to open device");
      DeviceName = knownName ?? _device.Name;
      BluetoothAddress = bluetoothAddress;

      var svcRes = await _device.GetGattServicesForUuidAsync(UartService, BluetoothCacheMode.Uncached);
      var svc = svcRes?.Services?.FirstOrDefault() ?? throw new Exception("UART service not found");

      var chars = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
      _rx = chars.Characteristics.FirstOrDefault(c => c.Uuid == RxCharGuid) ?? throw new Exception("RX not found");
      _tx = chars.Characteristics.FirstOrDefault(c => c.Uuid == TxCharGuid) ?? throw new Exception("TX not found");

      var st = await _tx.WriteClientCharacteristicConfigurationDescriptorAsync(
                 GattClientCharacteristicConfigurationDescriptorValue.Notify);
      if (st != GattCommunicationStatus.Success) throw new Exception("Failed to enable notifications");

      _tx.ValueChanged += TxOnValueChanged;
      Log?.Invoke($"Connected: {DeviceName}");
    }

    // --------- Commands ----------
    public Task QueryStandaloneStatusAsync() =>
      WriteUbxAsync(BuildUbx(0xFF, 0x22, ReadOnlySpan<byte>.Empty));

    public Task BeginDownloadAsync() =>
      WriteUbxAsync(BuildUbx(0xFF, 0x23, ReadOnlySpan<byte>.Empty));

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

    // --------- Low-level plumbing ----------
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
          // Find 0xB5 0x62 safely (scan indices, not elements)
          for (int i = 0; i < _buf.Count - 1; i++)
          {
            if (_buf[i] == 0xB5 && _buf[i + 1] == 0x62)
            {
              start = i;
              break;
            }
          }

          if (start < 0)
          {
            // keep at most one trailing byte (in case it's 0xB5)
            if (_buf.Count > 1) _buf.RemoveRange(0, _buf.Count - 1);
            return;
          }

          // Need at least header+cls/id+len (6 bytes)
          if (_buf.Count - start < 6) return;

          byte cls = _buf[start + 2];
          byte id = _buf[start + 3];
          int len = _buf[start + 4] | (_buf[start + 5] << 8);
          int frameLen = 6 + len + 2; // header+len + payload + checksum

          if (_buf.Count - start < frameLen) return; // wait for more

          // checksum over cls,id,lenL,lenH,payload
          byte ckA = 0, ckB = 0;
          for (int i = start + 2; i < start + 6 + len; i++) { ckA += _buf[i]; ckB += ckA; }
          byte expA = _buf[start + 6 + len];
          byte expB = _buf[start + 6 + len + 1];

          if (ckA != expA || ckB != expB)
          {
            // bad frame → skip this header and continue scanning
            _buf.RemoveRange(0, start + 2); // drop up to (but keep) 0x62 as next candidate tail
            continue;
          }

          // valid frame → extract payload and drop consumed bytes
          var payload = new byte[len];
          _buf.CopyTo(start + 6, payload, 0, len);
          _buf.RemoveRange(0, start + frameLen);

          HandleFrame(cls, id, payload);
        }
      }
    }

    private void HandleFrame(byte cls, byte id, byte[] payload)
    {
      if (cls == 0xFF && id == 0x02 && payload.Length == 2)
        AckReceived?.Invoke(new RaceboxAck(payload[0], payload[1]));
      else if (cls == 0xFF && id == 0x03 && payload.Length == 2)
        NackReceived?.Invoke(new RaceboxNack(payload[0], payload[1]));
      else if (cls == 0xFF && id == 0x22 && payload.Length == 12)
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
      }
      else if (cls == 0xFF && id == 0x23 && payload.Length == 4)
      {
        var expected = BitConverter.ToInt32(payload, 0);
        DownloadProgress?.Invoke(0, expected);
      }
      else if (cls == 0xFF && id == 0x21 && payload.Length == 80)
      {
        HistoryRecord80B?.Invoke(payload);
      }
      else if (cls == 0xFF && id == 0x26 && payload.Length == 12)
      {
        // optional: state-change packets during dump (useful later for session splitting)
      }
    }
    private int _expected = 0, _received = 0;

    private void ParseQueue()
    {
      while (TryReadOneFrame(_fifo, out var cls, out var id, out var payload))
      {
        if (cls == 0xFF && id == 0x02 && payload.Length == 2)
          AckReceived?.Invoke(new RaceboxAck(payload[0], payload[1]));
        else if (cls == 0xFF && id == 0x03 && payload.Length == 2)
          NackReceived?.Invoke(new RaceboxNack(payload[0], payload[1]));
        else if (cls == 0xFF && id == 0x22 && payload.Length == 12)
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
        }
        else if (cls == 0xFF && id == 0x23 && payload.Length == 4)
        {
          _expected = BitConverter.ToInt32(payload, 0);
          _received = 0;
          DownloadProgress?.Invoke(_received, _expected);
        }
        else if (cls == 0xFF && id == 0x21 && payload.Length == 80)
        {
          _received++;
          HistoryRecord80B?.Invoke(payload);
          if (_expected > 0) DownloadProgress?.Invoke(_received, _expected);
          if (_expected > 0 && _received >= _expected) DownloadCompleted?.Invoke();
        }
      }
    }

    private static bool TryReadOneFrame(ConcurrentQueue<byte> q, out byte cls, out byte id, out byte[] payload)
    {
      cls = id = 0; payload = Array.Empty<byte>();
      while (q.TryDequeue(out var b))
      {
        if (b != 0xB5) continue;
        if (!q.TryDequeue(out var b2)) { q.Enqueue(b); return false; }
        if (b2 != 0x62) continue;

        if (!TryDequeue(q, 4, out var hdr)) { Requeue(q, new byte[] { 0xB5, 0x62 }.Concat(hdr).ToArray()); return false; }
        cls = hdr[0]; id = hdr[1];
        int len = hdr[2] | (hdr[3] << 8);

        if (!TryDequeue(q, len + 2, out var rest)) { Requeue(q, new byte[] { 0xB5, 0x62, cls, id, hdr[2], hdr[3] }.Concat(rest).ToArray()); return false; }

        var pl = rest.AsSpan(0, len).ToArray();
        byte ckA = rest[len], ckB = rest[len + 1];

        byte cA = 0, cB = 0;
        void Sum(byte v) { cA += v; cB += cA; }
        Sum(cls); Sum(id); Sum((byte)(len & 0xFF)); Sum((byte)(len >> 8));
        foreach (var v in pl) Sum(v);

        if (cA != ckA || cB != ckB) { payload = Array.Empty<byte>(); continue; }
        payload = pl;
        return true;
      }
      return false;

      static bool TryDequeue(ConcurrentQueue<byte> q, int count, out byte[] data)
      {
        data = new byte[count];
        for (int i = 0; i < count; i++) if (!q.TryDequeue(out data[i])) { data = data.AsSpan(0, i).ToArray(); return false; }
        return true;
      }
      static void Requeue(ConcurrentQueue<byte> q, byte[] bytes)
      {
        for (int i = bytes.Length - 1; i >= 0; i--) q.Enqueue(bytes[i]);
      }
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

    public async ValueTask DisposeAsync()
    {
      try
      {
        if (_tx != null)
        {
          await _tx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None);
          _tx.ValueChanged -= TxOnValueChanged;
        }
      }
      catch { }
      _device?.Dispose();
    }
  }

  public record RaceboxAck(byte opClass, byte opId);
  public record RaceboxNack(byte opClass, byte opId);
  public record RaceboxStatus
  {
    public bool Recording { get; init; }
    public byte MemoryPercent { get; init; }
    public bool SecurityEnabled { get; init; }
    public bool SecurityUnlocked { get; init; }
    public uint StoredRecords { get; init; }
    public uint TotalCapacity { get; init; }
  }
}
