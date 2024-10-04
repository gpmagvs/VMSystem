using AGVSystemCommonNet6.Configuration;
using static AGVSystemCommonNet6.MAP.PathFinder;

namespace VMSystem.TrafficControl
{
    public class clsTrafficControlParameters
    {
        public clsAGVTaskControlConfigs Basic { get; set; } = new clsAGVTaskControlConfigs();

        public clsNavigationConfigs Navigation { get; set; } = new clsNavigationConfigs();

        public clsVehicleGeometryExpands VehicleGeometryExpands { get; set; } = new clsVehicleGeometryExpands();

        public bool DisableChargeStationEntryPointWhenNavigation { get; set; } = true;

        /// <summary>
        /// 實驗性功能
        /// </summary>
        public clsExperimentalFeatureSettings Experimental { get; set; } = new clsExperimentalFeatureSettings();

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

        public class clsNavigationConfigs
        {
            public Dictionary<string, string> Notes { get; set; } = new Dictionary<string, string>
            {
                { "PathPlanAlgorithm","0:Dijsktral, 1:DFS" }
            };
            /// <summary>
            /// 路徑規劃演算法(0:Dijsktral, 1:DFS)
            /// </summary>
            public PathFinderOption.ALGORITHM PathPlanAlgorithm { get; set; } = PathFinderOption.ALGORITHM.DFS;
            /// <summary>
            /// 等待因設備零件更換而無法通行的點位之等待時間上限 ,Unit:sec
            /// </summary>
            public int TimeoutWhenWaitPtPassableByEqPartReplacing { get; set; } = 30;
            /// <summary>
            /// 背景監視碰撞發生 
            /// </summary>
            public bool PathConflicBackgroundMonitor { get; set; } = true;
        }
        public class clsVehicleGeometryExpand
        {
            public double Length { get; set; } = 1.0;
            public double Width { get; set; } = 1.0;
        }

        /// <summary>
        /// 實驗性功能參數
        /// </summary>
        public class clsExperimentalFeatureSettings
        {
            /// <summary>
            /// 當派車拒絕VMS進行AGV取放貨動作後，是否要將車子轉向避車角度
            /// </summary>
            public bool TurnToAvoidDirectionWhenLDULDActionReject { get; set; } = false;
        }
    }


}
