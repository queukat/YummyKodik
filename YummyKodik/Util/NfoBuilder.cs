// File: Util/NfoBuilder.cs

using System.Text;
using System.Xml;

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
            using var writer = XmlWriter.Create(sb, settings);

            writer.WriteStartDocument();
            writer.WriteStartElement("tvshow");

            writer.WriteElementString("title", title ?? string.Empty);
            writer.WriteElementString("plot", plot ?? string.Empty);

            writer.WriteEndElement(); // tvshow
            writer.WriteEndDocument();

            return sb.ToString();
        }

        public static string BuildEpisodeNfo(int episodeNumber, int season, string seriesTitle, string description)
        {
            var settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = false,
                Encoding = new UTF8Encoding(false),
                Indent = true
            };

            var sb = new StringBuilder();
            using var writer = XmlWriter.Create(sb, settings);

            writer.WriteStartDocument();
            writer.WriteStartElement("episodedetails");

            writer.WriteElementString("title", $"Episode {episodeNumber}");
            writer.WriteElementString("season", season.ToString());
            writer.WriteElementString("episode", episodeNumber.ToString());
            writer.WriteElementString("showtitle", seriesTitle ?? string.Empty);
            writer.WriteElementString("plot", description ?? string.Empty);
            writer.WriteElementString("dateadded", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));

            writer.WriteEndElement(); // episodedetails
            writer.WriteEndDocument();

            return sb.ToString();
        }
    }
}
