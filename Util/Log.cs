using UnityEngine;

namespace ImpatientCommuters.Util
{
    internal static class Log
    {
        private const string Prefix = "ImpatientCommuters: ";

#if DEBUG
        internal static bool DebugEnabled = true;
#else
        internal static bool DebugEnabled = false;
#endif

        internal static void Info(string message) => Debug.Log(Prefix + message);
        internal static void Warning(string message) => Debug.LogWarning(Prefix + message);
        internal static void Error(string message) => Debug.LogError(Prefix + message);

        internal static void DebugLog(string message)
        {
            if (DebugEnabled)
                Debug.Log(Prefix + message);
        }
    }
}
