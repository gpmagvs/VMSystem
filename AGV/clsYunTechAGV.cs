﻿using AGVSystemCommonNet6;
using AGVSystemCommonNet6.Log;
using AGVSystemCommonNet6.Microservices.VMS;

namespace VMSystem.AGV
{
    public class clsYunTechAGV : clsGPMForkAGV
    {
        public override clsEnums.VMS_GROUP VMSGroup { get; set; } = clsEnums.VMS_GROUP.YUNTECH_FORK;
        public override clsEnums.AGV_MODEL model { get; set; } = clsEnums.AGV_MODEL.YUNTECH_FORK_AGV;
        public clsYunTechAGV(string name, clsAGVOptions connections) : base(name, connections)
        {
            LOG.INFO($"AGV {name} Create. MODEL={model} ");
        }

        public override Task<object> GetAGVStateFromDB()
        {
            return base.GetAGVStateFromDB();
        }
    }
}
