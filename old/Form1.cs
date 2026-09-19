using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Nat.Utils;
using Nat.Dal;
using System.Data.SqlTypes;
using System.Data.SQLite;
using System.IO;
using Nat.Objects;
using Nat.Dal.DAL;
using System.Threading;
using System.Runtime.Serialization.Formatters.Binary;
using System.Diagnostics;
using System.Globalization;
using Nat_Conveyor.Properties;
using System.Net.NetworkInformation;
using Nat.Dal.NAT;
using Nat.Toolkit;
using Nat.Dal.Config;
using Autofac;
using Org.BouncyCastle.Utilities.Net;
using System.Diagnostics.Eventing.Reader;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.ProgressBar;

namespace Nat_Conveyor
{
    public partial class Form1 : Form
    {
        DateTime dtElapsedTime = new DateTime();
        DateTime dtLastCameraTime = new DateTime();
        DateTime dtLastDimTime = new DateTime();
        System.Windows.Forms.Timer tElapsedTime = new System.Windows.Forms.Timer();
        System.Windows.Forms.Timer tConveyorChuteTimer = new System.Windows.Forms.Timer();
        BackgroundWorker bgCamera = new BackgroundWorker();
        BackgroundWorker bgScale = new BackgroundWorker();
        BackgroundWorker bgDimension = new BackgroundWorker();
        BackgroundWorker bgData = new BackgroundWorker();
        BackgroundWorker bgDDEReceived = new BackgroundWorker();
        BackgroundWorker bgStatChute = new BackgroundWorker();
        BackgroundWorker bgConveyorStats = new BackgroundWorker();

        TcpListener ScaleTCPserver = null;
        TcpClient ScaleTCPClient = null;
        TcpListener DimTCPserver = null;
        TcpClient DimTCPClient = null;

        string dimensionServerAddr = "";
        string scaleServerAddr = "";
        string cameraServerAddr = "";

        string DimCurrentData = "";

        PingReply pingCamera = null;
        PingReply pingDimension = null;
        PingReply pingScale = null;


        DateTime lastSync = new DateTime();
        DataFlowfrm dataFlowFrm = new DataFlowfrm();
        List<ScanNoData> scanNoData = new List<ScanNoData>();
        List<ScanNoData> scanNoDimScaleData = new List<ScanNoData>();
        List<ScanNoData> allScan = new List<ScanNoData>();
        List<ConveyorShift> allShift = new List<ConveyorShift>();

        DDEInterface DDe = new DDEInterface();

        ConveyorStats gConveyorStats = null;// new ConveyorStats();

        List<ShortShipping> waybillList = new List<ShortShipping>();

        public int delay = 0;

        int doubleRead = 0;

        DateTime dtLastReset = DateTime.Now;

        bool manualPostalCode = false;

        int rejectedChute = Settings.Default.RejectedChute;

        string depotName = "";

        private IDALConveyorStatsChute DALConveyorStatsChute { get; set; }
        private IDALDestination DALDestination { get; set; }
        private IDALReportSender DALReportSender { get; set; }
        private IDALConveyorWBs DALConveyorWBs { get; set; }
        private IDALShipping DALShipping { get; set; }
        private IDALConveyorShift DALConveyorShift { get; set; }
        private IDALSMSSender DALSMSSender { get; set; }


        private void ResolveDependencies()
        {
            DALConveyorStatsChute = ContainerConfig.Scope.Resolve<IDALConveyorStatsChute>();
            DALDestination = ContainerConfig.Scope.Resolve<IDALDestination>();
            DALReportSender = ContainerConfig.Scope.Resolve<IDALReportSender>();
            DALConveyorWBs = ContainerConfig.Scope.Resolve<IDALConveyorWBs>();
            DALShipping = ContainerConfig.Scope.Resolve<IDALShipping>();
            DALConveyorShift = ContainerConfig.Scope.Resolve<IDALConveyorShift>();
            DALSMSSender = ContainerConfig.Scope.Resolve<IDALSMSSender>();
        }

        public Form1()
        {
            ResolveDependencies();
            InitializeComponent();

            CultureInfo newCulture = CultureInfo.CreateSpecificCulture("en-us");

            newCulture.DateTimeFormat.ShortDatePattern = "yyyy-MM-dd";
            newCulture.DateTimeFormat.DateSeparator = "-";

            newCulture.NumberFormat.NumberDecimalSeparator = ".";
            Thread.CurrentThread.CurrentCulture = newCulture;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            //button1.Visible = false;
            //button4.Visible = false;
            if (Settings.Default.depotID != 2)
               button3.Visible = false;

            Logger.ConfigLogger();

            if (!Directory.Exists("log_txt"))
                Directory.CreateDirectory("log_txt");

            Process[] localByName = Process.GetProcessesByName("Nat_Conveyor");
            int i = 0;
            foreach (var proc in localByName)
            {
                if (proc.MainModule.FileName.Contains("_" + Properties.Settings.Default.lineID.ToString()))
                {
                    i++;
                }
                if (i > 1)
                {
                    MessageBox.Show("Process already running !" + proc.MainModule.FileName);
                    Application.Exit();
                    
                }
            }

            DrawStatus();

            Text = "Nationex v." + Application.ProductVersion + " " + (Properties.Settings.Default.master == true ? "Master" : "Slave");
            lblLine.Text = "Line :" + Properties.Settings.Default.lineID.ToString();

            if (Properties.Settings.Default.lineID == 0)
            {
                Left = 0;
                Top = 0;
            }
            else
            {
                Left = this.Width;
                Top = 0;
            }


            InitBackgroundWorker();

            LoadShift();

            gConveyorStats = new ConveyorStats();


            tElapsedTime.Start();

            bgData.RunWorkerAsync();
            bgScale.RunWorkerAsync();
            bgDimension.RunWorkerAsync();

            bgDDEReceived.RunWorkerAsync();

            //bgStatChute.RunWorkerAsync();


            btnNoDimScaleEnable.ForeColor = Color.DarkRed;
            btnNoDimScaleEnable_Click(null, null);

            Thread.Sleep(10);

             bgCamera.RunWorkerAsync();

            tConveyorChuteTimer.Start();
            
            delay = Settings.Default.GlobalDelay;
            
            if (Properties.Settings.Default.Disable98Code)
                btnNoDimScaleEnable_Click(null, null);

            bgConveyorStats.RunWorkerAsync();
            
            gConveyorStats.Ccs.SQLConnected = true;

            if (Settings.Default.CurrentShift == 9)
                rejectedChute = Settings.Default.NoRead;
            else rejectedChute = Settings.Default.RejectedChute;


            if (Settings.Default.depotID == 1)
                depotName = "STH";
            else if (Settings.Default.depotID == 2)
                depotName = "QC";
            if (Settings.Default.depotID == 12)
                depotName = "TOR";

            if (Settings.Default.boa == true)
            {
                Settings.Default.small_parcel = 0;
                ResetChuteToBoa();
                Logger.Info("small parcel :" + Settings.Default.small_parcel);
            }

        }


        private void TElapsedTime_Tick(object sender, EventArgs e)
        {
            DrawStatus();

            lblElapsedTime.Text = new DateTime((DateTime.Now - dtElapsedTime).Ticks).ToString("dd.HH:mm:ss");
            lblCurrentDate.Text = DateTime.Now.ToString("HH:mm:ss");

            if (!bgCamera.IsBusy)
                bgCamera.RunWorkerAsync();

            if (!bgScale.IsBusy)
                bgScale.RunWorkerAsync();

            if (!bgDimension.IsBusy)
                bgDimension.RunWorkerAsync();

            lblTotParcel.Text = gConveyorStats.Csc.totalParcel.ToString();

            lblCameraTotal.Text = gConveyorStats.Csc.totalParcelFromCamera.ToString();
            lblScaleTotal.Text = gConveyorStats.Csc.totalParcelFromScale.ToString();
            lblDimensionTotal.Text = gConveyorStats.Csc.totalParcelFromDimension.ToString();
            lblDBShip.Text = gConveyorStats.Csc.totDBShip.ToString();
            lblDBPostal.Text = gConveyorStats.Csc.totDBPostal.ToString();
            lblRejected.Text = gConveyorStats.Csc.totRejected.ToString();
            lblScaleError.Text = gConveyorStats.Csc.totScaleError.ToString();
            lblDimError.Text = gConveyorStats.Csc.totDimensionerError.ToString();
            if (gConveyorStats.Csc.totalParcel > 0)
            {
                lblDimErrPourc.Text = Math.Round(((double)(gConveyorStats.Csc.totDimensionerError / gConveyorStats.Csc.totalParcel) * 100), 1).ToString();
                lblScaleErrPourc.Text = Math.Round(((double)(gConveyorStats.Csc.totScaleError / gConveyorStats.Csc.totalParcel) * 100), 1).ToString();
            }
            lblTotInsert.Text = gConveyorStats.Csc.totDBInsert.ToString();
            lblTotRemoteDB.Text = gConveyorStats.Csc.totRemoteInsert.ToString();


            lblTotalSortWB.Text = gConveyorStats.Csc.totSortWaybill.ToString();
            lblTotalSortPostalCode.Text = gConveyorStats.Csc.totSortPostalCode.ToString();
            //lblTotRecycling.Text = (allScan.Sum(p => p.Count) - allScan.Count).ToString();
            lblNoRead.Text = gConveyorStats.Csc.noRead.ToString();
            lblTotSortPostalWOWaybill.Text = gConveyorStats.Csc.NbSortByPostalcodeWithoutWb.ToString();

            lblTotScaleErr.Text = gConveyorStats.Csc.totContScaleError.ToString();

            lblParcelPerMin.Text = gConveyorStats.Csc.parcelPerMin.ToString();
            //txtError.Text = gConveyorStats.Ccs.error;

            UpdateDBScan();
            //DALServiceMonitoring.SendHeartBeat("Nat_conveyor", Application.ProductVersion);
            //CheckSQLStatus();

            gConveyorStats.Ccs.DDEConnected = DDe.IsDDeConnected();
            
            if (!gConveyorStats.Ccs.DDEConnected)
                DDe.Connect();

            ManageDDEConnection();

            if (DateTime.Now.Second == 0 && Properties.Settings.Default.lineID == 0)
            {
                try
                {
                    gConveyorStats.Csc.SortTimeConveyor++;

                        if (DDe.Request("Program:MainProgram.VITESSE_M13A,L1,C1").ToIntNoException() > 0)
                        {
                            ConveyorStatsDDE statDDE = new ConveyorStatsDDE();
                            statDDE.DepotID = Properties.Settings.Default.depotID;
                            statDDE.InsertDate = DateTime.Now;

                        //TOTAL_TRI
                        //TOTAL_CONVOYE.ACC
                            statDDE.NbParcelConveyed = DDe.Request(string.Format("Program:MainProgram.TOTAL_CONVOYE.ACC,L1,C1")).ToIntNoException();
                            statDDE.NbParcelSorted = DDe.Request(string.Format("Program:MainProgram.TOTAL_TRI.ACC,L1,C1")).ToIntNoException();
                            statDDE.nbRejected = DDe.Request(string.Format("Program:MainProgram.NOMBRE_DE_COLIS_CHUTE_16.ACC,L1,C1")).ToIntNoException();
                            statDDE.PercentageFullChute = DDe.Request(string.Format("Program:MainProgram.POURCENTAGE_CHUTE_PLEINE,L1,C1")).ToDoubleNoException();
                            statDDE.PercentageCode98 = DDe.Request(string.Format("Program:MainProgram.POURCENTAGE_98,L1,C1")).ToDoubleNoException();
                            statDDE.PercentageCode42 = DDe.Request(string.Format("Program:MainProgram.POURCENTAGE_42,L1,C1")).ToDoubleNoException();
                            statDDE.PercentageCode68 = DDe.Request(string.Format("Program:MainProgram.POURCENTAGE_68,L1,C1")).ToDoubleNoException();

                            //Logger.Info(statDDE.PercentageFullChute);

                         /*   DBLocalConnection.Instance.ExecuteCommand(string.Format("insert into conveyor_stats_dde (DEPOT_ID,PC_FULLCHUTE,PC_CODE98,PC_CODE68,PC_CODE42,NB_SORTED, NB_SCANNED,NB_REJECTED,NB_RECYCLED,PC_REJECTED,PC_RECYCLED,INSERT_DATE, NB_SCALE_ERR,NB_DIM_ERR,LINE_ID,CODE98_ENABLED) " +
                                "values({0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14})",
                                Properties.Settings.Default.depotID,
                                Math.Round(statDDE.PercentageFullChute,1),
                                Math.Round(statDDE.PercentageCode98,1),
                                Math.Round(statDDE.PercentageCode68,1),
                                Math.Round(statDDE.PercentageCode42,1),
                                statDDE.NbParcelSorted,
                                statDDE.NbParcelConveyed,
                                statDDE.nbRejected,
                                statDDE.nbRecycled,
                                Math.Round(statDDE.PercentageRejected,1),
                                Math.Round(statDDE.PercentageRecycled,1),
                                DateTime.Now.ToQuotedMysqlDateTimeString(),
                                gConveyorStats.Csc.totScaleError,
                                gConveyorStats.Csc.totDimensionerError,
                                Settings.Default.lineID,
                                gConveyorStats.Ccs.NoDimScaleEnable
                                ));
                         */
                         
                        }
                    
                }
                catch { }
            }

            if (!manualPostalCode)
            {
                TimeSpan ts = new TimeSpan(Properties.Settings.Default.PostalCodeSoftEnable.Split(':')[0].ToIntNoException(),
                    Properties.Settings.Default.PostalCodeSoftEnable.Split(':')[1].ToIntNoException(),
                    Properties.Settings.Default.PostalCodeSoftEnable.Split(':')[2].ToIntNoException());
                TimeSpan ts2 = new TimeSpan(Properties.Settings.Default.PostalCodeSoftDisable.Split(':')[0].ToIntNoException(),
                    Properties.Settings.Default.PostalCodeSoftDisable.Split(':')[1].ToIntNoException(),
                    Properties.Settings.Default.PostalCodeSoftDisable.Split(':')[2].ToIntNoException());

                if (DateTime.Now.TimeOfDay > ts && DateTime.Now.TimeOfDay < ts2)
                {
                    Properties.Settings.Default.postalCodeSort = true;
                }
                else if (DateTime.Now.TimeOfDay > new TimeSpan(9, 0, 0))
                    Properties.Settings.Default.postalCodeSort = false;
            }
            
        }


