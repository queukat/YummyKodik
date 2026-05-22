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
                    .Select(x => x.Path)
                    .ToList();

                foreach (var mistakenDir in mistakenDirs)
                {
                    var movedCount = seasonNumber == 0
                        ? MoveAllSeasonArtifacts(logger, mistakenDir, seasonDir, seasonNumber)
                        : MoveSeasonArtifactsForSeason(logger, mistakenDir, seasonDir, seasonNumber);
                    if (movedCount <= 0)
                    {
                        continue;
                    }

                    logger.LogInformation(
                        "[YummyKodik] Reconciled {Count} season {Season} artifact(s) from '{Old}' into '{New}'.",
                        movedCount,
                        seasonNumber,
                        mistakenDir,
                        seasonDir);

                    TryDeleteEmptySeasonDirectory(logger, mistakenDir);
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
                foreach (var path in EnumerateSeasonArtifactFiles(seasonDir))
                {
                    MigrateLegacyEpisodeFileName(logger, seasonDir, path, normalizedSeasonNumber, newSeasonPrefix);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[YummyKodik] Season file migration failed. seasonDir='{SeasonDir}' season={Season}", seasonDir, seasonNumber);
            }
        }

        private static int MoveSeasonArtifactsForSeason(ILogger logger, string sourceDir, string targetDir, int seasonNumber)
        {
            return MoveSeasonArtifacts(
                logger,
                sourceDir,
                targetDir,
                seasonNumber,
                group => GroupSeasonMatches(group, seasonNumber),
                LogMoveSeasonArtifactFailure);
        }

        private static int MoveAllSeasonArtifacts(ILogger logger, string sourceDir, string targetDir, int seasonNumber)
        {
            return MoveSeasonArtifacts(
                logger,
                sourceDir,
                targetDir,
                seasonNumber,
                _ => true,
                LogMoveSpecialArtifactFailure);
        }

        private static void MigrateLegacyEpisodeFileName(
            ILogger logger,
            string seasonDir,
            string path,
            int normalizedSeasonNumber,
            string newSeasonPrefix)
        {
            var fileName = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            var nameNoExt = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            if (!TryGetEpisodeFileSeasonPrefix(nameNoExt, out var currentSeasonPrefix))
            {
                return;
            }

            var ext = Path.GetExtension(path);
            if (string.Equals(currentSeasonPrefix, newSeasonPrefix, StringComparison.OrdinalIgnoreCase))
            {
                UpdateEpisodeNfoSeasonIfNeeded(logger, path, ext, normalizedSeasonNumber);
                return;
            }

            var renamedNoExt = string.Concat(newSeasonPrefix, nameNoExt.AsSpan(3));
            var target = Path.Combine(seasonDir, renamedNoExt + ext);
            if (TryRenameLegacyEpisodeFile(logger, path, target))
            {
                UpdateEpisodeNfoSeasonIfNeeded(logger, target, ext, normalizedSeasonNumber);
            }
        }

        private static bool TryRenameLegacyEpisodeFile(ILogger logger, string path, string target)
        {
            if (File.Exists(target))
            {
                TryDeleteLegacyEpisodeFile(logger, path);
                return false;
            }

            try
            {
                File.Move(path, target);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[YummyKodik] Failed to rename file '{Old}' -> '{New}'.", path, target);
                return false;
            }
        }

        private static void TryDeleteLegacyEpisodeFile(ILogger logger, string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[YummyKodik] Failed to delete legacy file '{Path}'.", path);
            }
        }

        private static int MoveSeasonArtifacts(
            ILogger logger,
            string sourceDir,
            string targetDir,
            int seasonNumber,
            Func<IGrouping<string, string>, bool> shouldMoveGroup,
            Action<ILogger, Exception, string, string> logMoveFailure)
        {
            if (!CanMoveSeasonArtifacts(sourceDir, targetDir))
            {
                return 0;
            }

            Directory.CreateDirectory(targetDir);

            var movedCount = 0;

            foreach (var group in EnumerateSeasonArtifactGroups(sourceDir).Where(shouldMoveGroup))
            {
                movedCount += MoveSeasonArtifactGroup(logger, group, targetDir, seasonNumber, logMoveFailure);
            }

            return movedCount;
        }

        private static bool CanMoveSeasonArtifacts(string sourceDir, string targetDir)
        {
            return !string.IsNullOrWhiteSpace(sourceDir) &&
                   !string.IsNullOrWhiteSpace(targetDir) &&
                   Directory.Exists(sourceDir) &&
                   !string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<IGrouping<string, string>> EnumerateSeasonArtifactGroups(string sourceDir)
        {
            return EnumerateSeasonArtifactFiles(sourceDir)
                .GroupBy(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Where(g => !string.IsNullOrWhiteSpace(g.Key));
        }

        private static List<string> EnumerateSeasonArtifactFiles(string directory)
        {
            return Directory.EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                .Where(IsSeasonArtifactFile)
                .ToList();
        }

        private static bool IsSeasonArtifactFile(string path)
        {
            return IsSeasonArtifactExtension(Path.GetExtension(path));
        }

        private static bool IsSeasonArtifactExtension(string extension)
        {
            return extension.Equals(".strm", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".nfo", StringComparison.OrdinalIgnoreCase);
        }

        private static bool GroupSeasonMatches(IGrouping<string, string> group, int seasonNumber)
        {
            return DetectSeasonNumberFromArtifacts(group) == seasonNumber;
        }

        private static int MoveSeasonArtifactGroup(
            ILogger logger,
            IEnumerable<string> paths,
            string targetDir,
            int seasonNumber,
            Action<ILogger, Exception, string, string> logMoveFailure)
        {
            var movedCount = 0;
            foreach (var path in paths.Where(path => TryMoveSeasonArtifact(logger, path, targetDir, seasonNumber, logMoveFailure)))
            {
                movedCount++;
            }

            return movedCount;
        }

        private static bool TryMoveSeasonArtifact(
            ILogger logger,
            string path,
            string targetDir,
            int seasonNumber,
            Action<ILogger, Exception, string, string> logMoveFailure)
        {
            var ext = Path.GetExtension(path);
            var nameNoExt = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            var targetNameNoExt = RewriteEpisodeFileSeasonPrefix(nameNoExt, seasonNumber);
            var targetPath = Path.Combine(targetDir, targetNameNoExt + ext);

            try
            {
                MoveSeasonArtifactFile(path, targetPath);
                UpdateEpisodeNfoSeasonIfNeeded(logger, targetPath, ext, seasonNumber);
                return true;
            }
            catch (Exception ex)
            {
                logMoveFailure(logger, ex, path, targetPath);
                return false;
            }
        }

        private static void LogMoveSeasonArtifactFailure(ILogger logger, Exception exception, string path, string targetPath)
        {
            logger.LogDebug(exception, "[YummyKodik] Failed to move season artifact '{Path}' -> '{TargetPath}'.", path, targetPath);
        }

        private static void LogMoveSpecialArtifactFailure(ILogger logger, Exception exception, string path, string targetPath)
        {
            logger.LogDebug(exception, "[YummyKodik] Failed to move special artifact '{Path}' -> '{TargetPath}'.", path, targetPath);
        }

        private static void MoveSeasonArtifactFile(string path, string targetPath)
        {
            if (string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (File.Exists(targetPath))
            {
                File.Delete(path);
                return;
            }

            File.Move(path, targetPath);
        }

        private static void UpdateEpisodeNfoSeasonIfNeeded(ILogger logger, string path, string extension, int seasonNumber)
        {
            if (extension.Equals(".nfo", StringComparison.OrdinalIgnoreCase))
            {
                TryUpdateEpisodeNfoSeason(logger, path, seasonNumber);
            }
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

            seasonPrefix = string.Concat("S", fileNameWithoutExtension.AsSpan(1, 2));
            return true;
        }

        private static string RewriteEpisodeFileSeasonPrefix(string fileNameWithoutExtension, int seasonNumber)
        {
            if (!TryGetEpisodeFileSeasonPrefix(fileNameWithoutExtension, out _))
            {
                return fileNameWithoutExtension;
            }

            var normalizedSeasonNumber = seasonNumber >= 0 ? seasonNumber : 1;
            return string.Concat("S", normalizedSeasonNumber.ToString("00"), fileNameWithoutExtension.AsSpan(3));
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
