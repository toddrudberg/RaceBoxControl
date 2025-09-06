using Undaunted.AirRacing.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic.Logging;
using System.Security.Cryptography;

namespace RaceBoxControl
{
  public partial class frmRaceControl : Form
  {
    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    public static void ShowConsole()
    {
      AllocConsole();
      Console.Clear();
    }

    public static void HideConsole()
    {
      FreeConsole();
    }

    private IReadOnlyList<(ulong address, string name)> _devices;


    public frmRaceControl()
    {
      InitializeComponent();
    }

    private void frmRaceControl_Load(object sender, EventArgs e)
    {
      ShowConsole();
      Console.WriteLine("Console logging started...");

    }
    void WriteColored(string text, ConsoleColor color)
    {
      var old = Console.ForegroundColor;
      Console.ForegroundColor = color;
      Console.WriteLine(text);
      Console.ForegroundColor = old;
    }

    private async void btnScan_Click(object sender, EventArgs e)
    {
      try
      {
        Console.WriteLine("Scanning...");
        RaceBoxComs raceBoxComs = new RaceBoxComs();
        _devices = await raceBoxComs.ListRaceboxesAsync(TimeSpan.FromSeconds(30)); // new API: (addr,name)

        if (_devices.Count == 0)
        {
          Console.WriteLine("No RaceBox discovered. Try again after waking the device.");
          return;
        }

        foreach (var d in _devices)
        {
          Console.WriteLine($"FOUND RB: {d.name} [0x{d.address:X}]");
          lstDevices.Items.Add(d);
        }

        // OPTIONAL: auto-connect to the first one and query status
        //var first = _devices[0];
        //await _rb.ConnectAsync(first.address, first.name);
        Console.WriteLine("Scan Complete...");
        //await _rb.QueryStandaloneStatusAsync();
      }
      catch (Exception ex)
      {
        Console.WriteLine("Scan failed: " + ex);
      }
    }