        private void InitBackgroundWorker() 
        {
            dtElapsedTime = DateTime.Now;
            tElapsedTime.Tick += TElapsedTime_Tick;
            tElapsedTime.Interval = 1000;

            bgScale.WorkerSupportsCancellation = true;
            bgScale.WorkerReportsProgress = true;
            bgScale.ProgressChanged += BgScale_ProgressChanged;
            bgScale.DoWork += BgScale_DoWork;

            bgDimension.WorkerSupportsCancellation = true;
            bgDimension.WorkerReportsProgress = true;


            if (Settings.Default.NewDimensionner == false)
                bgDimension.DoWork += BgDimension_DoWork_old;
            else
                bgDimension.DoWork += BgDimension_DoWork_server;


            bgDimension.ProgressChanged += BgDimension_ProgressChanged;

            bgCamera.WorkerSupportsCancellation = true;
            bgCamera.WorkerReportsProgress = true;
            bgCamera.DoWork += BgCamera_DoWork;
            bgCamera.ProgressChanged += BgCamera_ProgressChanged;

            
            bgData.WorkerSupportsCancellation = true;
            bgData.WorkerReportsProgress = true;
            bgData.DoWork += BgData_DoWork;

            
            bgDDEReceived.WorkerReportsProgress = true;
            bgDDEReceived.DoWork += BgDDEReceived_DoWork;

            bgConveyorStats.DoWork += BgConveyortats_DoWork;
            bgConveyorStats.WorkerSupportsCancellation = true;
        }

        private void BgConveyortats_DoWork(object sender, DoWorkEventArgs e)
        {
            Logger.Info("Conveyor Stat eonfDay hour: " + TimeSpan.Parse(Properties.Settings.Default.EodTime).Hours);

            while (true)
            {
                try
                {
                    
                    gConveyorStats.UpdateBD(TimeSpan.Parse(Properties.Settings.Default.EodTime).Hours);
                    
                    if (DateTime.Now.ToString("HH:mm:ss") == Properties.Settings.Default.EodTime)
                    {
                        manualPostalCode = false;

                        Logger.Info("Reset data for endofDay !");

                        gConveyorStats.EndOfDay(TimeSpan.Parse(Properties.Settings.Default.EodTime).Hours);
                        ResetLocalDB();

                        if (!Properties.Settings.Default.Disable98Code)
                        {
                            btnNoDimScaleEnable.ForeColor = Color.DarkGreen;

                            gConveyorStats.Ccs.NoDimScaleEnable = Properties.Settings.Default.Disable98Code;
                        }
                    }
                    
                    //if (DateTime.Now.Second == 0 && DDe.Request("B3:271/0").ToIntNoException() == 1)
                    //{
                    //    Thread.Sleep(2000); // 1 sec.
                    //    DDe.Poke("B3:271/0", "0");
                        
                    //    gConveyorStats.ResetStat();
                    //}
                }
                catch (Exception ex)
                {
                    Logger.Error("error BgConveyortats_DoWork",ex);
                }

                Thread.Sleep(1000); // 1 sec.
                if (e.Cancel)
                    break;
            }
            
        }
        
        private void BgDDEReceived_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                string[] t = Settings.Default.NoExtStatsStart.Split(':');
                string[] tEnd = Settings.Default.NoExtStatsEnd.Split(':');

                TimeSpan ts = new TimeSpan(t[0].ToIntNoException(), t[1].ToIntNoException(), t[2].ToIntNoException());
                TimeSpan tsEnd = new TimeSpan(tEnd[0].ToIntNoException(), tEnd[1].ToIntNoException(), tEnd[2].ToIntNoException());

