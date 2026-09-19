using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nat_Conveyor
{
    public class _ConveyorStatsCounter
    {
        public int totalParcelFromCamera { get; set; }      
        public int totalParcelFromDimension { get; set; }
        public int totalParcelFromScale { get; set; }

        public int totalParcel { get; set; }
        public int goodParcel { get; set; }
        public int noRead { get; set; }
        public double totWeight { get; set; }

        public int totDBShip { get; set; }
        public int totDBInsert { get; set; }
        public int totRemoteInsert { get; set; }
        public int totDBPostal { get; set; }

        public int totSortWaybill { get; set; }
        public int totSortPostalCode { get; set; }
        public int totRejected { get; set; }
        public int totScaleError { get; set; }
        public int totDimensionerError { get; set; }
     
        
        public int totContScaleError { get; set; }   
        public int totContDimError { get; set; }
        public int totContCameraError { get; set; }

        public string[] chutes { get; set; }

        public _ConveyorStatsCounter()
        {
            chutes = new string[50];

        }
    }

}
