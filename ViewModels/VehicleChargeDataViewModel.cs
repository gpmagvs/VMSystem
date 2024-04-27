namespace VMSystem.ViewModels
{
    public class VehicleChargeDataViewModel
    {
        public VehicleChargeDataViewModel()
        {
            this.agvName = "";
            this.lowLevel = 20;
            this.middleLevel = 50;
            this.highLevel = 99;
        }
        public VehicleChargeDataViewModel(string agvName, double low, double middle, double high)
        {
            this.agvName = agvName;
            this.lowLevel = low;
            this.middleLevel = middle;
            this.highLevel = high;
        }
        public string agvName { get; set; } = "";
        public double lowLevel { get; set; }
        public double middleLevel { get; set; }
        public double highLevel { get; set; }
    }
}