                if (Properties.Settings.Default.lineID == 0)
                {
                    while (true)
                    {
                        if (DateTime.Now.Hour >= ts.Hours && DateTime.Now.Hour <= tsEnd.Hours)
                        {
                            Logger.Debug("DDe.Poke(B3: 39 / 0 : 0");
                            DDe.Poke("B3:39/0", "0");
                        }
                        else
                        {
                            DDe.Poke("B3:39/0", "1");
                            Logger.Debug("DDe.Poke(B3: 39 / 0 : 1");
                        }

                        Thread.Sleep(60 * 1000); // sleep 1 min.
                    }
                }
            }
            catch
            {
                Logger.Warn("exception DDe.Poke(B3: 39 / 0");
            }
        }

        private void BgCamera_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            string data = e.UserState as string;

            if ((DateTime.Now - dtLastCameraTime).TotalMilliseconds < 250)
            {
                Logger.Info($"Received cam date twice : [{(DateTime.Now - dtLastCameraTime).TotalMilliseconds}] [{dtLastCameraTime.ToString("yyyyMMdd HH:mm:ss.fff")}]  [{data}]");
                return;
            }


            dtLastCameraTime = DateTime.Now;

            gConveyorStats.Csc.parcelPerMin = Math.Round(60000 / (DateTime.Now - gConveyorStats.Ccs.dtCamera).TotalMilliseconds,1);

            gConveyorStats.Ccs.lastCameraData = data;
            txtLastCamera.Text = data;
            gConveyorStats.Ccs.dtCamera = DateTime.Now;
            gConveyorStats.Csc.totalParcelFromCamera++;
            gConveyorStats.Csc.totalParcel++;

          
            TriggerGetChute();
            
            
        }


        private void BgCamera_DoWork(object sender, DoWorkEventArgs e)
        {
            TcpListener server = null;
            Dimension currentDimension = new Dimension();
            string currentData = "";
            BackgroundWorker worker = sender as BackgroundWorker;
            

            try
            {
                int port = Properties.Settings.Default.camera_port;

                
                while (true)
                {
                    System.Net.IPAddress localAddr = System.Net.IPAddress.Parse("127.0.0.1");

                    server = new TcpListener(port);

                    server.Start();

                    // Buffer for reading data
                    Byte[] bytes = new Byte[256];
                    String data = null;
                    TcpClient client = server.AcceptTcpClient();
                    cameraServerAddr = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();

                    data = null;
                    gConveyorStats.Ccs.CameraTCPConnected = true;
                    client.ReceiveTimeout = Properties.Settings.Default.ReadTimeout; // default 10 sec 
                                                   
                    NetworkStream stream = client.GetStream();
                    // Enter the listening loop.

                    try
                    {
                        int i = 0;
                        currentData = "";
                        string subData = "";
                        string lastData = "";
                        while ((i = stream.Read(bytes, 0, bytes.Length)) != 0)
                        {
                            // Translate data bytes to a ASCII string.
                            data = System.Text.Encoding.ASCII.GetString(bytes, 0, i);

                            AppendLog("log_txt\\camera_in_"+ DateTime.Now .ToString("yyyy-MM-dd")+ ".log", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " "+ gConveyorStats.Ccs.dtCamera.ToString("HH:mm:ss.fff") + " " +  data);

                            // Process the data sent by the client.
                            data = data.ToUpper();
                            Console.WriteLine(data);
                            byte[] msg = System.Text.Encoding.ASCII.GetBytes(data);

                            currentData += data;

                            try
                            {
                                while (currentData.Contains("\r"))
                                {
                                    int start = 0;
                                    int end = currentData.IndexOf("\r");
                                    subData = currentData.Substring(start, end - start);
                                    currentData = (currentData.Length < end + 1) ? currentData.Substring(end + 1) : "";

                                    if (lastData == subData && gConveyorStats.Ccs.dtCamera.AddMilliseconds(10) > DateTime.Now)
                                    {
                                        doubleRead++;
                                        continue;
                                    }

                                    if (subData != "")
                                    {
                                        if (delay > 0)
                                            Thread.Sleep(delay);
                                        worker.ReportProgress(0, subData);
                                        
                                        AppendLog("log_txt\\camera_sent_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", gConveyorStats.Ccs.dtCamera.ToString("HH:mm:ss.fff") + " " + DateTime.Now.ToString("HH:mm:ss.fff") + " " + subData +"\r\n");
                                    }
                                    
                                    lastData = subData;
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn("BgCamera_DoWork: " + ex.Message);
                                gConveyorStats.Ccs.error += ex.Message + "\n";
                            }
                        }
                    }
                    catch (System.IO.IOException exio)
                    {
                        gConveyorStats.Ccs.CameraTCPConnected = false;
                        stream.Close();
                        client.Close();
                        server.Stop();
                        Logger.Debug("Camera read timeout...");
                        // break;
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("Error BgCamera_DoWork: " + ex.Message,ex);
                        gConveyorStats.Ccs.error += ex.Message + "\n";

                        gConveyorStats.Ccs.CameraTCPConnected = false;
                        client.Close();
                        server.Stop();
                        break;
                        
                    }
                }
                Logger.Warn("Exit BgCamera_DoWork ");
                gConveyorStats.Ccs.CameraTCPConnected = false;
              
                server.Stop();
            }
            catch (Exception ex)
            {
                Logger.Warn("BgCamera_DoWork: " + ex.Message);

                gConveyorStats.Ccs.error += ex.Message + "\n";

                gConveyorStats.Ccs.CameraTCPConnected = false;
                
            }
        }

        private void BgDimension_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            Dimension data = new Dimension();

            if ((DateTime.Now - dtLastDimTime).TotalMilliseconds < 250)
            {
                txtLastDimensioner.Text = "DOUBLE DIM";
                data.stat = -1;
                data.L = -1;
                data.W = -1;
                data.H = -1;
                dtLastDimTime = DateTime.Now;
                gConveyorStats.Ccs.dtLastDimension = DateTime.Now;
                gConveyorStats.Ccs.lastDimension = data;
                return;
            }
            dtLastDimTime = DateTime.Now;

            try
            {
                txtLastDimensioner.Text = e.UserState.ToString();
                
                data.stat = e.UserState.ToString().Substring(0, 4).ToIntNoException();
                data.L = e.UserState.ToString().Substring(4, 4).ToDecimalNoException() / 10;
                data.W = e.UserState.ToString().Substring(8, 4).ToDecimalNoException() / 10;
                data.H = e.UserState.ToString().Substring(12, 4).ToDecimalNoException() / 10;
            }
            catch (Exception ex)
            {
                Logger.Warn("BgDimension_ProgressChanged: " + ex.Message);

                gConveyorStats.Ccs.error += ex.Message + "\n";

            }
            gConveyorStats.Ccs.lastDimension = data;

            gConveyorStats.Ccs.dtLastDimension = DateTime.Now;
            gConveyorStats.Csc.totalParcelFromDimension++;
            lblDimState.Text = (lblDimState.Text.ToIntNoException() + 1).ToString();

        }

        private void BgDimension_DoWork_old(object sender, DoWorkEventArgs e)
        {

            Dimension currentDimension = new Dimension();
            string currentData = "";
            BackgroundWorker worker = sender as BackgroundWorker;

            int port = Properties.Settings.Default.dimension_port;

            System.Net.IPAddress localAddr = System.Net.IPAddress.Parse("127.0.0.1");

            try
            {
                DimTCPserver = new TcpListener(port);

                // Enter the listening loop.
                while (true)
                {

                    NetworkStream stream = null;
                    try
                    {
                        Logger.Info("Starting dim old ...");


                        DimTCPserver.Start();

                    // Buffer for reading data
                    Byte[] bytes = new Byte[256];
                    String data = null;

                    DimTCPClient = DimTCPserver.AcceptTcpClient();

                    dimensionServerAddr = ((IPEndPoint)DimTCPClient.Client.RemoteEndPoint).Address.ToString();
                    Logger.Info("Dim converter connected..."+ dimensionServerAddr);


                        //  client.ReceiveTimeout = 60000;
                        data = null;
                    gConveyorStats.Ccs.DimensionTCPConnected = true;
                    // Get a stream object for reading and writing
                    stream = DimTCPClient.GetStream();
                    
                        int i = 0;
                        currentData = "";
                        string subData = "";
                        while ((i = stream.Read(bytes, 0, bytes.Length)) != 0)
                        {
                            // Translate data bytes to a ASCII string.
                            data = System.Text.Encoding.ASCII.GetString(bytes, 0, i);

                            // Process the data sent by the client.
                            data = data.Replace("Q00003", "");
       
                            currentData += data;
                            try
                            {

                                while (((currentData.Contains((char)3) && currentData.Contains((char)2))))
                                {
                                    int start = currentData.IndexOf((char)2);
                                    int end = currentData.IndexOf((char)3);
                                    if (end - start - 1 > 0)
                                        subData = currentData.Substring(start + 1, end - start - 1);
                                    else
                                        subData = "";

                                    currentData = (currentData.Length > (end + 1)) ? currentData.Substring(end + 1) : "";
                                    if (subData.Length > 4)
                                        AppendLog("log_txt\\dimension_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + data + "\r\n");

                                    if (subData.Length == 16)
                                    {
                                        if (gConveyorStats.Ccs.dimEnable)
                                            worker.ReportProgress(1, subData);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn("BgDimension_DoWork: " + ex.Message);

                                gConveyorStats.Ccs.error += ex.Message + "\n";
                            }

                        }
                    }
                    catch (System.IO.IOException exio)
                    {
                        Logger.Warn("BgDimension_DoWork: IOException : " + exio.Message);
                        gConveyorStats.Ccs.DimensionTCPConnected = false;

                        if (DimTCPClient != null)
                        {
                            DimTCPClient.Close();
                            DimTCPClient = null;
                        }

                    }
                    catch (SocketException se)
                    {
                        Logger.Warn("BgDimension_DoWork: SocketException : " + se.Message);
                        if ((se.SocketErrorCode == SocketError.Interrupted || se.SocketErrorCode == SocketError.AddressAlreadyInUse))
                        {
                            // a blocking listen has been cancelled
                            gConveyorStats.Ccs.DimensionTCPConnected = false;
                            if (DimTCPClient != null)
                            {
                                DimTCPClient.Close();
                                DimTCPClient = null;
                            }

                        }

                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("BgDimension_DoWork: " + ex.Message);

                        gConveyorStats.Ccs.error += ex.Message + "\n";
                        if (DimTCPClient != null)
                        {
                            DimTCPClient.Close();
                            DimTCPClient = null;
                        }
                        
                        DimTCPserver.Stop();
                        gConveyorStats.Ccs.DimensionTCPConnected = false;
                        break;
                    }


                }
            }
            catch (Exception ex)
            {
                if (DimTCPClient != null)
                {
                    DimTCPClient.Close();
                    DimTCPClient = null;
                }
                DimTCPserver.Stop();
                gConveyorStats.Ccs.DimensionTCPConnected = false;
                Logger.Warn("BgDimension_DoWork: " + ex.Message);

                gConveyorStats.Ccs.error += ex.Message + "\n";

            }
            finally
            {
                DimTCPserver.Stop();
                gConveyorStats.Ccs.ScaleTCPConnected = false;
            }
        }

        private void BgDimension_DoWork_server(object sender, DoWorkEventArgs e)
        {

            Dimension currentDimension = new Dimension();
            string currentData = "";
            BackgroundWorker worker = sender as BackgroundWorker;

            int port = Properties.Settings.Default.dimension_port;

            System.Net.IPAddress localAddr = System.Net.IPAddress.Parse(Settings.Default.DimConverterIP);
            var ipEndPoint = new IPEndPoint(localAddr, Settings.Default.dimension_port);
            try
            {
                TcpClient DimTCPserver = new TcpClient();
                

                // Enter the listening loop.
                while (true)
                {

                    NetworkStream stream = null;
                    try
                    {
                        Logger.Info("Starting dim new ...");


                        //DimTCPserver.Start();
                        DimTCPserver.Connect(ipEndPoint);

                        // Buffer for reading data
                        Byte[] bytes = new Byte[256];
                        String data = null;

                       // DimTCPClient = DimTCPserver.AcceptTcpClient();

                        //dimensionServerAddr = ((IPEndPoint)DimTCPClient.Client.RemoteEndPoint).Address.ToString();
                        //Logger.Info("Dim converter connected..." + dimensionServerAddr);


                        //  client.ReceiveTimeout = 60000;
                        data = null;
                        gConveyorStats.Ccs.DimensionTCPConnected = true;
                        // Get a stream object for reading and writing
                        stream = DimTCPserver.GetStream();

                        int i = 0;
                        currentData = "";
                        string subData = "";
                        while ((i = stream.Read(bytes, 0, bytes.Length)) != 0)
                        {
                            // Translate data bytes to a ASCII string.
                            data = System.Text.Encoding.ASCII.GetString(bytes, 0, i);

                            // Process the data sent by the client.
                            data = data.Replace("Q00003", "");

                            currentData += data;
                            try
                            {

                                while (((currentData.Contains((char)3) && currentData.Contains((char)2))))
                                {
                                    int start = currentData.IndexOf((char)2);
                                    int end = currentData.IndexOf((char)3);
                                    if (end - start - 1 > 0)
                                        subData = currentData.Substring(start + 1, end - start - 1);
                                    else
                                        subData = "";

                                    currentData = (currentData.Length > (end + 1)) ? currentData.Substring(end + 1) : "";
                                    if (subData.Length > 4)
                                        AppendLog("log_txt\\dimension_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + data + "\r\n");

                                    if (subData.Length == 16)
                                    {
                                        if (gConveyorStats.Ccs.dimEnable)
                                            worker.ReportProgress(1, subData);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn("BgDimension_DoWork: " + ex.Message);

                                gConveyorStats.Ccs.error += ex.Message + "\n";
                            }

                        }
                    }
                    catch (System.IO.IOException exio)
                    {
                        Logger.Warn("BgDimension_DoWork: IOException : " + exio.Message);
                        gConveyorStats.Ccs.DimensionTCPConnected = false;

                        if (DimTCPClient != null)
                        {
                            //DimTCPClient.Close();
                            //DimTCPClient = null;
                        }

                    }
                    catch (SocketException se)
                    {
                        Logger.Warn("BgDimension_DoWork: SocketException : " + se.Message);
                        if ((se.SocketErrorCode == SocketError.Interrupted || se.SocketErrorCode == SocketError.AddressAlreadyInUse))
                        {
                            // a blocking listen has been cancelled
                            gConveyorStats.Ccs.DimensionTCPConnected = false;
                            if (DimTCPClient != null)
                            {
                            //    DimTCPClient.Close();
                            //    DimTCPClient = null;
                            }

                        }

                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("BgDimension_DoWork: " + ex.Message);

                        gConveyorStats.Ccs.error += ex.Message + "\n";
                        if (DimTCPClient != null)
                        {
                           // DimTCPClient.Close();
                          //  DimTCPClient = null;
                        }

                        DimTCPserver.Close();
                        gConveyorStats.Ccs.DimensionTCPConnected = false;
                        break;
                    }


                }
            }
            catch (Exception ex)
            {
                if (DimTCPClient != null)
                {
                   // DimTCPClient.Close();
                   // DimTCPClient = null;
                }
                DimTCPserver.Stop();
                gConveyorStats.Ccs.DimensionTCPConnected = false;
                Logger.Warn("BgDimension_DoWork: " + ex.Message);

                gConveyorStats.Ccs.error += ex.Message + "\n";

            }
            finally
            {
                Logger.Warn("BgDimension_DoWork: stop ");
                DimTCPserver.Stop();
                gConveyorStats.Ccs.ScaleTCPConnected = false;
            }
        }

        private void BgScale_DoWork(object sender, DoWorkEventArgs e)
        {
            string currentData = "";
            BackgroundWorker worker = sender as BackgroundWorker;

            int port = Properties.Settings.Default.scale_port;

            try
            {
                System.Net.IPAddress localAddr = System.Net.IPAddress.Parse(Settings.Default.ScaleConverterIP);

                Logger.Info("Starting Scale...");
                while (!worker.CancellationPending)
                {
                    try
                    {
                        

                        gConveyorStats.Ccs.ScaleTCPConnected = false;
                        ScaleTCPserver = new TcpListener(port);

                        ScaleTCPserver.Start();

                        // Buffer for reading data
                        Byte[] bytes = new Byte[256];
                        String data = null;

                        ScaleTCPClient = ScaleTCPserver.AcceptTcpClient();
                        scaleServerAddr = ((IPEndPoint)ScaleTCPClient.Client.RemoteEndPoint).Address.ToString();
                        Logger.Info("Scale converter connected..."+ scaleServerAddr);

                        data = null;
                        // Get a stream object for reading and writing
                        NetworkStream stream = ScaleTCPClient.GetStream();
                        // Enter the listening loop.
                        string subData = "";

                        gConveyorStats.Ccs.ScaleTCPConnected = true;
                        int i = 0;
                        currentData = "";

                        while (!worker.CancellationPending && (i = stream.Read(bytes, 0, 250)) != 0)
                        {
                            // Translate data bytes to a ASCII string.
                            data = System.Text.Encoding.ASCII.GetString(bytes, 0, i);

                            AppendLog("log_txt\\scale_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", DateTime.Now.ToString() + ";" + i + ";" + data + ";\n\r");

                            Console.WriteLine(data);


                            //try
                            {
                                if (Settings.Default.depotID == 28) //sth sol
                                {
                                    currentData = data;

                                    if (currentData.Length == 16)
                                    {
                                        subData = currentData.Substring(0, 11);

                                        if (subData.Length > 1)
                                        {
                                            if (gConveyorStats.Ccs.scaleEnable)
                                                worker.ReportProgress(0, subData.Trim());
                                        }

                                    }
                                }
                                if (Settings.Default.lineID == 3) //sth sol
                                {
                                    currentData = data;

                                    if (currentData.Length == 16)
                                    {
                                        subData = currentData.Substring(0, 11);

                                        if (subData.Length > 1)
                                        {
                                            if (gConveyorStats.Ccs.scaleEnable)
                                                worker.ReportProgress(0, subData.Trim());
                                        }

                                    }
                                }
                                if (Settings.Default.depotID == 2) //qc
                                {
                                    currentData = data;

                                    if (currentData.Length.In(16))
                                    {
                                        subData = currentData.Substring(1, 10);

                                        if (subData.Length > 1)
                                        {
                                            if (gConveyorStats.Ccs.scaleEnable)
                                                worker.ReportProgress(0, subData.Trim());
                                        }

                                    }
                                }
                                if (Settings.Default.depotID == 12) //toronto
                                {
                                    currentData = data;

                                    if (currentData.Length == 12)
                                    {
                                        subData = currentData.Substring(0, 12);

                                        if (subData.Length > 1)
                                        {
                                            if (gConveyorStats.Ccs.scaleEnable)
                                                worker.ReportProgress(0, subData.Trim());
                                        }

                                    }
                                }
                                else
                                {
                                    currentData += data;

                                    while (currentData.Contains("\n") && currentData.Contains((char)2))
                                    {
                                        int start = currentData.IndexOf((char)2);
                                        int end = currentData.IndexOf("\r\n");
                                        if (end - start - 1 > 0)
                                            subData = currentData.Substring(start + 1, end - start - 1);
                                        else
                                            subData = "";

                                        currentData = (currentData.Length > end + 1) ? currentData.Substring(end + 2) : "";
                                        if (subData.Length == 8)
                                        {
                                            if (gConveyorStats.Ccs.scaleEnable)
                                                worker.ReportProgress(0, subData);
                                            //break;
                                        }

                                    }
                                }
                            }

                        }
                        stream.Close();
                        Logger.Info("Scale close ..." );
                    }
                    catch (System.IO.IOException exio)
                    {
                        gConveyorStats.Ccs.ScaleTCPConnected = false;

                        if (ScaleTCPClient != null)
                        {
                            ScaleTCPClient.Close();
                            ScaleTCPClient = null;
                        }

                        Logger.Info("Scale Dowork IOException..."+exio.Message);
                    }
                    catch (SocketException se)
                    {
                        if ((se.SocketErrorCode == SocketError.Interrupted))
                        {
                            // a blocking listen has been cancelled
                            gConveyorStats.Ccs.ScaleTCPConnected = false;
                            if (ScaleTCPClient != null)
                            {
                                ScaleTCPClient.Close();
                                ScaleTCPClient = null;
                            }
                        }
                        if ((se.SocketErrorCode == SocketError.AddressAlreadyInUse))
                        {
                            // a blocking listen has been cancelled
                            gConveyorStats.Ccs.ScaleTCPConnected = false;
                            if (ScaleTCPClient != null)
                            {
                                ScaleTCPClient.Close();
                                ScaleTCPClient = null;
                            }
                            
                        }
                        ScaleTCPserver.Stop();
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("BgScale_DoWork Exception: " + ex.Message);
                        gConveyorStats.Ccs.error += ex.Message + "\n";

                        gConveyorStats.Ccs.ScaleTCPConnected = false;
                        if (ScaleTCPClient != null)
                        {
                            ScaleTCPClient.Close();
                            ScaleTCPClient = null;
                        }
                        ScaleTCPserver.Stop();

                        break;

                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("BgScale_DoWork: " + ex.Message);
                gConveyorStats.Ccs.error += ex.Message + "\n";

            }
            finally
            {
                Logger.Info("Scale close ...");
                gConveyorStats.Ccs.ScaleTCPConnected = false;
                ScaleTCPserver.Stop();
            }
        }

        private void BgScale_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            string data = e.UserState as string;

            txtLastScale.Text = data;


            if (Settings.Default.depotID == 1)
            {
                if (data.Length > 5)
                    data = data.Substring(0, 6);
            }

            gConveyorStats.Ccs.lastScale = data.ToDecimalNoException();

            if (gConveyorStats.Ccs.lastScale <=0)
                gConveyorStats.Ccs.lastScaleErrorCode = data.ToDecimalNoException();

                gConveyorStats.Csc.totalParcelFromScale++;
            gConveyorStats.Ccs.dtLastScale = DateTime.Now;
            lblscaleState.Text = (lblscaleState.Text.ToIntNoException()+1).ToString();
            gConveyorStats.Csc.totWeight += (double)gConveyorStats.Ccs.lastScale;

            //if (gConveyorStats.Ccs.dtCamera.AddSeconds(5) < gConveyorStats.Ccs.dtLastDimension || gConveyorStats.Ccs.dtCamera.AddSeconds(5) < gConveyorStats.Ccs.dtLastScale)
            //{
            //    gConveyorStats.Csc.totContCameraError++;
            //}
            //else
            //    gConveyorStats.Csc.totContCameraError=0;

            

        }

        private void TriggerGetChute()
        {
            try
            {
                lblDimState.Text = "0";
                lblscaleState.Text = "0";
                if (gConveyorStats.Ccs.lastDimension == null)
                    gConveyorStats.Ccs.lastDimension = new Dimension();


                double scaleDelay = (gConveyorStats.Ccs.dtCamera - gConveyorStats.Ccs.dtLastScale).TotalMilliseconds;
                double DimensionDelay = (gConveyorStats.Ccs.dtCamera - gConveyorStats.Ccs.dtLastDimension).TotalMilliseconds;

                lblDelayDimension.Text = Math.Round(DimensionDelay, 3).ToString();
                lblDelayScale.Text = Math.Round(scaleDelay, 3).ToString();

                if (DimensionDelay > 600)
                {
                    lblDelayDimension.ForeColor = Color.Red;
                }
                else
                    lblDelayDimension.ForeColor = Color.White;

                int idBoaSeq = 0;
                if (Settings.Default.boa)
                {
                    idBoaSeq = GetIdSequence();
                    gConveyorStats.Ccs.idBoaSeq = idBoaSeq;
                    AppendLog("log_txt\\boa_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", DateTime.Now.ToString("HH:mm:ss.fff") + "Get Seq: " + idBoaSeq + " " + gConveyorStats.Ccs.lastBareCode + " " + gConveyorStats.Ccs.lastDimension.ToString() + " " + gConveyorStats.Ccs.lastScale.ToString() + "\r\n");
                }


                    DateTime d1 = DateTime.Now;
                int chute = DBGetChute();

                int toChute = chute == 99 ? rejectedChute : chute;

                if (gConveyorStats.Ccs.lastDimension.W == -1) gConveyorStats.Csc.totDimensionerError++;
                if (gConveyorStats.Ccs.lastScale <= 0 || gConveyorStats.Ccs.lastScale >= 990)
                {
                    gConveyorStats.Csc.totScaleError++;
                }

                if (idBoaSeq != 0)
                {
                    if (Settings.Default.small_parcel > 0 && chute != rejectedChute && gConveyorStats.Ccs.lastScale>0)
                    {
                        if (chute == 1 && gConveyorStats.Ccs.lastScale < Settings.Default.small_parcel)
                            chute = 4;
                        else if (chute.In (19,21)  && gConveyorStats.Ccs.lastScale < Settings.Default.small_parcel)
                            chute = 20;
                        else if (chute.In (23, 25) && gConveyorStats.Ccs.lastScale < Settings.Default.small_parcel)
                            chute = 24;
                        else if (chute == 99) 
                            chute = rejectedChute;
                        else if (gConveyorStats.Ccs.lastScale < Settings.Default.small_parcel)
                            chute++;
                    }
                        SendChuteToBoa(idBoaSeq, chute, gConveyorStats.Ccs.lastBareCode.ToString());
                    AppendLog("log_txt\\boa_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", DateTime.Now.ToString("HH:mm:ss.fff") + "insert :  " + idBoaSeq + " " + gConveyorStats.Ccs.lastBareCode + " " + chute + " " + gConveyorStats.Ccs.lastDimension.ToString() + " " + gConveyorStats.Ccs.lastScale.ToString() + "\r\n");
                }

                    for (int i = 0; i < 3; i++)
                {
                    
                    if (Settings.Default.depotID == 12) // Toronto
                    {
                        if (Settings.Default.TestMode == 1) toChute = 1;

                        if (Properties.Settings.Default.lineID == 0)
                        {
                           // Logger.Info("send chute to COLISDDE :  " + toChute);
                            DDe.Poke("COLISDDE", toChute.ToString()); //send chute no to conveyor
                        }
                    }
                    if (Settings.Default.depotID == 2) // QC
                    {
                        if (Properties.Settings.Default.lineID == 0)
                            DDe.Poke("COLISDDE", toChute.ToString()); //send chute no to conveyor
                    }

                    if (Settings.Default.depotID == 1) // STH
                    {
                        if (Properties.Settings.Default.lineID == 0)
                            DDe.Poke("COLISDDE_M31", toChute.ToString()); //send chute no to conveyor
                        if (Properties.Settings.Default.lineID == 1)
                            DDe.Poke("COLISDDE_M30", toChute.ToString()); //send chute no to conveyor
                        if (Properties.Settings.Default.lineID == 3)
                        {
                            DDe.Poke("COLISDDE_M5", toChute.ToString()); //send chute no to conveyor
                           // Logger.Debug("colisdde_m5 " + toChute);
                        }
                    }
                }

                lblChuteNo.Text = chute.ToString();

                txtWidth.Text = (gConveyorStats.Ccs.lastDimension.W == -1) ? "" : gConveyorStats.Ccs.lastDimension.W.ToString();
                txtHeight.Text = (gConveyorStats.Ccs.lastDimension.H == -1) ? "" : gConveyorStats.Ccs.lastDimension.H.ToString();
                txtLong.Text = (gConveyorStats.Ccs.lastDimension.L == -1) ? "" : gConveyorStats.Ccs.lastDimension.L.ToString();
                txtWeight.Text = (gConveyorStats.Ccs.lastScale == -1) ? "" : gConveyorStats.Ccs.lastScale.ToString();

                lblParcelID.Text = gConveyorStats.Ccs.lastBareCode.ToString();
                lblPostalCode.Text = gConveyorStats.Ccs.lastPostalCode.ToString();
                lblBoaId.Text = gConveyorStats.Ccs.idBoaSeq.ToString();
                lblLastDisable_code98.Text = gConveyorStats.Ccs.lastIsDisableCode98.ToString();

                AppendLog("log_txt\\conveyor_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log",  DateTime.Now.ToString("HH:mm:ss.fff") +" " + Properties.Settings.Default.lineID+ " " + gConveyorStats.Ccs.lastBareCode + " " + chute + " "+ gConveyorStats.Ccs.lastDimension.ToString() + " "+ gConveyorStats.Ccs.lastScale.ToString() + "\r\n");

                ManageError();

                if (chute != 99)
                    InsertData(chute);

            }
            catch (Exception ex)
            {
                Logger.Error("TriggerGetChute :", ex);
            }
        }

        private int GetIdSequence()
        {
            DataTable dt = DBLocalConnection.Instance.FillDT("select id_sequence from boa_to_nationex where nationex_lu is null and camera_passee=1 order by date_apparition");
            if (dt.Rows.Count == 0) return 0;
            if (dt.Rows.Count > 1)
            {
                AppendLog("log_txt\\boa_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", DateTime.Now.ToString("HH:mm:ss.fff") + " many id not read: " + dt.Rows[dt.Rows.Count - 1]["id_sequence"].ToIntNoException() + ", " + dt.Rows[0]["id_sequence"].ToIntNoException()+" " + gConveyorStats.Ccs.lastBareCode + " " + rejectedChute + " " + " " + gConveyorStats.Ccs.lastScale.ToString() + "\r\n");

                DataTable dt2 = DBLocalConnection.Instance.FillDT($@"select id_sequence from nationex_to_boa where id_sequence={dt.Rows[dt.Rows.Count - 1]["id_sequence"].ToIntNoException()}");
                if (dt2.Rows.Count == 0)
                {
                    AppendLog("log_txt\\boa_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", DateTime.Now.ToString("HH:mm:ss.fff") + " Need to skip this id: " + dt.Rows[dt.Rows.Count - 1]["id_sequence"].ToIntNoException() + " " + gConveyorStats.Ccs.lastBareCode + " " + rejectedChute + " " + " " + gConveyorStats.Ccs.lastScale.ToString() + "\r\n");
                    SendChuteToBoa(dt.Rows[dt.Rows.Count - 1]["id_sequence"].ToIntNoException(), rejectedChute, "");
                }
                ResetChuteToBoa();
                    
                return dt.Rows[dt.Rows.Count-1]["id_sequence"].ToIntNoException();
            }
            
            return dt.Rows[0]["id_sequence"].ToIntNoException();




        }

        private void SendChuteToBoa(int _idSeq, int _chute, string _shipppingId)
        {
            Logger.Warn("sendchutetoboa ! " + _shipppingId);
            DataTable dt2 = DBLocalConnection.Instance.FillDT($@"select id_sequence from nationex_to_boa where id_sequence={_idSeq}");
            if (dt2.Rows.Count == 0)
            {
                DBLocalConnection.Instance.ExecuteCommand($@"insert into nationex_to_boa (id_sequence, voie_tri, id_colis,date_association) values ({_idSeq}, {_chute},'{_shipppingId}',now())").ToIntNoException();
            }
            DBLocalConnection.Instance.ExecuteCommand($@"update boa_to_nationex set nationex_lu=true where id_sequence={_idSeq}");
        }
        private void ResetChuteToBoa()
        {
            Logger.Warn("Reset Boa !");

            DataTable dt = DBLocalConnection.Instance.FillDT("select id_sequence from boa_to_nationex where nationex_lu is null and camera_passee=1 order by date_apparition");

            foreach (DataRow row in dt.Rows)
            {
                DataTable dt2 = DBLocalConnection.Instance.FillDT($@"select id_sequence from nationex_to_boa where id_sequence={row["id_sequence"].ToIntNoException()}");
                if (dt2.Rows.Count == 0)
                {
                    DBLocalConnection.Instance.ExecuteCommand($@"insert into nationex_to_boa (id_sequence, voie_tri, date_association) values ({row["id_sequence"].ToIntNoException()}, -1,now())").ToIntNoException();
                    DBLocalConnection.Instance.ExecuteCommand($@"update boa_to_nationex set nationex_lu=true where id_sequence={row["id_sequence"].ToIntNoException()}");
                }
            }
        }


        private int DBGetChute()
        {
            int chuteNo = rejectedChute;
            object chute = null;
            // get all barecode and postalcode
            string[] split = gConveyorStats.Ccs.lastCameraData.Split(',');

            List<string> bareCodes = split.Where(p => p.Length > 8).Distinct().ToList();
            List<string> postalCode = split.Where(p => (p.Length == 6 && Char.IsLetter(p[0]) && Char.IsLetter(p[2])) || (p.Length ==7 && p[3]==' ' && Char.IsLetter(p[0]) && Char.IsLetter(p[2]))).Distinct().ToList();
            List<string> goodBareCode = new List<string>();
            int nbGoodBareCode = 0;
            string lastbarcode = "";

            for(int i=0;i< postalCode.Count;i++)
            {
                if (postalCode[i].Length == 7)
                {
                    postalCode[i] = postalCode[i].Replace(" ", "");
                }
            }
            postalCode = postalCode.Distinct().ToList();

            
            // reset last data
            gConveyorStats.Ccs.lastBareCode = "";
            gConveyorStats.Ccs.lastPostalCode = "";
            gConveyorStats.Ccs.lastIsDisableCode98 = false;
            gConveyorStats.Ccs.lastCustomerId = 0;


            if (gConveyorStats.Ccs.lastCameraData.Contains("?"))
            {
                gConveyorStats.Csc.noRead++;
                AppendLog("log_txt\\noread_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", DateTime.Now.ToString() + ";" + gConveyorStats.Ccs.lastCameraData + "\r\n");
                chuteNo = Properties.Settings.Default.NoRead;
            }

            // sorting by barecode
            foreach (string barecode in bareCodes)
            {
                string currentBareCode = barecode;
                

                if (!currentBareCode.Contains("?") && currentBareCode.Trim() !="")
                {
                    currentBareCode = RenameShippingForBentley(currentBareCode, "010");
                    

                    try
                    {
                        Logger.Debug("check customer barcode :" + currentBareCode);

                        var qry = $@"
select shipping_id,customer_id,route_id,disable_code98 
from conveyor_shipment 
where customer_barcode='{currentBareCode}'";

                        DataTable dt = DBLocalConnection.Instance.FillDT(qry);

                        if (dt.Rows.Count == 0)
                        {
                            Logger.Debug("not found customer barcode :" + currentBareCode);
                            if (currentBareCode.ToLongNoException() == 0)
                            {
                                continue;
                            }

                            if (currentBareCode.Length > 10)
                            {
                                Logger.Debug("check in WB for shipment :" + currentBareCode);

                                qry = $@"
select shipping_id,customer_id,route_id,disable_code98 from conveyor_shipment 
where shipping_id={currentBareCode.Substring(0, 9)} or (reference_no='{currentBareCode.Substring(0, 11)}' and customer_id=129326)
";

                                dt = DBLocalConnection.Instance.FillDT(qry);


                                // OVERRIDE LOTOQUEBEC 
                                // LORSQUE SHIFT BROKER & LOTOQUEBEC
                                // ON VA CHERCHER LA ROUTE DU CODE POSTAL (LOCATION) PLUTOT QUE LE SHIPMENT.DEST_ROUTE_ID
                                if (dt.Rows.Count > 0 && 
                                    dt.Rows[0]["CUSTOMER_ID"].ToIntNoException() == (int)eCustomerID.LOTOQUEBEC)
                                {
                                    var currentShift = allShift.FirstOrDefault(s => s.ConveyorShiftID == Settings.Default.CurrentShift);
                                    if (currentShift != null && currentShift.ConveyorID == 1)
                                    {
                                        Logger.Info($"Found LotoQuebec shipment, sorting by postal code [{currentBareCode}] [{currentBareCode.Substring(0, 9)}]");

                                        qry = $@"
select shipping_id,customer_id,l.route_id,disable_code98 
from conveyor_shipment s
inner join location l on l.POSTAL_CODE = s.DEST_POSTAL_CODE
where shipping_id = {currentBareCode.Substring(0, 9)}
";

                                        dt = DBLocalConnection.Instance.FillDT(qry);
                                    }
                                    else if (currentShift == null)
                                    {
                                        Logger.Error("Current shift not found for Lotoquebec");
                                    }
                                }
                            }
                        }
                        else
                        {
                            Logger.Debug("found customer barcode :" + currentBareCode);
                            currentBareCode = dt.Rows[0]["shipping_id"].ToString() + "01";
                            
                        }

                        
                        if (dt.Rows.Count > 0)
                        {
                           


                            string sql = string.Format("select chute_no,new_route_id from conveyor_shift_route where shift_id={0} and new_route_id={1}", Settings.Default.CurrentShift, dt.Rows[0]["route_id"].ToIntNoException());
                            chute = DBLocalConnection.Instance.ExecuteScalar(sql).ToIntNoException();

                            if (chute.ToIntNoException()>0)
                                chuteNo = chute.ToIntNoException();


                            if (dt.Rows[0]["route_id"].ToIntNoException() != 0 && chute.ToIntNoException() == 0)
                                Logger.Error($"Route not configured [{ currentBareCode}] [{ dt.Rows[0]["shipping_id"].ToString()}]  [{dt.Rows[0]["route_id"].ToIntNoException()}] [{chute.ToIntNoException()}]");
                            else 
                                Logger.Info($"[{currentBareCode}] [{ dt.Rows[0]["shipping_id"].ToString()}] shift [{ Settings.Default.CurrentShift}] route [{dt.Rows[0]["route_id"].ToString()}] chute [{chute}]");

                            if (dt.Rows[0]["customer_id"].ToIntNoException() == 129326)
                                currentBareCode = dt.Rows[0]["shipping_id"].ToString() + "01";

                            nbGoodBareCode++;
                            goodBareCode.Add(currentBareCode.Substring(0,11));
                            
                            gConveyorStats.Ccs.lastBareCode = currentBareCode.Substring(0, 11);
                            gConveyorStats.Ccs.lastCustomerId = dt.Rows[0]["customer_id"].ToIntNoException();
                            gConveyorStats.Ccs.lastIsDisableCode98 = dt.Rows[0]["disable_code98"].ToBoolFalseDefault();

                            lastbarcode = currentBareCode;
                        }
                        else
                        {
                            Logger.Warn($"No waybill found:[{currentBareCode}]");
                            if (currentBareCode.Length.In(11, 12))
                                lastbarcode = currentBareCode;
                        }

                    }
                    catch (Exception ex)
                    {
                        Logger.Error("exception waybill :" +ex.Message);
                    }
                    //AppendLog("log_txt\\getChute" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", 
                    //    DateTime.Now.ToString() + ";" + currentBareCode + ";" + gConveyorStats.Ccs.lastDimension.W + ";" + 
                    //    gConveyorStats.Ccs.lastScale + " ; "+ chuteNo+"\r\n");

                    if (gConveyorStats.Ccs.NoDimScaleEnable)
                    {
                        if (gConveyorStats.Ccs.lastDimension.W <= 0 ||
                            gConveyorStats.Ccs.lastDimension.H <= 0 ||
                            gConveyorStats.Ccs.lastDimension.L <= 0 ||
                            gConveyorStats.Ccs.lastScale < 0 || 
                            gConveyorStats.Ccs.lastScale > 150 || 
                            gConveyorStats.Ccs.lastDimension.W > 100 || 
                            gConveyorStats.Ccs.lastDimension.L > 100  || 
                            gConveyorStats.Ccs.lastDimension.W> 100)
                        {
                            decimal cube = (gConveyorStats.Ccs.lastDimension.W * gConveyorStats.Ccs.lastDimension.L * gConveyorStats.Ccs.lastDimension.H) / 1728;
                            if ((gConveyorStats.Ccs.lastDimension.W >0 || gConveyorStats.Ccs.lastDimension.H > 0|| gConveyorStats.Ccs.lastDimension.L > 0) &&
                                gConveyorStats.Ccs.lastScale <=0 && cube <= (decimal)0.5)
                                AppendLog("log_txt\\smal_parcel_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", DateTime.Now.ToString() + ";" + currentBareCode + ";" + Math.Round(cube,2) + "\r\n");
                            else if (gConveyorStats.Ccs.lastIsDisableCode98 == false)
                            {

                                bool goto98 = IsCode98(gConveyorStats.Ccs.lastBareCode);

                                AppendLog("log_txt\\noscale2_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", DateTime.Now.ToString() + ";" + currentBareCode + ";" + gConveyorStats.Ccs.lastDimension.W + ";" + gConveyorStats.Ccs.lastScale + "\r\n");

                                if (goto98)
                                {
                                    chuteNo = 98;
                                    break;
                                }
                                else
                                {
                                    chuteNo = rejectedChute;
                                    break;
                                }
                            }
                            else
                                AppendLog("log_txt\\disable_code98_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", DateTime.Now.ToString() + "; " + currentBareCode + " ; " + gConveyorStats.Ccs.lastDimension.W + " ; " + gConveyorStats.Ccs.lastScale + "\r\n");

                        }
                        else
                        {
                            RemoveCode98(gConveyorStats.Ccs.lastBareCode);
                        }
                    }

                    AppendLog("log_txt\\getChute_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", DateTime.Now.ToString() + "; " + currentBareCode + " ;NoDimScaleEnable:" + gConveyorStats.Ccs.NoDimScaleEnable + " ; " + gConveyorStats.Ccs.lastDimension.W + " ; " + gConveyorStats.Ccs.lastScale + "lastIsDisableCode98: " + gConveyorStats.Ccs.lastIsDisableCode98 +"; chute : "+ chuteNo + "\r\n"); ;

                    //if (chute != null && chute.ToIntNoException() != 0)
                    {
                       // chuteNo = chute.ToIntNoException();
                        gConveyorStats.Csc.totSortWaybill++;
                        if (gConveyorStats.Ccs.code86)
                            RemoveCode86(gConveyorStats.Ccs.lastBareCode);
                    }
                   // else
                    {

                        if (gConveyorStats.Ccs.code86 && chuteNo !=98)
                        {
                            bool goto86 = IsCode86(gConveyorStats.Ccs.lastBareCode);

                            if (goto86)
                            {
                                chuteNo = 86;
                            }
                            else
                                chuteNo = rejectedChute;
                        }
                    }
                }
            }
            Logger.Info($"set lastbarcode:[{lastbarcode}]");

            if (gConveyorStats.Ccs.lastBareCode == "")
                gConveyorStats.Ccs.lastBareCode = lastbarcode;

            //sort by postal code

            chuteNo = SortByLocation(gConveyorStats.Ccs.lastBareCode, postalCode, chuteNo);

            if (goodBareCode.Distinct().Count() > 1)
            {
                AppendLog("log_txt\\doubleread_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", DateTime.Now.ToString() + ";" + gConveyorStats.Ccs.lastCameraData + "\r\n");
                
                chuteNo = 99;
            }

            if (chuteNo == rejectedChute)
            {
                AppendLog("log_txt\\rejected_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", DateTime.Now.ToString() + ";"+gConveyorStats.Ccs.lastCameraData + ";" + gConveyorStats.Ccs.lastScale +  ";"  + "\r\n");
                gConveyorStats.Csc.totRejected++;

            }
            if (chuteNo == 98)
            {
                AppendLog("log_txt\\code98_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", DateTime.Now.ToString() + ";" + gConveyorStats.Ccs.lastCameraData + ";" + gConveyorStats.Ccs.lastScale + ";" + "\r\n");
                gConveyorStats.Csc.total98++;

            }
            gConveyorStats.Ccs.lastChute = chuteNo;

            return chuteNo;
        }



        private int SortByLocation(string _currentBarCode, List<string> postalCode, int _chuteNo)
        {
            bool bypass = false;
            if (postalCode.Count > 0)
            {

                if (!Properties.Settings.Default.postalCodeSort && _currentBarCode == "")
                {
                    AppendLog("log_txt\\location_withoutwb_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", DateTime.Now.ToString() + ";bc:" + _currentBarCode + "; cam :" + gConveyorStats.Ccs.lastCameraData + "\r\n");

                    return _chuteNo;
                }

                if (((_chuteNo == 86 || _chuteNo == rejectedChute) && (_currentBarCode != "" && postalCode.Count == 1)))
                {
                    if (postalCode[0].Length == 7)
                        postalCode[0] = postalCode[0].Substring(0, 3) + postalCode[0].Substring(4, 3);

                    DataTable dt = DBLocalConnection.Instance.FillDT(string.Format("select c.new_route_id,c.chute_no from location p INNER JOIN conveyor_shift_route c ON p.route_id = c.new_route_id  WHERE  c.shift_id={0} and p.postal_code={1}", Settings.Default.CurrentShift, StrUtils.QuotedStr(postalCode[0])));

                    if (dt.Rows.Count > 0)
                    {
                        if (_chuteNo == 86 || _chuteNo == rejectedChute)
                        {
                            RemoveCode86(_currentBarCode);
                        }

                        _chuteNo = dt.Rows[0]["chute_no"].ToIntNoException();

                        gConveyorStats.Ccs.lastPostalCode = postalCode[0];

                        Logger.Info($"[{postalCode[0]}] route [{dt.Rows[0]["new_route_id"].ToString()}] chute [{_chuteNo}]");

                        if (gConveyorStats.Ccs.lastBareCode == "")
                            gConveyorStats.Csc.NbSortByPostalcodeWithoutWb++;
                        else
                            gConveyorStats.Csc.totSortPostalCode++;

                        AppendLog("log_txt\\location_" + DateTime.Now.ToString("yyyy-MM-dd") + ".log", DateTime.Now.ToString() + ";(cam:" + gConveyorStats.Ccs.lastBareCode + ");" + gConveyorStats.Ccs.lastCameraData + ";" + gConveyorStats.Ccs.lastBareCode + "\r\n");
                    }
                    else
                        Logger.Info($"Postal code not found [{postalCode[0]}]");
                }
            }
            return _chuteNo;
        }

        private string RenameShippingForBentley(string _shippingNo, string add = "")
        {
            try
            {
                string s = _shippingNo.ToString();

                if (s.Length == 12 && s.StartsWith("7000"))
                    _shippingNo = "7" + _shippingNo.Substring(4) + add;
                
            }
            catch (Exception ex)
            {
                
            }
            return _shippingNo;
        }
        
        
        private void BgData_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                bool first = true;
                DDe.Connect();

                BackgroundWorker worker = sender as BackgroundWorker;
                
                while (true)
                {
                    try
                    {
                        gConveyorStats.Csc.totDBShip = DBLocalConnection.Instance.ExecuteScalar(string.Format("select count(*) from conveyor_shipment")).ToIntNoException();
                        gConveyorStats.Csc.totDBPostal = DBLocalConnection.Instance.ExecuteScalar(string.Format("select count(*) from location")).ToIntNoException();

                        lastSync = DateTime.Now;
                        first = false;
       
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("BgData_DoWork while:", ex);
                    }
                    Thread.Sleep(Properties.Settings.Default.SyncDelay * 1000);

                    if (e.Cancel) break;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("BgData_DoWork :", ex);
                gConveyorStats.Ccs.error += ex.Message + "\n";

            }
        }
  
        private void CheckSQLStatus()
        {
            try
            {

                object cnt = DBConnection.Instance.ExecuteScalar(string.Format("select count(*) from nationex.customer"));
                if (cnt == null || cnt.ToIntNoException() == 0)
                    gConveyorStats.Ccs.SQLConnected = false;
                else
                    gConveyorStats.Ccs.SQLConnected = true;
            }
            catch
            {
                gConveyorStats.Ccs.SQLConnected = false;

            }
        }

        private void InsertData(int chute)
        {
            try
            {
                
                if (gConveyorStats.Ccs.lastBareCode.Length.In(11,12))
                {
                    string sql = string.Format("insert into scan_history (parcel_id,l,h,w,weight,chute,date_insert,lineId,source_type) values({0},{1},{2},{3},{4},{5},{6},{7},{8})",
                        StrUtils.QuotedStr(gConveyorStats.Ccs.lastBareCode.Substring(0, 11)),
                        gConveyorStats.Ccs.lastDimension.L,
                        gConveyorStats.Ccs.lastDimension.H,
                        gConveyorStats.Ccs.lastDimension.W,
                        gConveyorStats.Ccs.lastScale,
                        chute,
                        StrUtils.QuotedStr(gConveyorStats.Ccs.dtCamera.ToString("yyyy-MM-dd HH:mm:ss")),
                        Properties.Settings.Default.lineID,
                        eSourceType.Conv_Conveyor.ToInt32());
                    Logger.Info(sql);
                    DBLocalConnection.Instance.ExecuteCommand(sql);
                    gConveyorStats.Csc.totDBInsert++;
                }
                else
                {
                    DBLocalConnection.Instance.ExecuteCommand(string.Format("insert into scan_noWB (camera_data,l,h,w,weight,chute,date_insert,lineId,source_type) values({0},{1},{2},{3},{4},{5},{6},{7},{8})",
                                           StrUtils.QuotedStr(gConveyorStats.Ccs.lastCameraData),
                                           gConveyorStats.Ccs.lastDimension.L,
                                           gConveyorStats.Ccs.lastDimension.H,
                                           gConveyorStats.Ccs.lastDimension.W,
                                           gConveyorStats.Ccs.lastScale,
                                           chute,
                                           StrUtils.QuotedStr(gConveyorStats.Ccs.dtCamera.ToString("yyyy-MM-dd HH:mm:ss")),
                                           Properties.Settings.Default.lineID,
                                           eSourceType.Conv_Conveyor.ToInt32())
                                           );

                    Logger.Info($"barcode invalid: [{gConveyorStats.Ccs.lastCameraData}] [{gConveyorStats.Ccs.lastBareCode}]");
                }


            }
            catch (Exception ex)
            {
                Logger.Error("Error InsertData :", ex);
                gConveyorStats.Ccs.error += ex.Message + "\n";

            }
            gConveyorStats.Ccs.lastBareCode = "";
            gConveyorStats.Ccs.lastDimension.L = -1;
            gConveyorStats.Ccs.lastDimension.W = -1;
            gConveyorStats.Ccs.lastDimension.H = -1;
            gConveyorStats.Ccs.lastScale = -1;
        }

  

        private void DrawStatus()
        {
            try
            {
                if (gConveyorStats == null) return;
               
                DrawIt(140, 28, gConveyorStats.Ccs.OldCameraTCPConnected = gConveyorStats.Ccs.CameraTCPConnected);

                DrawIt(235, 28, gConveyorStats.Ccs.OldDimensionTCPConnected = gConveyorStats.Ccs.DimensionTCPConnected);

                DrawIt(325, 28, gConveyorStats.Ccs.OldScaleTCPConnected = gConveyorStats.Ccs.ScaleTCPConnected);

                DrawIt(400, 28, gConveyorStats.Ccs.OldSQLConnected = gConveyorStats.Ccs.SQLConnected);

                DrawIt(465, 28, gConveyorStats.Ccs.OldDDEConnected = gConveyorStats.Ccs.DDEConnected);

            }
            catch
            { }
        }

        protected void DrawIt(int x, int y,bool _true)
        {
            Color c = Color.DarkRed;
            if (_true)
                c = Color.DarkGreen;

            using (System.Drawing.Graphics graphics = this.CreateGraphics())
            {
                System.Drawing.Rectangle rectangle = new System.Drawing.Rectangle(x, y, 10, 10);
                graphics.FillEllipse(new SolidBrush(c), rectangle);
            }

        }

        private void button2_Click(object sender, EventArgs e)
        {
            bgCamera.CancelAsync();
            bgData.CancelAsync();
            bgDimension.CancelAsync();
            bgScale.CancelAsync();
            this.Close();
        }

        private void button3_Click(object sender, EventArgs e)
        {
            DDEFrm frm = new DDEFrm();
            frm.dde = DDe;
            frm.ShowDialog();

        }

        private void UpdateDBScan()
        {
            object odt = DBLocalConnection.Instance.ExecuteScalar(string.Format("select count(*) from scan_history"));
            if (odt != null)
            {
                lblDBScan.Text = odt.ToString();
            }
        }

        private void AppendLog(string _path, string _data)
        {
            try
            {
                // This text is always added, making the file longer over time
                // if it is not deleted.
                using (StreamWriter sw = File.AppendText(_path))
                {
                    sw.Write(_data);
                }
            }
            catch(Exception ex)
            {
                Logger.Warn("AppendLog :", ex);
            }
        }

        private void ResetLocalDB()
        {
            
            DBLocalConnection.Instance.ExecuteScalar(string.Format("delete from conveyor_shipment"));
            DBLocalConnection.Instance.ExecuteScalar(string.Format("delete from code86"));
            DBLocalConnection.Instance.ExecuteScalar(string.Format("delete from code98"));
            gConveyorStats.Csc.totalParcel = 0;
            gConveyorStats.Csc.totalParcelFromCamera = 0;
            gConveyorStats.Csc.totalParcelFromDimension = 0;
            gConveyorStats.Csc.totalParcelFromScale = 0;
            gConveyorStats.Csc.totRejected = 0;
            gConveyorStats.Csc.totDimensionerError = 0;
            gConveyorStats.Csc.totScaleError = 0;
            gConveyorStats.Csc.totDBInsert = 0;
            gConveyorStats.Csc.totRemoteInsert = 0;
            gConveyorStats.Csc.totSortWaybill = 0;
            gConveyorStats.Csc.totSortPostalCode = 0;


            allScan.Clear();
            scanNoData.Clear();
            scanNoDimScaleData.Clear();
            gConveyorStats.Csc.totDBPostal = 0;
            gConveyorStats.Csc.totDBShip = 0;

            if (Properties.Settings.Default.lineID == 1)
            {
                DDe.Poke("C5:36.ACC", "0"); //send chute no to conveyor
                DDe.Poke("C5:37.ACC", "0"); //send chute no to conveyor
            }
            else
            {
                DDe.Poke("C5:33.ACC", "0"); //send chute no to conveyor
                DDe.Poke("C5:35.ACC", "0"); //send chute no to conveyor
            }

        }

        private void button1_Click(object sender, EventArgs e)
        {
            DDEFrm frm = new DDEFrm();
            frm.dde = DDe;
            frm.ShowDialog();
           
        }

        private void button4_Click(object sender, EventArgs e)
        {
            dtLastReset = DateTime.Now;

            gConveyorStats.Csc.totalParcel = 0;
            gConveyorStats.Csc.totalParcelFromCamera = 0;
            gConveyorStats.Csc.totalParcelFromDimension = 0;
            gConveyorStats.Csc.totalParcelFromScale = 0;
            gConveyorStats.Csc.totRejected = 0;
            gConveyorStats.Csc.totDimensionerError = 0;
            gConveyorStats.Csc.totScaleError = 0;
            gConveyorStats.Csc.totDBInsert = 0;
            gConveyorStats.Csc.totRemoteInsert = 0;
            gConveyorStats.Csc.totSortWaybill = 0;
            gConveyorStats.Csc.totSortPostalCode = 0;
            gConveyorStats.Csc.noRead = 0;

            allScan.Clear();
            if (Properties.Settings.Default.lineID == 0 || Properties.Settings.Default.lineID == 1|| Properties.Settings.Default.lineID==3 )
            {
                DBLocalConnection.Instance.ExecuteCommand(string.Format("truncate scan_history"));
                DBLocalConnection.Instance.ExecuteCommand(string.Format("truncate  wb"));
                DBLocalConnection.Instance.ExecuteCommand(string.Format("truncate  conveyor_shipment"));
                DBLocalConnection.Instance.ExecuteCommand(string.Format("truncate  postalcode"));
                DBLocalConnection.Instance.ExecuteCommand(string.Format("truncate  location"));
                DBLocalConnection.Instance.ExecuteCommand(string.Format("delete from code98"));
                DBLocalConnection.Instance.ExecuteCommand(string.Format("delete from code86"));
                gConveyorStats.Csc.totDBPostal = 0;
                gConveyorStats.Csc.totDBShip = 0;
            }

            if (Properties.Settings.Default.lineID == 1)
            {
                DDe.Poke("C5:36.ACC", "0"); //send chute no to conveyor
                DDe.Poke("C5:37.ACC", "0"); //send chute no to conveyor
            }
            else
            {
                DDe.Poke("C5:33.ACC", "0"); //send chute no to conveyor
                DDe.Poke("C5:35.ACC", "0"); //send chute no to conveyor
            }

            if (Settings.Default.depotID != 12)
                DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"WARN: Reset data [{depotName}] ligne [{Properties.Settings.Default.lineID}]");
            else
                DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"WARN: Reset data [{depotName}] line [{Properties.Settings.Default.lineID}]");
        }

        private void ManageError()
        {
            if (!gConveyorStats.Ccs.onRejectedError && gConveyorStats.Ccs.lastChute== rejectedChute)
            {
                gConveyorStats.Ccs.onRejectedError = true;
                gConveyorStats.Csc.totContRejectedError = 1;
            }
            else if (gConveyorStats.Ccs.onRejectedError && gConveyorStats.Ccs.lastChute == rejectedChute)
                gConveyorStats.Csc.totContRejectedError++;


            if (!gConveyorStats.Ccs.onDimError && gConveyorStats.Ccs.lastDimension.L == -1)
            {
                gConveyorStats.Ccs.onDimError = true;
                gConveyorStats.Csc.totContDimError = 1;
            }
            else if (gConveyorStats.Ccs.onDimError && gConveyorStats.Ccs.lastDimension.L == -1)
                gConveyorStats.Csc.totContDimError++;

            if (gConveyorStats.Ccs.onDimError && gConveyorStats.Ccs.lastDimension.L != -1)
            {
                if (gConveyorStats.Csc.totContDimError > 100)
                {
                    if (Settings.Default.depotID != 12)
                        DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"Les dimensions sont de retour [{depotName}] ligne [{Properties.Settings.Default.lineID}]");
                    else
                        DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"The dimensions are back [{depotName}] line [{Properties.Settings.Default.lineID}]");
                }

                    //DALReportSender.SendReport(eNatReports.ConveyorCamMonitoring, "Les  dimensions sont de retour sur le convoyeur " + Properties.Settings.Default.lineID, new List<string>(), "");

                    gConveyorStats.Csc.totContDimError = 0;
                gConveyorStats.Ccs.onDimError = false;
                gConveyorStats.Ccs.onDimErrorSent = false;
            }

            if (!gConveyorStats.Ccs.onScaleError && gConveyorStats.Ccs.lastScale <= 0)
            {
                gConveyorStats.Ccs.onScaleError = true;
                gConveyorStats.Csc.totContScaleError = 1;
            }
            else if (gConveyorStats.Ccs.onScaleError && gConveyorStats.Ccs.lastScale <=0 )
                gConveyorStats.Csc.totContScaleError++;
            if (gConveyorStats.Ccs.onScaleError && gConveyorStats.Ccs.lastScale > 0 )
            {
                if (gConveyorStats.Csc.totContScaleError > 10)
                {
                    if (Settings.Default.depotID != 12)
                        DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"Les poids sont de retour [{depotName}] ligne [{Properties.Settings.Default.lineID}]");
                    else
                        DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"The weight are back [{depotName}] line [{Properties.Settings.Default.lineID}]");

                    //DALReportSender.SendReport(eNatReports.ConveyorCamMonitoring, $"Les  poids sont de retour sur le convoyeur [{Properties.Settings.Default.depotID}] [{Properties.Settings.Default.lineID}] [{gConveyorStats.Ccs.lastScale}]", new List<string>(), "");
                }
                gConveyorStats.Csc.totContScaleError = 0;
                gConveyorStats.Ccs.onScaleError = false;
                gConveyorStats.Ccs.onScaleErrorSent = false;
            }


            if (gConveyorStats.Ccs.onRejectedError && gConveyorStats.Ccs.lastChute != rejectedChute)
            {
                if (gConveyorStats.Csc.totContRejectedError > 100)
                {
                    if (Settings.Default.depotID != 12)
                        DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"OK: Les colis ne sont plus rejetés [{depotName}] ligne [{Properties.Settings.Default.lineID}]");
                    else
                        DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"OK: The parcel aren't rejected anymore [{depotName}] line [{Properties.Settings.Default.lineID}]");

                    //DALReportSender.SendReport(eNatReports.ConveyorCamMonitoring, $"Les colis ne sont plus rejetés  [{Properties.Settings.Default.depotID}] [{Properties.Settings.Default.lineID}]", new List<string>(), "");
                }

                gConveyorStats.Csc.totContRejectedError = 0;
                gConveyorStats.Ccs.onRejectedErrorSent = false;

            }
            if (Settings.Default.dimension_port!=0 && gConveyorStats.Csc.totContDimError > 25 && !gConveyorStats.Ccs.onDimErrorSent)
            {
                if (Settings.Default.depotID != 12)
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"ERR: Aucune dimension après 25 colis  [{depotName}] ligne [{Properties.Settings.Default.lineID}]");
                else
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"ERR: No dimension after 25 parcels [{depotName}] line [{Properties.Settings.Default.lineID}]");

                //DALReportSender.SendReport(eNatReports.ConveyorCamMonitoring, "Erreur aucune dimension sur le convoyeur apres 10 colis passés " + Properties.Settings.Default.lineID, new List<string>(), "");
                gConveyorStats.Ccs.onDimErrorSent = true;

                if (Settings.Default.depotID == 2)
                {
                    Application.Restart();
                }
                else
                    ResetDim();
            }
            if (gConveyorStats.Csc.totContScaleError > 10 && !gConveyorStats.Ccs.onScaleErrorSent && gConveyorStats.Ccs.NoDimScaleEnable == true)
            {
                if (Settings.Default.depotID != 12)
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"ERR: Aucune poids après 10 colis  [{depotName}] ligne [{Properties.Settings.Default.lineID}]");
                else
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"ERR: No weight after 10 parcels [{depotName}] line [{Properties.Settings.Default.lineID}]");

                //DALReportSender.SendReport(eNatReports.ConveyorCamMonitoring, $"Erreur aucun poids sur le convoyeur apres 10 colis passés depot:[{Properties.Settings.Default.depotID}] ligne:[{Properties.Settings.Default.lineID}] err:[{gConveyorStats.Ccs.lastScaleErrorCode}]", new List<string>(), "");
                gConveyorStats.Ccs.onScaleErrorSent = true;
                if (Settings.Default.depotID == 2 )
                {
                    Application.Restart();
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"ERR: Applicatoin restart [{depotName}] ");

                }
                else ResetScale();
            }
            if (gConveyorStats.Csc.totContRejectedError > 100 && !gConveyorStats.Ccs.onRejectedErrorSent)
            {
                if (Settings.Default.depotID != 12)
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"ERR: Tout est rejeté après 25 colis [{depotName}] ligne [{Properties.Settings.Default.lineID}]");
                else
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"ERR: All parcels are reject after 25 parcels [{depotName}] line [{Properties.Settings.Default.lineID}]");

                //DALReportSender.SendReport(eNatReports.ConveyorCamMonitoring, $"Erreur Tous les colis sont reject apres 25 colis passés depot:[{Properties.Settings.Default.depotID}] ligne:[{Properties.Settings.Default.lineID}] err:[{gConveyorStats.Ccs.lastScaleErrorCode}]", new List<string>(), "");
                gConveyorStats.Ccs.onRejectedErrorSent = true;
                ErrorFrm frm = new ErrorFrm();
                frm.ShowDialog();
            }

            if (gConveyorStats.Csc.totContCameraError > 100 && !gConveyorStats.Ccs.onCameraErrorSent)
            {
                if (Settings.Default.depotID != 12)
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"ERR: Aucun colis scan après 10 colis [{depotName}] ligne [{Properties.Settings.Default.lineID}]");
                else
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"ERR: No parcel scan after 10 parcels [{depotName}] line [{Properties.Settings.Default.lineID}]");

                //DALReportSender.SendReport(eNatReports.ConveyorCamMonitoring, $"Erreur aucun scan sur le convoyeur depot:[{Properties.Settings.Default.depotID}] ligne:[{Properties.Settings.Default.lineID}] err:[{gConveyorStats.Ccs.lastScaleErrorCode}]", new List<string>(), "");
                gConveyorStats.Ccs.onCameraErrorSent = true;
            }
            if (gConveyorStats.Csc.totContCameraError == 0 && gConveyorStats.Ccs.onCameraErrorSent)
            {
                if (Settings.Default.depotID != 12)
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"OK: Les colis sont maintenant lus [{depotName}] ligne [{Properties.Settings.Default.lineID}]");
                else
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"OK: All parcels are now scan [{depotName}] line [{Properties.Settings.Default.lineID}]");

               // DALReportSender.SendReport(eNatReports.ConveyorCamMonitoring, $"Les Scan sur le convoyeur sont de retour depot:[{Properties.Settings.Default.depotID}] ligne:[{Properties.Settings.Default.lineID}] err:[{gConveyorStats.Ccs.lastScaleErrorCode}]", new List<string>(), "");
                gConveyorStats.Ccs.onCameraErrorSent = false;
            }
            if (gConveyorStats.Csc.totContRejectedError == 0 && gConveyorStats.Ccs.onCameraErrorSent)
            {
                if (Settings.Default.depotID != 12)
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"OK: Les colis ne sont plus rejetés [{depotName}] ligne [{Properties.Settings.Default.lineID}]");
                else
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"OK: All parcels are now back [{depotName}] line [{Properties.Settings.Default.lineID}]");

