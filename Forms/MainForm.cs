using AForge.Video;
using AForge.Video.DirectShow;
using Automation.BDaq;
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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Tab;
using QRCoder;
//////////

namespace RCU_FG_Output_Counter
{
    public partial class MainForm : Form
    {
        private int _count = 0;
        private int _batchBaseline = 0;
        private bool _batchActive = false;
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

        // Capture countdown timer (UI timer)
        private System.Windows.Forms.Timer _captureTimer;
        private int _countdownValue = 0;
        private bool _countdownActive = false;

        private string _currentFG;
        private string _currentCPN;
        private string _currentCPN2;
        private string _currentLot;
        private int _currentStdPack;

        private int currentSerial = 0;
        private int endSerial = 0;
        private QRCodeGenerator qrGenerator = new QRCodeGenerator();

        public MainForm()
        {
            InitializeComponent();
            InitAdvantechDaq(); 
            _captureTimer = new System.Windows.Forms.Timer();
            _captureTimer.Interval = 1000;              // 1 second
            _captureTimer.Tick += CaptureTimer_Tick;     // <-- this needs the method below
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.MainForm_FormClosing);

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
              AND CONVERT(date, Tarikh) = @Tarikh;";

                    using (SqlCommand cmd = new SqlCommand(query, con))
                    {
                        cmd.Parameters.AddWithValue("@Work_Order", txtWO.Text);
                        cmd.Parameters.AddWithValue("@hempn", txtHEMPN.Text);

                        // FIX: Send DateTime, not string
                        cmd.Parameters.Add("@Tarikh", SqlDbType.Date).Value =
                            DateTime.Parse(txtPrddte.Text);

                        using (SqlDataReader dr = cmd.ExecuteReader())
                        {
                            if (dr.Read())
                            {
                                txtLine.Text = dr["Line_ID"].ToString();
                                txtleader.Text = dr["Leader"].ToString();
                                txtsleader.Text = dr["Sub_Leader"].ToString();
                                txtophem.Text = dr["No_OperatorHEM"].ToString();
                                txtopsub.Text = dr["No_OperatorSUB"].ToString();
                                txtttl.Text = dr["No_Operator"].ToString();
                            }
                            else
                            {
                                // Clear fields when no data found
                                txtLine.Clear();
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
        /*
        private void OnSensorPulseDetected()
        {
            if (!_batchActive) return;

            int current = Interlocked.Add(ref _count, 0); // atomic read
            int batchCount = current - _batchBaseline;
            if (batchCount < 0) batchCount = 0;

            lblCount.Text = batchCount.ToString("N0");
            lblLastScan.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            if (int.TryParse(txtSTDPK.Text, out int stdPack) && stdPack > 0)
            {
                if (batchCount > 0 && batchCount % stdPack == 0)
                {
                    System.Media.SystemSounds.Asterisk.Play();
                    if (batchCount != _lastCaptureCount) // avoid duplicate firing
                    {
                        _lastCaptureCount = batchCount;
                        _batchActive = false;  // pause counting

                        using (var qrForm = new QRLabelCheck(_currentFG, _currentCPN, _currentCPN2, _currentLot, _currentStdPack))
                        {
                            if (qrForm.ShowDialog(this) == DialogResult.OK)
                            {
                                string qrData = qrForm.ScannedQR;
                                MessageBox.Show($"FG Label OK: {qrData}");

                                _batchActive = true;   // resume
                                StartCountdown();      // take picture
                            }
                        }
                    }
                }
            }
        }
        */
        private void StartCountdown()
        {
            if (_countdownActive) return;

            _countdownValue = 6;
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

                int freq = 600 + (6 - _countdownValue) * 200; // 600 Hz at start, +200 Hz each step
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
                    if (prompt.EnteredPassword != "master") // check password
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
                                              AND PROD_DATE = @ProductionDate";

                                using (SqlCommand cmd = new SqlCommand(updateQuery, con))
                                {
                                    cmd.Parameters.AddWithValue("@WO", txtWO.Text);
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
                    if (prompt.EnteredPassword != "master") // check password
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
                    if (prompt.EnteredPassword != "master") // check password
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

            // Update status
            lblstatup.Text = "Stopped";
            lblstatup.ForeColor = System.Drawing.Color.Red;

            try
            {
                string connectionString = ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString;
                using (SqlConnection con = new SqlConnection(connectionString))
                {
                    con.Open();

                    string checkQuery = "SELECT COUNT(*) FROM PROD_OUTPUT WHERE WO = @WO AND BATCH_NO = @BATCH_NO AND PROD_DATE = @PROD_DATE"; // 050226
                    SqlCommand checkCmd = new SqlCommand(checkQuery, con);
                    checkCmd.Parameters.AddWithValue("@WO", txtWO.Text);
                    checkCmd.Parameters.AddWithValue("@BATCH_NO", cmbBatchID.Text);
                    DateTime prodDatee = DateTime.ParseExact(txtPrddte.Text, "dd/MM/yyyy", null);
                    checkCmd.Parameters.Add("@PROD_DATE", SqlDbType.Date).Value = prodDatee;

                    int count = (int)checkCmd.ExecuteScalar();

                    if (count > 0)
                    {
                        // UPDATE existing record
                        string updateQuery = @"
                                                UPDATE PROD_OUTPUT
                                                SET 
                                                    WO_Qty = @WO_Qty,
                                                    HEM_PN = @HEM_PN,
                                                    CUST_PN_1 = @CUST_PN_1,
                                                    CUST_PN_2 = @CUST_PN_2,
                                                    PROD_DATE = @PROD_DATE,
                                                    PROD_TIME = @PROD_TIME,
                                                    LOT = @LOT,
                                                    STDPK = @STDPK,
                                                    LINE_NO = @LINE_NO,
                                                    BATCH_QTY = @BATCH_QTY,
                                                    Leader = @Leader,
                                                    Sub_Leader = @Sub_Leader,
                                                    No_OperatorHEM = @No_OperatorHEM,
                                                    No_OperatorSUB = @No_OperatorSUB,
                                                    No_Operator = @No_Operator,
                                                    Remarks = @Remarks
                                                WHERE WO = @WO AND BATCH_NO = @BATCH_NO AND PROD_DATE = @PROD_DATE";    // 050226

                        SqlCommand updateCmd = new SqlCommand(updateQuery, con);
                        updateCmd.Parameters.AddWithValue("@WO", txtWO.Text);
                        updateCmd.Parameters.AddWithValue("@WO_Qty", Convert.ToInt32(txtWOQ.Text));
                        updateCmd.Parameters.AddWithValue("@HEM_PN", txtHEMPN.Text);
                        updateCmd.Parameters.AddWithValue("@CUST_PN_1", txtcpn1.Text);
                        updateCmd.Parameters.AddWithValue("@CUST_PN_2", txtcpn2.Text);

                        DateTime prodDate = DateTime.Parse(txtPrddte.Text);
                        updateCmd.Parameters.AddWithValue("@PROD_DATE", prodDate);
                        updateCmd.Parameters.AddWithValue("@PROD_TIME", DateTime.Now.TimeOfDay);
                        updateCmd.Parameters.AddWithValue("@LOT", txtLot.Text);
                        updateCmd.Parameters.AddWithValue("@STDPK", Convert.ToInt32(txtSTDPK.Text));
                        updateCmd.Parameters.AddWithValue("@LINE_NO", txtLine.Text);
                        updateCmd.Parameters.AddWithValue("@BATCH_NO", cmbBatchID.Text);
                        updateCmd.Parameters.AddWithValue("@BATCH_QTY", Convert.ToInt32(lblCount.Text));

                        // NEW FIELDS
                        updateCmd.Parameters.AddWithValue("@Leader", txtleader.Text);
                        updateCmd.Parameters.AddWithValue("@Sub_Leader", txtsleader.Text);
                        updateCmd.Parameters.AddWithValue("@No_OperatorHEM", Convert.ToInt32(txtophem.Text));
                        updateCmd.Parameters.AddWithValue("@No_OperatorSUB", Convert.ToInt32(txtopsub.Text));
                        updateCmd.Parameters.AddWithValue("@No_Operator", Convert.ToInt32(txtttl.Text));
                        updateCmd.Parameters.AddWithValue("@Remarks", txtrmk.Text);

                        updateCmd.ExecuteNonQuery();

                        MessageBox.Show("Existing batch updated successfully.", "Data Updated", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    else
                    {
                        // INSERT new record
                        string insertQuery = @"
    INSERT INTO PROD_OUTPUT 
    (WO, WO_Qty, HEM_PN, CUST_PN_1, CUST_PN_2, PROD_DATE, PROD_TIME, LOT, STDPK, LINE_NO, 
     BATCH_NO, BATCH_QTY, Leader, Sub_Leader, No_OperatorHEM, No_OperatorSUB, No_Operator, Remarks)
    VALUES 
    (@WO, @WO_Qty, @HEM_PN, @CUST_PN_1, @CUST_PN_2, @PROD_DATE, @PROD_TIME, @LOT, @STDPK, @LINE_NO, 
     @BATCH_NO, @BATCH_QTY, @Leader, @Sub_Leader, @No_OperatorHEM, @No_OperatorSUB, @No_Operator, @Remarks)";

                        SqlCommand insertCmd = new SqlCommand(insertQuery, con);
                        insertCmd.Parameters.AddWithValue("@WO", txtWO.Text);
                        insertCmd.Parameters.AddWithValue("@WO_Qty", Convert.ToInt32(txtWOQ.Text));
                        insertCmd.Parameters.AddWithValue("@HEM_PN", txtHEMPN.Text);
                        insertCmd.Parameters.AddWithValue("@CUST_PN_1", txtcpn1.Text);
                        insertCmd.Parameters.AddWithValue("@CUST_PN_2", txtcpn2.Text);

                        DateTime prodDate = DateTime.Parse(txtPrddte.Text);
                        insertCmd.Parameters.AddWithValue("@PROD_DATE", prodDate);
                        insertCmd.Parameters.AddWithValue("@PROD_TIME", DateTime.Now.TimeOfDay);
                        insertCmd.Parameters.AddWithValue("@LOT", txtLot.Text);
                        insertCmd.Parameters.AddWithValue("@STDPK", Convert.ToInt32(txtSTDPK.Text));
                        insertCmd.Parameters.AddWithValue("@LINE_NO", txtLine.Text);
                        insertCmd.Parameters.AddWithValue("@BATCH_NO", cmbBatchID.Text);
                        insertCmd.Parameters.AddWithValue("@BATCH_QTY", Convert.ToInt32(lblCount.Text));

                        // NEW FIELDS
                        insertCmd.Parameters.AddWithValue("@Leader", txtleader.Text);
                        insertCmd.Parameters.AddWithValue("@Sub_Leader", txtsleader.Text);
                        insertCmd.Parameters.AddWithValue("@No_OperatorHEM", Convert.ToInt32(txtophem.Text));
                        insertCmd.Parameters.AddWithValue("@No_OperatorSUB", Convert.ToInt32(txtopsub.Text));
                        insertCmd.Parameters.AddWithValue("@No_Operator", Convert.ToInt32(txtttl.Text));
                        insertCmd.Parameters.AddWithValue("@Remarks", txtrmk.Text);

                        insertCmd.ExecuteNonQuery();

                        MessageBox.Show("New batch inserted successfully.", "Data Saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
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
        
        /*
        private void UpdateOutputLabels()
        {
            string query = @"
        SELECT 
            ISNULL(SUM(CASE WHEN WO = @WO THEN STDPK ELSE 0 END), 0) AS Total_WO_Output,
            ISNULL(SUM(CASE WHEN PROD_DATE = CAST(GETDATE() AS DATE) THEN STDPK ELSE 0 END), 0) AS Total_WO_Output_Today,
            ISNULL(SUM(CASE WHEN WO = @WO AND BATCH_NO = @BatchNo THEN BATCH_QTY ELSE 0 END), 0) AS curr_batch_qty
        FROM PROD_OUTPUT";

            string connectionString = ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString;

            try
            {
                using (SqlConnection con = new SqlConnection(connectionString))
                using (SqlCommand cmd = new SqlCommand(query, con))
                {
                    cmd.Parameters.AddWithValue("@WO", txtWO.Text);
                    cmd.Parameters.AddWithValue("@BatchNo", cmbBatchID.Text);
                    con.Open();
                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            label1.Text = Convert.ToInt32(reader["Total_WO_Output"]).ToString();
                            label2.Text = Convert.ToInt32(reader["Total_WO_Output_Today"]).ToString();
                            lblCount.Text = Convert.ToInt32(reader["curr_batch_qty"]).ToString();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error updating labels: {ex.Message}");
                label1.Text = "0";
                label2.Text = "0";
            }
        }

        */
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

            //Unlocked the textbox
            txtWO.ReadOnly = false;
            txtWOQ.ReadOnly = false;
            txtcpn1.ReadOnly = false;
            txtcpn2.ReadOnly = false;
            txtHEMPN.ReadOnly = false;
            txtPrddte.ReadOnly = false;
            txtLot.ReadOnly = false;
            txtSTDPK.ReadOnly = false;
            txtLot2.ReadOnly = false;
            txtPO.ReadOnly = false;
            txtCust.ReadOnly = false;
            cmbBatchID.Enabled = true;

            // Assign to textboxes
            txtWO.Text = parts[0].Trim();     // Work Order
            txtWOQ.Text = parts[1].Trim();    // Work Order Qty
            txtHEMPN.Text = parts[2].Trim();  // Part Number
            txtcpn1.Text = parts[3].Trim();   // cpn1
            txtcpn2.Text = parts[4].Trim();   // cpn2
            txtLot.Text = parts[5].Trim();    // Lot
            txtLot2.Text = parts[6].Trim();    // Lot2
            txtPrddte.Text = parts[7].Trim(); // Production Date
            txtSTDPK.Text = parts[8].Trim();  // Std Pack Qty
            txtCust.Text = parts[9].Trim(); // Customer name
            txtPO.Text = parts[10].Trim(); // Customer PO

            //locked the textbox
            txtWO.ReadOnly = true;
            txtWOQ.ReadOnly = true;
            txtcpn1.ReadOnly = true;
            txtcpn2.ReadOnly = true;
            txtHEMPN.ReadOnly = true;
            txtPrddte.ReadOnly = true;
            txtLot.ReadOnly = true;
            txtSTDPK.ReadOnly = true;
            txtLot2.ReadOnly = true;
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
                txtHEMPN.Clear();
                txtPrddte.Clear();
                txtLot.Clear();
                txtSTDPK.Clear();
                txtQRBOM.Clear();
                txtLot2.Clear();
                txtCust.Clear();
                txtPO.Clear();

                txtLine.Clear();
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
                    if (prompt.EnteredPassword == "master") // check password
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

                        txtLine.Clear();
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
        private void Camera1_NewFrame(object sender, NewFrameEventArgs e)
        {
            var bmp = (Bitmap)e.Frame.Clone();
            var old = picCamera1.Image;
            picCamera1.Image = bmp;
            old?.Dispose();
        }
        private void Camera2_NewFrame(object sender, NewFrameEventArgs e)
        {
            var bmp = (Bitmap)e.Frame.Clone();
            var old = picCamera2.Image;
            picCamera2.Image = bmp;
            old?.Dispose();
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

            if (camera1 != null)
            {
                if (camera1.IsRunning) { camera1.SignalToStop(); camera1.WaitForStop(); }
                camera1 = null;
            }

            if (camera2 != null)
            {
                if (camera2.IsRunning) { camera2.SignalToStop(); camera2.WaitForStop(); }
                camera2 = null;
            }
        }
        private void MainForm_Load(object sender, EventArgs e)
        {
            using (var prompt = new PasswordPrompt())
            {
                if (prompt.ShowDialog(this) == DialogResult.OK)
                {
                    if (prompt.EnteredPassword != "master") // replace with your password
                    {
                        MessageBox.Show("Incorrect password. Application will close.",
                                        "Access Denied", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        this.Close(); // close form if wrong password
                        return; // stop further execution
                    }
                }
                else
                {
                    // If user presses Cancel, exit
                    this.Close();
                    return;
                }
            }

            txtWO.ReadOnly = true;
            txtWOQ.ReadOnly = true;
            txtcpn1.ReadOnly = true;
            txtcpn2.ReadOnly = true;
            txtHEMPN.ReadOnly = true;
            txtPrddte.ReadOnly = true;
            txtLot.ReadOnly = true;
            txtSTDPK.ReadOnly = true;
            txtLot2.ReadOnly = true;
            txtCust.ReadOnly = true;
            txtPO.ReadOnly = true;

            // ===== Camera Initialization (after password accepted) =====
            videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);

            if (videoDevices.Count < 2)
            {
                MessageBox.Show("Less than 2 cameras detected.",
                                "Camera Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Camera 1
            camera1 = new VideoCaptureDevice(videoDevices[0].MonikerString);
            camera1.NewFrame += Camera1_NewFrame;
            camera1.Start();

            // Camera 2
            camera2 = new VideoCaptureDevice(videoDevices[1].MonikerString);
            camera2.NewFrame += Camera2_NewFrame;
            camera2.Start();

            

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
        private void panel1_Paint(object sender, PaintEventArgs e)
        {

        }
        private void UpdateOutputLabels()
        {
            string query = @"
                        SELECT 
                            ISNULL(SUM(CASE WHEN WO = @WO THEN BATCH_QTY ELSE 0 END), 0) AS Total_WO_Output,
                            ISNULL(SUM(CASE WHEN PROD_DATE = CAST(GETDATE() AS DATE) THEN BATCH_QTY ELSE 0 END), 0) AS Total_WO_Output_Today,
                            ISNULL(SUM(CASE WHEN WO = @WO AND BATCH_NO = @BatchNo AND PROD_DATE = @ProductionDate THEN BATCH_QTY ELSE 0 END), 0) AS curr_batch_qty
                        FROM PROD_OUTPUT";

            string connectionString = ConfigurationManager.ConnectionStrings["ConnString"].ConnectionString;

            try
            {
                using (SqlConnection con = new SqlConnection(connectionString))
                using (SqlCommand cmd = new SqlCommand(query, con))
                {
                    cmd.Parameters.AddWithValue("@WO", txtWO.Text);
                    cmd.Parameters.AddWithValue("@BatchNo", cmbBatchID.Text);
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
        // 2️⃣ Real-time counting from sensor
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
            //++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++++  "N0" format specifier
            lblCount.Text = batchCount.ToString();
            lblLastScan.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            if (int.TryParse(txtSTDPK.Text, out int stdPack) && stdPack > 0)
            {
                if (batchCount > 0 && batchCount % stdPack == 0)
                {
                    string currentPN = txtHEMPN.Text.Trim();

                    // 🚫 Skip QR check if PN exists in PN_NO_LABEL
                    if (IsPNNoLabel(currentPN))
                    {
                        return; // Do nothing, continue counting
                    }

                    System.Media.SystemSounds.Asterisk.Play();

                    if (batchCount != _lastCaptureCount)
                    {
                        _lastCaptureCount = batchCount;
                        _batchActive = false;

                        this.Invoke(new Action(() =>
                        {
                            try
                            {
                                currentSerial++;
                                endSerial = currentSerial;
                               

                                printDocument1.Print();
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show("Print Error: " + ex.Message);
                            }
                        }));

                        _batchActive = true;
                        StartCountdown();
                    }
                }
            }
        }

        // -------------------------------
        // 3️⃣ Start a new batch manually
        // -------------------------------
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
                    if (prompt.EnteredPassword != "master")
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
                    if (prompt.EnteredPassword != "master")
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

        private void printDocument1_PrintPage(object sender, System.Drawing.Printing.PrintPageEventArgs e)
        {
            Graphics g = e.Graphics;

            using (Font font = new Font("Arial", 14))
            using (Font smallfont = new Font("Arial", 11))
            {
                string serialFormatted = currentSerial.ToString("D4");

                // Build QR content (NO QC TYPE)
                string qrContent =
                    txtcpn1.Text + ";" +
                    txtcpn2.Text + ";" +
                    txtHEMPN.Text + ";" +
                    txtLot.Text + ";" +
                    txtLot2.Text + ";" +
                    txtPO.Text + ";" +
                    txtCust.Text + ";" +
                    txtSTDPK.Text + ";" +
                    txtLine + ":" + cmbBatchID.Text + ":" + serialFormatted + ";";

                // Print label text
                g.DrawString(txtcpn1.Text, smallfont, Brushes.Black, 20, 20);
                g.DrawString(txtcpn2.Text, smallfont, Brushes.Black, 20, 30);
                g.DrawString(txtHEMPN.Text, font, Brushes.Black, 20, 140);
                g.DrawString(txtLot.Text, smallfont, Brushes.Black, 20, 200);
                g.DrawString(txtLot2.Text, smallfont, Brushes.Black, 20, 220);
                g.DrawString(txtPO.Text, font, Brushes.Black, 20, 280);
                g.DrawString(txtCust.Text, font, Brushes.Black, 20, 340);
                g.DrawString(txtSTDPK.Text, font, Brushes.Black, 250, 210);
                g.DrawString(serialFormatted, font, Brushes.Black, 250, 280);

                // Generate QR
                QRCodeData qrCodeData = qrGenerator.CreateQrCode(qrContent, QRCodeGenerator.ECCLevel.Q);
                QRCode qrCode = new QRCode(qrCodeData);

                using (Bitmap qrImage = qrCode.GetGraphic(2))
                {
                    g.DrawImage(qrImage, new Point(180, 190));
                }
            }

            // Only print ONE page per trigger
            e.HasMorePages = false;
        }
    }
}
