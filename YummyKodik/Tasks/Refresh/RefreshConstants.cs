using YummyKodik.Yummy;

namespace YummyKodik.Tasks.Refresh;

internal static class RefreshConstants
{
    public static readonly YummyVideoProviderKind[] PreferredYummyProviderOrder =
    {
        YummyVideoProviderKind.Alloha,
        YummyVideoProviderKind.Cvh
    };

    public const string HlsFormatQuerySuffix = "&format=hls";
    public const string StrmExtension = ".strm";
    public const string NfoExtension = ".nfo";
}
