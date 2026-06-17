namespace CentrED.Utility;

public class Logger
{
    public TextWriter Out = Console.Out;

    public static bool DebugEnabled;

    public void LogInfo(string log)
    {
        Log("INFO", log);
    }

    public void LogWarn(string log)
    {
        Log("WARN", log);
    }

    public void LogError(string log)
    {
        Log("ERROR", log);
    }

    public void LogDebug(string log)
    {
        if (DebugEnabled)
            Log("DEBUG", log);
    }

    internal void Log(string level, string log)
    {
        Out.WriteLine($"[{level}] {DateTime.Now} {log}");
    }
}