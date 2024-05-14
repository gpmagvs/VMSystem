﻿using AGVSystemCommonNet6.MAP;
using System.Runtime.CompilerServices;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.OrderHandler;
using VMSystem.AGV.TaskDispatch.Tasks;

namespace VMSystem.TrafficControl
{
    public static class Extensions
    {
        public static TaskBase CurrentRunningTask(this IAGV agv)
        {
            return agv.taskDispatchModule.OrderHandler.RunningTask;
        }
        public static OrderHandlerBase CurrentOrderHandler(this IAGV agv)
        {
            return agv.taskDispatchModule.OrderHandler;
        }
        public static TaskBase PreviousSegmentTask(this IAGV agv)
        {
            var completeTaskStack = agv.taskDispatchModule.OrderHandler.CompleteTaskStack;
            if (!completeTaskStack.Any())
                return null;
            return agv.taskDispatchModule.OrderHandler.CompleteTaskStack.ToList().Last();
        }
        public static IEnumerable<IAGV> FilterOutAGVFromCollection(this IEnumerable<IAGV> AGVLIST, IAGV FilterOutAGV)
        {
            return AGVLIST.Where(agv => agv != FilterOutAGV);
        }

        public static IEnumerable<IAGV> FilterOutAGVFromCollection(this IEnumerable<IAGV> AGVLIST, string FilterOutAGVName)
        {
            return AGVLIST.Where(agv => agv.Name != FilterOutAGVName);
        }

        public static bool IsSingleWay(this MapPoint mapoint)
        {
            var ptIndex = StaMap.GetIndexOfPoint(mapoint);
            var ptsToThisPoint = StaMap.Map.Points.Where(pt => pt.Value.Enable && pt.Value.StationType == MapPoint.STATION_TYPE.Normal && pt.Value.TagNumber != mapoint.TagNumber)
                             .Where(pt => pt.Value.Target.Keys.Contains(ptIndex));
            return ptsToThisPoint.Count() <= 2;

        }

        public static MapPoint GetOutPointOfPathWithSingleWay(this IEnumerable<MapPoint> path)
        {
            try
            {

                if (path.Count() < 3)
                    return null;
                var states = path.ToDictionary(pt => pt, pt => pt.IsSingleWay());

                bool existSingleWay = false;
                List<bool> singleWayBoolList = states.Values.ToList();
                int _indexOfSigleWayEnd = -1;
                for (var i = singleWayBoolList.Count - 1; i >= 1; i--)
                {
                    var sate = singleWayBoolList[i];
                    if (sate == true && singleWayBoolList[i - 1] == true)
                    {
                        _indexOfSigleWayEnd = i;
                        existSingleWay = true;
                        break;
                    }
                }
                if (!existSingleWay)
                    return null;

                var pointList = states.Keys.ToList();
                return pointList.FirstOrDefault(pt => pointList.IndexOf(pt) > _indexOfSigleWayEnd && !pt.IsVirtualPoint);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
