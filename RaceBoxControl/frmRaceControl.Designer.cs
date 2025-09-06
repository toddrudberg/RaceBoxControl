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
      btnReadHex = new Button();
      btnDownLoadIndividual = new Button();
      btnUnpackAll = new Button();
      SuspendLayout();
      // 
      // btnScan
      // 
      btnScan.Location = new Point(343, 357);
      btnScan.Margin = new Padding(4);
      btnScan.Name = "btnScan";
      btnScan.Size = new Size(558, 59);
      btnScan.TabIndex = 0;
      btnScan.Text = "Scan Devices";
      btnScan.UseVisualStyleBackColor = true;
      btnScan.Click += btnScan_Click;
      // 
      // btnConnect
      // 
      btnConnect.Location = new Point(343, 647);
      btnConnect.Margin = new Padding(4);
      btnConnect.Name = "btnConnect";
      btnConnect.Size = new Size(558, 59);
      btnConnect.TabIndex = 1;
      btnConnect.Text = "Download All";
      btnConnect.UseVisualStyleBackColor = true;
      btnConnect.Click += btnDownloadAll_Click;
      // 
      // lstDevices
      // 
      lstDevices.FormattingEnabled = true;
      lstDevices.ItemHeight = 41;
      lstDevices.Location = new Point(1101, 452);
      lstDevices.Margin = new Padding(4);
      lstDevices.Name = "lstDevices";
      lstDevices.Size = new Size(892, 537);
      lstDevices.TabIndex = 2;
      // 
      // btnReadHex
      // 
      btnReadHex.Location = new Point(1121, 1079);
      btnReadHex.Margin = new Padding(4);
      btnReadHex.Name = "btnReadHex";
      btnReadHex.Size = new Size(558, 59);
      btnReadHex.TabIndex = 3;
      btnReadHex.Text = "Read Hex and Convert";
      btnReadHex.UseVisualStyleBackColor = true;
      btnReadHex.Click += btnReadHex_Click;
      // 
      // btnDownLoadIndividual
      // 
      btnDownLoadIndividual.Location = new Point(1121, 1012);
      btnDownLoadIndividual.Margin = new Padding(4);
      btnDownLoadIndividual.Name = "btnDownLoadIndividual";
      btnDownLoadIndividual.Size = new Size(558, 59);
      btnDownLoadIndividual.TabIndex = 4;
      btnDownLoadIndividual.Text = "Download Individual";
      btnDownLoadIndividual.UseVisualStyleBackColor = true;
      btnDownLoadIndividual.Click += btnDownLoadIndividual_Click;
      // 
      // btnUnpackAll
      // 
      btnUnpackAll.Location = new Point(343, 714);
      btnUnpackAll.Margin = new Padding(4);
      btnUnpackAll.Name = "btnUnpackAll";
      btnUnpackAll.Size = new Size(558, 59);
      btnUnpackAll.TabIndex = 5;
      btnUnpackAll.Text = "Unpack All";
      btnUnpackAll.UseVisualStyleBackColor = true;
      btnUnpackAll.Click += btnUnpackAll_Click;
      // 
      // frmRaceControl
      // 
      AutoScaleDimensions = new SizeF(17F, 41F);
      AutoScaleMode = AutoScaleMode.Font;
      ClientSize = new Size(2247, 1261);
      Controls.Add(btnUnpackAll);
      Controls.Add(btnDownLoadIndividual);
      Controls.Add(btnReadHex);
      Controls.Add(lstDevices);
      Controls.Add(btnConnect);
      Controls.Add(btnScan);
      Margin = new Padding(4);
      Name = "frmRaceControl";
      Text = "Form1";
      Load += frmRaceControl_Load;
      ResumeLayout(false);
    }

    #endregion

    private Button btnScan;
    private Button btnConnect;
    private ListBox lstDevices;
    private Button btnReadHex;
    private Button btnDownLoadIndividual;
    private Button btnUnpackAll;
  }
}
