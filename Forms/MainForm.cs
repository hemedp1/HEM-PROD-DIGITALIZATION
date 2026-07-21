using AForge.Video;
using AForge.Video.DirectShow;
using Automation.BDaq;
using QRCoder;
using SLRDbConnector;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Tab;
//////////

namespace RCU_FG_Output_Counter
{
    public partial class MainForm : Form
    {
        private int _count = 0;
        private int _batchBaseline = 0;
        private bool _batchActive = false;
        private List<KeyValuePair<string, int>> _lastPrintedLots = new List<KeyValuePair<string, int>>();
        private bool _updatePending = false;
        private int _lastCaptureCount = 0;

        // Advantech DAQ
        private InstantDiCtrl _diCtrl;
        private int _channel = 0; // input channel number
        private System.Threading.Timer _daqPollTimer;
        private byte _prevPortData = 0;
        private DateTime _lastPulseTime = DateTime.MinValue;
        private int _debounceMillis = 100;
        private bool _daqDeviceIsConnected = false;

        // Cameras
        private FilterInfoCollection videoDevices; // List of all cameras
        private VideoCaptureDevice camera1;
        private VideoCaptureDevice camera2;

        //Add on 110526

        // Camera health monitoring
        private DateTime _lastFrameTimeCam1 = DateTime.MinValue;
        private DateTime _lastFrameTimeCam2 = DateTime.MinValue;

        private System.Windows.Forms.Timer _cameraHealthTimer;
        private readonly object _camLock = new object();

        // Capture countdown timer (UI timer)
        private System.Windows.Forms.Timer _captureTimer;
        private int _countdownValue = 0;
        private bool _countdownActive = false;

        //  ?
        private string _currentFG; 
        private string _currentCPN;
        private string _currentCPN2;
        private string _currentLot;
        private int _currentStdPack;

        private int currentSerial = 0;
        private int endSerial = 0;
        private QRCodeGenerator qrGenerator = new QRCodeGenerator();

        private bool isReprint = false;
        private string reprintBatch = "";
        private int reprintSerial = 0;


        private string rfMark = "";
        private string cMark = "";

        private bool isQCCopy = false;
        public MainForm()
        {
            InitializeComponent();
            InitAdvantechDaq(); 
            _captureTimer = new System.Windows.Forms.Timer();
            _captureTimer.Interval = 1000;              // 1 second
            _captureTimer.Tick += CaptureTimer_Tick;     // <-- this needs the method below
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.MainForm_FormClosing);

