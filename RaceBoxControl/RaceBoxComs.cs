using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InTheHand.Bluetooth;

namespace Undaunted.AirRacing.IO
{
  public sealed class RaceBoxComs : IAsyncDisposable
  {
    // Nordic UART (UART-over-BLE) UUIDs
    private static readonly Guid UartService = Guid.Parse("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    private static readonly Guid RxCharGuid = Guid.Parse("6E400002-B5A3-F393-E0A9-E50E24DCCA9E"); // write
    private static readonly Guid TxCharGuid = Guid.Parse("6E400003-B5A3-F393-E0A9-E50E24DCCA9E"); // notify

    private BluetoothDevice? _device;
    private GattCharacteristic? _rx, _tx;
    private readonly ConcurrentQueue<byte> _fifo = new();
    private readonly CancellationTokenSource _cts = new();

    public string? DeviceName { get; private set; }
    public string? DeviceId { get; private set; }

    // Events for UI/pipeline
    public event Action<string>? Log;
    public event Action<RaceboxAck>? AckReceived;
    public event Action<RaceboxNack>? NackReceived;
    public event Action<RaceboxStatus>? StatusReceived;   // FF 22 (12B)
    public event Action<byte[]>? HistoryRecord80B;        // FF 21 (80B)
    public event Action<int, int>? DownloadProgress;       // received, expected
    public event Action? DownloadCompleted;

    public async Task ScanAndConnectAsync(TimeSpan timeout, Predicate<string>? nameFilter = null)
    {
      nameFilter ??= n => n.StartsWith("RaceBox Mini", StringComparison.OrdinalIgnoreCase)
                       || n.StartsWith("RaceBox Micro", StringComparison.OrdinalIgnoreCase);

      Log?.Invoke("Scanning for RaceBox…");
      using var cts = new CancellationTokenSource(timeout);

      var opts = new RequestDeviceOptions
      {
        AcceptAllDevices = true
        // You can also add filters if supported in your version:
        // Filters = { new BluetoothLEScanFilter { NamePrefix = "RaceBox Mini" }, ... }
      };

      BluetoothDevice? chosen = null;

      while (!cts.IsCancellationRequested && chosen is null)
      {
        // Get a snapshot of currently visible devices
        var found = await Bluetooth.ScanForDevicesAsync(opts);

        chosen = found.FirstOrDefault(d => nameFilter(d.Name ?? string.Empty));
        if (chosen == null)
        {
          await Task.Delay(500, cts.Token); // wait a bit before rescanning
        }
      }

      if (chosen is null)
        throw new TimeoutException("No RaceBox found within the scan timeout.");

      await ConnectAsync(chosen);
    }


    public async Task ConnectAsync(BluetoothDevice device)
    {
      _device = device;
      DeviceName = device.Name;
      DeviceId = device.Id;

      await _device.Gatt.ConnectAsync();

      var svc = await _device.Gatt.GetPrimaryServiceAsync(UartService)
                ?? throw new Exception("UART service not found");

      _rx = await svc.GetCharacteristicAsync(RxCharGuid)
            ?? throw new Exception("RX characteristic not found");
      _tx = await svc.GetCharacteristicAsync(TxCharGuid)
            ?? throw new Exception("TX characteristic not found");

      _tx.CharacteristicValueChanged += TxOnValueChanged;
      await _tx.StartNotificationsAsync();

      Log?.Invoke($"Connected: {DeviceName} ({DeviceId})");
    }

    // ---------- High-level commands ----------

    public Task QueryStandaloneStatusAsync() =>
      WriteUbxAsync(BuildUbx(0xFF, 0x22, ReadOnlySpan<byte>.Empty)); // FF 22

    public Task BeginDownloadAsync() =>
      WriteUbxAsync(BuildUbx(0xFF, 0x23, ReadOnlySpan<byte>.Empty)); // FF 23 (start)

    public Task CancelDownloadAsync() =>
      WriteUbxAsync(BuildUbx(0xFF, 0x23, stackalloc byte[] { 0xFF })); // FF 23 (cancel)

    public Task UnlockMemoryAsync(uint code)
    {
      Span<byte> pl = stackalloc byte[4];
      BitConverter.TryWriteBytes(pl, code);
      return WriteUbxAsync(BuildUbx(0xFF, 0x30, pl)); // Unlock
    }
    public async Task<IReadOnlyList<BluetoothDevice>> ListRaceboxesAsync(TimeSpan timeout)
    {
      using var cts = new CancellationTokenSource(timeout);
      var opts = new RequestDeviceOptions { AcceptAllDevices = true };

      var hits = new Dictionary<string, BluetoothDevice>(); // de-dupe by Id
      while (!cts.IsCancellationRequested)
      {
        var found = await Bluetooth.ScanForDevicesAsync(opts);
        foreach (var d in found)
        {
          var name = d.Name ?? string.Empty;
          if (name.StartsWith("RaceBox Mini", StringComparison.OrdinalIgnoreCase)
           || name.StartsWith("RaceBox Micro", StringComparison.OrdinalIgnoreCase))
          {
            hits[d.Id] = d;
          }
        }
        if (hits.Count > 0) break; // stop on first hit batch; or keep looping if you prefer
        await Task.Delay(400, cts.Token);
      }
      return hits.Values.ToList();
    }

    public async Task<IReadOnlyList<BluetoothDevice>> ListRaceboxesVerboseAsync(TimeSpan timeout)
    {
      using var cts = new CancellationTokenSource(timeout);
      var opts = new RequestDeviceOptions { AcceptAllDevices = true };

      var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      var hits = new Dictionary<string, BluetoothDevice>();

      while (!cts.IsCancellationRequested)
      {
        IReadOnlyCollection<BluetoothDevice> snap;
        try { snap = await Bluetooth.ScanForDevicesAsync(opts); }
        catch (Exception ex)
        {
          Console.WriteLine($"Scan error: {ex.Message}");
          await Task.Delay(300);
          continue;
        }

        foreach (var d in snap)
        {
          var id = d.Id ?? "";
          var name = d.Name ?? "";
          if (seen.Add(id)) Console.WriteLine($"BLE: {name} [{id}]"); // <-- console log every first sighting

          if ((name.StartsWith("RaceBox", StringComparison.OrdinalIgnoreCase)))
          {
            hits[id] = d;
          }
        }

        if (hits.Count > 0) break;        // stop as soon as we saw at least one RB
        await Task.Delay(300);             // small pause before next snapshot
      }

      if (hits.Count == 0) Console.WriteLine("Scan timeout with no RaceBox found.");
      return hits.Values.ToList();
    }

    public Task SetStandaloneRecordingAsync(bool enable, byte dataRateHzCode = 0 /*25Hz*/, byte flags = 0x1F,
                                            ushort stationarySpeedMmPs = 1389, ushort stationarySec = 30,
                                            ushort noFixSec = 30, ushort autoShutdownSec = 300)
    {
      Span<byte> pl = stackalloc byte[12];
      pl[0] = (byte)(enable ? 1 : 0);
      pl[1] = dataRateHzCode;  // device-specific code from manual
      pl[2] = flags; pl[3] = 0;
      BitConverter.TryWriteBytes(pl.Slice(4, 2), stationarySpeedMmPs);
      BitConverter.TryWriteBytes(pl.Slice(6, 2), stationarySec);
      BitConverter.TryWriteBytes(pl.Slice(8, 2), noFixSec);
      BitConverter.TryWriteBytes(pl.Slice(10, 2), autoShutdownSec);
      return WriteUbxAsync(BuildUbx(0xFF, 0x25, pl));
    }

    // ---------- Low-level plumbing ----------

    private async Task WriteUbxAsync(byte[] packet)
    {
      if (_rx is null) throw new InvalidOperationException("Not connected.");
      await _rx.WriteValueWithoutResponseAsync(packet);
    }

    private void TxOnValueChanged(object? sender, GattCharacteristicValueChangedEventArgs e)
    {
      var data = e.Value; // byte[]
      foreach (var b in data) _fifo.Enqueue(b);
      ParseUbxFromQueue();
    }

    private void ParseUbxFromQueue()
    {
      while (TryReadOneFrame(_fifo, out var cls, out var id, out var payload))
      {
        // ACK/NACK
        if (cls == 0xFF && id == 0x02 && payload.Length == 2)
          AckReceived?.Invoke(new RaceboxAck(payload[0], payload[1]));
        else if (cls == 0xFF && id == 0x03 && payload.Length == 2)
          NackReceived?.Invoke(new RaceboxNack(payload[0], payload[1]));

        // FF 22: 12-byte status
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
        // FF 23: 4-byte “expected records” on start
        else if (cls == 0xFF && id == 0x23 && payload.Length == 4)
        {
          _expectedHistory = BitConverter.ToInt32(payload, 0);
          _receivedHistory = 0;
          DownloadProgress?.Invoke(_receivedHistory, _expectedHistory);
        }
        // FF 21: 80-byte history record
        else if (cls == 0xFF && id == 0x21 && payload.Length == 80)
        {
          _receivedHistory++;
          HistoryRecord80B?.Invoke(payload);
          if (_expectedHistory > 0) DownloadProgress?.Invoke(_receivedHistory, _expectedHistory);
          if (_expectedHistory > 0 && _receivedHistory >= _expectedHistory)
            DownloadCompleted?.Invoke();
        }
        // other messages you can add (FF 26 state, etc.)
      }
    }

    private int _expectedHistory = 0, _receivedHistory = 0;

    private static bool TryReadOneFrame(ConcurrentQueue<byte> q, out byte cls, out byte id, out byte[] payload)
    {
      cls = id = 0; payload = Array.Empty<byte>();

      // Seek UBX header 0xB5 0x62
      while (q.TryDequeue(out var b))
      {
        if (b != 0xB5) continue;
        if (!q.TryDequeue(out var b2)) { q.Enqueue(b); return false; }
        if (b2 != 0x62)
        {
          // keep scanning, don't requeue b2 because it wasn't header
          continue;
        }

        if (!TryDequeue(q, 4, out var hdr)) { RequeuePrefix(q, 0xB5, 0x62, hdr); return false; }
        cls = hdr[0]; id = hdr[1];
        int len = hdr[2] | (hdr[3] << 8);

        if (!TryDequeue(q, len + 2, out var rest))
        {
          RequeuePrefix(q, 0xB5, 0x62, hdr, rest);
          return false;
        }

        var pl = rest.AsSpan(0, len).ToArray();
        byte ckA = rest[len], ckB = rest[len + 1];

        // checksum over cls,id,lenL,lenH,payload
        byte cA = 0, cB = 0;
        Sum(ref cA, ref cB, cls); Sum(ref cA, ref cB, id);
        Sum(ref cA, ref cB, (byte)(len & 0xFF)); Sum(ref cA, ref cB, (byte)(len >> 8));
        foreach (var v in pl) { Sum(ref cA, ref cB, v); }

        if (cA != ckA || cB != ckB) { payload = Array.Empty<byte>(); continue; }
        payload = pl;
        return true;
      }

      return false;

      static void Sum(ref byte a, ref byte b, byte v) { a += v; b += a; }

      static bool TryDequeue(ConcurrentQueue<byte> q, int count, out byte[] data)
      {
        data = new byte[count];
        for (int i = 0; i < count; i++)
          if (!q.TryDequeue(out data[i])) { data = data.AsSpan(0, i).ToArray(); return false; }
        return true;
      }

      static void RequeuePrefix(ConcurrentQueue<byte> q, byte b0, byte b1, byte[] part1, byte[]? part2 = null)
      {
        // requeue in reverse to preserve order at head
        if (part2 != null) for (int i = part2.Length - 1; i >= 0; i--) q.Enqueue(part2[i]);
        for (int i = part1.Length - 1; i >= 0; i--) q.Enqueue(part1[i]);
        q.Enqueue(b1); q.Enqueue(b0);
      }
    }

    private static byte[] BuildUbx(byte cls, byte id, ReadOnlySpan<byte> payload)
    {
      int len = payload.Length;
      var buf = new byte[6 + len + 2];
      buf[0] = 0xB5; buf[1] = 0x62; buf[2] = cls; buf[3] = id;
      buf[4] = (byte)(len & 0xFF); buf[5] = (byte)((len >> 8) & 0xFF);
      payload.CopyTo(buf.AsSpan(6));
      byte ckA = 0, ckB = 0;
      for (int i = 2; i < buf.Length - 2; i++) { ckA += buf[i]; ckB += ckA; }
      buf[^2] = ckA; buf[^1] = ckB;
      return buf;
    }

    public async ValueTask DisposeAsync()
    {
      try
      {
        if (_tx != null)
        {
          await _tx.StopNotificationsAsync();
          _tx.CharacteristicValueChanged -= TxOnValueChanged;
        }
      }
      catch { /* ignore */ }
      finally
      {
        _tx = null;
        _rx = null;

        // Proper way to close the GATT link with InTheHand
        _device?.Gatt?.Disconnect();   // <- this is the right call

        _device = null;
        _cts.Cancel();
        _cts.Dispose();
      }
    }
  }




    // ---------- Models ----------

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
