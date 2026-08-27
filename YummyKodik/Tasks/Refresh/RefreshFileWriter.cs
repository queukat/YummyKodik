using System.Xml;

namespace YummyKodik.Tasks.Refresh;

internal static class RefreshFileWriter
{
    private static readonly TimeSpan[] ReplaceRetryDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400)
    ];

    public static async Task<TextWriteOutcome> WriteTextAtomicallyAsync(
        string path,
        string content,
        RefreshPerformanceMetrics? perf,
        string artifactKind,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException($"Failed to determine directory for path '{path}'.");
        }

        Directory.CreateDirectory(directory);

        var hasExistingFile = File.Exists(path);
        if (hasExistingFile)
        {
            var existingContent = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            if (string.Equals(existingContent, content, StringComparison.Ordinal))
            {
                perf?.AddCount($"io.{artifactKind}_unchanged");
                return TextWriteOutcome.Unchanged;
            }
        }

        var tempPath = Path.Combine(
            directory,
            Path.GetFileName(path) + ".tmp." + Guid.NewGuid().ToString("N"));

        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
            await MoveTempFileIntoPlaceAsync(tempPath, path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // ignore temp cleanup failures
                }
            }
        }

        var outcome = hasExistingFile ? TextWriteOutcome.Updated : TextWriteOutcome.Created;
        perf?.AddCount($"io.{artifactKind}_{GetMetricSuffix(outcome)}");
        return outcome;
    }

    private static async Task MoveTempFileIntoPlaceAsync(
        string tempPath,
        string path,
        CancellationToken cancellationToken)
    {
        var originalAttributes = TryGetFileAttributes(path);

        try
        {
            if (originalAttributes.HasValue)
            {
                TryClearReadOnly(path, originalAttributes.Value);
            }

            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ShouldRetryReplace(ex, attempt) && File.Exists(tempPath))
                {
                    await Task.Delay(ReplaceRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (originalAttributes.HasValue && File.Exists(path))
            {
                TryRestoreAttributes(path, originalAttributes.Value);
            }
        }
    }

    private static FileAttributes? TryGetFileAttributes(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetAttributes(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryClearReadOnly(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReadOnly) == 0)
        {
            return;
        }

        try
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The replace below will surface a useful failure if the file cannot be updated.
        }
    }

    private static bool ShouldRetryReplace(Exception ex, int attempt)
    {
        return attempt < ReplaceRetryDelays.Length &&
               ex is IOException or UnauthorizedAccessException;
    }

    private static void TryRestoreAttributes(string path, FileAttributes attributes)
    {
        try
        {
            File.SetAttributes(path, attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort only; content durability is more important than metadata restoration.
        }
    }

    public static async Task<bool> IsValidXmlFileAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return IsValidXmlContent(content);
    }

    public static bool IsValidXmlContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreWhitespace = true
            };

            using var reader = XmlReader.Create(new StringReader(content), settings);
            return reader.MoveToContent() == XmlNodeType.Element;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static string GetMetricSuffix(TextWriteOutcome outcome)
    {
        return outcome switch
        {
            TextWriteOutcome.Created => "created",
            TextWriteOutcome.Updated => "updated",
            _ => "unchanged"
        };
    }
}
