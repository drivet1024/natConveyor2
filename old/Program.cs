using Nat.Dal;
using Nat.Dal.Config;
using Nat.Dal.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Nat_Conveyor
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
#if MYSQL_DEV
            DBConnection.Instance.SetServer(Settings.Default.MysqlServerDEV_ip);
#endif
            using (ContainerConfig.Scope = ContainerConfig.Configure().BeginLifetimeScope())
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Form1());
            }
        }
    }
}
