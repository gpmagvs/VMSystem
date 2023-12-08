using AGVSystemCommonNet6.AGVDispatch.Messages;
using AGVSystemCommonNet6.AGVDispatch.Model;
using System.Drawing;

namespace VMSystem.Tools
{
    public class NavigationTools
    {

        public static double CalculationForwardAngle(PointF startPt, PointF endPt)
        {

            //double deltaX = startPt.X - endPt.X;
            //double deltaY = startPt.Y - endPt.Y;
            double deltaX = endPt.X - startPt.X;
            double deltaY = endPt.Y - startPt.Y;
            // 使用 Atan2 來計算弧度，然後轉換為度
            double angleInRadians = Math.Atan2(deltaY, deltaX);
            double angleInDegrees = angleInRadians * (180 / Math.PI);
            // 將角度調整到 -180 至 180 度的範圍
            if (angleInDegrees > 180)
            {
                angleInDegrees -= 360;
            }
            return angleInDegrees;

        }

        internal static double CalculationForwardAngle(clsCoordination startCoordination, clsCoordination endCoordination)
        {
            return CalculationForwardAngle(new PointF((float)startCoordination.X, (float)startCoordination.Y), new PointF((float)endCoordination.X, (float)endCoordination.Y));
        }
    }
}
