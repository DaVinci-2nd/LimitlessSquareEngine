using System;

namespace LimitlessSquareEngine.Engine
{
    public static class GameClock
    {
        public static double Now { get; private set; }

        public static double TotalDays => Now / 86400.0;

        internal static void Advance(double deltaTime)
        {
            if (double.IsNaN(deltaTime) || deltaTime <= 0.0)
                return;

            Now += deltaTime;
        }
    }
}