//                DALReportSender.SendReport(eNatReports.ConveyorCamMonitoring, $"Les Scan sur le convoyeur sont de retour depot:[{Properties.Settings.Default.depotID}] ligne:[{Properties.Settings.Default.lineID}] err:[{gConveyorStats.Ccs.lastScaleErrorCode}]", new List<string>(), "");
                gConveyorStats.Ccs.onCameraErrorSent = false;
            }

            //ping 
            if (gConveyorStats.Ccs.onPingCameraError == true && !gConveyorStats.Ccs.onPingCameraErrorSent)
            {
                DALReportSender.SendReport(eNatReports.ConveyorCamMonitoring, "Ping impossible sur camera "  +Properties.Settings.Default.lineID, new List<string>(), "");
                gConveyorStats.Ccs.onCameraErrorSent = true;
            }
            else if (gConveyorStats.Ccs.onPingCameraError == false && gConveyorStats.Ccs.onPingCameraErrorSent)
            {
                DALReportSender.SendReport(eNatReports.ConveyorCamMonitoring, "Ping de retour sur camera "  +Properties.Settings.Default.lineID, new List<string>(), "");
                gConveyorStats.Ccs.onCameraErrorSent = false;
            }

            if (gConveyorStats.Ccs.onPingDimError == true && !gConveyorStats.Ccs.onPingDimErrorSent)
            {
                Logger.Info("ping impossible... " + gConveyorStats.Ccs.onPingDimError.ToString());
                DALReportSender.SendReport(eNatReports.ConveyorCamMonitoring, "Ping impossible sur dim " + Properties.Settings.Default.lineID, new List<string>(), "");
                gConveyorStats.Ccs.onPingDimErrorSent = true;
            }
            else if (gConveyorStats.Ccs.onPingDimError == false && gConveyorStats.Ccs.onPingDimErrorSent)
            {
                DALReportSender.SendReport(eNatReports.ConveyorCamMonitoring, "Ping de retour sur dim " + Properties.Settings.Default.lineID, new List<string>(), "");
                gConveyorStats.Ccs.onPingDimErrorSent = false;
            }

            if (gConveyorStats.Ccs.onPingScaleError == true && !gConveyorStats.Ccs.onPingScaleErrorSent)
            {
                DALReportSender.SendReport(eNatReports.ConveyorCamMonitoring, "Ping impossible sur balance " + Properties.Settings.Default.lineID, new List<string>(), "");
                gConveyorStats.Ccs.onPingScaleErrorSent = true;
            }
            else if (gConveyorStats.Ccs.onPingScaleError == false && gConveyorStats.Ccs.onPingScaleErrorSent)
            {
                DALReportSender.SendReport(eNatReports.ConveyorCamMonitoring, "Ping de retour sur balance " + Properties.Settings.Default.lineID, new List<string>(), "");
                gConveyorStats.Ccs.onPingScaleErrorSent = false;
            }
        }

        
  

        private void ManageDDEConnection()
        {
            if (!gConveyorStats.Ccs.DDEConnected && gConveyorStats.Ccs.OldDDEConnected)
            {
                if (Settings.Default.depotID != 12)
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"ERR: DDE non-connecté [{depotName}] ligne [{Properties.Settings.Default.lineID}]");
                else
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"ERR: DDE not connected [{depotName}] line [{Properties.Settings.Default.lineID}]");
//                DALReportSender.SendReport(eNatReports.ConveyorCamMonitoring, "ERREUR DDE non connecté depotid= " + Settings.Default.depotID, new List<string>(), "");
            }
            if (gConveyorStats.Ccs.DDEConnected && !gConveyorStats.Ccs.OldDDEConnected)
            {
                if (Settings.Default.depotID != 12)
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"OK: DDE re-connecté [{depotName}] ligne [{Properties.Settings.Default.lineID}]");
                else
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"OK: DDE re-connected [{depotName}] line [{Properties.Settings.Default.lineID}]");

