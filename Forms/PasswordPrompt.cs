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
    public partial class PasswordPrompt : Form
    {
        private TextBox txtPassword;
        private Button btnOK;
        private Button btnCancel;

        public string EnteredPassword { get; private set; }
        public PasswordPrompt()
        {
            //InitializeComponent();
            this.Text = "Password Required";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.StartPosition = FormStartPosition.CenterParent;
            this.MinimizeBox = false;
            this.MaximizeBox = false;
            this.Width = 350;
            this.Height = 150;

            Label lbl = new Label();
            lbl.Text = "Enter Supervisor Password:";
            lbl.Left = 10;
            lbl.Top = 15;
            lbl.Width = 300;
            this.Controls.Add(lbl);

            txtPassword = new TextBox();
            txtPassword.Left = 10;
            txtPassword.Top = 40;
            txtPassword.Width = 300;
            txtPassword.PasswordChar = '*';   // mask input
            this.Controls.Add(txtPassword);

            btnOK = new Button();
            btnOK.Text = "OK";
            btnOK.Left = 150;
            btnOK.Top = 70;
            btnOK.Click += (s, e) => { EnteredPassword = txtPassword.Text; this.DialogResult = DialogResult.OK; };
            this.Controls.Add(btnOK);

            btnCancel = new Button();
            btnCancel.Text = "Cancel";
            btnCancel.Left = 240;
            btnCancel.Top = 70;
            btnCancel.Click += (s, e) => { this.DialogResult = DialogResult.Cancel; };
            this.Controls.Add(btnCancel);
        }
    }
}
