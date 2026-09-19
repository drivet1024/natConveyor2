using Nat.Dal;
using Nat.Dal.DAL;
using Nat.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nat.Utils;
using Nat_Conveyor.Properties;
using Nat.Toolkit;
using Nat.Dal.Config;
using Autofac;
using static System.Windows.Forms.AxHost;

namespace Nat_Conveyor
{
    public class ConveyorStats
    {
       // private static ConveyorStats instance;

        public DateTime LastHeartBeat { get; set; }
        public DateTime LastHistory { get; set; }
        public DateTime LastUpdate { get; set; }

        public ConveyorStatsCounter Csc = new ConveyorStatsCounter();
        public ConveyorConnectionStatus Ccs = new ConveyorConnectionStatus();


        //public static ConveyorStats Instance
        //{
        //    get
        //    {
        //        if (instance == null) instance = new ConveyorStats();
        //        return instance;
        //    }

        //}
        private IDALConveyorStatsCounter DALConveyorStatsCounter { get; set; }
        private IDALCounter DALCounter { get; set; }
        private IDALConveyorStatsChute DALConveyorStatsChute { get; set; }
        private void ResolveDependencies()
        {
            DALConveyorStatsCounter = ContainerConfig.Scope.Resolve<IDALConveyorStatsCounter>();
            DALCounter = ContainerConfig.Scope.Resolve<IDALCounter>();
            DALConveyorStatsChute = ContainerConfig.Scope.Resolve<IDALConveyorStatsChute>();
        }

        public ConveyorStats()
        {
            ResolveDependencies();

            Csc = DALConveyorStatsCounter.Get(Properties.Settings.Default.depotID, Properties.Settings.Default.lineID);
            
            Logger.Info("Get stats :" + Csc.totalParcelFromCamera + " "+ Csc.lastUpdate.Value.ToQuotedMysqlDateTimeString());
        }

        public void UpdateBD(int hour)
        {
           // Logger.Info("update conveeyor status "+ Properties.Settings.Default.lineID);
            DALConveyorStatsCounter.InsertUpdate(Properties.Settings.Default.depotID, Properties.Settings.Default.lineID, Csc, Ccs, hour);
        }

        public void EndOfDay(int hour)
        {
            try
            {

                DDEInterface DDe = new DDEInterface();
                DDe.Connect();
                Logger.Info("EOD done... M13A: " + Csc.MotorM13A);

               

                if (Settings.Default.Conveyor_version == "V2")
                {
                    Csc.totalParcelConveyor = DDe.Request("Program:MainProgram.TOTAL_CONVOYE.ACC,L1,C1").ToIntNoException();
                    Csc.RejectedConveyor = DDe.Request("Program:MainProgram.TOTAL_CHUTE_PLEINE.ACC,L1,C1").ToIntNoException();
                    Csc.Code42Conveyor = DDe.Request("Program:MainProgram.TOTAL_CODE_42.ACC,L1,C1").ToIntNoException();
                    Csc.Code98Conveyor = DDe.Request("Program:MainProgram.NOMBRE_DE_CODE_98.ACC,L1,C1").ToIntNoException();
                    Csc.PercentageCode68 = DDe.Request(string.Format("Program:MainProgram.POURCENTAGE_68,L1,C1")).ToDoubleNoException();
                    //Csc.ConveyorParcelSpacing = DALCounter.GetConveyorParcelSpacing();
                    //Csc.stoppedRampElapsedTime0 = DDe.Request("C5:34.ACC").ToIntNoException();
                    //Csc.stoppedRampElapsedTime1 = DDe.Request("C5:5.ACC").ToIntNoException();
                }

                
                DALConveyorStatsCounter.InsertUpdate(Properties.Settings.Default.depotID, Properties.Settings.Default.lineID, Csc, Ccs, hour);

                //DALConveyorStatsCounter.InsertHistory(Properties.Settings.Default.depotID, Properties.Settings.Default.lineID, Csc);

                Csc = new ConveyorStatsCounter();

                DALConveyorStatsCounter.InsertUpdate(Properties.Settings.Default.depotID, Properties.Settings.Default.lineID, Csc, Ccs, hour);


                //if (Properties.Settings.Default.lineID == 100)
                //{
                //    Logger.Info("EOD done chute...");
                //    for (int i = 1; i <= 41; i++)
                //    {
                //        ConveyorStatsChute chute = new ConveyorStatsChute();
                //        chute.DepotID = Properties.Settings.Default.depotID;
                //        chute.UpdateDate = DateTime.Now.Date;
                //        chute.ChuteNo = i;
                //        chute.NbParcel = DDe.Request(string.Format("Program:MainProgram.NOMBRE_DE_COLIS_CHUTE_{0}.ACC,L1,C1", i.ToString())).ToIntNoException();
                //        chute.nbRejected = DDe.Request(string.Format("Program:MainProgram.NOMBRE_DE_COLIS_CHUTE_PLEINE_{0}.ACC,L1,C1", i.ToString())).ToIntNoException();

                //        //DALConveyorStatsChute.Insert(chute);
                //    }
                   
                //}
            }
            catch (Exception ex)
            {
                Logger.Warn("error eod",ex);
            }

        }

        public void ResetStat(int hour)
        {
            Csc = new ConveyorStatsCounter();

            DALConveyorStatsCounter.InsertUpdate(Properties.Settings.Default.depotID, Properties.Settings.Default.lineID, Csc, Ccs, hour);
          
           // Csc = DALConveyorStatsCounter.Get(Properties.Settings.Default.depotID, Properties.Settings.Default.lineID);
            
            Logger.Info("ResetStats");
        }
    }
}
