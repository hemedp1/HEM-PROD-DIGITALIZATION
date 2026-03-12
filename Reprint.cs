using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RCU_FG_Output_Counter
{
    public partial class Reprint : Form
    {
        public string BatchNumber { get; private set; }
        public int SerialNumber { get; private set; }
        public Reprint()
        {
            InitializeComponent();
        }

        private void btnOK_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtBatch.Text) ||
                !int.TryParse(txtSerial.Text, out int serial))
            {
                MessageBox.Show("Please enter valid batch and serial number.", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            BatchNumber = txtBatch.Text.Trim();
            SerialNumber = serial;

            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }
    }
}
