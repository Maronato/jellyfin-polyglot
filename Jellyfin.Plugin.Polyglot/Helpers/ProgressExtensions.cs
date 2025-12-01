using System;

namespace Jellyfin.Plugin.Polyglot.Helpers;

/// <summary>
/// Extension methods for IProgress to provide safe progress reporting.
/// </summary>
public static class ProgressExtensions
{
    /// <summary>
    /// Safely reports progress without throwing if the callback fails.
    /// </summary>
    /// <param name="progress">The progress instance (can be null).</param>
    /// <param name="value">The progress value to report.</param>
    public static void SafeReport(this IProgress<double>? progress, double value)
    {
        if (progress == null)
        {
            return;
        }

        try
        {
            progress.Report(value);
        }
        catch
        {
            // Ignore progress reporting failures
        }
    }
}



