using Autofac;
using Nat.Dal;
using Nat.Dal.Config;
using Nat.Dal.DAL;
using Nat.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Nat_Conveyor
{
    public partial class DDEFrm : Form
    {
        bool chuteOpened = true;
        bool mainWorking = true;
        bool activeNew = false;
        public DDEInterface dde = null;
        public DDEFrm()
        {
            ResolveDependencies();
            InitializeComponent();
        }

        private IDALLocation DALLocation { get; set; }
        private void ResolveDependencies()
        {
            DALLocation = ContainerConfig.Scope.Resolve<IDALLocation>();
        }

        private void DDEFrm_Load(object sender, EventArgs e)
        {
           
            string ret = dde.Request("B3:76/0");
            
            if (ret.Contains("1")) chuteOpened = false;
            else chuteOpened = true;

            string mainret = dde.Request("START");
         
            if (mainret.Contains("0")) mainWorking = false;
            else mainWorking = true;

            ret = dde.Request("B3:135/0");

            if (ret.Contains("1")) activeNew = false;
            else activeNew = true;

            UpdateButton();
            //btnMainConv.Text = mainret;
        }
        private void UpdateButton()
        {
            if (chuteOpened)
            {
                btnChute.Text = "Chute open";
                btnChute.ForeColor = Color.Green;
            }
            else
            {
                btnChute.Text = "Chute close";
                btnChute.ForeColor = Color.DarkRed;
            }
            if (mainWorking)
            {
                btnMainConv.Text = "Running";
                btnMainConv.ForeColor = Color.Green;
            }
            else
            {
                btnMainConv.Text = "Stop";
                btnMainConv.ForeColor = Color.DarkRed;
            }
            if (activeNew)
            {
                btnActiveNew.Text = "Active";
                btnActiveNew.ForeColor = Color.Green;
            }
            else
            {
                btnActiveNew.Text = "Non Active";
                btnActiveNew.ForeColor = Color.DarkRed;

            }
        }

        private void btnChute_Click(object sender, EventArgs e)
        {
            if (chuteOpened)
            {
                chuteOpened = false;
                dde.Poke("B3:76/0", "1");
            }
            else
            {
                dde.Poke("B3:76/0", "0");
                chuteOpened = true;
            }

            UpdateButton();
        }

        private void button2_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void btnMainConv_Click(object sender, EventArgs e)
        {
            if (mainWorking)
            {
                mainWorking = false;
                dde.Poke("START", "0");
            }
            else
            {
                dde.Poke("START", "1");
                mainWorking = true;
            }

            UpdateButton();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            if (activeNew)
            {
                activeNew = false;
                dde.Poke("B3:135/0", "1");
            }
            else
            {
                dde.Poke("B3:135/0", "0");
                activeNew = true;
            }

            UpdateButton();
        }

        private void button1_Click_1(object sender, EventArgs e)
        {
            var dt = DBLocalConnection.Instance.FillDT(string.Format("select * from aldo_store"));

            try
            {
                foreach (DataRow dr in dt.Rows)
                {
                    var loc = DALLocation.GetLocations(StrUtils.TrimPostalCode(dr["postal_code"].ToString()));
                    var l = loc.FirstOrDefault().ChuteNo;

                    DBLocalConnection.Instance.ExecuteCommand(string.Format("update aldo_store set chute_no={0} where stores={1}", l, dr["stores"].ToString()));
                }
            }
            catch
            { }
        }
    }
}
