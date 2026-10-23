using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace BridgEN.App.Ocr;

/// <summary>Bounded local metadata for investigating resets without saving text or images.</summary>
internal static class OcrDiagnostics
{
    private static readonly object Gate = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BridgEN", "logs");

    public static void Record(OcrTextObservation observation, string source, string candidate,
        OcrImageTreatment treatment, bool sourceChanged, int sourceBlocks, int sourceRows)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                utc = DateTimeOffset.UtcNow,
                treatment = treatment.ToString(),
                visualChanged = observation.VisualChanged,
                textChanged = sourceChanged,
                reason = observation.Reason,
                revision = observation.Revision,
                stable = observation.IsStable,
                unchangedMs = (long)observation.UnchangedFor.TotalMilliseconds,
                sourceLength = source.Length,
                candidateLength = candidate.Length,
                acceptedLength = observation.Text.Length,
                sourceBlocks,
                sourceRows,
                candidateRows = candidate.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length,
                acceptedRows = observation.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length
            });
            Debug.WriteLine(line);
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, "ocr-diagnostics.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= 1_048_576)
                {
                    File.Move(path, Path.Combine(LogDirectory, "ocr-diagnostics.previous.jsonl"), overwrite: true);
                }
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"OCR diagnostics unavailable: {exception.Message}");
        }
    }
}
