using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik;
using YummyKodik.Cvh;
using YummyKodik.Alloha;
using YummyKodik.Configuration;
using YummyKodik.Kodik;
using YummyKodik.Logging;
using YummyKodik.Media;
using YummyKodik.Shikimori;
using YummyKodik.Tasks;
using YummyKodik.Tasks.Refresh;
using YummyKodik.Util;
using YummyKodik.Web;
using YummyKodik.Yummy;
if (args.Length > 0 &&
    string.Equals(args[0], "--live-alloha-probe", StringComparison.OrdinalIgnoreCase))
{
    await RunLiveAllohaProbe();
    return;
}

if (args.Length >= 7 &&
    string.Equals(args[0], "--live-alloha-source-probe", StringComparison.OrdinalIgnoreCase))
{
    var source = new YummyAllohaSource
    {
        MovieToken = args[1],
        RequestToken = args[2],
        TranslationId = int.Parse(args[3]),
        SeasonNumber = int.Parse(args[4]),
        EpisodeNumber = int.Parse(args[5]),
        RefererUrl = args[6],
        Hidden = args.Length >= 8 ? args[7] : string.Empty
    };

    var preferredVoiceName = args.Length >= 9 ? args[8] : null;
    await RunLiveAllohaProbeForSource(source, preferredVoiceName);
    return;
}

var tests = new (string Name, Action Run)[]
{
    ("KodikClient_GetAnimeInfoAsync_TracksExplicitEpisodeCoverageFromSearch", KodikClient_GetAnimeInfoAsync_TracksExplicitEpisodeCoverageFromSearch),
    ("KodikClient_GetEpisodeTimingsAsync_UsesEpisodeLevelSearchLink", KodikClient_GetEpisodeTimingsAsync_UsesEpisodeLevelSearchLink),
    ("KodikClient_GetEpisodeLinkAsync_FallsBackToSeasonLinkWhenEpisodeLinkIsBroken", KodikClient_GetEpisodeLinkAsync_FallsBackToSeasonLinkWhenEpisodeLinkIsBroken),
    ("KodikClient_RunSearchCacheSharesInflightAndEvictsFaults", KodikClient_RunSearchCacheSharesInflightAndEvictsFaults),
    ("KodikClient_RunScriptCacheSharesSuccessAndEvictsAfterPostFailure", KodikClient_RunScriptCacheSharesSuccessAndEvictsAfterPostFailure),
    ("KodikSupplement_RuntimeProbeUsesDeepValidatedLink", KodikSupplement_RuntimeProbeUsesDeepValidatedLink),
    ("KodikPlaybackService_ProxiesHlsResourcesAndCachesSegments", KodikPlaybackService_ProxiesHlsResourcesAndCachesSegments),
    ("KodikPlaybackService_RetriesTransientSegmentFailures", KodikPlaybackService_RetriesTransientSegmentFailures),
    ("KodikPlaybackService_UsesShortBackoffForTransportFailures", KodikPlaybackService_UsesShortBackoffForTransportFailures),
    ("KodikPlaybackService_RetriesTransientNotFoundSegments", KodikPlaybackService_RetriesTransientNotFoundSegments),
    ("KodikPlaybackService_BoundsPersistentTransientFailures", KodikPlaybackService_BoundsPersistentTransientFailures),
    ("KodikStreaming_FirstChunkBeforeEofAndCompletedCache", KodikStreamingTests.FirstChunkBeforeEofAndCompletedCache),
    ("KodikStreaming_PartialReadFailureDoesNotRetryOrCache", KodikStreamingTests.PartialReadFailureDoesNotRetryOrCache),
    ("KodikStreaming_DownstreamWriteFailureDoesNotRetryOrCache", KodikStreamingTests.DownstreamWriteFailureDoesNotRetryOrCache),
    ("KodikStreaming_CancellationDoesNotRetryOrCache", KodikStreamingTests.CancellationDoesNotRetryOrCache),
    ("KodikStreaming_SniffedManifestIsFullyRewritten", KodikStreamingTests.SniffedManifestIsFullyRewritten),
    ("KodikStreaming_OversizedResourceStreamsWithoutCaching", KodikStreamingTests.OversizedResourceStreamsWithoutCaching),
    ("KodikStreaming_TruncatedContentLengthDoesNotCache", KodikStreamingTests.TruncatedContentLengthDoesNotCache),
    ("KodikPrefetch_HasTwoBackgroundWorkers", KodikPrefetchTests.PrefetchHasTwoBackgroundWorkers),
    ("KodikPrefetch_ForegroundJoinsWithoutDuplicateDownload", KodikPrefetchTests.ForegroundJoinsPrefetchWithoutDuplicateDownload),
    ("KodikPrefetch_CanceledConsumerPreservesSharedDownload", KodikPrefetchTests.CanceledConsumerPreservesSharedDownload),
    ("KodikPrefetch_SeekThenConsumerCancellationStopsObsoleteDownload", KodikPrefetchTests.SeekThenConsumerCancellationStopsObsoleteDownload),
    ("KodikPrefetch_IneligibleRegisteredResourcesStreamDirectly", KodikPrefetchTests.IneligibleRegisteredResourcesStreamDirectly),
    ("KodikClient_GetAnimeInfoAsync_FallsBackToHtmlFindPlayerResponse", KodikClient_GetAnimeInfoAsync_FallsBackToHtmlFindPlayerResponse),
    ("KodikTokenResolver_DecodesOnlineModPayload", KodikTokenResolver_DecodesOnlineModPayload),
    ("KodikTitleResolver_NormalizesUnicodeWords", KodikTitleResolver_NormalizesUnicodeWords),
    ("NeedsKodikSupplement_SkipsAnnouncementWithoutAvailableEpisodes", NeedsKodikSupplement_SkipsAnnouncementWithoutAvailableEpisodes),
    ("NeedsKodikSupplement_UsesKodikWhenAiredEpisodesExist", NeedsKodikSupplement_UsesKodikWhenAiredEpisodesExist),
    ("LimitToExpectedAvailableEpisodes_CapsProviderCoverageToAiredCount", LimitToExpectedAvailableEpisodes_CapsProviderCoverageToAiredCount),
    ("GetExpectedAvailableEpisodeCount_UsesKnownVideoEpisodesWhenAiredLags", GetExpectedAvailableEpisodeCount_UsesKnownVideoEpisodesWhenAiredLags),
    ("LimitToExpectedAvailableEpisodes_PreservesExplicitProviderCoverageWhenAiredLags", LimitToExpectedAvailableEpisodes_PreservesExplicitProviderCoverageWhenAiredLags),
    ("LimitToExpectedAvailableEpisodes_KeepsKnownEpisodesWhenAiredCountIsUnknown", LimitToExpectedAvailableEpisodes_KeepsKnownEpisodesWhenAiredCountIsUnknown),
    ("ResolveSeasonNumber_UsesViewingOrderIndex", ResolveSeasonNumber_UsesViewingOrderIndex),
    ("ResolveSeasonNumber_IgnoresCalendarSeasonField", ResolveSeasonNumber_IgnoresCalendarSeasonField),
    ("ResolveSeasonNumber_ParsesSeasonWithPartSuffix", ResolveSeasonNumber_ParsesSeasonWithPartSuffix),
    ("ResolveSeasonNumber_PrefersExplicitSeasonSuffixOverViewingOrderIndex", ResolveSeasonNumber_PrefersExplicitSeasonSuffixOverViewingOrderIndex),
    ("ResolveSeasonNumber_UsesShikimoriLayoutFallback", ResolveSeasonNumber_UsesShikimoriLayoutFallback),
    ("ResolveSeasonNumber_DoesNotTreatSpecialAsSecondSeason", ResolveSeasonNumber_DoesNotTreatSpecialAsSecondSeason),
    ("ResolveSeriesTitle_UsesViewingOrderBaseTitle", ResolveSeriesTitle_UsesViewingOrderBaseTitle),
    ("ResolveSeriesTitle_UsesViewingOrderBaseTitleForSpecial", ResolveSeriesTitle_UsesViewingOrderBaseTitleForSpecial),
    ("ResolveSeriesTitle_StripsTrailingSeasonSuffix", ResolveSeriesTitle_StripsTrailingSeasonSuffix),
    ("ResolveSeriesTitle_StripsSeasonWithPartSuffix", ResolveSeriesTitle_StripsSeasonWithPartSuffix),
    ("ResolveSeriesTitle_PrefersStrippedExplicitSeasonSuffixOverViewingOrderBaseTitle", ResolveSeriesTitle_PrefersStrippedExplicitSeasonSuffixOverViewingOrderBaseTitle),
    ("ResolveSeriesTitle_UsesShikimoriLayoutFallback", ResolveSeriesTitle_UsesShikimoriLayoutFallback),
    ("ShouldCreateSeasonDirectory_ForAnnouncedSecondSeason", ShouldCreateSeasonDirectory_ForAnnouncedSecondSeason),
    ("ShikimoriSeriesLayoutResolver_TreatsPartAsSameSeason", ShikimoriSeriesLayoutResolver_TreatsPartAsSameSeason),
    ("ShikimoriSeriesLayoutResolver_DoesNotTreatSpecialAsNewSeason", ShikimoriSeriesLayoutResolver_DoesNotTreatSpecialAsNewSeason),
    ("NfoBuilder_BuildSeriesNfo_ProducesXml", NfoBuilder_BuildSeriesNfo_ProducesXml),
    ("NfoBuilder_BuildEpisodeNfo_ProducesXml", NfoBuilder_BuildEpisodeNfo_ProducesXml),
    ("NfoBuilder_BuildEpisodeNfo_WritesExactRuntime", NfoBuilder_BuildEpisodeNfo_WritesExactRuntime),
    ("NfoBuilder_EnsureEpisodeRuntime_BackfillsLegacyNfo", NfoBuilder_EnsureEpisodeRuntime_BackfillsLegacyNfo),
    ("EpisodeRuntimeBackfillService_BorrowsSiblingRuntime", EpisodeRuntimeBackfillService_BorrowsSiblingRuntime),
    ("RefreshState_RuntimeBackfillReconcilesManagedNfoHash", RefreshState_RuntimeBackfillReconcilesManagedNfoHash),
    ("RewriteEpisodeFileSeasonPrefix_UsesSeasonZeroForSpecials", RewriteEpisodeFileSeasonPrefix_UsesSeasonZeroForSpecials),
    ("BuildSeriesFolderName_UsesProviderCompatibleShikimoriTag", BuildSeriesFolderName_UsesProviderCompatibleShikimoriTag),
    ("PrepareSeasonDirectory_MovesSeasonOneArtifactsOutOfMistakenSeasonTwoFolder", PrepareSeasonDirectory_MovesSeasonOneArtifactsOutOfMistakenSeasonTwoFolder),
    ("PrepareSeasonDirectory_MovesSeasonTwoArtifactsOutOfLegacySeasonOneFolder", PrepareSeasonDirectory_MovesSeasonTwoArtifactsOutOfLegacySeasonOneFolder),
    ("PrepareSeasonDirectory_DoesNotMoveActualSeasonOneArtifactsWhenPreparingSeasonTwo", PrepareSeasonDirectory_DoesNotMoveActualSeasonOneArtifactsWhenPreparingSeasonTwo),
    ("PrepareSeasonDirectory_MovesSpecialArtifactsIntoSeasonZeroFolder", PrepareSeasonDirectory_MovesSpecialArtifactsIntoSeasonZeroFolder),
    ("YummyVideoCatalog_ParsesCvhProviders", YummyVideoCatalog_ParsesCvhProviders),
    ("YummyVideoCatalog_DecodesCvhDubbingCodePlusAsSpace", YummyVideoCatalog_DecodesCvhDubbingCodePlusAsSpace),
    ("YummyClient_GetUserListAsync_AcceptsValidEmptyResponse", YummyClient_GetUserListAsync_AcceptsValidEmptyResponse),
    ("YummyClient_GetUserListAsync_RejectsMissingResponseArray", YummyClient_GetUserListAsync_RejectsMissingResponseArray),
    ("CvhClient_AddsBrowserHeadersAndParsesResponses", CvhClient_AddsBrowserHeadersAndParsesResponses),
    ("CvhClient_PrefersDubbingNameOverNumericDubbingCode", CvhClient_PrefersDubbingNameOverNumericDubbingCode),
    ("CvhClient_MatchesEquivalentVoiceKeys", CvhClient_MatchesEquivalentVoiceKeys),
    ("CvhClient_DoesNotFallbackToDifferentVoiceWhenPreferredVoiceIsMissing", CvhClient_DoesNotFallbackToDifferentVoiceWhenPreferredVoiceIsMissing),
    ("CvhClient_RejectsInvalidSourceIdentifiers", CvhClient_RejectsInvalidSourceIdentifiers),
    ("CvhClient_ThrowsMeaningfulErrorOnEmptyPlaylist", CvhClient_ThrowsMeaningfulErrorOnEmptyPlaylist),
    ("CvhClient_DownloadManifestAddsHeadersAndRewritesUrls", CvhClient_DownloadManifestAddsHeadersAndRewritesUrls),
    ("CvhClient_BuildManifestResponseBody_ProxiesNestedPlaylists", CvhClient_BuildManifestResponseBody_ProxiesNestedPlaylists),
    ("CvhStreaming_FirstChunkArrivesBeforeEof", CvhStreamingTests.FirstChunkArrivesBeforeEof),
    ("CvhStreaming_CookiesAndResourceHeadersArePreserved", CvhStreamingTests.CookiesAndResourceHeadersArePreserved),
    ("CvhStreaming_SniffedManifestIsRewrittenBeforeWriting", CvhStreamingTests.SniffedManifestIsRewrittenBeforeWriting),
    ("CvhStreaming_PartialFailureAndCancellationStopWrites", CvhStreamingTests.PartialFailureAndCancellationStopWrites),
    ("YummyVideoCatalog_ParsesAllohaProviders", YummyVideoCatalog_ParsesAllohaProviders),
    ("AllohaApiClient_ParsesSerialCatalogEntries", AllohaApiClient_ParsesSerialCatalogEntries),
    ("AllohaApiClient_ParsesEpisodesArrayCatalogEntries", AllohaApiClient_ParsesEpisodesArrayCatalogEntries),
    ("AllohaApiCatalogLoader_FilterEntriesForSeason_KeepsOnlyRequestedSeason", AllohaApiCatalogLoader_FilterEntriesForSeason_KeepsOnlyRequestedSeason),
    ("AllohaApiCatalogLoader_LoadEntriesAsync_CachesSuccessfulCatalogLoads", AllohaApiCatalogLoader_LoadEntriesAsync_CachesSuccessfulCatalogLoads),
    ("AllohaApiCatalogLoader_LoadEntriesAsync_DeduplicatesConcurrentCatalogLoads", AllohaApiCatalogLoader_LoadEntriesAsync_DeduplicatesConcurrentCatalogLoads),
    ("AllohaApiCatalogLoader_LoadEntriesAsync_CachesFailuresBriefly", AllohaApiCatalogLoader_LoadEntriesAsync_CachesFailuresBriefly),
    ("YummyVideoCatalog_MergesAdditionalAllohaEntriesWithoutOverwritingYummyData", YummyVideoCatalog_MergesAdditionalAllohaEntriesWithoutOverwritingYummyData),
    ("YummyVideoCatalog_KeepsAllohaEntriesFromDifferentSeasonsDistinct", YummyVideoCatalog_KeepsAllohaEntriesFromDifferentSeasonsDistinct),
    ("YummyVideoCatalog_CombinesCoverageAcrossProviders", YummyVideoCatalog_CombinesCoverageAcrossProviders),
    ("TranslationNameKeyNormalizer_UsesCuratedVoiceAliasGroups", TranslationNameKeyNormalizer_UsesCuratedVoiceAliasGroups),
    ("YummyVideoCatalog_MatchesEquivalentVoiceNames", YummyVideoCatalog_MatchesEquivalentVoiceNames),
    ("YummyVideoCatalog_MatchesCrossProviderVoiceAliases", YummyVideoCatalog_MatchesCrossProviderVoiceAliases),
    ("YummyVideoCatalog_FindPreferredEntryWithSkipsAcrossProviders_FallsBackToOtherProvider", YummyVideoCatalog_FindPreferredEntryWithSkipsAcrossProviders_FallsBackToOtherProvider),
    ("YummyVideoCatalog_FindPreferredEntryWithSkipsAcrossProviders_PrefersRequestedProvider", YummyVideoCatalog_FindPreferredEntryWithSkipsAcrossProviders_PrefersRequestedProvider),
    ("YummyKodikMediaSourceProvider_RuntimePublicationPolicyRepairsMissingOrShortValues", YummyKodikMediaSourceProvider_RuntimePublicationPolicyRepairsMissingOrShortValues),
    ("YummyKodikMediaSourceProvider_AuthoritativeRuntimeCorrectsPlausibleMismatch", YummyKodikMediaSourceProvider_AuthoritativeRuntimeCorrectsPlausibleMismatch),
    ("YummyKodikMediaSourceProvider_ResolvesPrimaryOnlyForExplicitSeriesSelection", YummyKodikMediaSourceProvider_ResolvesPrimaryOnlyForExplicitSeriesSelection),
    ("YummyKodikMediaSourceProvider_FillsMissingSourceRuntimeFromSiblingOrItem", YummyKodikMediaSourceProvider_FillsMissingSourceRuntimeFromSiblingOrItem),
    ("YummyKodikMediaSourceProvider_SharesRuntimeOnlyWithinEpisodeVersionSet", YummyKodikMediaSourceProvider_SharesRuntimeOnlyWithinEpisodeVersionSet),
    ("YummyKodikMediaSourceFactory_CarriesRuntimeWithoutReflection", YummyKodikMediaSourceFactory_CarriesRuntimeWithoutReflection),
    ("YummyKodikMediaSegmentProvider_ClonesCachedSegmentsPerItem", YummyKodikMediaSegmentProvider_ClonesCachedSegmentsPerItem),
    ("ResolveProviderCoverage_UsesYummyHintWhenKodikSeriesCountIsZero", ResolveProviderCoverage_UsesYummyHintWhenKodikSeriesCountIsZero),
    ("ResolveProviderCoverage_PreservesYummyCoverageBeyondKodik", ResolveProviderCoverage_PreservesYummyCoverageBeyondKodik),
    ("GenerateKodikEpisodeFilesAsync_FillsMissingTranslationsForExistingEpisode", GenerateKodikEpisodeFilesAsync_FillsMissingTranslationsForExistingEpisode),
    ("ResolveEpisodesNeedingKodikTranslation_SkipsYummyCoveredPairs", ResolveEpisodesNeedingKodikTranslation_SkipsYummyCoveredPairs),
    ("EpisodeArtifactMaintenance_NormalizesEquivalentTranslationVariants", EpisodeArtifactMaintenance_NormalizesEquivalentTranslationVariants),
    ("ResolveEpisodeTranslationFileBaseName_ReusesExistingEquivalentArtifactName", ResolveEpisodeTranslationFileBaseName_ReusesExistingEquivalentArtifactName),
    ("KeepLatestEpisodePerResolvedLink_DropsEarlierEpisodesWhenKodikReusesSameVideo", KeepLatestEpisodePerResolvedLink_DropsEarlierEpisodesWhenKodikReusesSameVideo),
    ("CleanupUnexpectedEpisodeArtifacts_RemovesStaleFilesBeyondExpectedCoverage", CleanupUnexpectedEpisodeArtifacts_RemovesStaleFilesBeyondExpectedCoverage),
    ("RefreshFileWriter_ReplacesReadOnlyExistingArtifact", RefreshFileWriter_ReplacesReadOnlyExistingArtifact),
    ("RefreshState_AllowsSingleFileSkipWhenFingerprintAndFilesMatch", RefreshState_AllowsSingleFileSkipWhenFingerprintAndFilesMatch),
    ("RefreshState_InvalidatesSkipWhenInputsChange", RefreshState_InvalidatesSkipWhenInputsChange),
    ("RefreshState_LegacyGenerationContractDisablesSkip", RefreshState_LegacyGenerationContractDisablesSkip),
    ("RefreshState_ExtraEpisodeArtifactsDisableSkip", RefreshState_ExtraEpisodeArtifactsDisableSkip),
    ("RefreshState_ZeroExpectedEpisodesWithStaleArtifactsDisablesSkip", RefreshState_ZeroExpectedEpisodesWithStaleArtifactsDisablesSkip),
    ("RefreshState_PreservesMultipleSeasonsInSeriesRoot", RefreshState_PreservesMultipleSeasonsInSeriesRoot),
    ("RefreshState_WritesAndReadsMediaSegmentsForEpisodeFile", RefreshState_WritesAndReadsMediaSegmentsForEpisodeFile),
    ("RefreshState_KodikCatalogSignatureIsOrderIndependent", RefreshState_KodikCatalogSignatureIsOrderIndependent),
    ("RefreshState_PerVoiceDeepSkipRequiresFreshMatchingCatalog", RefreshState_PerVoiceDeepSkipRequiresFreshMatchingCatalog),
    ("RefreshState_NewYummyKodikVoiceInvalidatesPreLookupSkip", RefreshState_NewYummyKodikVoiceInvalidatesPreLookupSkip),
    ("RefreshState_PerVoiceDeepSkipRejectsDamagedOrUnexpectedFiles", RefreshState_PerVoiceDeepSkipRejectsDamagedOrUnexpectedFiles),
    ("RefreshState_SkipDecisionReportsReasonsAndFileCounts", RefreshState_SkipDecisionReportsReasonsAndFileCounts),
    ("ExistingLibraryFallbackRefreshInfoLoader_LoadsSnapshotFromRefreshState", ExistingLibraryFallbackRefreshInfoLoader_LoadsSnapshotFromRefreshState),
    ("StaleReleaseCleanup_DeletesStaleManagedDirectory", StaleReleaseCleanup_DeletesStaleManagedDirectory),
    ("StaleReleaseCleanup_RetainsCurrentAndManualKeys", StaleReleaseCleanup_RetainsCurrentAndManualKeys),
    ("StaleReleaseCleanup_SkipsMissingCorruptAndAmbiguousState", StaleReleaseCleanup_SkipsMissingCorruptAndAmbiguousState),
    ("StaleReleaseCleanup_SkipsSeasonWithUnknownFile", StaleReleaseCleanup_SkipsSeasonWithUnknownFile),
    ("StaleReleaseCleanup_SkipsSeasonWithModifiedManagedFile", StaleReleaseCleanup_SkipsSeasonWithModifiedManagedFile),
    ("StaleReleaseCleanup_CanonicalizesPlainAndUrlCurrentKeys", StaleReleaseCleanup_CanonicalizesPlainAndUrlCurrentKeys),
    ("StaleReleaseCleanup_SkipsLegacyGenerationContract", StaleReleaseCleanup_SkipsLegacyGenerationContract),
    ("StaleReleaseCleanup_DeletesMixedRootStaleSeasonAndStateEntry", StaleReleaseCleanup_DeletesMixedRootStaleSeasonAndStateEntry),
    ("StaleReleaseCleanup_DeletesAllStaleRootForEmptyCurrentSet", StaleReleaseCleanup_DeletesAllStaleRootForEmptyCurrentSet),
    ("RefreshTitleKeySource_ValidEmptyUserListMarksFetchSucceeded", RefreshTitleKeySource_ValidEmptyUserListMarksFetchSucceeded),
    ("RefreshTask_ProcessesAtMostTwoTitlesConcurrently", RefreshTask_ProcessesAtMostTwoTitlesConcurrently),
    ("RefreshTask_RunGateSkipsConcurrentRun", RefreshTask_RunGateSkipsConcurrentRun),
    ("RefreshTask_LazyKodikInitializationRunsOnlyWhenValueIsUsed", RefreshTask_LazyKodikInitializationRunsOnlyWhenValueIsUsed),
    ("ShikimoriGraphQlClient_DeduplicatesConcurrentSameIdRequests", ShikimoriGraphQlClient_DeduplicatesConcurrentSameIdRequests),
    ("RefreshState_PerVoiceModeDoesNotPreSkip", RefreshState_PerVoiceModeDoesNotPreSkip),
    ("KodikClient_PreferredQualityFallsBackToAvailableMaximum", KodikClient_PreferredQualityFallsBackToAvailableMaximum),
    ("AllohaPlaybackService_BuildsExpectedBorthSuffix", AllohaPlaybackService_BuildsExpectedBorthSuffix),
    ("AllohaPlaybackService_CreatesSessionViaIframeAndBnsi", AllohaPlaybackService_CreatesSessionViaIframeAndBnsi),
    ("AllohaPlaybackService_DoesNotResolveDynamicTokenAfterSuccessfulManifest", AllohaPlaybackService_DoesNotResolveDynamicTokenAfterSuccessfulManifest),
    ("AllohaPlaybackService_RetriesManifestWithDynamicStreamTokenAfter403", AllohaPlaybackService_RetriesManifestWithDynamicStreamTokenAfter403),
    ("AllohaPlaybackService_UsesIframeOriginForMirroredHost", AllohaPlaybackService_UsesIframeOriginForMirroredHost),
    ("AllohaPlaybackService_RejectsUntrustedIframeUrl", AllohaPlaybackService_RejectsUntrustedIframeUrl),
    ("AllohaPlaybackService_PrefersRequestedVoiceWhenBnsiReturnsMultipleTracks", AllohaPlaybackService_PrefersRequestedVoiceWhenBnsiReturnsMultipleTracks),
    ("AllohaPlaybackService_UsesAlternateVoiceFieldWhenLabelIsOpaque", AllohaPlaybackService_UsesAlternateVoiceFieldWhenLabelIsOpaque),
    ("AllohaPlaybackService_MatchesShortAnilibAliasToAnilibria", AllohaPlaybackService_MatchesShortAnilibAliasToAnilibria),
    ("YummyKodikStreamController_AllowsSingleOpaqueAllohaTrackMarker", YummyKodikStreamController_AllowsSingleOpaqueAllohaTrackMarker),
    ("YummyKodikStreamController_RejectsSingleGenericRussianAllohaTrackMarker", YummyKodikStreamController_RejectsSingleGenericRussianAllohaTrackMarker),
    ("YummyKodikStreamController_RejectsMultipleOpaqueAllohaTrackMarkers", YummyKodikStreamController_RejectsMultipleOpaqueAllohaTrackMarkers),
    ("YummyKodikStreamController_OrdersGatewayFallbackProviders", YummyKodikStreamController_OrdersGatewayFallbackProviders),
    ("YummyKodikStreamController_PrioritizesKodikForVoiceMissingFromYummyCatalog", YummyKodikStreamController_PrioritizesKodikForVoiceMissingFromYummyCatalog),
    ("YummyKodikStreamController_UsesSharedYummyVoicePreferenceAcrossProviders", YummyKodikStreamController_UsesSharedYummyVoicePreferenceAcrossProviders),
    ("YummyKodikStreamController_DetectsSavedVoiceOnNeighborProvider", YummyKodikStreamController_DetectsSavedVoiceOnNeighborProvider),
    ("YummyKodikStreamController_UnionsProviderAndManagedVoices", YummyKodikStreamController_UnionsProviderAndManagedVoices),
    ("YummyKodikStreamController_MirrorsWidgetVoiceAcrossMixedProviderKeys", YummyKodikStreamController_MirrorsWidgetVoiceAcrossMixedProviderKeys),
    ("YummyKodikStreamController_ExplicitPerVoiceSourceOverridesSavedDefault", YummyKodikStreamController_ExplicitPerVoiceSourceOverridesSavedDefault),
    ("KodikPlaybackSelector_ResolvesSavedVoiceNameToTranslationId", KodikPlaybackSelector_ResolvesSavedVoiceNameToTranslationId),
    ("EpisodeVersionsMerge_UsesSavedYummyVoicePreferenceForPrimary", EpisodeVersionsMerge_UsesSavedYummyVoicePreferenceForPrimary),
    ("EpisodeVersionsMerge_MatchesSavedKodikTranslationIdFromStrm", EpisodeVersionsMerge_MatchesSavedKodikTranslationIdFromStrm),
    ("EpisodeVersionsMerge_ComparesJellyfin12LinksByItemId", EpisodeVersionsMerge_ComparesJellyfin12LinksByItemId),
    ("EpisodeVersionsMerge_SavedVoiceReplacesAlreadyMergedPrimaryAcrossEpisodes", EpisodeVersionsMergeTests.SavedVoiceReplacesAlreadyMergedPrimaryAcrossEpisodes),
    ("EpisodeVersionsMerge_PreferenceChangeRevisitsEarlierSeriesBeforeNextUnrelatedGroup", EpisodeVersionsMergeTests.PreferenceChangeRevisitsEarlierSeriesBeforeNextUnrelatedGroup),
    ("EpisodeVersionsMerge_PrimaryWithStaleOwnerBecomesVisibleAndThenNoOp", EpisodeVersionsMergeTests.PrimaryWithStaleOwnerBecomesVisibleAndThenNoOp),
    ("EpisodeVersionsMerge_LinkOnlyBatchUsesFreshMetadataAndDoesNotRecurse", EpisodeVersionsMergeTests.LinkOnlyBatchUsesFreshMetadataAndDoesNotRecurse),
    ("EpisodeVersionsMerge_IncompleteConcurrentScanIsNotPersisted", EpisodeVersionsMergeTests.IncompleteConcurrentScanIsNotPersisted),
    ("EpisodeVersionsMerge_NativeAlternatesAlreadyCoveringGroupAreNoOp", EpisodeVersionsMergeTests.NativeAlternatesAlreadyCoveringGroupAreNoOp),
    ("NfoEncoding_SeriesNfoParsesFromUtf8Bytes", NfoEncodingTests.SeriesNfoParsesFromUtf8Bytes),
    ("NfoEncoding_EpisodeNfoParsesFromUtf8Bytes", NfoEncodingTests.EpisodeNfoParsesFromUtf8Bytes),
    ("NfoEncoding_RuntimeEnrichmentParsesFromUtf8BytesAndPreservesMetadata", NfoEncodingTests.RuntimeEnrichmentParsesFromUtf8BytesAndPreservesMetadata),
    ("PostRefreshMergeBarrier_MissingOrUnindexedEpisodeIsUnresolved", PostRefreshMergeBarrier_MissingOrUnindexedEpisodeIsUnresolved),
    ("PostRefreshMergeBarrier_NewEpisodeBeforeArtifactRefreshIsUnresolved", PostRefreshMergeBarrier_NewEpisodeBeforeArtifactRefreshIsUnresolved),
    ("PostRefreshMergeBarrier_LateRefreshedEpisodeIsReady", PostRefreshMergeBarrier_LateRefreshedEpisodeIsReady),
    ("PostRefreshMergeBarrier_PreExistingEpisodeDoesNotBlockReadiness", PostRefreshMergeBarrier_PreExistingEpisodeDoesNotBlockReadiness),
    ("PostRefreshMergeBarrier_DeletedEpisodeMustDisappear", PostRefreshMergeBarrier_DeletedEpisodeMustDisappear),
    ("YummyKodikStreamController_FindsKodikFallbackVoiceByAlias", YummyKodikStreamController_FindsKodikFallbackVoiceByAlias),
    ("YummyKodikStreamController_KodikFallbackUsesDefaultWhenRequestedVoiceMissing", YummyKodikStreamController_KodikFallbackUsesDefaultWhenRequestedVoiceMissing),
    ("AllohaPlaybackService_RewritesManifestUrisToProxyUrls", AllohaPlaybackService_RewritesManifestUrisToProxyUrls),
    ("AllohaPlaybackService_SharesOnlyInFlightResolution", AllohaPlaybackService_SharesOnlyInFlightResolution),
    ("AllohaPlaybackService_DownloadProxyResourceRewritesNestedManifest", AllohaPlaybackService_DownloadProxyResourceRewritesNestedManifest),
    ("AllohaPlaybackService_BuffersUpcomingMediaSegments", AllohaPlaybackService_BuffersUpcomingMediaSegments),
    ("AllohaPlaybackService_PrefetchSurvivesCompletedSegmentRequest", AllohaPlaybackService_PrefetchSurvivesCompletedSegmentRequest),
    ("AllohaPlaybackService_DownloadProxyResourceRefreshesSessionAfter403", AllohaPlaybackService_DownloadProxyResourceRefreshesSessionAfter403),
    ("AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter403", AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter403),
    ("AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfterAlloha500", AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfterAlloha500),
    ("AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter502", AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter502),
    ("AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentAfterNetworkFailure", AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentAfterNetworkFailure),
    ("AllohaPlaybackService_SuppressesConcurrentFailedRefreshStorm", AllohaPlaybackService_SuppressesConcurrentFailedRefreshStorm),
    ("YummyKodikStreamUri_ParsesCvhRequest", YummyKodikStreamUri_ParsesCvhRequest),
    ("YummyKodikStreamUri_ParsesAllohaRequest", YummyKodikStreamUri_ParsesAllohaRequest),
    ("YummyKodikStreamUri_BuildsAllohaRequestWithEmbeddedSource", YummyKodikStreamUri_BuildsAllohaRequestWithEmbeddedSource),
    ("YummyKodikStreamUri_TrimsTrailingSlashFromProviderBaseUrl", YummyKodikStreamUri_TrimsTrailingSlashFromProviderBaseUrl),
    ("YummyKodikLogFilter_DefaultsToWarning", YummyKodikLogFilter_DefaultsToWarning),
    ("YummyKodikLogFilter_UsesConfiguredMinimumLevel", YummyKodikLogFilter_UsesConfiguredMinimumLevel),
    ("YummyKodikLogFilter_CategoryRuleSuppressesPluginInformationLogs", YummyKodikLogFilter_CategoryRuleSuppressesPluginInformationLogs),
    ("YummyKodikLogger_SuppressesInformationBeforeInnerLogger", YummyKodikLogger_SuppressesInformationBeforeInnerLogger),
    ("RefreshPerformanceSummary_IsVisibleWithoutInformationNoise", RefreshPerformanceSummary_IsVisibleWithoutInformationNoise),
    ("JellyfinWebIndexPatcher_InsertsManagedBootstrapBeforeHeadClose", JellyfinWebIndexPatcher_InsertsManagedBootstrapBeforeHeadClose),
    ("JellyfinWebIndexPatcher_ReplacesExistingManagedBootstrap", JellyfinWebIndexPatcher_ReplacesExistingManagedBootstrap),
    ("JellyfinWebIndexPatcher_DoesNotDuplicateBootstrap", JellyfinWebIndexPatcher_DoesNotDuplicateBootstrap),
    ("JellyfinWebIndexPatcher_UpgradesBothScriptsWithoutDuplicates", JellyfinWebIndexPatcher_UpgradesBothScriptsWithoutDuplicates),
    ("SeriesTranslationScript_AcceptsJellyfinPascalCaseTranslationOptions", SeriesTranslationScript_AcceptsJellyfinPascalCaseTranslationOptions)
};

var passed = 0;
var selectedTests = args.Length == 2 && args[0] == "--filter"
    ? tests.Where(test => test.Item1.Contains(args[1], StringComparison.OrdinalIgnoreCase)).ToArray()
    : tests;
if (selectedTests.Length == 0)
{
    throw new ArgumentException("No regression tests match the requested filter.");
}

foreach (var (name, run) in selectedTests)
{
    run();
    Console.WriteLine($"PASS {name}");
    passed++;
}

Console.WriteLine($"Passed {passed}/{selectedTests.Length} tests.");

static async Task RunLiveAllohaProbe()
{
    var source = new YummyAllohaSource
    {
        MovieToken = "9846b7afc8843cd0da8151b9cb4b10",
        RequestToken = "8b5512267a2a52e9de06d67d342e0c",
        TranslationId = 222,
        SeasonNumber = 1,
        EpisodeNumber = 3,
        Hidden = "translation,season,episode",
        RefererUrl = "https://alloha.yani.tv/?token_movie=9846b7afc8843cd0da8151b9cb4b10&translation=222&season=1&episode=3&token=8b5512267a2a52e9de06d67d342e0c&hidden=translation,season,episode"
    };

    await RunLiveAllohaProbeForSource(source, "РуАниме / DEEP");
}

static async Task RunLiveAllohaProbeForSource(
    YummyAllohaSource source,
    string? preferredVoiceName)
{
    const string proxyBaseUrl = "http://localhost:8096/YummyKodik/alloha-proxy";

    Console.WriteLine("LIVE ALLOHA PROBE");
    Console.WriteLine("source=" + source.RefererUrl);

    var logger = NullLogger<AllohaPlaybackService>.Instance;
    var resolverFactory = typeof(AllohaPlaybackService).GetMethod(
        "CreateDefaultStreamTokenResolver",
        BindingFlags.Static | BindingFlags.NonPublic);
    AssertTrue(resolverFactory is not null, "Live probe should be able to create the default stream token resolver.");

    var resolver = resolverFactory!.Invoke(null, new object[] { logger })!;

    using var http = new HttpClient(new RecordingPassThroughHandler(
        new HttpClientHandler(),
        message => Console.WriteLine("HTTP " + message)));

    var ctor = typeof(AllohaPlaybackService)
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
        .SingleOrDefault(x => x.GetParameters().Length == 3);
    AssertTrue(ctor is not null, "Live probe should be able to create the internal Alloha playback service.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { logger, http, resolver });
    await PrintLiveAllohaSourceMetadata(service, source);

    var session = await service.CreateSessionAsync(
        source,
        preferredQuality: 1080,
        preferredVoiceName,
        CancellationToken.None);

    Console.WriteLine("manifestUrl=" + session.ManifestUrl);
    Console.WriteLine("selectedVoice=" + session.SelectedVoiceName);
    Console.WriteLine("availableVoices=" + string.Join(", ", session.AvailableVoiceNames));
    Console.WriteLine("audioTrackId=" + session.AudioTrackId);
    Console.WriteLine("streamTokenReady=" + (!string.IsNullOrWhiteSpace(session.StreamToken)));

    var masterManifest = AllohaPlaybackService.BuildManifestResponseBody(session, proxyBaseUrl);
    Console.WriteLine("masterManifest:");
    Console.WriteLine(masterManifest);
    Console.WriteLine("masterProxyResourceCount=" + session.ProxyResources.Count);

    var initialNestedResources = session.ProxyResources
        .Where(x => x.Value.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.Value, StringComparer.Ordinal)
        .ToArray();

    foreach (var resource in initialNestedResources)
    {
        Console.WriteLine("nestedPlaylist:id=" + resource.Key + " url=" + resource.Value);
        try
        {
            var nested = await service.DownloadProxyResourceAsync(
                session,
                resource.Key,
                resource.Value,
                proxyBaseUrl,
                CancellationToken.None);

            Console.WriteLine("nestedPlaylist:contentType=" + nested.ContentType);
            Console.WriteLine(Encoding.UTF8.GetString(nested.Content));
        }
        catch (Exception ex)
        {
            Console.WriteLine("nestedPlaylist:FAILED");
            Console.WriteLine(ex.ToString());
            throw;
        }
    }

    var segmentResources = session.ProxyResources
        .Where(x =>
            x.Value.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) ||
            x.Value.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
            x.Value.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.Value, StringComparer.Ordinal)
        .Take(4)
        .ToArray();

    Console.WriteLine("segmentProbeCount=" + segmentResources.Length);
    foreach (var resource in segmentResources)
    {
        Console.WriteLine("segment:id=" + resource.Key + " url=" + resource.Value);
        try
        {
            var segment = await service.DownloadProxyResourceAsync(
                session,
                resource.Key,
                resource.Value,
                proxyBaseUrl,
                CancellationToken.None);

            Console.WriteLine("segment:contentType=" + segment.ContentType + " bytes=" + segment.Content.Length);
        }
        catch (Exception ex)
        {
            Console.WriteLine("segment:FAILED");
            Console.WriteLine(ex.ToString());
            throw;
        }
    }

    Console.WriteLine("LIVE ALLOHA PROBE OK");
}

static async Task PrintLiveAllohaSourceMetadata(AllohaPlaybackService service, YummyAllohaSource source)
{
    try
    {
        var serviceType = typeof(AllohaPlaybackService);
        var iframeUrl = InvokeStatic<string>(serviceType, "BuildIframeRequestUrl", source.RefererUrl);
        var iframeOrigin = InvokeStatic<string>(serviceType, "ResolveIframeOrigin", iframeUrl, source.RefererUrl);
        var iframeHtml = await InvokeInstanceTask<string>(
            service,
            "DownloadIframeHtmlAsync",
            iframeUrl,
            CancellationToken.None);
        var bootstrap = InvokeStatic<object>(serviceType, "ParseBootstrapPayload", iframeHtml, source);
        var viewporti = GetProperty<string>(bootstrap, "Viewporti");
        var fileId = GetProperty<long>(bootstrap, "FileId");
        var borth = InvokeStatic<string>(serviceType, "BuildBorthHeader", viewporti);
        var playlist = await InvokeInstanceTask<object>(
            service,
            "RequestBnsiPayloadAsync",
            source,
            iframeOrigin,
            fileId,
            borth,
            CancellationToken.None);

        Console.WriteLine("iframeUrl=" + iframeUrl);
        Console.WriteLine("iframeOrigin=" + iframeOrigin);
        Console.WriteLine("fileId=" + fileId);

        var voices = GetEnumerableProperty(playlist, "AvailableVoiceNames")
            .Select(x => x?.ToString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        Console.WriteLine("bnsiAvailableVoices=" + string.Join(", ", voices));

        var candidates = GetEnumerableProperty(playlist, "ManifestCandidates")
            .Where(x => x is not null)
            .Cast<object>()
            .ToArray();
        Console.WriteLine("bnsiCandidateCount=" + candidates.Length);
        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            Console.WriteLine(
                $"bnsiCandidate[{i}]: quality={GetProperty<object?>(candidate, "Quality")} " +
                $"audioTrackId={GetProperty<string>(candidate, "AudioTrackId")} " +
                $"voice={GetProperty<string>(candidate, "VoiceName")} " +
                $"url={TrimForProbe(GetProperty<string>(candidate, "Url"), 180)}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("bnsiProbe:FAILED");
        Console.WriteLine(ex.ToString());
        throw;
    }
}

static T InvokeStatic<T>(Type type, string methodName, params object?[] args)
{
    var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
    AssertTrue(method is not null, methodName + " should exist.");
    return (T)method!.Invoke(null, args)!;
}

static async Task<T> InvokeInstanceTask<T>(object instance, string methodName, params object?[] args)
{
    var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
    AssertTrue(method is not null, methodName + " should exist.");
    var task = (Task)method!.Invoke(instance, args)!;
    await task.ConfigureAwait(false);
    return (T)task.GetType().GetProperty("Result")!.GetValue(task)!;
}

static T GetProperty<T>(object instance, string propertyName)
{
    var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    AssertTrue(property is not null, propertyName + " should exist.");
    return (T)property!.GetValue(instance)!;
}

static IEnumerable<object?> GetEnumerableProperty(object instance, string propertyName)
{
    var value = GetProperty<object?>(instance, propertyName);
    if (value is not System.Collections.IEnumerable enumerable)
    {
        yield break;
    }

    foreach (var item in enumerable)
    {
        yield return item;
    }
}

static string TrimForProbe(string? value, int maxLength)
{
    var normalized = (value ?? string.Empty).Trim();
    if (normalized.Length <= maxLength)
    {
        return normalized;
    }

    return string.Concat(normalized.AsSpan(0, maxLength), "...");
}

static void ResolveSeasonNumber_UsesViewingOrderIndex()
{
    var anime = BuildSecondSeasonAnime();

    var season = YummySeriesLayoutResolver.ResolveSeasonNumber(anime, anime.Title);
    AssertEqual(2, season, "Season must come from viewing_order for sequels.");
}

static void LimitToExpectedAvailableEpisodes_CapsProviderCoverageToAiredCount()
{
    var anime = new YummyAnimeResponse
    {
        Episodes = new YummyEpisodesInfo
        {
            Aired = 1,
            Count = 12
        },
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1"
            }
        }
    };

    var limited = YummyEpisodeAvailability.LimitToExpectedAvailableEpisodes(anime, TestData.EpisodeOne);

    AssertEqual(1, limited.Length, "Provider coverage should be capped to the aired episode count.");
    AssertEqual(1, limited[0], "Only the first aired episode should remain available.");
}

static void GetExpectedAvailableEpisodeCount_UsesKnownVideoEpisodesWhenAiredLags()
{
    var anime = new YummyAnimeResponse
    {
        Episodes = new YummyEpisodesInfo
        {
            Aired = 1,
            Count = 0
        },
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1"
            },
            new()
            {
                Number = "2"
            }
        }
    };

    var expected = YummyEpisodeAvailability.GetExpectedAvailableEpisodeCount(anime);

    AssertEqual(2, expected, "Explicit video episode numbers should override stale aired metadata when providers are already ahead.");
}

static void LimitToExpectedAvailableEpisodes_PreservesExplicitProviderCoverageWhenAiredLags()
{
    var anime = new YummyAnimeResponse
    {
        Episodes = new YummyEpisodesInfo
        {
            Aired = 1,
            Count = 0
        },
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1"
            }
        }
    };

    var limited = YummyEpisodeAvailability.LimitToExpectedAvailableEpisodes(anime, TestData.EpisodesOneTwo);

    AssertEqual(2, limited.Length, "Provider-reported episode coverage should be preserved when aired lags behind live data.");
    AssertEqual(1, limited[0], "Episode ordering should stay normalized.");
    AssertEqual(2, limited[1], "The second explicitly supported episode should not be cut off by stale aired metadata.");
}

static void LimitToExpectedAvailableEpisodes_KeepsKnownEpisodesWhenAiredCountIsUnknown()
{
    var anime = new YummyAnimeResponse
    {
        Episodes = new YummyEpisodesInfo
        {
            Aired = 0,
            Count = 0
        },
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1"
            }
        }
    };

    var limited = YummyEpisodeAvailability.LimitToExpectedAvailableEpisodes(anime, TestData.UnorderedEpisodesWithDuplicate);

    AssertEqual(3, limited.Length, "When Yummy does not know aired count, existing provider episodes should be preserved.");
    AssertEqual(1, limited[0], "Episodes should be normalized and sorted.");
    AssertEqual(2, limited[1], "Duplicate provider episodes should be removed.");
    AssertEqual(3, limited[2], "Episode ordering should remain stable after normalization.");
}

static void ResolveSeasonNumber_IgnoresCalendarSeasonField()
{
    var anime = new YummyAnimeResponse
    {
        Title = "Похоже, сильнейшая профессия — это не герой или мудрец, а (временный) инспектор?",
        AnimeId = 24394,
        AnimeUrl = "pohozhe-silneyshaya-professiya-eto-ne-geroy-ili-mudrec-a-vremennyy-inspektor",
        Season = 2
    };

    var season = YummySeriesLayoutResolver.ResolveSeasonNumber(anime, anime.Title);
    AssertEqual(1, season, "Calendar season field must not be treated as a sequel season number.");
}

static void ResolveSeasonNumber_ParsesSeasonWithPartSuffix()
{
    var anime = new YummyAnimeResponse
    {
        Title = "О моём перерождении в слизь 2. Часть 2"
    };

    var season = YummySeriesLayoutResolver.ResolveSeasonNumber(anime, anime.Title);
    AssertEqual(2, season, "Season parser should keep the numbered season even when the title has a part suffix.");
}

static void ResolveSeasonNumber_PrefersExplicitSeasonSuffixOverViewingOrderIndex()
{
    var anime = BuildSlimeFourthSeasonAnime();

    var season = YummySeriesLayoutResolver.ResolveSeasonNumber(anime, anime.Title);
    AssertEqual(4, season, "Explicit numeric sequel suffix should win over viewing_order position.");
}

static void ResolveSeasonNumber_UsesShikimoriLayoutFallback()
{
    var anime = new YummyAnimeResponse
    {
        Title = "Атака титанов: Финал"
    };

    var season = YummySeriesLayoutResolver.ResolveSeasonNumber(
        anime,
        anime.Title,
        new ShikimoriSeriesLayoutInfo
        {
            SeasonNumber = 4,
            BaseTitle = "Атака титанов"
        });

    AssertEqual(4, season, "Shikimori fallback should provide sequel season numbers when the title has no explicit number.");
}

static void ResolveSeasonNumber_DoesNotTreatSpecialAsSecondSeason()
{
    var anime = new YummyAnimeResponse
    {
        Title = "Я получил читерские способности в другом мире и стал экстраординарным в реальном мире: История о том, как повышение уровня изменило мою жизнь — Спецвыпуск",
        AnimeId = 11307,
        AnimeUrl = "isekai-de-cheat-skill-wo-te-ni-shita-ore-wa-genjitsu-sekai-wo-mo-musou-suru-level-up-wa-jinsei-wo-kaeta-shin-anime",
        Type = new YummyAnimeType
        {
            Alias = "special"
        },
        ViewingOrder = new List<YummyViewingOrderItem>
        {
            new()
            {
                AnimeId = 10350,
                AnimeUrl = "chiterskiy-navyk-iz-drugogo-mira",
                Title = "Я получил читерские способности в другом мире и стал экстраординарным в реальном мире: История о том, как повышение уровня изменило мою жизнь",
                Data = new YummyViewingOrderData
                {
                    Index = 0
                }
            },
            new()
            {
                AnimeId = 11307,
                AnimeUrl = "isekai-de-cheat-skill-wo-te-ni-shita-ore-wa-genjitsu-sekai-wo-mo-musou-suru-level-up-wa-jinsei-wo-kaeta-shin-anime",
                Title = "Я получил читерские способности в другом мире и стал экстраординарным в реальном мире: История о том, как повышение уровня изменило мою жизнь — Спецвыпуск",
                Data = new YummyViewingOrderData
                {
                    Index = 1
                }
            }
        }
    };

    var season = YummySeriesLayoutResolver.ResolveSeasonNumber(anime, anime.Title);
    AssertEqual(0, season, "Jellyfin specials should use season zero instead of becoming regular seasons.");
}

static void ResolveSeriesTitle_UsesViewingOrderBaseTitle()
{
    var anime = BuildSecondSeasonAnime();
    var season = YummySeriesLayoutResolver.ResolveSeasonNumber(anime, anime.Title);

    var title = YummySeriesLayoutResolver.ResolveSeriesTitle(anime, anime.Title, season);
    AssertEqual("Фермерская жизнь в ином мире", title, "Base title should come from viewing_order for sequels.");
}

static void ResolveSeriesTitle_StripsTrailingSeasonSuffix()
{
    var anime = new YummyAnimeResponse
    {
        Title = "Тестовый сериал 3",
        Season = 3
    };

    var title = YummySeriesLayoutResolver.ResolveSeriesTitle(anime, anime.Title, 3);
    AssertEqual("Тестовый сериал", title, "Trailing season number should be removed from series title.");
}

static void ResolveSeriesTitle_StripsSeasonWithPartSuffix()
{
    var anime = new YummyAnimeResponse
    {
        Title = "О моём перерождении в слизь 2. Часть 2"
    };

    var title = YummySeriesLayoutResolver.ResolveSeriesTitle(anime, anime.Title, 2);
    AssertEqual("О моём перерождении в слизь", title, "Season title stripping should keep the franchise base title for split cours.");
}

static void ResolveSeriesTitle_PrefersStrippedExplicitSeasonSuffixOverViewingOrderBaseTitle()
{
    var anime = BuildSlimeFourthSeasonAnime();
    var season = YummySeriesLayoutResolver.ResolveSeasonNumber(anime, anime.Title);

    var title = YummySeriesLayoutResolver.ResolveSeriesTitle(anime, anime.Title, season);
    AssertEqual("О моём перерождении в слизь", title, "Explicit season suffix should be stripped before viewing_order root picks a recap title.");
}

static void ResolveSeriesTitle_UsesShikimoriLayoutFallback()
{
    var anime = new YummyAnimeResponse
    {
        Title = "Атака титанов: Финал"
    };

    var title = YummySeriesLayoutResolver.ResolveSeriesTitle(
        anime,
        anime.Title,
        seasonNumber: 4,
        shikimoriLayout: new ShikimoriSeriesLayoutInfo
        {
            SeasonNumber = 4,
            BaseTitle = "Атака титанов"
        });

    AssertEqual("Атака титанов", title, "Shikimori fallback should provide the root franchise title when the sequel title has no explicit numeric suffix.");
}

static void ResolveSeriesTitle_UsesViewingOrderBaseTitleForSpecial()
{
    var anime = new YummyAnimeResponse
    {
        Title = "Я получил читерские способности в другом мире и стал экстраординарным в реальном мире: История о том, как повышение уровня изменило мою жизнь — Спецвыпуск",
        AnimeId = 11307,
        AnimeUrl = "isekai-de-cheat-skill-wo-te-ni-shita-ore-wa-genjitsu-sekai-wo-mo-musou-suru-level-up-wa-jinsei-wo-kaeta-shin-anime",
        Type = new YummyAnimeType
        {
            Alias = "special"
        },
        ViewingOrder = new List<YummyViewingOrderItem>
        {
            new()
            {
                AnimeId = 10350,
                AnimeUrl = "chiterskiy-navyk-iz-drugogo-mira",
                Title = "Я получил читерские способности в другом мире и стал экстраординарным в реальном мире: История о том, как повышение уровня изменило мою жизнь",
                Data = new YummyViewingOrderData
                {
                    Index = 0
                }
            },
            new()
            {
                AnimeId = 11307,
                AnimeUrl = "isekai-de-cheat-skill-wo-te-ni-shita-ore-wa-genjitsu-sekai-wo-mo-musou-suru-level-up-wa-jinsei-wo-kaeta-shin-anime",
                Title = "Я получил читерские способности в другом мире и стал экстраординарным в реальном мире: История о том, как повышение уровня изменило мою жизнь — Спецвыпуск",
                Data = new YummyViewingOrderData
                {
                    Index = 1
                }
            }
        }
    };

    var title = YummySeriesLayoutResolver.ResolveSeriesTitle(anime, anime.Title, seasonNumber: 0);
    AssertEqual(
        "Я получил читерские способности в другом мире и стал экстраординарным в реальном мире: История о том, как повышение уровня изменило мою жизнь",
        title,
        "Specials should still reuse the base franchise title for the series folder.");
}

static void ShouldCreateSeasonDirectory_ForAnnouncedSecondSeason()
{
    var anime = BuildSecondSeasonAnime();
    var shouldCreate = YummySeriesLayoutResolver.ShouldCreateSeasonDirectory(anime, 2);
    AssertTrue(shouldCreate, "Second-season TV announcements must still get a Season folder.");
}

static void ShikimoriSeriesLayoutResolver_TreatsPartAsSameSeason()
{
    var layout = ShikimoriSeriesLayoutResolver.BuildFromMainlineChain(
        new[]
        {
            new ShikimoriSeriesLayoutNode
            {
                Id = 37430,
                RussianTitle = "О моём перерождении в слизь",
                Name = "Tensei shitara Slime Datta Ken",
                Kind = "tv"
            },
            new ShikimoriSeriesLayoutNode
            {
                Id = 39551,
                RussianTitle = "О моём перерождении в слизь 2",
                Name = "Tensei shitara Slime Datta Ken 2nd Season",
                Kind = "tv"
            },
            new ShikimoriSeriesLayoutNode
            {
                Id = 41487,
                RussianTitle = "О моём перерождении в слизь 2. Часть 2",
                Name = "Tensei shitara Slime Datta Ken 2nd Season Part 2",
                Kind = "tv"
            },
            new ShikimoriSeriesLayoutNode
            {
                Id = 53580,
                RussianTitle = "О моём перерождении в слизь 3",
                Name = "Tensei shitara Slime Datta Ken 3rd Season",
                Kind = "tv"
            },
            new ShikimoriSeriesLayoutNode
            {
                Id = 59970,
                RussianTitle = "О моём перерождении в слизь 4",
                Name = "Tensei shitara Slime Datta Ken 4th Season",
                Kind = "tv"
            }
        });

    AssertEqual("О моём перерождении в слизь", layout?.BaseTitle ?? string.Empty, "Root franchise title should come from the earliest mainline entry.");
    AssertEqual(4, layout?.SeasonNumber ?? 0, "Split-cour TV entries should not bump the sequel season number.");
}

static void ShikimoriSeriesLayoutResolver_DoesNotTreatSpecialAsNewSeason()
{
    var layout = ShikimoriSeriesLayoutResolver.BuildFromMainlineChain(
        new[]
        {
            new ShikimoriSeriesLayoutNode
            {
                Id = 52830,
                RussianTitle = "Я получил читерские способности в другом мире и стал экстраординарным в реальном мире: История о том, как повышение уровня изменило мою жизнь",
                Name = "Isekai de Cheat Skill wo Te ni Shita Ore wa, Genjitsu Sekai wo mo Musou Suru: Level Up wa Jinsei wo Kaeta",
                Kind = "tv"
            },
            new ShikimoriSeriesLayoutNode
            {
                Id = 56906,
                RussianTitle = "Я получил читерские способности в другом мире и стал экстраординарным в реальном мире: История о том, как повышение уровня изменило мою жизнь — Спецвыпуск",
                Name = "Isekai de Cheat Skill wo Te ni Shita Ore wa, Genjitsu Sekai wo mo Musou Suru: Level Up wa Jinsei wo Kaeta (TV Special)",
                Kind = "special"
            }
        });

    AssertEqual(1, layout?.SeasonNumber ?? 0, "A special should keep the mainline season number instead of becoming a fake season two.");
}

static void NfoBuilder_BuildSeriesNfo_ProducesXml()
{
    var xml = NfoBuilder.BuildSeriesNfo("Test series", "Plot");

    AssertTrue(!string.IsNullOrWhiteSpace(xml), "Series NFO should not be empty.");
    AssertTrue(xml.Contains("<tvshow>", StringComparison.Ordinal), "Series NFO should contain the tvshow root element.");
    AssertTrue(xml.Contains("<title>Test series</title>", StringComparison.Ordinal), "Series NFO should contain the title.");
}

static void NfoBuilder_BuildEpisodeNfo_ProducesXml()
{
    var xml = NfoBuilder.BuildEpisodeNfo(episodeNumber: 1, season: 2, seriesTitle: "Test series", description: "Plot");

    AssertTrue(!string.IsNullOrWhiteSpace(xml), "Episode NFO should not be empty.");
    AssertTrue(xml.Contains("<episodedetails>", StringComparison.Ordinal), "Episode NFO should contain the episodedetails root element.");
    AssertTrue(xml.Contains("<season>2</season>", StringComparison.Ordinal), "Episode NFO should contain the requested season number.");
}

static void NfoBuilder_BuildEpisodeNfo_WritesExactRuntime()
{
    const int durationSeconds = 1425;
    var xml = NfoBuilder.BuildEpisodeNfo(
        episodeNumber: 1,
        season: 2,
        seriesTitle: "Test series",
        description: "Plot",
        durationSeconds);
    var document = XDocument.Parse(xml);

    AssertEqual("24", document.Root!.Element("runtime")!.Value, "Episode NFO should include Jellyfin's minute-based runtime.");
    AssertEqual(
        durationSeconds.ToString(),
        document.Descendants("durationinseconds").Single().Value,
        "Episode NFO should also keep the exact duration in Jellyfin streamdetails.");
    AssertTrue(
        NfoBuilder.TryGetEpisodeRuntimeSeconds(xml, out var parsedDuration),
        "Generated episode NFO runtime should be reusable without another provider request.");
    AssertEqual(durationSeconds, parsedDuration, "Generated episode NFO should retain exact runtime seconds.");
}

static void NfoBuilder_EnsureEpisodeRuntime_BackfillsLegacyNfo()
{
    const int durationSeconds = 1425;
    var legacyXml = NfoBuilder.BuildEpisodeNfo(1, 1, "Test series", "Plot");
    var enrichedXml = NfoBuilder.EnsureEpisodeRuntime(legacyXml, durationSeconds);
    var document = XDocument.Parse(enrichedXml);

    AssertEqual("24", document.Root!.Element("runtime")!.Value, "Runtime backfill should add the minute-based Jellyfin field.");
    AssertEqual(
        durationSeconds.ToString(),
        document.Descendants("durationinseconds").Single().Value,
        "Runtime backfill should add the exact streamdetails duration.");
    AssertEqual(
        enrichedXml,
        NfoBuilder.EnsureEpisodeRuntime(enrichedXml, durationSeconds),
        "Runtime backfill should be idempotent once the NFO already has the desired values.");
    AssertTrue(
        NfoBuilder.TryGetEpisodeRuntimeSeconds(enrichedXml, out var parsedDuration),
        "Backfilled episode runtime should be reusable on later refresh runs.");
    AssertEqual(durationSeconds, parsedDuration, "Backfilled episode NFO should retain exact runtime seconds.");
}

static void EpisodeRuntimeBackfillService_BorrowsSiblingRuntime()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    var seasonDir = Path.Combine(tempRoot, "Series", "Season 01");
    var knownPath = Path.Combine(seasonDir, "S01E01 - Known.nfo");
    var missingPath = Path.Combine(seasonDir, "S01E01 - Missing.nfo");
    var otherEpisodePath = Path.Combine(seasonDir, "S01E02 - Missing.nfo");

    try
    {
        Directory.CreateDirectory(seasonDir);
        File.WriteAllText(
            knownPath,
            NfoBuilder.BuildEpisodeNfo(1, 1, "Test", "Plot", durationSeconds: 1425));
        File.WriteAllText(
            missingPath,
            NfoBuilder.BuildEpisodeNfo(1, 1, "Test", "Plot"));
        File.WriteAllText(
            otherEpisodePath,
            NfoBuilder.BuildEpisodeNfo(2, 1, "Test", "Plot"));

        var updatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updated = EpisodeRuntimeBackfillService
            .BackfillMissingAsync(tempRoot, NullLogger.Instance, CancellationToken.None, updatedPaths)
            .GetAwaiter()
            .GetResult();

        AssertEqual(1, updated, "Only the sibling version of the same episode should be backfilled.");
        AssertTrue(
            updatedPaths.SetEquals(new[] { Path.GetFullPath(missingPath) }),
            "Runtime backfill should report only the NFO path it actually rewrote.");
        var enrichedXml = File.ReadAllText(missingPath);
        AssertTrue(
            NfoBuilder.TryGetExactEpisodeRuntimeSeconds(enrichedXml, out var durationSeconds),
            "Sibling runtime backfill should persist an exact duration.");
        AssertEqual(1425, durationSeconds, "Sibling runtime backfill should reuse the known episode duration.");
        AssertFalse(
            NfoBuilder.TryGetEpisodeRuntimeSeconds(File.ReadAllText(otherEpisodePath), out _),
            "A runtime must not leak into a different episode.");

        var secondPass = EpisodeRuntimeBackfillService
            .BackfillMissingAsync(tempRoot, NullLogger.Instance, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertEqual(0, secondPass, "Sibling runtime backfill should be idempotent.");
    }
    finally
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}

static void RefreshState_RuntimeBackfillReconcilesManagedNfoHash()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    var seriesRoot = Path.Combine(tempRoot, "Series");
    var seasonDir = Path.Combine(seriesRoot, "Season 01");
    const string knownBaseName = "S01E01 - Known";
    const string missingBaseName = "S01E01 - Missing";
    var missingNfoPath = Path.Combine(seasonDir, missingBaseName + ".nfo");

    try
    {
        Directory.CreateDirectory(seasonDir);
        File.WriteAllText(Path.Combine(seriesRoot, "tvshow.nfo"), NfoBuilder.BuildSeriesNfo("Test", "Plot"));
        File.WriteAllText(Path.Combine(seasonDir, knownBaseName + ".strm"), "https://jellyfin.test/known");
        File.WriteAllText(Path.Combine(seasonDir, missingBaseName + ".strm"), "https://jellyfin.test/missing");
        File.WriteAllText(
            Path.Combine(seasonDir, knownBaseName + ".nfo"),
            NfoBuilder.BuildEpisodeNfo(1, 1, "Test", "Plot", durationSeconds: 1425));
        File.WriteAllText(missingNfoPath, NfoBuilder.BuildEpisodeNfo(1, 1, "Test", "Plot"));

        var input = BuildRefreshStateSeasonInput();
        var expectedFiles = new Dictionary<int, HashSet<string>>
        {
            [1] = new(StringComparer.OrdinalIgnoreCase)
            {
                knownBaseName,
                missingBaseName
            }
        };
        var written = RefreshStateManager.WriteSeasonStateAsync(
                seriesRoot,
                input,
                expectedFiles,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertTrue(written, "The fixture state should be written before runtime backfill.");
        AssertTrue(
            RefreshStateManager.CanSkipSingleFileRefreshAsync(seriesRoot, input, CancellationToken.None)
                .GetAwaiter()
                .GetResult(),
            "The fixture should initially match its managed hashes.");

        var updatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updated = EpisodeRuntimeBackfillService
            .BackfillMissingAsync(tempRoot, NullLogger.Instance, CancellationToken.None, updatedPaths)
            .GetAwaiter()
            .GetResult();
        AssertEqual(1, updated, "Runtime backfill should rewrite the missing sibling NFO.");
        AssertFalse(
            RefreshStateManager.CanSkipSingleFileRefreshAsync(seriesRoot, input, CancellationToken.None)
                .GetAwaiter()
                .GetResult(),
            "A runtime rewrite must make the old managed hash stale before reconciliation.");

        var reconciled = RefreshStateManager
            .ReconcileManagedFileHashesAsync(tempRoot, updatedPaths, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertEqual(1, reconciled, "Only the NFO rewritten by runtime backfill should receive a new managed hash.");
        AssertTrue(
            RefreshStateManager.CanSkipSingleFileRefreshAsync(seriesRoot, input, CancellationToken.None)
                .GetAwaiter()
                .GetResult(),
            "Reconciled runtime NFO hashes should preserve the safe refresh skip path.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void RewriteEpisodeFileSeasonPrefix_UsesSeasonZeroForSpecials()
{
    var helperType = typeof(NfoBuilder).Assembly.GetType("YummyKodik.Tasks.SeasonDirectoryMaintenance");
    AssertTrue(helperType != null, "SeasonDirectoryMaintenance helper should exist for season-zero regression coverage.");

    var method = helperType!.GetMethod(
        "RewriteEpisodeFileSeasonPrefix",
        BindingFlags.NonPublic | BindingFlags.Static);
    AssertTrue(method is not null, "Season prefix rewrite helper should be reachable via reflection.");

    var rewritten = (string)method!.Invoke(null, new object[] { "S01E01 - Special", 0 })!;
    AssertEqual("S00E01 - Special", rewritten, "Jellyfin specials should keep the season zero prefix in generated filenames.");
}

static void BuildSeriesFolderName_UsesProviderCompatibleShikimoriTag()
{
    var anime = BuildSecondSeasonAnime();
    var value = YummyProviderTagFormatter.BuildBestIdTag(anime);
    AssertEqual("[shikimori-62146]", value, "Folder tag should match the Shikimori provider filename format.");
}

static void PrepareSeasonDirectory_MovesSeasonOneArtifactsOutOfMistakenSeasonTwoFolder()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    var seriesRoot = Path.Combine(tempRoot, "Series");
    var mistakenSeasonDir = Path.Combine(seriesRoot, "Season 02");
    var targetSeasonDir = Path.Combine(seriesRoot, "Season 01");

    try
    {
        Directory.CreateDirectory(mistakenSeasonDir);
        File.WriteAllText(Path.Combine(mistakenSeasonDir, "S02E01.strm"), "https://example.test/season1/ep1");
        File.WriteAllText(
            Path.Combine(mistakenSeasonDir, "S02E01.nfo"),
            NfoBuilder.BuildEpisodeNfo(episodeNumber: 1, season: 1, seriesTitle: "Test", description: "desc"));

        var preparedDir = InvokePrepareSeasonDirectory(seriesRoot, targetSeasonDir, seasonNumber: 1);

        AssertEqual(targetSeasonDir, preparedDir, "Season preparation should return the canonical season one directory.");
        AssertTrue(File.Exists(Path.Combine(targetSeasonDir, "S01E01.strm")), "Season one STRM should be moved into Season 01.");
        AssertTrue(File.Exists(Path.Combine(targetSeasonDir, "S01E01.nfo")), "Season one NFO should be moved into Season 01.");
        AssertFalse(Directory.Exists(mistakenSeasonDir), "Mistaken Season 02 directory should be removed once empty.");

        var nfo = File.ReadAllText(Path.Combine(targetSeasonDir, "S01E01.nfo"));
        AssertTrue(nfo.Contains("<season>1</season>", StringComparison.Ordinal), "Moved NFO should keep season one metadata.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void PrepareSeasonDirectory_MovesSeasonTwoArtifactsOutOfLegacySeasonOneFolder()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    var seriesRoot = Path.Combine(tempRoot, "Series");
    var legacySeasonDir = Path.Combine(seriesRoot, "Season 01");
    var targetSeasonDir = Path.Combine(seriesRoot, "Season 02");

    try
    {
        Directory.CreateDirectory(legacySeasonDir);
        File.WriteAllText(Path.Combine(legacySeasonDir, "S01E01.strm"), "https://example.test/season2/ep1");
        File.WriteAllText(
            Path.Combine(legacySeasonDir, "S01E01.nfo"),
            NfoBuilder.BuildEpisodeNfo(episodeNumber: 1, season: 2, seriesTitle: "Test", description: "desc"));

        var preparedDir = InvokePrepareSeasonDirectory(seriesRoot, targetSeasonDir, seasonNumber: 2);

        AssertEqual(targetSeasonDir, preparedDir, "Season preparation should return the canonical sequel season directory.");
        AssertTrue(File.Exists(Path.Combine(targetSeasonDir, "S02E01.strm")), "Season two STRM should be moved into Season 02.");
        AssertTrue(File.Exists(Path.Combine(targetSeasonDir, "S02E01.nfo")), "Season two NFO should be moved into Season 02.");
        AssertFalse(Directory.Exists(legacySeasonDir), "Legacy Season 01 directory should be removed once the sequel artifacts are moved out.");

        var nfo = File.ReadAllText(Path.Combine(targetSeasonDir, "S02E01.nfo"));
        AssertTrue(nfo.Contains("<season>2</season>", StringComparison.Ordinal), "Moved NFO should keep sequel metadata.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void PrepareSeasonDirectory_DoesNotMoveActualSeasonOneArtifactsWhenPreparingSeasonTwo()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    var seriesRoot = Path.Combine(tempRoot, "Series");
    var seasonOneDir = Path.Combine(seriesRoot, "Season 01");
    var targetSeasonDir = Path.Combine(seriesRoot, "Season 02");

    try
    {
        Directory.CreateDirectory(seasonOneDir);
        File.WriteAllText(Path.Combine(seasonOneDir, "S01E01.strm"), "https://example.test/season1/ep1");
        File.WriteAllText(
            Path.Combine(seasonOneDir, "S01E01.nfo"),
            NfoBuilder.BuildEpisodeNfo(episodeNumber: 1, season: 1, seriesTitle: "Test", description: "desc"));

        InvokePrepareSeasonDirectory(seriesRoot, targetSeasonDir, seasonNumber: 2);

        AssertTrue(File.Exists(Path.Combine(seasonOneDir, "S01E01.strm")), "Preparing season two must not steal real season one STRM files.");
        AssertTrue(File.Exists(Path.Combine(seasonOneDir, "S01E01.nfo")), "Preparing season two must not steal real season one NFO files.");
        AssertFalse(File.Exists(Path.Combine(targetSeasonDir, "S02E01.strm")), "Season two folder should stay empty when the source artifacts belong to season one.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void PrepareSeasonDirectory_MovesSpecialArtifactsIntoSeasonZeroFolder()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    var seriesRoot = Path.Combine(tempRoot, "Series");
    var legacySeasonDir = Path.Combine(seriesRoot, "Season 01");
    var targetSeasonDir = Path.Combine(seriesRoot, "Season 00");

    try
    {
        Directory.CreateDirectory(legacySeasonDir);
        File.WriteAllText(Path.Combine(legacySeasonDir, "S01E01.strm"), "https://example.test/special/ep1");
        File.WriteAllText(
            Path.Combine(legacySeasonDir, "S01E01.nfo"),
            NfoBuilder.BuildEpisodeNfo(episodeNumber: 1, season: 1, seriesTitle: "Test", description: "desc"));

        var preparedDir = InvokePrepareSeasonDirectory(seriesRoot, targetSeasonDir, seasonNumber: 0);

        AssertEqual(targetSeasonDir, preparedDir, "Special preparation should return the Season 00 directory.");
        AssertTrue(File.Exists(Path.Combine(targetSeasonDir, "S00E01.strm")), "Special STRM should move into Season 00.");
        AssertTrue(File.Exists(Path.Combine(targetSeasonDir, "S00E01.nfo")), "Special NFO should move into Season 00.");
        AssertFalse(Directory.Exists(legacySeasonDir), "Legacy Season 01 special directory should be removed once empty.");

        var nfo = File.ReadAllText(Path.Combine(targetSeasonDir, "S00E01.nfo"));
        AssertTrue(nfo.Contains("<season>0</season>", StringComparison.Ordinal), "Moved special NFO should be rewritten to season zero.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void YummyVideoCatalog_ParsesCvhProviders()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 4861,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1",
                Duration = 1080,
                IframeUrl = "https://play.example/player?anime_id=4861&episode=1&dubbing_code=158&dubbing=%D0%9E%D0%B7%D0%B2%D1%83%D1%87%D0%BA%D0%B0%20AniStar",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "Озвучка AniStar"
                },
                Skips = new YummyVideoSkips
                {
                    Opening = new YummySkipSegment
                    {
                        Time = 90,
                        Length = 60
                    }
                }
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime);

    AssertTrue(catalog.HasAnyCvhEpisodes, "CVH entries should be detected from Yummy videos.");
    AssertEqual(1, catalog.GetSupportedEpisodeNumbers().Single(), "Episode list should include the parsed CVH episode.");
    AssertEqual("AniStar", catalog.GetSupportedVoiceNames(1).Single(), "Voice name should be normalized.");
    AssertEqual(4861L, catalog.FindPreferredPlayableEntry(1, "AniStar")?.Cvh?.AnimeId ?? 0, "CVH source anime id should come from the iframe payload.");

    var chosenVoice = catalog.PickPreferredVoiceName(1, explicitVoiceName: string.Empty, savedVoiceName: "AniStar", preferredFilter: string.Empty, out var reason);
    AssertEqual("AniStar", chosenVoice, "Saved voice should be selected when available.");
    AssertEqual("saved", reason, "Reason should explain why the voice was picked.");
}

static void YummyVideoCatalog_DecodesCvhDubbingCodePlusAsSpace()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 62825,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "2",
                Duration = 1440,
                IframeUrl = "https://play.example/player?anime_id=62825&episode=2&dubbing_code=Dream+Cast&dubbing=%D0%9E%D0%B7%D0%B2%D1%83%D1%87%D0%BA%D0%B0+Dream+Cast",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "Озвучка Dream Cast"
                }
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime);
    var entry = catalog.FindPreferredPlayableEntry(2, "Dream Cast");

    AssertTrue(entry?.Cvh != null, "CVH source should be created for plus-encoded query parameters.");
    AssertEqual("Dream Cast", entry!.Cvh!.DubbingCode, "dubbing_code should be decoded with spaces.");
    AssertEqual("Dream Cast", entry.Cvh.DubbingName, "dubbing should stay normalized after query decoding.");
}

static void YummyClient_GetUserListAsync_AcceptsValidEmptyResponse()
{
    var handler = new DelegatingTestHandler(request =>
    {
        AssertEqual(HttpMethod.Get, request.Method, "Yummy user-list request should use GET.");
        AssertEqual("https://yummy.test/users/42/lists/0", request.RequestUri!.AbsoluteUri, "Yummy user-list request should target the requested user and list.");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"response\":[]}", Encoding.UTF8, "application/json")
        };
    });

    using var http = new HttpClient(handler);
    var client = new YummyClient(http, "test-client", "https://yummy.test");
    var items = client.GetUserListAsync(42, 0, CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual(0, items.Count, "A syntactically complete empty user list is a valid successful response.");
}

static void YummyClient_GetUserListAsync_RejectsMissingResponseArray()
{
    var handler = new DelegatingTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("{}", Encoding.UTF8, "application/json")
    });

    using var http = new HttpClient(handler);
    var client = new YummyClient(http, "test-client", "https://yummy.test");

    var error = AssertThrows<InvalidOperationException>(
        () => client.GetUserListAsync(42, 0, CancellationToken.None).GetAwaiter().GetResult(),
        "A 2xx user-list response without the response array must fail rather than open the cleanup gate.");
    AssertTrue(error.Message.Contains("response", StringComparison.OrdinalIgnoreCase), "Missing response-array error should identify the incomplete payload.");
}

static void CvhClient_AddsBrowserHeadersAndParsesResponses()
{
    var requests = new List<HttpRequestMessage>();
    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));

        if (request.RequestUri!.AbsoluteUri.Contains("/player/sv/playlist?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "items": [
                        {
                          "vkId": "12624919026235",
                          "voiceStudio": "AniStar",
                          "episode": 11,
                          "season": 1
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.RequestUri!.AbsoluteUri.EndsWith("/player/sv/video/12624919026235", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "duration": 1424,
                      "sources": {
                        "hlsUrl": "https://ok6-7.vkuser.net/video.m3u8"
                      }
                    }
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected CVH test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var client = new CvhClient(http);
    var source = new YummyCvhSource
    {
        AnimeId = 61549,
        EpisodeNumber = 11,
        DubbingCode = "AniStar",
        DubbingName = "AniStar"
    };

    var resolved = client.ResolveEpisodeStreamAsync(source, 1080, CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual("https://ok6-7.vkuser.net/video.m3u8", resolved.StreamUrl, "CVH hls url should be returned from the video payload.");
    AssertEqual("AniStar", resolved.VoiceName, "Resolved voice should come from the matching playlist item.");
    AssertEqual(2, requests.Count, "CVH client should perform playlist and video requests.");

    foreach (var request in requests)
    {
        AssertEqual("https://ru.yummyani.me", request.Headers.GetValues("Origin").Single(), "CVH requests must carry the Yummy origin.");
        AssertTrue(request.Headers.Referrer is not null, "CVH requests must carry a Yummy iframe referer.");
        AssertTrue(request.Headers.Referrer!.AbsoluteUri.Contains("anime_id=61549", StringComparison.Ordinal), "Referer should include the CVH anime id.");
        AssertTrue(request.Headers.Referrer!.AbsoluteUri.Contains("episode=11", StringComparison.Ordinal), "Referer should include the requested episode.");
        AssertTrue(request.Headers.Referrer!.AbsoluteUri.Contains("dubbing_code=AniStar", StringComparison.Ordinal), "Referer should include the dubbing code.");
    }
}

static void CvhClient_ThrowsMeaningfulErrorOnEmptyPlaylist()
{
    var handler = new DelegatingTestHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)
    {
        Content = new StringContent(string.Empty)
    });

    using var http = new HttpClient(handler);
    var client = new CvhClient(http);
    var source = new YummyCvhSource
    {
        AnimeId = 61549,
        EpisodeNumber = 11,
        DubbingCode = "AniStar",
        DubbingName = "AniStar"
    };

    var ex = AssertThrows<InvalidOperationException>(
        () => client.ResolveEpisodeStreamAsync(source, 1080, CancellationToken.None).GetAwaiter().GetResult(),
        "Empty CVH payloads should throw a meaningful upstream error.");

    AssertTrue(ex.Message.Contains("empty playlist animeId=61549 response", StringComparison.OrdinalIgnoreCase), "Exception should explain that CVH returned an empty playlist response.");
}

static void CvhClient_RejectsInvalidSourceIdentifiers()
{
    using var http = new HttpClient(new DelegatingTestHandler(_ =>
        throw new InvalidOperationException("CVH validation should fail before any HTTP request.")));
    var client = new CvhClient(http);

    var missingAnimeId = new YummyCvhSource
    {
        AnimeId = 0,
        EpisodeNumber = 1
    };

    var animeIdError = AssertThrows<ArgumentOutOfRangeException>(
        () => client.ResolveEpisodeStreamAsync(missingAnimeId, 720, CancellationToken.None).GetAwaiter().GetResult(),
        "CVH should reject non-positive anime ids.");
    AssertEqual("source", animeIdError.ParamName, "Exception should point to the invalid source object.");

    var missingEpisodeNumber = new YummyCvhSource
    {
        AnimeId = 61549,
        EpisodeNumber = 0
    };

    var episodeError = AssertThrows<ArgumentOutOfRangeException>(
        () => client.ResolveEpisodeStreamAsync(missingEpisodeNumber, 720, CancellationToken.None).GetAwaiter().GetResult(),
        "CVH should reject non-positive episode numbers.");
    AssertEqual("source", episodeError.ParamName, "Exception should point to the invalid source object.");
}

static void CvhClient_PrefersDubbingNameOverNumericDubbingCode()
{
    var handler = new DelegatingTestHandler(request =>
    {
        if (request.RequestUri!.AbsoluteUri.Contains("/player/sv/playlist?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "items": [
                        {
                          "vkId": "12624919026235",
                          "voiceStudio": "AniStar",
                          "episode": 11,
                          "season": 1
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.RequestUri!.AbsoluteUri.EndsWith("/player/sv/video/12624919026235", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "duration": 1424,
                      "sources": {
                        "hlsUrl": "https://ok6-7.vkuser.net/video.m3u8"
                      }
                    }
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected CVH test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var client = new CvhClient(http);
    var source = new YummyCvhSource
    {
        AnimeId = 61549,
        EpisodeNumber = 11,
        DubbingCode = "158",
        DubbingName = "AniStar"
    };

    var resolved = client.ResolveEpisodeStreamAsync(source, 1080, CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual("AniStar", resolved.VoiceName, "Human-readable dubbing name should be used to select the matching playlist item.");
}

static void CvhClient_MatchesEquivalentVoiceKeys()
{
    var handler = new DelegatingTestHandler(request =>
    {
        if (request.RequestUri!.AbsoluteUri.Contains("/player/sv/playlist?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "items": [
                        {
                          "vkId": "12624919026235",
                          "voiceStudio": "2х2",
                          "episode": 11,
                          "season": 1
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.RequestUri!.AbsoluteUri.EndsWith("/player/sv/video/12624919026235", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "duration": 1424,
                      "sources": {
                        "hlsUrl": "https://ok6-7.vkuser.net/video.m3u8"
                      }
                    }
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected CVH test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var client = new CvhClient(http);
    var source = new YummyCvhSource
    {
        AnimeId = 61549,
        EpisodeNumber = 11,
        DubbingCode = "2x2",
        DubbingName = "2x2"
    };

    var resolved = client.ResolveEpisodeStreamAsync(source, 1080, CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual("2х2", resolved.VoiceName, "Equivalent provider keys should match even when Yummy and upstream use different x/х forms.");
}

static void CvhClient_DoesNotFallbackToDifferentVoiceWhenPreferredVoiceIsMissing()
{
    var handler = new DelegatingTestHandler(request =>
    {
        if (request.RequestUri!.AbsoluteUri.Contains("/player/sv/playlist?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "items": [
                        {
                          "vkId": "12624919026235",
                          "voiceStudio": "AniLiberty",
                          "episode": 2,
                          "season": 1
                        }
                      ]
                    }
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected CVH test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var client = new CvhClient(http);
    var source = new YummyCvhSource
    {
        AnimeId = 62825,
        EpisodeNumber = 2,
        DubbingCode = "Dream Cast",
        DubbingName = "Dream Cast"
    };

    var ex = AssertThrows<InvalidOperationException>(
        () => client.ResolveEpisodeStreamAsync(source, 1080, CancellationToken.None).GetAwaiter().GetResult(),
        "CVH should not silently fallback to another translation when the preferred voice is missing.");

    AssertTrue(ex.Message.Contains("CVH episode 2 is not available", StringComparison.OrdinalIgnoreCase), "Exception should explain that the requested episode/voice is unavailable.");
}

static void CvhClient_DownloadManifestAddsHeadersAndRewritesUrls()
{
    var requests = new List<HttpRequestMessage>();
    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                #EXTM3U
                #EXT-X-VERSION:3
                segment-001.ts
                https://cdn.example/segment-002.ts
                """)
        };
    });

    using var http = new HttpClient(handler);
    var client = new CvhClient(http);
    var source = new YummyCvhSource
    {
        AnimeId = 61207,
        EpisodeNumber = 1,
        DubbingCode = "AniStar",
        DubbingName = "AniStar"
    };

    var manifest = client.DownloadManifestAsync(
        "https://vd744.okcdn.ru/path/master.m3u8",
        source,
        CancellationToken.None).GetAwaiter().GetResult();

    AssertTrue(manifest.Contains("https://vd744.okcdn.ru/path/segment-001.ts", StringComparison.Ordinal), "Relative CVH segment urls should be rewritten to absolute urls.");
    AssertTrue(manifest.Contains("https://cdn.example/segment-002.ts", StringComparison.Ordinal), "Absolute CVH segment urls should be preserved.");
    AssertEqual(1, requests.Count, "CVH manifest download should perform a single HTTP request.");

    var request = requests.Single();
    AssertEqual("https://ru.yummyani.me", request.Headers.GetValues("Origin").Single(), "CVH manifest request must carry the Yummy origin.");
    AssertTrue(request.Headers.Referrer is not null, "CVH manifest request must carry a Yummy iframe referer.");
    AssertTrue(request.Headers.Referrer!.AbsoluteUri.Contains("anime_id=61207", StringComparison.Ordinal), "Manifest referer should include the CVH anime id.");
    AssertTrue(request.Headers.Referrer!.AbsoluteUri.Contains("episode=1", StringComparison.Ordinal), "Manifest referer should include the requested episode.");
    AssertTrue(request.Headers.TryGetValues("Accept-Language", out var acceptLanguageValues), "CVH manifest request should include Accept-Language.");
    var acceptLanguage = string.Join(",", acceptLanguageValues ?? Array.Empty<string>());
    AssertTrue(acceptLanguage.Contains("ru-RU", StringComparison.Ordinal), "CVH manifest request should include the primary browser locale.");
    AssertTrue(acceptLanguage.Contains("en-US", StringComparison.Ordinal), "CVH manifest request should keep the browser-like locale fallback.");
    AssertTrue(request.Headers.UserAgent.ToString().Contains("Mozilla/5.0", StringComparison.Ordinal), "CVH manifest request should use a browser-like User-Agent.");
}

static void NeedsKodikSupplement_SkipsAnnouncementWithoutAvailableEpisodes()
{
    var anime = new YummyAnimeResponse
    {
        Title = "Анонс сериала",
        AnimeStatus = new YummyAnimeStatus
        {
            Alias = "anons",
            Title = "Анонс"
        },
        Episodes = new YummyEpisodesInfo
        {
            Count = 12,
            Aired = 0
        },
        Videos = new List<YummyVideoItem>()
    };

    var result = YummyEpisodeAvailability.NeedsKodikSupplement(anime, Array.Empty<int>());
    AssertFalse(result, "Announcements without aired/provider episodes should skip Kodik probing.");
}

static void NeedsKodikSupplement_UsesKodikWhenAiredEpisodesExist()
{
    var anime = new YummyAnimeResponse
    {
        Title = "Уже выходит",
        Episodes = new YummyEpisodesInfo
        {
            Count = 12,
            Aired = 3
        },
        Videos = new List<YummyVideoItem>()
    };

    var result = YummyEpisodeAvailability.NeedsKodikSupplement(anime, Array.Empty<int>());
    AssertTrue(result, "Titles with aired episodes should still probe Kodik when Yummy has not generated any files.");
}

static void KodikClient_GetAnimeInfoAsync_TracksExplicitEpisodeCoverageFromSearch()
{
    const string searchJson =
        """
        {
          "results": [
            {
              "link": "https://kodikplayer.com/serial/1/test?season=1&episode=2",
              "last_season": 1,
              "episodes_count": 2,
              "last_episode": 2,
              "seasons": {
                "1": {
                  "episodes": {
                    "2": {
                      "link": "https://kodikplayer.com/seria/22/hash/720p"
                    }
                  }
                }
              },
              "translation": {
                "id": 610,
                "title": "AniLibria.TV",
                "type": "voice"
              }
            }
          ]
        }
        """;

    var handler = new DelegatingTestHandler(request =>
    {
        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://kodik-api.com/search?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(searchJson)
            };
        }

        throw new InvalidOperationException("Unexpected Kodik test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(YummyKodik.Kodik.KodikClient).GetConstructor(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(HttpClient), typeof(string), typeof(Microsoft.Extensions.Logging.ILogger), typeof(Func<bool>) },
        modifiers: null);
    AssertTrue(ctor is not null, "Kodik test constructor should exist.");

    var client = (YummyKodik.Kodik.KodikClient)ctor!.Invoke(new object[] { http, "test-token", NullLogger.Instance, (Func<bool>)(() => false) });

    var info = client.GetAnimeInfoAsync("59970", YummyKodik.Kodik.KodikIdType.Shikimori, CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual(2, info.SeriesCount, "Search response should keep the highest explicit episode number as the available series count.");
    AssertEqual(1, info.Translations.Count, "Search response should keep the translation entry.");
    AssertEqual("610", info.Translations[0].Id, "Translation id should come from the search response.");
    AssertEqual("2", string.Join(",", info.Translations[0].AvailableEpisodes), "Per-translation episode coverage should keep only explicitly reported episodes.");
    AssertFalse(info.Translations[0].CoversEpisode(1), "Translation should not claim episode one when Kodik only reports episode two.");
    AssertTrue(info.Translations[0].CoversEpisode(2), "Translation should cover the explicitly reported episode.");
}

static void KodikClient_GetEpisodeTimingsAsync_UsesEpisodeLevelSearchLink()
{
    const string episodePlayerUrl = "https://kodikplayer.com/seria/1592110/4dbef1171292a4620dfd5baab0c36311/720p";
    const string searchJson =
        """
        {
          "results": [
            {
              "link": "//kodikplayer.com/serial/74203/a9a3259396843a9efba7e849922bbefb/720p",
              "last_season": 1,
              "episodes_count": 4,
              "last_episode": 4,
              "seasons": {
                "1": {
                  "episodes": {
                    "1": { "link": "//kodikplayer.com/seria/1589871/f89448a3c557915b4a69c647c9a2697f/720p" },
                    "2": { "link": "//kodikplayer.com/seria/1592110/4dbef1171292a4620dfd5baab0c36311/720p" },
                    "3": { "link": "//kodikplayer.com/seria/1593869/f738ffa1d917a449b7ef8494a465ba53/720p" },
                    "4": { "link": "//kodikplayer.com/seria/1596123/30b90fe73a99c3097c47c3ad9c2027f0/720p" }
                  }
                }
              },
              "translation": {
                "id": 609,
                "title": "AniDUB",
                "type": "voice"
              }
            }
          ]
        }
        """;

    var requests = new List<HttpRequestMessage>();
    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://kodik-api.com/search?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(searchJson)
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == episodePlayerUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body></body></html>")
            };
        }

        throw new InvalidOperationException("Unexpected Kodik test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(YummyKodik.Kodik.KodikClient).GetConstructor(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(HttpClient), typeof(string), typeof(Microsoft.Extensions.Logging.ILogger), typeof(Func<bool>) },
        modifiers: null);
    AssertTrue(ctor is not null, "Kodik test constructor should exist.");

    var client = (YummyKodik.Kodik.KodikClient)ctor!.Invoke(new object[] { http, "test-token", NullLogger.Instance, (Func<bool>)(() => false) });

    client.GetEpisodeTimingsAsync("63376", YummyKodik.Kodik.KodikIdType.Shikimori, 2, "609", CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual(2, requests.Count, "Resolving Kodik timings should perform search and then fetch one player page.");
    AssertEqual(episodePlayerUrl, requests[1].RequestUri!.AbsoluteUri, "Kodik should use the episode-specific player link from search data instead of the generic serial link.");
}

static void KodikClient_GetEpisodeLinkAsync_FallsBackToSeasonLinkWhenEpisodeLinkIsBroken()
{
    const string episodePlayerUrl = "https://kodikplayer.com/seria/1599999/broken/720p";
    const string seasonPlayerUrl = "https://kodikplayer.com/season/117971/8af7e0f0ebf3aa3d14b230717a2fbc8e/720p?episode=5&season=1&first_url=false&min_age=16";
    const string scriptUrl = "https://kodikplayer.com/assets/app.player_single.js";
    const string searchJson =
        """
        {
          "results": [
            {
              "link": "//kodikplayer.com/season/117971/8af7e0f0ebf3aa3d14b230717a2fbc8e/720p",
              "last_season": 1,
              "episodes_count": 5,
              "last_episode": 5,
              "seasons": {
                "1": {
                  "episodes": {
                    "5": { "link": "//kodikplayer.com/seria/1599999/broken/720p" }
                  }
                }
              },
              "translation": {
                "id": 610,
                "title": "AniLibria.TV",
                "type": "voice"
              }
            }
          ]
        }
        """;
    const string playerHtml =
        """
        <html>
        <head>
          <script>
            var urlParams = '{"d":"kodik.cc","d_sign":"d-sign","pd":"kodikplayer.com","pd_sign":"pd-sign","ref":"","ref_sign":"ref-sign"}';
            player.type = 'seria';
            player.hash = '0123456789abcdef0123456789abcdef';
            player.id = '1599999';
          </script>
          <script src="/assets/app.player_single.js"></script>
        </head>
        <body></body>
        </html>
        """;
    const string scriptBody = """$.ajax({type:"POST",url:atob("L2Z0b3I="),cache:!1,dataType:"json"})""";
    const string linksJson =
        """
        {
          "links": {
            "720": [
              {
                "src": "https://cloud.kodik-storage.example/useruploads/demo/720.mp4:hls:manifest.m3u8"
              }
            ]
          }
        }
        """;

    var requests = new List<HttpRequestMessage>();
    var postCount = 0;
    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://kodik-api.com/search?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(searchJson)
            };
        }

        if (request.Method == HttpMethod.Get &&
            (request.RequestUri!.AbsoluteUri == episodePlayerUrl ||
             request.RequestUri!.AbsoluteUri == seasonPlayerUrl))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(playerHtml)
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == scriptUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(scriptBody)
            };
        }

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri == "https://kodikplayer.com/ftor")
        {
            postCount++;
            return postCount == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("<html>Error code: upstream</html>")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(linksJson)
                };
        }

        throw new InvalidOperationException("Unexpected Kodik test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(YummyKodik.Kodik.KodikClient).GetConstructor(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(HttpClient), typeof(string), typeof(Microsoft.Extensions.Logging.ILogger), typeof(Func<bool>) },
        modifiers: null);
    AssertTrue(ctor is not null, "Kodik test constructor should exist.");

    var client = (YummyKodik.Kodik.KodikClient)ctor!.Invoke(new object[] { http, "test-token", NullLogger.Instance, (Func<bool>)(() => false) });

    var link = client.GetEpisodeLinkAsync("59970", YummyKodik.Kodik.KodikIdType.Shikimori, 5, "610", CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual("//cloud.kodik-storage.example/useruploads/demo/", link.BasePath, "Fallback season player link should produce the resolved base path.");
    AssertEqual(720, link.MaxQuality, "Fallback season player link should preserve max quality.");
    AssertEqual(2, requests.Count(x => x.Method == HttpMethod.Post && x.RequestUri!.AbsoluteUri == "https://kodikplayer.com/ftor"), "Kodik should retry video link resolution with the season player link after the episode link fails.");
    AssertTrue(
        requests.Any(x => x.Method == HttpMethod.Get && x.RequestUri!.AbsoluteUri == seasonPlayerUrl),
        "Kodik should fetch the generic season player link with the requested episode as fallback.");
}

static void KodikClient_RunSearchCacheSharesInflightAndEvictsFaults()
{
    const string playerUrl = "https://kodikplayer.com/seria/1592110/episode/720p";
    const string searchJson =
        """
        {
          "results": [
            {
              "link": "//kodikplayer.com/serial/74203/series/720p",
              "episodes_count": 1,
              "last_episode": 1,
              "seasons": { "1": { "episodes": { "1": { "link": "//kodikplayer.com/seria/1592110/episode/720p" } } } },
              "translation": { "id": 610, "title": "AniLibria.TV", "type": "voice" }
            }
          ]
        }
        """;

    var searchRequests = 0;
    var metrics = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
    using var http = new HttpClient(new AsyncDelegatingTestHandler(async (request, cancellationToken) =>
    {
        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://kodik-api.com/search?", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref searchRequests);
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) };
        }

        throw new InvalidOperationException("Unexpected cached search request: " + request.RequestUri);
    }));
    var client = new KodikClient(
        http,
        "test-token",
        (key, delta) => metrics.AddOrUpdate(key, delta, (_, current) => current + delta));

    Task.WhenAll(
            Enumerable.Range(0, 5)
                .Select(_ => client.GetAnimeInfoAsync("59970", KodikIdType.Shikimori, CancellationToken.None)))
        .GetAwaiter()
        .GetResult();
    client.GetAnimeInfoAsync("59970", KodikIdType.Shikimori, CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual(1, searchRequests, "Concurrent and sequential same-key searches should share one upstream request.");
    client.GetAnimeInfoAsync("59971", KodikIdType.Shikimori, CancellationToken.None).GetAwaiter().GetResult();
    client.GetAnimeInfoAsync("59970", KodikIdType.Kinopoisk, CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(3, searchRequests, "Search cache key must include both id type and id.");
    AssertTrue(metrics.TryGetValue("kodik.search.cache_hits", out var hits) && hits >= 5, "Shared searches should report cache hits.");

    var freshClient = new KodikClient(http, "test-token", static (_, _) => { });
    freshClient.GetAnimeInfoAsync("59970", KodikIdType.Shikimori, CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(4, searchRequests, "A fresh Kodik client must not inherit a previous refresh run's cache.");

    var faultSearchRequests = 0;
    using var faultHttp = new HttpClient(new DelegatingTestHandler(request =>
    {
        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://kodik-api.com/search?", StringComparison.Ordinal))
        {
            faultSearchRequests++;
            return faultSearchRequests == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("upstream failed") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) };
        }

        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == playerUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><body></body></html>") };
        }

        throw new InvalidOperationException("Unexpected faulted search request: " + request.RequestUri);
    }));
    var faultClient = new KodikClient(faultHttp, "test-token", static (_, _) => { });

    AssertThrows<KodikException>(
        () => faultClient.GetEpisodeTimingsAsync("59970", KodikIdType.Shikimori, 1, "610", CancellationToken.None)
            .GetAwaiter()
            .GetResult(),
        "A real search failure should be surfaced to the caller.");
    faultClient.GetEpisodeTimingsAsync("59970", KodikIdType.Shikimori, 1, "610", CancellationToken.None)
        .GetAwaiter()
        .GetResult();
    AssertEqual(2, faultSearchRequests, "A faulted search entry should be evicted so the next real call retries upstream.");
}

static void KodikClient_RunScriptCacheSharesSuccessAndEvictsAfterPostFailure()
{
    const string playerUrl = "https://kodikplayer.com/seria/1599999/demo/720p";
    const string scriptUrl = "https://kodikplayer.com/assets/app.player_single.js";
    const string playerHtml =
        """
        <html><head>
          <script>
            var urlParams = '{"d":"kodik.cc","d_sign":"d-sign","pd":"kodikplayer.com","pd_sign":"pd-sign","ref":"","ref_sign":"ref-sign"}';
            player.type = 'seria'; player.hash = '0123456789abcdef0123456789abcdef'; player.id = '1599999';
          </script>
          <script src="/assets/app.player_single.js"></script>
        </head><body></body></html>
        """;
    const string scriptBody = """$.ajax({type:"POST",url:atob("L2Z0b3I="),cache:!1,dataType:"json"})""";
    const string linksJson =
        """
        { "links": { "720": [ { "src": "https://cloud.kodik-storage.example/useruploads/demo/720.mp4:hls:manifest.m3u8" } ] } }
        """;

    var scriptGets = 0;
    var videoPosts = 0;
    using var http = new HttpClient(new AsyncDelegatingTestHandler(async (request, cancellationToken) =>
    {
        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == playerUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(playerHtml) };
        }

        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == scriptUrl)
        {
            scriptGets++;
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(scriptBody) };
        }

        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "https://kodikplayer.com/ftor")
        {
            videoPosts++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(linksJson) };
        }

        throw new InvalidOperationException("Unexpected cached script request: " + request.RequestUri);
    }));
    var client = new KodikClient(http, "test-token", static (_, _) => { });
    Task.WhenAll(
            client.GetPlayerLinkAsync(playerUrl, 1, CancellationToken.None),
            client.GetPlayerLinkAsync(playerUrl, 1, CancellationToken.None))
        .GetAwaiter()
        .GetResult();

    AssertEqual(1, scriptGets, "Concurrent successful script decoding should be single-flight for the current refresh run.");
    AssertEqual(2, videoPosts, "Only script bootstrap should be cached; each player payload must still be resolved.");

    var faultScriptGets = 0;
    var faultVideoPosts = 0;
    using var faultHttp = new HttpClient(new DelegatingTestHandler(request =>
    {
        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == playerUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(playerHtml) };
        }

        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == scriptUrl)
        {
            faultScriptGets++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(scriptBody) };
        }

        if (request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "https://kodikplayer.com/ftor")
        {
            faultVideoPosts++;
            return faultVideoPosts == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("upstream failed") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(linksJson) };
        }

        throw new InvalidOperationException("Unexpected invalidated script request: " + request.RequestUri);
    }));
    var faultClient = new KodikClient(faultHttp, "test-token", static (_, _) => { });
    AssertThrows<KodikException>(
        () => faultClient.GetPlayerLinkAsync(playerUrl, 1, CancellationToken.None).GetAwaiter().GetResult(),
        "A real video-links POST failure should be surfaced.");
    faultClient.GetPlayerLinkAsync(playerUrl, 1, CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual(2, faultScriptGets, "A post-path mapping should be reloaded only after the mapping produced a real POST failure.");
    AssertEqual(2, faultVideoPosts, "The next real caller should retry the failed video-links operation once.");
}

static void KodikSupplement_RuntimeProbeUsesDeepValidatedLink()
{
    const string manifestUrl = "https://cdn.kodik.example/useruploads/demo/720.mp4:hls:manifest.m3u8";
    var requests = new List<HttpRequestMessage>();
    using var http = new HttpClient(new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));
        if (request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == manifestUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("#EXTM3U\n#EXTINF:6.0,\nsegment.ts\n#EXT-X-ENDLIST\n")
            };
        }

        throw new InvalidOperationException("Runtime reuse attempted an unexpected link-resolution request: " + request.RequestUri);
    }));
    var client = new KodikClient(http, "test-token", NullLogger.Instance, static () => false);
    var translations = new[]
    {
        new KodikTranslation { Id = "610", Name = "AniLibria.TV", Type = "voice", MaxEpisode = 1 }
    };
    var playable = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal)
    {
        ["610"] = new HashSet<int> { 1 }
    };
    var links = new Dictionary<string, Dictionary<int, KodikLinkInfo>>(StringComparer.Ordinal)
    {
        ["610"] = new Dictionary<int, KodikLinkInfo>
        {
            [1] = new KodikLinkInfo("//cdn.kodik.example/useruploads/demo/", 720)
        }
    };

    var seconds = KodikSupplementService.ResolveKodikDurationSecondsAsync(
            NullLogger.Instance,
            client,
            KodikIdType.Shikimori,
            "59970",
            translations,
            playable,
            links,
            1,
            perf: null,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual(6, seconds!.Value, "Runtime probe should parse the manifest reached through the deep-validated link.");
    AssertEqual(1, requests.Count, "Reusing KodikLinkInfo should require only the manifest request.");
}

static void KodikPlaybackService_ProxiesHlsResourcesAndCachesSegments()
{
    const string proxyBaseUrl = "/base/YummyKodik/kodik-proxy";
    const string manifestUrl = "https://cdn.kodik.example/useruploads/demo/720.mp4:hls:manifest.m3u8";
    const string segmentUrl = "https://cdn.kodik.example/useruploads/demo/segment-001.ts";
    const string manifestText =
        """
        #EXTM3U
        #EXT-X-TARGETDURATION:6
        #EXTINF:6.000,
        segment-001.ts
        #EXT-X-ENDLIST
        """;
    var segmentBody = new byte[] { 1, 2, 3, 4 };
    var requests = new List<HttpRequestMessage>();

    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == manifestUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(manifestText, Encoding.UTF8, "application/vnd.apple.mpegurl")
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == segmentUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(segmentBody)
            };
        }

        throw new InvalidOperationException("Unexpected Kodik proxy test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var service = new KodikPlaybackService(http, NullLogger.Instance);
    var session = KodikPlaybackService.CreateSession(new KodikLinkInfo("//cdn.kodik.example/useruploads/demo/", 720), 720);

    var manifest = service.DownloadProxyResourceAsync(session, "manifest", session.ManifestUrl, proxyBaseUrl, CancellationToken.None)
        .GetAwaiter()
        .GetResult();
    var manifestBody = Encoding.UTF8.GetString(manifest.Content);

    AssertEqual("application/vnd.apple.mpegurl", manifest.ContentType, "Kodik proxy should preserve HLS manifest content type.");
    AssertTrue(manifestBody.Contains(proxyBaseUrl + "/", StringComparison.Ordinal), "Kodik manifest should retain the Jellyfin base path in root-relative proxy urls.");
    AssertFalse(manifestBody.Contains("://", StringComparison.Ordinal), "Kodik proxy urls embedded in manifests must remain root-relative.");
    AssertTrue(manifestBody.Contains(".ts?sessionId=" + session.SessionId, StringComparison.Ordinal), "Kodik segment proxy urls should keep a playable media extension and session id.");

    var segmentResource = session.ProxyResources.Single(x => x.Value == segmentUrl).Key;
    var firstSegment = service.DownloadProxyResourceAsync(session, segmentResource, segmentUrl, proxyBaseUrl, CancellationToken.None)
        .GetAwaiter()
        .GetResult();
    var secondSegment = service.DownloadProxyResourceAsync(session, segmentResource, segmentUrl, proxyBaseUrl, CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual("video/mp2t", firstSegment.ContentType, "Kodik TS segments should get a playable content type when upstream omits one.");
    AssertEqual(string.Join(",", segmentBody), string.Join(",", firstSegment.Content), "Kodik proxy should return the segment body.");
    AssertEqual(string.Join(",", segmentBody), string.Join(",", secondSegment.Content), "Kodik proxy should return cached segment bytes on repeat requests.");
    AssertEqual(1, requests.Count(x => x.RequestUri!.AbsoluteUri == segmentUrl), "Repeated Kodik segment requests should hit the one-minute proxy cache.");
}

static void KodikPlaybackService_RetriesTransientSegmentFailures()
{
    var resourceUrl = $"https://cdn.kodik.example/useruploads/retry-{Guid.NewGuid():N}/segment-001.ts";
    var expectedBody = new byte[] { 5, 6, 7, 8 };
    var requestCount = 0;
    var retryDelays = new List<TimeSpan>();

    var handler = new DelegatingTestHandler(_ =>
    {
        requestCount++;
        if (requestCount == 1)
        {
            throw new HttpRequestException("Simulated connection reset.");
        }

        if (requestCount == 2)
        {
            return new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("temporary upstream failure")
            };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(expectedBody)
        };
    });

    using var http = new HttpClient(handler);
    var service = new KodikPlaybackService(
        http,
        NullLogger.Instance,
        (delay, _) =>
        {
            retryDelays.Add(delay);
            return Task.CompletedTask;
        });
    var session = KodikPlaybackService.CreateSession(
        new KodikLinkInfo("//cdn.kodik.example/useruploads/retry/", 720),
        720);

    var result = service.DownloadProxyResourceAsync(
            session,
            "retry-segment",
            resourceUrl,
            "/YummyKodik/kodik-proxy",
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual(3, requestCount, "Kodik proxy should retry a transport reset and a transient 502.");
    AssertEqual(
        "150,6000",
        string.Join(",", retryDelays.Select(delay => delay.TotalMilliseconds)),
        "Kodik proxy should use a short delay after transport failure and the extended delay only after a received 502.");
    AssertEqual(
        string.Join(",", expectedBody),
        string.Join(",", result.Content),
        "Kodik proxy should return the successful retry payload.");
}

static void KodikPlaybackService_UsesShortBackoffForTransportFailures()
{
    var resourceUrl = $"https://cdn.kodik.example/useruploads/transport-{Guid.NewGuid():N}/segment-001.ts";
    var expectedBody = new byte[] { 13, 14, 15, 16 };
    var requestCount = 0;
    var retryDelays = new List<TimeSpan>();
    var handler = new DelegatingTestHandler(_ =>
    {
        requestCount++;
        if (requestCount == 1)
        {
            throw new HttpRequestException("Simulated connection reset.");
        }

        if (requestCount == 2)
        {
            throw new TaskCanceledException("Simulated upstream timeout.");
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(expectedBody)
        };
    });

    using var http = new HttpClient(handler);
    var service = new KodikPlaybackService(
        http,
        NullLogger.Instance,
        (delay, _) =>
        {
            retryDelays.Add(delay);
            return Task.CompletedTask;
        });
    var session = KodikPlaybackService.CreateSession(
        new KodikLinkInfo("//cdn.kodik.example/useruploads/transport/", 720),
        720);

    var result = service.DownloadProxyResourceAsync(
            session,
            "transport-segment",
            resourceUrl,
            "/YummyKodik/kodik-proxy",
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual(3, requestCount, "Kodik proxy should preserve its three-request cap for transport failures.");
    AssertEqual(
        "150,300",
        string.Join(",", retryDelays.Select(delay => delay.TotalMilliseconds)),
        "Transport resets and timeouts should keep the short retry schedule because the failed request already consumed wait time.");
    AssertEqual(
        string.Join(",", expectedBody),
        string.Join(",", result.Content),
        "Kodik proxy should return the payload after transport recovery.");
}

static void KodikPlaybackService_RetriesTransientNotFoundSegments()
{
    var resourceUrl = $"https://cdn.kodik.example/useruploads/not-found-{Guid.NewGuid():N}/segment-001.ts";
    var expectedBody = new byte[] { 9, 10, 11, 12 };
    var requestCount = 0;
    var retryDelays = new List<TimeSpan>();

    var handler = new DelegatingTestHandler(_ =>
    {
        requestCount++;
        if (requestCount < 3)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(expectedBody)
        };
    });

    using var http = new HttpClient(handler);
    var service = new KodikPlaybackService(
        http,
        NullLogger.Instance,
        (delay, _) =>
        {
            retryDelays.Add(delay);
            return Task.CompletedTask;
        });
    var session = KodikPlaybackService.CreateSession(
        new KodikLinkInfo("//cdn.kodik.example/useruploads/not-found/", 720),
        720);

    var result = service.DownloadProxyResourceAsync(
            session,
            "not-found-segment",
            resourceUrl,
            "/YummyKodik/kodik-proxy",
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual(3, requestCount, "Kodik proxy should retry a CDN segment that is temporarily unavailable with 404.");
    AssertEqual(
        "2000,6000",
        string.Join(",", retryDelays.Select(delay => delay.TotalMilliseconds)),
        "Received transient 404 responses should keep the extended bounded recovery window.");
    AssertEqual(
        string.Join(",", expectedBody),
        string.Join(",", result.Content),
        "Kodik proxy should return a segment after CDN propagation completes.");
}

static void KodikPlaybackService_BoundsPersistentTransientFailures()
{
    var resourceUrl = $"https://cdn.kodik.example/useruploads/persistent-{Guid.NewGuid():N}/manifest.m3u8";
    var requestCount = 0;
    var retryDelays = new List<TimeSpan>();
    var handler = new DelegatingTestHandler(_ =>
    {
        requestCount++;
        return new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("persistent upstream failure")
        };
    });

    using var http = new HttpClient(handler);
    var service = new KodikPlaybackService(
        http,
        NullLogger.Instance,
        (delay, _) =>
        {
            retryDelays.Add(delay);
            return Task.CompletedTask;
        });
    var session = KodikPlaybackService.CreateSession(
        new KodikLinkInfo("//cdn.kodik.example/useruploads/persistent/", 720),
        720);

    AssertThrows<InvalidOperationException>(() => service.DownloadProxyResourceAsync(
            session,
            "manifest",
            resourceUrl,
            "/YummyKodik/kodik-proxy",
            CancellationToken.None)
        .GetAwaiter()
        .GetResult(),
        "Persistent Kodik CDN failures should surface after the bounded retry window.");

    AssertEqual(3, requestCount, "Persistent Kodik CDN failures must remain capped at three HTTP requests.");
    AssertEqual(
        "2000,6000",
        string.Join(",", retryDelays.Select(delay => delay.TotalMilliseconds)),
        "Persistent failures should stop after the bounded eight-second recovery window.");
}

static void KodikClient_GetAnimeInfoAsync_FallsBackToHtmlFindPlayerResponse()
{
    const string finalPlayerUrl = "https://kodik.info/serial/12345/abcdef?episode=1&season=1";
    const string playerHtml =
        """
        <!DOCTYPE html>
        <html>
        <body>
            <div class="serial-series-box">
                <select>
                    <option value="1">1</option>
                    <option value="2">2</option>
                    <option value="3">3</option>
                </select>
            </div>
            <div class="serial-translations-box">
                <select>
                    <option value="110" data-translation-type="voice">AniLibria</option>
                    <option value="111" data-translation-type="subtitles">Субтитры</option>
                </select>
            </div>
        </body>
        </html>
        """;

    var requests = new List<HttpRequestMessage>();
    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://kodik-api.com/search?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"results":[]}""")
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://kodikplayer.com/find-player?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(playerHtml)
                {
                    Headers =
                    {
                        ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html")
                    }
                },
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalPlayerUrl)
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == finalPlayerUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(playerHtml)
            };
        }

        throw new InvalidOperationException("Unexpected Kodik test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(YummyKodik.Kodik.KodikClient).GetConstructor(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(HttpClient), typeof(string), typeof(Microsoft.Extensions.Logging.ILogger), typeof(Func<bool>) },
        modifiers: null);
    AssertTrue(ctor is not null, "Kodik test constructor should exist.");

    var client = (YummyKodik.Kodik.KodikClient)ctor!.Invoke(new object[] { http, "test-token", NullLogger.Instance, (Func<bool>)(() => false) });

    var info = client.GetAnimeInfoAsync("61931", YummyKodik.Kodik.KodikIdType.Shikimori, CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual(3, info.SeriesCount, "HTML find-player fallback should keep serial episode count.");
    AssertEqual(2, info.Translations.Count, "HTML find-player fallback should keep parsed translations.");
    AssertEqual("AniLibria", info.Translations[0].Name, "First translation should be parsed from the HTML fallback.");
    AssertEqual("110", info.Translations[0].Id, "Translation id should come from the HTML fallback select value.");
    AssertEqual(finalPlayerUrl, requests[2].RequestUri!.AbsoluteUri, "Fallback should follow the final player page url after find-player returns HTML.");
}

static void KodikTokenResolver_DecodesOnlineModPayload()
{
    const string expectedToken = "resolved-token";
    var numbers = EncodeKodikSecret(expectedToken, "kodik");
    var script =
        "const endpoint = 'https://kodik-api.com/search';\n" +
        "var token = Utils.decodeSecret([" + string.Join(",", numbers) + "]);";

    var handler = new DelegatingTestHandler(request =>
    {
        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == KodikTokenResolver.OnlineModUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(script)
            };
        }

        throw new InvalidOperationException("Kodik token resolver should not use fallback when online_mod.js contains a decodable token.");
    });

    using var http = new HttpClient(handler);
    var token = KodikTokenResolver.ResolveTokenAsync(http, CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual(expectedToken, token, "Kodik token resolver should decode the online_mod.js payload.");
}

static void KodikTitleResolver_NormalizesUnicodeWords()
{
    var normalized = InvokeStatic<string>(
        typeof(KodikTitleResolver),
        "Normalize",
        " Блич: Thousand-Year Blood War 2!! ");

    AssertEqual("бличthousandyearbloodwar2", normalized, "Kodik title normalization should keep letters and digits across alphabets.");
}

static int[] EncodeKodikSecret(string token, string decodeKey)
{
    var hash = InvokeStatic<string>(typeof(KodikTokenResolver), "Salt", "123456789" + decodeKey);
    var hashBuilder = new StringBuilder(hash);
    while (hashBuilder.Length < token.Length)
    {
        hashBuilder.Append(hash);
    }

    var expandedHash = hashBuilder.ToString();
    return token
        .Select((ch, index) => ch ^ expandedHash[index])
        .ToArray();
}

static void CvhClient_BuildManifestResponseBody_ProxiesNestedPlaylists()
{
    var session = new CvhPlaybackSession
    {
        SessionId = "cvh-session",
        ManifestUrl = "https://vd744.okcdn.ru/path/master.m3u8",
        ManifestText = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=1560378
            https://ok6-7.vkuser.net/expires/1774215751644/srcIp/212.58.123.211/type/2/id/12369819011643/video/
            #EXT-X-STREAM-INF:BANDWIDTH=5602553
            https://ok6-7.vkuser.net/expires/1774215751644/srcIp/212.58.123.211/type/4/id/12369819011643/video/
            """
    };

    const string proxyBaseUrl = "/base/YummyKodik/cvh-proxy";
    var manifest = CvhClient.BuildManifestResponseBody(session, proxyBaseUrl);

    AssertTrue(manifest.Contains(proxyBaseUrl + "/", StringComparison.Ordinal), "CVH master manifest should retain the Jellyfin base path in root-relative proxy urls.");
    AssertFalse(manifest.Contains("://", StringComparison.Ordinal), "CVH proxy urls embedded in manifests must remain root-relative.");
    AssertTrue(manifest.Contains(".m3u8?sessionId=cvh-session", StringComparison.Ordinal), "Proxied CVH nested playlist urls should carry an HLS extension and session id.");
    AssertEqual(2, session.ProxyResources.Count, "Each nested CVH playlist url should be tracked as a proxy resource.");
}

static void YummyKodikStreamUri_ParsesCvhRequest()
{
    var uri = "http://localhost:8096/YummyKodik/stream?provider=cvh&animeId=4861&ep=1&voice=AniStar&format=hls";
    var parsed = YummyKodikStreamUri.TryParseRequest(uri, out var request);

    AssertTrue(parsed, "CVH HTTP uri should be parsed.");
    AssertEqual(YummyStreamProviderKind.Cvh, request.Provider, "Provider kind should be CVH.");
    AssertEqual(4861L, request.AnimeId, "Anime id should be parsed from query.");
    AssertEqual(1, request.Episode, "Episode should be parsed from query.");
    AssertEqual("AniStar", request.VoiceName, "Voice should be parsed from query.");
}

static void YummyVideoCatalog_ParsesAllohaProviders()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 19312,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1",
                Duration = 1420,
                IframeUrl = "https://alloha.yani.tv/?token_movie=321272ebdc58c94adce7628d4a6017&translation=215&season=1&episode=1&token=8b5512267a2a52e9de06d67d342e0c&hidden=translation,season,episode",
                Data = new YummyVideoData
                {
                    PlayerId = 2,
                    Dubbing = "Озвучка Dream Cast"
                },
                Skips = new YummyVideoSkips
                {
                    Ending = new YummySkipSegment
                    {
                        Time = 1200,
                        Length = 90
                    }
                }
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime);

    AssertTrue(catalog.HasAnyAllohaEpisodes, "Alloha entries should be detected from Yummy videos.");
    AssertEqual(1, catalog.GetSupportedEpisodeNumbers(YummyVideoProviderKind.Alloha).Single(), "Episode list should include the parsed Alloha episode.");
    AssertEqual("Dream Cast", catalog.GetSupportedVoiceNames(YummyVideoProviderKind.Alloha, 1).Single(), "Alloha voice name should be normalized.");
    AssertEqual(215, catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Alloha, 1, "Dream Cast")?.Alloha?.TranslationId ?? 0, "Alloha translation id should come from the iframe payload.");

    var chosenVoice = catalog.PickPreferredVoiceName(
        YummyVideoProviderKind.Alloha,
        1,
        explicitVoiceName: string.Empty,
        savedVoiceName: "Dream Cast",
        preferredFilter: string.Empty,
        out var reason);
    AssertEqual("Dream Cast", chosenVoice, "Saved Alloha voice should be selected when available.");
    AssertEqual("saved", reason, "Reason should explain why the Alloha voice was picked.");
}

static void AllohaApiClient_ParsesSerialCatalogEntries()
{
    var requests = new List<HttpRequestMessage>();
    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "status": "success",
                  "data": {
                    "seasons": {
                      "1": {
                        "season": 1,
                        "episodes": {
                          "1": {
                            "episode": 1,
                            "translation": {
                              "215": {
                                "translation": "Dream Cast",
                                "iframe": "https://larkin-as.stloadi.live/?token_movie=321272ebdc58c94adce7628d4a6017&translation=215&season=1&episode=1&token=d317441359e505c343c2063edc97e7"
                              },
                              "79": {
                                "translation": "Субтитры",
                                "iframe": "https://larkin-as.stloadi.live/?token_movie=321272ebdc58c94adce7628d4a6017&translation=79&season=1&episode=1&token=d317441359e505c343c2063edc97e7"
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """)
        };
    });

    using var http = new HttpClient(handler);
    var client = new AllohaApiClient(http, "api-token-value");
    var entries = client.GetCatalogEntriesByKpAsync(10683417, CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual(2, entries.Count, "Alloha API should produce an entry per translation.");
    AssertEqual(1, entries.Select(x => x.EpisodeNumber).Distinct().Single(), "Alloha API entries should keep the episode number.");
    AssertTrue(entries.Any(x => string.Equals(x.DisplayVoiceName, "Dream Cast", StringComparison.Ordinal)), "Dream Cast must be parsed from Alloha API.");
    AssertTrue(entries.Any(x => string.Equals(x.DisplayVoiceName, "Субтитры", StringComparison.Ordinal)), "Subtitle translation must be parsed from Alloha API.");
    AssertEqual(215, entries.First(x => x.DisplayVoiceName == "Dream Cast").Alloha?.TranslationId ?? 0, "Alloha API entry should keep the translation id.");
    AssertEqual(1, requests.Count, "Alloha API client should perform a single request.");
    AssertTrue(requests.Single().RequestUri!.Query.Contains("token=api-token-value", StringComparison.Ordinal), "Alloha API request should include the configured token.");
    AssertTrue(requests.Single().RequestUri!.Query.Contains("kp=10683417", StringComparison.Ordinal), "Alloha API request should query by kp id.");
}

static void AllohaApiClient_ParsesEpisodesArrayCatalogEntries()
{
    var handler = new DelegatingTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {
              "status": "success",
              "data": {
                "seasons": {
                  "2": {
                    "season": 2,
                    "episodes": [
                      {
                        "episode": 1,
                        "translation": {
                          "215": {
                            "translation": "Dream Cast",
                            "iframe": "https://larkin-as.stloadi.live/?token_movie=movie-1&translation=215&season=2&episode=1&token=req-1"
                          }
                        }
                      },
                      {
                        "episode": 2,
                        "translation": {
                          "79": {
                            "translation": "Субтитры",
                            "iframe": "https://larkin-as.stloadi.live/?token_movie=movie-1&translation=79&season=2&episode=2&token=req-2"
                          }
                        }
                      }
                    ]
                  }
                }
              }
            }
            """)
    });

    using var http = new HttpClient(handler);
    var client = new AllohaApiClient(http, "api-token-value");
    var entries = client.GetCatalogEntriesByKpAsync(5235921, CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual(2, entries.Count, "Alloha API should tolerate episodes arrays and still emit entries.");
    AssertEqual("1,2", string.Join(",", entries.Select(x => x.EpisodeNumber).OrderBy(x => x)), "Episode numbers should be parsed from array items.");
    AssertEqual(2, entries.First(x => x.DisplayVoiceName == "Dream Cast").Alloha?.SeasonNumber ?? 0, "Season number should survive array parsing.");
    AssertEqual(79, entries.First(x => x.DisplayVoiceName == "Субтитры").Alloha?.TranslationId ?? 0, "Translation id should still come from the nested translation map.");
}

static void AllohaApiCatalogLoader_FilterEntriesForSeason_KeepsOnlyRequestedSeason()
{
    var entries = new[]
    {
        new YummyVideoEntry
        {
            EpisodeNumber = 1,
            Provider = YummyVideoProviderKind.Alloha,
            DisplayVoiceName = "Season 1 voice",
            Alloha = new YummyAllohaSource
            {
                TranslationId = 215,
                SeasonNumber = 1,
                EpisodeNumber = 1
            }
        },
        new YummyVideoEntry
        {
            EpisodeNumber = 1,
            Provider = YummyVideoProviderKind.Alloha,
            DisplayVoiceName = "Season 2 voice",
            Alloha = new YummyAllohaSource
            {
                TranslationId = 215,
                SeasonNumber = 2,
                EpisodeNumber = 1
            }
        }
    };

    var filtered = AllohaApiCatalogLoader.FilterEntriesForSeason(entries, seasonNumber: 2);

    AssertEqual(1, filtered.Count, "Season filtering should keep only entries from the requested season.");
    AssertEqual("Season 2 voice", filtered.Single().DisplayVoiceName, "Season filtering should retain the matching-season translation.");
}

static void AllohaApiCatalogLoader_LoadEntriesAsync_CachesSuccessfulCatalogLoads()
{
    var requests = 0;
    var handler = new DelegatingTestHandler(_ =>
    {
        Interlocked.Increment(ref requests);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildAllohaApiCatalogPayload())
        };
    });

    using var http = new HttpClient(handler);
    var cfg = BuildAllohaApiConfig("success-cache-token");
    var anime = BuildAllohaApiAnime(animeId: 41001, kpId: 910001);

    var first = AllohaApiCatalogLoader.LoadEntriesAsync(cfg, anime, http, NullLogger.Instance, CancellationToken.None)
        .GetAwaiter()
        .GetResult();
    var second = AllohaApiCatalogLoader.LoadEntriesAsync(cfg, anime, http, NullLogger.Instance, CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual(1, requests, "Successful Alloha API responses should be cached for repeated lookups.");
    AssertEqual(1, first.Count, "Cached Alloha API lookup should keep the parsed entry.");
    AssertEqual(1, second.Count, "Second Alloha API lookup should reuse the cached entry set.");
}

static void AllohaApiCatalogLoader_LoadEntriesAsync_DeduplicatesConcurrentCatalogLoads()
{
    var requests = 0;
    var handler = new DelegatingTestHandler(_ =>
    {
        Interlocked.Increment(ref requests);
        Thread.Sleep(75);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildAllohaApiCatalogPayload())
        };
    });

    using var http = new HttpClient(handler);
    var cfg = BuildAllohaApiConfig("inflight-cache-token");
    var anime = BuildAllohaApiAnime(animeId: 41002, kpId: 910002);

    var tasks = Enumerable.Range(0, 5)
        .Select(_ => Task.Run(() => AllohaApiCatalogLoader.LoadEntriesAsync(cfg, anime, http, NullLogger.Instance, CancellationToken.None)))
        .ToArray();

    var results = Task.WhenAll(tasks).GetAwaiter().GetResult();

    AssertEqual(1, requests, "Concurrent Alloha API lookups for the same title should share one upstream request.");
    AssertTrue(results.All(x => x.Count == 1), "Concurrent Alloha API lookups should all receive the shared catalog result.");
}

static void AllohaApiCatalogLoader_LoadEntriesAsync_CachesFailuresBriefly()
{
    var requests = 0;
    var handler = new DelegatingTestHandler(_ =>
    {
        Interlocked.Increment(ref requests);
        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""
                <html>
                <head><title>503 Service Unavailable</title></head>
                <body><center><h1>503 Service Unavailable</h1></center></body>
                </html>
                """)
        };
    });

    using var http = new HttpClient(handler);
    var cfg = BuildAllohaApiConfig("failure-cache-token");
    var anime = BuildAllohaApiAnime(animeId: 41003, kpId: 910003);

    var first = AllohaApiCatalogLoader.LoadEntriesAsync(cfg, anime, http, NullLogger.Instance, CancellationToken.None)
        .GetAwaiter()
        .GetResult();
    var second = AllohaApiCatalogLoader.LoadEntriesAsync(cfg, anime, http, NullLogger.Instance, CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual(0, first.Count, "Failed Alloha API lookups should keep the existing empty fallback behavior.");
    AssertEqual(0, second.Count, "Repeated failed Alloha API lookups should continue returning the empty fallback.");
    AssertEqual(1, requests, "Failed Alloha API lookups should be negative-cached briefly to avoid hammering the upstream.");
}

static void YummyVideoCatalog_MergesAdditionalAllohaEntriesWithoutOverwritingYummyData()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 19312,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1",
                Duration = 1420,
                IframeUrl = "https://alloha.yani.tv/?token_movie=321272ebdc58c94adce7628d4a6017&translation=215&season=1&episode=1&token=8b5512267a2a52e9de06d67d342e0c&hidden=translation,season,episode",
                Data = new YummyVideoData
                {
                    PlayerId = 2,
                    Dubbing = "Озвучка AniLibria"
                }
            }
        }
    };

    var additionalEntries = new[]
    {
        new YummyVideoEntry
        {
            EpisodeNumber = 1,
            Provider = YummyVideoProviderKind.Alloha,
            RawDubbing = "Dream Cast",
            DisplayVoiceName = "Dream Cast",
            DurationSeconds = 0,
            IframeUrl = "https://larkin-as.stloadi.live/?token_movie=321272ebdc58c94adce7628d4a6017&translation=215&season=1&episode=1&token=d317441359e505c343c2063edc97e7",
            Alloha = new YummyAllohaSource
            {
                MovieToken = "321272ebdc58c94adce7628d4a6017",
                RequestToken = "d317441359e505c343c2063edc97e7",
                TranslationId = 215,
                SeasonNumber = 1,
                EpisodeNumber = 1,
                RefererUrl = "https://larkin-as.stloadi.live/?token_movie=321272ebdc58c94adce7628d4a6017&translation=215&season=1&episode=1&token=d317441359e505c343c2063edc97e7"
            }
        },
        new YummyVideoEntry
        {
            EpisodeNumber = 1,
            Provider = YummyVideoProviderKind.Alloha,
            RawDubbing = "Субтитры",
            DisplayVoiceName = "Субтитры",
            DurationSeconds = 0,
            IframeUrl = "https://larkin-as.stloadi.live/?token_movie=321272ebdc58c94adce7628d4a6017&translation=79&season=1&episode=1&token=d317441359e505c343c2063edc97e7",
            Alloha = new YummyAllohaSource
            {
                MovieToken = "321272ebdc58c94adce7628d4a6017",
                RequestToken = "d317441359e505c343c2063edc97e7",
                TranslationId = 79,
                SeasonNumber = 1,
                EpisodeNumber = 1,
                RefererUrl = "https://larkin-as.stloadi.live/?token_movie=321272ebdc58c94adce7628d4a6017&translation=79&season=1&episode=1&token=d317441359e505c343c2063edc97e7"
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime, additionalEntries);
    var voiceNames = catalog.GetSupportedVoiceNames(YummyVideoProviderKind.Alloha, 1);

    AssertEqual(2, voiceNames.Count, "Merged Alloha catalog should include the extra translation.");
    AssertTrue(voiceNames.Contains("Dream Cast"), "Alloha API should be able to replace a misleading Yummy label for the same source.");
    AssertTrue(voiceNames.Contains("Субтитры"), "Extra Alloha API translation should be added.");
    AssertEqual(1420, catalog.GetDurationSeconds(YummyVideoProviderKind.Alloha, 1, "Dream Cast") ?? 0, "Existing Yummy entry should keep its richer duration data.");
    AssertEqual(
        "8b5512267a2a52e9de06d67d342e0c",
        catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Alloha, 1, "Dream Cast")?.Alloha?.RequestToken ?? string.Empty,
        "Duplicate Alloha merges should keep the original playable source fields when they already exist.");
}

static void YummyVideoCatalog_KeepsAllohaEntriesFromDifferentSeasonsDistinct()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 19312
    };

    var additionalEntries = new[]
    {
        new YummyVideoEntry
        {
            EpisodeNumber = 1,
            Provider = YummyVideoProviderKind.Alloha,
            RawDubbing = "Dream Cast S1",
            DisplayVoiceName = "Dream Cast S1",
            IframeUrl = "https://larkin-as.stloadi.live/?token_movie=movie&translation=215&season=1&episode=1&token=req-1",
            Alloha = new YummyAllohaSource
            {
                MovieToken = "movie",
                RequestToken = "req-1",
                TranslationId = 215,
                SeasonNumber = 1,
                EpisodeNumber = 1,
                RefererUrl = "https://larkin-as.stloadi.live/?token_movie=movie&translation=215&season=1&episode=1&token=req-1"
            }
        },
        new YummyVideoEntry
        {
            EpisodeNumber = 1,
            Provider = YummyVideoProviderKind.Alloha,
            RawDubbing = "Dream Cast S2",
            DisplayVoiceName = "Dream Cast S2",
            IframeUrl = "https://larkin-as.stloadi.live/?token_movie=movie&translation=215&season=2&episode=1&token=req-2",
            Alloha = new YummyAllohaSource
            {
                MovieToken = "movie",
                RequestToken = "req-2",
                TranslationId = 215,
                SeasonNumber = 2,
                EpisodeNumber = 1,
                RefererUrl = "https://larkin-as.stloadi.live/?token_movie=movie&translation=215&season=2&episode=1&token=req-2"
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime, additionalEntries);
    var voiceNames = catalog.GetSupportedVoiceNames(YummyVideoProviderKind.Alloha, 1);

    AssertEqual(2, voiceNames.Count, "Alloha entries from different seasons should not collapse into one dedupe key.");
    AssertTrue(voiceNames.Contains("Dream Cast S1"), "Season one Alloha translation should remain distinct.");
    AssertTrue(voiceNames.Contains("Dream Cast S2"), "Season two Alloha translation should remain distinct.");
}

static void YummyVideoCatalog_CombinesCoverageAcrossProviders()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 21008,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1",
                IframeUrl = "https://alloha.yani.tv/?token_movie=movie&translation=215&season=1&episode=1&token=request",
                Data = new YummyVideoData
                {
                    PlayerId = 2,
                    Dubbing = "Озвучка Dream Cast"
                }
            },
            new()
            {
                Number = "2",
                IframeUrl = "https://alloha.yani.tv/?token_movie=movie&translation=215&season=1&episode=2&token=request",
                Data = new YummyVideoData
                {
                    PlayerId = 2,
                    Dubbing = "Озвучка Dream Cast"
                }
            },
            new()
            {
                Number = "1",
                IframeUrl = "https://play.example/player?anime_id=21008&episode=1&dubbing_code=158&dubbing=AniStar",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "AniStar"
                }
            },
            new()
            {
                Number = "2",
                IframeUrl = "https://play.example/player?anime_id=21008&episode=2&dubbing_code=158&dubbing=AniStar",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "AniStar"
                }
            },
            new()
            {
                Number = "3",
                IframeUrl = "https://play.example/player?anime_id=21008&episode=3&dubbing_code=158&dubbing=AniStar",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "AniStar"
                }
            },
            new()
            {
                Number = "4",
                IframeUrl = "https://play.example/player?anime_id=21008&episode=4&dubbing_code=158&dubbing=AniStar",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "AniStar"
                }
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime);
    var providers = new[] { YummyVideoProviderKind.Alloha, YummyVideoProviderKind.Cvh };

    AssertEqual("1,2,3,4", string.Join(",", catalog.GetSupportedEpisodeNumbersAcrossProviders(providers)), "Combined episode coverage should use the union of provider episode lists.");
    AssertEqual("AniStar,Dream Cast", string.Join(",", catalog.GetAllVoiceNamesAcrossProviders(providers)), "Combined voice picker list should include voices from all configured providers.");
    AssertEqual("AniStar,Dream Cast", string.Join(",", catalog.GetSupportedVoiceNamesAcrossProviders(1, providers)), "Combined voice list should include voices from all providers for the episode.");
    AssertEqual(YummyVideoProviderKind.Cvh, catalog.PickPreferredProvider(1, explicitVoiceName: "AniStar", providers: providers) ?? YummyVideoProviderKind.Unknown, "AniStar should resolve to CVH when Alloha does not have that voice.");
    AssertEqual(YummyVideoProviderKind.Alloha, catalog.PickPreferredProvider(2, providers: providers) ?? YummyVideoProviderKind.Unknown, "When both providers have the episode, Alloha should keep higher priority.");
    AssertEqual(YummyVideoProviderKind.Cvh, catalog.PickPreferredProvider(4, providers: providers) ?? YummyVideoProviderKind.Unknown, "Episodes missing in Alloha should fall back to CVH.");
}

static void YummyVideoCatalog_MatchesEquivalentVoiceNames()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 21008,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1",
                IframeUrl = "https://play.example/player?anime_id=21008&episode=1&dubbing_code=158&dubbing=AniLibria",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "AniLibria"
                }
            },
            new()
            {
                Number = "1",
                IframeUrl = "https://play.example/player?anime_id=21008&episode=1&dubbing_code=377&dubbing=AniLeague",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "AniLeague"
                }
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime);
    var entry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, 1, "AniLibria.TV");
    var compactTvAliasEntry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, 1, "AnilibriaTV");
    var aliasEntry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, 1, "AniLiberty (AniLibria)");
    var shortAliasEntry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, 1, "AniLiberty");
    var compactAliasEntry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, 1, "Anilib");
    var directAliasEntry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, 1, "AniLibria");
    var dottedTvSuffixEntry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, 1, "AniLeague.TV");

    AssertTrue(entry is not null, "Equivalent voice names should resolve to the same Yummy-backed entry.");
    AssertTrue(compactTvAliasEntry is not null, "Compact provider codes like AnilibriaTV should resolve to the AniLibria voice.");
    AssertTrue(aliasEntry is not null, "Parenthetical provider aliases should resolve to the same Yummy-backed entry.");
    AssertTrue(shortAliasEntry is not null, "Short AniLiberty aliases should resolve to the same Yummy-backed entry.");
    AssertTrue(compactAliasEntry is not null, "Compact Anilib aliases should resolve to the same Yummy-backed entry.");
    AssertTrue(directAliasEntry is not null, "Direct canonical names should still resolve after provider-key normalization changes.");
    AssertTrue(dottedTvSuffixEntry is not null, "Provider names with dotted .TV suffixes should resolve to the transport-free Yummy voice.");
    AssertEqual("AniLibria", entry!.DisplayVoiceName, "Canonical Yummy voice should still be returned.");
    AssertEqual("AniLibria", compactTvAliasEntry!.DisplayVoiceName, "Compact CVH-style AniLibria codes should canonicalize to the AniLibria voice.");
    AssertEqual("AniLibria", aliasEntry!.DisplayVoiceName, "Alias-heavy provider names should still resolve to the canonical Yummy voice.");
    AssertEqual("AniLibria", shortAliasEntry!.DisplayVoiceName, "AniLiberty aliases should canonicalize to the AniLibria voice.");
    AssertEqual("AniLibria", compactAliasEntry!.DisplayVoiceName, "Short Alloha-style Anilib aliases should canonicalize to the AniLibria voice.");
    AssertEqual("AniLibria", directAliasEntry!.DisplayVoiceName, "Canonical names should continue resolving normally.");
    AssertEqual("AniLeague", dottedTvSuffixEntry!.DisplayVoiceName, "Trailing .TV transport suffixes should canonicalize to the same Yummy voice.");
}

static void TranslationNameKeyNormalizer_UsesCuratedVoiceAliasGroups()
{
    AssertEqual("2x2", TranslationNameKeyNormalizer.Normalize("2х2"), "Curated numeral aliases should normalize to the ASCII canonical key.");
    AssertEqual("anilibria", TranslationNameKeyNormalizer.Normalize("Anilib"), "Short Alloha-style AniLibria aliases should normalize to the canonical key.");
    AssertEqual("anilibria", TranslationNameKeyNormalizer.Normalize("AniLiberty"), "CVH-style AniLiberty aliases should normalize to the canonical key.");
    AssertEqual("anilibria", TranslationNameKeyNormalizer.Normalize("AniLiberty (AniLibria)"), "Parenthetical provider aliases should normalize to the canonical AniLibria key.");
    AssertEqual("anilibria", TranslationNameKeyNormalizer.Normalize("AnilibriaTV"), "Compact .TV provider variants should normalize to the AniLibria key.");
    AssertEqual("studioband", TranslationNameKeyNormalizer.Normalize("Студийная Банда"), "Cross-script StudioBand aliases should normalize to the canonical key.");
    AssertEqual("studioband", TranslationNameKeyNormalizer.Normalize("Studio Band"), "Spacing-only StudioBand variants should normalize to the canonical key.");
    AssertEqual("anileague", TranslationNameKeyNormalizer.Normalize("AniLeague.TV"), "Dotted .TV suffixes should normalize to the transport-free canonical key.");
    AssertEqual("shizaproject", TranslationNameKeyNormalizer.Normalize("SHIZA Project"), "Stable provider names without aliases should keep their direct canonical key.");
    AssertEqual(
        "AniLibria.TV",
        TranslationNameKeyNormalizer.FindEquivalent(
            "AniLibria",
            new[] { "AnimeVost", "AniLibria.TV" }),
        "A saved canonical voice should resolve to the actual catalog option id used for widget highlighting.");
    AssertEqual(
        "РуАниме / DEEP",
        TranslationNameKeyNormalizer.FindEquivalent(
            "РуАниме _ DEEP",
            new[] { "РуАниме / DEEP", "AniStar & DEEP" }),
        "Provider punctuation variants should resolve to the actual catalog option id.");
}

static void YummyVideoCatalog_MatchesCrossProviderVoiceAliases()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 21009,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1",
                IframeUrl = "https://play.example/player?anime_id=21009&episode=1&dubbing_code=StudioBand&dubbing=StudioBand",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "StudioBand"
                }
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime);
    var entry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, 1, "Студийная Банда");
    var spacedEntry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, 1, "Studio Band");

    AssertTrue(entry is not null, "Cross-provider aliases discovered from anchor titles should resolve to the same voice.");
    AssertTrue(spacedEntry is not null, "Provider formatting variants with spaces should resolve to the same voice.");
    AssertEqual("StudioBand", entry!.DisplayVoiceName, "Provider-native aliases should canonicalize to the StudioBand voice.");
    AssertEqual("StudioBand", spacedEntry!.DisplayVoiceName, "Provider formatting variants should preserve the canonical StudioBand voice.");
}

static void YummyVideoCatalog_FindPreferredEntryWithSkipsAcrossProviders_FallsBackToOtherProvider()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 21008,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1",
                IframeUrl = "https://alloha.yani.tv/?token_movie=movie&translation=215&season=1&episode=1&token=request",
                Data = new YummyVideoData
                {
                    PlayerId = 2,
                    Dubbing = "Озвучка Dream Cast"
                },
                Skips = null
            },
            new()
            {
                Number = "1",
                IframeUrl = "https://play.example/player?anime_id=21008&episode=1&dubbing_code=158&dubbing=Dream Cast",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "Dream Cast"
                },
                Skips = new YummyVideoSkips
                {
                    Opening = new YummySkipSegment
                    {
                        Time = 90,
                        Length = 90
                    }
                }
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime);
    var entry = catalog.FindPreferredEntryWithSkipsAcrossProviders(
        1,
        "Dream Cast",
        new[] { YummyVideoProviderKind.Alloha, YummyVideoProviderKind.Cvh });

    AssertTrue(entry is not null, "Cross-provider skip lookup should find a usable entry.");
    AssertEqual(YummyVideoProviderKind.Cvh, entry!.Provider, "Cross-provider skip lookup should fall back to CVH when the requested Alloha entry has no skips.");
    AssertEqual(90, entry.Skips?.Opening?.Time ?? 0, "Cross-provider skip lookup should keep the fallback skip timings.");
}

static void YummyVideoCatalog_FindPreferredEntryWithSkipsAcrossProviders_PrefersRequestedProvider()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 21008,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1",
                IframeUrl = "https://alloha.yani.tv/?token_movie=movie&translation=215&season=1&episode=1&token=request",
                Data = new YummyVideoData
                {
                    PlayerId = 2,
                    Dubbing = "Озвучка Dream Cast"
                },
                Skips = new YummyVideoSkips
                {
                    Opening = new YummySkipSegment
                    {
                        Time = 12,
                        Length = 88
                    }
                }
            },
            new()
            {
                Number = "1",
                IframeUrl = "https://play.example/player?anime_id=21008&episode=1&dubbing_code=158&dubbing=Dream Cast",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "Dream Cast"
                },
                Skips = new YummyVideoSkips
                {
                    Opening = new YummySkipSegment
                    {
                        Time = 90,
                        Length = 90
                    }
                }
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime);
    var entry = catalog.FindPreferredEntryWithSkipsAcrossProviders(
        1,
        "Dream Cast",
        new[] { YummyVideoProviderKind.Alloha, YummyVideoProviderKind.Cvh });

    AssertTrue(entry is not null, "Cross-provider skip lookup should return the requested provider when it already has skips.");
    AssertEqual(YummyVideoProviderKind.Alloha, entry!.Provider, "Cross-provider skip lookup should preserve the requested provider priority when it has usable skips.");
    AssertEqual(12, entry.Skips?.Opening?.Time ?? 0, "Cross-provider skip lookup should keep the requested provider's own skip timings.");
}

static void YummyKodikMediaSourceProvider_RuntimePublicationPolicyRepairsMissingOrShortValues()
{
    var policyType = typeof(YummyKodikStreamUri).Assembly.GetType("YummyKodik.Media.MediaRunTimePolicy");
    AssertTrue(policyType is not null, "Media-source runtime publication policy should exist.");
    var method = policyType!.GetMethod(
        "ShouldPublish",
        BindingFlags.Static | BindingFlags.Public);
    AssertTrue(method is not null, "Media-source runtime publication policy should exist.");

    bool ShouldPublish(long? current, long? resolved)
    {
        return (bool)method!.Invoke(null, new object?[] { current, resolved })!;
    }

    var episodeRunTime = TimeSpan.FromMinutes(24).Ticks;
    AssertTrue(ShouldPublish(null, episodeRunTime), "A resolved provider runtime should fill a missing Jellyfin item runtime.");
    AssertTrue(
        ShouldPublish(TimeSpan.FromSeconds(7).Ticks, episodeRunTime),
        "A resolved provider runtime should replace the implausibly short first-play runtime.");
    AssertFalse(
        ShouldPublish(TimeSpan.FromMinutes(23).Ticks, episodeRunTime),
        "An already plausible Jellyfin runtime should be preserved.");
    AssertFalse(ShouldPublish(null, null), "A missing provider runtime must not overwrite Jellyfin metadata.");
}

static void YummyKodikMediaSourceProvider_AuthoritativeRuntimeCorrectsPlausibleMismatch()
{
    var staleNfoRuntime = TimeSpan.FromSeconds(1299).Ticks;
    var resolvedHlsRuntime = TimeSpan.FromMilliseconds(1256050).Ticks;

    AssertTrue(
        MediaRunTimePolicy.ShouldPublishAuthoritative(staleNfoRuntime, resolvedHlsRuntime),
        "An exact provider runtime should correct a plausible but materially wrong NFO runtime.");
    AssertFalse(
        MediaRunTimePolicy.ShouldPublishAuthoritative(
            resolvedHlsRuntime + TimeSpan.FromSeconds(1).Ticks,
            resolvedHlsRuntime),
        "Sub-second and rounding-level runtime differences should not rewrite Jellyfin metadata.");
    AssertFalse(
        MediaRunTimePolicy.ShouldPublishAuthoritative(staleNfoRuntime, null),
        "A missing provider runtime must not overwrite a plausible Jellyfin runtime.");
}

static void YummyKodikMediaSourceProvider_ResolvesPrimaryOnlyForExplicitSeriesSelection()
{
    var configuration = new PluginConfiguration();
    configuration.SetUserSeriesPreferredTranslationId(
        Guid.Empty,
        "yummy:123",
        "СВ-Дубль");

    AssertTrue(
        MediaRunTimePolicy.HasExplicitSeriesSelection(
            configuration,
            new[] { "cvh:123", "yummy:123" }),
        "A mirrored widget selection should activate merged-primary source resolution.");
    AssertFalse(
        MediaRunTimePolicy.HasExplicitSeriesSelection(
            configuration,
            new[] { "yummy:456", "shikimori:456" }),
        "A selection for another series must not affect the requested episode.");

    configuration.SetUserSeriesPreferredTranslationId(Guid.Empty, "yummy:123", null);
    AssertFalse(
        MediaRunTimePolicy.HasExplicitSeriesSelection(
            configuration,
            new[] { "yummy:123" }),
        "Auto should disable forced primary source resolution and retain native version selection.");
}

static void YummyKodikMediaSourceProvider_FillsMissingSourceRuntimeFromSiblingOrItem()
{
    var resolvedTicks = TimeSpan.FromMinutes(24).Ticks;
    var sources = new[]
    {
        new MediaSourceInfo { Name = "Missing", RunTimeTicks = null },
        new MediaSourceInfo { Name = "Resolved", RunTimeTicks = resolvedTicks },
        new MediaSourceInfo { Name = "Short", RunTimeTicks = TimeSpan.FromSeconds(7).Ticks }
    };

    var fallback = MediaRunTimePolicy.FillMissingSourceRunTimes(
        itemRunTimeTicks: TimeSpan.FromMinutes(23).Ticks,
        sources);

    AssertEqual(resolvedTicks, fallback!.Value, "A provider duration should be preferred as the source fallback.");
    AssertTrue(
        sources.All(source => source.RunTimeTicks == resolvedTicks),
        "Missing and implausibly short media-source runtimes should inherit a known sibling duration.");

    var itemOnlySource = new MediaSourceInfo { Name = "Item fallback" };
    var itemTicks = TimeSpan.FromMinutes(25).Ticks;
    MediaRunTimePolicy.FillMissingSourceRunTimes(
        itemTicks,
        new[] { itemOnlySource });
    AssertEqual(
        itemTicks,
        itemOnlySource.RunTimeTicks!.Value,
        "A dynamic media source should inherit a plausible Jellyfin item runtime when provider metadata is missing.");
}

static void YummyKodikMediaSourceProvider_SharesRuntimeOnlyWithinEpisodeVersionSet()
{
    const string presentationKey = "episode-presentation-key";
    var seasonDir = Path.Combine("D:\\video\\YummyKodik", "Series", "Season 01");

    AssertTrue(
        MediaRunTimePolicy.IsSiblingEpisodeVersion(
            presentationKey,
            seasonDir,
            presentationKey,
            Path.Combine(seasonDir, "S01E03 - Voice.strm")),
        "A STRM in the same season directory with the same presentation key should receive the runtime.");
    AssertFalse(
        MediaRunTimePolicy.IsSiblingEpisodeVersion(
            presentationKey,
            seasonDir,
            presentationKey,
            Path.Combine("D:\\video\\OtherLibrary", "Season 01", "S01E03.strm")),
        "Runtime propagation must not cross library or season directories.");
    AssertFalse(
        MediaRunTimePolicy.IsSiblingEpisodeVersion(
            presentationKey,
            seasonDir,
            "different-episode",
            Path.Combine(seasonDir, "S01E04.strm")),
        "Runtime propagation must not leak into a different episode presentation key.");
}

static void YummyKodikMediaSourceFactory_CarriesRuntimeWithoutReflection()
{
    var assembly = typeof(YummyKodikStreamUri).Assembly;
    var optionsType = assembly.GetType("YummyKodik.Media.MediaSourceBuildOptions");
    var factoryType = assembly.GetType("YummyKodik.Media.YummyKodikMediaSourceFactory");
    AssertTrue(optionsType is not null, "Media-source build options should exist.");
    AssertTrue(factoryType is not null, "Media-source factory should exist.");

    var options = Activator.CreateInstance(optionsType!, nonPublic: true);
    AssertTrue(options is not null, "Media-source build options should be constructible.");
    optionsType!.GetProperty("ItemId")!.SetValue(options, "item-1");
    optionsType.GetProperty("Episode")!.SetValue(options, 7);
    optionsType.GetProperty("Suffix")!.SetValue(options, "kodik-auto");
    optionsType.GetProperty("Name")!.SetValue(options, "Auto");
    optionsType.GetProperty("Url")!.SetValue(options, "http://127.0.0.1:8096/YummyKodik/stream?ep=7&format=hls");
    optionsType.GetProperty("Container")!.SetValue(options, "m3u8");
    optionsType.GetProperty("SupportsDirectPlay")!.SetValue(options, false);
    optionsType.GetProperty("RunTimeTicks")!.SetValue(options, TimeSpan.FromMinutes(24).Ticks);

    var build = factoryType!.GetMethod("Build", BindingFlags.Static | BindingFlags.Public);
    AssertTrue(build is not null, "Media-source factory Build method should exist.");
    var source = (MediaSourceInfo)build!.Invoke(null, new[] { options })!;

    AssertEqual(VideoType.VideoFile, source.VideoType!.Value, "Dynamic YummyKodik media sources should be classified as regular video.");
    AssertEqual(TimeSpan.FromMinutes(24).Ticks, source.RunTimeTicks!.Value, "Dynamic media source should carry the resolved total episode duration.");
    AssertEqual("m3u8", source.Container, "Dynamic media source should expose the resolved HLS container.");
    AssertFalse(source.SupportsDirectPlay, "Gateway HLS source should not be mistaken for the original static STRM source.");
}

static void YummyKodikMediaSegmentProvider_ClonesCachedSegmentsPerItem()
{
    var templateItemId = Guid.NewGuid();
    var itemA = Guid.NewGuid();
    var itemB = Guid.NewGuid();
    var templateId = Guid.NewGuid();
    IReadOnlyList<MediaSegmentDto> template = new[]
    {
        new MediaSegmentDto
        {
            Id = templateId,
            ItemId = templateItemId,
            Type = MediaSegmentType.Intro,
            StartTicks = TimeSpan.FromSeconds(12).Ticks,
            EndTicks = TimeSpan.FromSeconds(88).Ticks
        }
    };

    var cloneA = YummyKodikMediaSegmentCache.CloneSegmentsForItem(template, itemA).Single();
    var cloneB = YummyKodikMediaSegmentCache.CloneSegmentsForItem(template, itemB).Single();

    AssertEqual(itemA, cloneA.ItemId, "Cached segment clones must target the requested item.");
    AssertEqual(itemB, cloneB.ItemId, "Cached segment clones must target each requested item independently.");
    AssertFalse(cloneA.Id == templateId, "Cached segment clone must not reuse the template segment id.");
    AssertFalse(cloneB.Id == templateId, "Cached segment clone must not reuse the template segment id.");
    AssertFalse(cloneA.Id == cloneB.Id, "Cached segment clones for different items must not collide.");
    AssertEqual(template[0].Type, cloneA.Type, "Cached segment clone should preserve segment type.");
    AssertEqual(template[0].StartTicks, cloneA.StartTicks, "Cached segment clone should preserve start ticks.");
    AssertEqual(template[0].EndTicks, cloneA.EndTicks, "Cached segment clone should preserve end ticks.");
}

static void GenerateKodikEpisodeFilesAsync_FillsMissingTranslationsForExistingEpisode()
{
    var expectedEpisodeTranslationKeys = new Dictionary<int, HashSet<string>>();

    EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, "Dream Cast");
    EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, "AnimeVost");

    var missing = TestData.KodikSupplementTranslationNames
        .Where(x => !EpisodeArtifactMaintenance.HasExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, x))
        .ToArray();

    AssertEqual(1, missing.Length, "Only translations absent from Yummy/CVH coverage should remain for Kodik supplementation.");
    AssertEqual("AniLibria", missing[0], "Kodik supplement should only add the missing translation.");
}

static void ResolveEpisodesNeedingKodikTranslation_SkipsYummyCoveredPairs()
{
    var expectedEpisodeTranslationKeys = new Dictionary<int, HashSet<string>>();
    EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, "AniLibria.TV");
    EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 3, "AniLibria");
    var translation = new KodikTranslation
    {
        Id = "610",
        Type = "voice",
        Name = "AniLibria",
        MaxEpisode = 3,
        AvailableEpisodes = new[] { 1, 2, 3 }
    };

    var episodes = KodikSupplementService.ResolveEpisodesNeedingKodikTranslation(
        translation,
        new[] { 1, 2, 3 },
        expectedEpisodeTranslationKeys);

    AssertEqual("2", string.Join(",", episodes), "Kodik should deep-check only episode/voice pairs not already supplied by an equivalent Yummy provider voice.");
}

static void EpisodeArtifactMaintenance_NormalizesEquivalentTranslationVariants()
{
    var expectedEpisodeTranslationKeys = new Dictionary<int, HashSet<string>>();
    EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, "AniLibria");

    AssertTrue(
        EpisodeArtifactMaintenance.HasExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, "AniLibria.TV"),
        "Equivalent translation suffixes should collapse to the same key so Yummy can replace stale Kodik files.");
    AssertTrue(
        EpisodeArtifactMaintenance.HasExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, "AniLiberty (AniLibria)"),
        "Parenthetical provider aliases should collapse to the same key so equivalent voices keep stable files.");
}

static void ResolveEpisodeTranslationFileBaseName_ReusesExistingEquivalentArtifactName()
{
    var aliases = new Dictionary<int, Dictionary<string, string>>
    {
        [1] = new(StringComparer.OrdinalIgnoreCase)
        {
            [EpisodeArtifactMaintenance.NormalizeEpisodeTranslationKey("AniLibria.TV")] = "S01E01 - AniLibria.TV"
        }
    };

    var fileBaseName = EpisodeArtifactMaintenance.ResolveEpisodeTranslationFileBaseName(aliases, 1, "S01E01", "AniLibria");

    AssertEqual("S01E01 - AniLibria.TV", fileBaseName, "Refresh should reuse the existing equivalent file name to keep Jellyfin item ids stable.");
}

static void ResolveProviderCoverage_UsesYummyHintWhenKodikSeriesCountIsZero()
{
    var coverage = YummyEpisodeAvailability.ResolveProviderCoverage(0, 1);
    AssertEqual(1, coverage.KodikAvailableEpisodes, "When Kodik search returns zero seriesCount but Yummy knows episode 1 exists, refresh should still generate files.");
    AssertEqual(1, coverage.OverallAvailableEpisodes, "The overall cleanup ceiling should preserve Yummy's known episode.");
}

static void ResolveProviderCoverage_PreservesYummyCoverageBeyondKodik()
{
    var coverage = YummyEpisodeAvailability.ResolveProviderCoverage(8, 16);

    AssertEqual(8, coverage.KodikAvailableEpisodes, "Kodik link processing should stay bounded by Kodik's eight reported episodes.");
    AssertEqual(16, coverage.OverallAvailableEpisodes, "Whole-season cleanup must preserve the wider sixteen-episode Yummy/CVH coverage.");
}

static void KeepLatestEpisodePerResolvedLink_DropsEarlierEpisodesWhenKodikReusesSameVideo()
{
    var candidateEpisodes = new[] { 1, 2 };
    var resolvedBasePaths = new Dictionary<int, string>
    {
        [1] = "//cloud.solodcdn.com/useruploads/shared/",
        [2] = "//cloud.solodcdn.com/useruploads/shared/"
    };

    var result = KodikEpisodeLinkDeduper.KeepLatestEpisodePerResolvedLink(candidateEpisodes, resolvedBasePaths);
    AssertEqual("2", string.Join(",", result.OrderBy(x => x)), "When Kodik resolves multiple episodes to the same video, only the latest episode should remain.");
}

static void CleanupUnexpectedEpisodeArtifacts_RemovesStaleFilesBeyondExpectedCoverage()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    var seasonDir = Path.Combine(tempRoot, "Season 02");
    Directory.CreateDirectory(seasonDir);

    try
    {
        File.WriteAllText(Path.Combine(seasonDir, "S02E01 - Dream Cast.strm"), "keep");
        File.WriteAllText(Path.Combine(seasonDir, "S02E01 - Dream Cast.nfo"), "<ok />");
        File.WriteAllText(Path.Combine(seasonDir, "S02E01 - Old Voice.strm"), "delete");
        File.WriteAllText(Path.Combine(seasonDir, "S02E01 - Old Voice.nfo"), "<delete />");
        File.WriteAllText(Path.Combine(seasonDir, "S02E02 - Old Voice.strm"), "delete");
        File.WriteAllText(Path.Combine(seasonDir, "S02E02 - Old Voice.nfo"), "<delete />");

        var expectedEpisodeFileBaseNames = new Dictionary<int, HashSet<string>>
        {
            [1] = new(StringComparer.OrdinalIgnoreCase)
            {
                "S02E01 - Dream Cast"
            }
        };

        EpisodeArtifactMaintenance.CleanupUnexpectedEpisodeArtifacts(
            NullLogger.Instance,
            seasonDir,
            2,
            expectedEpisodeFileBaseNames,
            1);

        AssertTrue(File.Exists(Path.Combine(seasonDir, "S02E01 - Dream Cast.strm")), "Expected translation STRM should remain after cleanup.");
        AssertTrue(File.Exists(Path.Combine(seasonDir, "S02E01 - Dream Cast.nfo")), "Expected translation NFO should remain after cleanup.");
        AssertFalse(File.Exists(Path.Combine(seasonDir, "S02E01 - Old Voice.strm")), "Unexpected translation STRM should be removed.");
        AssertFalse(File.Exists(Path.Combine(seasonDir, "S02E01 - Old Voice.nfo")), "Unexpected translation NFO should be removed.");
        AssertFalse(File.Exists(Path.Combine(seasonDir, "S02E02 - Old Voice.strm")), "Episodes beyond the currently available range should be removed.");
        AssertFalse(File.Exists(Path.Combine(seasonDir, "S02E02 - Old Voice.nfo")), "NFO for unavailable future episodes should be removed.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void RefreshFileWriter_ReplacesReadOnlyExistingArtifact()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    var seasonDir = Path.Combine(tempRoot, "Season 01");
    var strmPath = Path.Combine(seasonDir, "S01E01.strm");

    try
    {
        Directory.CreateDirectory(seasonDir);
        File.WriteAllText(strmPath, "old" + Environment.NewLine);
        File.SetAttributes(strmPath, File.GetAttributes(strmPath) | FileAttributes.ReadOnly);

        InvokeRefreshFileWriterWriteTextAtomically(strmPath, "new" + Environment.NewLine);

        AssertEqual("new" + Environment.NewLine, File.ReadAllText(strmPath), "Read-only generated artifacts should still be atomically replaced.");
        AssertTrue(
            (File.GetAttributes(strmPath) & FileAttributes.ReadOnly) != 0,
            "Replacing a read-only artifact should restore the original read-only attribute.");
        AssertFalse(
            Directory.EnumerateFiles(seasonDir, "*.tmp.*", SearchOption.TopDirectoryOnly).Any(),
            "Atomic replacement should not leave temp files behind.");
    }
    finally
    {
        if (File.Exists(strmPath))
        {
            File.SetAttributes(strmPath, FileAttributes.Normal);
        }

        TryDeleteDirectory(tempRoot);
    }
}

static void RefreshState_AllowsSingleFileSkipWhenFingerprintAndFilesMatch()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

    try
    {
        var fixture = CreateRefreshStateFixture(tempRoot);
        var written = RefreshStateManager.WriteSeasonStateAsync(
                fixture.SeriesRoot,
                fixture.Input,
                fixture.ExpectedEpisodeFileBaseNames,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertTrue(written, "Complete single-file artifacts should produce refresh state.");

        var canSkip = RefreshStateManager.CanSkipSingleFileRefreshAsync(
                fixture.SeriesRoot,
                fixture.Input,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertTrue(canSkip, "Matching fingerprint and complete managed files should allow the single-file pre-Kodik skip.");

        var stateJson = File.ReadAllText(Path.Combine(fixture.SeriesRoot, RefreshStateManager.StateFileName));
        AssertFalse(stateJson.Contains("secret-request", StringComparison.Ordinal), "Refresh state must not store raw secret-bearing STRM URLs.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void RefreshState_InvalidatesSkipWhenInputsChange()
{
    var baseline = RefreshStateManager.BuildFingerprint(BuildRefreshStateFingerprintInput());
    AssertTrue(
        typeof(RefreshStateFingerprintInput).GetProperty("ServerBaseUrl") is null,
        "Refresh-state fingerprint inputs must not retain the client-facing ServerBaseUrl.");

    var sameGatewayWithTrailingSlash = RefreshStateManager.BuildFingerprint(
        BuildRefreshStateFingerprintInput(streamGatewayBaseUrl: "http://127.0.0.1:8096/"));
    AssertEqual(
        baseline,
        sameGatewayWithTrailingSlash,
        "Fingerprint should be independent of public server topology and normalize the internal gateway trailing slash.");

    var cases = new (string Name, RefreshStateFingerprintInput Input)[]
    {
        ("StreamGatewayBaseUrl", BuildRefreshStateFingerprintInput(streamGatewayBaseUrl: "http://127.0.0.1:18096")),
        ("PreferredTranslationFilter", BuildRefreshStateFingerprintInput(preferredTranslationFilter: "dreamcast")),
        ("PreferredQuality", BuildRefreshStateFingerprintInput(preferredQuality: 720)),
        ("Mode", BuildRefreshStateFingerprintInput(mode: "per-voice")),
        ("ProviderCoverage", BuildRefreshStateFingerprintInput(providerCoverage: new[] { "ep:1:preferred:Cvh" })),
        ("SeasonTitleIdentity", BuildRefreshStateFingerprintInput(seriesTitle: "Frieren Season Two", seasonNumber: 2)),
        ("ExpectedEpisodeCount", BuildRefreshStateFingerprintInput(expectedAvailableEpisodes: 2))
    };

    foreach (var testCase in cases)
    {
        var fingerprint = RefreshStateManager.BuildFingerprint(testCase.Input);
        AssertFalse(
            string.Equals(baseline, fingerprint, StringComparison.Ordinal),
            $"Fingerprint should change when {testCase.Name} changes.");
    }

    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    try
    {
        var fixture = CreateRefreshStateFixture(tempRoot, fingerprint: baseline);
        RefreshStateManager.WriteSeasonStateAsync(
                fixture.SeriesRoot,
                fixture.Input,
                fixture.ExpectedEpisodeFileBaseNames,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var changedFingerprintInput = BuildRefreshStateSeasonInput(fingerprint: cases[0].Input is { } changedInput
            ? RefreshStateManager.BuildFingerprint(changedInput)
            : string.Empty);
        var changedFingerprintCanSkip = RefreshStateManager.CanSkipSingleFileRefreshAsync(
                fixture.SeriesRoot,
                changedFingerprintInput,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertFalse(changedFingerprintCanSkip, "Changed fingerprint should disable state pre-skip.");

        var changedExpectedCountInput = BuildRefreshStateSeasonInput(fingerprint: baseline, expectedAvailableEpisodes: 2);
        var changedExpectedCountCanSkip = RefreshStateManager.CanSkipSingleFileRefreshAsync(
                fixture.SeriesRoot,
                changedExpectedCountInput,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertFalse(changedExpectedCountCanSkip, "Changed expected episode count should disable state pre-skip.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void RefreshState_ExtraEpisodeArtifactsDisableSkip()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

    try
    {
        var fixture = CreateRefreshStateFixture(tempRoot);
        RefreshStateManager.WriteSeasonStateAsync(
                fixture.SeriesRoot,
                fixture.Input,
                fixture.ExpectedEpisodeFileBaseNames,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var seasonDir = Path.Combine(fixture.SeriesRoot, fixture.Input.SeasonKey);
        File.WriteAllText(Path.Combine(seasonDir, "S01E02.strm"), "stale");
        File.WriteAllText(Path.Combine(seasonDir, "S01E02.nfo"), "<episodedetails />");

        var canSkip = RefreshStateManager.CanSkipSingleFileRefreshAsync(
                fixture.SeriesRoot,
                fixture.Input,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertFalse(canSkip, "Extra episode-shaped artifacts should disable state pre-skip so full cleanup still runs.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void RefreshState_ZeroExpectedEpisodesWithStaleArtifactsDisablesSkip()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

    try
    {
        var fingerprint = RefreshStateManager.BuildFingerprint(BuildRefreshStateFingerprintInput(
            expectedAvailableEpisodes: 0,
            providerCoverage: Array.Empty<string>()));
        var fixture = CreateRefreshStateFixture(
            tempRoot,
            expectedAvailableEpisodes: 0,
            fingerprint: fingerprint);

        RefreshStateManager.WriteSeasonStateAsync(
                fixture.SeriesRoot,
                fixture.Input,
                fixture.ExpectedEpisodeFileBaseNames,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var seasonDir = Path.Combine(fixture.SeriesRoot, fixture.Input.SeasonKey);
        var staleStrmPath = Path.Combine(seasonDir, "S01E01.strm");
        var staleNfoPath = Path.Combine(seasonDir, "S01E01.nfo");
        File.WriteAllText(staleStrmPath, "stale");
        File.WriteAllText(staleNfoPath, "<episodedetails />");

        var canSkip = RefreshStateManager.CanSkipSingleFileRefreshAsync(
                fixture.SeriesRoot,
                fixture.Input,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertFalse(canSkip, "Zero-episode state must not skip when stale episode-shaped artifacts are present.");

        EpisodeArtifactMaintenance.CleanupUnexpectedEpisodeArtifacts(
            NullLogger.Instance,
            seasonDir,
            fixture.Input.SeasonNumber,
            fixture.ExpectedEpisodeFileBaseNames,
            maxAvailableEpisodeNumber: 0);

        AssertFalse(File.Exists(staleStrmPath), "Zero-episode cleanup should remove stale STRM artifacts.");
        AssertFalse(File.Exists(staleNfoPath), "Zero-episode cleanup should remove stale NFO artifacts.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void RefreshState_PreservesMultipleSeasonsInSeriesRoot()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

    try
    {
        var seasonOne = CreateRefreshStateFixture(tempRoot, seasonNumber: 1);
        var seasonTwo = CreateRefreshStateFixture(
            tempRoot,
            seasonNumber: 2,
            fingerprint: RefreshStateManager.BuildFingerprint(BuildRefreshStateFingerprintInput(seasonNumber: 2)));

        RefreshStateManager.WriteSeasonStateAsync(
                seasonOne.SeriesRoot,
                seasonOne.Input,
                seasonOne.ExpectedEpisodeFileBaseNames,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        RefreshStateManager.WriteSeasonStateAsync(
                seasonTwo.SeriesRoot,
                seasonTwo.Input,
                seasonTwo.ExpectedEpisodeFileBaseNames,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(seasonOne.SeriesRoot, RefreshStateManager.StateFileName)));
        var seasons = document.RootElement.GetProperty("seasons");
        AssertTrue(seasons.TryGetProperty("Season 01", out _), "State file should keep the first season entry.");
        AssertTrue(seasons.TryGetProperty("Season 02", out _), "State file should add the second season entry without overwriting season one.");

        AssertTrue(
            RefreshStateManager.CanSkipSingleFileRefreshAsync(seasonOne.SeriesRoot, seasonOne.Input, CancellationToken.None).GetAwaiter().GetResult(),
            "Season one state should remain usable after writing season two.");
        AssertTrue(
            RefreshStateManager.CanSkipSingleFileRefreshAsync(seasonTwo.SeriesRoot, seasonTwo.Input, CancellationToken.None).GetAwaiter().GetResult(),
            "Season two state should be usable from the shared state file.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void RefreshState_LegacyGenerationContractDisablesSkip()
{
    AssertTrue(
        RefreshStateManager.GenerationContractVersion > 1,
        "Gateway STRM generation must bump the refresh-state contract version.");

    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    try
    {
        var fixture = CreateRefreshStateFixture(tempRoot);
        RefreshStateManager.WriteSeasonStateAsync(
                fixture.SeriesRoot,
                fixture.Input,
                fixture.ExpectedEpisodeFileBaseNames,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var statePath = Path.Combine(fixture.SeriesRoot, RefreshStateManager.StateFileName);
        var currentVersion = RefreshStateManager.GenerationContractVersion;
        var currentJson = File.ReadAllText(statePath);
        var legacyJson = currentJson.Replace(
            $"\"generationContractVersion\": {currentVersion}",
            $"\"generationContractVersion\": {currentVersion - 1}",
            StringComparison.Ordinal);
        AssertFalse(
            string.Equals(currentJson, legacyJson, StringComparison.Ordinal),
            "Test fixture should contain the generated contract version before simulating a legacy state file.");
        File.WriteAllText(statePath, legacyJson);

        var canSkip = RefreshStateManager.CanSkipSingleFileRefreshAsync(
                fixture.SeriesRoot,
                fixture.Input,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertFalse(canSkip, "A v1 refresh-state file must not skip refresh after the gateway STRM contract changes.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void RefreshState_WritesAndReadsMediaSegmentsForEpisodeFile()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

    try
    {
        var fixture = CreateRefreshStateFixture(tempRoot, createStrmPerVoiceTranslation: true);
        var seasonDir = Path.Combine(fixture.SeriesRoot, fixture.Input.SeasonKey);
        var voiceFileBaseName = "S01E01 - AniLibria";
        File.WriteAllText(Path.Combine(seasonDir, voiceFileBaseName + ".strm"), "https://jellyfin.test/YummyKodik/cvh/1/1");
        File.WriteAllText(
            Path.Combine(seasonDir, voiceFileBaseName + ".nfo"),
            NfoBuilder.BuildEpisodeNfo(1, 1, "Frieren", "Plot"));
        fixture.ExpectedEpisodeFileBaseNames[1].Add(voiceFileBaseName);

        var mediaSegments = new Dictionary<string, RefreshStateMediaSegmentEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [voiceFileBaseName] = new RefreshStateMediaSegmentEntry
            {
                FileBaseName = voiceFileBaseName,
                EpisodeNumber = 1,
                Provider = "Cvh",
                VoiceName = "AniLibria",
                SourceProvider = "Cvh",
                SourceVoiceName = "Komnata Didi",
                Segments = new[]
                {
                    new RefreshStateMediaSegment
                    {
                        Type = "Intro",
                        StartTicks = TimeSpan.FromSeconds(44).Ticks,
                        EndTicks = TimeSpan.FromSeconds(133).Ticks
                    },
                    new RefreshStateMediaSegment
                    {
                        Type = "Outro",
                        StartTicks = TimeSpan.FromSeconds(1482).Ticks,
                        EndTicks = TimeSpan.FromSeconds(1496).Ticks
                    }
                }
            }
        };

        RefreshStateManager.WriteSeasonStateAsync(
                fixture.SeriesRoot,
                fixture.Input,
                fixture.ExpectedEpisodeFileBaseNames,
                mediaSegments,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var entry = RefreshStateManager.TryReadMediaSegmentEntryForPathAsync(
                Path.Combine(seasonDir, voiceFileBaseName + ".strm"),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        AssertTrue(entry != null, "Media segment state should be readable by episode file path.");
        AssertEqual("AniLibria", entry!.VoiceName, "Requested voice should be stored with local media segments.");
        AssertEqual("Komnata Didi", entry.SourceVoiceName, "Source voice should record where fallback timings came from.");
        AssertEqual(2, entry.Segments.Length, "Intro and outro segments should be stored.");
        AssertEqual(TimeSpan.FromSeconds(44).Ticks, entry.Segments[0].StartTicks, "Intro start ticks should round-trip.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void ExistingLibraryFallbackRefreshInfoLoader_LoadsSnapshotFromRefreshState()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    var seriesRoot = Path.Combine(tempRoot, "Frieren [shikimori-52991]");
    var seasonDir = Path.Combine(seriesRoot, "Season 01");

    try
    {
        Directory.CreateDirectory(seasonDir);
        File.WriteAllText(Path.Combine(seriesRoot, "tvshow.nfo"), NfoBuilder.BuildSeriesNfo("Frieren", "Existing plot"));
        File.WriteAllText(
            Path.Combine(seasonDir, "S01E01 - Dream Cast.strm"),
            "https://jellyfin.test/YummyKodik/stream?provider=cvh&animeId=21008&ep=1&voice=Dream%20Cast&format=hls" + Environment.NewLine);
        File.WriteAllText(
            Path.Combine(seasonDir, "S01E01 - Dream Cast.nfo"),
            NfoBuilder.BuildEpisodeNfo(1, 1, "Frieren", "Existing plot"));

        var input = BuildRefreshStateSeasonInput(fingerprint: "provider-only-fallback-test");
        var expectedEpisodeFileBaseNames = new Dictionary<int, HashSet<string>>
        {
            [1] = new(StringComparer.OrdinalIgnoreCase)
            {
                "S01E01 - Dream Cast"
            }
        };

        RefreshStateManager.WriteSeasonStateAsync(
                seriesRoot,
                input,
                expectedEpisodeFileBaseNames,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var keys = ExistingLibraryFallbackRefreshInfoLoader.FindExistingCleanKeys(tempRoot);
        AssertTrue(keys.Contains("frieren", StringComparer.OrdinalIgnoreCase), "Existing refresh state should provide fallback title keys when the Yummy user list is unavailable.");

        var refresh = new ExistingLibraryFallbackRefreshInfoLoader()
            .TryLoad(NullLogger.Instance, "frieren", tempRoot, "http://127.0.0.1:8096");

        AssertTrue(refresh is not null, "Existing local library snapshot should be loadable by clean key.");
        AssertEqual("Frieren", refresh!.TitleInfo.Title, "Fallback title should come from tvshow.nfo.");
        AssertEqual(1, refresh.TitleInfo.SeasonNumber, "Fallback season should come from refresh state.");
        AssertEqual(52991L, refresh.TitleInfo.Anime.RemoteIds?.ShikimoriId ?? 0, "Fallback remote id should come from the provider tag.");
        AssertEqual(21008L, refresh.TitleInfo.Anime.AnimeId, "Fallback Yummy anime id should be recovered from existing provider STRM urls.");
        AssertEqual(seasonDir, refresh.Files.SeasonDir, "Fallback season dir should point at the existing generated season.");
        AssertEqual(
            "http://127.0.0.1:8096",
            refresh.Files.BaseUrl,
            "Fallback refresh should generate future STRM files through Jellyfin's internal gateway.");

        var state = ExistingLibraryFallbackRefreshInfoLoader.BuildExistingEpisodeState(seasonDir, 1);
        AssertTrue(state.GeneratedEpisodeNumbers.Contains(1), "Existing STRM files should count as already generated episodes.");
        AssertTrue(
            EpisodeArtifactMaintenance.HasExpectedEpisodeTranslation(state.ExpectedEpisodeTranslationKeys, 1, "Dream Cast"),
            "Existing per-voice files should be treated as expected during provider-only fallback.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void StaleReleaseCleanup_DeletesStaleManagedDirectory()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    try
    {
        var staleSeries = Path.Combine(tempRoot, "Stale series");
        WriteManagedCleanupSeason(staleSeries, 1, "stale-release");

        var result = RunStaleReleaseCleanup(tempRoot, Array.Empty<string>());

        AssertFalse(Directory.Exists(staleSeries), "A managed series absent from the current keys should be deleted.");
        AssertEqual(1, result.DeletedDirectoryCount, "Exactly one stale managed series root should be deleted.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void StaleReleaseCleanup_RetainsCurrentAndManualKeys()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    try
    {
        var currentSeries = Path.Combine(tempRoot, "Current series");
        var manualSeries = Path.Combine(tempRoot, "Manual series");
        WriteManagedCleanupSeason(currentSeries, 1, "current-release");
        WriteManagedCleanupSeason(manualSeries, 1, "manual-release");

        var result = RunStaleReleaseCleanup(tempRoot, new[] { "CURRENT-RELEASE", "manual-release" });

        AssertTrue(Directory.Exists(currentSeries), "A current user-list key must retain its managed series directory.");
        AssertTrue(Directory.Exists(manualSeries), "A manual slug passed in the current key set must retain its managed series directory.");
        AssertEqual(2, result.RetainedDirectoryCount, "Both current and manual configured releases should be retained.");
        AssertEqual(0, result.DeletedDirectoryCount, "No current release should be deleted.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void StaleReleaseCleanup_SkipsMissingCorruptAndAmbiguousState()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    try
    {
        var missingState = Path.Combine(tempRoot, "Missing state");
        var corruptState = Path.Combine(tempRoot, "Corrupt state");
        var ambiguousState = Path.Combine(tempRoot, "Ambiguous state");
        Directory.CreateDirectory(missingState);
        Directory.CreateDirectory(corruptState);
        Directory.CreateDirectory(ambiguousState);
        File.WriteAllText(Path.Combine(corruptState, RefreshStateManager.StateFileName), "{ not-json");
        WriteCleanupState(ambiguousState, ("Season 01", string.Empty));

        var result = RunStaleReleaseCleanup(tempRoot, Array.Empty<string>());

        AssertTrue(Directory.Exists(missingState), "Directories without managed refresh state must not be deleted.");
        AssertTrue(Directory.Exists(corruptState), "Directories with corrupt refresh state must not be deleted.");
        AssertTrue(Directory.Exists(ambiguousState), "Directories with ambiguous refresh state must not be deleted.");
        AssertEqual(3, result.SkippedDirectoryCount, "Missing, corrupt, and ambiguous state should each take the safe skip path.");
        AssertEqual(0, result.DeletedDirectoryCount, "Unproven directories must never be deleted by stale cleanup.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void StaleReleaseCleanup_SkipsSeasonWithUnknownFile()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    try
    {
        var seriesRoot = Path.Combine(tempRoot, "Unknown file series");
        var seasonDir = WriteManagedCleanupSeason(seriesRoot, 1, "stale-release");
        File.WriteAllText(Path.Combine(seasonDir, "user-note.txt"), "do not delete");

        var result = RunStaleReleaseCleanup(tempRoot, Array.Empty<string>());

        AssertTrue(Directory.Exists(seriesRoot), "An untracked file in a stale season must block recursive deletion.");
        AssertTrue(Directory.Exists(seasonDir), "A season containing an untracked file must remain intact.");
        AssertEqual(0, result.DeletedDirectoryCount, "Cleanup must not delete a season whose manifest is no longer exact.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void StaleReleaseCleanup_SkipsSeasonWithModifiedManagedFile()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    try
    {
        var seriesRoot = Path.Combine(tempRoot, "Modified file series");
        var seasonDir = WriteManagedCleanupSeason(seriesRoot, 1, "stale-release");
        File.AppendAllText(Path.Combine(seasonDir, "S01E01.strm"), "modified");

        var result = RunStaleReleaseCleanup(tempRoot, Array.Empty<string>());

        AssertTrue(Directory.Exists(seriesRoot), "A stale root with modified managed artifacts must not be deleted.");
        AssertTrue(Directory.Exists(seasonDir), "A season with a managed-file hash mismatch must remain intact.");
        AssertEqual(0, result.DeletedDirectoryCount, "Cleanup must not delete a stale season when its managed file hash no longer matches state.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void StaleReleaseCleanup_CanonicalizesPlainAndUrlCurrentKeys()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    try
    {
        var seriesRoot = Path.Combine(tempRoot, "Canonical key series");
        WriteManagedCleanupSeason(seriesRoot, 1, "frieren");
        var statePath = Path.Combine(seriesRoot, RefreshStateManager.StateFileName);
        var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        var season = state["seasons"]!.AsObject()["Season 01"]!.AsObject();
        season["cleanKey"] = "https://yani.tv/anime/frieren";
        File.WriteAllText(statePath, state.ToJsonString());

        var result = RunStaleReleaseCleanup(tempRoot, new[] { "frieren" });

        AssertTrue(Directory.Exists(seriesRoot), "A plain current slug must retain a matching URL-form state cleanKey.");
        AssertEqual(1, result.RetainedDirectoryCount, "Shared key canonicalization should retain the matching managed root.");
        AssertEqual(0, result.DeletedDirectoryCount, "Equivalent URL and slug key forms must never trigger stale deletion.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void StaleReleaseCleanup_SkipsLegacyGenerationContract()
{
    AssertTrue(RefreshStateManager.GenerationContractVersion > 1, "Gateway cleanup must have a newer generation contract than legacy state files.");

    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    try
    {
        var seriesRoot = Path.Combine(tempRoot, "Legacy contract series");
        WriteManagedCleanupSeason(seriesRoot, 1, "stale-release");
        var statePath = Path.Combine(seriesRoot, RefreshStateManager.StateFileName);
        var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        state["generationContractVersion"] = RefreshStateManager.GenerationContractVersion - 1;
        File.WriteAllText(statePath, state.ToJsonString());

        var result = RunStaleReleaseCleanup(tempRoot, Array.Empty<string>());

        AssertTrue(Directory.Exists(seriesRoot), "A stale directory with an older generation contract must be preserved.");
        AssertEqual(1, result.SkippedDirectoryCount, "Legacy generation state should take the safe skip path.");
        AssertEqual(0, result.DeletedDirectoryCount, "Legacy state must not authorize deletion.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void StaleReleaseCleanup_DeletesMixedRootStaleSeasonAndStateEntry()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    try
    {
        var seriesRoot = Path.Combine(tempRoot, "Shared series");
        var currentSeason = Path.Combine(seriesRoot, "Season 01");
        var staleSeason = Path.Combine(seriesRoot, "Season 02");
        WriteManagedCleanupSeason(seriesRoot, 1, "current-release");
        WriteManagedCleanupSeason(seriesRoot, 2, "stale-release");

        var result = RunStaleReleaseCleanup(tempRoot, new[] { "current-release" });

        AssertTrue(Directory.Exists(seriesRoot), "A mixed series root must remain while it has a current season.");
        AssertTrue(Directory.Exists(currentSeason), "The current season directory must remain in a mixed series root.");
        AssertFalse(Directory.Exists(staleSeason), "The stale season directory must be removed from a mixed series root.");
        AssertEqual(1, result.DeletedDirectoryCount, "Removing one stale season must count as one managed directory deletion.");
        AssertEqual(0, result.DeletedSeriesDirectoryCount, "Removing only a stale season must not count as deleting its retained series root.");
        AssertEqual(1, result.DeletedSeasonDirectoryCount, "Mixed-root cleanup should report the removed stale season.");

        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(seriesRoot, RefreshStateManager.StateFileName)));
        var seasons = state.RootElement.GetProperty("seasons");
        AssertTrue(seasons.TryGetProperty("Season 01", out _), "Current season state must remain after mixed-root cleanup.");
        AssertFalse(seasons.TryGetProperty("Season 02", out _), "Stale season state must be removed with its directory.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void StaleReleaseCleanup_DeletesAllStaleRootForEmptyCurrentSet()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    try
    {
        var seriesRoot = Path.Combine(tempRoot, "All stale series");
        WriteManagedCleanupSeason(seriesRoot, 1, "stale-one");
        WriteManagedCleanupSeason(seriesRoot, 2, "stale-two");

        var result = RunStaleReleaseCleanup(tempRoot, Array.Empty<string>());

        AssertFalse(Directory.Exists(seriesRoot), "An all-stale managed series root should be deleted when the valid current user list is empty.");
        AssertEqual(1, result.DeletedDirectoryCount, "Deleting an all-stale multi-season root should count as one root deletion.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void RefreshTitleKeySource_ValidEmptyUserListMarksFetchSucceeded()
{
    var handler = new DelegatingTestHandler(request =>
    {
        AssertEqual(HttpMethod.Get, request.Method, "Refresh title key source should fetch the configured Yummy user list.");
        AssertEqual("https://yummy.test/users/42/lists/7", request.RequestUri!.AbsoluteUri, "Refresh title key source should request the configured user and list ids.");
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"response\":[]}", Encoding.UTF8, "application/json")
        };
    });

    using var http = new HttpClient(handler);
    var yummy = new YummyClient(http, "test-client", "https://yummy.test");
    var source = new RefreshTitleKeySource(NullLogger.Instance, static () => { });
    var cfg = new PluginConfiguration
    {
        UseUserListSubscription = true,
        YummyUserId = 42,
        YummyUserListId = 7,
        Slugs = new List<string>()
    };

    var keys = source.BuildAsync(cfg, yummy, CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual(0, keys.Count, "A valid empty user list should produce no release keys.");
    AssertTrue(source.UserListFetchSucceeded, "A valid empty user-list response must open the stale-cleanup success gate.");
    AssertFalse(source.UserListFetchFailed, "A valid empty user-list response must not be marked as a fetch failure.");
}

static void RefreshTask_ProcessesAtMostTwoTitlesConcurrently()
{
    var active = 0;
    var maxActive = 0;
    var keys = Enumerable.Range(1, 8).Select(x => x.ToString()).ToArray();
    var progress = new RecordingProgress();

    InvokeRefreshTaskStaticTask(
            "ProcessKeysInParallelAsync",
            keys,
            (Func<string, CancellationToken, Task>)(async (_, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMax(ref maxActive, current);
                await Task.Delay(40, cancellationToken).ConfigureAwait(false);
                Interlocked.Decrement(ref active);
            }),
            progress,
            NullLogger.Instance,
            null,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertTrue(maxActive <= 2, "Refresh workers should never exceed MaxDegreeOfParallelism=2.");
    AssertEqual(100.0, progress.Values.Last(), "Progress should reach 100 after all title workers complete.");
}

static void RefreshTask_RunGateSkipsConcurrentRun()
{
    using var gate = new SemaphoreSlim(1, 1);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondBodyRan = false;

    var first = InvokeRefreshTaskStaticTaskResult<bool>(
        "RunWithRunGateAsync",
        gate,
        NullLogger.Instance,
        (Func<Task>)(async () =>
        {
            entered.SetResult();
            await release.Task.ConfigureAwait(false);
        }));

    AssertTrue(entered.Task.Wait(TimeSpan.FromSeconds(2)), "First gated refresh body should start.");

    var second = InvokeRefreshTaskStaticTaskResult<bool>(
            "RunWithRunGateAsync",
            gate,
            NullLogger.Instance,
            (Func<Task>)(() =>
            {
                secondBodyRan = true;
                return Task.CompletedTask;
            }))
        .GetAwaiter()
        .GetResult();

    AssertFalse(second, "Second refresh should exit quickly while the run gate is held.");
    AssertFalse(secondBodyRan, "Skipped refresh should not execute the body.");

    release.SetResult();
    AssertTrue(first.GetAwaiter().GetResult(), "First refresh should complete normally after releasing the gate.");
}

static void RefreshTask_LazyKodikInitializationRunsOnlyWhenValueIsUsed()
{
    var method = typeof(RefreshYummyKodikLibraryTask).GetMethod(
        "CreateSharedLazyTask",
        BindingFlags.Static | BindingFlags.NonPublic);
    AssertTrue(method is not null, "Shared lazy task helper should exist.");

    var calls = 0;
    Func<Task<int>> factory = async () =>
    {
        Interlocked.Increment(ref calls);
        await Task.Delay(40).ConfigureAwait(false);
        return 42;
    };

    var lazy = (Lazy<Task<int>>)method!.MakeGenericMethod(typeof(int)).Invoke(null, new object[] { factory })!;
    AssertEqual(0, calls, "Kodik lazy initialization should not run until the shared value is requested.");

    var tasks = Enumerable.Range(0, 8)
        .Select(_ => Task.Run(async () => await lazy.Value.ConfigureAwait(false)))
        .ToArray();
    var results = Task.WhenAll(tasks).GetAwaiter().GetResult();

    AssertEqual(1, calls, "Concurrent Kodik lazy initialization requests should share one factory call.");
    AssertTrue(results.All(value => value == 42), "All concurrent Kodik lazy callers should receive the initialized value.");
}

static void ShikimoriGraphQlClient_DeduplicatesConcurrentSameIdRequests()
{
    var requestCount = 0;
    using var http = new HttpClient(new AsyncDelegatingTestHandler(async (_, cancellationToken) =>
    {
        Interlocked.Increment(ref requestCount);
        await Task.Delay(50, cancellationToken).ConfigureAwait(false);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "data": {
                    "animes": [
                      {
                        "id": "52991",
                        "name": "Sousou no Frieren",
                        "russian": "Провожающая в последний путь Фрирен",
                        "kind": "tv",
                        "related": []
                      }
                    ]
                  }
                }
                """)
        };
    }));
    var client = new ShikimoriGraphQlClient(http, "https://shikimori.test/graphql");

    var tasks = Enumerable.Range(0, 8)
        .Select(_ => client.TryResolveSeriesLayoutAsync(52991, CancellationToken.None))
        .ToArray();
    Task.WhenAll(tasks).GetAwaiter().GetResult();

    AssertEqual(1, requestCount, "Concurrent same-id Shikimori lookups should share one cached task.");
    AssertTrue(tasks.All(task => task.Result?.SeasonNumber == 1), "All concurrent Shikimori lookups should receive the resolved layout.");
}

static void RefreshState_PerVoiceModeDoesNotPreSkip()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

    try
    {
        var fixture = CreateRefreshStateFixture(tempRoot, createStrmPerVoiceTranslation: true);
        RefreshStateManager.WriteSeasonStateAsync(
                fixture.SeriesRoot,
                fixture.Input,
                fixture.ExpectedEpisodeFileBaseNames,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var canSkip = RefreshStateManager.CanSkipSingleFileRefreshAsync(
                fixture.SeriesRoot,
                fixture.Input,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertFalse(canSkip, "Per-voice mode must not pre-skip Kodik lookup even when state and files match.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void RefreshState_KodikCatalogSignatureIsOrderIndependent()
{
    var first = RefreshStateManager.BuildKodikCatalogSignature(new RefreshStateKodikCatalogInput
    {
        IdType = "Shikimori",
        Id = "52991",
        SeriesCount = 2,
        Translations = new[]
        {
            new RefreshStateKodikTranslationInput
            {
                Id = "610",
                Type = "voice",
                Name = "AniLibria",
                MaxEpisode = 2,
                AvailableEpisodes = new[] { 2, 1, 2 }
            },
            new RefreshStateKodikTranslationInput
            {
                Id = "777",
                Type = "voice",
                Name = "AniStar",
                MaxEpisode = 2,
                AvailableEpisodes = new[] { 1, 2 }
            }
        }
    });
    var reordered = RefreshStateManager.BuildKodikCatalogSignature(new RefreshStateKodikCatalogInput
    {
        IdType = "Shikimori",
        Id = "52991",
        SeriesCount = 2,
        Translations = new[]
        {
            new RefreshStateKodikTranslationInput
            {
                Id = "777",
                Type = "voice",
                Name = "AniStar",
                MaxEpisode = 2,
                AvailableEpisodes = new[] { 2, 1 }
            },
            new RefreshStateKodikTranslationInput
            {
                Id = "610",
                Type = "voice",
                Name = "AniLibria",
                MaxEpisode = 2,
                AvailableEpisodes = new[] { 1, 2 }
            }
        }
    });
    var changed = RefreshStateManager.BuildKodikCatalogSignature(new RefreshStateKodikCatalogInput
    {
        IdType = "Shikimori",
        Id = "52991",
        SeriesCount = 2,
        Translations = new[]
        {
            new RefreshStateKodikTranslationInput
            {
                Id = "610",
                Type = "voice",
                Name = "AniLibria",
                MaxEpisode = 2,
                AvailableEpisodes = new[] { 1, 2 }
            },
            new RefreshStateKodikTranslationInput
            {
                Id = "778",
                Type = "voice",
                Name = "New Voice",
                MaxEpisode = 2,
                AvailableEpisodes = new[] { 1, 2 }
            }
        }
    });

    AssertEqual(first, reordered, "Kodik catalog signature should ignore provider ordering and duplicate episode numbers.");
    AssertFalse(string.Equals(first, changed, StringComparison.Ordinal), "A changed Kodik translation catalog should invalidate the signature.");
}

static void RefreshState_PerVoiceDeepSkipRequiresFreshMatchingCatalog()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

    try
    {
        var fixture = CreateRefreshStateFixture(tempRoot, createStrmPerVoiceTranslation: true);
        var validatedAtUtc = new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
        const string signature = "sha256:kodik-catalog-a";
        var seasonDir = Path.Combine(fixture.SeriesRoot, fixture.Input.SeasonKey);
        const string kodikOnlyFileBaseName = "S01E02 - Kodik Voice";
        File.WriteAllText(
            Path.Combine(seasonDir, kodikOnlyFileBaseName + ".strm"),
            "https://jellyfin.test/YummyKodik/stream?type=shikimori&id=52991&ep=2&tr=610&format=hls" + Environment.NewLine);
        File.WriteAllText(
            Path.Combine(seasonDir, kodikOnlyFileBaseName + ".nfo"),
            NfoBuilder.BuildEpisodeNfo(2, 1, "Frieren", "Plot"));
        fixture.ExpectedEpisodeFileBaseNames[2] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            kodikOnlyFileBaseName
        };
        var written = RefreshStateManager.WriteSeasonStateAsync(
                fixture.SeriesRoot,
                fixture.Input,
                fixture.ExpectedEpisodeFileBaseNames,
                mediaSegmentEntriesByFileBaseName: null,
                new RefreshStateKodikValidation
                {
                    CatalogSignature = signature,
                    DeepValidatedAtUtc = validatedAtUtc
                },
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertTrue(written, "Complete per-voice artifacts should produce refresh state with Kodik validation metadata.");

        var canSkip = RefreshStateManager.CanSkipPerVoiceDeepRefreshAsync(
                fixture.SeriesRoot,
                fixture.Input,
                signature,
                validatedAtUtc.AddHours(1),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertTrue(canSkip, "Fresh matching Yummy state and Kodik catalog should skip deep per-episode validation.");

        var canSkipKodikLookup = RefreshStateManager.CanSkipPerVoiceKodikLookupAsync(
                fixture.SeriesRoot,
                fixture.Input,
                validatedAtUtc.AddHours(1),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertTrue(canSkipKodikLookup, "A high-quality fast refresh should avoid a redundant Kodik lookup while verified fallback state is fresh.");

        var targetQualityInput = BuildRefreshStateSeasonInput(
            expectedAvailableEpisodes: fixture.Input.ExpectedAvailableEpisodes,
            createStrmPerVoiceTranslation: true,
            preferredQuality: RefreshStateManager.KodikKnownMaximumQuality,
            fingerprint: fixture.Input.Fingerprint);
        var targetQualityCanSkipLookup = RefreshStateManager.CanSkipPerVoiceKodikLookupAsync(
                fixture.SeriesRoot,
                targetQualityInput,
                validatedAtUtc.AddHours(1),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertFalse(targetQualityCanSkipLookup, "When Kodik can satisfy the requested quality, its lightweight catalog must still be checked every refresh.");

        var changedCatalogCanSkip = RefreshStateManager.CanSkipPerVoiceDeepRefreshAsync(
                fixture.SeriesRoot,
                fixture.Input,
                "sha256:kodik-catalog-b",
                validatedAtUtc.AddHours(1),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertFalse(changedCatalogCanSkip, "Changed Kodik metadata should force deep validation.");

        var expiredCanSkip = RefreshStateManager.CanSkipPerVoiceDeepRefreshAsync(
                fixture.SeriesRoot,
                fixture.Input,
                signature,
                validatedAtUtc.Add(RefreshStateManager.PerVoiceDeepValidationInterval),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertFalse(expiredCanSkip, "Expired Kodik validation should force a periodic deep check.");

        var expiredLookupCanSkip = RefreshStateManager.CanSkipPerVoiceKodikLookupAsync(
                fixture.SeriesRoot,
                fixture.Input,
                validatedAtUtc.Add(RefreshStateManager.PerVoiceDeepValidationInterval),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertFalse(expiredLookupCanSkip, "Expired high-quality fast-path state should force a fresh Kodik catalog lookup.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void RefreshState_NewYummyKodikVoiceInvalidatesPreLookupSkip()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

    try
    {
        static YummyAnimeResponse BuildAnime(bool includeDreamCast, string iframeMarker, int duration)
        {
            var videos = new List<YummyVideoItem>
            {
                new()
                {
                    Number = "9",
                    IframeUrl = "https://kodik.test/animevost/" + iframeMarker,
                    Duration = duration,
                    Data = new YummyVideoData
                    {
                        PlayerId = (int)YummyVideoProviderKind.Kodik,
                        Player = "Плеер Kodik",
                        Dubbing = "Озвучка AnimeVost"
                    }
                }
            };

            if (includeDreamCast)
            {
                videos.Add(new YummyVideoItem
                {
                    Number = "9",
                    IframeUrl = "https://kodik.test/dream-cast/" + iframeMarker,
                    Duration = duration,
                    Data = new YummyVideoData
                    {
                        PlayerId = (int)YummyVideoProviderKind.Kodik,
                        Player = "Плеер Kodik",
                        Dubbing = "Озвучка Dream Cast"
                    }
                });
            }

            return new YummyAnimeResponse
            {
                AnimeId = 11237,
                AnimeUrl = "taynaya-bitva-za-prestol-silneyshego-printsa-duraleya",
                Title = "Тайная битва за престол сильнейшего принца-дуралея",
                Videos = videos
            };
        }

        static YummyRefreshInfo BuildRefresh(YummyAnimeResponse anime)
        {
            return new YummyRefreshInfo(
                new YummyAnimeTitleInfo(anime, anime.AnimeUrl, anime.Title, anime.Title, 1),
                YummyVideoCatalog.Create(anime),
                new SeriesFileInfo("series", "season", "http://127.0.0.1:8096"),
                new EpisodeAvailabilityInfo(new[] { 9 }, 9, Array.Empty<int>(), Array.Empty<int>(), new[] { 9 }));
        }

        var cfg = new PluginConfiguration
        {
            CreateStrmPerVoiceTranslation = true,
            PreferredQuality = 1080
        };
        var baselineCoverage = RefreshStateService.BuildProviderCoverageFingerprintItems(
            cfg,
            BuildRefresh(BuildAnime(includeDreamCast: false, iframeMarker: "before", duration: 1420)));
        var metadataOnlyCoverage = RefreshStateService.BuildProviderCoverageFingerprintItems(
            cfg,
            BuildRefresh(BuildAnime(includeDreamCast: false, iframeMarker: "after", duration: 1500)));
        var changedCoverage = RefreshStateService.BuildProviderCoverageFingerprintItems(
            cfg,
            BuildRefresh(BuildAnime(includeDreamCast: true, iframeMarker: "after", duration: 1427)));

        AssertEqual(
            string.Join("|", baselineCoverage),
            string.Join("|", metadataOnlyCoverage),
            "Kodik iframe and duration churn must not invalidate the availability fingerprint.");
        AssertFalse(
            baselineCoverage.Any(x => x.Contains("dreamcast", StringComparison.Ordinal)),
            "The baseline fingerprint must reproduce the catalog before Dream Cast episode 9 appeared.");
        AssertTrue(
            changedCoverage.Any(x => string.Equals(x, "ep:9:yummy-kodik:voice:dreamcast", StringComparison.Ordinal)),
            "A new Yummy-advertised Kodik voice must enter the semantic refresh fingerprint.");

        var baselineFingerprint = RefreshStateManager.BuildFingerprint(BuildRefreshStateFingerprintInput(
            mode: "per-voice",
            preferredQuality: 1080,
            expectedAvailableEpisodes: 9,
            providerCoverage: baselineCoverage));
        var changedFingerprint = RefreshStateManager.BuildFingerprint(BuildRefreshStateFingerprintInput(
            mode: "per-voice",
            preferredQuality: 1080,
            expectedAvailableEpisodes: 9,
            providerCoverage: changedCoverage));
        AssertFalse(
            string.Equals(baselineFingerprint, changedFingerprint, StringComparison.Ordinal),
            "New Kodik-only episode/voice availability must change the refresh fingerprint.");

        var fixture = CreateRefreshStateFixture(
            tempRoot,
            expectedAvailableEpisodes: 9,
            createStrmPerVoiceTranslation: true,
            fingerprint: baselineFingerprint);
        var validatedAtUtc = new DateTimeOffset(2026, 9, 1, 18, 46, 46, TimeSpan.Zero);
        AssertTrue(
            RefreshStateManager.WriteSeasonStateAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    fixture.ExpectedEpisodeFileBaseNames,
                    mediaSegmentEntriesByFileBaseName: null,
                    new RefreshStateKodikValidation
                    {
                        CatalogSignature = "sha256:catalog-before-dream-cast-episode-9",
                        DeepValidatedAtUtc = validatedAtUtc
                    },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult(),
            "The pre-change per-voice state should be valid for the fast-path transition regression.");

        var unchangedDecision = RefreshStateManager.EvaluatePerVoiceKodikLookupAsync(
                fixture.SeriesRoot,
                fixture.Input,
                validatedAtUtc.AddMinutes(17),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertTrue(unchangedDecision.ShouldSkip, "An unchanged 1080p title should retain the pre-Kodik fast path.");

        var changedInput = BuildRefreshStateSeasonInput(
            expectedAvailableEpisodes: 9,
            createStrmPerVoiceTranslation: true,
            preferredQuality: 1080,
            fingerprint: changedFingerprint,
            cleanKey: fixture.Input.CleanKey);
        var changedDecision = RefreshStateManager.EvaluatePerVoiceKodikLookupAsync(
                fixture.SeriesRoot,
                changedInput,
                validatedAtUtc.AddMinutes(17),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertFalse(changedDecision.ShouldSkip, "A newly advertised Kodik-only voice must force a fresh Kodik lookup.");
        AssertEqual(
            RefreshSkipReason.FingerprintMismatch,
            changedDecision.Reason,
            "The fast path should reject the changed title before redundant managed-file hashing.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void RefreshState_PerVoiceDeepSkipRejectsDamagedOrUnexpectedFiles()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

    try
    {
        var fixture = CreateRefreshStateFixture(tempRoot, createStrmPerVoiceTranslation: true);
        var validatedAtUtc = DateTimeOffset.UtcNow;
        const string signature = "sha256:kodik-catalog";
        RefreshStateManager.WriteSeasonStateAsync(
                fixture.SeriesRoot,
                fixture.Input,
                fixture.ExpectedEpisodeFileBaseNames,
                mediaSegmentEntriesByFileBaseName: null,
                new RefreshStateKodikValidation
                {
                    CatalogSignature = signature,
                    DeepValidatedAtUtc = validatedAtUtc
                },
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var seasonDir = Path.Combine(fixture.SeriesRoot, fixture.Input.SeasonKey);
        var managedStrm = Directory.EnumerateFiles(seasonDir, "*.strm", SearchOption.TopDirectoryOnly).Single();
        File.AppendAllText(managedStrm, "damaged");
        var damagedCanSkip = RefreshStateManager.CanSkipPerVoiceDeepRefreshAsync(
                fixture.SeriesRoot,
                fixture.Input,
                signature,
                validatedAtUtc.AddMinutes(1),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertFalse(damagedCanSkip, "A modified managed artifact should force deep validation.");

        File.WriteAllText(managedStrm, "https://jellyfin.test/YummyKodik/stream?ep=1&allohaRequestToken=secret-request-1" + Environment.NewLine);
        RefreshStateManager.WriteSeasonStateAsync(
                fixture.SeriesRoot,
                fixture.Input,
                fixture.ExpectedEpisodeFileBaseNames,
                mediaSegmentEntriesByFileBaseName: null,
                new RefreshStateKodikValidation
                {
                    CatalogSignature = signature,
                    DeepValidatedAtUtc = validatedAtUtc
                },
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        File.WriteAllText(Path.Combine(seasonDir, "S01E02 - Stale.strm"), "stale");
        File.WriteAllText(Path.Combine(seasonDir, "S01E02 - Stale.nfo"), "<episodedetails />");

        var unexpectedCanSkip = RefreshStateManager.CanSkipPerVoiceDeepRefreshAsync(
                fixture.SeriesRoot,
                fixture.Input,
                signature,
                validatedAtUtc.AddMinutes(1),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertFalse(unexpectedCanSkip, "Unexpected episode artifacts should force deep validation and cleanup.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void RefreshState_SkipDecisionReportsReasonsAndFileCounts()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

    try
    {
        var missingInput = BuildRefreshStateSeasonInput();
        var missing = RefreshStateManager.EvaluateSingleFileRefreshAsync(
                Path.Combine(tempRoot, "Missing"),
                missingInput,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertEqual(RefreshSkipReason.StateMissing, missing.Reason, "Missing state should have an actionable skip reason.");
        AssertEqual(0, missing.ManagedFilesChecked, "Missing state must not claim any file hashes were checked.");

        var fixture = CreateRefreshStateFixture(tempRoot);
        AssertTrue(
            RefreshStateManager.WriteSeasonStateAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    fixture.ExpectedEpisodeFileBaseNames,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult(),
            "Complete fixture should write refresh state.");

        var matched = RefreshStateManager.EvaluateSingleFileRefreshAsync(
                fixture.SeriesRoot,
                fixture.Input,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertTrue(matched.ShouldSkip, "Matching state should produce a positive skip decision.");
        AssertEqual(RefreshSkipReason.Matched, matched.Reason, "Matching state should report Matched.");
        AssertEqual(3, matched.ManagedFilesChecked, "Fixture should hash tvshow NFO plus one STRM/NFO pair.");

        var changedInput = BuildRefreshStateSeasonInput(fingerprint: "sha256:changed");
        var changed = RefreshStateManager.EvaluateSingleFileRefreshAsync(
                fixture.SeriesRoot,
                changedInput,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertEqual(RefreshSkipReason.FingerprintMismatch, changed.Reason, "Input drift should be distinguishable from file damage.");
        AssertEqual(0, changed.ManagedFilesChecked, "Fingerprint mismatch should reject before hashing files.");

        var seasonDir = Path.Combine(fixture.SeriesRoot, fixture.Input.SeasonKey);
        var strmPath = Directory.EnumerateFiles(seasonDir, "*.strm", SearchOption.TopDirectoryOnly).Single();
        File.AppendAllText(strmPath, "damaged");
        var damaged = RefreshStateManager.EvaluateSingleFileRefreshAsync(
                fixture.SeriesRoot,
                fixture.Input,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertEqual(RefreshSkipReason.ManagedFileHashMismatch, damaged.Reason, "Damaged content should report a managed hash mismatch.");
        AssertTrue(damaged.ManagedFilesChecked > 0 && damaged.ManagedFilesChecked <= 3, "File count should reflect hashes actually computed before mismatch.");

        File.WriteAllText(
            strmPath,
            "https://jellyfin.test/YummyKodik/stream?ep=1&allohaRequestToken=secret-request-1" + Environment.NewLine);
        RefreshStateManager.WriteSeasonStateAsync(
                fixture.SeriesRoot,
                fixture.Input,
                fixture.ExpectedEpisodeFileBaseNames,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        File.WriteAllText(Path.Combine(seasonDir, "S01E02 - Stale.strm"), "stale");
        File.WriteAllText(Path.Combine(seasonDir, "S01E02 - Stale.nfo"), "<episodedetails />");
        var unexpected = RefreshStateManager.EvaluateSingleFileRefreshAsync(
                fixture.SeriesRoot,
                fixture.Input,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertEqual(RefreshSkipReason.UnexpectedArtifacts, unexpected.Reason, "Extra episode files should be a distinct skip rejection reason.");
        AssertEqual(3, unexpected.ManagedFilesChecked, "Unexpected-artifact scan should run only after all managed hashes match.");
        AssertEqual(2, unexpected.UnexpectedArtifactCount, "Both stale STRM and NFO should be reported.");

        var perVoiceRoot = Path.Combine(tempRoot, "PerVoice");
        var perVoiceFixture = CreateRefreshStateFixture(perVoiceRoot, createStrmPerVoiceTranslation: true);
        var validatedAt = DateTimeOffset.UtcNow.AddDays(-2);
        const string signature = "sha256:kodik-catalog";
        RefreshStateManager.WriteSeasonStateAsync(
                perVoiceFixture.SeriesRoot,
                perVoiceFixture.Input,
                perVoiceFixture.ExpectedEpisodeFileBaseNames,
                mediaSegmentEntriesByFileBaseName: null,
                new RefreshStateKodikValidation
                {
                    CatalogSignature = signature,
                    DeepValidatedAtUtc = validatedAt
                },
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        var expired = RefreshStateManager.EvaluatePerVoiceDeepRefreshAsync(
                perVoiceFixture.SeriesRoot,
                perVoiceFixture.Input,
                signature,
                DateTimeOffset.UtcNow,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertEqual(RefreshSkipReason.DeepValidationExpired, expired.Reason, "Expired 24-hour validation should have a stable reason.");
        AssertEqual(0, expired.ManagedFilesChecked, "Expired validation should reject before redundant hashing.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void KodikClient_PreferredQualityFallsBackToAvailableMaximum()
{
    var link = new KodikLinkInfo("//cloud.kodik-storage.example/useruploads/demo/", 720);

    var hlsUrl = KodikClient.BuildHlsUrl(link, 1080);
    var mp4Url = KodikClient.BuildMp4Url(link, 1080);

    AssertTrue(hlsUrl.EndsWith("/720.mp4:hls:manifest.m3u8", StringComparison.Ordinal), "Kodik HLS should keep the stream and use its best available quality when 1080p is requested.");
    AssertTrue(mp4Url.EndsWith("/720.mp4", StringComparison.Ordinal), "Kodik MP4 should keep the stream and use its best available quality when 1080p is requested.");
}

static void AllohaPlaybackService_BuildsExpectedBorthSuffix()
{
    var buildBorthSuffix = typeof(AllohaPlaybackService).GetMethod(
        "BuildBorthSuffix",
        BindingFlags.NonPublic | BindingFlags.Static);
    AssertTrue(buildBorthSuffix is not null, "Alloha Borth helper should exist.");

    var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";
    var suffix = buildBorthSuffix!.Invoke(null, new object[] { viewporti }) as string;
    AssertEqual(
        "SmdyTmVwVmFnQWd8MTc3NDEwNjgwNnxNVVZ1N09oSmNUdFZxUU11RlJqTkFXVjRFU1g0SXpTSGZDRFdyUXdsQ29Z",
        suffix,
        "Borth suffix must match the live Alloha viewporti transform.");
}

static void AllohaPlaybackService_CreatesSessionViaIframeAndBnsi()
{
    var requests = new List<HttpRequestMessage>();
    var iframeUrl = "https://alloha.yani.tv/?token_movie=6ab5db4ce142f2103d0bed3e641380&translation=215&season=1&episode=2&token=8b5512267a2a52e9de06d67d342e0c&hidden=translation,season,episode";
    var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";
    var expectedBorth = "9badb2c5dd28e9cd0bed84e7391523d9d308a48b690428dae6049233218645d9|SmdyTmVwVmFnQWd8MTc3NDEwNjgwNnxNVVZ1N09oSmNUdFZxUU11RlJqTkFXVjRFU1g0SXpTSGZDRFdyUXdsQ29Z";

    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://alloha.yani.tv/?token_movie=6ab5db4ce142f2103d0bed3e641380", StringComparison.Ordinal))
        {
            var iframeHtml =
                "<!DOCTYPE html>\n" +
                "<html>\n" +
                "<head>\n" +
                $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                "</head>\n" +
                "<body>\n" +
                "<script>\n" +
                "const fileList = JSON.parse('{\"active\":{\"id\":1191328,\"seasons\":1,\"episode\":2,\"id_translation\":215},\"all\":{\"t215\":{\"file\":{\"1\":{\"2\":{\"id\":1191328}}}}}}');\n" +
                "</script>\n" +
                "</body>\n" +
                "</html>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(iframeHtml)
            };
        }

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri == "https://alloha.yani.tv/bnsi/movies/1191328")
        {
            AssertEqual(expectedBorth, request.Headers.GetValues("Borth").Single(), "Alloha bnsi request should use the computed Borth header.");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "skipTime": 90,
                      "removeTime": 1200,
                      "hlsSource": [
                        {
                          "quality": {
                            "720": "https://stream-balancer-alloha.example/serial/playlist-720.m3u8",
                            "1080": "https://stream-balancer-alloha.example/serial/master.m3u8 or https://backup.example/serial/master.m3u8"
                          }
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == "https://stream-balancer-alloha.example/serial/master.m3u8")
        {
            AssertEqual("9badb2c5dd28e9cd0bed84e7391523d9d308a48b690428dae6049233218645d9", request.Headers.GetValues("Accepts-Controls").Single(), "Alloha manifest request should use Accepts-Controls.");
            AssertTrue(request.Headers.GetValues("Authorizations").Single().StartsWith("Bearer ", StringComparison.Ordinal), "Alloha manifest request should carry the guard token.");
            AssertEqual(iframeUrl, request.Headers.Referrer!.AbsoluteUri, "Alloha manifest request should keep the iframe referer.");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-VERSION:3
                    segment-001.ts
                    https://cdn.example/segment-002.ts
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected Alloha test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var proxyBaseUrl = "/base/YummyKodik/alloha-proxy";
    var source = new YummyAllohaSource
    {
        MovieToken = "6ab5db4ce142f2103d0bed3e641380",
        RequestToken = "8b5512267a2a52e9de06d67d342e0c",
        TranslationId = 215,
        SeasonNumber = 1,
        EpisodeNumber = 2,
        Hidden = "translation,season,episode",
        RefererUrl = iframeUrl
    };

    var session = service.CreateSessionAsync(source, 1080, CancellationToken.None).GetAwaiter().GetResult();
    var manifestBody = AllohaPlaybackService.BuildManifestResponseBody(session, proxyBaseUrl);

    AssertEqual("https://stream-balancer-alloha.example/serial/master.m3u8", session.ManifestUrl, "Alloha should choose the requested quality manifest.");
    AssertTrue(manifestBody.Contains($"{proxyBaseUrl}/", StringComparison.Ordinal), "Alloha manifest should retain the Jellyfin base path in root-relative proxy urls.");
    AssertFalse(manifestBody.Contains("://", StringComparison.Ordinal), "Alloha proxy urls embedded in manifests must remain root-relative.");
    AssertTrue(manifestBody.Contains($".ts?sessionId={session.SessionId}&resource=", StringComparison.Ordinal), "Alloha segment proxy urls should keep a playable media extension.");
    AssertTrue(session.ProxyResources.Values.Contains("https://stream-balancer-alloha.example/serial/segment-001.ts"), "Relative Alloha manifest urls should be registered as proxy resources.");
    AssertTrue(session.ProxyResources.Values.Contains("https://cdn.example/segment-002.ts"), "Absolute Alloha manifest urls should be registered as proxy resources.");
    AssertEqual(3, requests.Count, "Alloha browserless flow should perform iframe, bnsi, and manifest requests.");
}

static void AllohaPlaybackService_SharesOnlyInFlightResolution()
{
    const string iframeUrl = "https://alloha.yani.tv/?token_movie=single-flight-movie&translation=410&season=1&episode=7&token=single-flight-token";
    const string bnsiUrl = "https://alloha.yani.tv/bnsi/movies/1410007";
    const string manifestBaseUrl = "https://stream-balancer-alloha.example/single-flight";
    const string viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";
    var iframeRequests = 0;
    var bnsiRequests = 0;
    var manifestRequests = 0;
    var firstResolutionStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseFirstResolution = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    using var http = new HttpClient(new AsyncDelegatingTestHandler(async (request, cancellationToken) =>
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (request.Method == HttpMethod.Get &&
            url.StartsWith("https://alloha.yani.tv/?token_movie=single-flight-movie", StringComparison.Ordinal))
        {
            var requestNumber = Interlocked.Increment(ref iframeRequests);
            if (requestNumber == 1)
            {
                firstResolutionStarted.TrySetResult(true);
                await releaseFirstResolution.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "<!DOCTYPE html><html><head>" +
                    $"<meta name=\"viewporti\" content=\"{viewporti}\">" +
                    "</head><body><script>" +
                    "const fileList = JSON.parse('{\"active\":{\"id\":1410007,\"seasons\":1,\"episode\":7,\"id_translation\":410},\"all\":{\"t410\":{\"file\":{\"1\":{\"7\":{\"id\":1410007}}}}}}');" +
                    "</script></body></html>")
            };
        }

        if (request.Method == HttpMethod.Post && url == bnsiUrl)
        {
            var requestNumber = Interlocked.Increment(ref bnsiRequests);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {
                      "hlsSource": [
                        {
                          "quality": {
                            "1080": "{{manifestBaseUrl}}/master-{{requestNumber}}.m3u8"
                          }
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.Method == HttpMethod.Get && url.StartsWith(manifestBaseUrl + "/master-", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref manifestRequests);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("#EXTM3U\n#EXTINF:3.0,\nsegment-001.ts\n#EXT-X-ENDLIST")
            };
        }

        throw new InvalidOperationException("Unexpected Alloha single-flight test request: " + request.RequestUri);
    }));
    var service = new AllohaPlaybackService(NullLogger<AllohaPlaybackService>.Instance, http);
    var source = new YummyAllohaSource
    {
        MovieToken = "single-flight-movie",
        RequestToken = "single-flight-token",
        TranslationId = 410,
        SeasonNumber = 1,
        EpisodeNumber = 7,
        RefererUrl = iframeUrl
    };

    var firstSessionTask = service.CreateSessionAsync(source, 1080, CancellationToken.None);
    AssertTrue(firstResolutionStarted.Task.Wait(1000), "The first Alloha resolution should reach the upstream iframe request.");
    var concurrentSessionTask = service.CreateSessionAsync(source, 1080, CancellationToken.None);
    releaseFirstResolution.TrySetResult(true);
    var concurrentSessions = Task.WhenAll(firstSessionTask, concurrentSessionTask).GetAwaiter().GetResult();

    AssertEqual(1, iframeRequests, "Concurrent Alloha sessions should share one in-flight iframe request.");
    AssertEqual(1, bnsiRequests, "Concurrent Alloha sessions should share one in-flight BNSI request.");
    AssertEqual(1, manifestRequests, "Concurrent Alloha sessions should share one in-flight manifest request.");
    AssertEqual(concurrentSessions[0].ManifestUrl, concurrentSessions[1].ManifestUrl, "Concurrent sessions should use the same freshly resolved payload.");

    var sequentialSession = service.CreateSessionAsync(source, 1080, CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual(2, iframeRequests, "A sequential Alloha session must resolve a fresh volatile payload.");
    AssertEqual(2, bnsiRequests, "A sequential Alloha session must not reuse the previous BNSI payload.");
    AssertEqual(2, manifestRequests, "A sequential Alloha session must validate a fresh manifest.");
    AssertTrue(
        !string.Equals(concurrentSessions[0].ManifestUrl, sequentialSession.ManifestUrl, StringComparison.Ordinal),
        "A sequential session must not inherit the previous session's token-bearing manifest.");
}

static void AllohaPlaybackService_DoesNotResolveDynamicTokenAfterSuccessfulManifest()
{
    var resolverCalls = 0;
    var iframeUrl = "https://alloha.yani.tv/?token_movie=success-movie&translation=340&season=4&episode=12&token=req-token";
    var manifestUrl = "https://e1-72-f3-r402.vkvideo.cloud/success/master.m3u8";
    var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";

    var handler = new DelegatingTestHandler(request =>
    {
        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://alloha.yani.tv/?token_movie=success-movie", StringComparison.Ordinal))
        {
            var iframeHtml =
                "<!DOCTYPE html>\n" +
                "<html>\n" +
                "<head>\n" +
                $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                "</head>\n" +
                "<body>\n" +
                "<script>\n" +
                "const fileList = JSON.parse('{\"active\":{\"id\":1191500,\"seasons\":4,\"episode\":12,\"id_translation\":340},\"all\":{\"t340\":{\"file\":{\"4\":{\"12\":{\"id\":1191500}}}}}}');\n" +
                "</script>\n" +
                "</body>\n" +
                "</html>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(iframeHtml)
            };
        }

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri == "https://alloha.yani.tv/bnsi/movies/1191500")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {
                      "pnr": "wss://alloha-ws.example/socket",
                      "pnk": "ws-session-success",
                      "hlsSource": [
                        {
                          "label": "AniMaunt",
                          "audioId": "2",
                          "quality": {
                            "1080": "{{manifestUrl}}"
                          }
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == manifestUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-VERSION:3
                    segment-001.ts
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected Alloha successful-manifest test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    Func<AllohaStreamTokenRequest, CancellationToken, Task<string?>> resolver = (_, _) =>
    {
        resolverCalls++;
        return Task.FromResult<string?>("unexpected-token");
    };
    var ctor = typeof(AllohaPlaybackService)
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
        .SingleOrDefault(x => x.GetParameters().Length == 3);
    AssertTrue(ctor is not null, "Alloha internal test constructor should allow injecting stream token resolver.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http, resolver });
    var source = new YummyAllohaSource
    {
        MovieToken = "success-movie",
        RequestToken = "req-token",
        TranslationId = 340,
        SeasonNumber = 4,
        EpisodeNumber = 12,
        RefererUrl = iframeUrl
    };

    var session = service.CreateSessionAsync(source, 1080, "AniMaunt", CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual(manifestUrl, session.ManifestUrl, "Alloha should use the successful manifest response.");
    AssertEqual(string.Empty, session.StreamToken, "Successful manifest setup should not wait for a dynamic stream token.");
    AssertEqual(0, resolverCalls, "Successful manifest setup should not call the optional dynamic token resolver.");
}

static void AllohaPlaybackService_RetriesManifestWithDynamicStreamTokenAfter403()
{
    var requests = new List<HttpRequestMessage>();
    var resolverRequests = new List<AllohaStreamTokenRequest>();
    var iframeUrl = "https://alloha.yani.tv/?token_movie=retry-movie&translation=163&season=1&episode=1&token=retry-token";
    var manifestUrl = "https://e1-72-f3-r402.vkvideo.cloud/demo/master.m3u8";
    var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";

    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://alloha.yani.tv/?token_movie=retry-movie", StringComparison.Ordinal))
        {
            var iframeHtml =
                "<!DOCTYPE html>\n" +
                "<html>\n" +
                "<head>\n" +
                $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                "</head>\n" +
                "<body>\n" +
                "<script>\n" +
                "const fileList = JSON.parse('{\"active\":{\"id\":1191400,\"seasons\":1,\"episode\":1,\"id_translation\":163},\"all\":{\"t163\":{\"file\":{\"1\":{\"1\":{\"id\":1191400}}}}}}');\n" +
                "</script>\n" +
                "</body>\n" +
                "</html>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(iframeHtml)
            };
        }

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri == "https://alloha.yani.tv/bnsi/movies/1191400")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {
                      "pnr": "wss://alloha-ws.example/socket",
                      "pnk": "ws-session-1",
                      "hlsSource": [
                        {
                          "label": "AniMaunt",
                          "audioId": "7",
                          "quality": {
                            "1080": "{{manifestUrl}}"
                          }
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == manifestUrl)
        {
            var acceptsControls = request.Headers.TryGetValues("Accepts-Controls", out var values)
                ? values.Single()
                : string.Empty;

            if (acceptsControls == "dynamic-edge-token")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        #EXTM3U
                        #EXT-X-VERSION:3
                        segment-001.ts
                        """)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<html><body>403 Forbidden</body></html>")
            };
        }

        throw new InvalidOperationException("Unexpected Alloha retry test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    Func<AllohaStreamTokenRequest, CancellationToken, Task<string?>> resolver = (request, _) =>
    {
        resolverRequests.Add(request);
        return Task.FromResult<string?>("dynamic-edge-token");
    };
    var ctor = typeof(AllohaPlaybackService)
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
        .SingleOrDefault(x => x.GetParameters().Length == 3);
    AssertTrue(ctor is not null, "Alloha internal test constructor should allow injecting stream token resolver.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http, resolver });
    var source = new YummyAllohaSource
    {
        MovieToken = "retry-movie",
        RequestToken = "retry-token",
        TranslationId = 163,
        SeasonNumber = 1,
        EpisodeNumber = 1,
        RefererUrl = iframeUrl
    };

    var session = service.CreateSessionAsync(source, 1080, "AniMaunt", CancellationToken.None).GetAwaiter().GetResult();
    var manifestRequests = requests
        .Where(x => x.Method == HttpMethod.Get && x.RequestUri!.AbsoluteUri == manifestUrl)
        .ToArray();

    AssertEqual(manifestUrl, session.ManifestUrl, "Alloha should keep the manifest selected before retry.");
    AssertEqual("dynamic-edge-token", session.StreamToken, "Successful 403 recovery should keep the dynamic stream token for proxied resources.");
    AssertEqual(2, manifestRequests.Length, "Alloha manifest download should retry once after upstream 403.");
    AssertEqual(
        "9badb2c5dd28e9cd0bed84e7391523d9d308a48b690428dae6049233218645d9",
        manifestRequests[0].Headers.GetValues("Accepts-Controls").Single(),
        "Initial Alloha manifest request should keep the static guard header.");
    AssertEqual(
        "dynamic-edge-token",
        manifestRequests[1].Headers.GetValues("Accepts-Controls").Single(),
        "Retried Alloha manifest request should use the dynamic stream token.");
    AssertEqual(1, resolverRequests.Count, "Alloha should resolve the dynamic stream token once after manifest 403.");
    AssertEqual("wss://alloha-ws.example/socket", resolverRequests[0].WebSocketBaseUrl, "Token request should include the BNSI websocket endpoint.");
    AssertEqual("ws-session-1", resolverRequests[0].WebSocketSessionId, "Token request should include the BNSI websocket session id.");
    AssertEqual("7", resolverRequests[0].AudioTrackId, "Token request should include the selected audio track.");
    AssertEqual(1080, resolverRequests[0].SelectedQuality, "Token request should include the selected manifest quality.");
}

static void AllohaPlaybackService_UsesIframeOriginForMirroredHost()
{
    var iframeUrl = "https://larkin-as.stloadi.live/?token_movie=mirror-movie&translation=222&season=2&episode=1&token=mirror-token";
    var viewporti = "1RhgM1dv3ztOKcE92RUowlxB2DSE3W1WkFFOcFIxd41UpsZ481aatG5NZMdWVH0EFTzjH0UTcdacSTdSNMMZSMdY";
    const string expectedOrigin = "https://larkin-as.stloadi.live";

    var handler = new DelegatingTestHandler(request =>
    {
        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://larkin-as.stloadi.live/?token_movie=mirror-movie", StringComparison.Ordinal))
        {
            var iframeHtml =
                "<!DOCTYPE html>\n" +
                "<html>\n" +
                "<head>\n" +
                $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                "</head>\n" +
                "<body>\n" +
                "<script>\n" +
                "const fileList = JSON.parse('{\"type\":\"serial\",\"active\":{\"id\":844166,\"seasons\":2,\"episode\":1,\"id_translation\":222},\"all\":{\"2\":{\"1\":{\"t222\":{\"id\":844166}}}}}');\n" +
                "</script>\n" +
                "</body>\n" +
                "</html>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(iframeHtml)
            };
        }

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri == "https://larkin-as.stloadi.live/bnsi/movies/844166")
        {
            AssertEqual(expectedOrigin, request.Headers.GetValues("Origin").Single(), "Alloha bnsi request should target the iframe origin for mirrored hosts.");
            AssertEqual(iframeUrl, request.Headers.Referrer!.AbsoluteUri, "Alloha bnsi request should keep the mirrored iframe referer.");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "hlsSource": [
                        {
                          "quality": {
                            "1080": "https://stream-balancer-alloha.example/mirror/master.m3u8"
                          }
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == "https://stream-balancer-alloha.example/mirror/master.m3u8")
        {
            AssertEqual(expectedOrigin, request.Headers.GetValues("Origin").Single(), "Alloha manifest request should reuse the iframe origin for mirrored hosts.");
            AssertEqual(iframeUrl, request.Headers.Referrer!.AbsoluteUri, "Alloha manifest request should keep the mirrored iframe referer.");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-VERSION:3
                    mirror-segment-001.ts
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected mirrored Alloha test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist for mirrored host scenarios.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var source = new YummyAllohaSource
    {
        MovieToken = "mirror-movie",
        RequestToken = "mirror-token",
        TranslationId = 222,
        SeasonNumber = 2,
        EpisodeNumber = 1,
        RefererUrl = iframeUrl
    };

    var session = service.CreateSessionAsync(source, 1080, CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual(expectedOrigin, session.RequiredHttpHeaders["Origin"], "Resolved mirrored sessions should keep the iframe origin in required headers.");
    AssertEqual(iframeUrl, session.RequiredHttpHeaders["Referer"], "Resolved mirrored sessions should keep the iframe referer in required headers.");
}

static void AllohaPlaybackService_RejectsUntrustedIframeUrl()
{
    var handler = new DelegatingTestHandler(_ =>
    {
        throw new InvalidOperationException("Untrusted Alloha iframe URLs must be rejected before sending a request.");
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist for URL validation scenarios.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var source = new YummyAllohaSource
    {
        MovieToken = "movie",
        RequestToken = "request",
        TranslationId = 215,
        SeasonNumber = 1,
        EpisodeNumber = 1,
        RefererUrl = "https://127.0.0.1:9443/private?token_movie=movie&translation=215&season=1&episode=1&token=request"
    };

    var ex = AssertThrows<InvalidOperationException>(
        () => service.CreateSessionAsync(source, 1080, CancellationToken.None).GetAwaiter().GetResult(),
        "Alloha should reject iframe URLs outside the known upstream hosts.");
    AssertTrue(ex.Message.Contains("Alloha iframe URL is not allowed", StringComparison.Ordinal), "Exception should explain that the iframe URL is not allowed.");
}

static void AllohaPlaybackService_PrefersRequestedVoiceWhenBnsiReturnsMultipleTracks()
{
    var iframeUrl = "https://alloha.yani.tv/?token_movie=voice-movie&translation=215&season=1&episode=2&token=req-token";
    var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";

    var handler = new DelegatingTestHandler(request =>
    {
        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://alloha.yani.tv/?token_movie=voice-movie", StringComparison.Ordinal))
        {
            var iframeHtml =
                "<!DOCTYPE html>\n" +
                "<html>\n" +
                "<head>\n" +
                $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                "</head>\n" +
                "<body>\n" +
                "<script>\n" +
                "const fileList = JSON.parse('{\"active\":{\"id\":1191328,\"seasons\":1,\"episode\":2,\"id_translation\":215},\"all\":{\"t215\":{\"file\":{\"1\":{\"2\":{\"id\":1191328}}}}}}');\n" +
                "</script>\n" +
                "</body>\n" +
                "</html>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(iframeHtml)
            };
        }

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri == "https://alloha.yani.tv/bnsi/movies/1191328")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "hlsSource": [
                        {
                          "label": "(Russian) DUB | DEEP",
                          "audioId": "1",
                          "quality": {
                            "1080": "https://stream-balancer-alloha.example/deep/master.m3u8"
                          }
                        },
                        {
                          "label": "(Russian) AniLibria.TV",
                          "audioId": "2",
                          "quality": {
                            "1080": "https://stream-balancer-alloha.example/anilibria/master.m3u8"
                          }
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == "https://stream-balancer-alloha.example/anilibria/master.m3u8")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-VERSION:3
                    segment-001.ts
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected Alloha preferred-voice test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var source = new YummyAllohaSource
    {
        MovieToken = "voice-movie",
        RequestToken = "req-token",
        TranslationId = 215,
        SeasonNumber = 1,
        EpisodeNumber = 2,
        RefererUrl = iframeUrl
    };

    var session = service.CreateSessionAsync(source, 1080, "AniLiberty", CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual("https://stream-balancer-alloha.example/anilibria/master.m3u8", session.ManifestUrl, "Alloha should prefer the manifest candidate matching the requested voice.");
    AssertEqual("AniLibria.TV", session.SelectedVoiceName, "Selected Alloha voice should match the requested track rather than the first quality candidate.");
}

static void AllohaPlaybackService_UsesAlternateVoiceFieldWhenLabelIsOpaque()
{
    var iframeUrl = "https://alloha.yani.tv/?token_movie=voice-movie-opaque&translation=215&season=1&episode=4&token=req-token";
    var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";

    var handler = new DelegatingTestHandler(request =>
    {
        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://alloha.yani.tv/?token_movie=voice-movie-opaque", StringComparison.Ordinal))
        {
            var iframeHtml =
                "<!DOCTYPE html>\n" +
                "<html>\n" +
                "<head>\n" +
                $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                "</head>\n" +
                "<body>\n" +
                "<script>\n" +
                "const fileList = JSON.parse('{\"active\":{\"id\":1191330,\"seasons\":1,\"episode\":4,\"id_translation\":215},\"all\":{\"t215\":{\"file\":{\"1\":{\"4\":{\"id\":1191330}}}}}}');\n" +
                "</script>\n" +
                "</body>\n" +
                "</html>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(iframeHtml)
            };
        }

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri == "https://alloha.yani.tv/bnsi/movies/1191330")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "hlsSource": [
                        {
                          "label": "1",
                          "voice": "AniDUB",
                          "audioId": "1",
                          "quality": {
                            "720": "https://stream-balancer-alloha.example/anidub-opaque/master.m3u8"
                          }
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == "https://stream-balancer-alloha.example/anidub-opaque/master.m3u8")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-VERSION:3
                    segment-001.ts
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected Alloha opaque-label test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var source = new YummyAllohaSource
    {
        MovieToken = "voice-movie-opaque",
        RequestToken = "req-token",
        TranslationId = 215,
        SeasonNumber = 1,
        EpisodeNumber = 4,
        RefererUrl = iframeUrl
    };

    var session = service.CreateSessionAsync(source, 720, "AniDUB", CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual("https://stream-balancer-alloha.example/anidub-opaque/master.m3u8", session.ManifestUrl, "Alloha should keep the only manifest candidate when the readable voice lives outside the label field.");
    AssertEqual("AniDUB", session.SelectedVoiceName, "Alloha should prefer the alternate upstream voice field over an opaque numeric label.");
}

static void AllohaPlaybackService_MatchesShortAnilibAliasToAnilibria()
{
    var iframeUrl = "https://alloha.yani.tv/?token_movie=voice-movie-short&translation=215&season=1&episode=3&token=req-token";
    var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";

    var handler = new DelegatingTestHandler(request =>
    {
        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://alloha.yani.tv/?token_movie=voice-movie-short", StringComparison.Ordinal))
        {
            var iframeHtml =
                "<!DOCTYPE html>\n" +
                "<html>\n" +
                "<head>\n" +
                $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                "</head>\n" +
                "<body>\n" +
                "<script>\n" +
                "const fileList = JSON.parse('{\"active\":{\"id\":1191329,\"seasons\":1,\"episode\":3,\"id_translation\":215},\"all\":{\"t215\":{\"file\":{\"1\":{\"3\":{\"id\":1191329}}}}}}');\n" +
                "</script>\n" +
                "</body>\n" +
                "</html>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(iframeHtml)
            };
        }

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri == "https://alloha.yani.tv/bnsi/movies/1191329")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "hlsSource": [
                        {
                          "label": "(Russian) DUB | DEEP",
                          "audioId": "1",
                          "quality": {
                            "1080": "https://stream-balancer-alloha.example/deep-short/master.m3u8"
                          }
                        },
                        {
                          "label": "(Russian) Anilib",
                          "audioId": "2",
                          "quality": {
                            "1080": "https://stream-balancer-alloha.example/anilib-short/master.m3u8"
                          }
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == "https://stream-balancer-alloha.example/anilib-short/master.m3u8")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-VERSION:3
                    segment-001.ts
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected Alloha short-alias test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var source = new YummyAllohaSource
    {
        MovieToken = "voice-movie-short",
        RequestToken = "req-token",
        TranslationId = 215,
        SeasonNumber = 1,
        EpisodeNumber = 3,
        RefererUrl = iframeUrl
    };

    var session = service.CreateSessionAsync(source, 1080, "Anilibria", CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual("https://stream-balancer-alloha.example/anilib-short/master.m3u8", session.ManifestUrl, "Alloha should treat short Anilib labels as AniLibria when matching the requested voice.");
    AssertEqual("Anilib", session.SelectedVoiceName, "Selected Alloha voice should preserve the upstream short alias label after matching.");
}

static void YummyKodikStreamController_AllowsSingleOpaqueAllohaTrackMarker()
{
    var method = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
        "AllohaSessionSupportsRequestedVoice",
        BindingFlags.NonPublic | BindingFlags.Static);
    AssertTrue(method is not null, "Alloha voice validation helper should exist.");

    var session = new AllohaPlaybackSession
    {
        AudioTrackId = "1",
        SelectedVoiceName = "1",
        AvailableVoiceNames = new[] { "1" }
    };

    var supported = (bool)method!.Invoke(null, new object[] { session, "AniDUB" })!;
    AssertTrue(supported, "A single opaque Alloha track marker should not block playback when the translation was already chosen upstream.");
}

static void YummyKodikStreamController_RejectsSingleGenericRussianAllohaTrackMarker()
{
    var method = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
        "AllohaSessionSupportsRequestedVoice",
        BindingFlags.NonPublic | BindingFlags.Static);
    AssertTrue(method is not null, "Alloha voice validation helper should exist.");

    var session = new AllohaPlaybackSession
    {
        AudioTrackId = "1",
        SelectedVoiceName = "Русский",
        AvailableVoiceNames = new[] { "Русский" }
    };

    var supported = (bool)method!.Invoke(null, new object[] { session, "AnimeVost" })!;
    AssertFalse(supported, "A generic Russian Alloha track marker is not enough evidence that an explicit named voice was selected.");
}

static void YummyKodikStreamController_RejectsMultipleOpaqueAllohaTrackMarkers()
{
    var method = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
        "AllohaSessionSupportsRequestedVoice",
        BindingFlags.NonPublic | BindingFlags.Static);
    AssertTrue(method is not null, "Alloha voice validation helper should exist.");

    var session = new AllohaPlaybackSession
    {
        AudioTrackId = "1",
        SelectedVoiceName = "1",
        AvailableVoiceNames = new[] { "1", "2" }
    };

    var supported = (bool)method!.Invoke(null, new object[] { session, "AniDUB" })!;
    AssertFalse(supported, "Opaque multi-track Alloha sessions should still be rejected until we can match a real voice name.");
}

static void YummyKodikStreamController_OrdersGatewayFallbackProviders()
{
    var method = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
        "GetFallbackProviderOrder",
        BindingFlags.NonPublic | BindingFlags.Static);
    AssertTrue(method is not null, "Gateway fallback provider ordering helper should exist.");

    var allohaFallback = ((System.Collections.IEnumerable)method!.Invoke(null, new object[] { YummyStreamProviderKind.Alloha })!)
        .Cast<object>()
        .Select(x => x.ToString())
        .ToArray();
    var cvhFallback = ((System.Collections.IEnumerable)method.Invoke(null, new object[] { YummyStreamProviderKind.Cvh })!)
        .Cast<object>()
        .Select(x => x.ToString())
        .ToArray();

    AssertEqual("Kodik,Cvh", string.Join(",", allohaFallback), "Alloha failures should try Kodik before the less reliable CVH transport.");
    AssertEqual("Alloha,Kodik", string.Join(",", cvhFallback), "CVH failures should first try the preferred Yummy provider and then Kodik.");
}

static void YummyKodikStreamController_PrioritizesKodikForVoiceMissingFromYummyCatalog()
{
    var method = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
        "GetVoiceAwareFallbackProviderOrder",
        BindingFlags.NonPublic | BindingFlags.Static);
    AssertTrue(method is not null, "Voice-aware gateway fallback ordering helper should exist.");

    var catalog = YummyVideoCatalog.Create(new YummyAnimeResponse
    {
        AnimeId = 12852,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "42",
                IframeUrl = "https://play.example/player?anime_id=12852&episode=42&dubbing=AnimeVost",
                Data = new YummyVideoData
                {
                    PlayerId = (int)YummyVideoProviderKind.Cvh,
                    Dubbing = "AnimeVost"
                }
            }
        }
    });

    var fallback = ((System.Collections.IEnumerable)method!.Invoke(
            null,
            new object[] { YummyStreamProviderKind.Cvh, catalog, 42, "СВ-Дубль" })!)
        .Cast<object>()
        .Select(x => x.ToString())
        .ToArray();

    AssertEqual(
        "Kodik,Alloha",
        string.Join(",", fallback),
        "A voice absent from both Yummy-backed providers should go directly to Kodik instead of warning once per provider.");
}

static void YummyKodikStreamController_UsesSharedYummyVoicePreferenceAcrossProviders()
{
    var setMethod = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
        "SetYummyVoicePreference",
        BindingFlags.NonPublic | BindingFlags.Static);
    var getMethod = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
        "GetSavedYummyVoiceName",
        BindingFlags.NonPublic | BindingFlags.Static);
    AssertTrue(setMethod is not null, "Shared Yummy voice setter should exist.");
    AssertTrue(getMethod is not null, "Shared Yummy voice getter should exist.");

    var cfg = new PluginConfiguration();
    var userId = Guid.NewGuid();

    var changed = (bool)setMethod!.Invoke(null, new object[] { cfg, userId, 21008L, YummyStreamProviderKind.Alloha, "AniStar" })!;
    AssertTrue(changed, "Saving a Yummy voice should change the configuration.");

    var savedForCvh = (string?)getMethod!.Invoke(null, new object[] { cfg, userId, 21008L, YummyStreamProviderKind.Cvh });
    AssertEqual("AniStar", savedForCvh ?? string.Empty, "A voice selected from an Alloha-backed page should be visible to CVH-backed episode URLs.");
    AssertEqual("AniStar", cfg.GetUserSeriesPreferredTranslationId(userId, "yummy:21008") ?? string.Empty, "Shared Yummy preference key should be populated.");
    AssertEqual("AniStar", cfg.GetUserSeriesPreferredTranslationId(userId, "alloha:21008") ?? string.Empty, "Legacy provider key should be populated for compatibility.");

    changed = (bool)setMethod.Invoke(null, new object[] { cfg, userId, 21008L, YummyStreamProviderKind.Cvh, string.Empty })!;
    AssertTrue(changed, "Clearing a Yummy voice should remove stored provider preferences.");
    AssertEqual(string.Empty, cfg.GetUserSeriesPreferredTranslationId(userId, "yummy:21008") ?? string.Empty, "Shared Yummy preference key should be cleared.");
    AssertEqual(string.Empty, cfg.GetUserSeriesPreferredTranslationId(userId, "alloha:21008") ?? string.Empty, "Legacy Alloha key should be cleared.");
    AssertEqual(string.Empty, cfg.GetUserSeriesPreferredTranslationId(userId, "cvh:21008") ?? string.Empty, "Legacy CVH key should be cleared.");
}

static void YummyKodikStreamController_DetectsSavedVoiceOnNeighborProvider()
{
    var method = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
        "ShouldUseDifferentYummyProviderForSavedVoice",
        BindingFlags.NonPublic | BindingFlags.Static);
    AssertTrue(method is not null, "Saved-voice provider routing helper should exist.");

    var anime = new YummyAnimeResponse
    {
        AnimeId = 21008,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1",
                IframeUrl = "https://larkin-as.stloadi.live/?token_movie=movie&translation=215&season=1&episode=1&token=req",
                Data = new YummyVideoData
                {
                    PlayerId = (int)YummyVideoProviderKind.Alloha,
                    Dubbing = "Dream Cast"
                }
            },
            new()
            {
                Number = "1",
                IframeUrl = "https://play.example/player?anime_id=21008&episode=1&dubbing_code=158&dubbing=AniStar",
                Data = new YummyVideoData
                {
                    PlayerId = (int)YummyVideoProviderKind.Cvh,
                    Dubbing = "AniStar"
                }
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime);
    var args = new object[] { catalog, YummyVideoProviderKind.Alloha, 1, "AniStar", YummyVideoProviderKind.Unknown };

    var shouldReroute = (bool)method!.Invoke(null, args)!;
    AssertTrue(shouldReroute, "Alloha playback should defer to a neighboring provider when only that provider has the saved voice.");
    AssertEqual(YummyVideoProviderKind.Cvh, (YummyVideoProviderKind)args[4], "Saved voice should resolve to the CVH provider.");
}

static void YummyKodikStreamController_UnionsProviderAndManagedVoices()
{
    var options = InvokeStatic<object>(
        typeof(YummyKodik.Api.YummyKodikStreamController),
        "BuildTranslationOptions",
        (IEnumerable<string>)new[] { "AnimeVost", "AniStar", "AniMaunt" },
        (IEnumerable<string>)new[] { "AnimeVost", "СВ-Дубль" });

    var names = ((System.Collections.IEnumerable)options)
        .Cast<object>()
        .Select(x => GetProperty<string>(x, "Name"))
        .ToArray();

    AssertEqual(
        "AnimeVost,AniStar,AniMaunt,СВ-Дубль",
        string.Join(",", names),
        "The widget should append real managed versions missing from the provider catalog without duplicating existing voices.");
}

static void YummyKodikStreamController_MirrorsWidgetVoiceAcrossMixedProviderKeys()
{
    var controllerType = typeof(YummyKodik.Api.YummyKodikStreamController);
    var cfg = new PluginConfiguration();
    var firstUser = Guid.NewGuid();
    var secondUser = Guid.NewGuid();
    var keys = new[] { "yummy:12852", "alloha:12852", "cvh:12852", "shikimori:56215" };

    foreach (var key in keys)
    {
        cfg.SetSeriesPreferredTranslationId(key, "AnimeVost");
    }

    cfg.SetUserSeriesPreferredTranslationId(firstUser, "cvh:12852", "AniStar");
    cfg.SetUserSeriesPreferredTranslationId(secondUser, "shikimori:56215", "729");

    var changed = InvokeStatic<bool>(
        controllerType,
        "SetLibraryWideTranslationPreference",
        cfg,
        (IEnumerable<string>)keys,
        "СВ-Дубль");

    AssertTrue(changed, "Widget selection should update the mixed-provider preference group.");
    foreach (var key in keys)
    {
        AssertEqual(
            "СВ-Дубль",
            cfg.GetSeriesPreferredTranslationId(key) ?? string.Empty,
            "Every provider key should carry the same library-wide widget voice.");
    }

    AssertFalse(
        cfg.UserSeriesPreferredTranslations.Any(x => keys.Contains(x.SeriesKey, StringComparer.OrdinalIgnoreCase)),
        "Stale per-user values for the same library-global primary must be removed.");

    changed = InvokeStatic<bool>(
        controllerType,
        "SetLibraryWideTranslationPreference",
        cfg,
        (IEnumerable<string>)keys,
        string.Empty);

    AssertTrue(changed, "Auto should clear the library-wide voice lock.");
    foreach (var key in keys)
    {
        AssertEqual(
            string.Empty,
            cfg.GetSeriesPreferredTranslationId(key) ?? string.Empty,
            "Auto should clear every provider key in the mixed group.");
    }
}

static void YummyKodikStreamController_ExplicitPerVoiceSourceOverridesSavedDefault()
{
    var controllerType = typeof(YummyKodik.Api.YummyKodikStreamController);

    var cvhVoice = InvokeStatic<string>(
        controllerType,
        "PickGatewayVoice",
        "Dream Cast",
        "AnimeVost");
    AssertEqual(
        "Dream Cast",
        cvhVoice,
        "An explicit per-voice CVH/Alloha STRM must override a stale saved default.");

    var translations = new[]
    {
        new KodikTranslation { Id = "923", Name = "AnimeVost", Type = "voice", AvailableEpisodes = new[] { 7 } },
        new KodikTranslation { Id = "1978", Name = "Dream Cast", Type = "voice", AvailableEpisodes = new[] { 7 } }
    };
    var kodikSelection = KodikPlaybackSelector.PickTranslationForPlayback(
        translations,
        Array.Empty<string>(),
        savedTranslationId: "923",
        explicitTranslationId: "1978",
        episode: 7);
    AssertEqual(
        "1978",
        kodikSelection.TranslationId,
        "An explicit per-voice Kodik STRM must override a stale saved default.");
    AssertEqual("explicit", kodikSelection.Reason, "Kodik selection should report explicit-source precedence.");

    var defaultVoice = InvokeStatic<string>(
        controllerType,
        "PickGatewayVoice",
        string.Empty,
        "AnimeVost");
    AssertEqual("AnimeVost", defaultVoice, "A saved voice should remain the default when a source has no explicit voice.");
}

static void KodikPlaybackSelector_ResolvesSavedVoiceNameToTranslationId()
{
    var translations = new[]
    {
        new KodikTranslation
        {
            Id = "729",
            Type = "voice",
            Name = "СВ-Дубль",
            AvailableEpisodes = new[] { 41, 42 }
        },
        new KodikTranslation
        {
            Id = "825",
            Type = "voice",
            Name = "AniMaunt",
            AvailableEpisodes = new[] { 41, 42 }
        }
    };

    var selection = KodikPlaybackSelector.PickTranslationForPlayback(
        translations,
        Array.Empty<string>(),
        "СВ-Дубль",
        string.Empty,
        42);

    AssertEqual("729", selection.TranslationId, "A canonical widget voice name should resolve back to the Kodik provider id.");
    AssertEqual("saved-name", selection.Reason, "Kodik selection should report the voice-name compatibility path.");
}

static void EpisodeVersionsMerge_UsesSavedYummyVoicePreferenceForPrimary()
{
    var serviceType = typeof(YummyKodik.Versioning.YummyKodikEpisodeVersionsMergeHostedService);
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

    try
    {
        Directory.CreateDirectory(tempRoot);
        var dreamPath = Path.Combine(tempRoot, "S01E01 - Dream Cast.strm");
        var aniPath = Path.Combine(tempRoot, "S01E01 - AniLibria.strm");
        File.WriteAllText(dreamPath, "http://localhost:8096/YummyKodik/stream?provider=alloha&animeId=21008&ep=1&voice=Dream%20Cast");
        File.WriteAllText(aniPath, "http://localhost:8096/YummyKodik/stream?provider=alloha&animeId=21008&ep=1&voice=AniLibria");

        var cfg = new PluginConfiguration();
        var userId = Guid.NewGuid();
        AssertTrue(
            cfg.SetUserSeriesPreferredTranslationId(userId, "yummy:21008", "Dream Cast"),
            "Saved Yummy voice should be stored for the shared series key.");

        var tokens = InvokeStatic<string[]>(
            serviceType,
            "BuildSavedPreferenceTokensForPaths",
            (IEnumerable<string>)new[] { dreamPath, aniPath },
            cfg);

        AssertEqual("Dream Cast", string.Join(",", tokens), "Version merge should find the saved Yummy voice for this group.");

        var needleSafe = InvokeStatic<string>(serviceType, "NormalizeTokenForFilename", "Dream Cast");
        var matchesDream = InvokeStatic<bool>(serviceType, "PathMatchesPreferredToken", dreamPath, "Dream Cast", needleSafe);
        var matchesAni = InvokeStatic<bool>(serviceType, "PathMatchesPreferredToken", aniPath, "Dream Cast", needleSafe);

        AssertTrue(matchesDream, "Saved Yummy voice should match the corresponding voice STRM.");
        AssertFalse(matchesAni, "Saved Yummy voice should not match a different voice STRM.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void EpisodeVersionsMerge_MatchesSavedKodikTranslationIdFromStrm()
{
    var serviceType = typeof(YummyKodik.Versioning.YummyKodikEpisodeVersionsMergeHostedService);
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

    try
    {
        Directory.CreateDirectory(tempRoot);
        var kodikPath = Path.Combine(tempRoot, "S01E01 - AniLibria.strm");
        var otherPath = Path.Combine(tempRoot, "S01E01 - Other.strm");
        File.WriteAllText(kodikPath, "http://localhost:8096/YummyKodik/stream?type=shikimori&id=52991&ep=1&tr=610");
        File.WriteAllText(otherPath, "http://localhost:8096/YummyKodik/stream?type=shikimori&id=52991&ep=1&tr=611");

        var cfg = new PluginConfiguration();
        var userId = Guid.NewGuid();
        AssertTrue(
            cfg.SetUserSeriesPreferredTranslationId(userId, "shikimori:52991", "610"),
            "Saved Kodik translation id should be stored for the series key.");

        var tokens = InvokeStatic<string[]>(
            serviceType,
            "BuildSavedPreferenceTokensForPaths",
            (IEnumerable<string>)new[] { kodikPath, otherPath },
            cfg);

        AssertEqual("610", string.Join(",", tokens), "Version merge should find the saved Kodik translation id for this group.");

        var matchesSaved = InvokeStatic<bool>(serviceType, "PathMatchesPreferredToken", kodikPath, "610", "610");
        var matchesOther = InvokeStatic<bool>(serviceType, "PathMatchesPreferredToken", otherPath, "610", "610");

        AssertTrue(matchesSaved, "Saved Kodik translation id should match the STRM tr query.");
        AssertFalse(matchesOther, "Saved Kodik translation id should not match another tr query.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void EpisodeVersionsMerge_ComparesJellyfin12LinksByItemId()
{
    var serviceType = typeof(YummyKodik.Versioning.YummyKodikEpisodeVersionsMergeHostedService);
    var firstId = Guid.NewGuid();
    var secondId = Guid.NewGuid();
    var existing = new[]
    {
        new LinkedChild { ItemId = firstId, Type = LinkedChildType.LinkedAlternateVersion },
        new LinkedChild { ItemId = secondId, Type = LinkedChildType.LinkedAlternateVersion }
    };
    var reordered = new[]
    {
        new LinkedChild { ItemId = secondId, Type = LinkedChildType.LinkedAlternateVersion },
        new LinkedChild { ItemId = firstId, Type = LinkedChildType.LinkedAlternateVersion }
    };
    var different = new[]
    {
        new LinkedChild { ItemId = firstId, Type = LinkedChildType.LinkedAlternateVersion },
        new LinkedChild { ItemId = Guid.NewGuid(), Type = LinkedChildType.LinkedAlternateVersion }
    };

    AssertTrue(
        InvokeStatic<bool>(serviceType, "LinkedChildrenSetEquals", existing, reordered),
        "Jellyfin 12 linked children should compare by ItemId regardless of order.");
    AssertFalse(
        InvokeStatic<bool>(serviceType, "LinkedChildrenSetEquals", existing, different),
        "Different Jellyfin 12 linked child ids must not compare as the same version set.");
}

static void PostRefreshMergeBarrier_MissingOrUnindexedEpisodeIsUnresolved()
{
    var refreshStartedUtc = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
    var artifact = new YummyKodik.Versioning.ExpectedEpisodeArtifact(
        @"C:\YummyKodik\Series\Season 01\S01E01.strm",
        refreshStartedUtc.AddSeconds(4));

    var missing = YummyKodik.Versioning.YummyKodikPostRefreshMergeBarrier.EvaluatePostRefreshReadiness(
        new[] { artifact },
        Array.Empty<YummyKodik.Versioning.EpisodeReadinessSnapshot>(),
        refreshStartedUtc);
    AssertFalse(missing.IsReady, "A refreshed STRM without a Jellyfin Episode must keep the post-refresh merge barrier unresolved.");
    AssertEqual(1, missing.UnresolvedPaths.Count, "Missing Episode should be reported as one unresolved artifact.");

    var unindexed = YummyKodik.Versioning.YummyKodikPostRefreshMergeBarrier.EvaluatePostRefreshReadiness(
        new[] { artifact },
        new[]
        {
            new YummyKodik.Versioning.EpisodeReadinessSnapshot(
                artifact.Path,
                IndexNumber: null,
                DateCreatedUtc: refreshStartedUtc,
                DateLastRefreshedUtc: refreshStartedUtc.AddSeconds(5))
        },
        refreshStartedUtc);
    AssertFalse(unindexed.IsReady, "An Episode without IndexNumber must not release the post-refresh merge barrier.");
    AssertEqual(1, unindexed.UnresolvedPaths.Count, "Unindexed Episode should be reported as unresolved.");
}

static void PostRefreshMergeBarrier_NewEpisodeBeforeArtifactRefreshIsUnresolved()
{
    var refreshStartedUtc = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
    var artifact = new YummyKodik.Versioning.ExpectedEpisodeArtifact(
        @"C:\YummyKodik\Series\Season 01\S01E01.strm",
        refreshStartedUtc.AddSeconds(10));

    var readiness = YummyKodik.Versioning.YummyKodikPostRefreshMergeBarrier.EvaluatePostRefreshReadiness(
        new[] { artifact },
        new[]
        {
            new YummyKodik.Versioning.EpisodeReadinessSnapshot(
                artifact.Path,
                IndexNumber: 1,
                DateCreatedUtc: refreshStartedUtc.AddSeconds(1),
                DateLastRefreshedUtc: refreshStartedUtc.AddSeconds(5))
        },
        refreshStartedUtc);

    AssertFalse(readiness.IsReady, "A newly materialized Episode whose refresh predates the generated STRM/NFO timestamp must remain unresolved.");
    AssertEqual(1, readiness.UnresolvedPaths.Count, "Early refreshed Episode should keep its artifact unresolved.");
}

static void PostRefreshMergeBarrier_LateRefreshedEpisodeIsReady()
{
    var refreshStartedUtc = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
    var artifact = new YummyKodik.Versioning.ExpectedEpisodeArtifact(
        @"C:\YummyKodik\Series\Season 01\S01E01.strm",
        refreshStartedUtc.AddSeconds(10));

    var readiness = YummyKodik.Versioning.YummyKodikPostRefreshMergeBarrier.EvaluatePostRefreshReadiness(
        new[] { artifact },
        new[]
        {
            new YummyKodik.Versioning.EpisodeReadinessSnapshot(
                artifact.Path,
                IndexNumber: 1,
                DateCreatedUtc: refreshStartedUtc.AddSeconds(1),
                DateLastRefreshedUtc: refreshStartedUtc.AddSeconds(12))
        },
        refreshStartedUtc);

    AssertTrue(readiness.IsReady, "The late Episode refresh after generated artifact timestamps must release the barrier for one final merge.");
    AssertEqual(0, readiness.UnresolvedPaths.Count, "Ready Episode should leave no unresolved artifact paths.");
}

static void PostRefreshMergeBarrier_PreExistingEpisodeDoesNotBlockReadiness()
{
    var refreshStartedUtc = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
    var artifact = new YummyKodik.Versioning.ExpectedEpisodeArtifact(
        @"C:\YummyKodik\Series\Season 01\S01E01.strm",
        refreshStartedUtc.AddSeconds(10));

    var readiness = YummyKodik.Versioning.YummyKodikPostRefreshMergeBarrier.EvaluatePostRefreshReadiness(
        new[] { artifact },
        new[]
        {
            new YummyKodik.Versioning.EpisodeReadinessSnapshot(
                artifact.Path,
                IndexNumber: 1,
                DateCreatedUtc: refreshStartedUtc.AddMinutes(-30),
                DateLastRefreshedUtc: refreshStartedUtc.AddMinutes(-30))
        },
        refreshStartedUtc);

    AssertTrue(readiness.IsReady, "A pre-existing indexed Episode must not block the post-refresh merge barrier merely because its prior refresh timestamp is old.");
}

static void PostRefreshMergeBarrier_DeletedEpisodeMustDisappear()
{
    var refreshStartedUtc = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
    const string deletedPath = @"C:\YummyKodik\Series\Season 01\S01E01 - Removed.strm";
    var staleSnapshot = new YummyKodik.Versioning.EpisodeReadinessSnapshot(
        deletedPath,
        IndexNumber: 1,
        DateCreatedUtc: refreshStartedUtc.AddDays(-1),
        DateLastRefreshedUtc: refreshStartedUtc.AddDays(-1));

    var stillPresent = YummyKodik.Versioning.YummyKodikPostRefreshMergeBarrier.EvaluatePostRefreshReadiness(
        Array.Empty<YummyKodik.Versioning.ExpectedEpisodeArtifact>(),
        new[] { staleSnapshot },
        refreshStartedUtc,
        new[] { deletedPath });
    AssertFalse(stillPresent.IsReady, "A deleted STRM still present in Jellyfin must keep the final merge barrier unresolved.");

    var removed = YummyKodik.Versioning.YummyKodikPostRefreshMergeBarrier.EvaluatePostRefreshReadiness(
        Array.Empty<YummyKodik.Versioning.ExpectedEpisodeArtifact>(),
        Array.Empty<YummyKodik.Versioning.EpisodeReadinessSnapshot>(),
        refreshStartedUtc,
        new[] { deletedPath });
    AssertTrue(removed.IsReady, "The barrier should release after Jellyfin removes the deleted Episode item.");
}

static void YummyKodikStreamController_FindsKodikFallbackVoiceByAlias()
{
    var method = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
        "FindKodikTranslationByVoiceName",
        BindingFlags.NonPublic | BindingFlags.Static);
    AssertTrue(method is not null, "Kodik fallback voice matcher should exist.");

    var translations = new[]
    {
        new KodikTranslation
        {
            Id = "10",
            Type = "voice",
            Name = "AniLiberty (AniLibria)",
            AvailableEpisodes = new[] { 4 }
        },
        new KodikTranslation
        {
            Id = "11",
            Type = "voice",
            Name = "Other",
            AvailableEpisodes = new[] { 4 }
        }
    };

    var match = (KodikTranslation?)method!.Invoke(null, new object[] { translations, "AnilibriaTV", 4 });
    AssertEqual("10", match?.Id ?? string.Empty, "Kodik fallback should use the same cross-provider voice alias matching as Yummy providers.");
}

static void YummyKodikStreamController_KodikFallbackUsesDefaultWhenRequestedVoiceMissing()
{
    var method = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
        "PickKodikFallbackSelection",
        BindingFlags.NonPublic | BindingFlags.Static);
    AssertTrue(method is not null, "Kodik fallback selector should exist.");

    var translations = new[]
    {
        new KodikTranslation
        {
            Id = "610",
            Type = "voice",
            Name = "Dream Cast",
            AvailableEpisodes = new[] { 12 }
        }
    };

    var selection = method!.Invoke(
        null,
        new object[] { translations, Array.Empty<string>(), null!, "AniLibria", string.Empty, 12 });
    if (selection == null)
    {
        throw new InvalidOperationException("Kodik fallback should select an available translation when the requested Yummy voice is missing.");
    }

    var translationId = selection.GetType().GetProperty("TranslationId")?.GetValue(selection)?.ToString() ?? string.Empty;
    var reason = selection.GetType().GetProperty("Reason")?.GetValue(selection)?.ToString() ?? string.Empty;

    AssertEqual("610", translationId, "Missing cross-provider voice match should not block fallback to an available Kodik translation.");
    AssertTrue(!string.Equals("fallback-explicit-voice", reason, StringComparison.Ordinal), "Fallback reason should not claim an explicit voice match when none exists.");
}

static void AllohaPlaybackService_RewritesManifestUrisToProxyUrls()
{
    const string proxyBaseUrl = "/base/YummyKodik/alloha-proxy";
    var session = new AllohaPlaybackSession
    {
        SessionId = "session-1",
        ManifestUrl = "https://stream-balancer-alloha.example/serial/master.m3u8",
        ManifestText = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",NAME="AniStar",URI="index-f1-a1.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=5309735
            index-f1-v1.m3u8
            """,
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
    };

    var manifestBody = AllohaPlaybackService.BuildManifestResponseBody(session, proxyBaseUrl);

    AssertTrue(manifestBody.Contains($"{proxyBaseUrl}/", StringComparison.Ordinal), "Master manifest should retain the Jellyfin base path in root-relative proxy urls.");
    AssertFalse(manifestBody.Contains("://", StringComparison.Ordinal), "Alloha master manifest proxy urls must remain root-relative.");
    AssertTrue(manifestBody.Contains(".m3u8?sessionId=session-1&resource=", StringComparison.Ordinal), "Nested Alloha playlists should keep a playlist extension in proxy urls.");
    AssertTrue(!manifestBody.Contains("URI=\"index-f1-a1.m3u8\"", StringComparison.Ordinal), "Directive URI attributes should not keep raw relative Alloha urls.");
    AssertTrue(!manifestBody.Contains("\nindex-f1-v1.m3u8", StringComparison.Ordinal), "Variant playlist lines should not keep raw relative Alloha urls.");
    AssertTrue(session.ProxyResources.Values.Contains("https://stream-balancer-alloha.example/serial/index-f1-a1.m3u8"), "Audio playlist should be registered as a proxy resource.");
    AssertTrue(session.ProxyResources.Values.Contains("https://stream-balancer-alloha.example/serial/index-f1-v1.m3u8"), "Variant playlist should be registered as a proxy resource.");
}

static void AllohaPlaybackService_DownloadProxyResourceRewritesNestedManifest()
{
    var requests = new List<HttpRequestMessage>();
    const string resourceId = "resource-1";
    const string parentManifestUrl = "https://stream-balancer-alloha.example/serial/master.m3u8";
    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                #EXTM3U
                #EXT-X-MAP:URI="init.mp4"
                #EXTINF:3.003,
                segment-001.m4s
                """)
        };
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var session = new AllohaPlaybackSession
    {
        SessionId = "session-2",
        RefererUrl = "https://alloha.yani.tv/?token_movie=demo",
        RequiredHttpHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accepts-Controls"] = "9badb2c5dd28e9cd0bed84e7391523d9d308a48b690428dae6049233218645d9",
            ["Authorizations"] = "Bearer test-guard-token",
            ["Origin"] = "https://alloha.yani.tv",
            ["Referer"] = "https://alloha.yani.tv/?token_movie=demo"
        },
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
    };
    session.ProxyResources[resourceId] = "https://stream-balancer-alloha.example/serial/index-f1-v1.m3u8";
    session.ProxyResourceReferers[resourceId] = parentManifestUrl;

    var resource = service.DownloadProxyResourceAsync(
            session,
            resourceId,
            "https://stream-balancer-alloha.example/serial/index-f1-v1.m3u8",
            "http://localhost:8096/YummyKodik/alloha-proxy",
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    var manifestBody = System.Text.Encoding.UTF8.GetString(resource.Content);
    AssertEqual("application/vnd.apple.mpegurl", resource.ContentType, "Nested Alloha playlists should be served back as HLS manifests.");
    AssertTrue(manifestBody.Contains("http://localhost:8096/YummyKodik/alloha-proxy/", StringComparison.Ordinal), "Nested Alloha playlists should be rewritten to local proxy urls.");
    AssertTrue(manifestBody.Contains(".m4s?sessionId=session-2&resource=", StringComparison.Ordinal), "Nested Alloha segments should keep a playable media extension in proxy urls.");
    AssertTrue(session.ProxyResources.Values.Contains("https://stream-balancer-alloha.example/serial/init.mp4"), "Nested manifest map URI should be registered as a proxy resource.");
    AssertTrue(session.ProxyResources.Values.Contains("https://stream-balancer-alloha.example/serial/segment-001.m4s"), "Nested manifest segment should be registered as a proxy resource.");

    var request = requests.Single();
    AssertEqual("application/vnd.apple.mpegurl, application/x-mpegURL, */*", string.Join(", ", request.Headers.GetValues("Accept")), "Nested Alloha playlists should keep an HLS manifest Accept header.");
    AssertEqual("9badb2c5dd28e9cd0bed84e7391523d9d308a48b690428dae6049233218645d9", request.Headers.GetValues("Accepts-Controls").Single(), "Proxy resource request should keep Alloha Accepts-Controls.");
    AssertEqual("Bearer test-guard-token", request.Headers.GetValues("Authorizations").Single(), "Proxy resource request should keep the Alloha guard token.");
    AssertEqual("https://alloha.yani.tv/?token_movie=demo", request.Headers.Referrer!.AbsoluteUri, "Nested Alloha playlists should keep the iframe page as referer.");
    AssertEqual("https://alloha.yani.tv", string.Join(", ", request.Headers.GetValues("Origin")), "Nested Alloha playlists should keep the iframe page origin.");
}

static void AllohaPlaybackService_BuffersUpcomingMediaSegments()
{
    const string proxyBaseUrl = "http://localhost:8096/YummyKodik/alloha-proxy";
    const string playlistResourceId = "playlist-buffer";
    const string parentManifestUrl = "https://stream-balancer-alloha.example/serial/master.m3u8";
    const string playlistUrl = "https://stream-balancer-alloha.example/serial/index-f1-v1.m3u8";
    const string segment1Url = "https://stream-balancer-alloha.example/serial/segment-001.m4s";
    const string segment2Url = "https://stream-balancer-alloha.example/serial/segment-002.m4s";
    const string segment3Url = "https://stream-balancer-alloha.example/serial/segment-003.m4s";
    const string segment4Url = "https://stream-balancer-alloha.example/serial/segment-004.m4s";
    var segmentBodies = new Dictionary<string, byte[]>(StringComparer.Ordinal)
    {
        [segment1Url] = new byte[] { 1, 1, 1 },
        [segment2Url] = new byte[] { 2, 2, 2 },
        [segment3Url] = new byte[] { 3, 3, 3 },
        [segment4Url] = new byte[] { 4, 4, 4 }
    };
    var segmentRequestCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

    var handler = new DelegatingTestHandler(request =>
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (request.Method == HttpMethod.Get && url == playlistUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-TARGETDURATION:60
                    #EXTINF:60.000,
                    segment-001.m4s
                    #EXTINF:60.000,
                    segment-002.m4s
                    #EXTINF:60.000,
                    segment-003.m4s
                    #EXTINF:60.000,
                    segment-004.m4s
                    #EXT-X-ENDLIST
                    """)
            };
        }

        if (request.Method == HttpMethod.Get && segmentBodies.TryGetValue(url, out var body))
        {
            var requestCount = segmentRequestCounts.AddOrUpdate(url, 1, (_, count) => count + 1);
            if (requestCount > 1)
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("<html><body>403 Forbidden</body></html>")
                };
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body)
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("video/iso.segment");
            return response;
        }

        throw new InvalidOperationException("Unexpected Alloha buffer test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var session = new AllohaPlaybackSession
    {
        SessionId = "session-buffer-1",
        ManifestUrl = parentManifestUrl,
        RefererUrl = "https://alloha.yani.tv/?token_movie=demo",
        RequiredHttpHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accepts-Controls"] = "9badb2c5dd28e9cd0bed84e7391523d9d308a48b690428dae6049233218645d9",
            ["Authorizations"] = "Bearer test-guard-token",
            ["Origin"] = "https://alloha.yani.tv",
            ["Referer"] = "https://alloha.yani.tv/?token_movie=demo"
        },
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
    };
    session.ProxyResources[playlistResourceId] = playlistUrl;
    session.ProxyResourceReferers[playlistResourceId] = parentManifestUrl;

    _ = service.DownloadProxyResourceAsync(
            session,
            playlistResourceId,
            playlistUrl,
            proxyBaseUrl,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    var segment1ResourceId = session.ProxyResources.Single(x => x.Value == segment1Url).Key;
    var segment2ResourceId = session.ProxyResources.Single(x => x.Value == segment2Url).Key;
    var segment3ResourceId = session.ProxyResources.Single(x => x.Value == segment3Url).Key;

    var firstSegment = service.DownloadProxyResourceAsync(
            session,
            segment1ResourceId,
            segment1Url,
            proxyBaseUrl,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual("video/iso.segment", firstSegment.ContentType, "Foreground Alloha segment should keep the upstream media type.");
    AssertEqual("1,1,1", string.Join(",", firstSegment.Content), "Foreground Alloha segment should return the upstream bytes.");

    AssertTrue(
        WaitUntil(() =>
            session.BufferedProxyResources.ContainsKey(segment2ResourceId) &&
            session.BufferedProxyResources.ContainsKey(segment3ResourceId),
            timeoutMs: 3000),
        "Alloha proxy should prefetch roughly two minutes of upcoming media segments.");

    AssertEqual(0, GetRequestCount(segment4Url), "Initial Alloha prefetch should stop near the two-minute target instead of fetching the whole playlist.");

    var segment2RequestsBefore = GetRequestCount(segment2Url);
    var secondSegment = service.DownloadProxyResourceAsync(
            session,
            segment2ResourceId,
            segment2Url,
            proxyBaseUrl,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual("2,2,2", string.Join(",", secondSegment.Content), "Prefetched Alloha segment should be served from the local buffer.");
    AssertEqual(segment2RequestsBefore, GetRequestCount(segment2Url), "Buffered Alloha segment should not hit upstream again.");

    int GetRequestCount(string url)
    {
        return segmentRequestCounts.TryGetValue(url, out var count) ? count : 0;
    }
}

static void AllohaPlaybackService_PrefetchSurvivesCompletedSegmentRequest()
{
    const string proxyBaseUrl = "http://localhost:8096/YummyKodik/alloha-proxy";
    const string playlistResourceId = "playlist-session-prefetch";
    const string parentManifestUrl = "https://stream-balancer-alloha.example/serial/master.m3u8";
    const string playlistUrl = "https://stream-balancer-alloha.example/serial/index-f1-v1.m3u8";
    const string segment1Url = "https://stream-balancer-alloha.example/serial/segment-001.m4s";
    const string segment2Url = "https://stream-balancer-alloha.example/serial/segment-002.m4s";
    const string segment3Url = "https://stream-balancer-alloha.example/serial/segment-003.m4s";
    var prefetchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var releasePrefetch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    using var http = new HttpClient(new AsyncDelegatingTestHandler(async (request, cancellationToken) =>
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (request.Method == HttpMethod.Get && url == playlistUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-TARGETDURATION:60
                    #EXTINF:60.000,
                    segment-001.m4s
                    #EXTINF:60.000,
                    segment-002.m4s
                    #EXTINF:60.000,
                    segment-003.m4s
                    #EXT-X-ENDLIST
                    """)
            };
        }

        if (request.Method == HttpMethod.Get && url == segment1Url)
        {
            return CreateSegmentResponse(1);
        }

        if (request.Method == HttpMethod.Get && (url == segment2Url || url == segment3Url))
        {
            prefetchStarted.TrySetResult(true);
            await releasePrefetch.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return CreateSegmentResponse((byte)(url == segment2Url ? 2 : 3));
        }

        throw new InvalidOperationException("Unexpected Alloha session-prefetch test request: " + request.RequestUri);
    }));

    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var session = new AllohaPlaybackSession
    {
        SessionId = "session-prefetch-request-lifetime",
        ManifestUrl = parentManifestUrl,
        RefererUrl = "https://alloha.yani.tv/?token_movie=demo",
        RequiredHttpHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
    };
    session.ProxyResources[playlistResourceId] = playlistUrl;
    session.ProxyResourceReferers[playlistResourceId] = parentManifestUrl;

    _ = service.DownloadProxyResourceAsync(
            session,
            playlistResourceId,
            playlistUrl,
            proxyBaseUrl,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    var segment1ResourceId = session.ProxyResources.Single(x => x.Value == segment1Url).Key;
    var segment2ResourceId = session.ProxyResources.Single(x => x.Value == segment2Url).Key;
    var segment3ResourceId = session.ProxyResources.Single(x => x.Value == segment3Url).Key;

    using var requestCancellation = new CancellationTokenSource();
    _ = service.DownloadProxyResourceAsync(
            session,
            segment1ResourceId,
            segment1Url,
            proxyBaseUrl,
            requestCancellation.Token)
        .GetAwaiter()
        .GetResult();

    AssertTrue(prefetchStarted.Task.Wait(1000), "Alloha should start background prefetch after serving the foreground segment.");
    requestCancellation.Cancel();
    releasePrefetch.TrySetResult(true);

    AssertTrue(
        WaitUntil(
            () => session.BufferedProxyResources.ContainsKey(segment2ResourceId) &&
                  session.BufferedProxyResources.ContainsKey(segment3ResourceId),
            timeoutMs: 3000),
        "Alloha prefetch should remain session-scoped after the foreground HTTP request is complete.");

    static HttpResponseMessage CreateSegmentResponse(byte marker)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new[] { marker, marker, marker })
        };
        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("video/iso.segment");
        return response;
    }
}

static void AllohaPlaybackService_DownloadProxyResourceRefreshesSessionAfter403()
{
    var requests = new List<HttpRequestMessage>();
    const string proxyBaseUrl = "http://localhost:8096/YummyKodik/alloha-proxy";
    const string iframeUrl = "https://alloha.yani.tv/?token_movie=movie-token&translation=222&season=1&episode=3&token=req-token&hidden=translation,season,episode";
    const string oldMasterUrl = "https://stream-balancer-alloha-old.example/serial/master.m3u8";
    const string oldNestedUrl = "https://stream-balancer-alloha-old.example/serial/index-f1-v1.m3u8";
    const string newMasterUrl = "https://stream-balancer-alloha-fresh.example/serial/master.m3u8";
    const string newNestedUrl = "https://stream-balancer-alloha-fresh.example/serial/index-f1-v1.m3u8";
    var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";

    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));
        var url = request.RequestUri!.AbsoluteUri;

        if (request.Method == HttpMethod.Get && url == oldNestedUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<HTML>Error</HTML>")
            };
        }

        if (request.Method == HttpMethod.Get &&
            url.StartsWith("https://alloha.yani.tv/?token_movie=movie-token", StringComparison.Ordinal))
        {
            var iframeHtml =
                "<!DOCTYPE html>\n" +
                "<html>\n" +
                "<head>\n" +
                $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                "</head>\n" +
                "<body>\n" +
                "<script>\n" +
                "const fileList = JSON.parse('{\"active\":{\"id\":1191328,\"seasons\":1,\"episode\":3,\"id_translation\":222},\"all\":{\"t222\":{\"file\":{\"1\":{\"3\":{\"id\":1191328}}}}}}');\n" +
                "</script>\n" +
                "</body>\n" +
                "</html>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(iframeHtml)
            };
        }

        if (request.Method == HttpMethod.Post &&
            url == "https://alloha.yani.tv/bnsi/movies/1191328")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {
                      "hlsSource": [
                        {
                          "label": "РуАниме / DEEP",
                          "audioId": "1",
                          "quality": {
                            "1080": "{{newMasterUrl}}"
                          }
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.Method == HttpMethod.Get && url == newMasterUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-STREAM-INF:BANDWIDTH=5309735
                    index-f1-v1.m3u8
                    """)
            };
        }

        if (request.Method == HttpMethod.Get && url == newNestedUrl)
        {
            AssertEqual(iframeUrl, request.Headers.Referrer!.AbsoluteUri, "Refreshed nested playlist should keep the Alloha iframe page as referer.");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXTINF:3.003,
                    segment-001.m4s
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected Alloha refresh test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var session = new AllohaPlaybackSession
    {
        SessionId = "session-refresh-1",
        ManifestUrl = oldMasterUrl,
        ManifestText = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=5309735
            index-f1-v1.m3u8
            """,
        RefererUrl = iframeUrl,
        RequiredHttpHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accepts-Controls"] = "9badb2c5dd28e9cd0bed84e7391523d9d308a48b690428dae6049233218645d9",
            ["Authorizations"] = "Bearer test-guard-token",
            ["Origin"] = "https://alloha.yani.tv",
            ["Referer"] = iframeUrl
        },
        IframeUrl = iframeUrl,
        SelectedVoiceName = "РуАниме / DEEP",
        SelectedQuality = 1080,
        Source = new YummyAllohaSource
        {
            MovieToken = "movie-token",
            RequestToken = "req-token",
            TranslationId = 222,
            SeasonNumber = 1,
            EpisodeNumber = 3,
            Hidden = "translation,season,episode",
            RefererUrl = iframeUrl
        },
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
    };

    AllohaPlaybackService.BuildManifestResponseBody(session, proxyBaseUrl);
    var resourceId = session.ProxyResources.Single(x => x.Value == oldNestedUrl).Key;

    var resource = service.DownloadProxyResourceAsync(
            session,
            resourceId,
            oldNestedUrl,
            proxyBaseUrl,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    var manifestBody = System.Text.Encoding.UTF8.GetString(resource.Content);
    AssertEqual("application/vnd.apple.mpegurl", resource.ContentType, "Refreshed nested playlist should still be served as an HLS manifest.");
    AssertEqual(newMasterUrl, session.ManifestUrl, "403 recovery should refresh the session manifest url.");
    AssertEqual(newNestedUrl, session.ProxyResources[resourceId], "Old proxy resource id should be rebound to the refreshed nested playlist url.");
    AssertEqual(newMasterUrl, session.ProxyResourceReferers[resourceId], "Rebound nested playlist should keep the refreshed master manifest as referer.");
    AssertTrue(manifestBody.Contains(".m4s?sessionId=session-refresh-1&resource=", StringComparison.Ordinal), "Refreshed nested manifest should still be rewritten to local proxy segment urls.");

    var requestUrls = requests.Select(x => x.RequestUri!.AbsoluteUri).ToArray();
    AssertEqual(oldNestedUrl, requestUrls[0], "Initial attempt should hit the stale nested playlist first.");
    AssertTrue(requestUrls.Contains(newMasterUrl), "403 recovery should re-resolve a fresh master manifest.");
    AssertEqual(newNestedUrl, requestUrls[^1], "Retry should hit the rebound nested playlist url after refresh.");
}

static void AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter403()
{
    AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter(
        HttpStatusCode.Forbidden,
        "<HTML>Error</HTML>",
        "403");
}

static void AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfterAlloha500()
{
    AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter(
        HttpStatusCode.InternalServerError,
        """
        <HTML>
        <BODY>
        Description: Could not process this request.
        </BODY>
        </HTML>
        """,
        "Alloha 500");
}

static void AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter502()
{
    AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter(
        HttpStatusCode.BadGateway,
        "Bad Gateway",
        "502");
}

static void AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentAfterNetworkFailure()
{
    AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter(
        HttpStatusCode.OK,
        string.Empty,
        "network failure",
        new HttpRequestException("The upstream connection was reset."));
}

static void AllohaPlaybackService_SuppressesConcurrentFailedRefreshStorm()
{
    const string proxyBaseUrl = "http://localhost:8096/YummyKodik/alloha-proxy";
    const string iframeUrl = "https://alloha.yani.tv/?token_movie=movie-token&translation=222&season=1&episode=3&token=req-token&hidden=translation,season,episode";
    const string oldMasterUrl = "https://stream-balancer-alloha-old.example/serial/master.m3u8";
    const string failedMasterUrl = "https://stream-balancer-alloha-failed.example/serial/master.m3u8";
    const string segment1Url = "https://stream-balancer-alloha-old.example/serial/segment-001.m4s";
    const string segment2Url = "https://stream-balancer-alloha-old.example/serial/segment-002.m4s";
    const string segment3Url = "https://stream-balancer-alloha-old.example/serial/segment-003.m4s";
    var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";
    var bothInitialResourcesStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var initialResourceRequests = 0;
    var iframeRequests = 0;
    var bnsiRequests = 0;
    var failedManifestRequests = 0;

    var handler = new AsyncDelegatingTestHandler(async (request, cancellationToken) =>
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (url is segment1Url or segment2Url or segment3Url)
        {
            if (Interlocked.Increment(ref initialResourceRequests) >= 2)
            {
                bothInitialResourcesStarted.TrySetResult(true);
            }

            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<HTML>Error</HTML>")
            };
        }

        if (request.Method == HttpMethod.Get &&
            url.StartsWith("https://alloha.yani.tv/?token_movie=movie-token", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref iframeRequests);
            await bothInitialResourcesStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            var iframeHtml =
                "<!DOCTYPE html>\n" +
                "<html>\n" +
                "<head>\n" +
                $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                "</head>\n" +
                "<body>\n" +
                "<script>\n" +
                "const fileList = JSON.parse('{\"active\":{\"id\":1191328,\"seasons\":1,\"episode\":3,\"id_translation\":222},\"all\":{\"t222\":{\"file\":{\"1\":{\"3\":{\"id\":1191328}}}}}}');\n" +
                "</script>\n" +
                "</body>\n" +
                "</html>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(iframeHtml)
            };
        }

        if (request.Method == HttpMethod.Post &&
            url == "https://alloha.yani.tv/bnsi/movies/1191328")
        {
            Interlocked.Increment(ref bnsiRequests);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {
                      "hlsSource": [
                        {
                          "label": "РуАниме / DEEP",
                          "audioId": "1",
                          "quality": {
                            "1080": "{{failedMasterUrl}}"
                          }
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.Method == HttpMethod.Get && url == failedMasterUrl)
        {
            Interlocked.Increment(ref failedManifestRequests);
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<HTML>Error</HTML>")
            };
        }

        throw new InvalidOperationException("Unexpected Alloha failed-refresh test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var service = new AllohaPlaybackService(NullLogger<AllohaPlaybackService>.Instance, http);
    var blockedSession = CreateSession("failed-refresh-session");

    var firstFailure = CaptureFailureAsync(service.DownloadProxyResourceAsync(
        blockedSession,
        "segment-1",
        segment1Url,
        proxyBaseUrl,
        CancellationToken.None));
    var secondFailure = CaptureFailureAsync(service.DownloadProxyResourceAsync(
        blockedSession,
        "segment-2",
        segment2Url,
        proxyBaseUrl,
        CancellationToken.None));
    var concurrentFailures = Task.WhenAll(firstFailure, secondFailure).GetAwaiter().GetResult();

    AssertTrue(
        concurrentFailures.All(error => error is InvalidOperationException),
        "Both queued segment requests should surface the failed Alloha recovery.");
    AssertEqual(2, initialResourceRequests, "Both already-started segment requests may observe the original 403.");
    AssertEqual(1, iframeRequests, "Concurrent segment failures should share one Alloha session refresh attempt.");
    AssertEqual(1, bnsiRequests, "Concurrent segment failures should not duplicate Alloha BNSI resolution.");
    AssertEqual(1, failedManifestRequests, "Concurrent segment failures should not duplicate the failing manifest probe.");
    AssertTrue(
        Volatile.Read(ref blockedSession.ProxyResourceRefreshBlockedUntilUtcTicks) > DateTime.UtcNow.Ticks,
        "A failed recovery should start the per-session cooldown.");

    AssertThrows<InvalidOperationException>(() => service.DownloadProxyResourceAsync(
            blockedSession,
            "segment-3",
            segment3Url,
            proxyBaseUrl,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult(),
        "A new segment request in the failed session should fail before repeating upstream work.");

    AssertEqual(2, initialResourceRequests, "Cooldown should reject new segment requests before another upstream resource call.");
    AssertEqual(1, iframeRequests, "Cooldown should suppress another session refresh attempt.");

    var freshSession = CreateSession("fresh-refresh-session");
    AssertThrows<InvalidOperationException>(() => service.DownloadProxyResourceAsync(
            freshSession,
            "segment-3",
            segment3Url,
            proxyBaseUrl,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult(),
        "A fresh playback session should retain its independent recovery attempt.");

    AssertEqual(3, initialResourceRequests, "A fresh session should not inherit another session's failed-resource cooldown.");
    AssertEqual(2, iframeRequests, "A fresh session should be allowed one independent Alloha refresh attempt.");
    AssertEqual(2, bnsiRequests, "A fresh session should independently resolve its Alloha source.");
    AssertEqual(2, failedManifestRequests, "A fresh session should independently probe the resolved manifest.");

    AllohaPlaybackSession CreateSession(string sessionId)
    {
        var session = new AllohaPlaybackSession
        {
            SessionId = sessionId,
            ManifestUrl = oldMasterUrl,
            ManifestText = "#EXTM3U",
            RefererUrl = iframeUrl,
            IframeUrl = iframeUrl,
            SelectedVoiceName = "РуАниме / DEEP",
            SelectedQuality = 1080,
            Source = new YummyAllohaSource
            {
                MovieToken = "movie-token",
                RequestToken = "req-token",
                TranslationId = 222,
                SeasonNumber = 1,
                EpisodeNumber = 3,
                Hidden = "translation,season,episode",
                RefererUrl = iframeUrl
            },
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
        };

        session.ProxyResources["segment-1"] = segment1Url;
        session.ProxyResources["segment-2"] = segment2Url;
        session.ProxyResources["segment-3"] = segment3Url;
        session.ProxyResourceReferers["segment-1"] = oldMasterUrl;
        session.ProxyResourceReferers["segment-2"] = oldMasterUrl;
        session.ProxyResourceReferers["segment-3"] = oldMasterUrl;
        return session;
    }

    static async Task<Exception?> CaptureFailureAsync(Task<AllohaProxyResource> request)
    {
        try
        {
            await request.ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}

static void AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter(
    HttpStatusCode initialStatusCode,
    string initialBody,
    string recoveryLabel,
    Exception? initialException = null)
{
    var requests = new List<HttpRequestMessage>();
    const string proxyBaseUrl = "http://localhost:8096/YummyKodik/alloha-proxy";
    const string iframeUrl = "https://alloha.yani.tv/?token_movie=movie-token&translation=222&season=1&episode=3&token=req-token&hidden=translation,season,episode";
    const string oldMasterUrl = "https://stream-balancer-alloha-old.example/serial/master.m3u8";
    const string oldNestedUrl = "https://stream-balancer-alloha-old.example/serial/index-f1-v1.m3u8";
    const string oldSegmentUrl = "https://stream-balancer-alloha-old.example/serial/segment-001.m4s";
    const string newMasterUrl = "https://stream-balancer-alloha-fresh.example/serial/master.m3u8";
    const string newSegmentUrl = "https://stream-balancer-alloha-fresh.example/serial/segment-001.m4s";
    const string expectedNestedUrl = "https://stream-balancer-alloha-fresh.example/serial/index-f1-v1.m3u8";
    var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";

    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));
        var url = request.RequestUri!.AbsoluteUri;

        if (request.Method == HttpMethod.Get && url == oldSegmentUrl)
        {
            if (initialException != null)
            {
                throw initialException;
            }

            return new HttpResponseMessage(initialStatusCode)
            {
                Content = new StringContent(initialBody)
            };
        }

        if (request.Method == HttpMethod.Get &&
            url.StartsWith("https://alloha.yani.tv/?token_movie=movie-token", StringComparison.Ordinal))
        {
            var iframeHtml =
                "<!DOCTYPE html>\n" +
                "<html>\n" +
                "<head>\n" +
                $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                "</head>\n" +
                "<body>\n" +
                "<script>\n" +
                "const fileList = JSON.parse('{\"active\":{\"id\":1191328,\"seasons\":1,\"episode\":3,\"id_translation\":222},\"all\":{\"t222\":{\"file\":{\"1\":{\"3\":{\"id\":1191328}}}}}}');\n" +
                "</script>\n" +
                "</body>\n" +
                "</html>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(iframeHtml)
            };
        }

        if (request.Method == HttpMethod.Post &&
            url == "https://alloha.yani.tv/bnsi/movies/1191328")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {
                      "hlsSource": [
                        {
                          "label": "РуАниме / DEEP",
                          "audioId": "1",
                          "quality": {
                            "1080": "{{newMasterUrl}}"
                          }
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.Method == HttpMethod.Get && url == newMasterUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-STREAM-INF:BANDWIDTH=5309735
                    index-f1-v1.m3u8
                    """)
            };
        }

        if (request.Method == HttpMethod.Get && url == newSegmentUrl)
        {
            AssertEqual(iframeUrl, request.Headers.Referrer!.AbsoluteUri, "Refreshed segment should keep the Alloha iframe page as referer.");

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp2t");
            return response;
        }

        throw new InvalidOperationException("Unexpected Alloha segment-refresh test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var session = new AllohaPlaybackSession
    {
        SessionId = "session-refresh-2",
        ManifestUrl = oldMasterUrl,
        ManifestText = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=5309735
            index-f1-v1.m3u8
            """,
        RefererUrl = iframeUrl,
        RequiredHttpHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accepts-Controls"] = "9badb2c5dd28e9cd0bed84e7391523d9d308a48b690428dae6049233218645d9",
            ["Authorizations"] = "Bearer test-guard-token",
            ["Origin"] = "https://alloha.yani.tv",
            ["Referer"] = iframeUrl
        },
        IframeUrl = iframeUrl,
        SelectedVoiceName = "РуАниме / DEEP",
        SelectedQuality = 1080,
        Source = new YummyAllohaSource
        {
            MovieToken = "movie-token",
            RequestToken = "req-token",
            TranslationId = 222,
            SeasonNumber = 1,
            EpisodeNumber = 3,
            Hidden = "translation,season,episode",
            RefererUrl = iframeUrl
        },
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
    };

    session.ProxyResources["playlist-old"] = oldNestedUrl;
    session.ProxyResourceReferers["playlist-old"] = oldMasterUrl;
    session.ProxyResourceOriginalReferences["playlist-old"] = "index-f1-v1.m3u8";
    session.ProxyResources["segment-old"] = oldSegmentUrl;
    session.ProxyResourceReferers["segment-old"] = oldNestedUrl;
    session.ProxyResourceOriginalReferences["segment-old"] = "segment-001.m4s";
    session.ProxyResourceParentIds["segment-old"] = "playlist-old";

    var resource = service.DownloadProxyResourceAsync(
            session,
            "segment-old",
            oldSegmentUrl,
            proxyBaseUrl,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual("video/mp2t", resource.ContentType, "Refreshed segment retry should keep the upstream binary media type.");
    AssertEqual(newSegmentUrl, session.ProxyResources["segment-old"], $"Segment {recoveryLabel} recovery should rebind the old segment key to the refreshed segment url.");
    AssertEqual(expectedNestedUrl, session.ProxyResourceReferers["segment-old"], "Segment rebinding should carry the refreshed nested playlist as referer.");
    AssertTrue(!requests.Any(x => x.RequestUri!.AbsoluteUri == expectedNestedUrl), "Parent-chain rebinding should not need to download the refreshed nested playlist just to derive the segment url.");

    var requestUrls = requests.Select(x => x.RequestUri!.AbsoluteUri).ToArray();
    AssertEqual(oldSegmentUrl, requestUrls[0], "Initial segment attempt should hit the stale segment url first.");
    AssertTrue(requestUrls.Contains(newMasterUrl), $"Segment {recoveryLabel} recovery should still refresh the master manifest.");
    AssertEqual(newSegmentUrl, requestUrls[^1], "Segment retry should use the rebound url derived from the refreshed parent chain.");
}

static void YummyKodikStreamUri_ParsesAllohaRequest()
{
    var uri = "http://localhost:8096/YummyKodik/stream?provider=alloha&animeId=19312&ep=1&voice=Dream%20Cast&format=hls";
    var parsed = YummyKodikStreamUri.TryParseRequest(uri, out var request);

    AssertTrue(parsed, "Alloha HTTP uri should be parsed.");
    AssertEqual(YummyStreamProviderKind.Alloha, request.Provider, "Provider kind should be Alloha.");
    AssertEqual(19312L, request.AnimeId, "Anime id should be parsed from query.");
    AssertEqual(1, request.Episode, "Episode should be parsed from query.");
    AssertEqual("Dream Cast", request.VoiceName, "Voice should be parsed from query.");
}

static void YummyKodikStreamUri_BuildsAllohaRequestWithEmbeddedSource()
{
    var source = new YummyAllohaSource
    {
        MovieToken = "movie-token",
        RequestToken = "request-token",
        TranslationId = 215,
        SeasonNumber = 4,
        EpisodeNumber = 1,
        Hidden = "translation,season,episode",
        RefererUrl = "https://alloha.yani.tv/?token_movie=movie-token&translation=215&season=4&episode=1&token=request-token"
    };

    var uri = YummyKodikStreamUri.BuildAllohaHttpUrl(
        "http://localhost:8096",
        animeId: 15066,
        episode: 1,
        voiceName: "AniLibria",
        source: source);

    AssertTrue(uri.Contains("provider=alloha", StringComparison.Ordinal), "Alloha stream url should target the Alloha provider.");
    AssertTrue(uri.Contains("animeId=15066", StringComparison.Ordinal), "Alloha stream url should keep anime id.");
    AssertTrue(uri.Contains("voice=AniLibria", StringComparison.Ordinal), "Alloha stream url should keep the display voice.");
    AssertTrue(uri.Contains("allohaMovieToken=movie-token", StringComparison.Ordinal), "Alloha stream url should embed the movie token.");
    AssertTrue(uri.Contains("allohaRequestToken=request-token", StringComparison.Ordinal), "Alloha stream url should embed the request token.");
    AssertTrue(uri.Contains("allohaTranslationId=215", StringComparison.Ordinal), "Alloha stream url should embed the translation id.");
    AssertTrue(uri.Contains("allohaSeason=4", StringComparison.Ordinal), "Alloha stream url should embed the season number.");
    AssertTrue(uri.Contains("allohaHidden=translation%2Cseason%2Cepisode", StringComparison.Ordinal), "Alloha stream url should embed the hidden flags.");
    AssertTrue(uri.Contains("allohaRefererUrl=", StringComparison.Ordinal), "Alloha stream url should embed the referer url.");
}

static void YummyKodikStreamUri_TrimsTrailingSlashFromProviderBaseUrl()
{
    var cvh = YummyKodikStreamUri.BuildCvhHttpUrl(
        "http://localhost:8099/",
        animeId: 4861,
        episode: 1,
        voiceName: "AniStar");

    var alloha = YummyKodikStreamUri.BuildAllohaHttpUrl(
        "http://localhost:8099/",
        animeId: 19312,
        episode: 1,
        voiceName: "Dream Cast");

    AssertTrue(cvh.StartsWith("http://localhost:8099/YummyKodik/stream?", StringComparison.Ordinal), "CVH url should not contain a double slash before the plugin endpoint.");
    AssertTrue(alloha.StartsWith("http://localhost:8099/YummyKodik/stream?", StringComparison.Ordinal), "Alloha url should not contain a double slash before the plugin endpoint.");
    AssertFalse(cvh.Contains("8099//YummyKodik", StringComparison.Ordinal), "CVH url should trim a trailing base-url slash.");
    AssertFalse(alloha.Contains("8099//YummyKodik", StringComparison.Ordinal), "Alloha url should trim a trailing base-url slash.");
}

static void YummyKodikLogFilter_DefaultsToWarning()
{
    var defaultCfg = new PluginConfiguration();
    AssertFalse(YummyKodikLogFilter.ShouldLog(LogLevel.Information, defaultCfg), "Default plugin logging should suppress informational logs.");
    AssertTrue(YummyKodikLogFilter.ShouldLog(LogLevel.Warning, defaultCfg), "Default plugin logging should keep warnings.");
    AssertTrue(YummyKodikLogFilter.ShouldLog(LogLevel.Error, defaultCfg), "Default plugin logging should keep errors.");

    var blankCfg = new PluginConfiguration
    {
        MinimumLogLevel = string.Empty
    };
    AssertFalse(YummyKodikLogFilter.ShouldLog(LogLevel.Information, blankCfg), "Blank log level should fall back to Warning.");

    AssertTrue(
        YummyKodikLogFilter.ShouldLog("provider", "Microsoft.Hosting", LogLevel.Information),
        "Non-YummyKodik categories should pass through the plugin filter.");
}

static void YummyKodikLogFilter_UsesConfiguredMinimumLevel()
{
    var cfg = new PluginConfiguration
    {
        MinimumLogLevel = "Information"
    };
    AssertTrue(YummyKodikLogFilter.ShouldLog(LogLevel.Information, cfg), "Information level should allow informational diagnostics.");
    AssertFalse(YummyKodikLogFilter.ShouldLog(LogLevel.Debug, cfg), "Information level should still suppress Debug logs.");

    cfg.MinimumLogLevel = "debug";
    AssertTrue(YummyKodikLogFilter.ShouldLog(LogLevel.Debug, cfg), "Configured log level parsing should be case-insensitive.");
    AssertFalse(YummyKodikLogFilter.ShouldLog(LogLevel.Trace, cfg), "Debug level should suppress Trace logs.");

    cfg.MinimumLogLevel = "Error";
    AssertFalse(YummyKodikLogFilter.ShouldLog(LogLevel.Warning, cfg), "Error level should suppress warnings.");
    AssertTrue(YummyKodikLogFilter.ShouldLog(LogLevel.Error, cfg), "Error level should keep errors.");
    AssertTrue(YummyKodikLogFilter.ShouldLog(LogLevel.Critical, cfg), "Error level should keep critical logs.");

    cfg.MinimumLogLevel = "None";
    AssertFalse(YummyKodikLogFilter.ShouldLog(LogLevel.Critical, cfg), "None should suppress all plugin logs.");

    cfg.MinimumLogLevel = "unexpected";
    AssertFalse(YummyKodikLogFilter.ShouldLog(LogLevel.Information, cfg), "Unknown log level should fall back to Warning.");
    AssertTrue(YummyKodikLogFilter.ShouldLog(LogLevel.Warning, cfg), "Unknown log level fallback should keep warnings.");

    cfg.MinimumLogLevel = "Information";
    YummyKodikLogFilter.ConfigurationProvider = () => cfg;
    try
    {
        AssertTrue(
            YummyKodikLogFilter.ShouldLog("provider", "YummyKodik.Tasks.Refresh.RefreshTitleService", LogLevel.Information),
            "YummyKodik category filtering should use the current plugin configuration provider.");
        AssertFalse(
            YummyKodikLogFilter.ShouldLog("provider", "YummyKodik.Tasks.Refresh.RefreshTitleService", LogLevel.Debug),
            "YummyKodik category filtering should suppress logs below the configured provider level.");
    }
    finally
    {
        YummyKodikLogFilter.ConfigurationProvider = static () => null;
    }
}

static void YummyKodikLogFilter_CategoryRuleSuppressesPluginInformationLogs()
{
    var cfg = new PluginConfiguration();
    YummyKodikLogFilter.ConfigurationProvider = () => cfg;
    var provider = new CaptureLoggerProvider();

    try
    {
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(provider);
            builder.AddFilter("YummyKodik", YummyKodikLogFilter.ShouldLogPluginCategory);
        });

        var pluginLogger = factory.CreateLogger("YummyKodik.Plugin");
        pluginLogger.LogInformation("suppressed");
        pluginLogger.LogWarning("kept");

        var otherLogger = factory.CreateLogger("Microsoft.Hosting");
        otherLogger.LogInformation("other");

        AssertFalse(provider.Events.Any(x => x.Category == "YummyKodik.Plugin" && x.Level == LogLevel.Information), "Category rule should suppress YummyKodik information logs.");
        AssertTrue(provider.Events.Any(x => x.Category == "YummyKodik.Plugin" && x.Level == LogLevel.Warning), "Category rule should keep YummyKodik warnings.");
        AssertTrue(provider.Events.Any(x => x.Category == "Microsoft.Hosting" && x.Level == LogLevel.Information), "Category rule should not suppress other categories.");
    }
    finally
    {
        YummyKodikLogFilter.ConfigurationProvider = static () => null;
    }
}

static void YummyKodikLogger_SuppressesInformationBeforeInnerLogger()
{
    var cfg = new PluginConfiguration();
    YummyKodikLogFilter.ConfigurationProvider = () => cfg;
    var provider = new CaptureLoggerProvider();

    try
    {
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(provider);
        });

        var wrapped = new YummyKodikLogger(factory.CreateLogger("YummyKodik.Plugin"), "YummyKodik.Plugin");
        wrapped.LogInformation("suppressed");
        wrapped.LogWarning("kept");

        AssertFalse(provider.Events.Any(x => x.Category == "YummyKodik.Plugin" && x.Level == LogLevel.Information), "Wrapped plugin logger should suppress Information before it reaches Jellyfin.");
        AssertTrue(provider.Events.Any(x => x.Category == "YummyKodik.Plugin" && x.Level == LogLevel.Warning), "Wrapped plugin logger should keep Warning logs.");

        cfg.MinimumLogLevel = "Information";
        wrapped.LogInformation("enabled");
        AssertTrue(provider.Events.Any(x => x.Category == "YummyKodik.Plugin" && x.Level == LogLevel.Information), "Lowering the plugin level to Information should re-enable informational logs.");
    }
    finally
    {
        YummyKodikLogFilter.ConfigurationProvider = static () => null;
    }
}

static void RefreshPerformanceSummary_IsVisibleWithoutInformationNoise()
{
    var cfg = new PluginConfiguration
    {
        MinimumLogLevel = "Warning",
        EnablePerformanceDebugLogging = true
    };
    YummyKodikLogFilter.ConfigurationProvider = () => cfg;
    var provider = new CaptureLoggerProvider();

    try
    {
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(provider);
            builder.AddFilter("YummyKodik", YummyKodikLogFilter.ShouldLogPluginCategory);
        });

        var wrapped = new YummyKodikLogger(factory.CreateLogger("YummyKodik.Tasks.Refresh"), "YummyKodik.Tasks.Refresh");
        wrapped.LogInformation("ordinary information must remain hidden");

        var run = new RefreshRunMetrics(cfg.EnablePerformanceDebugLogging);
        run.AddDuration("phase.strm_snapshot", TimeSpan.FromMilliseconds(3));
        run.AddDuration("phase.runtime_backfill.pre", TimeSpan.FromMilliseconds(4));
        run.AddDuration("phase.runtime_publish.pre", TimeSpan.FromMilliseconds(5));
        run.AddDuration("phase.title_keys", TimeSpan.FromMilliseconds(6));
        run.AddDuration("phase.stale_cleanup", TimeSpan.FromMilliseconds(7));
        run.AddDuration("phase.titles", TimeSpan.FromMilliseconds(8));
        run.AddDuration("phase.runtime_backfill.post", TimeSpan.FromMilliseconds(9));
        run.AddDuration("phase.readiness_merge", TimeSpan.FromMilliseconds(10));
        run.AddCount("kodik.http_requests", 11);
        run.AddCount("kodik.search.cache_hits", 12);
        run.AddCount("skip.files_checked", 13);
        run.AddCount("io.strm_updated", 2);
        run.AddCount("titles.total", 4);
        run.AddCount("titles.finished", 4);
        run.AddCount("titles.unhandled_failed", 1);

        var title = new RefreshPerformanceMetrics(cfg.EnablePerformanceDebugLogging, run);
        title.AddCount("skip.PerVoiceAfterCatalog.Matched");
        title.LogSummary(wrapped, "Frieren", "frieren");
        run.LogSummary(wrapped);

        AssertFalse(
            provider.Events.Any(x => x.Message.Contains("ordinary information", StringComparison.Ordinal)),
            "Enabling perf summaries must not enable ordinary Information noise.");
        var summaries = provider.Events
            .Where(x => x.Message.Contains("[YummyKodik][perf]", StringComparison.Ordinal))
            .ToArray();
        AssertEqual(2, summaries.Length, "Enabled performance logging should expose title and run summaries through the Warning minimum.");
        var runSummary = summaries.Single(x => x.Message.Contains("Run ", StringComparison.Ordinal));
        AssertTrue(runSummary.Message.Contains("kodikHttpRequests=11", StringComparison.Ordinal), "Run summary should expose actual instrumented KodikClient HTTP requests.");
        AssertTrue(runSummary.Message.Contains("cacheHits=12", StringComparison.Ordinal), "Run summary should expose cache hits.");
        AssertTrue(runSummary.Message.Contains("filesChecked=13", StringComparison.Ordinal), "Run summary should expose actually hashed files.");
        AssertTrue(runSummary.Message.Contains("filesChanged=2", StringComparison.Ordinal), "Run summary should aggregate file change operations.");
        AssertTrue(runSummary.Message.Contains("phase.readiness_merge=10ms", StringComparison.Ordinal), "Run summary should contain common phase timings.");
        AssertTrue(runSummary.Message.Contains("skip.PerVoiceAfterCatalog.Matched=1", StringComparison.Ordinal), "Title skip decisions should aggregate into the run summary.");

        var beforeDisabled = provider.Events.Count;
        new RefreshPerformanceMetrics(enabled: false).LogSummary(wrapped, "Disabled", "disabled");
        new RefreshRunMetrics(enabled: false).LogSummary(wrapped);
        AssertEqual(beforeDisabled, provider.Events.Count, "Disabled performance logging should emit no summaries.");
    }
    finally
    {
        YummyKodikLogFilter.ConfigurationProvider = static () => null;
    }
}

static void JellyfinWebIndexPatcher_InsertsManagedBootstrapBeforeHeadClose()
{
    const string html = "<!doctype html>\r\n<html><head><title>Test</title></head><body></body></html>";
    const string scriptUrl = "/web/ConfigurationPage?name=seriesTranslation.js&v=1.0.0.0";

    var changed = JellyfinWebIndexPatcher.TryInjectSeriesTranslationScript(html, scriptUrl, out var patchedHtml);

    AssertTrue(changed, "Bootstrap script should be injected into a plain Jellyfin index.html.");
    AssertTrue(patchedHtml.Contains(JellyfinWebIndexPatcher.StartMarker, StringComparison.Ordinal), "Managed bootstrap start marker should be present.");
    AssertTrue(patchedHtml.Contains("ConfigurationPage?name=seriesTranslation.js", StringComparison.Ordinal), "Managed bootstrap should point to the translation widget script.");
    AssertTrue(patchedHtml.Contains("v=1.0.0.0", StringComparison.Ordinal), "Managed bootstrap should carry the cache-busting version.");
    AssertTrue(patchedHtml.IndexOf(JellyfinWebIndexPatcher.StartMarker, StringComparison.Ordinal) < patchedHtml.IndexOf("</head>", StringComparison.OrdinalIgnoreCase), "Managed bootstrap should be inserted before </head>.");
}

static void JellyfinWebIndexPatcher_ReplacesExistingManagedBootstrap()
{
    var original = string.Join(
        "\n",
        "<html><head>",
        JellyfinWebIndexPatcher.BuildManagedSnippet("/web/ConfigurationPage?name=seriesTranslation.js&v=old"),
        "</head><body></body></html>");

    var changed = JellyfinWebIndexPatcher.TryInjectSeriesTranslationScript(
        original,
        "/web/ConfigurationPage?name=seriesTranslation.js&v=new",
        out var patchedHtml);

    AssertTrue(changed, "Managed bootstrap should be updated when the script URL changes.");
    AssertTrue(patchedHtml.Contains("v=new", StringComparison.Ordinal), "Updated bootstrap should contain the new version.");
    AssertFalse(patchedHtml.Contains("v=old", StringComparison.Ordinal), "Updated bootstrap should replace the old version.");
}

static void JellyfinWebIndexPatcher_DoesNotDuplicateBootstrap()
{
    var original = string.Join(
        "\n",
        "<html><head>",
        JellyfinWebIndexPatcher.BuildManagedSnippet("/web/ConfigurationPage?name=seriesTranslation.js&v=stable"),
        "</head><body></body></html>");

    var changed = JellyfinWebIndexPatcher.TryInjectSeriesTranslationScript(
        original,
        "/web/ConfigurationPage?name=seriesTranslation.js&v=stable",
        out var patchedHtml);

    AssertFalse(changed, "Managed bootstrap should not be duplicated when it is already current.");
    AssertEqual(original, patchedHtml, "Unchanged bootstrap should leave index.html intact.");
}

static void JellyfinWebIndexPatcher_UpgradesBothScriptsWithoutDuplicates()
{
    const string translationUrl = "/web/ConfigurationPage?name=seriesTranslation.js&v=build-new";
    const string playbackUrl = "/web/ConfigurationPage?name=playbackBuffer.js&v=build-new";
    var original = "<html><head>\r\n"
        + JellyfinWebIndexPatcher.BuildManagedSnippet(
            "/web/ConfigurationPage?name=seriesTranslation.js&v=build-old", "\r\n")
        + "\r\n</head><body></body></html>";

    var upgraded = JellyfinWebIndexPatcher.TryInjectSeriesTranslationScript(
        original, translationUrl, out var patched, playbackUrl);
    AssertTrue(upgraded, "An installed single-script bootstrap must upgrade in place to both web helpers.");
    AssertFalse(patched.Contains("build-old", StringComparison.Ordinal), "The previous build key must be removed during upgrade.");
    AssertTrue(patched.Contains("name=playbackBuffer.js&amp;v=build-new", StringComparison.Ordinal), "Playback buffering must have the current escaped build cache key.");
    AssertTrue(patched.Contains("name=seriesTranslation.js&amp;v=build-new", StringComparison.Ordinal), "The translation script must share the current build cache key.");
    AssertEqual(2, patched.Split("<script defer=\"defer\"", StringSplitOptions.None).Length - 1, "Both web helpers must load using defer.");
    AssertTrue(patched.IndexOf("name=playbackBuffer.js", StringComparison.Ordinal)
        < patched.IndexOf("name=seriesTranslation.js", StringComparison.Ordinal), "The playback hook should install before the translation helper.");

    var repeated = JellyfinWebIndexPatcher.TryInjectSeriesTranslationScript(
        patched, translationUrl, out var unchanged, playbackUrl);
    AssertFalse(repeated, "Applying the same two-script bootstrap must be idempotent.");
    AssertEqual(patched, unchanged, "An unchanged bootstrap must retain the exact existing HTML.");
    AssertEqual(1, unchanged.Split("name=playbackBuffer.js", StringSplitOptions.None).Length - 1, "The playback helper must occur exactly once.");
    AssertEqual(1, unchanged.Split("name=seriesTranslation.js", StringSplitOptions.None).Length - 1, "The translation helper must occur exactly once.");

    using var embeddedPlayback = typeof(NfoBuilder).Assembly.GetManifestResourceStream("YummyKodik.Web.playbackBuffer.js");
    AssertTrue(embeddedPlayback is not null, "The playback helper referenced by the bootstrap must be embedded in the plugin.");
}

static void SeriesTranslationScript_AcceptsJellyfinPascalCaseTranslationOptions()
{
    using var stream = typeof(NfoBuilder).Assembly.GetManifestResourceStream(
        "YummyKodik.Web.seriesTranslation.js");
    AssertTrue(stream is not null, "The embedded translation widget script should exist.");

    using var reader = new StreamReader(stream!);
    var script = reader.ReadToEnd();

    AssertTrue(
        script.Contains("readValue(t, \"id\", \"Id\")", StringComparison.Ordinal),
        "The widget should accept Jellyfin's PascalCase translation Id.");
    AssertTrue(
        script.Contains("readValue(t, \"name\", \"Name\")", StringComparison.Ordinal),
        "The widget should accept Jellyfin's PascalCase translation Name.");
    AssertTrue(
        script.Contains("readValue(t, \"type\", \"Type\")", StringComparison.Ordinal),
        "The widget should accept Jellyfin's PascalCase translation Type.");
    AssertTrue(
        script.Contains("ykRequest: Date.now().toString()", StringComparison.Ordinal),
        "Translation API requests should bypass stale browser responses.");
    AssertTrue(
        script.Contains("\"✓ \" + item.label", StringComparison.Ordinal),
        "The selected widget voice should have an explicit visual marker.");
}

static YummyAnimeResponse BuildSecondSeasonAnime()
{
    return new YummyAnimeResponse
    {
        Title = "Фермерская жизнь в ином мире 2",
        AnimeId = 22446,
        AnimeUrl = "fermerskaya-zhizn-v-inom-mire-2",
        Season = 2,
        RemoteIds = new YummyRemoteIds
        {
            ShikimoriId = 62146
        },
        Type = new YummyAnimeType
        {
            Alias = "tv",
            Name = "Сериал",
            ShortName = "ТВ",
            Value = 1
        },
        Episodes = new YummyEpisodesInfo
        {
            Count = 0,
            Aired = 0
        },
        ViewingOrder = new List<YummyViewingOrderItem>
        {
            new()
            {
                AnimeId = 4785,
                AnimeUrl = "fermerskaya-zhizn-v-inom-mire",
                Title = "Фермерская жизнь в ином мире",
                Data = new YummyViewingOrderData
                {
                    Id = 3119,
                    Index = 0,
                    Text = "адаптация ранобэ"
                }
            },
            new()
            {
                AnimeId = 22446,
                AnimeUrl = "fermerskaya-zhizn-v-inom-mire-2",
                Title = "Фермерская жизнь в ином мире 2",
                Data = new YummyViewingOrderData
                {
                    Id = 3119,
                    Index = 1,
                    Text = "продолжение"
                }
            }
        }
    };
}

static PluginConfiguration BuildAllohaApiConfig(string apiToken)
{
    return new PluginConfiguration
    {
        AllohaApiToken = apiToken,
        AllohaApiBaseUrl = "https://api.alloha.tv"
    };
}

static YummyAnimeResponse BuildAllohaApiAnime(long animeId, long kpId)
{
    return new YummyAnimeResponse
    {
        AnimeId = animeId,
        RemoteIds = new YummyRemoteIds
        {
            KpId = kpId
        }
    };
}

static string BuildAllohaApiCatalogPayload()
{
    return """
        {
          "status": "success",
          "data": {
            "seasons": {
              "1": {
                "season": 1,
                "episodes": {
                  "1": {
                    "episode": 1,
                    "translation": {
                      "215": {
                        "translation": "Dream Cast",
                        "iframe": "https://larkin-as.stloadi.live/?token_movie=movie-1&translation=215&season=1&episode=1&token=req-1"
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;
}

static YummyAnimeResponse BuildSlimeFourthSeasonAnime()
{
    return new YummyAnimeResponse
    {
        Title = "О моём перерождении в слизь 4",
        AnimeId = 15066,
        AnimeUrl = "o-moem-pererozhdenii-v-sliz-4",
        Season = 2,
        Type = new YummyAnimeType
        {
            Alias = "tv",
            Name = "Сериал",
            ShortName = "ТВ",
            Value = 1
        },
        ViewingOrder = new List<YummyViewingOrderItem>
        {
            new()
            {
                AnimeId = 1012,
                AnimeUrl = "o-moem-pererozhdenii-v-sliz-hinata-sakaguchi",
                Title = "О моём перерождении в слизь: Хината Сакагучи",
                Data = new YummyViewingOrderData
                {
                    Id = 3467,
                    Index = 0,
                    Text = "компиляция сериала"
                }
            },
            new()
            {
                AnimeId = 1009,
                AnimeUrl = "o-moem-pererozhdenii-v-sliz",
                Title = "О моём перерождении в слизь",
                Data = new YummyViewingOrderData
                {
                    Id = 3467,
                    Index = 1,
                    Text = "адаптация ранобэ"
                }
            },
            new()
            {
                AnimeId = 15066,
                AnimeUrl = "o-moem-pererozhdenii-v-sliz-4",
                Title = "О моём перерождении в слизь 4",
                Data = new YummyViewingOrderData
                {
                    Id = 3467,
                    Index = 16,
                    Text = "продолжение"
                }
            }
        }
    };
}

static (string SeriesRoot, RefreshStateSeasonInput Input, Dictionary<int, HashSet<string>> ExpectedEpisodeFileBaseNames)
    CreateRefreshStateFixture(
        string tempRoot,
        int seasonNumber = 1,
        int expectedAvailableEpisodes = 1,
        bool createStrmPerVoiceTranslation = false,
        string? fingerprint = null)
{
    var seriesRoot = Path.Combine(tempRoot, "Series");
    var seasonKey = RefreshStateManager.BuildSeasonKey(seasonNumber);
    var seasonDir = Path.Combine(seriesRoot, seasonKey);
    Directory.CreateDirectory(seasonDir);

    File.WriteAllText(Path.Combine(seriesRoot, "tvshow.nfo"), NfoBuilder.BuildSeriesNfo("Frieren", "Plot"));

    var expectedEpisodeFileBaseNames = new Dictionary<int, HashSet<string>>();
    var effectiveSeasonNumber = seasonNumber >= 0 ? seasonNumber : 1;
    for (var episodeNumber = 1; episodeNumber <= expectedAvailableEpisodes; episodeNumber++)
    {
        var fileBaseName = $"S{effectiveSeasonNumber:00}E{episodeNumber:00}";
        File.WriteAllText(
            Path.Combine(seasonDir, fileBaseName + ".strm"),
            $"https://jellyfin.test/YummyKodik/stream?ep={episodeNumber}&allohaRequestToken=secret-request-{episodeNumber}" + Environment.NewLine);
        File.WriteAllText(
            Path.Combine(seasonDir, fileBaseName + ".nfo"),
            NfoBuilder.BuildEpisodeNfo(episodeNumber, effectiveSeasonNumber, "Frieren", "Plot"));

        expectedEpisodeFileBaseNames[episodeNumber] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            fileBaseName
        };
    }

    var input = BuildRefreshStateSeasonInput(
        seasonNumber,
        expectedAvailableEpisodes,
        createStrmPerVoiceTranslation,
        fingerprint: fingerprint ?? RefreshStateManager.BuildFingerprint(BuildRefreshStateFingerprintInput(seasonNumber: seasonNumber)));

    return (seriesRoot, input, expectedEpisodeFileBaseNames);
}

static RefreshStateSeasonInput BuildRefreshStateSeasonInput(
    int seasonNumber = 1,
    int expectedAvailableEpisodes = 1,
    bool createStrmPerVoiceTranslation = false,
    int preferredQuality = 1080,
    string? fingerprint = null,
    string cleanKey = "frieren")
{
    return new RefreshStateSeasonInput
    {
        SeasonKey = RefreshStateManager.BuildSeasonKey(seasonNumber),
        SeasonNumber = seasonNumber,
        CleanKey = cleanKey,
        CreateStrmPerVoiceTranslation = createStrmPerVoiceTranslation,
        PreferredQuality = preferredQuality,
        Fingerprint = fingerprint ?? RefreshStateManager.BuildFingerprint(BuildRefreshStateFingerprintInput(seasonNumber: seasonNumber)),
        ExpectedAvailableEpisodes = expectedAvailableEpisodes
    };
}

static RefreshStateFingerprintInput BuildRefreshStateFingerprintInput(
    string mode = "single-file",
    string streamGatewayBaseUrl = "http://127.0.0.1:8096",
    string preferredTranslationFilter = "anilibria",
    int preferredQuality = 1080,
    string seriesTitle = "Frieren",
    int seasonNumber = 1,
    int expectedAvailableEpisodes = 1,
    IReadOnlyList<string>? providerCoverage = null)
{
    return new RefreshStateFingerprintInput
    {
        Mode = mode,
        StreamGatewayBaseUrl = streamGatewayBaseUrl,
        PreferredTranslationFilter = preferredTranslationFilter,
        PreferredQuality = preferredQuality,
        CleanKey = "frieren",
        RawTitle = seasonNumber == 1 ? "Frieren" : "Frieren 2",
        SeriesTitle = seriesTitle,
        SeasonKey = RefreshStateManager.BuildSeasonKey(seasonNumber),
        SeasonNumber = seasonNumber,
        AnimeId = 52991,
        AnimeUrl = "frieren",
        ShikimoriId = 52991,
        KinopoiskId = 123456,
        ImdbId = "tt1234567",
        ExpectedAvailableEpisodes = expectedAvailableEpisodes,
        KnownSupportedEpisodes = Enumerable.Range(1, expectedAvailableEpisodes).ToArray(),
        AllohaSupportedEpisodes = Enumerable.Range(1, expectedAvailableEpisodes).ToArray(),
        CvhSupportedEpisodes = Array.Empty<int>(),
        YummySupportedEpisodes = Enumerable.Range(1, expectedAvailableEpisodes).ToArray(),
        ProviderCoverage = providerCoverage ?? new[] { "ep:1:preferred:Alloha" },
        AllohaApiBaseUrl = "https://api.alloha.test",
        AllohaApiTokenHash = RefreshStateManager.HashSecret("alloha-api-token")
    };
}

static Task InvokeRefreshTaskStaticTask(string methodName, params object?[] args)
{
    var method = typeof(RefreshYummyKodikLibraryTask).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
    AssertTrue(method is not null, methodName + " should exist.");
    return (Task)method!.Invoke(null, args)!;
}

static Task<T> InvokeRefreshTaskStaticTaskResult<T>(string methodName, params object?[] args)
{
    var method = typeof(RefreshYummyKodikLibraryTask).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
    AssertTrue(method is not null, methodName + " should exist.");
    return (Task<T>)method!.Invoke(null, args)!;
}

static void InvokeRefreshFileWriterWriteTextAtomically(string path, string content)
{
    var writerType = typeof(RefreshStateManager).Assembly.GetType("YummyKodik.Tasks.Refresh.RefreshFileWriter");
    AssertTrue(writerType is not null, "RefreshFileWriter should exist for atomic writer regression coverage.");

    var method = writerType!.GetMethod("WriteTextAtomicallyAsync", BindingFlags.Static | BindingFlags.Public);
    AssertTrue(method is not null, "WriteTextAtomicallyAsync should be reachable for atomic writer regression coverage.");

    var task = (Task)method!.Invoke(null, new object?[] { path, content, null, "strm", CancellationToken.None })!;
    task.GetAwaiter().GetResult();
}

static StaleReleaseCleanupResult RunStaleReleaseCleanup(string outputRoot, IEnumerable<string> currentKeys)
{
    var normalizedKeys = new HashSet<string>(currentKeys, StringComparer.OrdinalIgnoreCase);
    return StaleReleaseCleanupService.CleanupAsync(
            outputRoot,
            normalizedKeys,
            NullLogger.Instance,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();
}

static void WriteCleanupState(string seriesRoot, params (string SeasonKey, string CleanKey)[] seasons)
{
    var payload = new
    {
        schemaVersion = RefreshStateManager.SchemaVersion,
        seasons = seasons.ToDictionary(
            season => season.SeasonKey,
            season => new { cleanKey = season.CleanKey },
            StringComparer.Ordinal)
    };

    File.WriteAllText(
        Path.Combine(seriesRoot, RefreshStateManager.StateFileName),
        JsonSerializer.Serialize(payload));
}

static string WriteManagedCleanupSeason(string seriesRoot, int seasonNumber, string cleanKey)
{
    var seasonKey = RefreshStateManager.BuildSeasonKey(seasonNumber);
    var seasonDir = Path.Combine(seriesRoot, seasonKey);
    var fileBaseName = $"S{seasonNumber:00}E01";
    Directory.CreateDirectory(seasonDir);
    File.WriteAllText(Path.Combine(seriesRoot, "tvshow.nfo"), NfoBuilder.BuildSeriesNfo("Cleanup series", "Plot"));
    File.WriteAllText(Path.Combine(seasonDir, fileBaseName + ".strm"), "http://127.0.0.1:8096/YummyKodik/stream?provider=cvh&animeId=1&ep=1" + Environment.NewLine);
    File.WriteAllText(Path.Combine(seasonDir, fileBaseName + ".nfo"), NfoBuilder.BuildEpisodeNfo(1, seasonNumber, "Cleanup series", "Plot"));

    var expectedFiles = new Dictionary<int, HashSet<string>>
    {
        [1] = new(StringComparer.OrdinalIgnoreCase) { fileBaseName }
    };
    var input = BuildRefreshStateSeasonInput(
        seasonNumber: seasonNumber,
        expectedAvailableEpisodes: 1,
        fingerprint: RefreshStateManager.BuildFingerprint(BuildRefreshStateFingerprintInput(seasonNumber: seasonNumber)),
        cleanKey: cleanKey);

    var written = RefreshStateManager.WriteSeasonStateAsync(
            seriesRoot,
            input,
            expectedFiles,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();
    AssertTrue(written, "Complete generated season artifacts should produce cleanup state.");
    return seasonDir;
}

static void UpdateMax(ref int target, int value)
{
    while (true)
    {
        var current = Volatile.Read(ref target);
        if (value <= current)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref target, value, current) == current)
        {
            return;
        }
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
    }
}

static bool WaitUntil(Func<bool> condition, int timeoutMs)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (DateTime.UtcNow < deadline)
    {
        if (condition())
        {
            return true;
        }

        Thread.Sleep(20);
    }

    return condition();
}

static void AssertTrue(bool value, string message)
{
    if (!value)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertFalse(bool value, string message)
{
    if (value)
    {
        throw new InvalidOperationException(message);
    }
}

static TException AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException ex)
    {
        return ex;
    }

    throw new InvalidOperationException(message);
}

static HttpRequestMessage CloneRequest(HttpRequestMessage request)
{
    var clone = new HttpRequestMessage(request.Method, request.RequestUri);
    foreach (var header in request.Headers)
    {
        clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    return clone;
}

static string InvokePrepareSeasonDirectory(string seriesRoot, string seasonDir, int seasonNumber)
{
    var helperType = typeof(NfoBuilder).Assembly.GetType("YummyKodik.Tasks.SeasonDirectoryMaintenance");
    AssertTrue(helperType != null, "SeasonDirectoryMaintenance helper should exist for season regression coverage.");

    var method = helperType!.GetMethod(
        "PrepareSeasonDirectory",
        BindingFlags.NonPublic | BindingFlags.Static);

    AssertTrue(method != null, "PrepareSeasonDirectory should be reachable via reflection for regression coverage.");

    return (string)method!.Invoke(null, new object[] { NullLogger.Instance, seriesRoot, seasonDir, seasonNumber })!;
}

static void TryDeleteDirectory(string path)
{
    try
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch
    {
    }
}

sealed class DelegatingTestHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public DelegatingTestHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(_handler(request));
    }
}

sealed class AsyncDelegatingTestHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

    public AsyncDelegatingTestHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return _handler(request, cancellationToken);
    }
}

sealed class CaptureLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly List<LogEvent> _events = new();

    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new CaptureLogger(categoryName, Add);
    }

    public void Dispose()
    {
    }

    private void Add(LogEvent logEvent)
    {
        lock (_gate)
        {
            _events.Add(logEvent);
        }
    }
}

sealed class CaptureLogger : ILogger
{
    private readonly string _categoryName;
    private readonly Action<LogEvent> _add;

    public CaptureLogger(string categoryName, Action<LogEvent> add)
    {
        _categoryName = categoryName;
        _add = add;
    }

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
    {
        return NullScope.Instance;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        return true;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        _add(new LogEvent(_categoryName, logLevel, formatter(state, exception)));
    }
}

sealed class NullScope : IDisposable
{
    public static readonly NullScope Instance = new();

    private NullScope()
    {
    }

    public void Dispose()
    {
    }
}

sealed record LogEvent(string Category, LogLevel Level, string Message);

sealed class RecordingProgress : IProgress<double>
{
    private readonly object _gate = new();
    private readonly List<double> _values = new();

    public IReadOnlyList<double> Values
    {
        get
        {
            lock (_gate)
            {
                return _values.ToArray();
            }
        }
    }

    public void Report(double value)
    {
        lock (_gate)
        {
            _values.Add(value);
        }
    }
}

sealed class RecordingPassThroughHandler : DelegatingHandler
{
    private readonly Action<string> _log;

    public RecordingPassThroughHandler(HttpMessageHandler innerHandler, Action<string> log)
        : base(innerHandler)
    {
        _log = log;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _log(
            "request " +
            request.Method +
            " " +
            request.RequestUri +
            " | Accepts-Controls=" +
            JoinHeader(request, "Accepts-Controls") +
            " | Authorizations=" +
            JoinHeader(request, "Authorizations") +
            " | Origin=" +
            JoinHeader(request, "Origin") +
            " | Referer=" +
            request.Headers.Referrer?.AbsoluteUri);

        var response = await base.SendAsync(request, cancellationToken);
        _log(
            "response " +
            (int)response.StatusCode +
            " " +
            request.RequestUri +
            " | Content-Type=" +
            response.Content.Headers.ContentType?.MediaType);
        return response;
    }

    private static string JoinHeader(HttpRequestMessage request, string name)
    {
        return request.Headers.TryGetValues(name, out var values)
            ? string.Join(", ", values)
            : string.Empty;
    }
}

static class TestData
{
    internal static readonly int[] EpisodeOne = { 1 };
    internal static readonly int[] EpisodesOneTwo = { 1, 2 };
    internal static readonly int[] UnorderedEpisodesWithDuplicate = { 3, 1, 2, 2 };

    internal static readonly string[] KodikSupplementTranslationNames =
    {
        "Dream Cast",
        "AnimeVost",
        "AniLibria"
    };
}
