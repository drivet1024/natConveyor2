namespace Nat_Conveyor
{
    partial class DataFlowfrm
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
            this.txtScaleStatus = new System.Windows.Forms.TextBox();
            this.txtDimensionStatus = new System.Windows.Forms.TextBox();
            this.txtCameraStatus = new System.Windows.Forms.TextBox();
            this.button2 = new System.Windows.Forms.Button();
            this.button1 = new System.Windows.Forms.Button();
            this.txtPingCam = new System.Windows.Forms.TextBox();
            this.txtPingDim = new System.Windows.Forms.TextBox();
            this.txtPingScale = new System.Windows.Forms.TextBox();
            this.label1 = new System.Windows.Forms.Label();
            this.label2 = new System.Windows.Forms.Label();
            this.label3 = new System.Windows.Forms.Label();
            this.SuspendLayout();
            // 
            // txtScaleStatus
            // 
            this.txtScaleStatus.Location = new System.Drawing.Point(742, 54);
            this.txtScaleStatus.Multiline = true;
            this.txtScaleStatus.Name = "txtScaleStatus";
            this.txtScaleStatus.Size = new System.Drawing.Size(150, 347);
            this.txtScaleStatus.TabIndex = 7;
            // 
            // txtDimensionStatus
            // 
            this.txtDimensionStatus.Location = new System.Drawing.Point(477, 54);
            this.txtDimensionStatus.Multiline = true;
            this.txtDimensionStatus.Name = "txtDimensionStatus";
            this.txtDimensionStatus.Size = new System.Drawing.Size(259, 347);
            this.txtDimensionStatus.TabIndex = 6;
            // 
            // txtCameraStatus
            // 
            this.txtCameraStatus.Location = new System.Drawing.Point(227, 54);
            this.txtCameraStatus.Multiline = true;
            this.txtCameraStatus.Name = "txtCameraStatus";
            this.txtCameraStatus.Size = new System.Drawing.Size(244, 347);
            this.txtCameraStatus.TabIndex = 5;
            // 
            // button2
            // 
            this.button2.BackColor = System.Drawing.SystemColors.ControlDarkDark;
            this.button2.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.button2.Font = new System.Drawing.Font("Century Gothic", 15.75F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.button2.Location = new System.Drawing.Point(755, 407);
            this.button2.Name = "button2";
            this.button2.Size = new System.Drawing.Size(137, 59);
            this.button2.TabIndex = 22;
            this.button2.Text = "Close";
            this.button2.UseVisualStyleBackColor = false;
            this.button2.Click += new System.EventHandler(this.button2_Click);
            // 
            // button1
            // 
            this.button1.BackColor = System.Drawing.SystemColors.ControlDarkDark;
            this.button1.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.button1.Font = new System.Drawing.Font("Century Gothic", 15.75F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.button1.Location = new System.Drawing.Point(612, 407);
            this.button1.Name = "button1";
            this.button1.Size = new System.Drawing.Size(137, 59);
            this.button1.TabIndex = 23;
            this.button1.Text = "Reset";
            this.button1.UseVisualStyleBackColor = false;
            this.button1.Click += new System.EventHandler(this.button1_Click);
            // 
            // txtPingCam
            // 
            this.txtPingCam.Location = new System.Drawing.Point(12, 29);
            this.txtPingCam.Multiline = true;
            this.txtPingCam.Name = "txtPingCam";
            this.txtPingCam.Size = new System.Drawing.Size(154, 118);
            this.txtPingCam.TabIndex = 24;
            // 
            // txtPingDim
            // 
            this.txtPingDim.Location = new System.Drawing.Point(12, 167);
            this.txtPingDim.Multiline = true;
            this.txtPingDim.Name = "txtPingDim";
            this.txtPingDim.Size = new System.Drawing.Size(154, 136);
            this.txtPingDim.TabIndex = 25;
            // 
            // txtPingScale
            // 
            this.txtPingScale.Location = new System.Drawing.Point(12, 322);
            this.txtPingScale.Multiline = true;
            this.txtPingScale.Name = "txtPingScale";
            this.txtPingScale.Size = new System.Drawing.Size(154, 144);
            this.txtPingScale.TabIndex = 26;
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(9, 13);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(43, 13);
            this.label1.TabIndex = 27;
            this.label1.Text = "Camera";
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(12, 151);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(56, 13);
            this.label2.TabIndex = 28;
            this.label2.Text = "Dimension";
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(9, 306);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(34, 13);
            this.label3.TabIndex = 29;
            this.label3.Text = "Scale";
            // 
            // DataFlowfrm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.SystemColors.ControlDarkDark;
            this.ClientSize = new System.Drawing.Size(904, 480);
            this.Controls.Add(this.label3);
            this.Controls.Add(this.label2);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.txtPingScale);
            this.Controls.Add(this.txtPingDim);
            this.Controls.Add(this.txtPingCam);
            this.Controls.Add(this.button1);
            this.Controls.Add(this.button2);
            this.Controls.Add(this.txtScaleStatus);
            this.Controls.Add(this.txtDimensionStatus);
            this.Controls.Add(this.txtCameraStatus);
            this.ForeColor = System.Drawing.SystemColors.ControlText;
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
            this.Name = "DataFlowfrm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "DataFlowfrm";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.TextBox txtScaleStatus;
        private System.Windows.Forms.TextBox txtDimensionStatus;
        private System.Windows.Forms.TextBox txtCameraStatus;
        private System.Windows.Forms.Button button2;
        private System.Windows.Forms.Button button1;
        private System.Windows.Forms.TextBox txtPingCam;
        private System.Windows.Forms.TextBox txtPingDim;
        private System.Windows.Forms.TextBox txtPingScale;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.Label label3;
    }
}