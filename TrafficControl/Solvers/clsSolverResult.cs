using AGVSystemCommonNet6.MAP;

namespace VMSystem.TrafficControl.Solvers
{
    public class clsSolverResult
    {
        public clsSolverResult() { }
        public clsSolverResult(SOLVER_RESULT Status)
        {
            this.Status = Status;
        }
        public SOLVER_RESULT Status { get; set; } = SOLVER_RESULT.SUCCESS;
        public enum SOLVER_RESULT
        {
            SUCCESS, CANCEL, FAIL
        }

    }
}
