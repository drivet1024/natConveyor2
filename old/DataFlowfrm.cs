using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Nat_Conveyor
{
    public delegate void DoUpdate(string _cam, string _dim, string _scale);

    public partial class DataFlowfrm : Form
    {
        public event DoUpdate OnUpdate;

        public DataFlowfrm()
        {
            InitializeComponent();


        }

        public void UpdatePingCam(string _cam)
        {
            if (this.Visible)
            {
                txtPingCam.AppendText(_cam + "\n");
            }
        }
        public void UpdatePingDim(string _cam)
        {
            if (this.Visible)
            {
                txtPingDim.AppendText(_cam + "\n");
            }
        }
        public void UpdatePingScale(string _cam)
        {
            if (this.Visible)
            {
                txtPingScale.AppendText(_cam + "\n");
            }
        }

        public void Update(string _cam, string _dim, string _scale)
        {
            if (this.Visible)
            {
                txtCameraStatus.AppendText(_cam + "\n");
                txtDimensionStatus.AppendText(_dim + "\n");
                txtScaleStatus.AppendText(_scale + "\n");
            }
        }

        private void button2_Click(object sender, EventArgs e)
        {
            this.Hide();
            
        }

        private void button1_Click(object sender, EventArgs e)
        {
            txtCameraStatus.Text = "";
            txtDimensionStatus.Text = "";
            txtScaleStatus.Text = "";

            File.Delete("camera.log");
            File.Delete("scale.log");
            File.Delete("dimension.log");
            File.Delete("rejected.log");



        }
    }
}
