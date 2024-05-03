namespace VMSystem.Dispatch
{
    public class Scheduler
    {
        public List<TimeWindow> TimeWindows { get; set; } = new List<TimeWindow>();

        public void AddTimeWindow(TimeWindow window)
        {
            TimeWindows.Add(window);
        }

        public bool CheckForConflicts()
        {
            foreach (var group in TimeWindows.GroupBy(w => w.Position))
            {
                var windowsAtPosition = group.ToList();
                for (int i = 0; i < windowsAtPosition.Count; i++)
                {
                    for (int j = i + 1; j < windowsAtPosition.Count; j++)
                    {
                        if (windowsAtPosition[i].EndTime > windowsAtPosition[j].StartTime && windowsAtPosition[i].StartTime < windowsAtPosition[j].EndTime)
                        {
                            Console.WriteLine($"Conflict detected at position {group.Key} between times {windowsAtPosition[i].StartTime} and {windowsAtPosition[j].EndTime}");
                            return true;
                        }
                    }
                }
            }
            return false;
        }
    }

}
