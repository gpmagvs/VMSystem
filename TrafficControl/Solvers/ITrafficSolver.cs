namespace VMSystem.TrafficControl.Solvers
{
    public interface ITrafficSolver
    {
        public enum TRAFFIC_SOLVER_SITUATION
        {

        }
        public TRAFFIC_SOLVER_SITUATION Situation { get; set; }
        public Task<clsSolverResult> Solve();
    }
}
