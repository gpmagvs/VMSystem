using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.Microservices.VMS;
using VMSystem.TrafficControl;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.AGV
{
    public class clsGPMInspectionAGV : clsAGV
    {
        public override clsEnums.VMS_GROUP VMSGroup { get; set; } = clsEnums.VMS_GROUP.GPM_INSPECTION_AGV;
        public override AGV_TYPE model { get; set; } = AGV_TYPE.INSPECTION_AGV;

        public override ONLINE_STATE online_state
        {
            get => base.online_state;
            set
            {
                if (base.online_state != value)
                {
                    base.online_state = value;
                    Task.Run(async () =>
                    {
                        bool registedSuccess = false;
                        LOG.INFO($"Try Unregist Regions when Online State Changed to {value}");
                        while (!(registedSuccess = await TrafficControl.PartsAGVSHelper.UnRegistStationExceptSpeficStationName(new List<string>() { this.currentMapPoint.Graph.Display })))
                        {
                            await Task.Delay(1000);
                        }
                    });
                }
            }
        }
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