    private async void btnDownloadAll_Click(object sender, EventArgs e)
    {

      var folder = System.IO.Path.Combine(@"c:\raceBoxData", DateTime.Now.ToString("yyyyMMddHHmm"));
      Directory.CreateDirectory(folder); // creates if missing, no-op if exists

      foreach (var d in _devices)
      {
        TaskCompletionSource<bool> tcsDone = new();
        Undaunted.AirRacing.IO.RaceboxCsvWriter? writer = null;
        await using RaceBoxComs raceboxComs = new();

        void OnRec(byte[] rec80) { writer?.Append(rec80); }
        void OnProg(int got, int exp) { if (exp > 0 && got % 500 == 0) Console.WriteLine($". {got}/{exp}"); }
        void OnDone() { Console.WriteLine("Download complete."); tcsDone.TrySetResult(true); }

        try
        {
          WriteColored($"Connecting to {d.address}...", ConsoleColor.Yellow);
          await raceboxComs.ConnectAsync(d.address, d.name);
          WriteColored($"Connected to {d.name}", ConsoleColor.Green);

          // unique per-device filename
          var safeName = string.Concat(d.name.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)));
          var outPath = Path.Combine(folder, $"{safeName}_{d.address:X}.csv");

          writer = new Undaunted.AirRacing.IO.RaceboxCsvWriter(outPath);

          raceboxComs.HistoryRecord80B += OnRec;
          raceboxComs.DownloadProgress += OnProg;
          raceboxComs.DownloadCompleted += OnDone;

          Console.WriteLine("Starting full download (FF 23)...");
          await raceboxComs.BeginDownloadAsync();

          using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
          using (cts.Token.Register(() => tcsDone.TrySetCanceled()))
            await tcsDone.Task;

          Console.WriteLine($"Saved to: {outPath}");
        }
        catch (TaskCanceledException)
        {
          Console.WriteLine("Download timed out.");
        }
        catch (Exception ex)
        {
          Console.WriteLine("Download failed: " + ex.Message);
        }
        finally
        {
          // unhook first, then disconnect, then dispose writer
          raceboxComs.HistoryRecord80B -= OnRec;
          raceboxComs.DownloadProgress -= OnProg;
          raceboxComs.DownloadCompleted -= OnDone;

          try { await raceboxComs.DisposeAsync(); } catch { /* swallow to keep loop going */ }

          writer?.Dispose();

          // small pause to let the adapter settle between devices
          await Task.Delay(200);
        }
      }
    }


    private void btnReadHex_Click(object sender, EventArgs e)
    {
      List<Racebox80Record> records = new List<Racebox80Record>();
      using (var ofd = new OpenFileDialog())
      {
        ofd.Title = "Select RaceBox CSV";
        ofd.Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*";
        ofd.DefaultExt = "csv";
        ofd.CheckFileExists = true;
        ofd.CheckPathExists = true;

        if (ofd.ShowDialog() == DialogResult.OK)
        {
          string path = ofd.FileName;
          Console.WriteLine($"Selected file: {path}");
          string outpath = ofd.FileName.Replace(".csv", ".processed.csv");

          // Example: parse and dump a few rows
          RaceboxCsv.WriteSelectedCsv(
                inputHexLinesPath: ofd.FileName,
                outputCsvPath: outpath,
                headerCsv: "Utc,lat,lon, altMSL_ft,speed_mph,gX,gY,gZ,HAcc_ft,VAcc_ft,time_accuracy_ms"
            );

          //foreach (var rec in Racebox80Parser.ParseFile(path))
          //{
          //  Console.WriteLine($"{rec.UtcTimestampOrNull:O}, {rec.LatDeg:F6}, {rec.LonDeg:F6}");
          //  records.Add(rec);
          //}
        }
        else
        {
          Console.WriteLine("No file selected.");
        }
      }
    }


    // Returns the chosen folder path or null if canceled
    private string? PickFolder(string? initial = null, string? description = null)
    {
      using var dlg = new FolderBrowserDialog
      {
        SelectedPath = initial ?? @"C:\raceBoxData",
        Description = description ?? "Select destination folder for this RaceBox download",
        ShowNewFolderButton = true
      };

      return dlg.ShowDialog(this) == DialogResult.OK
           ? dlg.SelectedPath
           : null;
    }


    //private async void btnDownloadOne_Click(object sender, EventArgs e)
    //  {
    //    // pick which device (however you choose it)
    //    var d = _devices.First(); // e.g., from a selected item

    //    var folder = PickFolder(description: $"Choose a folder for {d.name} [{d.address:X}]");
    //    if (folder is null) return; // user canceled

    //    Directory.CreateDirectory(folder); // safe even if it exists

    //    var safeName = string.Concat(d.name.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)));
    //    var file = Path.Combine(folder, $"{safeName}_{d.address:X}_{DateTime.Now:yyyyMMddHHmm}.csv");

    //    // …then run your connect/download into `file`
    //    await DownloadOneAsync(d, file);
    //  }
    private async void btnDownLoadIndividual_Click(object sender, EventArgs e)
    {
      string folder = PickFolder();
      TaskCompletionSource<bool> tcsDone = new();
      Undaunted.AirRacing.IO.RaceboxCsvWriter? writer = null;
      await using RaceBoxComs raceboxComs = new();

      void OnRec(byte[] rec80) { writer?.Append(rec80); }
      void OnProg(int got, int exp) { if (exp > 0 && got % 500 == 0) Console.WriteLine($". {got}/{exp}"); }
      void OnDone() { Console.WriteLine("Download complete."); tcsDone.TrySetResult(true); }

      var d = _devices[lstDevices.SelectedIndex];

      try
      {
        WriteColored($"Connecting to {d.address}...", ConsoleColor.Yellow);
        await raceboxComs.ConnectAsync(d.address, d.name);
        WriteColored($"Connected to {d.name}", ConsoleColor.Green);

        // unique per-device filename
        var safeName = string.Concat(d.name.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)));
        var outPath = Path.Combine(folder, $"{safeName}_{d.address:X}.csv");

        writer = new Undaunted.AirRacing.IO.RaceboxCsvWriter(outPath);

        raceboxComs.HistoryRecord80B += OnRec;
        raceboxComs.DownloadProgress += OnProg;
        raceboxComs.DownloadCompleted += OnDone;

        Console.WriteLine("Starting full download (FF 23)...");
        await raceboxComs.BeginDownloadAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using (cts.Token.Register(() => tcsDone.TrySetCanceled()))
          await tcsDone.Task;

        Console.WriteLine($"Saved to: {outPath}");
      }
      catch (TaskCanceledException)
      {
        Console.WriteLine("Download timed out.");
      }
      catch (Exception ex)
      {
        Console.WriteLine("Download failed: " + ex.Message);
      }
      finally
      {
        // unhook first, then disconnect, then dispose writer
        raceboxComs.HistoryRecord80B -= OnRec;
        raceboxComs.DownloadProgress -= OnProg;
        raceboxComs.DownloadCompleted -= OnDone;

        try { await raceboxComs.DisposeAsync(); } catch { /* swallow to keep loop going */ }

        writer?.Dispose();

        // small pause to let the adapter settle between devices
        await Task.Delay(200);
      }
    }

    private async void btnUnpackAll_Click(object sender, EventArgs e)
    {
      var folder = PickFolder(description: "Pick a folder that contains .hex files to process");
      if (folder is null) return;

      var processedDir = Path.Combine(folder, "ProcessedData");
      Directory.CreateDirectory(processedDir); // safe if it already exists

      // Only the chosen folder (not recursive). Change to AllDirectories if you want recursion.
      var hexFiles = Directory.EnumerateFiles(folder, "*.hex", SearchOption.TopDirectoryOnly).ToList();
      if (hexFiles.Count == 0)
      {
        Console.WriteLine("No .hex files found.");
        return;
      }

      foreach (var hexPath in hexFiles)
      {
        var name = Path.GetFileNameWithoutExtension(hexPath);
        var outPath = Path.Combine(processedDir, $"{name}.csv"); // or .bin/.json — whatever you output

        Console.WriteLine($"Processing {Path.GetFileName(hexPath)} ? {outPath}");
        try
        {
          await ProcessHexFileAsync(hexPath, outPath);
          Console.WriteLine("  ? done");
        }
        catch (Exception ex)
        {
          Console.WriteLine($"  ? failed: {ex.Message}");
        }
      }

      Console.WriteLine($"All done. Output in: {processedDir}");
    }

    private Task ProcessHexFileAsync(string hexPath, string outPath)
    {
      // TODO: parse hexPath and write results to outPath
      // Example placeholder:
      // var bytes = File.ReadAllBytes(hexPath); ... transform ... File.WriteAllText(outPath, csv);
      RaceboxCsv.WriteSelectedCsv(
              inputHexLinesPath: hexPath,
              outputCsvPath: outPath,
              headerCsv: "Utc,lat,lon, altMSL_ft,speed_mph,gX,gY,gZ,HAcc_ft,VAcc_ft,time_accuracy_ms"
              );
      return Task.CompletedTask;
    }
  }
}
