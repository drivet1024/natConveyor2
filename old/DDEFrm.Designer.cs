namespace Nat_Conveyor
{
    partial class DDEFrm
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
            this.button2 = new System.Windows.Forms.Button();
            this.btnChute = new System.Windows.Forms.Button();
            this.btnMainConv = new System.Windows.Forms.Button();
            this.btnActiveNew = new System.Windows.Forms.Button();
            this.button1 = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // button2
            // 
            this.button2.BackColor = System.Drawing.SystemColors.ControlDarkDark;
            this.button2.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.button2.Font = new System.Drawing.Font("Century Gothic", 15.75F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.button2.Location = new System.Drawing.Point(446, 371);
            this.button2.Name = "button2";
            this.button2.Size = new System.Drawing.Size(137, 59);
            this.button2.TabIndex = 22;
            this.button2.Text = "Quit";
            this.button2.UseVisualStyleBackColor = false;
            this.button2.Click += new System.EventHandler(this.button2_Click);
            // 
            // btnChute
            // 
            this.btnChute.BackColor = System.Drawing.SystemColors.ControlDarkDark;
            this.btnChute.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnChute.Font = new System.Drawing.Font("Century Gothic", 15.75F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.btnChute.Location = new System.Drawing.Point(32, 123);
            this.btnChute.Name = "btnChute";
            this.btnChute.Size = new System.Drawing.Size(166, 59);
            this.btnChute.TabIndex = 23;
            this.btnChute.Text = "Chute closed";
            this.btnChute.UseVisualStyleBackColor = false;
            this.btnChute.Visible = false;
            this.btnChute.Click += new System.EventHandler(this.btnChute_Click);
            // 
            // btnMainConv
            // 
            this.btnMainConv.BackColor = System.Drawing.SystemColors.ControlDarkDark;
            this.btnMainConv.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnMainConv.Font = new System.Drawing.Font("Century Gothic", 15.75F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.btnMainConv.Location = new System.Drawing.Point(32, 29);
            this.btnMainConv.Name = "btnMainConv";
            this.btnMainConv.Size = new System.Drawing.Size(166, 59);
            this.btnMainConv.TabIndex = 24;
            this.btnMainConv.Text = "Main stop";
            this.btnMainConv.UseVisualStyleBackColor = false;
            this.btnMainConv.Click += new System.EventHandler(this.btnMainConv_Click);
            // 
            // btnActiveNew
            // 
            this.btnActiveNew.BackColor = System.Drawing.SystemColors.ControlDarkDark;
            this.btnActiveNew.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.btnActiveNew.Font = new System.Drawing.Font("Century Gothic", 15.75F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.btnActiveNew.Location = new System.Drawing.Point(404, 29);
            this.btnActiveNew.Name = "btnActiveNew";
            this.btnActiveNew.Size = new System.Drawing.Size(166, 59);
            this.btnActiveNew.TabIndex = 25;
            this.btnActiveNew.Text = "Activate new";
            this.btnActiveNew.UseVisualStyleBackColor = false;
            this.btnActiveNew.Visible = false;
            this.btnActiveNew.Click += new System.EventHandler(this.button1_Click);
            // 
            // button1
            // 
            this.button1.BackColor = System.Drawing.SystemColors.ControlDarkDark;
            this.button1.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.button1.Font = new System.Drawing.Font("Century Gothic", 15.75F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.button1.Location = new System.Drawing.Point(32, 226);
            this.button1.Name = "button1";
            this.button1.Size = new System.Drawing.Size(166, 59);
            this.button1.TabIndex = 26;
            this.button1.Text = "update Aldo";
            this.button1.UseVisualStyleBackColor = false;
            this.button1.Visible = false;
            this.button1.Click += new System.EventHandler(this.button1_Click_1);
            // 
            // DDEFrm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(16F, 33F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.Color.Gray;
            this.ClientSize = new System.Drawing.Size(595, 442);
            this.Controls.Add(this.button1);
            this.Controls.Add(this.btnActiveNew);
            this.Controls.Add(this.btnMainConv);
            this.Controls.Add(this.btnChute);
            this.Controls.Add(this.button2);
            this.Font = new System.Drawing.Font("Century Gothic", 8.25F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedToolWindow;
            this.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.Name = "DDEFrm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Conveyor status";
            this.Load += new System.EventHandler(this.DDEFrm_Load);
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.Button button2;
        private System.Windows.Forms.Button btnChute;
        private System.Windows.Forms.Button btnMainConv;
        private System.Windows.Forms.Button btnActiveNew;
        private System.Windows.Forms.Button button1;
    }
}