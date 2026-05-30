using Microsoft.Extensions.Logging;
using YummyKodik.Configuration;

namespace YummyKodik.Logging;

internal static class YummyKodikLogFilter
{
    public const string DefaultMinimumLogLevel = "Warning";

    public static Func<PluginConfiguration?> ConfigurationProvider { get; set; } = static () => null;

    public static bool ShouldLogPluginCategory(LogLevel logLevel)
    {
        return ShouldLog(logLevel, ConfigurationProvider());
    }

    public static bool ShouldLogCategory(string? categoryName, LogLevel logLevel)
    {
        return !IsYummyKodikCategory(categoryName) ||
               ShouldLog(logLevel, ConfigurationProvider());
    }

    public static bool ShouldLog(string? providerName, string? categoryName, LogLevel logLevel)
    {
        if (!IsYummyKodikCategory(categoryName))
        {
            return true;
        }

        return ShouldLog(logLevel, ConfigurationProvider());
    }

    internal static bool ShouldLog(LogLevel logLevel, PluginConfiguration? configuration)
    {
        if (logLevel == LogLevel.None)
        {
            return false;
        }

        var minimumLogLevel = ParseMinimumLogLevel(configuration?.MinimumLogLevel);
        return minimumLogLevel != LogLevel.None && logLevel >= minimumLogLevel;
    }

    internal static LogLevel ParseMinimumLogLevel(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            normalized = DefaultMinimumLogLevel;
        }

        return Enum.TryParse<LogLevel>(normalized, ignoreCase: true, out var logLevel)
            ? logLevel
            : LogLevel.Warning;
    }

    private static bool IsYummyKodikCategory(string? categoryName)
    {
        return !string.IsNullOrWhiteSpace(categoryName) &&
               (string.Equals(categoryName, "YummyKodik", StringComparison.Ordinal) ||
                categoryName.StartsWith("YummyKodik.", StringComparison.Ordinal));
    }
}
