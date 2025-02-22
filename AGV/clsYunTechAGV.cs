﻿using AGVSystemCommonNet6;
using AGVSystemCommonNet6.DATABASE;
using AGVSystemCommonNet6.Microservices.VMS;
using static AGVSystemCommonNet6.clsEnums;

namespace VMSystem.AGV
{
    public class clsYunTechAGV : clsAGV
    {
        public override clsEnums.VMS_GROUP VMSGroup { get; set; } = clsEnums.VMS_GROUP.YUNTECH_FORK;
        public override AGV_TYPE model { get; set; } = AGV_TYPE.FORK;
        public clsYunTechAGV(string name, clsAGVOptions connections, AGVSDbContext dbContext) : base(name, connections, dbContext)
        {
            logger.Info($"AGV {name} Create. MODEL={model} ");
        }

    }
}
