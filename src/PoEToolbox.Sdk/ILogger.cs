namespace PoEToolbox.Sdk;

/// <summary>
/// Simple logger interface for plugins.
/// </summary>
public interface ILogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}
