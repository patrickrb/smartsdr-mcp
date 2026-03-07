namespace ComPortPTT
{
    partial class Form1
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
            this.comboComPort = new System.Windows.Forms.ComboBox();
            this.comboPolarity = new System.Windows.Forms.ComboBox();
            this.radCTS = new System.Windows.Forms.RadioButton();
            this.radDSR = new System.Windows.Forms.RadioButton();
            this.statusStrip1 = new System.Windows.Forms.StatusStrip();
            this.toolStriplblCTS = new System.Windows.Forms.ToolStripStatusLabel();
            this.toolStriplblDSR = new System.Windows.Forms.ToolStripStatusLabel();
            this.statusStrip1.SuspendLayout();
            this.SuspendLayout();
            // 
            // comboComPort
            // 
            this.comboComPort.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.comboComPort.FormattingEnabled = true;
            this.comboComPort.Location = new System.Drawing.Point(23, 24);
            this.comboComPort.Name = "comboComPort";
            this.comboComPort.Size = new System.Drawing.Size(60, 21);
            this.comboComPort.TabIndex = 0;
            this.comboComPort.SelectedIndexChanged += new System.EventHandler(this.comboComPort_SelectedIndexChanged);
            // 
            // comboPolarity
            // 
            this.comboPolarity.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.comboPolarity.FormattingEnabled = true;
            this.comboPolarity.Items.AddRange(new object[] {
            "Active Low",
            "Active High"});
            this.comboPolarity.Location = new System.Drawing.Point(212, 24);
            this.comboPolarity.Name = "comboPolarity";
            this.comboPolarity.Size = new System.Drawing.Size(89, 21);
            this.comboPolarity.TabIndex = 3;
            this.comboPolarity.SelectedIndexChanged += new System.EventHandler(this.comboPolarity_SelectedIndexChanged);
            // 
            // radCTS
            // 
            this.radCTS.AutoSize = true;
            this.radCTS.Checked = true;
            this.radCTS.Location = new System.Drawing.Point(104, 25);
            this.radCTS.Name = "radCTS";
            this.radCTS.Size = new System.Drawing.Size(46, 17);
            this.radCTS.TabIndex = 4;
            this.radCTS.Text = "CTS";
            this.radCTS.UseVisualStyleBackColor = true;
            this.radCTS.CheckedChanged += new System.EventHandler(this.radCTS_CheckedChanged);
            // 
            // radDSR
            // 
            this.radDSR.AutoSize = true;
            this.radDSR.Location = new System.Drawing.Point(156, 25);
            this.radDSR.Name = "radDSR";
            this.radDSR.Size = new System.Drawing.Size(48, 17);
            this.radDSR.TabIndex = 5;
            this.radDSR.Text = "DSR";
            this.radDSR.UseVisualStyleBackColor = true;
            this.radDSR.CheckedChanged += new System.EventHandler(this.radDSR_CheckedChanged);
            // 
            // statusStrip1
            // 
            this.statusStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.toolStriplblCTS,
            this.toolStriplblDSR});
            this.statusStrip1.Location = new System.Drawing.Point(0, 59);
            this.statusStrip1.Name = "statusStrip1";
            this.statusStrip1.Size = new System.Drawing.Size(321, 22);
            this.statusStrip1.TabIndex = 6;
            this.statusStrip1.Text = "statusStrip1";
            // 
            // toolStriplblCTS
            // 
            this.toolStriplblCTS.Name = "toolStriplblCTS";
            this.toolStriplblCTS.Size = new System.Drawing.Size(28, 17);
            this.toolStriplblCTS.Text = "CTS";
            // 
            // toolStriplblDSR
            // 
            this.toolStriplblDSR.Name = "toolStriplblDSR";
            this.toolStriplblDSR.Size = new System.Drawing.Size(28, 17);
            this.toolStriplblDSR.Text = "DSR";
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(321, 81);
            this.Controls.Add(this.statusStrip1);
            this.Controls.Add(this.radDSR);
            this.Controls.Add(this.radCTS);
            this.Controls.Add(this.comboPolarity);
            this.Controls.Add(this.comboComPort);
            this.Name = "Form1";
            this.Text = "COM Port PTT";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.Form1_FormClosing);
            this.statusStrip1.ResumeLayout(false);
            this.statusStrip1.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.ComboBox comboComPort;
        private System.Windows.Forms.ComboBox comboPolarity;
        private System.Windows.Forms.RadioButton radCTS;
        private System.Windows.Forms.RadioButton radDSR;
        private System.Windows.Forms.StatusStrip statusStrip1;
        private System.Windows.Forms.ToolStripStatusLabel toolStriplblCTS;
        private System.Windows.Forms.ToolStripStatusLabel toolStriplblDSR;
    }
}

