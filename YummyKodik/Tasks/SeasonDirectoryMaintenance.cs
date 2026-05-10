using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace YummyKodik.Tasks
{
    internal static class SeasonDirectoryMaintenance
    {
        internal static string PrepareSeasonDirectory(ILogger logger, string seriesRoot, string seasonDir, int seasonNumber)
        {
            if (string.IsNullOrWhiteSpace(seasonDir))
            {
                return seasonDir;
            }

            Directory.CreateDirectory(seasonDir);
            TryMigrateIncorrectCalendarSeasonFolder(logger, seriesRoot, seasonDir, seasonNumber);
            MigrateLegacySeasonFolder(logger, seriesRoot, seasonDir, seasonNumber);
            MigrateLegacyEpisodeFileNames(logger, seasonDir, seasonNumber);
            return seasonDir;
        }

        private static void TryMigrateIncorrectCalendarSeasonFolder(ILogger logger, string seriesRoot, string seasonDir, int seasonNumber)
        {
            if ((seasonNumber != 1 && seasonNumber != 0) || string.IsNullOrWhiteSpace(seriesRoot) || string.IsNullOrWhiteSpace(seasonDir))
            {
                return;
            }

            if (!Directory.Exists(seriesRoot))
            {
                return;
            }

            try
            {
                var mistakenDirs = Directory.EnumerateDirectories(seriesRoot, "Season *", SearchOption.TopDirectoryOnly)
                    .Where(path => !string.Equals(path, seasonDir, StringComparison.OrdinalIgnoreCase))
                    .Select(path => new
                    {
                        Path = path,
                        Match = Regex.Match(
                            Path.GetFileName(path) ?? string.Empty,
                            @"^Season (?<season>\d{2})$",
                            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                            matchTimeout: TimeSpan.FromSeconds(1))
                    })
                    .Where(x => x.Match.Success)
                    .Select(x => new
                    {
                        x.Path,
                        Season = int.Parse(x.Match.Groups["season"].Value)
                    })
                    .Where(x => seasonNumber == 0 ? x.Season > 0 : x.Season > 1)
                    .ToList();

                foreach (var mistakenDir in mistakenDirs)
                {
                    var movedCount = seasonNumber == 0
                        ? MoveAllSeasonArtifacts(logger, mistakenDir.Path, seasonDir, seasonNumber)
                        : MoveSeasonArtifactsForSeason(logger, mistakenDir.Path, seasonDir, seasonNumber);
                    if (movedCount <= 0)
                    {
                        continue;
                    }

                    logger.LogInformation(
                        "[YummyKodik] Reconciled {Count} season {Season} artifact(s) from '{Old}' into '{New}'.",
                        movedCount,
                        seasonNumber,
                        mistakenDir.Path,
                        seasonDir);

                    TryDeleteEmptySeasonDirectory(logger, mistakenDir.Path);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[YummyKodik] Failed to reconcile mistaken season folder under '{SeriesRoot}'.", seriesRoot);
            }
        }

        private static void MigrateLegacySeasonFolder(ILogger logger, string seriesRoot, string seasonDir, int seasonNumber)
        {
            if (seasonNumber == 1 || string.IsNullOrWhiteSpace(seriesRoot) || string.IsNullOrWhiteSpace(seasonDir))
            {
                return;
            }

            var legacySeasonDir = Path.Combine(seriesRoot, "Season 01");
            if (!Directory.Exists(legacySeasonDir))
            {
                return;
            }

            try
            {
                var movedCount = seasonNumber == 0
                    ? MoveAllSeasonArtifacts(logger, legacySeasonDir, seasonDir, seasonNumber)
                    : MoveSeasonArtifactsForSeason(logger, legacySeasonDir, seasonDir, seasonNumber);
                if (movedCount <= 0)
                {
                    return;
                }

                logger.LogInformation(
                    "[YummyKodik] Reconciled {Count} season {Season} artifact(s) from legacy season folder '{Old}' into '{New}'.",
                    movedCount,
                    seasonNumber,
                    legacySeasonDir,
                    seasonDir);

                TryDeleteEmptySeasonDirectory(logger, legacySeasonDir);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[YummyKodik] Failed to reconcile legacy season folder '{Old}' -> '{New}'.", legacySeasonDir, seasonDir);
            }
        }

        private static void MigrateLegacyEpisodeFileNames(ILogger logger, string seasonDir, int seasonNumber)
        {
            if (string.IsNullOrWhiteSpace(seasonDir) || !Directory.Exists(seasonDir))
            {
                return;
            }

            var normalizedSeasonNumber = seasonNumber >= 0 ? seasonNumber : 1;
            var newSeasonPrefix = "S" + normalizedSeasonNumber.ToString("00");

            try
            {
                var files = Directory.EnumerateFiles(seasonDir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(p =>
                    {
                        var ext = Path.GetExtension(p);
                        return ext.Equals(".strm", StringComparison.OrdinalIgnoreCase) ||
                               ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                foreach (var path in files)
                {
                    var fileName = Path.GetFileName(path);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }

                    var nameNoExt = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                    if (!TryGetEpisodeFileSeasonPrefix(nameNoExt, out var currentSeasonPrefix))
                    {
                        continue;
                    }

                    var ext = Path.GetExtension(path);
                    if (string.Equals(currentSeasonPrefix, newSeasonPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        if (ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase))
                        {
                            TryUpdateEpisodeNfoSeason(logger, path, normalizedSeasonNumber);
                        }

                        continue;
                    }

                    var renamedNoExt = newSeasonPrefix + nameNoExt.Substring(3);
                    var target = Path.Combine(seasonDir, renamedNoExt + ext);

                    if (File.Exists(target))
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "[YummyKodik] Failed to delete legacy file '{Path}'.", path);
                        }

                        continue;
                    }

                    try
                    {
                        File.Move(path, target);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "[YummyKodik] Failed to rename file '{Old}' -> '{New}'.", path, target);
                        continue;
                    }

                    if (ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase))
                    {
                        TryUpdateEpisodeNfoSeason(logger, target, normalizedSeasonNumber);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[YummyKodik] Season file migration failed. seasonDir='{SeasonDir}' season={Season}", seasonDir, seasonNumber);
            }
        }

        private static int MoveSeasonArtifactsForSeason(ILogger logger, string sourceDir, string targetDir, int seasonNumber)
        {
            if (string.IsNullOrWhiteSpace(sourceDir) ||
                string.IsNullOrWhiteSpace(targetDir) ||
                !Directory.Exists(sourceDir) ||
                string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            Directory.CreateDirectory(targetDir);

            var movedCount = 0;
            var files = Directory.EnumerateFiles(sourceDir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(p =>
                {
                    var ext = Path.GetExtension(p);
                    return ext.Equals(".strm", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            foreach (var group in files
                         .GroupBy(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                         .Where(g => !string.IsNullOrWhiteSpace(g.Key)))
            {
                var detectedSeason = DetectSeasonNumberFromArtifacts(group);
                if (detectedSeason != seasonNumber)
                {
                    continue;
                }

                foreach (var path in group)
                {
                    var ext = Path.GetExtension(path);
                    var nameNoExt = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                    var targetNameNoExt = RewriteEpisodeFileSeasonPrefix(nameNoExt, seasonNumber);
                    var targetPath = Path.Combine(targetDir, targetNameNoExt + ext);

                    try
                    {
                        if (!string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(targetPath))
                            {
                                File.Delete(path);
                            }
                            else
                            {
                                File.Move(path, targetPath);
                            }
                        }

                        if (ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase))
                        {
                            TryUpdateEpisodeNfoSeason(logger, targetPath, seasonNumber);
                        }

                        movedCount++;
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "[YummyKodik] Failed to move season artifact '{Path}' -> '{TargetPath}'.", path, targetPath);
                    }
                }
            }

            return movedCount;
        }

        private static int MoveAllSeasonArtifacts(ILogger logger, string sourceDir, string targetDir, int seasonNumber)
        {
            if (string.IsNullOrWhiteSpace(sourceDir) ||
                string.IsNullOrWhiteSpace(targetDir) ||
                !Directory.Exists(sourceDir) ||
                string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            Directory.CreateDirectory(targetDir);

            var movedCount = 0;
            var files = Directory.EnumerateFiles(sourceDir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(p =>
                {
                    var ext = Path.GetExtension(p);
                    return ext.Equals(".strm", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            foreach (var group in files
                         .GroupBy(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                         .Where(g => !string.IsNullOrWhiteSpace(g.Key)))
            {
                foreach (var path in group)
                {
                    var ext = Path.GetExtension(path);
                    var nameNoExt = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                    var targetNameNoExt = RewriteEpisodeFileSeasonPrefix(nameNoExt, seasonNumber);
                    var targetPath = Path.Combine(targetDir, targetNameNoExt + ext);

                    try
                    {
                        if (!string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(targetPath))
                            {
                                File.Delete(path);
                            }
                            else
                            {
                                File.Move(path, targetPath);
                            }
                        }

                        if (ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase))
                        {
                            TryUpdateEpisodeNfoSeason(logger, targetPath, seasonNumber);
                        }

                        movedCount++;
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "[YummyKodik] Failed to move special artifact '{Path}' -> '{TargetPath}'.", path, targetPath);
                    }
                }
            }

            return movedCount;
        }

        private static int? DetectSeasonNumberFromArtifacts(IEnumerable<string> paths)
        {
            var artifactPaths = paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (artifactPaths == null || artifactPaths.Count == 0)
            {
                return null;
            }

            var nfoSeasonNumbers = artifactPaths
                .Where(path => Path.GetExtension(path).Equals(".nfo", StringComparison.OrdinalIgnoreCase))
                .Select(TryReadEpisodeSeasonFromNfo)
                .Where(season => season.HasValue && season.Value > 0)
                .Select(season => season!.Value)
                .Distinct()
                .ToList();

            if (nfoSeasonNumbers.Count == 1)
            {
                return nfoSeasonNumbers[0];
            }

            if (nfoSeasonNumbers.Count > 1)
            {
                return null;
            }

            var fileNameSeasonNumbers = artifactPaths
                .Select(path => TryReadEpisodeSeasonFromFileName(Path.GetFileNameWithoutExtension(path) ?? string.Empty))
                .Where(season => season.HasValue && season.Value > 0)
                .Select(season => season!.Value)
                .Distinct()
                .ToList();

            return fileNameSeasonNumbers.Count == 1 ? fileNameSeasonNumbers[0] : null;
        }

        private static int? TryReadEpisodeSeasonFromNfo(string nfoPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nfoPath) || !File.Exists(nfoPath))
                {
                    return null;
                }

                var xml = File.ReadAllText(nfoPath);
                if (string.IsNullOrWhiteSpace(xml))
                {
                    return null;
                }

                var match = Regex.Match(
                    xml,
                    @"<season>\s*(?<season>\d+)\s*</season>",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                    matchTimeout: TimeSpan.FromSeconds(1));

                if (!match.Success ||
                    !int.TryParse(match.Groups["season"].Value, out var seasonNumber) ||
                    seasonNumber <= 0)
                {
                    return null;
                }

                return seasonNumber;
            }
            catch
            {
                return null;
            }
        }

        private static int? TryReadEpisodeSeasonFromFileName(string fileNameWithoutExtension)
        {
            return TryGetEpisodeFileSeasonPrefix(fileNameWithoutExtension, out var seasonPrefix) &&
                   int.TryParse(seasonPrefix.AsSpan(1), out var seasonNumber) &&
                   seasonNumber > 0
                ? seasonNumber
                : null;
        }

        private static bool TryGetEpisodeFileSeasonPrefix(string fileNameWithoutExtension, out string seasonPrefix)
        {
            seasonPrefix = string.Empty;

            if (string.IsNullOrWhiteSpace(fileNameWithoutExtension) ||
                fileNameWithoutExtension.Length < 4 ||
                char.ToUpperInvariant(fileNameWithoutExtension[0]) != 'S' ||
                !char.IsDigit(fileNameWithoutExtension[1]) ||
                !char.IsDigit(fileNameWithoutExtension[2]) ||
                char.ToUpperInvariant(fileNameWithoutExtension[3]) != 'E')
            {
                return false;
            }

            seasonPrefix = "S" + fileNameWithoutExtension.Substring(1, 2);
            return true;
        }

        private static string RewriteEpisodeFileSeasonPrefix(string fileNameWithoutExtension, int seasonNumber)
        {
            if (!TryGetEpisodeFileSeasonPrefix(fileNameWithoutExtension, out _))
            {
                return fileNameWithoutExtension;
            }

            var normalizedSeasonNumber = seasonNumber >= 0 ? seasonNumber : 1;
            return "S" + normalizedSeasonNumber.ToString("00") + fileNameWithoutExtension.Substring(3);
        }

        private static void TryDeleteEmptySeasonDirectory(ILogger logger, string directoryPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
                {
                    return;
                }

                if (Directory.EnumerateFileSystemEntries(directoryPath).Any())
                {
                    return;
                }

                Directory.Delete(directoryPath);
                logger.LogInformation("[YummyKodik] Removed empty legacy season folder '{Path}'.", directoryPath);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[YummyKodik] Failed to delete empty season folder '{Path}'.", directoryPath);
            }
        }

        private static void TryUpdateEpisodeNfoSeason(ILogger logger, string nfoPath, int seasonNumber)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nfoPath) || !File.Exists(nfoPath))
                {
                    return;
                }

                var xml = File.ReadAllText(nfoPath);
                if (!IsValidXmlContent(xml))
                {
                    return;
                }

                var updated = Regex.Replace(
                    xml,
                    @"<season>\s*\d+\s*</season>",
                    "<season>" + seasonNumber + "</season>",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                    matchTimeout: TimeSpan.FromSeconds(1));

                if (!string.Equals(xml, updated, StringComparison.Ordinal))
                {
                    File.WriteAllText(nfoPath, updated);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[YummyKodik] Failed to update episode season in NFO '{Path}'.", nfoPath);
            }
        }

        private static bool IsValidXmlContent(string? content)
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
    }
}
