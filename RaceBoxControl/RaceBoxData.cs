using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RaceBoxControl
{
  public sealed class Racebox80Record
  {
    // Time / status
    public uint ITOWms { get; init; }               // ms since GPS week start
    public int Year { get; init; }
    public byte Month { get; init; }
    public byte Day { get; init; }
    public byte Hour { get; init; }
    public byte Minute { get; init; }
    public byte Second { get; init; }
    public sbyte ValidityFlags { get; init; }        // bitmask (date/time valid, etc.)
    public uint TimeAcc_ns { get; init; }
    public int NanoSeconds { get; init; }          // signed
    public byte FixStatus { get; init; }            // 0=no, 2=2D, 3=3D
    public byte FixFlags { get; init; }             // bit0=fixOK, etc.
    public byte DateTimeFlags { get; init; }        // confirmation bits
    public byte NumSV { get; init; }

    // Position
    public double LonDeg { get; init; }              // /1e7
    public double LatDeg { get; init; }              // /1e7
    public double AltWGS_m { get; init; }            // mm -> m
    public double AltMSL_m { get; init; }            // mm -> m
    public double HAcc_m { get; init; }              // mm -> m
    public double VAcc_m { get; init; }              // mm -> m
    public double Speed_mps { get; init; }           // mm/s -> m/s (3D or ground per device cfg)
    public double Heading_deg { get; init; }         // /1e5
    public double SAcc_mps { get; init; }            // mm/s -> m/s
    public double HeadingAcc_deg { get; init; }      // /1e5
    public double PDOP { get; init; }                // /100
    public byte LatLonFlags { get; init; }

    // Battery/Input
    public byte BatteryRaw { get; init; }          // Mini/Mini S: bit7=charging, low7=battery %
                                                   // Micro: input voltage *10 (e.g., 0x79 = 12.1V)

    // Motion
    public double GX_g { get; init; }                // milli-g -> g
    public double GY_g { get; init; }
    public double GZ_g { get; init; }
    public double RotX_degps { get; init; }          // centi-deg/s -> deg/s
    public double RotY_degps { get; init; }
    public double RotZ_degps { get; init; }

    public DateTime? UtcTimestampOrNull =>
        Year > 0 ? new DateTime(Year, Month, Day, Hour, Minute, Second, DateTimeKind.Utc) : null;

    public bool FixOk => (FixFlags & 0x01) != 0;
    public bool IsCharging => (BatteryRaw & 0x80) != 0;
    public int BatteryPct => BatteryRaw & 0x7F;
    public double InputVoltage_Micro_V => BatteryRaw / 10.0; // for RaceBox Micro only
  }

  public static class Racebox80Parser
  {
    public static IEnumerable<Racebox80Record> ParseFile(string path)
    {
      foreach (var raw in File.ReadLines(path))
      {
        var line = raw.Trim();
        if (string.IsNullOrEmpty(line)) continue;
        if (line.Length != 160) throw new InvalidDataException($"Line is {line.Length} chars, expected 160.");

        var payload = HexToBytes(line);
        if (payload.Length != 80) throw new InvalidDataException("Decoded payload is not 80 bytes.");

        yield return ParsePayload(payload);
      }
    }

    public static void WriteCsv(string inputPath, string outputCsvPath)
    {
      using var sw = new StreamWriter(outputCsvPath);
      sw.WriteLine(string.Join(",",
          "Utc", "iTOWms", "lat", "lon", "altMSL_m", "altWGS_m", "speed_mps", "speed_kph", "heading_deg",
          "PDOP", "HAcc_m", "VAcc_m", "fixOK", "fixStatus", "numSV",
          "gX", "gY", "gZ", "rotX_degps", "rotY_degps", "rotZ_degps",
          "batt_pct", "charging", "batt_raw"));

      foreach (var r in ParseFile(inputPath))
      {
        var utc = r.UtcTimestampOrNull?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? "";
        var speedKph = r.Speed_mps * 3.6;
        sw.WriteLine(string.Join(",",
            utc, r.ITOWms, r.LatDeg.ToString("F7"), r.LonDeg.ToString("F7"),
            r.AltMSL_m.ToString("F3"), r.AltWGS_m.ToString("F3"),
            r.Speed_mps.ToString("F3"), speedKph.ToString("F3"),
            r.Heading_deg.ToString("F5"), r.PDOP.ToString("F2"),
            r.HAcc_m.ToString("F3"), r.VAcc_m.ToString("F3"),
            r.FixOk, r.FixStatus, r.NumSV,
            r.GX_g.ToString("F3"), r.GY_g.ToString("F3"), r.GZ_g.ToString("F3"),
            r.RotX_degps.ToString("F2"), r.RotY_degps.ToString("F2"), r.RotZ_degps.ToString("F2"),
            r.BatteryPct, r.IsCharging, r.BatteryRaw));
      }
    }

    // ————— internals —————

    private static Racebox80Record ParsePayload(ReadOnlySpan<byte> b)
    {
      // NOTE: all fields are little-endian per spec
      return new Racebox80Record
      {
        ITOWms = BinaryPrimitives.ReadUInt32LittleEndian(b[0..4]),
        Year = BinaryPrimitives.ReadUInt16LittleEndian(b[4..6]),
        Month = b[6],
        Day = b[7],
        Hour = b[8],
        Minute = b[9],
        Second = b[10],
        ValidityFlags = (sbyte)b[11],
        TimeAcc_ns = BinaryPrimitives.ReadUInt32LittleEndian(b[12..16]),
        NanoSeconds = BinaryPrimitives.ReadInt32LittleEndian(b[16..20]),
        FixStatus = b[20],
        FixFlags = b[21],
        DateTimeFlags = b[22],
        NumSV = b[23],

        LonDeg = BinaryPrimitives.ReadInt32LittleEndian(b[24..28]) / 1e7,
        LatDeg = BinaryPrimitives.ReadInt32LittleEndian(b[28..32]) / 1e7,
        AltWGS_m = BinaryPrimitives.ReadInt32LittleEndian(b[32..36]) / 1000.0,
        AltMSL_m = BinaryPrimitives.ReadInt32LittleEndian(b[36..40]) / 1000.0,
        HAcc_m = BinaryPrimitives.ReadUInt32LittleEndian(b[40..44]) / 1000.0,
        VAcc_m = BinaryPrimitives.ReadUInt32LittleEndian(b[44..48]) / 1000.0,
        Speed_mps = BinaryPrimitives.ReadInt32LittleEndian(b[48..52]) / 1000.0,
        Heading_deg = BinaryPrimitives.ReadInt32LittleEndian(b[52..56]) / 1e5,
        SAcc_mps = BinaryPrimitives.ReadUInt32LittleEndian(b[56..60]) / 1000.0,
        HeadingAcc_deg = BinaryPrimitives.ReadUInt32LittleEndian(b[60..64]) / 1e5,
        PDOP = BinaryPrimitives.ReadUInt16LittleEndian(b[64..66]) / 100.0,
        LatLonFlags = b[66],
        BatteryRaw = b[67],

        GX_g = BinaryPrimitives.ReadInt16LittleEndian(b[68..70]) / 1000.0,
        GY_g = BinaryPrimitives.ReadInt16LittleEndian(b[70..72]) / 1000.0,
        GZ_g = BinaryPrimitives.ReadInt16LittleEndian(b[72..74]) / 1000.0,
        RotX_degps = BinaryPrimitives.ReadInt16LittleEndian(b[74..76]) / 100.0,
        RotY_degps = BinaryPrimitives.ReadInt16LittleEndian(b[76..78]) / 100.0,
        RotZ_degps = BinaryPrimitives.ReadInt16LittleEndian(b[78..80]) / 100.0,
      };
    }

    private static byte[] HexToBytes(string hex)
    {
      int len = hex.Length;
      var bytes = new byte[len / 2];
      for (int i = 0; i < len; i += 2)
        bytes[i / 2] = byte.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
      return bytes;
    }
  }
}