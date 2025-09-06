using RaceBoxControl;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

public static class RaceboxCsv
{
  // Map header → string selector (kept as strings so CSV is ready-formatted)
  private static readonly Dictionary<string, Func<Racebox80Record, string>> Columns =
      new(StringComparer.OrdinalIgnoreCase)
      {
        // Time
        ["Utc"] = r =>
        {
          if (r.UtcTimestampOrNull == null) return "";
          // Add milliseconds from NanoSeconds/ITOWms
          var baseTime = r.UtcTimestampOrNull.Value;
          // ITOWms already has ms since week start, but to keep it simple,
          // we can just add the milliseconds from NanoSeconds (nanoseconds → ms)
          var millis = r.NanoSeconds / 1000000.0;
          var utcWithMs = baseTime.AddMilliseconds(millis);
          return utcWithMs.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);
        },
        ["iTOWms"] = r => r.ITOWms.ToString(CultureInfo.InvariantCulture),
        ["time_accuracy_ms"] = r => (r.TimeAcc_ns / 1e6).ToString("F3", CultureInfo.InvariantCulture),

        // Position
        ["lat"] = r => r.LatDeg.ToString("F7", CultureInfo.InvariantCulture),
        ["lon"] = r => r.LonDeg.ToString("F7", CultureInfo.InvariantCulture),
        ["altMSL_m"] = r => r.AltMSL_m.ToString("F3", CultureInfo.InvariantCulture),
        ["altWGS_m"] = r => r.AltWGS_m.ToString("F3", CultureInfo.InvariantCulture),
        ["altWGS_ft"] = r => (r.AltWGS_m * 3.28084).ToString("F1", CultureInfo.InvariantCulture),
        ["altMSL_ft"] = r => (r.AltMSL_m * 3.28084).ToString("F1", CultureInfo.InvariantCulture),
        ["HAcc_m"] = r => r.HAcc_m.ToString("F3", CultureInfo.InvariantCulture),
        ["VAcc_m"] = r => r.VAcc_m.ToString("F3", CultureInfo.InvariantCulture),
        ["HAcc_ft"] = r => (r.HAcc_m * 3.28084).ToString("F3", CultureInfo.InvariantCulture),
        ["VAcc_ft"] = r => (r.VAcc_m * 3.28084).ToString("F3", CultureInfo.InvariantCulture),
        ["PDOP"] = r => r.PDOP.ToString("F2", CultureInfo.InvariantCulture),
        ["numSV"] = r => r.NumSV.ToString(CultureInfo.InvariantCulture),

        // Motion
        ["speed_mps"] = r => r.Speed_mps.ToString("F3", CultureInfo.InvariantCulture),
        ["speed_kph"] = r => (r.Speed_mps * 3.6).ToString("F3", CultureInfo.InvariantCulture),
        ["speed_mph"] = r => (r.Speed_mps * 2.23694).ToString("F3", CultureInfo.InvariantCulture),
        ["heading_deg"] = r => r.Heading_deg.ToString("F5", CultureInfo.InvariantCulture),
        ["SAcc_mps"] = r => r.SAcc_mps.ToString("F3", CultureInfo.InvariantCulture),
        ["HeadingAcc_deg"] = r => r.HeadingAcc_deg.ToString("F5", CultureInfo.InvariantCulture),

        // Fix/flags
        ["fixOK"] = r => r.FixOk ? "1" : "0",
        ["fixStatus"] = r => r.FixStatus.ToString(CultureInfo.InvariantCulture),

        // Battery/Input
        ["batt_pct"] = r => r.BatteryPct.ToString(CultureInfo.InvariantCulture),
        ["charging"] = r => r.IsCharging ? "1" : "0",
        ["batt_raw"] = r => r.BatteryRaw.ToString(CultureInfo.InvariantCulture),
        ["inputV_micro"] = r => r.InputVoltage_Micro_V.ToString("F1", CultureInfo.InvariantCulture), // Micro only

        // IMU (g, deg/s)
        ["gX"] = r => r.GX_g.ToString("F3", CultureInfo.InvariantCulture),
        ["gY"] = r => r.GY_g.ToString("F3", CultureInfo.InvariantCulture),
        ["gZ"] = r => r.GZ_g.ToString("F3", CultureInfo.InvariantCulture),
        ["rotX_degps"] = r => r.RotX_degps.ToString("F2", CultureInfo.InvariantCulture),
        ["rotY_degps"] = r => r.RotY_degps.ToString("F2", CultureInfo.InvariantCulture),
        ["rotZ_degps"] = r => r.RotZ_degps.ToString("F2", CultureInfo.InvariantCulture),
      };

  /// <summary>
  /// Generates a CSV with only the requested headers (case-insensitive, order preserved).
  /// Input is your hex-lines file (80-byte payload per line).
  /// </summary>
  public static void WriteSelectedCsv(string inputHexLinesPath, string outputCsvPath, string headerCsv)
      => WriteSelectedCsv(inputHexLinesPath, outputCsvPath, ParseHeaderList(headerCsv));

  public static void WriteSelectedCsv(string inputHexLinesPath, string outputCsvPath, IEnumerable<string> headers)
  {
    var headerList = headers.Select(h => h.Trim())
                            .Where(h => !string.IsNullOrEmpty(h))
                            .ToList();

    // Build selector list in header order
    var selectors = new List<Func<Racebox80Record, string>>(headerList.Count);
    foreach (var h in headerList)
    {
      if (!Columns.TryGetValue(h, out var sel))
      {
        Console.WriteLine($"[WARN] Unknown header '{h}' — skipping.");
        continue; // or throw new ArgumentException(...)
      }
      selectors.Add(sel);
    }

    if (selectors.Count == 0)
      throw new ArgumentException("No valid headers were provided.");

    using var sw = new StreamWriter(outputCsvPath);
    sw.WriteLine(string.Join(",", headerList.Where(Columns.ContainsKey))); // write header row

    foreach (var rec in Racebox80Parser.ParseFile(inputHexLinesPath))
    {
      var row = new string[selectors.Count];
      for (int i = 0; i < selectors.Count; i++)
        row[i] = selectors[i](rec);
      sw.WriteLine(string.Join(",", row));
    }
  }

  private static IEnumerable<string> ParseHeaderList(string headerCsv)
      => headerCsv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => s.Trim());
}
