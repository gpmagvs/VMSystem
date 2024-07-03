using Newtonsoft.Json;
using RosSharp.RosBridgeClient;
using SQLitePCL;
using System.Collections.Concurrent;
using VMSystem.TrafficControl.ConflicDetection;

namespace VMSystem.AGV.TaskDispatch.Tasks
{
    public class TaskDiagnosis
    {
        public Traffic traffic = new Traffic();
        public TaskDiagnosis()
        { }
        public class Traffic
        {
            public Dictionary<string, TaskDiagnosis.Traffic.TaskTrafficObject> dict_TaskTraffic = new Dictionary<string, TaskTrafficObject>();
            //public ConcurrentDictionary<int, int> dict_AvoidPoint_Count = new ConcurrentDictionary<int, int>();
            //public ConcurrentDictionary<int, Dictionary<int, TaskDiagnosis.Traffic.ConflictPointObject>> dict_ConflictPoint = new ConcurrentDictionary<int, Dictionary<int, ConflictPointObject>>();
            private async void CheckTaskExist(string strTaskName)
            {
                if (dict_TaskTraffic.ContainsKey(strTaskName) == false)
                {
                    dict_TaskTraffic.Add(strTaskName, new TaskTrafficObject());
                    dict_TaskTraffic[strTaskName].strTaskName = strTaskName;
                    dict_TaskTraffic[strTaskName].dt = DateTime.Now;
                }
            }

            public void AddAvoidPointTag(string strTaskName, int tag)
            {
                CheckTaskExist(strTaskName);
                if (dict_TaskTraffic[strTaskName].dict_AvoidPoint_Count.ContainsKey(tag) == false)
                    dict_TaskTraffic[strTaskName].dict_AvoidPoint_Count.TryAdd(tag, 0);
                dict_TaskTraffic[strTaskName].dict_AvoidPoint_Count[tag]++;
            }
            //public void AddAvoidPointTag(int tag)
            //{
            //    if (dict_AvoidPoint_Count.ContainsKey(tag) == false)
            //        dict_AvoidPoint_Count.TryAdd(tag, 0);
            //    dict_AvoidPoint_Count[tag]++;
            //}
            public void AddConflictPoint(string strTaskName, int from, int to)
            {
                CheckTaskExist(strTaskName);
                if (dict_TaskTraffic[strTaskName].dict_ConflictPoint.ContainsKey(from) == false)
                    dict_TaskTraffic[strTaskName].dict_ConflictPoint.Add(from, new Dictionary<int, ConflictPointObject>());
                if (dict_TaskTraffic[strTaskName].dict_ConflictPoint[from].ContainsKey(to) == false)
                {
                    dict_TaskTraffic[strTaskName].dict_ConflictPoint[from].Add(to, new ConflictPointObject());
                    dict_TaskTraffic[strTaskName].dict_ConflictPoint[from][to].from = from;
                    dict_TaskTraffic[strTaskName].dict_ConflictPoint[from][to].to = to;
                }
                dict_TaskTraffic[strTaskName].dict_ConflictPoint[from][to].counter++;
            }
            public bool CheckTrafficBlock(string strTaskName)
            {
                int SumAvoidTime = dict_TaskTraffic[strTaskName].dict_AvoidPoint_Count.Select(x => x.Value).ToList().Sum();
                if (SumAvoidTime >= 3)
                    return true;
                else
                    return false;
            }
            public void LogResult()
            {
                string strFolder = "C:\\Users\\user\\Downloads\\TaskDiagnosis";
                if (Directory.Exists(strFolder) == false)
                    Directory.CreateDirectory(strFolder);
                string strFile = strFolder + "\\TrafficLog.csv";
                string strAvoidText = string.Empty;
                foreach (var ite in dict_TaskTraffic.OrderBy(x => x.Value.dt))
                {
                    string strTask = JsonConvert.SerializeObject(ite.Value) + "\r\n";
                    strAvoidText += strTask;
                }
                File.AppendAllText(strFile, strAvoidText);
            }
            public class ConflictPointObject
            {
                public int from;
                public int to;
                public int counter = 0;
            }

            public class TaskTrafficObject
            {
                public string strTaskName = string.Empty;
                public DateTime dt;
                public Dictionary<int, int> dict_AvoidPoint_Count = new Dictionary<int, int>();
                public Dictionary<int, Dictionary<int, TaskDiagnosis.Traffic.ConflictPointObject>> dict_ConflictPoint = new Dictionary<int, Dictionary<int, ConflictPointObject>>();
            }

        }
    }
}
