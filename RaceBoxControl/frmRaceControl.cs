using InTheHand.Bluetooth;
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
        var devices = await _rb.ListRaceboxesVerboseAsync(TimeSpan.FromSeconds(20));
        if (devices.Count == 0)
        {
          Console.WriteLine("No RaceBox discovered. Try again after waking the device.");
          return;
        }
        foreach (var d in devices) Console.WriteLine($"FOUND RB: {d.Name} [{d.Id}]");
      }
      catch (Exception ex)
      {
        Console.WriteLine("Scan failed: " + ex);
      }
    }






    private void btnConnect_Click(object sender, EventArgs e)
    {
      //  if (lstDevices.SelectedItem is null) return;
      //  var dev = (BluetoothDevice)lstDevices.SelectedItem.GetType().GetProperty("Device")!.GetValue(lstDevices.SelectedItem);

      //  _rb.StatusReceived += st => AppendLog($"rec:{st.Recording} mem:{st.MemoryPercent}% recs:{st.StoredRecords}/{st.TotalCapacity}");
      //  _rb.AckReceived += ack => AppendLog($"ACK {ack.opClass:X2} {ack.opId:X2}");
      //  _rb.HistoryRecord80B += rec => {/* later: decode -> CSV */};

      //  await _rb.ConnectAsync(dev);
      //  await _rb.QueryStandaloneStatusAsync(); // FF 22
    }
  }
}
