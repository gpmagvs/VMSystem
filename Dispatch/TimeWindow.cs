namespace VMSystem.Dispatch
{
    public class TimeWindow
    {
        public double StartTime { get; set; }
        public double EndTime { get; set; }
        public Tuple<double, double> Position { get; set; }

        public TimeWindow(double start, double end, Tuple<double, double> position)
        {
            StartTime = start;
            EndTime = end;
            Position = position;
        }
    }
}