//                DALReportSender.SendReport(eNatReports.ConveyorCamMonitoring, "DDE re-connecté depotid= "+ Settings.Default.depotID, new List<string>(), "");
            }
        }

        private void btnNoDimScaleEnable_Click(object sender, EventArgs e)
        {
            if (gConveyorStats.Ccs.NoDimScaleEnable)
            {
                if (Settings.Default.depotID != 12)
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"WARN: code 98 désactivé [{depotName}] ligne [{Properties.Settings.Default.lineID}]");
                else
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"WARN: code 98 desactivated [{depotName}] line [{Properties.Settings.Default.lineID}]");

                btnNoDimScaleEnable.ForeColor = Color.DarkRed;
                gConveyorStats.Ccs.NoDimScaleEnable = false;
            }
            else
            {
                if (Settings.Default.depotID != 12)
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"OK: code 98 activé [{depotName}] ligne [{Properties.Settings.Default.lineID}]");
                else
                    DALSMSSender.SendSms(Properties.Settings.Default.smsList, $"OK: code 98 activated [{depotName}] line [{Properties.Settings.Default.lineID}]");

                btnNoDimScaleEnable.ForeColor = Color.DarkGreen;
                gConveyorStats.Ccs.NoDimScaleEnable = true;
            }
        }

       

        private void btnup_Click(object sender, EventArgs e)
        {
            delay += 10;
        }

        private void btndown_Click(object sender, EventArgs e)
        {
            if (delay >=10)
                delay -= 10;
        }

        private bool IsCode86(string _cameraData)
        {
            object o = DBLocalConnection.Instance.ExecuteScalar(string.Format("select cnt from code86 where camera_data={0}", StrUtils.QuotedStr(_cameraData)));
            if (o != null)
            {
                if (o.ToIntNoException() == Properties.Settings.Default.Code86Retry)
                {
                    DBLocalConnection.Instance.ExecuteCommand(string.Format("delete from code86 where camera_data={0}", StrUtils.QuotedStr(_cameraData)));
                    return false;
                }
                else
                {
                    DBLocalConnection.Instance.ExecuteCommand(string.Format("update code86 set cnt = cnt + 1 where camera_data={0}", StrUtils.QuotedStr(_cameraData)));
                    return true;
                }
            }
            else
            {
                DBLocalConnection.Instance.ExecuteCommand(string.Format("insert into code86 (camera_data,cnt) values({0},1)", StrUtils.QuotedStr(_cameraData)));
                return true;
            }
        }
        private bool IsCode98(string _cameraData)
        {
            object o = DBLocalConnection.Instance.ExecuteScalar(string.Format("select cnt from code98 where camera_data={0}", StrUtils.QuotedStr(_cameraData)));
            if (o != null)
            {
                if (o.ToIntNoException() == Properties.Settings.Default.Code86Retry)
                {
                    DBLocalConnection.Instance.ExecuteCommand(string.Format("delete from code98 where camera_data={0}", StrUtils.QuotedStr(_cameraData)));
                    return false;
                }
                else
                {
                    DBLocalConnection.Instance.ExecuteCommand(string.Format("update code98 set cnt = cnt + 1 where camera_data={0}", StrUtils.QuotedStr(_cameraData)));
                    return true;
                }
            }
            else
            {
                DBLocalConnection.Instance.ExecuteCommand(string.Format("insert into code98 (camera_data,cnt) values({0},1)", StrUtils.QuotedStr(_cameraData)));
                return true;
            }
        }
        private int GetNbCode98()
        {
            object o = DBLocalConnection.Instance.ExecuteScalar(string.Format("select count(*) from code98"));
            return o.ToIntNoException();
        }
        private int GetNbCode86()
        {
            object o = DBLocalConnection.Instance.ExecuteScalar(string.Format("select count(*) from code86"));
            return o.ToIntNoException();
        }
        private int RemoveCode98(string _cameraData)
        {
               return DBLocalConnection.Instance.ExecuteCommand(string.Format("delete from code98 where camera_data={0}", StrUtils.QuotedStr(_cameraData)));
        }
        private int RemoveCode86(string _cameraData)
        {
            return DBLocalConnection.Instance.ExecuteCommand(string.Format("delete from code86 where camera_data={0}", StrUtils.QuotedStr(_cameraData)));
        }

        private void button6_Click(object sender, EventArgs e)
        {
            // gConveyorStats.Ccs.toResetLocalDB = true;
            if (Properties.Settings.Default.lineID == 1)
            {
                DDe.Poke("C5:36.ACC", "0"); //send chute no to conveyor
                DDe.Poke("C5:37.ACC", "0"); //send chute no to conveyor
            }
            else
            {
                DDe.Poke("C5:33.ACC", "0"); //send chute no to conveyor
                DDe.Poke("C5:35.ACC", "0"); //send chute no to conveyor
            }
        }

        private void button7_Click(object sender, EventArgs e)
        {
            Logger.Info("Reset data for endofDay !");
            gConveyorStats.EndOfDay(TimeSpan.Parse(Properties.Settings.Default.EodTime).Hours);
        }

        private void btnPostal_Click(object sender, EventArgs e)
        {
            
            if (Properties.Settings.Default.postalCodeSort)
                Properties.Settings.Default.postalCodeSort = false;
            else
                Properties.Settings.Default.postalCodeSort = true;

            manualPostalCode = true;
        }


        private void button6_Click_1(object sender, EventArgs e)
        {
            if (Settings.Default.CurrentShift == allShift.Max(p=>p.ConveyorShiftID))
                Settings.Default.CurrentShift = allShift.Min(p => p.ConveyorShiftID);
            else
                Settings.Default.CurrentShift = allShift.OrderBy(p=>p.ConveyorShiftID).FirstOrDefault(p=>p.ConveyorShiftID > Settings.Default.CurrentShift).ConveyorShiftID;

            Settings.Default.Save();

            Logger.Info("Change Shift to : " + Settings.Default.CurrentShift);

            if (Settings.Default.CurrentShift == 9)
                rejectedChute = Settings.Default.NoRead;
            else rejectedChute = Settings.Default.RejectedChute;


            DBConnection.Instance.ExecuteCommand($"update nationex.depot set current_shift_id={Settings.Default.CurrentShift} where depotnumber={Settings.Default.depotID}");

            button6.Text = allShift.FirstOrDefault(p=>p.ConveyorShiftID ==Settings.Default.CurrentShift).Name;
        }

        private void LoadShift()
        {
            if (Settings.Default.SortType == 0)
            {
                button6.Enabled = false;
                button6.Hide();
                button7.Hide();
                return;
            }

            allShift = DALConveyorShift.GetAll();

            int currentShift =DBConnection.Instance.ExecuteScalar($"select current_shift_id from nationex.depot where depotnumber={Settings.Default.depotID}").ToIntNoException();

            Settings.Default.CurrentShift = currentShift; 
            button6.Text = allShift.FirstOrDefault(p => p.ConveyorShiftID == Settings.Default.CurrentShift).Name;

            //button6.Text = allShift.FirstOrDefault(i=>i.ConveyorShiftID == allShift.Min(p=>p.ConveyorShiftID)).Name;
        }

        private void button7_Click_1(object sender, EventArgs e)
        {
            MessageBox.Show("Veuillez aller dans Natpro/Opérations/Gestion des convoyeurs   / Please go in NatPro/Operations/Conveyor management");
            return;

            RouteConfigFrm frm = new RouteConfigFrm();
            frm.frm = this;
            frm.ShowDialog();
        }

        private void btnResetDimScale_Click(object sender, EventArgs e)
        {
            try
            {
                System.Windows.Forms.Cursor.Current = System.Windows.Forms.Cursors.WaitCursor;

                ResetScale();
                Logger.Info("Reset Done !");
                System.Windows.Forms.Cursor.Current = System.Windows.Forms.Cursors.Default;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        private void ResetDim()
        {
            Logger.Info("Reset Dim !");

            //reset Dimentioner
            if (Settings.Default.DimConverterType == "Old")
                SerialConverter.ResetOldConverter(Settings.Default.DimConverterIP);
            else
                SerialConverter.ResetNewConverter(Settings.Default.DimConverterIP);

            Logger.Info("DimTCPserver.Stop !");
            DimTCPserver.Stop();
            Logger.Info("after DimTCPserver.Stop !");

            if (DimTCPClient != null)
            {
                Logger.Info("DimTCPClient.Stop !");
                DimTCPClient.Close();
            }
        }

        private void ResetScale()
        {
            Logger.Info("Reset Scale !");
            // Reset Scale
            if (Settings.Default.ScaleConverterType == "Old")
                SerialConverter.ResetOldConverter(Settings.Default.ScaleConverterIP);
            else
                SerialConverter.ResetNewConverter(Settings.Default.ScaleConverterIP);

            Logger.Info("ScaleTCPserver.Stop !");

            ScaleTCPserver.Stop();
            if (ScaleTCPClient != null)
            {
                Logger.Info("ScaleTCPClient.Stop !");

                ScaleTCPClient.Close();
            }
        }

        private void btnResetDim_Click(object sender, EventArgs e)
        {
            try
            {
                System.Windows.Forms.Cursor.Current = System.Windows.Forms.Cursors.WaitCursor;

                ResetDim();
                Logger.Info("Reset Done !");
                System.Windows.Forms.Cursor.Current = System.Windows.Forms.Cursors.Default;
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
            }
        }

        

        private void button1_Click_1(object sender, EventArgs e)
        {
            gConveyorStats.Ccs.lastCameraData = textTest.Text;
            TriggerGetChute();
        }

        private void lblLastDisable_code98_Click(object sender, EventArgs e)
        {

        }
    }
}
