// File: Util/NfoBuilder.cs

using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace YummyKodik.Util
{
    /// <summary>
    /// Simple helpers to build NFO XML for series and episodes.
    /// </summary>
    public static class NfoBuilder
    {
        public static string BuildSeriesNfo(string title, string plot)
        {
            var settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = false,
                Encoding = new UTF8Encoding(false),
                Indent = true
            };

            var sb = new StringBuilder();
            using (var writer = XmlWriter.Create(sb, settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("tvshow");

                writer.WriteElementString("title", title ?? string.Empty);
                writer.WriteElementString("plot", plot ?? string.Empty);

                writer.WriteEndElement(); // tvshow
                writer.WriteEndDocument();
            }

            return sb.ToString();
        }

        public static string BuildEpisodeNfo(
            int episodeNumber,
            int season,
            string seriesTitle,
            string description,
            int? durationSeconds = null)
        {
            var settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = false,
                Encoding = new UTF8Encoding(false),
                Indent = true
            };

            var sb = new StringBuilder();
            using (var writer = XmlWriter.Create(sb, settings))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("episodedetails");

                writer.WriteElementString("title", $"Episode {episodeNumber}");
                writer.WriteElementString("season", season.ToString());
                writer.WriteElementString("episode", episodeNumber.ToString());
                writer.WriteElementString("showtitle", seriesTitle ?? string.Empty);
                writer.WriteElementString("plot", description ?? string.Empty);
                WriteEpisodeRuntime(writer, durationSeconds);
                writer.WriteElementString("dateadded", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));

                writer.WriteEndElement(); // episodedetails
                writer.WriteEndDocument();
            }

            return sb.ToString();
        }

        public static string EnsureEpisodeRuntime(string xml, int durationSeconds)
        {
            if (string.IsNullOrWhiteSpace(xml) || durationSeconds <= 0)
            {
                return xml ?? string.Empty;
            }

            var document = XDocument.Parse(xml, LoadOptions.None);
            var root = document.Root;
            if (root == null || !string.Equals(root.Name.LocalName, "episodedetails", StringComparison.OrdinalIgnoreCase))
            {
                return xml;
            }

            var changed = false;
            var runtimeMinutes = GetRuntimeMinutes(durationSeconds).ToString(CultureInfo.InvariantCulture);
            var runtime = FindChild(root, "runtime");
            if (runtime == null)
            {
                root.Add(new XElement(root.Name.Namespace + "runtime", runtimeMinutes));
                changed = true;
            }
            else if (!string.Equals(runtime.Value.Trim(), runtimeMinutes, StringComparison.Ordinal))
            {
                runtime.Value = runtimeMinutes;
                changed = true;
            }

            var fileInfo = FindChild(root, "fileinfo");
            if (fileInfo == null)
            {
                fileInfo = new XElement(root.Name.Namespace + "fileinfo");
                root.Add(fileInfo);
                changed = true;
            }

            var streamDetails = FindChild(fileInfo, "streamdetails");
            if (streamDetails == null)
            {
                streamDetails = new XElement(root.Name.Namespace + "streamdetails");
                fileInfo.Add(streamDetails);
                changed = true;
            }

            var video = FindChild(streamDetails, "video");
            if (video == null)
            {
                video = new XElement(root.Name.Namespace + "video");
                streamDetails.Add(video);
                changed = true;
            }

            var durationInSeconds = FindChild(video, "durationinseconds");
            var durationValue = durationSeconds.ToString(CultureInfo.InvariantCulture);
            if (durationInSeconds == null)
            {
                video.Add(new XElement(root.Name.Namespace + "durationinseconds", durationValue));
                changed = true;
            }
            else if (!string.Equals(durationInSeconds.Value.Trim(), durationValue, StringComparison.Ordinal))
            {
                durationInSeconds.Value = durationValue;
                changed = true;
            }

            if (!changed)
            {
                return xml;
            }

            var settings = CreateXmlWriterSettings();
            var sb = new StringBuilder();
            using (var writer = XmlWriter.Create(sb, settings))
            {
                document.Save(writer);
            }

            return sb.ToString();
        }

        public static bool TryGetEpisodeRuntimeSeconds(string? xml, out int durationSeconds)
        {
            durationSeconds = 0;
            if (string.IsNullOrWhiteSpace(xml))
            {
                return false;
            }

            if (TryGetExactEpisodeRuntimeSeconds(xml, out durationSeconds))
            {
                return true;
            }

            try
            {
                var document = XDocument.Parse(xml, LoadOptions.None);
                var runtime = document.Root?
                    .Elements()
                    .FirstOrDefault(element =>
                        string.Equals(element.Name.LocalName, "runtime", StringComparison.OrdinalIgnoreCase));
                if (runtime != null &&
                    int.TryParse(
                        runtime.Value.Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var runtimeMinutes) &&
                    runtimeMinutes > 0)
                {
                    durationSeconds = checked(runtimeMinutes * 60);
                    return true;
                }
            }
            catch (Exception ex) when (ex is XmlException or FormatException or OverflowException)
            {
                durationSeconds = 0;
            }

            return false;
        }

        public static bool TryGetExactEpisodeRuntimeSeconds(string? xml, out int durationSeconds)
        {
            durationSeconds = 0;
            if (string.IsNullOrWhiteSpace(xml))
            {
                return false;
            }

            try
            {
                var document = XDocument.Parse(xml, LoadOptions.None);
                var exactDuration = document
                    .Descendants()
                    .FirstOrDefault(element =>
                        string.Equals(element.Name.LocalName, "durationinseconds", StringComparison.OrdinalIgnoreCase));
                return exactDuration != null &&
                       int.TryParse(
                           exactDuration.Value.Trim(),
                           NumberStyles.Integer,
                           CultureInfo.InvariantCulture,
                           out durationSeconds) &&
                       durationSeconds > 0;
            }
            catch (Exception ex) when (ex is XmlException or FormatException or OverflowException)
            {
                durationSeconds = 0;
                return false;
            }
        }

        private static void WriteEpisodeRuntime(XmlWriter writer, int? durationSeconds)
        {
            if (!durationSeconds.HasValue || durationSeconds.Value <= 0)
            {
                return;
            }

            writer.WriteElementString(
                "runtime",
                GetRuntimeMinutes(durationSeconds.Value).ToString(CultureInfo.InvariantCulture));
            writer.WriteStartElement("fileinfo");
            writer.WriteStartElement("streamdetails");
            writer.WriteStartElement("video");
            writer.WriteElementString(
                "durationinseconds",
                durationSeconds.Value.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        private static long GetRuntimeMinutes(int durationSeconds)
        {
            return Math.Max(
                1,
                (long)Math.Round(
                    TimeSpan.FromSeconds(durationSeconds).TotalMinutes,
                    MidpointRounding.AwayFromZero));
        }

        private static XElement? FindChild(XContainer parent, string localName)
        {
            return parent
                .Elements()
                .FirstOrDefault(element => string.Equals(element.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
        }

        private static XmlWriterSettings CreateXmlWriterSettings()
        {
            return new XmlWriterSettings
            {
                OmitXmlDeclaration = false,
                Encoding = new UTF8Encoding(false),
                Indent = true
            };
        }
    }
}