            printDocument1.PrinterSettings.PrinterName = "Adobe PDF"; //"4BARCODE 4B-3044TC";
            
        }
        private void MainForm_Load(object sender, EventArgs e)
        {
            using (var prompt = new PasswordPrompt())
            {
                if (prompt.ShowDialog(this) == DialogResult.OK)
                {
                    if (prompt.EnteredPassword != "admin")
                    {
                        MessageBox.Show("Incorrect password. Application will close.",
                                        "Access Denied", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        this.Close();
                        return;
                    }
                }
                else
                {
                    this.Close();
                    return;
                }
            }

            // Disable QR scanning until line selected
            txtQRBOM.ReadOnly = true;

            txtWO.ReadOnly = true;
            txtWOQ.ReadOnly = true;
            txtcpn1.ReadOnly = true;
            txtcpn2.ReadOnly = true;
            txtHEMPN.ReadOnly = true;
            txtPrddte.ReadOnly = true;
            txtLot.ReadOnly = true;
            txtSTDPK.ReadOnly = true;

            // =========================
            // CAMERA INITIALIZATION
            // =========================
            videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);

            if (videoDevices.Count < 2)
            {
                MessageBox.Show("Less than 2 cameras detected.",
                                "Camera Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            StartCamera1();
            StartCamera2();

            // =========================
            // CAMERA HEALTH TIMER (AUTO RECOVERY)
            // =========================
            _cameraHealthTimer = new System.Windows.Forms.Timer();
            _cameraHealthTimer.Interval = 2000; // check every 2 seconds
            _cameraHealthTimer.Tick += CameraHealthTimer_Tick;
            _cameraHealthTimer.Start();
        }
        private void StartCamera1()
        {
            try
            {
                camera1 = new VideoCaptureDevice(videoDevices[0].MonikerString);
                camera1.NewFrame += Camera1_NewFrame;
                camera1.Start();
                _lastFrameTimeCam1 = DateTime.Now;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Camera 1 start failed: " + ex.Message);
            }
        }

        private void StartCamera2()
        {
            try
            {
                camera2 = new VideoCaptureDevice(videoDevices[1].MonikerString);
                camera2.NewFrame += Camera2_NewFrame;
                camera2.Start();
                _lastFrameTimeCam2 = DateTime.Now;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Camera 2 start failed: " + ex.Message);
            }
        }
        private void Camera1_NewFrame(object sender, NewFrameEventArgs e)
        {
            try
            {
                _lastFrameTimeCam1 = DateTime.Now;

                Bitmap bmp = (Bitmap)e.Frame.Clone();

                this.BeginInvoke((MethodInvoker)(() =>
                {
                    var old = picCamera1.Image;
                    picCamera1.Image = bmp;
                    old?.Dispose();
                }));
            }
            catch { }
        }

        private void Camera2_NewFrame(object sender, NewFrameEventArgs e)
        {
            try
            {
                _lastFrameTimeCam2 = DateTime.Now;

                Bitmap bmp = (Bitmap)e.Frame.Clone();

                this.BeginInvoke((MethodInvoker)(() =>
                {
                    var old = picCamera2.Image;
                    picCamera2.Image = bmp;
                    old?.Dispose();
                }));
            }
            catch { }
        }
        private void CameraHealthTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if ((DateTime.Now - _lastFrameTimeCam1).TotalSeconds > 3)
                {
                    RestartCamera(1);
                }

                if ((DateTime.Now - _lastFrameTimeCam2).TotalSeconds > 3)
                {
                    RestartCamera(2);
                }
            }
            catch { }
        }
        private void RestartCamera(int camIndex)
        {
            lock (_camLock)
            {
                try
                {
                    if (videoDevices == null || videoDevices.Count < camIndex)
                        return;

                    if (camIndex == 1)
                    {
                        if (camera1 != null)
                        {
                            if (camera1.IsRunning)
                            {
                                camera1.SignalToStop();
                                camera1.WaitForStop();
                            }
                            camera1.NewFrame -= Camera1_NewFrame;
                            camera1 = null;
                        }

                        StartCamera1();
                    }
                    else if (camIndex == 2)
                    {
                        if (camera2 != null)
                        {
                            if (camera2.IsRunning)
                            {
                                camera2.SignalToStop();
                                camera2.WaitForStop();
                            }
                            camera2.NewFrame -= Camera2_NewFrame;
                            camera2 = null;
                        }

                        StartCamera2();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Camera restart failed: " + ex.Message);
                }
            }
        }
        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // 1️⃣ First check if the user is trying to close manually
            if (e.CloseReason == CloseReason.UserClosing && _batchActive)
            {
                e.Cancel = true;
                MessageBox.Show(
                    "Unable to close — a batch is currently in progress!",
                    "Batch Active",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return; // VERY IMPORTANT
            }

            // 2️⃣ Only run cleanup if closing is allowed
            _daqPollTimer?.Dispose();
            _diCtrl?.Dispose();

            // Stop camera health timer
            _cameraHealthTimer?.Stop();
            _cameraHealthTimer?.Dispose();

            if (camera1 != null)
            {
                if (camera1.IsRunning)
                {
                    camera1.SignalToStop();
                    camera1.WaitForStop();
                }
            }

            if (camera2 != null)
            {
                if (camera2.IsRunning)
                {
                    camera2.SignalToStop();
                    camera2.WaitForStop();
                }
            }
        }
        private void cmbline_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cmbline.SelectedIndex != -1)
            {
                txtQRBOM.ReadOnly = false;
                txtQRBOM.Focus(); // ready for QR scan
            }
            else
            {
                txtQRBOM.ReadOnly = true;
            }
        }
        private void InitAdvantechDaq()
        {
            try
            {
                _diCtrl = new InstantDiCtrl();

                if (_diCtrl.SupportedDevices == null || _diCtrl.SupportedDevices.Count == 0)
                {
                    _diCtrl?.Dispose();
                    _diCtrl = null;
                    _daqDeviceIsConnected = false;
                    lblStatus.Text = "No DAQ Found";
                    lblStatus.ForeColor = Color.Orange;
                    return;
                }

                // === DEBUG: Show all devices detected ===
                string devices = string.Join("\n",
                    _diCtrl.SupportedDevices.Cast<DeviceTreeNode>()
                        .Select(d => $"{d.Description}, BID={d.DeviceNumber}")
                );
                //MessageBox.Show("Detected devices:\n" + devices, "DAQ Debug");

                // === Pick USB-4750 (ignore DemoDevice) ===
                //var node = _diCtrl.SupportedDevices
                //.FirstOrDefault(d => d.Description.Contains("USB-4750"));
                var node = _diCtrl.SupportedDevices
                    .FirstOrDefault(d => d.DeviceNumber == 1);  // <-- select BID#1

                if (node.Equals(default(Automation.BDaq.DeviceTreeNode)))
                {
                    _daqDeviceIsConnected = false;
                    lblStatus.Text = "USB-4750 not found!";
                    lblStatus.ForeColor = Color.Orange;
                    return;
                }

                // Bind to that device
                _diCtrl.SelectedDevice = new DeviceInformation(1);

                // === DEBUG: Show what we actually bound to ===
                MessageBox.Show("Bound to: " + _diCtrl.SelectedDevice.Description +
                                " (BID=" + _diCtrl.SelectedDevice.DeviceNumber + ")",
                                "DAQ Debug");

                if (!_diCtrl.Initialized)
                {
                    _daqDeviceIsConnected = false;
                    lblStatus.Text = "DAQ Not Connected";
                    lblStatus.ForeColor = Color.Red;
                    return;
                }

                // Start polling
                _daqPollTimer = new System.Threading.Timer(DaqPoll, null, 0, 40);
                _daqDeviceIsConnected = true;
                lblStatus.Text = "Connected to DAQ";
                //lblStatus.Text = $"Connected to DAQ {node.Description}, BID={node.DeviceNumber}";
                lblStatus.ForeColor = Color.Green;
            }
            catch (Exception ex)
            {
                _diCtrl?.Dispose();
                _diCtrl = null;
                _daqDeviceIsConnected = false;
                lblStatus.Text = "DAQ Init Error";
                lblStatus.ForeColor = Color.Red;

                if (this.Visible)
                {
                    MessageBox.Show(
                        "Error initializing DAQ:\n" + ex.Message,
                        "DAQ Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error
                    );
                }
            }
        }
        private void loadLineDETAILS()
        {
            try
            {
                using (SqlConnection con = new SqlConnection(ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString))
                {
                    con.Open();

                    string query = @"
            SELECT 
                Line_ID, 
                Leader, 
                Sub_Leader, 
                No_OperatorHEM, 
                No_OperatorSUB, 
                No_Operator
            FROM PRODUCTIONLINE
            WHERE Work_Order = @Work_Order
              AND HEM_PN = @hempn
              AND Line_ID = @LINE_NO
              AND CONVERT(date, Tarikh) = @Tarikh;";

                    using (SqlCommand cmd = new SqlCommand(query, con))
                    {
                        cmd.Parameters.AddWithValue("@Work_Order", txtWO.Text);
                        cmd.Parameters.AddWithValue("@hempn", txtHEMPN.Text);
                        cmd.Parameters.AddWithValue("@LINE_NO", cmbline.Text);

                        // FIX: Send DateTime, not string
                        cmd.Parameters.Add("@Tarikh", SqlDbType.Date).Value =
                            DateTime.Parse(txtPrddte.Text);

                        using (SqlDataReader dr = cmd.ExecuteReader())
                        {
                            if (dr.Read())
                            {
                                //txtLine.Text = dr["Line_ID"].ToString();
                                txtleader.Text = dr["Leader"].ToString();
                                txtsleader.Text = dr["Sub_Leader"].ToString();
                                txtophem.Text = dr["No_OperatorHEM"].ToString();
                                txtopsub.Text = dr["No_OperatorSUB"].ToString();
                                txtttl.Text = dr["No_Operator"].ToString();
                            }
                            else
                            {
                                // Clear fields when no data found
                                // txtLine.Clear();
                                txtleader.Clear();
                                txtsleader.Clear();
                                txtophem.Clear();
                                txtopsub.Clear();
                                txtttl.Clear();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading line details: {ex.Message}",
                    "Database Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void woStatus()
        {
            string query = @"select Batch_Output_Status from BATCH_OUTPUT_UNIFIED_8 where Work_Order = @WO";

            string connectionString = ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString;

            try
            {
                using (SqlConnection con = new SqlConnection(connectionString))
                using (SqlCommand cmd = new SqlCommand(query, con))
                {
                    cmd.Parameters.AddWithValue("@WO", txtWO.Text);

                    con.Open();
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            // Just show total WO output
                            lblWoStatus.Text = reader["Batch_Output_Status"].ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error updating labels: {ex.Message}");
                label1.Text = "0";
            }
        }
        private void Total_Output_WO()   //called inside btnmisc_Click - last row 
        {
            string query = @"
        SELECT 
        ISNULL(SUM(BATCH_QTY), 0) AS Total_WO_Output
        FROM PROD_OUTPUT
        WHERE WO = @WO";

            string connectionString = ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString;

            try
            {
                using (SqlConnection con = new SqlConnection(connectionString))
                using (SqlCommand cmd = new SqlCommand(query, con))
                {
                    cmd.Parameters.AddWithValue("@WO", txtWO.Text);

                    con.Open();
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            // Just show total WO output
                            label1.Text = reader["Total_WO_Output"].ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error updating labels: {ex.Message}");
                label1.Text = "0";
            }
        }
        private void Total_Output_WO_Today()
        {
            if (string.IsNullOrWhiteSpace(txtWO.Text))
            {
                label1.Text = "0";
                return;
            }

            if (!DateTime.TryParseExact(txtPrddte.Text, "dd/MM/yyyy",
                                        CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out DateTime prodDate))
            {
                MessageBox.Show("Invalid production date format. Please use dd/MM/yyyy.");
                return;
            }

            string query = @"
            SELECT ISNULL(SUM(BATCH_QTY), 0) AS Total_WO_Output
            FROM PROD_OUTPUT
            WHERE WO = @WO AND PROD_DATE = @ProductionDate";

            try
            {
                using (SqlConnection con = new SqlConnection(
                       ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString))
                using (SqlCommand cmd = new SqlCommand(query, con))
                {
                    cmd.Parameters.Add("@WO", SqlDbType.VarChar).Value = txtWO.Text.Trim();
                    cmd.Parameters.Add("@ProductionDate", SqlDbType.Date).Value = prodDate.Date;

                    con.Open();

                    object result = cmd.ExecuteScalar();
                    label2.Text = (result != null) ? result.ToString() : "0";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error updating labels: {ex.Message}");
                label1.Text = "0";
            }
        }
        private void No_of_boxes()  //called inside btnStartBatch_Click
        {
            try
            {
                int totalOutput = Convert.ToInt32(label1.Text);
                int stdPk = Convert.ToInt32(txtSTDPK.Text);

                if (stdPk > 0)
                {
                    // Integer division (floor)
                    int result = totalOutput / stdPk;
                    label3.Text = result.ToString();
                }
                else
                {
                    label3.Text = "0";
                }
            }
            catch
            {
                label3.Text = "0";
            }
        }
        private void DaqPoll(object state)
        {
            if (!_daqDeviceIsConnected || _diCtrl == null)
                return;

            try
            {
                byte portData = 0;
                var ret = _diCtrl.Read(0, out portData);

                if (ret != ErrorCode.Success)
                {
                    HandleDaqDisconnect($"DAQ read error: {ret}");
                    return;
                }

                bool curr = (portData & (1 << _channel)) != 0;
                bool prev = (_prevPortData & (1 << _channel)) != 0;

                // Rising edge detection
                if (!prev && curr)
                {
                    var now = DateTime.UtcNow;

                    if ((now - _lastPulseTime).TotalMilliseconds >= _debounceMillis)
                    {
                        _lastPulseTime = now;

                        Interlocked.Increment(ref _count);

                        //_count++;
                        //this.BeginInvoke((MethodInvoker)OnSensorPulseDetected);

                        if (!_updatePending)
                        {
                            _updatePending = true;
                            this.BeginInvoke((MethodInvoker)delegate
                            {
                                try
                                {
                                    OnSensorPulseDetected();
                                }
                                finally
                                {
                                    _updatePending = false;
                                }
                            });
                        }
                    }
                }

                _prevPortData = portData;
            }
            catch (Exception ex)
            {
                HandleDaqDisconnect($"DAQ communication failed: {ex.Message}");
            }
        }
        private void HandleDaqDisconnect(string reason)
        {
            _daqDeviceIsConnected = false;

            try
            {
                _daqPollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                _daqPollTimer?.Dispose();
                _daqPollTimer = null;
            }
            catch { }

            this.BeginInvoke((MethodInvoker)delegate
            {
                lblStatus.Text = "DAQ Disconnected!";
                lblStatus.ForeColor = Color.Red;
                MessageBox.Show(reason);
            });

            _diCtrl?.Dispose();
            _diCtrl = null;
        }
       
        private void StartCountdown()
        {
            if (_countdownActive) return;

            _countdownValue = 20;
            _countdownActive = true;
            lbltimer.Visible = true;
            lbltimer.Text = $"Capturing in {_countdownValue}...";
            _captureTimer.Start();
        }
        private void CancelCountdown()
        {
            if (_captureTimer != null && _captureTimer.Enabled)
                _captureTimer.Stop();

            _countdownActive = false;
            lbltimer.Text = string.Empty;
            lbltimer.Visible = true;
        }
        private void CaptureTimer_Tick(object sender, EventArgs e)
        {
            _countdownValue--;

            if (_countdownValue > 0)
            {
                lbltimer.Text = $"Capturing in {_countdownValue}...";

                int freq = 2000 + (20 - _countdownValue) * 200; // 600 Hz at start, +200 Hz each step
                try
                {
                    Console.Beep(freq, 200); // 200 ms tone
                }
                catch
                {
                    System.Media.SystemSounds.Beep.Play();
                }
                // Change to red when 3 seconds or less remain
                if (_countdownValue <= 3)
                {
                    lbltimer.ForeColor = System.Drawing.Color.Red;
                }
                else
                {
                    lbltimer.Visible = true;
                }
            }
            else
            {
                CancelCountdown();
                _countdownActive = false;
                lbltimer.Text = string.Empty;
                lbltimer.Visible = true; // reset to visible

                // Final beep (longer / different pitch)
                try
                {
                    Console.Beep(2000, 400); // higher pitch for "capture"
                }
                catch
                {
                    System.Media.SystemSounds.Hand.Play(); // fallback sound
                }

                // Trigger capture
                btnCapture_Click_1(null, null);
            }
        }
        private void btnmisc_Click(object sender, EventArgs e)
        {
            // === Password prompt before starting ===
            using (var prompt = new PasswordPrompt())
            {
                if (prompt.ShowDialog(this) == DialogResult.OK)
                {
                    if (prompt.EnteredPassword != "admin") // check password
                    {
                        System.Media.SystemSounds.Hand.Play();
                        MessageBox.Show("Incorrect password!", "Access Denied",
                                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return; // stop here
                    }

                }
                else
                {
                    return; // canceled
                }
            }

            if (!_batchActive)
            {
                MessageBox.Show("Unable to do misc qty, No active batch running.",
                                "Misc Qty",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                return;
            }

            using (var inputForm = new Form())
            {
                inputForm.Width = 300;
                inputForm.Height = 150;
                inputForm.Text = "Adjust Count";
                inputForm.StartPosition = FormStartPosition.CenterScreen;

                Label lbl = new Label() { Left = 10, Top = 20, Text = "Enter new count:" };
                TextBox txt = new TextBox() { Left = 120, Top = 18, Width = 130 };
                txt.KeyPress += (s, ev) =>
                {
                    // allow only digits and control keys (Backspace, etc.)
                    if (!char.IsControl(ev.KeyChar) && !char.IsDigit(ev.KeyChar))
                    {
                        ev.Handled = true;
                    }
                };

                Button btnOk = new Button() { Text = "OK", Left = 120, Width = 60, Top = 50, DialogResult = DialogResult.OK };
                Button btnCancel = new Button() { Text = "Cancel", Left = 190, Width = 60, Top = 50, DialogResult = DialogResult.Cancel };

                inputForm.Controls.Add(lbl);
                inputForm.Controls.Add(txt);
                inputForm.Controls.Add(btnOk);
                inputForm.Controls.Add(btnCancel);

                inputForm.AcceptButton = btnOk;
                inputForm.CancelButton = btnCancel;

                if (inputForm.ShowDialog() == DialogResult.OK)
                {
                    if (int.TryParse(txt.Text, out int newCount))
                    {
                        // Reset baseline so live sensor continues from newCount
                        int current = Interlocked.Add(ref _count, 0);
                        _batchBaseline = current - newCount;
                        Interlocked.Exchange(ref _count, current);
                        //++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++  "N0" format specifier
                        // Update UI immediately
                        lblCount.Text = newCount.ToString();
                        lblLastScan.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                        ///++++++++++++++++++++++++++++++++++           Query Msc Qty data           +++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++

                        try
                        {
                            string connectionString = ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString;
                            using (SqlConnection con = new SqlConnection(connectionString))
                            {
                                con.Open();

                                // SQL Update for specific row with date
                                string updateQuery = @"
                                            UPDATE PROD_OUTPUT
                                            SET BATCH_QTY = @qtyAdjustment
                                            WHERE WO = @WO
                                              AND BATCH_NO = @BatchNo
                                              AND PROD_DATE = @ProductionDate
                                              AND LINE_NO = @LINE_NO";

                                using (SqlCommand cmd = new SqlCommand(updateQuery, con))
                                {
                                    cmd.Parameters.AddWithValue("@WO", txtWO.Text);
                                    cmd.Parameters.AddWithValue("@LINE_NO", cmbline.Text);
                                    cmd.Parameters.AddWithValue("@BatchNo", cmbBatchID.Text);
                                    DateTime prodDate = DateTime.ParseExact(txtPrddte.Text, "dd/MM/yyyy", null);
                                    cmd.Parameters.Add("@ProductionDate", SqlDbType.Date).Value = prodDate;

                                    cmd.Parameters.AddWithValue("@qtyAdjustment", Convert.ToInt32(lblCount.Text));

                                    int rows = cmd.ExecuteNonQuery();

                                    if (rows == 0)
                                    {
                                        MessageBox.Show("No matching record found to update.",
                                            "Notice", MessageBoxButtons.OK, MessageBoxIcon.Information);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            MessageBox.Show("An error occurred while saving the data: " + ex.Message,
                                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }


                        ///++++++++++++++++++++++++++++++++++++++++++++++++++           Query           +++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++



                    }
                    else
                    {
                        MessageBox.Show("Invalid number entered!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                Total_Output_WO();
            }
        }
        private void btnStartBatch_Click(object sender, EventArgs e)
        {
            // === Password prompt before starting test===
            using (var prompt = new PasswordPrompt())
            {
                if (prompt.ShowDialog(this) == DialogResult.OK)
                {
                    if (prompt.EnteredPassword != "admin") // check password
                    {
                        System.Media.SystemSounds.Hand.Play();
                        MessageBox.Show("Incorrect password!", "Access Denied",
                                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return; // stop here
                    }

                }
                else
                {
                    return; // canceled
                }
            }
            // === Check if a batch is already active ===
            if (_batchActive)
            {
                MessageBox.Show("Unable to start batch, there is already a batch running.",
                                "Batch Already Active",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                return; // stop here, don't start a new batch
            }

            //  Check Batch ID is not empty
            if (string.IsNullOrWhiteSpace(cmbBatchID.Text))
            {
                MessageBox.Show("Please select or enter a Batch ID before starting the batch.",
                                "Missing Batch ID",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                return; // stop execution here
            }

            //  Optional: check STDPK too
            if (string.IsNullOrWhiteSpace(txtSTDPK.Text))
            {
                MessageBox.Show("Please scan BOM Master.",
                                "Missing BOM Master Details",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                return;
            }

            //  If valid, proceed with your existing start batch logic
            _batchActive = true;
            _batchBaseline = Interlocked.Add(ref _count, 0);
            Interlocked.Exchange(ref _count, _batchBaseline);
            _lastCaptureCount = 0;
            lblCount.Text = "0";
            cmbBatchID.Enabled = false;

            // 🔒 Safeguard UI Fields: Disable editing during an active run
            txtWOQ.Enabled = false;   // Main Fresh Lot Qty
            txtWOQ2.Enabled = false;  // Rework Lot 2 Qty
            txtWOQ3.Enabled = false;  // Rework Lot 3 Qty

            // Optional: It's a good idea to lock the Lot number inputs too so they can't be changed mid-batch
            txtLot.Enabled = false;
            txtLot2.Enabled = false;
            txtLot3.Enabled = false;

            lblstatup.Text = "Started";
            lblstatup.ForeColor = System.Drawing.Color.Green;

            if (!cmbBatchID.Items.Contains(cmbBatchID.Text))
            {
                cmbBatchID.Items.Add(cmbBatchID.Text);
            }

            MessageBox.Show($"Batch {cmbBatchID.Text} started successfully.",
                            "Batch Started",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
            resumeWorkOrder(txtWO.Text);
            //Total_Output_WO_Today();
            UpdateOutputLabels();
            Total_Output_WO_Today();
            No_of_boxes();
        }
        private void btnEndBatch_Click(object sender, EventArgs e)
        {
            // === Password prompt before starting ===
            using (var prompt = new PasswordPrompt())
            {
                if (prompt.ShowDialog(this) == DialogResult.OK)
                {
                    if (prompt.EnteredPassword != "admin") // check password
                    {
                        System.Media.SystemSounds.Hand.Play();
                        MessageBox.Show("Incorrect password!", "Access Denied",
                                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return; // stop here
                    }

                }
                else
                {
                    return; // canceled
                }
            }
            // If no batch is active, just return
            if (!_batchActive)
            {
                MessageBox.Show("No active batch to stop.",
                                "Stop Batch",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                return;
            }

            // Cancel any running countdown
            CancelCountdown();

            // Deactivate batch
            _batchActive = false;

            // Unlock Batch ID for next batch
            cmbBatchID.Enabled = true;

            // 🔓 Restore UI Fields: Re-enable editing for the next batch setup
            txtWOQ.Enabled = true;
            txtWOQ2.Enabled = true;
            txtWOQ3.Enabled = true;

            // Optional: Re-enable the Lot textboxes
            txtLot.Enabled = true;
            txtLot2.Enabled = true;
            txtLot3.Enabled = true;

            // Update status
            lblstatup.Text = "Stopped";
            lblstatup.ForeColor = System.Drawing.Color.Red;

            try
            {
                // 1️⃣ Calculate the leftover pieces in the current partial box using Modulo (%)
                int totalPulses = Interlocked.Add(ref _count, 0);
                int currentBatchTotal = totalPulses - _batchBaseline;
                if (currentBatchTotal < 0) currentBatchTotal = 0;

                int stdPack = 1;
                int.TryParse(txtSTDPK.Text, out stdPack);
                if (stdPack <= 0) stdPack = 1;

                int loosePieces = currentBatchTotal % stdPack;

                // 🎯 SCENARIO A: There are loose pieces to save (e.g., 45 pieces left over)
                if (loosePieces > 0)
                {
                    int remainingToDeduct = loosePieces;

                    int q2Available = 0;
                    int q3Available = 0;
                    int mainAvailable = 0;

                    int.TryParse(txtWOQ2.Text, out q2Available);
                    int.TryParse(txtWOQ3.Text, out q3Available);
                    int.TryParse(txtWOQ.Text, out mainAvailable);

                    // Track what we deduct for this final partial box
                    List<KeyValuePair<string, int>> lotsUsedForEndBatch = new List<KeyValuePair<string, int>>();

                    // Check Rework Lot 2
                    if (remainingToDeduct > 0 && q2Available > 0 && !string.IsNullOrWhiteSpace(txtLot2.Text))
                    {
                        int take = Math.Min(remainingToDeduct, q2Available);
                        lotsUsedForEndBatch.Add(new KeyValuePair<string, int>(txtLot2.Text.Trim(), take));
                        q2Available -= take;
                        remainingToDeduct -= take;
                        txtWOQ2.Text = q2Available.ToString();
                    }

                    // Check Rework Lot 3
                    if (remainingToDeduct > 0 && q3Available > 0 && !string.IsNullOrWhiteSpace(txtLot3.Text))
                    {
                        int take = Math.Min(remainingToDeduct, q3Available);
                        lotsUsedForEndBatch.Add(new KeyValuePair<string, int>(txtLot3.Text.Trim(), take));
                        q3Available -= take;
                        remainingToDeduct -= take;
                        txtWOQ3.Text = q3Available.ToString();
                    }

                    // Check Fresh/Main Lot
                    if (remainingToDeduct > 0 && mainAvailable > 0)
                    {
                        int take = Math.Min(remainingToDeduct, mainAvailable);
                        lotsUsedForEndBatch.Add(new KeyValuePair<string, int>(txtLot.Text.Trim(), take));
                        mainAvailable -= take;
                        remainingToDeduct -= take;
                        txtWOQ.Text = mainAvailable.ToString();
                    }

                    // Fallback Safety
                    if (remainingToDeduct > 0)
                    {
                        lotsUsedForEndBatch.Add(new KeyValuePair<string, int>(txtLot.Text.Trim(), remainingToDeduct));
                    }

                string connectionString = ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString;
                using (SqlConnection con = new SqlConnection(connectionString))
                {
                    con.Open();

                        foreach (var lotAllocation in lotsUsedForEndBatch)
                        {
                            string allocatedLot = lotAllocation.Key;
                            int allocatedQty = lotAllocation.Value;

                            string insertQuery = @"
                            INSERT INTO PROD_OUTPUT 
                            (WO, WO_Qty, HEM_PN, CUST_PN_1, CUST_PN_2, PROD_DATE, PROD_TIME, LOT, STDPK, LINE_NO, 
                            BATCH_NO, BATCH_QTY, Leader, Sub_Leader, No_OperatorHEM, No_OperatorSUB, No_Operator, Remarks)
                            VALUES 
                            (@WO, @WO_Qty, @HEM_PN, @CUST_PN_1, @CUST_PN_2, @PROD_DATE, @PROD_TIME, @LOT, @STDPK, @LINE_NO, 
                            @BATCH_NO, @BATCH_QTY, @Leader, @Sub_Leader, @No_OperatorHEM, @No_OperatorSUB, @No_Operator, @Remarks)";

                            using (SqlCommand insertCmd = new SqlCommand(insertQuery, con))
                            {
                                insertCmd.Parameters.AddWithValue("@WO", txtWO.Text);
                                insertCmd.Parameters.AddWithValue("@WO_Qty", Convert.ToInt32(txtWOQ.Text));
                                insertCmd.Parameters.AddWithValue("@HEM_PN", txtHEMPN.Text);
                                insertCmd.Parameters.AddWithValue("@CUST_PN_1", txtcpn1.Text);
                                insertCmd.Parameters.AddWithValue("@CUST_PN_2", txtcpn2.Text);

                                DateTime prodDate = DateTime.ParseExact(txtPrddte.Text, "dd/MM/yyyy", null);
                                insertCmd.Parameters.AddWithValue("@PROD_DATE", prodDate);
                                insertCmd.Parameters.AddWithValue("@PROD_TIME", DateTime.Now.TimeOfDay);

                                // Save dynamic data for this split row
                                insertCmd.Parameters.AddWithValue("@LOT", allocatedLot);
                                insertCmd.Parameters.AddWithValue("@BATCH_QTY", allocatedQty);

                                insertCmd.Parameters.AddWithValue("@STDPK", stdPack);
                                insertCmd.Parameters.AddWithValue("@LINE_NO", cmbline.Text);
                                insertCmd.Parameters.AddWithValue("@BATCH_NO", cmbBatchID.Text);

                                insertCmd.Parameters.AddWithValue("@Leader", txtleader.Text);
                                insertCmd.Parameters.AddWithValue("@Sub_Leader", txtsleader.Text);
                                insertCmd.Parameters.AddWithValue("@No_OperatorHEM", Convert.ToInt32(txtophem.Text));
                                insertCmd.Parameters.AddWithValue("@No_OperatorSUB", Convert.ToInt32(txtopsub.Text));
                                insertCmd.Parameters.AddWithValue("@No_Operator", Convert.ToInt32(txtttl.Text));
                                insertCmd.Parameters.AddWithValue("@Remarks", txtrmk.Text);

                                insertCmd.ExecuteNonQuery();
                            }
                        }
                    }

                    ExportToFile(); // Create the Sage file including the loose pieces
                    MessageBox.Show($"End of batch pieces ({loosePieces} PCS) saved successfully.", "Data Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    // 🎯 SCENARIO B: Stopped exactly on a full box target
                    // No loose pieces left to insert, but generate the file for Sage anyway
                    ExportToFile();
                    MessageBox.Show("Batch ended perfectly on a full carton box. No loose pieces to record.", "Data Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("An error occurred while saving the data: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }


            // Reset visible count to 0 (operator clarity)
            lblCount.Text = "0";

            MessageBox.Show($"Batch {cmbBatchID.Text} stopped successfully.",
                            "Batch Stopped",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);


            
            // Clear the Batch ID text
            cmbBatchID.Text = string.Empty;
            woStatus();
            Total_Output_WO();
            Total_Output_WO_Today();
            No_of_boxes();
        }
        
       
        private void ProcessQR(string qrData)
        {
            if (string.IsNullOrWhiteSpace(qrData))
                return;

            // Split by comma
            string[] parts = qrData.Split(',');

            // ensure to have enough segments
            if (parts.Length < 8)
            {
                MessageBox.Show("QR data incomplete: " + qrData, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            // WO25005206,200,HTR1041-011050,CPN1,CPN2,25/05/2026,LOT1,LOT2,20,LOT3,50,100,CUSNAM,RF,CUSPO
            //Unlocked the textbox
            txtWO.ReadOnly = false;
            txtWOQ.ReadOnly = false;
            txtcpn1.ReadOnly = false;
            txtcpn2.ReadOnly = false;
            txtHEMPN.ReadOnly = false;
            txtPrddte.ReadOnly = false;
            txtLot.ReadOnly = false;
            txtLot2.ReadOnly = false;
            txtLot3.ReadOnly = false;
            txtWOQ2.ReadOnly = false;
            txtWOQ3.ReadOnly = false;
            txtSTDPK.ReadOnly = false;
            txtPO.ReadOnly = false;
            txtCust.ReadOnly = false;
            cmbBatchID.Enabled = true;
            //   0          

            // Assign to textboxes
            txtWO.Text = parts[0].Trim();     // Work Order
            txtWOQ.Text = parts[1].Trim();    // Work Order Qty
            txtHEMPN.Text = parts[2].Trim();  // Part Number
            txtcpn1.Text = parts[3].Trim();   // cpn1
            txtcpn2.Text = parts[4].Trim();   // cpn2
            txtPrddte.Text = parts[5].Trim(); // Production Date
            txtLot3.Text = parts[9].Trim();// Lot 3
            txtLot.Text = parts[6].Trim();//Lot 1
            txtWOQ2.Text = parts[8].Trim();
            txtLot2.Text = parts[7].Trim();//Lot 2
            txtWOQ3.Text = parts[10].Trim();
            txtSTDPK.Text = parts[11].Trim();  // Std Pack Qty
            txtCust.Text = parts[12].Trim(); // Customer name
            txtPO.Text = parts[14].Trim(); // Customer PO
            
            //locked the textbox
            txtWO.ReadOnly = true;
            txtWOQ.ReadOnly = true;
            txtWOQ2.ReadOnly = true;
            txtWOQ3.ReadOnly = true;
            txtcpn1.ReadOnly = true;
            txtcpn2.ReadOnly = true;
            txtHEMPN.ReadOnly = true;
            txtPrddte.ReadOnly = true;
            txtLot.ReadOnly = true;
            txtLot2.ReadOnly = true;
            txtLot3.ReadOnly = true;
            txtSTDPK.ReadOnly = true;
            txtCust.ReadOnly = true;
            txtPO.ReadOnly = true;
            

        _currentFG = txtHEMPN.Text;
        _currentCPN = txtcpn1.Text;
        _currentCPN2 = txtcpn2.Text;
        _currentLot = txtLot.Text;
        int.TryParse(txtSTDPK.Text, out _currentStdPack);

            loadLineDETAILS();
            Total_Output_WO();
            Total_Output_WO_Today();
            No_of_boxes();
            woStatus();

            if (lblWoStatus.Text.Trim().Equals("Completed", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("WO already completed. Please scan another BOM.",
                    "WO Completed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                // Clear all textboxes
                txtWO.Clear();
                txtcpn1.Clear();
                txtcpn2.Clear();
                txtWOQ.Clear();
                txtWOQ2.Clear();
                txtWOQ3.Clear();
                txtHEMPN.Clear();
                txtPrddte.Clear();
                txtLot.Clear();
                txtLot2.Clear();
                txtLot3.Clear();
                txtSTDPK.Clear();
                txtQRBOM.Clear();
                txtCust.Clear();
                txtPO.Clear();

                //cmbline.Clear();
                txtleader.Clear();
                txtsleader.Clear();
                txtophem.Clear();
                txtopsub.Clear();
                txtttl.Clear();
                label1.Text = "";   //Total WO Output
                label2.Text = "";        //Total WO Output Today
                lblWoStatus.Text = "";   //WO Status
                label3.Text = "";    //No. of Boxes for Current WO

                cmbBatchID.Text = "";

                // Focus back to QR scan box
                txtQRBOM.Focus();

                return; // stop the process
            }
        }

        private List<KeyValuePair<string, int>> DeductFromTextBoxes(int standardPackQty)
        {
            List<KeyValuePair<string, int>> boxDistribution = new List<KeyValuePair<string, int>>();
            int remainingToAllocate = standardPackQty;

            // Step 1: Drain Rework Lot 2 first
            if (remainingToAllocate > 0 && !string.IsNullOrWhiteSpace(txtLot2.Text))
            {
                int lot2Available = 0;
                int.TryParse(txtWOQ2.Text, out lot2Available);

                if (lot2Available > 0)
                {
                    int taken = Math.Min(lot2Available, remainingToAllocate);
                    txtWOQ2.Text = (lot2Available - taken).ToString();
                    remainingToAllocate -= taken;
                    boxDistribution.Add(new KeyValuePair<string, int>(txtLot2.Text.Trim(), taken));
                }
            }

            // Step 2: Drain Rework Lot 3 second
            if (remainingToAllocate > 0 && !string.IsNullOrWhiteSpace(txtLot3.Text))
            {
                int lot3Available = 0;
                int.TryParse(txtWOQ3.Text, out lot3Available);

                if (lot3Available > 0)
                {
                    int taken = Math.Min(lot3Available, remainingToAllocate);
                    txtWOQ3.Text = (lot3Available - taken).ToString();
                    remainingToAllocate -= taken;
                    boxDistribution.Add(new KeyValuePair<string, int>(txtLot3.Text.Trim(), taken));
                }
            }

            // Step 3: Catch-all — Everything else goes to your Main/Fresh Lot (txtLot)
            if (remainingToAllocate > 0 && !string.IsNullOrWhiteSpace(txtLot.Text))
            {
                boxDistribution.Add(new KeyValuePair<string, int>(txtLot.Text.Trim(), remainingToAllocate));
                remainingToAllocate = 0;
            }

            return boxDistribution;
        }

        private void txtQRBOM_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                // === Block scan if batch already active ===
                if (_batchActive)
                {
                    MessageBox.Show("Batch in progress!",
                                    "Batch Active",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Warning);
                    txtQRBOM.Clear();
                    txtQRBOM.Focus();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return;
                }

                // === Block scan if WO already set ===
                if (!string.IsNullOrWhiteSpace(txtWO.Text))
                {
                    MessageBox.Show("WO in progress!",
                                    "Work Order Active",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Warning);
                    txtQRBOM.Clear();
                    txtQRBOM.Focus();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return;
                }

                // === Process only if valid ===
                ProcessQR(txtQRBOM.Text);

                txtQRBOM.Clear();   // Clear for next scan
                txtQRBOM.Focus();   // always return focus here
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }
        private void btnClear_Click(object sender, EventArgs e)
        {

            if (_batchActive)
            {
                MessageBox.Show("Unable to clear — a batch is currently in progress!",
                                "Batch Active",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);
                return;
            }
            using (var prompt = new PasswordPrompt())
            {
                if (prompt.ShowDialog(this) == DialogResult.OK)
                {
                    if (prompt.EnteredPassword == "admin") // check password
                    {

                        // Clear all textboxes
                        txtWO.Clear();
                        txtcpn1.Clear();
                        txtcpn2.Clear();
                        txtWOQ.Clear();
                        txtHEMPN.Clear();
                        txtPrddte.Clear();
                        txtLot.Clear();
                        txtSTDPK.Clear();
                        txtQRBOM.Clear();
                        txtLot2.Clear();
                        txtCust.Clear();
                        txtPO.Clear();

                        //cmbline.Clear();
                        txtleader.Clear();
                        txtsleader.Clear();
                        txtophem.Clear();
                        txtopsub.Clear();
                        txtttl.Clear();
                        label1.Text = "";   //Total WO Output
                        label2.Text = "";        //Total WO Output Today
                        lblWoStatus.Text = "";   //WO Status
                        label3.Text = "";    //No. of Boxes for Current WO
                        cmbBatchID.Text = "";

                        // Focus back to QR scan box
                        txtQRBOM.Focus();
                    }
                    else
                    {
                        MessageBox.Show("Incorrect password!", "Access Denied",
                                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
         }

      
        
        private void btnCapture_Click_1(object sender, EventArgs e)
        {
            try
            {
                // === Validate required fields ===
                if (string.IsNullOrWhiteSpace(txtWO.Text))
                {
                    MessageBox.Show("Please scan BOM Master.",
                                                   "Missing BOM Master Details",
                                                   MessageBoxButtons.OK,
                                                   MessageBoxIcon.Warning);
                    return;
                }
                if (string.IsNullOrWhiteSpace(txtWO.Text))
                {
                    MessageBox.Show("Work Order (WO) is empty!", "Missing Data",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (string.IsNullOrWhiteSpace(txtHEMPN.Text))
                {
                    MessageBox.Show("Part Number (PN) is empty!", "Missing Data",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (string.IsNullOrWhiteSpace(txtLot.Text))
                {
                    MessageBox.Show("Lot is empty!", "Missing Data",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (string.IsNullOrWhiteSpace(cmbBatchID.Text))
                {
                    MessageBox.Show("Batch ID is empty!", "Missing Data",
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Define save folder (change as needed)
                string saveFolder = @"C:\CameraCaptures\";

                if (!Directory.Exists(saveFolder))
                    Directory.CreateDirectory(saveFolder);

                // Read values from textboxes
                string wo = txtWO.Text.Trim();
                string hempn = txtHEMPN.Text.Trim();
                string lot = txtLot.Text.Trim();
                string lot2 = txtLot2.Text.Trim();
                string batch = cmbBatchID.Text;

                // Use timestamp for uniqueness
                string timestamp = DateTime.Now.ToString("ddMMyy_HHmmss");

                // Build base filename
                string baseName = $"{wo}_{batch}_{hempn}_{lot}_{lot2}_{timestamp}";

                // Replace invalid filename characters
                foreach (char c in Path.GetInvalidFileNameChars())
                {
                    baseName = baseName.Replace(c, '_');
                }

                // Save Camera 1 picture
                if (picCamera1.Image != null)
                {
                    string file1 = Path.Combine(saveFolder, $"C1_{baseName}.jpg");
                    picCamera1.Image.Save(file1, System.Drawing.Imaging.ImageFormat.Jpeg);
                }

                // Save Camera 2 picture
                if (picCamera2.Image != null)
                {
                    string file2 = Path.Combine(saveFolder, $"C2_{baseName}.jpg");
                    picCamera2.Image.Save(file2, System.Drawing.Imaging.ImageFormat.Jpeg);
                }

                MessageBox.Show("Pictures saved successfully!", "Success",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error saving pictures: " + ex.Message,
                                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }  

        private void UpdateOutputLabels()
        {
            string query = @"
                        SELECT 
                            ISNULL(SUM(CASE WHEN WO = @WO THEN BATCH_QTY ELSE 0 END), 0) AS Total_WO_Output,
                            ISNULL(SUM(CASE WHEN PROD_DATE = CAST(GETDATE() AS DATE) THEN BATCH_QTY ELSE 0 END), 0) AS Total_WO_Output_Today,
                            ISNULL(SUM(CASE WHEN WO = @WO AND BATCH_NO = @BatchNo AND PROD_DATE = @ProductionDate AND LINE_NO = @LINE_NO THEN BATCH_QTY ELSE 0 END), 0) AS curr_batch_qty
                        FROM PROD_OUTPUT";

            string connectionString = ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString;

            try
            {
                using (SqlConnection con = new SqlConnection(connectionString))
                using (SqlCommand cmd = new SqlCommand(query, con))
                {
                    cmd.Parameters.AddWithValue("@WO", txtWO.Text);
                    cmd.Parameters.AddWithValue("@BatchNo", cmbBatchID.Text);
                    cmd.Parameters.AddWithValue("@LINE_NO", cmbline.Text);
                    DateTime prodDate = DateTime.ParseExact(txtPrddte.Text, "dd/MM/yyyy", null);

                    cmd.Parameters.Add("@ProductionDate", SqlDbType.Date).Value = prodDate.Date;


                    con.Open();
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            int currBatchQty = Convert.ToInt32(reader["curr_batch_qty"]);
                            lblCount.Text = currBatchQty.ToString();       // Display current batch count
                            label1.Text = Convert.ToInt32(reader["Total_WO_Output"]).ToString();
                            label2.Text = Convert.ToInt32(reader["Total_WO_Output_Today"]).ToString();

                            // Continue counting from retrieved DB quantity
                            _batchBaseline = _count - currBatchQty;
                            _batchActive = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error updating labels: {ex.Message}");
                label1.Text = "0";
                label2.Text = "0";
                lblCount.Text = "0";
                _batchBaseline = _count;      // start fresh
                _batchActive = true;
            }

           // MessageBox.Show($"label counts: {lblCount.Text}");
        }

        // -------------------------------
        //  Real-time counting from sensor
        // -------------------------------

        private bool IsPNNoLabel(string hemPN)
        {
            if (string.IsNullOrWhiteSpace(hemPN))
                return false;

            string connectionString = ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString;

            using (SqlConnection con = new SqlConnection(connectionString))
            {
                con.Open();

                string query = @"SELECT COUNT(1) 
                         FROM PN_NO_LABEL 
                         WHERE HEM_PN = @HEMPN";

                using (SqlCommand cmd = new SqlCommand(query, con))
                {
                    cmd.Parameters.AddWithValue("@HEMPN", hemPN.Trim());

                    int count = Convert.ToInt32(cmd.ExecuteScalar());
                    return count > 0;
                }
            }
        }
        private void OnSensorPulseDetected()
        {
            if (!_batchActive) return;

            int current = Interlocked.Add(ref _count, 0);   // atomic read
            int batchCount = current - _batchBaseline;
            if (batchCount < 0) batchCount = 0;

            // Use Invoke to update the count labels safely on the UI thread
            this.BeginInvoke(new Action(() =>
            {
                lblCount.Text = batchCount.ToString("N0");
                lblLastScan.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }));

            if (int.TryParse(txtSTDPK.Text, out int stdPack) && stdPack > 0)
            {
                if (batchCount > 0 && batchCount % stdPack == 0)
                {
                    System.Media.SystemSounds.Asterisk.Play();

                    if (batchCount != _lastCaptureCount)
                    {
                        _lastCaptureCount = batchCount;
                        _batchActive = false;

                        // Move all UI read/writes into a single Invoke context safely
                        this.Invoke(new Action(() =>
                        {
                            string currentPN = txtHEMPN.Text.Trim();

                            // 🚫 Skip QR check safely on the UI thread
                            if (IsPNNoLabel(currentPN))
                            {
                                _batchActive = true;
                                return;
                            }

                            try
                            {
                                currentSerial++;
                                endSerial = currentSerial;

                                // 1️⃣ Extract values from UI safely into local variables
                                int.TryParse(txtWOQ2.Text, out int q2Available);
                                int.TryParse(txtWOQ3.Text, out int q3Available);
                                int.TryParse(txtWOQ.Text, out int mainAvailable); // Read-only snapshot of master target

                                string lot1 = txtLot.Text.Trim();
                                string lot2 = txtLot2.Text.Trim();
                                string lot3 = txtLot3.Text.Trim();

                                string woText = txtWO.Text;
                                string cpn1Text = txtcpn1.Text;
                                string cpn2Text = txtcpn2.Text;
                                string prodDateText = txtPrddte.Text;
                                string lineText = cmbline.Text;
                                string batchIdText = cmbBatchID.Text;
                                string leaderText = txtleader.Text;
                                string sleaderText = txtsleader.Text;
                                int.TryParse(txtophem.Text, out int opHem);
                                int.TryParse(txtopsub.Text, out int opSub);
                                int.TryParse(txtttl.Text, out int opTtl);
                                string remarksText = txtrmk.Text;

                                int remainingToDeduct = stdPack;
                                List<KeyValuePair<string, int>> lotsUsedForThisBox = new List<KeyValuePair<string, int>>();

                                // Check Rework Lot 2
                                if (remainingToDeduct > 0 && q2Available > 0 && !string.IsNullOrWhiteSpace(lot2))
                                {
                                    int take = Math.Min(remainingToDeduct, q2Available);
                                    lotsUsedForThisBox.Add(new KeyValuePair<string, int>(lot2, take));
                                    q2Available -= take;
                                    remainingToDeduct -= take;
                                    txtWOQ2.Text = q2Available.ToString(); // UI Rework 2 balance decreases
                                }

                                // Check Rework Lot 3
                                if (remainingToDeduct > 0 && q3Available > 0 && !string.IsNullOrWhiteSpace(lot3))
                                {
                                    int take = Math.Min(remainingToDeduct, q3Available);
                                    lotsUsedForThisBox.Add(new KeyValuePair<string, int>(lot3, take));
                                    q3Available -= take;
                                    remainingToDeduct -= take;
                                    txtWOQ3.Text = q3Available.ToString(); // UI Rework 3 balance decreases
                                }

                                // Check Fresh/Main Lot (Lot 1)
                                if (remainingToDeduct > 0)
                                {
                                    // Consume remaining balance from Main Lot 1 without mutating txtWOQ.Text!
                                    lotsUsedForThisBox.Add(new KeyValuePair<string, int>(lot1, remainingToDeduct));
                                    remainingToDeduct = 0;
                                }

                                // 📸 Save snapshot specifically for SATO Print Engine
                                _lastPrintedLots = new List<KeyValuePair<string, int>>(lotsUsedForThisBox);

                                // 2️⃣ Save to database using allocated snapshots
                                try
                                {
                                    string connectionString = ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString;
                                    using (SqlConnection con = new SqlConnection(connectionString))
                                    {
                                        con.Open();

                                        foreach (var lotAllocation in lotsUsedForThisBox)
                                        {
                                            string allocatedLot = lotAllocation.Key;
                                            int allocatedQty = lotAllocation.Value;

                                            string insertQuery = @"
                                INSERT INTO PROD_OUTPUT 
                                (WO, WO_Qty, HEM_PN, CUST_PN_1, CUST_PN_2, PROD_DATE, PROD_TIME, LOT, STDPK, LINE_NO, 
                                 BATCH_NO, BATCH_QTY, Leader, Sub_Leader, No_OperatorHEM, No_OperatorSUB, No_Operator, Remarks)
                                VALUES 
                                (@WO, @WO_Qty, @HEM_PN, @CUST_PN_1, @CUST_PN_2, @PROD_DATE, @PROD_TIME, @LOT, @STDPK, @LINE_NO, 
                                 @BATCH_NO, @BATCH_QTY, @Leader, @Sub_Leader, @No_OperatorHEM, @No_OperatorSUB, @No_Operator, @Remarks)";

                                            using (SqlCommand insertCmd = new SqlCommand(insertQuery, con))
                                            {
                                                insertCmd.Parameters.AddWithValue("@WO", woText);
                                                insertCmd.Parameters.AddWithValue("@WO_Qty", mainAvailable); // Master WO target preserved
                                                insertCmd.Parameters.AddWithValue("@HEM_PN", currentPN);
                                                insertCmd.Parameters.AddWithValue("@CUST_PN_1", cpn1Text);
                                                insertCmd.Parameters.AddWithValue("@CUST_PN_2", cpn2Text);

                                                DateTime prodDate = DateTime.ParseExact(prodDateText, "dd/MM/yyyy", null);
                                                insertCmd.Parameters.AddWithValue("@PROD_DATE", prodDate);
                                                insertCmd.Parameters.AddWithValue("@PROD_TIME", DateTime.Now.TimeOfDay);

                                                insertCmd.Parameters.AddWithValue("@LOT", allocatedLot);//Rename as production Lot Column, Production Lot
                                                insertCmd.Parameters.AddWithValue("@BATCH_QTY", allocatedQty);

                                                insertCmd.Parameters.AddWithValue("@STDPK", stdPack);
                                                insertCmd.Parameters.AddWithValue("@LINE_NO", lineText);
                                                insertCmd.Parameters.AddWithValue("@BATCH_NO", batchIdText);

                                                insertCmd.Parameters.AddWithValue("@Leader", leaderText);
                                                insertCmd.Parameters.AddWithValue("@Sub_Leader", sleaderText);
                                                insertCmd.Parameters.AddWithValue("@No_OperatorHEM", opHem);
                                                insertCmd.Parameters.AddWithValue("@No_OperatorSUB", opSub);
                                                insertCmd.Parameters.AddWithValue("@No_Operator", opTtl);
                                                insertCmd.Parameters.AddWithValue("@Remarks", remarksText);

                                                insertCmd.ExecuteNonQuery();
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    MessageBox.Show("An error occurred while saving the data: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                }

                                Total_Output_WO();
                                Total_Output_WO_Today();
                                No_of_boxes();

                                // Fire SATO Printer
                                printDocument1.Print();
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show("Print Error: " + ex.Message);
                            }
                            finally
                            {
                                // Ensure flag flips back even if printing or database errors out
                                _batchActive = true;
                                StartCountdown();
                            }
                        }));
                    }
                }
            }
        }
        /*private void OnSensorPulseDetected()
        {
            if (!_batchActive) return;

            int current = Interlocked.Add(ref _count, 0);   // atomic read
            int batchCount = current - _batchBaseline;
            if (batchCount < 0) batchCount = 0;

            // Use Invoke to update the count labels safely on the UI thread
            this.BeginInvoke(new Action(() =>
            {
                lblCount.Text = batchCount.ToString("N0"); // Applied "N0" format specifier
                lblLastScan.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            }));

            if (int.TryParse(txtSTDPK.Text, out int stdPack) && stdPack > 0)
            {
                if (batchCount > 0 && batchCount % stdPack == 0)
                {
                    System.Media.SystemSounds.Asterisk.Play();

                    if (batchCount != _lastCaptureCount)
                    {
                        _lastCaptureCount = batchCount;
                        _batchActive = false;

                        // Move all UI read/writes into a single Invoke context safely
                        this.Invoke(new Action(() =>
                        {
                            string currentPN = txtHEMPN.Text.Trim();

                            // 🚫 Skip QR check safely on the UI thread
                            if (IsPNNoLabel(currentPN))
                            {
                                _batchActive = true;
                                return;
                            }

                            try
                            {
                                currentSerial++;
                                endSerial = currentSerial;

                                // 1️⃣ Extract values from UI safely into local variables
                                int.TryParse(txtWOQ2.Text, out int q2Available);
                                int.TryParse(txtWOQ3.Text, out int q3Available);
                                int.TryParse(txtWOQ.Text, out int mainAvailable);

                                string lot1 = txtLot.Text.Trim();
                                string lot2 = txtLot2.Text.Trim();
                                string lot3 = txtLot3.Text.Trim();

                                string woText = txtWO.Text;
                                string cpn1Text = txtcpn1.Text;
                                string cpn2Text = txtcpn2.Text;
                                string prodDateText = txtPrddte.Text;
                                string lineText = cmbline.Text;
                                string batchIdText = cmbBatchID.Text;
                                string leaderText = txtleader.Text;
                                string sleaderText = txtsleader.Text;
                                int.TryParse(txtophem.Text, out int opHem);
                                int.TryParse(txtopsub.Text, out int opSub);
                                int.TryParse(txtttl.Text, out int opTtl);
                                string remarksText = txtrmk.Text;

                                int remainingToDeduct = stdPack;
                                List<KeyValuePair<string, int>> lotsUsedForThisBox = new List<KeyValuePair<string, int>>();

                                // Check Rework Lot 2
                                if (remainingToDeduct > 0 && q2Available > 0 && !string.IsNullOrWhiteSpace(lot2))
                                {
                                    int take = Math.Min(remainingToDeduct, q2Available);
                                    lotsUsedForThisBox.Add(new KeyValuePair<string, int>(lot2, take));
                                    q2Available -= take;
                                    remainingToDeduct -= take;
                                    txtWOQ2.Text = q2Available.ToString();
                                }

                                // Check Rework Lot 3
                                if (remainingToDeduct > 0 && q3Available > 0 && !string.IsNullOrWhiteSpace(lot3))
                                {
                                    int take = Math.Min(remainingToDeduct, q3Available);
                                    lotsUsedForThisBox.Add(new KeyValuePair<string, int>(lot3, take));
                                    q3Available -= take;
                                    remainingToDeduct -= take;
                                    txtWOQ3.Text = q3Available.ToString();
                                }

                                // Check Fresh/Main Lot
                                if (remainingToDeduct > 0 && mainAvailable > 0)
                                {
                                    int take = Math.Min(remainingToDeduct, mainAvailable);
                                    lotsUsedForThisBox.Add(new KeyValuePair<string, int>(lot1, take));
                                    mainAvailable -= take;
                                    remainingToDeduct -= take;
                                    txtWOQ.Text = mainAvailable.ToString();
                                }

                                // Safety Fallback
                                if (remainingToDeduct > 0)
                                {
                                    lotsUsedForThisBox.Add(new KeyValuePair<string, int>(lot1, remainingToDeduct));
                                }

                                // 2️⃣ Save to database using snapshots rather than direct control references
                                try
                                {
                                    string connectionString = ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString;
                                    using (SqlConnection con = new SqlConnection(connectionString))
                                    {
                                        con.Open();

                                        foreach (var lotAllocation in lotsUsedForThisBox)
                                        {
                                            string allocatedLot = lotAllocation.Key;
                                            int allocatedQty = lotAllocation.Value;

                                            string insertQuery = @"
                                        INSERT INTO PROD_OUTPUT 
                                        (WO, WO_Qty, HEM_PN, CUST_PN_1, CUST_PN_2, PROD_DATE, PROD_TIME, LOT, STDPK, LINE_NO, 
                                         BATCH_NO, BATCH_QTY, Leader, Sub_Leader, No_OperatorHEM, No_OperatorSUB, No_Operator, Remarks)
                                        VALUES 
                                        (@WO, @WO_Qty, @HEM_PN, @CUST_PN_1, @CUST_PN_2, @PROD_DATE, @PROD_TIME, @LOT, @STDPK, @LINE_NO, 
                                         @BATCH_NO, @BATCH_QTY, @Leader, @Sub_Leader, @No_OperatorHEM, @No_OperatorSUB, @No_Operator, @Remarks)";

                                            using (SqlCommand insertCmd = new SqlCommand(insertQuery, con))
                                            {
                                                insertCmd.Parameters.AddWithValue("@WO", woText);
                                                insertCmd.Parameters.AddWithValue("@WO_Qty", mainAvailable); // Uses snapshot calculated data
                                                insertCmd.Parameters.AddWithValue("@HEM_PN", currentPN);
                                                insertCmd.Parameters.AddWithValue("@CUST_PN_1", cpn1Text);
                                                insertCmd.Parameters.AddWithValue("@CUST_PN_2", cpn2Text);

                                                DateTime prodDate = DateTime.ParseExact(prodDateText, "dd/MM/yyyy", null);
                                                insertCmd.Parameters.AddWithValue("@PROD_DATE", prodDate);
                                                insertCmd.Parameters.AddWithValue("@PROD_TIME", DateTime.Now.TimeOfDay);

                                                insertCmd.Parameters.AddWithValue("@LOT", allocatedLot);
                                                insertCmd.Parameters.AddWithValue("@BATCH_QTY", allocatedQty);

                                                insertCmd.Parameters.AddWithValue("@STDPK", stdPack);
                                                insertCmd.Parameters.AddWithValue("@LINE_NO", lineText);
                                                insertCmd.Parameters.AddWithValue("@BATCH_NO", batchIdText);

                                                insertCmd.Parameters.AddWithValue("@Leader", leaderText);
                                                insertCmd.Parameters.AddWithValue("@Sub_Leader", sleaderText);
                                                insertCmd.Parameters.AddWithValue("@No_OperatorHEM", opHem);
                                                insertCmd.Parameters.AddWithValue("@No_OperatorSUB", opSub);
                                                insertCmd.Parameters.AddWithValue("@No_Operator", opTtl);
                                                insertCmd.Parameters.AddWithValue("@Remarks", remarksText);

                                                insertCmd.ExecuteNonQuery();
                                            }
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    MessageBox.Show("An error occurred while saving the data: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                }

                                Total_Output_WO();
                                Total_Output_WO_Today();
                                No_of_boxes();

                                printDocument1.Print();
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show("Print Error: " + ex.Message);
                            }
                            finally
                            {
                                // Ensure flag flips back even if printing or database errors out
                                _batchActive = true;
                                StartCountdown();
                            }
                            }));
                    }
                }
            }
        }*/

        private void StartNewBatch()
        {
            _batchBaseline = _count;  // start counting from current global count
            _lastCaptureCount = 0;
            _batchActive = true;
            lblCount.Text = "0";       // reset UI to zero for new batch
        }

        private void btnComplete_Click(object sender, EventArgs e)
        {
            // === Password prompt ===
            using (var prompt = new PasswordPrompt())
            {
                if (prompt.ShowDialog(this) == DialogResult.OK)
                {
                    if (prompt.EnteredPassword != "admin")
                    {
                        System.Media.SystemSounds.Hand.Play();
                        MessageBox.Show("Incorrect password!", "Access Denied",
                                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
                else
                {
                    return;
                }
            }

            // Check if WO is selected
            if (string.IsNullOrWhiteSpace(txtWO.Text))
            {
                MessageBox.Show("No Work Order selected!", "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // If no batch is active, just return
            if (_batchActive)
            {
                MessageBox.Show("Batch Still Active!",
                                "Please end the batch",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                return;
            }


            // Check current status from database
            string currentStatus = GetWOStatus(txtWO.Text);

            if (currentStatus != "In Progress" && currentStatus != "Paused")
            {
                MessageBox.Show($"Cannot force complete the work order. Only 'In Progress' or 'Paused' WOs can be complete.\nCurrent status: '{currentStatus}'",
                                "Cannot Complete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }


            // Confirm pause
            DialogResult confirm = MessageBox.Show($"Complete Work Order {txtWO.Text}?",
                                                  "Confirm Complete",
                                                  MessageBoxButtons.YesNo,
                                                  MessageBoxIcon.Question);

            if (confirm == DialogResult.Yes)
            {
                CompleteWorkOrder(txtWO.Text);
            }

        }
        private void CompleteWorkOrder(string workOrder)
        {
            string connectionString = ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString;

            try
            {
                using (SqlConnection con = new SqlConnection(connectionString))
                {
                    con.Open();

                    // Update all active batches for this WO to set Pause_Indicator = 1
                    string updateQuery = @"
                    UPDATE PROD_OUTPUT 
                    SET Indicator = 2 
                    WHERE WO = @WO"; 
                    //AND (Indicator = 1 OR Indicator = 2 OR Indicator IS NULL)";  //AND PROD_DATE = @Today

                    using (SqlCommand cmd = new SqlCommand(updateQuery, con))
                    {
                        cmd.Parameters.AddWithValue("@WO", workOrder);
                        //cmd.Parameters.AddWithValue("@Today", DateTime.Today);

                        int rowsAffected = cmd.ExecuteNonQuery();

                        if (rowsAffected > 0)
                        {
                            //MessageBox.Show($"Work Order {workOrder} paused successfully.\n{rowsAffected} batch(es) marked as paused.",
                            //                "Paused", MessageBoxButtons.OK, MessageBoxIcon.Information);

                            // Also stop any active counting in the UI
                            //_batchActive = false;
                            //lblstatup.Text = "Paused";
                            //lblstatup.ForeColor = Color.Orange;

                            // Refresh status display
                            woStatus();
                        }
                        else
                        {
                            MessageBox.Show($"No active batches found for Work Order {workOrder}.",
                                            "No Action", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error pausing work order: {ex.Message}", "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnPauseWorkOrder_Click(object sender, EventArgs e)
        {
            // === Password prompt ===
            using (var prompt = new PasswordPrompt())
            {
                if (prompt.ShowDialog(this) == DialogResult.OK)
                {
                    if (prompt.EnteredPassword != "admin")
                    {
                        System.Media.SystemSounds.Hand.Play();
                        MessageBox.Show("Incorrect password!", "Access Denied",
                                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
                else
                {
                    return;
                }
            }

            // Check if WO is selected
            if (string.IsNullOrWhiteSpace(txtWO.Text))
            {
                MessageBox.Show("No Work Order selected!", "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // If no batch is active, just return
            if (_batchActive)
            {
                MessageBox.Show("Batch Still Active!",
                                "Please end the batch",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                return;
            }


            // Check current status from database
            string currentStatus = GetWOStatus(txtWO.Text);

            if (currentStatus != "In Progress")
            {
                MessageBox.Show($"Cannot pause. Current status is '{currentStatus}'. Only 'In Progress' WOs can be paused.",
                                "Cannot Pause", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Confirm pause
            DialogResult confirm = MessageBox.Show($"Pause Work Order {txtWO.Text}?",
                                                  "Confirm Pause",
                                                  MessageBoxButtons.YesNo,
                                                  MessageBoxIcon.Question);

            if (confirm == DialogResult.Yes)
            {
                PauseWorkOrder(txtWO.Text);
            }
        }

        private string GetWOStatus(string workOrder)
        {
            string status = "Unknown";
            string connectionString = ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString;

            try
            {
                using (SqlConnection con = new SqlConnection(connectionString))
                {
                    con.Open();

                    // Get status from the view
                    string query = @"
                SELECT TOP 1 Batch_Output_Status 
                FROM BATCH_OUTPUT_UNIFIED_8 
                WHERE Work_Order = @WorkOrder 
                ORDER BY Date_Production DESC";

                    using (SqlCommand cmd = new SqlCommand(query, con))
                    {
                        cmd.Parameters.AddWithValue("@WorkOrder", workOrder);
                        object result = cmd.ExecuteScalar();
                        if (result != null)
                        {
                            status = result.ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error checking status: {ex.Message}", "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return status;
        }

        private void PauseWorkOrder(string workOrder)
        {
            string connectionString = ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString;

            try
            {
                using (SqlConnection con = new SqlConnection(connectionString))
                {
                    con.Open();

                    // Update all active batches for this WO to set Pause_Indicator = 1
                    string updateQuery = @"
                UPDATE PROD_OUTPUT 
                SET Indicator = 1 
                WHERE WO = @WO 
                AND (Indicator IS NULL OR Indicator = 0)";  //AND PROD_DATE = @Today

                    using (SqlCommand cmd = new SqlCommand(updateQuery, con))
                    {
                        cmd.Parameters.AddWithValue("@WO", workOrder);
                        //cmd.Parameters.AddWithValue("@Today", DateTime.Today);

                        int rowsAffected = cmd.ExecuteNonQuery();

                        if (rowsAffected > 0)
                        {
                            MessageBox.Show($"Work Order {workOrder} paused successfully.",
                                            "Paused", MessageBoxButtons.OK, MessageBoxIcon.Information);

                            //MessageBox.Show($"Work Order {workOrder} paused successfully.\n{rowsAffected} batch(es) marked as paused.",
                            //               "Paused", MessageBoxButtons.OK, MessageBoxIcon.Information);

                            // Also stop any active counting in the UI
                            _batchActive = false;
                            //lblstatup.Text = "Paused";
                            //lblstatup.ForeColor = Color.Orange;

                            // Refresh status display
                            woStatus();
                        }
                        else
                        {
                            MessageBox.Show($"No active batches found for Work Order {workOrder}.",
                                            "No Action", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error pausing work order: {ex.Message}", "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void resumeWorkOrder(string workOrder)
        {
            string connectionString = ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString;

            try
            {
                using (SqlConnection con = new SqlConnection(connectionString))
                {
                    con.Open();

                    // Update all active batches for this WO to set Pause_Indicator = 1
                    string updateQuery = @"
                UPDATE PROD_OUTPUT 
                SET Indicator = 0 
                WHERE WO = @WO 
                AND Indicator = 1";  //AND PROD_DATE = @Today

                    using (SqlCommand cmd = new SqlCommand(updateQuery, con))
                    {
                        cmd.Parameters.AddWithValue("@WO", workOrder);
                        //cmd.Parameters.AddWithValue("@Today", DateTime.Today);

                        int rowsAffected = cmd.ExecuteNonQuery();

                        if (rowsAffected > 0)
                        {
                            //MessageBox.Show($"Work Order {workOrder} paused successfully.\n{rowsAffected} batch(es) marked as paused.",
                            //                "Paused", MessageBoxButtons.OK, MessageBoxIcon.Information);

                            // Also stop any active counting in the UI
                            //_batchActive = false;
                            //lblstatup.Text = "Paused";
                            //lblstatup.ForeColor = Color.Orange;

                            // Refresh status display
                            woStatus();
                        }
                        else
                        {
                            MessageBox.Show($"No active batches found for Work Order {workOrder}.",
                                            "No Action", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error pausing work order: {ex.Message}", "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
 
        private void Checkcustmark()
        {
            try
            {
                using (SqlConnection con = new SqlConnection(ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString))
                {
                    con.Open();

                    string query = @"
                SELECT Customer_Special_Label_Mark
                FROM ITEMMASTER
                WHERE Product_ID = @hempn";

                    using (SqlCommand cmd = new SqlCommand(query, con))
                    {
                        cmd.Parameters.AddWithValue("@hempn", txtHEMPN.Text);

                        object result = cmd.ExecuteScalar();

                        if (result != null && result != DBNull.Value)
                        {
                            cMark = result.ToString().Trim();  // store the value (LF or PL)
                        }
                        else
                        {
                            cMark = "";  // no cust mark
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error checking customer mark: {ex.Message}",
                    "Database Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void CheckRFItem()
        {
            try
            {
                using (SqlConnection con = new SqlConnection(ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString))
                {
                    con.Open();

                    string query = @"
                SELECT RoHS_Free_Mark
                FROM ITEMMASTER
                WHERE Product_ID = @hempn";

                    using (SqlCommand cmd = new SqlCommand(query, con))
                    {
                        cmd.Parameters.AddWithValue("@hempn", txtHEMPN.Text);

                        object result = cmd.ExecuteScalar();

                        if (result != null && result != DBNull.Value)
                        {
                            rfMark = result.ToString().Trim();  // store the value (RF or RF2)
                        }
                        else
                        {
                            rfMark = "";  // no RF mark
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error checking RF item: {ex.Message}",
                    "Database Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private void printDocument1_PrintPage(object sender, System.Drawing.Printing.PrintPageEventArgs e)
        {
            Graphics g = e.Graphics;

            using (Font bigFont = new Font("Book Antiqua", 20, FontStyle.Bold))
            using (Font smallFont = new Font("Book Antiqua", 16, FontStyle.Bold))
            using (Font dtefont = new Font("Book Antiqua", 12, FontStyle.Bold))
            using (Font dte2font = new Font("Book Antiqua", 8, FontStyle.Bold))
            {
                string batchUsed;
                string serialFormatted;
                string labelSerial;

                // Decide which batch and serial to use
                if (isReprint)
                {
                    batchUsed = reprintBatch;
                    serialFormatted = reprintSerial.ToString("D2");
                }
                else
                {
                    batchUsed = cmbBatchID.Text;
                    serialFormatted = currentSerial.ToString("D2");
                }

                // Decide what appears on the label
                if (isQCCopy)
                {
                    labelSerial = cmbline.Text + ":" + batchUsed + ":" + "BOM";
                }
                else
                {
                    labelSerial = cmbline.Text + ":" + batchUsed + ":" + serialFormatted;
                }

                bool hasSecondCPN = !string.IsNullOrWhiteSpace(txtcpn2.Text);
                string dayOnly = txtPrddte.Text.Substring(0, 2);
                string monthOnly = txtPrddte.Text.Substring(3, 2);
                string yearOnly = txtPrddte.Text.Substring(8, 2);
                string printedDate = DateTime.Now.ToString("dd/MM/yyyy");

                CheckRFItem();
                Checkcustmark();

                // 1️⃣ DYNAMIC LOT PREPARATION FOR QR CODE
                string qrLotString = "";

                if (_lastPrintedLots != null && _lastPrintedLots.Count > 0)
                {
                    List<string> qrSegments = new List<string>();
                    foreach (var lotItem in _lastPrintedLots)
                    {
                        qrSegments.Add(lotItem.Key);
                    }
                    qrLotString = string.Join(";", qrSegments);
                    if (qrSegments.Count == 1) qrLotString += ";;";
                    else if (qrSegments.Count == 2) qrLotString += ";";
                }
                else
                {
                    // Fallback for manual trigger / reprint
                    qrLotString = txtLot.Text.Trim() + ";" + txtLot2.Text.Trim() + ";";
                }

                // Single declaration of qrContent
                string qrContent =
                    txtHEMPN.Text + ";" +
                    "WH2FIN" + ";" +
                    txtLot.Text + ";" +
                    txtSTDPK.Text + ";" +
                    cmbline.Text + ":" + batchUsed + ":" + serialFormatted;
                    /*txtcpn1.Text + ";" +
                    txtcpn2.Text + ";" +
                    txtHEMPN.Text + ";" +
                    qrLotString +
                    txtPO.Text + ";" +
                    txtCust.Text + ";" +
                    txtSTDPK.Text + ";" +
                    cmbline.Text + ":" + batchUsed + ":" + serialFormatted + ";";*/

                // -------- Customer Part Number --------
                if (hasSecondCPN)
                {
                    g.DrawString(txtcpn1.Text, smallFont, Brushes.Black, 20, 36);
                    g.DrawString(txtcpn2.Text, smallFont, Brushes.Black, 20, 58);
                }
                else
                {
                    g.DrawString(txtcpn1.Text, bigFont, Brushes.Black, 20, 40);
                }

                // -------- HEM PN --------
                g.DrawString(txtHEMPN.Text, bigFont, Brushes.Black, 20, 93);

                // -------- LOT (DYNAMIC ADJUSTED LAYOUT) --------
                float lotX1 = 20;   // Left column X position
                float lotX2 = 118;  // Right column X position (beside Lot 2)
                float lotY1 = 153;  // Line 1 Y position
                float lotY2 = 173;  // Line 2 Y position

                if (_lastPrintedLots != null && _lastPrintedLots.Count > 0)
                {
                    if (_lastPrintedLots.Count == 1)
                    {
                        // CASE 1: Single Lot -> Big font, display Lot Name ONLY
                        g.DrawString(_lastPrintedLots[0].Key, bigFont, Brushes.Black, lotX1, lotY1);
                    }
                    else if (_lastPrintedLots.Count == 2)
                    {
                        // CASE 2: 2 Lots -> Small font stacked vertically
                        g.DrawString($"{_lastPrintedLots[0].Key} - {_lastPrintedLots[0].Value}", smallFont, Brushes.Black, lotX1, lotY1);
                        g.DrawString($"{_lastPrintedLots[1].Key} - {_lastPrintedLots[1].Value}", smallFont, Brushes.Black, lotX1, lotY2);
                    }
                    else if (_lastPrintedLots.Count >= 3)
                    {
                        // CASE 3: 3 Lots -> Small font, Fresh Lot (Lot 1) placed beside Lot 2
                        string lot2Text = $"{_lastPrintedLots[0].Key} - {_lastPrintedLots[0].Value}";
                        string lot3Text = $"{_lastPrintedLots[1].Key} - {_lastPrintedLots[1].Value}";
                        string lot1FreshText = $"{_lastPrintedLots[2].Key} - {_lastPrintedLots[2].Value}";

                        // Draw Lot 2 on left of Line 1
                        g.DrawString(lot2Text, dtefont, Brushes.Black, lotX1, lotY1);

                        // Draw Lot 1 (Fresh Lot) beside Lot 2 on right of Line 1
                        g.DrawString(lot1FreshText, dtefont, Brushes.Black, lotX2, lotY1);

                        // Draw Lot 3 on Line 2
                        g.DrawString(lot3Text, dtefont, Brushes.Black, lotX1, lotY2);
                    }
                }
                else
                {
                    // Fallback for direct manual printing
                    if (!string.IsNullOrWhiteSpace(txtLot2.Text))
                    {
                        g.DrawString(txtLot.Text, smallFont, Brushes.Black, lotX1, lotY1);
                        g.DrawString(txtLot2.Text, smallFont, Brushes.Black, lotX1, lotY2);
                    }
                    else
                    {
                        g.DrawString(txtLot.Text, bigFont, Brushes.Black, lotX1, lotY1);
                    }
                }

                // -------- Remaining Fields --------
                g.DrawString(txtPO.Text, bigFont, Brushes.Black, 20, 215);
                g.DrawString(txtCust.Text, bigFont, Brushes.Black, 20, 275);
                g.DrawString(txtSTDPK.Text, bigFont, Brushes.Black, 290, 153);
                g.DrawString(labelSerial, smallFont, Brushes.Black, 290, 215);
                g.DrawString(dayOnly, dtefont, Brushes.Black, 368, 2);
                g.DrawString(monthOnly, dtefont, Brushes.Black, 323, 2);
                g.DrawString(yearOnly, dtefont, Brushes.Black, 273, 2);
                g.DrawString(printedDate, dte2font, Brushes.Black, 346, 324);

                // Generate QR Code
                QRCodeData qrCodeData = qrGenerator.CreateQrCode(qrContent, QRCodeGenerator.ECCLevel.Q);
                QRCode qrCode = new QRCode(qrCodeData);

                using (Bitmap qrImage = qrCode.GetGraphic(6))
                {
                    int qrX = 209;      // X position
                    int qrY = 140;      // Y position
                    int qrWidth = 63;   // desired width in pixels
                    int qrHeight = 63;  // desired height in pixels

                    g.DrawImage(qrImage, qrX, qrY, qrWidth, qrHeight);
                }

                // Generate RF logo
                if (!string.IsNullOrEmpty(rfMark))
                {
                    using (Font rfFont = new Font("Book Antiqua", 20, FontStyle.Bold))
                    using (Pen rfPen = new Pen(Color.Black, 2))
                    {
                        float rfX = 325;
                        float rfY = 35;

                        SizeF textSize = e.Graphics.MeasureString(rfMark, rfFont);
                        float padding = 6;

                        e.Graphics.DrawRectangle(
                            rfPen,
                            rfX - padding,
                            rfY - padding,
                            textSize.Width + padding * 2,
                            textSize.Height + padding * 2);

                        e.Graphics.DrawString(rfMark, rfFont, Brushes.Black, rfX, rfY);
                    }
                }

                // Generate customer special logo
                if (!string.IsNullOrEmpty(cMark))
                {
                    using (Font cFont = new Font("Book Antiqua", 20, FontStyle.Bold))
                    using (Pen cPen = new Pen(Color.Black, 2))
                    {
                        float cX = 325;  // left/right
                        float cY = 92;   // height

                        SizeF textSize = e.Graphics.MeasureString(cMark, cFont);
                        float padding = 6;

                        e.Graphics.DrawRectangle(
                            cPen,
                            cX - padding,
                            cY - padding,
                            textSize.Width + padding * 2,
                            textSize.Height + padding * 2);

                        e.Graphics.DrawString(cMark, cFont, Brushes.Black, cX, cY);
                    }
                }
            }

            // Only print ONE page per trigger
            e.HasMorePages = false;
            isQCCopy = false;
            isReprint = false;
        }
        /*private void printDocument1_PrintPage(object sender, System.Drawing.Printing.PrintPageEventArgs e)
        {
            Graphics g = e.Graphics;

            using (Font bigFont = new Font("Book Antiqua", 20, FontStyle.Bold))
            using (Font smallFont = new Font("Book Antiqua", 16, FontStyle.Bold))
            using (Font dtefont = new Font("Book Antiqua", 12, FontStyle.Bold))
            using (Font dte2font = new Font("Book Antiqua", 8, FontStyle.Bold))
            {
                string batchUsed;
                string serialFormatted;
                string labelSerial;

                // Decide which batch and serial to use
                if (isReprint)
                {
                    batchUsed = reprintBatch;
                    serialFormatted = reprintSerial.ToString("D2");
                }
                else
                {
                    batchUsed = cmbBatchID.Text;
                    serialFormatted = currentSerial.ToString("D2");
                }

                // Decide what appears on the label
                if (isQCCopy)
                {
                    labelSerial = cmbline.Text + ":" + batchUsed + ":" + "BOM";
                }
                else
                {
                    labelSerial = cmbline.Text + ":" + batchUsed + ":" + serialFormatted;
                }

                bool hasSecondCPN = !string.IsNullOrWhiteSpace(txtcpn2.Text);
                string dayOnly = txtPrddte.Text.Substring(0, 2);
                string monthOnly = txtPrddte.Text.Substring(3, 2);
                string yearOnly = txtPrddte.Text.Substring(8, 2);
                string printedDate = DateTime.Now.ToString("dd/MM/yyyy");

                CheckRFItem();
                Checkcustmark();

                // 1️⃣ DYNAMIC LOT FORMATTING LOGIC
                List<string> lotDisplayLines = new List<string>();
                string qrLotString = "";

                if (_lastPrintedLots != null && _lastPrintedLots.Count > 0)
                {
                    if (_lastPrintedLots.Count == 1)
                    {
                        // Case 1: Single Lot -> Display Lot Name ONLY
                        lotDisplayLines.Add(_lastPrintedLots[0].Key);
                        qrLotString = _lastPrintedLots[0].Key + ";;";
                    }
                    else
                    {
                        // Case 2 & 3: Multiple Lots -> Display "Lot - Qty" for each line
                        List<string> qrSegments = new List<string>();
                        foreach (var lotItem in _lastPrintedLots)
                        {
                            lotDisplayLines.Add($"{lotItem.Key} - {lotItem.Value}");
                            qrSegments.Add(lotItem.Key);
                        }

                        qrLotString = string.Join(";", qrSegments);
                        if (qrSegments.Count == 2) qrLotString += ";"; // Pad semicolon if 2 lots used
                    }
                }
                else
                {
                    // Fallback for manual trigger / reprint
                    lotDisplayLines.Add(txtLot.Text.Trim());
                    qrLotString = txtLot.Text.Trim() + ";" + txtLot2.Text.Trim() + ";";
                }

                // Build QR content string matching your standard sequence
                string qrContent =
                    txtcpn1.Text + ";" +
                    txtcpn2.Text + ";" +
                    txtHEMPN.Text + ";" +
                    qrLotString +
                    txtPO.Text + ";" +
                    txtCust.Text + ";" +
                    txtSTDPK.Text + ";" +
                    cmbline.Text + ":" + batchUsed + ":" + serialFormatted + ";";

                // -------- Customer Part Number --------
                if (hasSecondCPN)
                {
                    g.DrawString(txtcpn1.Text, smallFont, Brushes.Black, 20, 36);
                    g.DrawString(txtcpn2.Text, smallFont, Brushes.Black, 20, 58);
                }
                else
                {
                    g.DrawString(txtcpn1.Text, bigFont, Brushes.Black, 20, 40);
                }

                // -------- HEM PN --------
                g.DrawString(txtHEMPN.Text, bigFont, Brushes.Black, 20, 93);

                // -------- LOT (DYNAMIC OUTPUT) --------
                float startLotY = 153;
                float lotLineSpacing = 20; // Vertical gap between split lot lines

                if (lotDisplayLines.Count == 1)
                {
                    // Single Lot -> Uses bigFont at exact original Y coordinate (153)
                    g.DrawString(lotDisplayLines[0], bigFont, Brushes.Black, 20, startLotY);
                }
                else
                {
                    // Multiple Lots -> Uses smallFont starting at Y = 153, stepping down per lot
                    for (int i = 0; i < lotDisplayLines.Count; i++)
                    {
                        float currentY = startLotY + (i * lotLineSpacing);
                        g.DrawString(lotDisplayLines[i], smallFont, Brushes.Black, 20, currentY);
                    }
                }

                // -------- Remaining Fields --------
                g.DrawString(txtPO.Text, bigFont, Brushes.Black, 20, 215);
                g.DrawString(txtCust.Text, bigFont, Brushes.Black, 20, 275);
                g.DrawString(txtSTDPK.Text, bigFont, Brushes.Black, 290, 153);
                g.DrawString(labelSerial, smallFont, Brushes.Black, 290, 215);
                g.DrawString(dayOnly, dtefont, Brushes.Black, 368, 2);
                g.DrawString(monthOnly, dtefont, Brushes.Black, 323, 2);
                g.DrawString(yearOnly, dtefont, Brushes.Black, 273, 2);
                g.DrawString(printedDate, dte2font, Brushes.Black, 346, 324);

                // Generate QR Code
                QRCodeData qrCodeData = qrGenerator.CreateQrCode(qrContent, QRCodeGenerator.ECCLevel.Q);
                QRCode qrCode = new QRCode(qrCodeData);

                using (Bitmap qrImage = qrCode.GetGraphic(6))
                {
                    int qrX = 209;      // X position
                    int qrY = 140;      // Y position
                    int qrWidth = 63;   // desired width in pixels
                    int qrHeight = 63;  // desired height in pixels

                    g.DrawImage(qrImage, qrX, qrY, qrWidth, qrHeight);
                }

                // Generate RF logo
                if (!string.IsNullOrEmpty(rfMark))
                {
                    using (Font rfFont = new Font("Book Antiqua", 20, FontStyle.Bold))
                    using (Pen rfPen = new Pen(Color.Black, 2))
                    {
                        float rfX = 325;
                        float rfY = 35;

                        SizeF textSize = e.Graphics.MeasureString(rfMark, rfFont);
                        float padding = 6;

                        e.Graphics.DrawRectangle(
                            rfPen,
                            rfX - padding,
                            rfY - padding,
                            textSize.Width + padding * 2,
                            textSize.Height + padding * 2);

                        e.Graphics.DrawString(rfMark, rfFont, Brushes.Black, rfX, rfY);
                    }
                }

                // Generate customer special logo
                if (!string.IsNullOrEmpty(cMark))
                {
                    using (Font cFont = new Font("Book Antiqua", 20, FontStyle.Bold))
                    using (Pen cPen = new Pen(Color.Black, 2))
                    {
                        float cX = 325;  // left/right
                        float cY = 92;   // height

                        SizeF textSize = e.Graphics.MeasureString(cMark, cFont);
                        float padding = 6;

                        e.Graphics.DrawRectangle(
                            cPen,
                            cX - padding,
                            cY - padding,
                            textSize.Width + padding * 2,
                            textSize.Height + padding * 2);

                        e.Graphics.DrawString(cMark, cFont, Brushes.Black, cX, cY);
                    }
                }
            }

            // Only print ONE page per trigger
            e.HasMorePages = false;
            isQCCopy = false;
            isReprint = false;
        }*/



        private void btnReprint_Click(object sender, EventArgs e)
        {
            // Ensure BOM already scanned, this is for BOM label copy DO NOT CONFUSE
            if (string.IsNullOrWhiteSpace(txtHEMPN.Text))
            {
                MessageBox.Show("Please scan BOM before printing QC label.",
                    "No BOM",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            isQCCopy = true;

            printDocument1.Print();
        }

        private void btnRep_Click(object sender, EventArgs e)
        {
            //  Password prompt 
            using (var prompt = new PasswordPrompt())
            {
                if (prompt.ShowDialog(this) == DialogResult.OK)
                {
                    if (prompt.EnteredPassword != "rcu123")
                    {
                        System.Media.SystemSounds.Hand.Play();
                        MessageBox.Show("Incorrect password!", "Access Denied",
                                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
                else
                {
                    return;
                }
            }
            using (var inputForm = new Reprint())
            {
                if (inputForm.ShowDialog() == DialogResult.OK)
                {
                    isReprint = true;
                    reprintBatch = inputForm.BatchNumber;
                    reprintSerial = inputForm.SerialNumber;

                    printDocument1.Print();
                }
            }
        }
        private void ExportToFile()
        {
            // Ensure we have a valid batch ID to query before proceeding
            string batchId = cmbBatchID.Text.Trim();
            if (string.IsNullOrWhiteSpace(batchId)) return;

            string dayOnly = txtPrddte.Text.Substring(0, 2);
            string monthOnly = txtPrddte.Text.Substring(3, 2);
            string yearOnly = txtPrddte.Text.Substring(8, 2);
            string dateCombine = dayOnly + monthOnly + yearOnly;

            try
            {
                // Path to your dual-homed NAS folder
                //string folderPath = @"\\192.168.0.5\exchange\";
                string folderPath = @"\\172.16.64.108\ftp haha\";
                if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);

                // DYNAMIC FILENAME: Clean up the combobox text to prevent invalid file characters
                string lineId = string.IsNullOrWhiteSpace(cmbline.Text) ? "UnknownLine" : cmbline.Text.Trim();
                foreach (char c in Path.GetInvalidFileNameChars())
                {
                    lineId = lineId.Replace(c, '_');
                }

                string fileName = $"RCU_{lineId}_Output";
                string fullPath = Path.Combine(folderPath, fileName);

                List<string> fileLines = new List<string>();

                // 1️⃣ Fetch everything saved in the database, SUMMING up quantities by LOT
                string connectionString = ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString;
                using (SqlConnection con = new SqlConnection(connectionString))
                {
                    con.Open();

                    // 🎯 NEW: Grouping by LOT and summing the quantities for this specific Batch/WO/Line
                    string query = @"SELECT LOT, SUM(BATCH_QTY) AS TOTAL_QTY 
                             FROM PROD_OUTPUT 
                             WHERE WO = @WO AND BATCH_NO = @BATCH_NO AND LINE_NO = @LINE_NO 
                             GROUP BY LOT";

                    using (SqlCommand cmd = new SqlCommand(query, con))
                    {
                        cmd.Parameters.AddWithValue("@WO", txtWO.Text.Trim());
                        cmd.Parameters.AddWithValue("@BATCH_NO", batchId);
                        cmd.Parameters.AddWithValue("@LINE_NO", cmbline.Text.Trim());

                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string dbLot = reader["LOT"].ToString().Trim();
                                int combinedQty = Convert.ToInt32(reader["TOTAL_QTY"]);

                                // Skip empty quantities safely
                                if (combinedQty <= 0) continue;

                                // 2️⃣ Match Master line (.M.) with the SUMMED quantity
                                string line1 = $".M.;.HEM01.;.{txtWO.Text.Trim()}.;.{txtHEMPN.Text.Trim()}.;{combinedQty};.EA.;.{dateCombine}.;..;..";

                                // 3️⃣ Match Stock line (.S.) directly below it
                                string line2 = $".S.;.WH2FIN.;.{dbLot}.;..;..;..;";

                                fileLines.Add(line1);
                                fileLines.Add(line2);
                            }
                        }
                    }
                }

                // 4️⃣ Write out the matched dataset array safely
                if (fileLines.Count > 0)
                {
                    File.WriteAllLines(fullPath, fileLines.ToArray());
                }
            }
            catch (Exception ex)
            {
                // Silent catch or log to a local file so production isn't interrupted
                File.AppendAllText("error_log.txt", $"{DateTime.Now}: {ex.Message}{Environment.NewLine}");
            }
        }
        /*private void ExportToFile()
        {
            string dayOnly = txtPrddte.Text.Substring(0, 2);
            string monthOnly = txtPrddte.Text.Substring(3, 2);
            string yearOnly = txtPrddte.Text.Substring(8, 2);
            string dateCombine = dayOnly + monthOnly + yearOnly;
            try
            {
                // Path to your dual-homed NAS folder
                string folderPath = @"\\192.168.0.5\exchange\";

                if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);

                // Filename format: WO_Batch_Timestamp
                string fileName = "RCU_B1_Output";
                string fullPath = Path.Combine(folderPath, fileName);

                // Formatting the date to DDMMYY as seen in your example (080526)
                string formattedDate = DateTime.Parse(txtPrddte.Text).ToString("ddMMyy");

                // Building the lines based on your example
                // .M.;.SITE.;.WO.;.PART.;QTY;.UOM.;.DATE.;..;..
                string line1 = $".M.;.HEM01.;.{txtWO.Text}.;.{txtHEMPN.Text}.;{lblCount.Text};.EA.;.{dateCombine}.;..;..";

                // .S.;.WH.;.DATE.;..;..;..;
                string line2 = $".S.;.WH2FIN.;.{txtLot.Text}.;..;..;..;";

                // Write both lines to the file
                File.WriteAllLines(fullPath, new string[] { line1, line2 });
            }
            catch (Exception ex)
            {
                // Silent catch or log to a local file so production isn't interrupted
                File.AppendAllText("error_log.txt", $"{DateTime.Now}: {ex.Message}{Environment.NewLine}");
            }
        }*/
    }
}
