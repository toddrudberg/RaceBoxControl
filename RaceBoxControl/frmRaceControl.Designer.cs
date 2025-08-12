namespace RaceBoxControl
{
    partial class frmRaceControl
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

    #region Windows Form Designer generated code

    /// <summary>
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
      btnScan = new Button();
      btnConnect = new Button();
      lstDevices = new ListBox();
      SuspendLayout();
      // 
      // btnScan
      // 
      btnScan.Location = new Point(262, 279);
      btnScan.Name = "btnScan";
      btnScan.Size = new Size(150, 46);
      btnScan.TabIndex = 0;
      btnScan.Text = "button1";
      btnScan.UseVisualStyleBackColor = true;
      btnScan.Click += btnScan_Click;
      // 
      // btnConnect
      // 
      btnConnect.Location = new Point(262, 351);
      btnConnect.Name = "btnConnect";
      btnConnect.Size = new Size(150, 46);
      btnConnect.TabIndex = 1;
      btnConnect.Text = "button2";
      btnConnect.UseVisualStyleBackColor = true;
      btnConnect.Click += btnDownloadAll_Click;
      // 
      // lstDevices
      // 
      lstDevices.FormattingEnabled = true;
      lstDevices.Location = new Point(842, 353);
      lstDevices.Name = "lstDevices";
      lstDevices.Size = new Size(683, 420);
      lstDevices.TabIndex = 2;
      // 
      // frmRaceControl
      // 
      AutoScaleDimensions = new SizeF(13F, 32F);
      AutoScaleMode = AutoScaleMode.Font;
      ClientSize = new Size(1718, 984);
      Controls.Add(lstDevices);
      Controls.Add(btnConnect);
      Controls.Add(btnScan);
      Name = "frmRaceControl";
      Text = "Form1";
      Load += frmRaceControl_Load;
      ResumeLayout(false);
    }

    #endregion

    private Button btnScan;
    private Button btnConnect;
    private ListBox lstDevices;
  }
}
