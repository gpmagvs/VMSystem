using System.Runtime.CompilerServices;
using VMSystem.AGV;
using VMSystem.AGV.TaskDispatch.Tasks;

namespace VMSystem.TrafficControl
{
    public static class Extensions
    {
        public static TaskBase CurrentRunningTask(this IAGV agv)
        {
            return agv.taskDispatchModule.OrderHandler.RunningTask;
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
    }
}
