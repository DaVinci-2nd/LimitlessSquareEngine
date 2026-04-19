using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LimitlessSquareEngine
{
    public static class EngineLaunchArgumentBuilder
    {
        private static string BuildResourceLoadArgument(ResourceLoadTiming resourceLoadTiming)
        {
            return resourceLoadTiming == ResourceLoadTiming.Lazy
                ? "--resource-load=lazy"
                : "--resource-load=preload";
        }

        public static string BuildGameModeArguments(
            string gameStartupFolder,
            ResourceLoadTiming resourceLoadTiming = ResourceLoadTiming.Lazy)
        {
            string folder = string.IsNullOrWhiteSpace(gameStartupFolder)
                ? AppContext.BaseDirectory
                : gameStartupFolder;

            string resourceLoadArgument = BuildResourceLoadArgument(resourceLoadTiming);
            return $"--game --game-startup-folder=\"{folder}\" {resourceLoadArgument}";
        }

        public static string BuildEditorModeArguments(
            string gameStartupFolder,
            ResourceLoadTiming resourceLoadTiming = ResourceLoadTiming.Lazy)
        {
            string folder = string.IsNullOrWhiteSpace(gameStartupFolder)
                ? AppContext.BaseDirectory
                : gameStartupFolder;

            string resourceLoadArgument = BuildResourceLoadArgument(resourceLoadTiming);
            return $"--editor --game-startup-folder=\"{folder}\" {resourceLoadArgument}";
        }
    }
}
