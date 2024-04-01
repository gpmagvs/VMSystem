using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.Microservices.VMS;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.AGV
{
    public class clsGPMInspectionAGV : clsGPMForkAGV
    {
        public override clsEnums.VMS_GROUP VMSGroup { get; set; } = clsEnums.VMS_GROUP.GPM_INSPECTION_AGV;
        public override AGV_TYPE model { get; set; } = AGV_TYPE.INSPECTION_AGV;

        protected override void CreateTaskDispatchModuleInstance()
        {
            taskDispatchModule = new clsInspectionAGVTaskDispatchModule(this);
        }
        public clsGPMInspectionAGV(string name, clsAGVOptions options) : base(name, options)
        {

        }
        public override async Task<(bool confirm, string message)> Locating(clsLocalizationVM localizationVM)
        {

            if (options.Simulation)
            {
                AgvSimulation.runningSTatus.Last_Visited_Node = localizationVM.currentID;

                AgvSimulation.runningSTatus.Last_Visited_Node = localizationVM.currentID;
                var _mapPoint = StaMap.GetPointByTagNumber(localizationVM.currentID);
                AgvSimulation.runningSTatus.Coordination.X = _mapPoint.X;
                AgvSimulation.runningSTatus.Coordination.Y = _mapPoint.Y;

                return (true, "");
            }

            // var response = new { Success = result.confirm, Message = result.message };
            Dictionary<string, object> response = await AGVHttp.PostAsync<Dictionary<string, object>, clsLocalizationVM>("api/AGV/Localization", localizationVM, 10);
            return ((bool)response["Success"], response["Message"].ToString());
        }

        public class clsLocalizationVM
        {
            public int currentID { get; set; }
            public double x { get; set; }
            public double y { get; set; }
            public double theata { get; set; }
        }
    }
}
