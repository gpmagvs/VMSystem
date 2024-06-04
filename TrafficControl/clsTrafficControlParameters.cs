using AGVSystemCommonNet6.Configuration;

namespace VMSystem.TrafficControl
{
    public class clsTrafficControlParameters
    {
        public clsAGVTaskControlConfigs Basic { get; set; } = new clsAGVTaskControlConfigs();

        public clsVehicleGeometryExpands VehicleGeometryExpands { get; set; } = new clsVehicleGeometryExpands();

        public bool DisableChargeStationEntryPointWhenNavigation { get; set; } = true;

        public class clsVehicleGeometryExpands
        {
            public clsVehicleGeometryExpand LeaveWorkStationGeoExpand { get; set; } = new clsVehicleGeometryExpand()
            {
                Length = 1.8,
                Width = 1.0,
            };
            public clsVehicleGeometryExpand LeaveChargeStationGeoExpand { get; set; } = new clsVehicleGeometryExpand()
            {
                Length = 2.0,
                Width = 2.0,
            };
            public clsVehicleGeometryExpand SpinOnPointGeoExpand { get; set; } = new clsVehicleGeometryExpand()
            {
                Length = 1.3,
                Width = 2
            };

            public clsVehicleGeomertyExpandWhenNavigation NavigationGeoExpand { get; set; } = new clsVehicleGeomertyExpandWhenNavigation()
            {
                Length = 1.4,
                Width = 1.4,
                LengthExpandWhenRotation = 1.2
            };

        }

        public class clsVehicleGeomertyExpandWhenNavigation : clsVehicleGeometryExpand
        {
            public double LengthExpandWhenRotation { get; set; } = 1.2;
        }

        public class clsVehicleGeometryExpand
        {
            public double Length { get; set; } = 1.0;
            public double Width { get; set; } = 1.0;
        }
    }


}
