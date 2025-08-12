using Undaunted.AirRacing.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualBasic.Logging;

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
    private RaceBoxComs _rb = new();

    public frmRaceControl()
    {
      InitializeComponent();
    }

    private void frmRaceControl_Load(object sender, EventArgs e)
    {
      ShowConsole();
      Console.WriteLine("Console logging started...");

    }

    private async void btnScan_Click(object sender, EventArgs e)
    {
      try
      {
        Console.WriteLine("Scanning...");
        var devices = await _rb.ListRaceboxesAsync(TimeSpan.FromSeconds(20)); // new API: (addr,name)

        if (devices.Count == 0)
        {
          Console.WriteLine("No RaceBox discovered. Try again after waking the device.");
          return;
        }

        foreach (var d in devices)
          Console.WriteLine($"FOUND RB: {d.name} [0x{d.addr:X}]");

        // OPTIONAL: auto-connect to the first one and query status
        var first = devices[0];
        await _rb.ConnectAsync(first.addr, first.name);
        Console.WriteLine("Connected...");
        //await _rb.QueryStandaloneStatusAsync();
      }
      catch (Exception ex)
      {
        Console.WriteLine("Scan failed: " + ex);
      }
    }

    private async void btnDownloadAll_Click(object sender, EventArgs e)
    {
      try
      {
        // Choose an output file
        var outPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            $"racebox_all_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        using var writer = new Undaunted.AirRacing.IO.RaceboxCsvWriter(outPath);

        // Wire events
        var tcsDone = new TaskCompletionSource<bool>();
        int expected = 0, received = 0;

        void OnRec(byte[] rec80) { writer.Append(rec80); received++; if (expected > 0 && received % 500 == 0) Console.WriteLine($"… {received}/{expected}"); }
        void OnProg(int got, int exp) { expected = exp; /* progress bar if you like */ }
        void OnDone() { Console.WriteLine($"Download complete: {received}/{expected} records"); tcsDone.TrySetResult(true); }

        _rb.HistoryRecord80B += OnRec;
        _rb.DownloadProgress += OnProg;
        _rb.DownloadCompleted += OnDone;

        Console.WriteLine("Starting full download (FF 23)...");
        await _rb.BeginDownloadAsync();

        // wait for completion (add a timeout if you want)
        await tcsDone.Task;

        // Unhook
        _rb.HistoryRecord80B -= OnRec;
        _rb.DownloadProgress -= OnProg;
        _rb.DownloadCompleted -= OnDone;

        Console.WriteLine($"Saved to: {outPath}");
      }
      catch (Exception ex)
      {
        Console.WriteLine("Download failed: " + ex.Message);
      }
    }
  }
}
