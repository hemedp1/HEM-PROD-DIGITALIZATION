using System;
using System.Linq;
using System.Windows.Forms;

namespace RCU_FG_Output_Counter
{
    public partial class QRLabelCheck : Form
    {
        private readonly string _wofg;
        private readonly string _wocpn;
        private readonly string _wocpn2;
        private readonly string _woLot;
        private readonly int _woStdPack;

        public string ScannedQR { get; private set; }

        public QRLabelCheck()
        {
            InitializeComponent();
            txtScan.KeyDown += TxtScan_KeyDown;
        }

        public QRLabelCheck(string FG, string CPN, string CPN2, string Lot, int StdPack)
        {
            InitializeComponent();

            this.Load += QRLabelCheck_Load;
            this.ControlBox = false;
            this.MinimizeBox = false;
            this.MaximizeBox = false;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;

            _wofg = FG;
            _wocpn = CPN;
            _wocpn2 = CPN2;
            _woLot = Lot;
            _woStdPack = StdPack;

            txtScan.KeyDown += TxtScan_KeyDown;
        }

        private void TxtScan_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;

            ScannedQR = txtScan.Text.Trim();

            if (string.IsNullOrEmpty(ScannedQR))
            {
                MessageBox.Show("QR code is empty. Please scan again.",
                    "QR Check", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string[] partsRaw = ScannedQR.Split(',');

            // Your QR always has 5 fields (CPN1, LOT, STD, CPN2 or empty, FG)
            if (partsRaw.Length != 5)
            {
                MessageBox.Show("Invalid QR format. Please rescan.",
                    "QR Check", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Trim all fields
            string[] parts = partsRaw.Select(p => p.Trim()).ToArray();

            string qrcpn1 = parts[0];
            string qrLot = parts[1];
            int.TryParse(parts[2], out int qrStdPack);
            string qrcpn2 = string.IsNullOrWhiteSpace(parts[3]) ? null : parts[3];
            string qrfg = parts[4];

            // --- VALIDATIONS ---

            if (qrcpn1 != _wocpn)
            {
                MessageBox.Show(
                    $"Part Number mismatch!\nExpected: {_wocpn}\nScanned: {qrcpn1}",
                    "Mismatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (qrLot != _woLot)
            {
                MessageBox.Show(
                    $"Lot mismatch!\nExpected: {_woLot}\nScanned: {qrLot}",
                    "Mismatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (qrStdPack != _woStdPack)
            {
                MessageBox.Show(
                    $"StdPack mismatch!\nExpected: {_woStdPack}\nScanned: {qrStdPack}",
                    "Mismatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (qrfg != _wofg)
            {
                MessageBox.Show(
                    $"FG mismatch!\nExpected: {_wofg}\nScanned: {qrfg}",
                    "Mismatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // CPN2 logic
            if (!string.IsNullOrWhiteSpace(_wocpn2))
            {
                // WO expects second CPN
                if (qrcpn2 == null)
                {
                    MessageBox.Show(
                        $"Second Part Number missing!\nExpected: {_wocpn2}",
                        "Mismatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (qrcpn2 != _wocpn2)
                {
                    MessageBox.Show(
                        $"Second Part Number mismatch!\nExpected: {_wocpn2}\nScanned: {qrcpn2}",
                        "Mismatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
            else
            {
                // WO does NOT expect second CPN
                if (qrcpn2 != null)
                {
                    MessageBox.Show(
                        $"Unexpected Second Part Number!\nExpected: (none)\nScanned: {qrcpn2}",
                        "Mismatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            // SUCCESS
            DialogResult = DialogResult.OK;
            this.Close();
        }


        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);

            if (e.CloseReason == CloseReason.UserClosing && this.DialogResult != DialogResult.OK)
            {
                MessageBox.Show("You must scan a valid FG Label to continue.",
                    "QR Check", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            txtScan.Focus();
        }

        private void QRLabelCheck_Load(object sender, EventArgs e)
        {
            txtScan.Focus();
        }
    }
}
