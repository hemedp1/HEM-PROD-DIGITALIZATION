
namespace RCU_FG_Output_Counter
{
    partial class QRLabelCheck
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
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
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.txtScan = new System.Windows.Forms.TextBox();
            this.lblcounter = new System.Windows.Forms.Label();
            this.SuspendLayout();
            // 
            // txtScan
            // 
            this.txtScan.Font = new System.Drawing.Font("Microsoft Sans Serif", 11F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.txtScan.Location = new System.Drawing.Point(131, 101);
            this.txtScan.Name = "txtScan";
            this.txtScan.Size = new System.Drawing.Size(471, 32);
            this.txtScan.TabIndex = 0;
            // 
            // lblcounter
            // 
            this.lblcounter.AutoSize = true;
            this.lblcounter.BackColor = System.Drawing.Color.Gainsboro;
            this.lblcounter.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblcounter.ForeColor = System.Drawing.SystemColors.HotTrack;
            this.lblcounter.Location = new System.Drawing.Point(246, 59);
            this.lblcounter.Name = "lblcounter";
            this.lblcounter.Size = new System.Drawing.Size(233, 29);
            this.lblcounter.TabIndex = 27;
            this.lblcounter.Text = "Scan QR Label 1 Lot";
            // 
            // QRLabelCheck
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(9F, 20F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.Color.Gainsboro;
            this.ClientSize = new System.Drawing.Size(745, 303);
            this.Controls.Add(this.lblcounter);
            this.Controls.Add(this.txtScan);
            this.Name = "QRLabelCheck";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "QRLabelCheck";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.TextBox txtScan;
        private System.Windows.Forms.Label lblcounter;
    }
}