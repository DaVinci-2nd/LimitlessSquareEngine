using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LimitlessSquareEngine
{
    public static class EngineLaunchArgumentBuilder
    {
        public static string BuildGameModeArguments(string gameStartupFolder)
        {
            string folder = string.IsNullOrWhiteSpace(gameStartupFolder)
                ? AppContext.BaseDirectory
                : gameStartupFolder;

            return $"--game --game-startup-folder=\"{folder}\"";
        }

        public static string BuildEditorModeArguments(string gameStartupFolder)
        {
            string folder = string.IsNullOrWhiteSpace(gameStartupFolder)
                ? AppContext.BaseDirectory
                : gameStartupFolder;

            return $"--editor --game-startup-folder=\"{folder}\"";
        }
    }
}
