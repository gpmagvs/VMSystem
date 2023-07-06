using VMSystem.AGV;

namespace VMSystem.TrafficControl
{
    /// <summary>
    /// 跟車模組
    /// </summary>
    public class clsAGVTracking
    {
        /// <summary>
        /// 領頭 AGV
        /// </summary>
        public IAGV Lead_AGV { get; set; }

        /// <summary>
        /// 跟隨 AGV
        /// </summary>
        public IAGV Follower_AGV { get; set; }  


        public void StartTrack()
        {
        }
    }
}
