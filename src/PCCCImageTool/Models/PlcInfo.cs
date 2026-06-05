using System;

namespace PCCCImageTool.Models;

public record PlcInfo(
    int ProcessorType,
    string Name,
    bool SupportsUploadDownload,
    string Family,    // "SLC", "MicroLogix", "PLC5", "Unknown"
    string Bulletin,  // e.g. "5/03"
    byte SeriesRevision,
    byte RamKb,
    string ModeStr)   // e.g. "RUN" or "PROG"
{
    /// <summary>
    /// Generates a default filename using the actual run mode string.
    /// </summary>
    /// <param name="modeStr">e.g. "RUN" or "PROG"</param>
    public string GetDefaultFileName(string modeStr = "UNKNOWN")
    {
        string familyTag = Family switch
        {
            "SLC"        => "SLC",
            "MicroLogix" => "ML",
            "PLC5"       => "PLC5",
            _            => "PLC"
        };

        string bulletinTag = string.IsNullOrWhiteSpace(Bulletin)
            ? "unknown"
            : SanitizeSegment(Bulletin);

        string safeMode = SanitizeSegment(modeStr);
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        return $"{familyTag}_{ProcessorType:X2}_{bulletinTag}_{safeMode}_{timestamp}.bin";
    }

    /// <summary>
    /// Replaces characters that are invalid in file names (on any platform)
    /// with underscores, then trims leading/trailing underscores and whitespace.
    /// </summary>
    private static string SanitizeSegment(string input)
    {
        // Union of invalid chars across Windows, Linux, and macOS
        char[] invalid = System.IO.Path.GetInvalidFileNameChars();

        var sb = new System.Text.StringBuilder(input.Length);
        foreach (char c in input)
            sb.Append(Array.IndexOf(invalid, c) >= 0 || c == ' ' ? '_' : c);

        // Collapse repeated underscores, e.g. "5__03" → "5_03"
        string result = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "_+", "_");
        return result.Trim('_');
    }
}
