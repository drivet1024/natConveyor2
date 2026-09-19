using Nat.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nat_Conveyor
{
    public class GlobalStatus
    {
        public bool CameraTCPConnected { get; set; } = false;
        public bool DimensionTCPConnected { get; set; } = false;
        public bool ScaleTCPConnected { get; set; } = false;
        public bool SQLConnected { get; set; } = false;
        public bool DDEConnected { get; set; } = false;

        public bool OldCameraTCPConnected { get; set; } = true;
        public bool OldDimensionTCPConnected { get; set; } = true;
        public bool OldScaleTCPConnected { get; set; } = true;
        public bool OldSQLConnected { get; set; } = true;
        public bool OldDDEConnected { get; set; } = true;

        public bool dimEnable { get; set; } = true;
        public bool scaleEnable { get; set; } = true;

        public DateTime dtLastScale { get; set; }
        public DateTime dtLastDimension { get; set; }
        public DateTime dtCamera { get; set; }

        public Dimension lastDimension {get;set;}
        public decimal lastScale { get; set; }
        public string lastCameraData { get; set; }
        public string lastBareCode { get; set; }
        public string lastPostalCode { get; set; }

        public bool toResetLocalDB { get; set; }
        public string error { get; set; }

        public bool onScaleError { get; set; }
        public bool onDimError { get; set; }
        public bool onCameraError { get; set; }

        public bool onScaleErrorSent { get; set; }
        public bool onDimErrorSent { get; set; }
        public bool onCameraErrorSent { get; set; }

        public bool code86 { get; set; } = false;
        public bool NoDimScaleEnable { get; set; } = false;
    }
}
