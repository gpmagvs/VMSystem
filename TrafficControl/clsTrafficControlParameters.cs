namespace VMSystem.TrafficControl
{
    public class clsTrafficControlParameters
    {

        public clsVehicleGeometryExpands VehicleGeometryExpands { get; set; } = new clsVehicleGeometryExpands();

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
        }


        public class clsVehicleGeometryExpand
        {
            public double Length { get; set; } = 1.0;
            public double Width { get; set; } = 1.0;
        }
    }


}
