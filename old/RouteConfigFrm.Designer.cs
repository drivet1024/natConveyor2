namespace Nat_Conveyor
{
    partial class RouteConfigFrm
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
            this.dgShift = new System.Windows.Forms.DataGridView();
            this.cbShift = new System.Windows.Forms.ComboBox();
            this.btnAdd = new System.Windows.Forms.Button();
            this.btnRemove = new System.Windows.Forms.Button();
            this.btnSave = new System.Windows.Forms.Button();
            this.btnClose = new System.Windows.Forms.Button();
            this.txtChuteNo = new System.Windows.Forms.TextBox();
            this.txtRouteNo = new System.Windows.Forms.TextBox();
            this.label1 = new System.Windows.Forms.Label();
            this.label2 = new System.Windows.Forms.Label();
            ((System.ComponentModel.ISupportInitialize)(this.dgShift)).BeginInit();
            this.SuspendLayout();
            // 
            // dgShift
            // 
            this.dgShift.AllowUserToAddRows = false;
            this.dgShift.AllowUserToDeleteRows = false;
            this.dgShift.ColumnHeadersHeightSizeMode = System.Windows.Forms.DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            this.dgShift.Location = new System.Drawing.Point(12, 45);
            this.dgShift.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.dgShift.MultiSelect = false;
            this.dgShift.Name = "dgShift";
            this.dgShift.ReadOnly = true;
            this.dgShift.RowHeadersVisible = false;
            this.dgShift.SelectionMode = System.Windows.Forms.DataGridViewSelectionMode.FullRowSelect;
            this.dgShift.Size = new System.Drawing.Size(249, 429);
            this.dgShift.TabIndex = 0;
            this.dgShift.CellClick += new System.Windows.Forms.DataGridViewCellEventHandler(this.dgShift_CellClick);
            this.dgShift.CellContentClick += new System.Windows.Forms.DataGridViewCellEventHandler(this.dgShift_CellContentClick);
            // 
            // cbShift
            // 
            this.cbShift.FormattingEnabled = true;
            this.cbShift.Location = new System.Drawing.Point(12, 13);
            this.cbShift.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.cbShift.Name = "cbShift";
            this.cbShift.Size = new System.Drawing.Size(249, 24);
            this.cbShift.TabIndex = 1;
            this.cbShift.SelectedIndexChanged += new System.EventHandler(this.cbShift_SelectedIndexChanged);
            // 
            // btnAdd
            // 
            this.btnAdd.Location = new System.Drawing.Point(275, 422);
            this.btnAdd.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.btnAdd.Name = "btnAdd";
            this.btnAdd.Size = new System.Drawing.Size(111, 52);
            this.btnAdd.TabIndex = 2;
            this.btnAdd.Text = "Add";
            this.btnAdd.UseVisualStyleBackColor = true;
            this.btnAdd.Click += new System.EventHandler(this.btnAdd_Click);
            // 
            // btnRemove
            // 
            this.btnRemove.Location = new System.Drawing.Point(419, 422);
            this.btnRemove.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.btnRemove.Name = "btnRemove";
            this.btnRemove.Size = new System.Drawing.Size(126, 52);
            this.btnRemove.TabIndex = 3;
            this.btnRemove.Text = "Remove";
            this.btnRemove.UseVisualStyleBackColor = true;
            this.btnRemove.Click += new System.EventHandler(this.btnRemove_Click);
            // 
            // btnSave
            // 
            this.btnSave.Location = new System.Drawing.Point(577, 422);
            this.btnSave.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.btnSave.Name = "btnSave";
            this.btnSave.Size = new System.Drawing.Size(126, 52);
            this.btnSave.TabIndex = 4;
            this.btnSave.Text = "Save";
            this.btnSave.UseVisualStyleBackColor = true;
            this.btnSave.Click += new System.EventHandler(this.btnSave_Click);
            // 
            // btnClose
            // 
            this.btnClose.Location = new System.Drawing.Point(740, 422);
            this.btnClose.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.btnClose.Name = "btnClose";
            this.btnClose.Size = new System.Drawing.Size(126, 52);
            this.btnClose.TabIndex = 5;
            this.btnClose.Text = "Close";
            this.btnClose.UseVisualStyleBackColor = true;
            this.btnClose.Click += new System.EventHandler(this.btnClose_Click);
            // 
            // txtChuteNo
            // 
            this.txtChuteNo.Location = new System.Drawing.Point(394, 119);
            this.txtChuteNo.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.txtChuteNo.Name = "txtChuteNo";
            this.txtChuteNo.Size = new System.Drawing.Size(250, 22);
            this.txtChuteNo.TabIndex = 6;
            // 
            // txtRouteNo
            // 
            this.txtRouteNo.Location = new System.Drawing.Point(394, 192);
            this.txtRouteNo.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.txtRouteNo.Name = "txtRouteNo";
            this.txtRouteNo.Size = new System.Drawing.Size(250, 22);
            this.txtRouteNo.TabIndex = 7;
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(321, 123);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(36, 16);
            this.label1.TabIndex = 8;
            this.label1.Text = "Chute";
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(321, 196);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(36, 16);
            this.label2.TabIndex = 9;
            this.label2.Text = "Route";
            // 
            // RouteConfigFrm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 16F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(877, 489);
            this.Controls.Add(this.label2);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.txtRouteNo);
            this.Controls.Add(this.txtChuteNo);
            this.Controls.Add(this.btnClose);
            this.Controls.Add(this.btnSave);
            this.Controls.Add(this.btnRemove);
            this.Controls.Add(this.btnAdd);
            this.Controls.Add(this.cbShift);
            this.Controls.Add(this.dgShift);
            this.Font = new System.Drawing.Font("Arial Narrow", 9.75F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.Name = "RouteConfigFrm";
            this.Text = "Route Chute Configation";
            this.Load += new System.EventHandler(this.RouteConfigFrm_Load);
            ((System.ComponentModel.ISupportInitialize)(this.dgShift)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.DataGridView dgShift;
        private System.Windows.Forms.ComboBox cbShift;
        private System.Windows.Forms.Button btnAdd;
        private System.Windows.Forms.Button btnRemove;
        private System.Windows.Forms.Button btnSave;
        private System.Windows.Forms.Button btnClose;
        private System.Windows.Forms.TextBox txtChuteNo;
        private System.Windows.Forms.TextBox txtRouteNo;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Label label2;
    }
}