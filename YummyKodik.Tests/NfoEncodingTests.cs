using System.Text;
using System.Xml.Linq;
using YummyKodik.Util;

internal static class NfoEncodingTests
{
    public static void SeriesNfoParsesFromUtf8Bytes()
    {
        var document = ParsePersistedUtf8(NfoBuilder.BuildSeriesNfo("Сериал & 日本語", "Описание <сюжета>"));
        Check(document.Root?.Name == "tvshow", "Series NFO must retain its root.");
        Check(document.Root?.Element("title")?.Value == "Сериал & 日本語", "Series title must round-trip through UTF-8 bytes.");
        Check(document.Root?.Element("plot")?.Value == "Описание <сюжета>", "Series plot must retain escaped text.");
    }

    public static void EpisodeNfoParsesFromUtf8Bytes()
    {
        var document = ParsePersistedUtf8(NfoBuilder.BuildEpisodeNfo(22, 3, "Стать королём", "Описание", 1425));
        var root = document.Root;
        Check(root?.Name == "episodedetails", "Episode NFO must retain its root.");
        Check(root?.Element("season")?.Value == "3" && root.Element("episode")?.Value == "22", "Episode identity must survive byte parsing.");
        Check(root?.Element("showtitle")?.Value == "Стать королём", "Unicode metadata must survive byte parsing.");
        Check(root?.Descendants("durationinseconds").Single().Value == "1425", "Exact runtime must survive byte parsing.");
    }

    public static void RuntimeEnrichmentParsesFromUtf8BytesAndPreservesMetadata()
    {
        const string existing = "<?xml version=\"1.0\" encoding=\"utf-16\"?><episodedetails><title>Серия</title><season>2</season><episode>4</episode><plot>日本語</plot><uniqueid type=\"tvdb\">123</uniqueid></episodedetails>";
        var enriched = NfoBuilder.EnsureEpisodeRuntime(existing, 1425);
        var root = ParsePersistedUtf8(enriched).Root!;
        Check(root.Element("title")?.Value == "Серия" && root.Element("plot")?.Value == "日本語", "Enrichment must preserve existing Unicode metadata.");
        Check(root.Element("season")?.Value == "2" && root.Element("episode")?.Value == "4", "Enrichment must preserve episode identity.");
        Check(root.Element("uniqueid")?.Value == "123" && root.Element("uniqueid")?.Attribute("type")?.Value == "tvdb", "Enrichment must preserve provider identity.");
        Check(root.Descendants("durationinseconds").Single().Value == "1425", "Enrichment must write exact runtime.");
        Check(NfoBuilder.EnsureEpisodeRuntime(enriched, 1425) == enriched, "An unchanged runtime must retain the exact text and avoid a filesystem rewrite.");
    }

    private static XDocument ParsePersistedUtf8(string xml)
    {
        // Jellyfin reads bytes, whereas parsing a .NET string ignores the XML encoding declaration.
        using var bytes = new MemoryStream(new UTF8Encoding(false).GetBytes(xml));
        var document = XDocument.Load(bytes);
        Check(string.Equals(document.Declaration?.Encoding, "utf-8", StringComparison.OrdinalIgnoreCase), "The XML declaration must describe the bytes written by RefreshFileWriter.");
        return document;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
