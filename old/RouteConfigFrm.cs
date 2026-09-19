using Nat.Dal.Config;
using Nat.Dal.DAL;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Autofac;
using Nat.Utils;
using Nat_Conveyor.Properties;

namespace Nat_Conveyor
{
    public partial class RouteConfigFrm : Form
    {
        private IDALConveyorShift DALConveyorShift { get; set; }
        public Form1 frm;

        public RouteConfigFrm()
        {
            DALConveyorShift = ContainerConfig.Scope.Resolve<IDALConveyorShift>();

            InitializeComponent();
        }

        private void RouteConfigFrm_Load(object sender, EventArgs e)
        {
            var shifts = DALConveyorShift.GetAll();

            foreach (var shift in shifts)
            {
                cbShift.Items.Add(shift.ConveyorShiftID.ToString() + " - " + shift.Name);
            }

            
        }

        private void LoadDataGrid()
        {
            return;
           /* if (cbShift.Text != "")
                dgShift.DataSource = DALConveyorShift.GetChutesListForShift(cbShift.Text.Substring(0, 1).ToIntNoException());*/
        }

        private void cbShift_SelectedIndexChanged(object sender, EventArgs e)
        {
            LoadDataGrid();
        }

        private void dgShift_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            txtChuteNo.Enabled = false;
            if (dgShift.Rows[0].Cells[0].Value.ToString() != "")
            {
                txtChuteNo.Text = dgShift.Rows[e.RowIndex].Cells[0].Value.ToString();
                txtRouteNo.Text = dgShift.Rows[e.RowIndex].Cells[1].Value.ToString();
            }
        }

        private void btnRemove_Click(object sender, EventArgs e)
        {
            return;

            if (dgShift.SelectedRows.Count > 0)
            {
                if (MessageBox.Show("Do you really want to remove the chute/route "  + dgShift.SelectedRows[0].Cells[0].Value.ToString() + " ?", "Warning !", MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    //DALConveyorShift.RemoveChute(cbShift.Text.Substring(0, 1).ToIntNoException(), dgShift.SelectedRows[0].Cells[0].Value.ToIntNoException(), dgShift.SelectedRows[0].Cells[1].Value.ToIntNoException());
                    LoadDataGrid();
                    //dgShift.SelectedRows[0].Cells[0]
                }
            }
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            return;

           /* if (txtChuteNo.Text != "")
            {
                if (dgShift.SelectedRows != null && dgShift.SelectedRows.Count ==1)
                    DALConveyorShift.RemoveChute(cbShift.Text.Substring(0, 1).ToIntNoException(), dgShift.SelectedRows[0].Cells[0].Value.ToIntNoException(), dgShift.SelectedRows[0].Cells[1].Value.ToIntNoException());
                DALConveyorShift.ModifyChute(Settings.Default.depotID, cbShift.Text.Substring(0, 1).ToIntNoException(), txtChuteNo.Text.ToIntNoException(),txtRouteNo.Text.ToIntNoException());
                txtChuteNo.Enabled = false;
                LoadDataGrid();
            }
            txtChuteNo.Enabled = true;
            txtChuteNo.Text = "";
            txtRouteNo.Text = "";
            dgShift.ClearSelection();*/

        }

        private void btnAdd_Click(object sender, EventArgs e)
        {
            txtChuteNo.Enabled = true;
            txtChuteNo.Text = "";
            txtRouteNo.Text = "";
            dgShift.ClearSelection();
        }

        private void dgShift_CellClick(object sender, DataGridViewCellEventArgs e)
        {
            txtChuteNo.Enabled = false;
            if (dgShift.Rows[e.RowIndex].Cells[0].Value.ToString() != "")
            {
                txtChuteNo.Text = dgShift.Rows[e.RowIndex].Cells[0].Value.ToString();
                txtRouteNo.Text = dgShift.Rows[e.RowIndex].Cells[1].Value.ToString();
            }
        }

        private void btnClose_Click(object sender, EventArgs e)
        {
            Close();
        }


    }
}
