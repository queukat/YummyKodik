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
if (args.Length == 3 && args[0] == "--ffmpeg-smoke")
{
    await FfmpegHlsSmokeTest.RunAsync(args[1], args[2]);
    return;
}

if (args.Length == 2 && args[0] == "--live-manifest")
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    var manifest = await HlsPlaybackManifestResolver.ResolveAsync(client, args[1], CancellationToken.None);
    var replay = await HlsPlaybackManifestResolver.ResolveAsync(client, manifest.Url, CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        sourcePath = new Uri(manifest.Url).AbsolutePath,
        seconds = TimeSpan.FromTicks(manifest.RunTimeTicks).TotalSeconds,
        stableSession = manifest.Url == replay.Url && manifest.RunTimeTicks == replay.RunTimeTicks
    }));
    return;
}

if (args.Length is 2 or 3 or 4 && (args[0] == "--live-alloha-strm" || args[0] == "--live-alloha-strm-recover" || args[0] == "--live-alloha-strm-sequential" || args[0] == "--live-alloha-strm-repeat" || args[0] == "--live-alloha-strm-channel"))
{
    await AllohaLivePlaybackProbe.RunAsync(args[1], simulateOldIdentity: args[0].EndsWith("-recover", StringComparison.Ordinal), quality: args.Length >= 3 ? int.Parse(args[2]) : 720, browserMajor: args.Length == 4 ? int.Parse(args[3]) : null, sequential: args[0].EndsWith("-sequential", StringComparison.Ordinal), repeatIdentity: args[0].EndsWith("-repeat", StringComparison.Ordinal), liveChannel: args[0].EndsWith("-channel", StringComparison.Ordinal));
    return;
}

var tests = new (string Name, Action Run)[]
{
    ("YummyAccount_CurrentConfigurationAndSaveFailure", YummyAccountTests.CurrentConfigurationAndSaveFailure),
    ("YummyAccount_AuthenticationTransitions", YummyAccountTests.AuthenticationTransitions),
    ("YummyAccount_StaleConfigurationCannotRestoreCredentials", YummyAccountTests.StaleConfigurationCannotRestoreCredentials),
    ("AllohaProxyRenewal_SequentialSiblingUsesRefreshedSession", AllohaProxyRenewalTests.SequentialSiblingUsesRefreshedSession),
    ("AllohaProxyRenewal_CapturedSiblingUrlUsesCurrentMapping", AllohaProxyRenewalTests.CapturedSiblingUrlUsesCurrentMapping),
    ("AllohaProxyRenewal_UnknownSiblingRemainsUnresolvable", AllohaProxyRenewalTests.UnknownSiblingRemainsUnresolvable),
    ("AllohaProxyRenewal_ForeignReferenceKeepsItsOrigin", AllohaProxyRenewalTests.ForeignReferenceKeepsItsOrigin),

    ("MultiLibrary_RefreshRoutesMovesAndFailuresIndependently", MultiLibraryTests.RefreshRoutesMovesAndFailuresIndependently),
    ("MultiLibrary_WidgetKeepsWholeSeriesVoicesAcrossEpisodes", MultiLibraryTests.WidgetKeepsWholeSeriesVoicesAcrossEpisodes),
    ("MultiLibrary_SeparateSchedulesNeverTouchTheOtherFolder", MultiLibraryTests.SeparateSchedulesNeverTouchTheOtherFolder),
    ("MultiLibrary_UnindexedRootNeverFallsBackToGlobalInventory", MultiLibraryTests.UnindexedRootNeverFallsBackToGlobalInventory),
    ("MultiLibrary_ScopedRefreshPreservesOtherRootAndLateRequests", LateNativeMergeTests.ScopedRefreshPreservesOtherRootAndLateRequests),
    ("MultiLibrary_BackgroundPassYieldsAtGroupBoundaryToScheduledRefresh", LateNativeMergeTests.BackgroundPassYieldsAtGroupBoundaryToScheduledRefresh),
    ("MultiLibrary_RootValidationAndConfigurationCompatibility", MultiLibraryTests.RootValidationAndConfigurationCompatibility),
    ("MultiLibrary_ListsShareAuthenticationAndPropagateCancellation", MultiLibraryTests.ListsShareAuthenticationAndPropagateCancellation),
    ("MultiLibrary_VersionsStayInsideEachLibrary", EpisodeVersionsMergeTests.VersionsStayInsideEachLibrary),
    ("MultiLibrary_NativeResolutionRecognizesSecondRoot", NativeEpisodeResolverTests.NativeResolutionRecognizesSecondRoot),
    ("StaleReleaseCleanup_MissingSeasonsPermitProvenMetadataCleanup", StaleReleaseCleanup_MissingSeasonsPermitProvenMetadataCleanup),

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
    ("AuditClosure_CallerCancellationAndResponseDisposal", AuditClosureTests.CallerCancellationAndResponseDisposal),
    ("AuditClosure_ProviderDiagnosticsDoNotExposePayloads", AuditClosureTests.ProviderDiagnosticsDoNotExposePayloads),
    ("AuditClosure_PartialFailureReportsFailedAfterMerge", MultiLibraryTests.PartialFailureReportsFailedAfterMergingSuccessfulTitles),
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
    ("SkipTimingConsensus_Selection", SkipTimingConsensusTests.Selection),
    ("YummyKodikMediaSourceProvider_RuntimePublicationPolicyRepairsMissingOrShortValues", YummyKodikMediaSourceProvider_RuntimePublicationPolicyRepairsMissingOrShortValues),
    ("YummyKodikMediaSourceProvider_AuthoritativeRuntimeCorrectsPlausibleMismatch", YummyKodikMediaSourceProvider_AuthoritativeRuntimeCorrectsPlausibleMismatch),
    ("YummyKodikMediaSourceProvider_ResolvesPrimaryOnlyForExplicitSeriesSelection", YummyKodikMediaSourceProvider_ResolvesPrimaryOnlyForExplicitSeriesSelection),
    ("YummyKodikMediaSourceProvider_FillsMissingSourceRuntimeFromSiblingOrItem", YummyKodikMediaSourceProvider_FillsMissingSourceRuntimeFromSiblingOrItem),
    ("YummyKodikMediaSourceProvider_SharesRuntimeOnlyWithinEpisodeVersionSet", YummyKodikMediaSourceProvider_SharesRuntimeOnlyWithinEpisodeVersionSet),
    ("YummyKodikMediaSourceFactory_CarriesRuntimeWithoutReflection", YummyKodikMediaSourceFactory_CarriesRuntimeWithoutReflection),
    ("HlsPlaybackManifest_FallbackDurationAndPlaybackShareOneSession", HlsPlaybackManifestTests.FallbackDurationAndPlaybackShareOneSession),
    ("HlsPlaybackManifest_BasePathPreservesProxyBoundary", HlsPlaybackManifestTests.BasePathPreservesProxyBoundary),
    ("HostedPlaybackLifecycle_StopDrainsWorkersAndDeadlineAllowsSafeUnwind", HostedPlaybackLifecycleTests.StopDrainsWorkersAndDeadlineAllowsSafeUnwind),
    ("MultiLibrary_WarmupScopesInventoriesAndSkipsUnindexedRoots", MultiLibraryTests.WarmupScopesInventoriesAndSkipsUnindexedRoots),
    ("NativePlayback_SelectedVoiceAndPinnedProbe", NativePlaybackSessionTests.NativeSelectionOpensOnlyChosenVoiceAndPinsProbe),
    ("NativePlayback_HlsEncodingPolicy", NativePlaybackSessionTests.HlsRequestsEncodeOnlyTheBoundManagedStream),
    ("NativePlayback_IndependentSessions", NativePlaybackSessionTests.ConcurrentOpensHaveIndependentSessionIdentity),
    ("NativePlayback_SingleFileVoiceChoices", NativePlaybackSessionTests.SingleFileRetainsDynamicVoiceChoices),
    ("NativePlayback_HostExportCreation", NativePlaybackSessionTests.HostExportCreationReceivesPlaybackLifecycle),
    ("NativePlayback_UnmanagedUnchanged", NativePlaybackSessionTests.UnmanagedPlaybackRetainsHostBehavior),
    ("NativePlayback_FailureAndCancellation", NativePlaybackSessionTests.FailedOrCanceledOpenNeverPublishesAnUnprobedStream),
    ("HlsPlaybackManifest_MasterResolvesOnlyPlaylistsAndPreservesPinnedMaster", HlsPlaybackManifestTests.MasterResolvesOnlyPlaylistsAndPreservesPinnedMaster),
    ("HlsPlaybackManifest_RejectsUnstableIncompleteAndInvalidManifests", HlsPlaybackManifestTests.RejectsUnstableIncompleteAndInvalidManifests),
    ("YummyKodikMediaSegmentProvider_ClonesCachedSegmentsPerItem", YummyKodikMediaSegmentProvider_ClonesCachedSegmentsPerItem),
    ("ResolveProviderCoverage_UsesYummyHintWhenKodikSeriesCountIsZero", ResolveProviderCoverage_UsesYummyHintWhenKodikSeriesCountIsZero),
    ("ResolveProviderCoverage_PreservesYummyCoverageBeyondKodik", ResolveProviderCoverage_PreservesYummyCoverageBeyondKodik),
    ("GenerateKodikEpisodeFilesAsync_FillsMissingTranslationsForExistingEpisode", GenerateKodikEpisodeFilesAsync_FillsMissingTranslationsForExistingEpisode),
    ("ResolveEpisodesNeedingKodikTranslation_SkipsYummyCoveredPairs", ResolveEpisodesNeedingKodikTranslation_SkipsYummyCoveredPairs),
    ("EpisodeArtifactMaintenance_NormalizesEquivalentTranslationVariants", EpisodeArtifactMaintenance_NormalizesEquivalentTranslationVariants),
    ("ResolveEpisodeTranslationFileBaseName_ReusesExistingEquivalentArtifactName", ResolveEpisodeTranslationFileBaseName_ReusesExistingEquivalentArtifactName),
    ("KeepLatestEpisodePerResolvedLink_DropsEarlierEpisodesWhenKodikReusesSameVideo", KeepLatestEpisodePerResolvedLink_DropsEarlierEpisodesWhenKodikReusesSameVideo),
    ("CleanupUnexpectedEpisodeArtifacts_RemovesStaleFilesBeyondExpectedCoverage", CleanupUnexpectedEpisodeArtifacts_RemovesStaleFilesBeyondExpectedCoverage),
    ("RefreshAudit_KodikOutagePreservesYummyGeneration", MultiLibraryTests.KodikOutagePreservesYummyGeneration),
    ("RefreshAudit_TruncatedXmlRebuilt", RefreshAuditTests.TruncatedXmlRebuilt),
    ("RefreshAudit_LongEpisodeNumbers", RefreshAuditTests.LongEpisodeNumbers),
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
    ("StaleReleaseCleanup_AllowsHostMetadataAndRenamedPoster", StaleReleaseCleanup_AllowsHostMetadataAndRenamedPoster),
    ("StaleReleaseCleanup_AcceptsKnownOlderOwnershipManifests", StaleReleaseCleanup_AcceptsKnownOlderOwnershipManifests),
    ("StaleReleaseCleanup_RejectsUnknownStreamsAndMalformedMetadata", StaleReleaseCleanup_RejectsUnknownStreamsAndMalformedMetadata),
    ("StaleReleaseCleanup_HostMetadataDoesNotHideModifiedStream", StaleReleaseCleanup_HostMetadataDoesNotHideModifiedStream),
    ("StaleReleaseCleanup_RetainsCurrentAndManualKeys", StaleReleaseCleanup_RetainsCurrentAndManualKeys),
    ("StaleReleaseCleanup_SkipsMissingCorruptAndAmbiguousState", StaleReleaseCleanup_SkipsMissingCorruptAndAmbiguousState),
    ("StaleReleaseCleanup_SkipsSeasonWithUnknownFile", StaleReleaseCleanup_SkipsSeasonWithUnknownFile),
    ("StaleReleaseCleanup_SkipsSeasonWithModifiedManagedFile", StaleReleaseCleanup_SkipsSeasonWithModifiedManagedFile),
    ("StaleReleaseCleanup_CanonicalizesPlainAndUrlCurrentKeys", StaleReleaseCleanup_CanonicalizesPlainAndUrlCurrentKeys),
    ("StaleReleaseCleanup_SkipsUnknownGenerationContract", StaleReleaseCleanup_SkipsUnknownGenerationContract),
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
    ("YummyKodikStreamController_NormalizesKnownManagedVoices", YummyKodikStreamController_NormalizesKnownManagedVoices),
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
    ("EpisodeVersionsMerge_NativeOwnershipTransfersToExplicitChoiceAndPreservesExternalLinks", EpisodeVersionsMergeTests.NativeOwnershipTransfersToExplicitChoiceAndPreservesExternalLinks),
    ("EpisodeVersionsMerge_IncompleteNativeMemberUsesProvenFileIdentity", EpisodeVersionsMergeTests.IncompleteNativeMemberUsesProvenFileIdentity),
    ("EpisodeVersionsMerge_IncompleteNativeMemberRejectsAmbiguousIdentity", EpisodeVersionsMergeTests.IncompleteNativeMemberRejectsAmbiguousIdentity),
    ("EpisodeVersionsMerge_IncompleteNativeMemberRevalidatesProofBeforeSaving", EpisodeVersionsMergeTests.IncompleteNativeMemberRevalidatesProofBeforeSaving),
    ("LateNativeMerge_CycleDuringAuthoritativePassSurvivesBatchRelease", LateNativeMergeTests.CycleDuringAuthoritativePassSurvivesBatchRelease),
    ("LateNativeMerge_CycleAfterAuthoritativePassSurvivesBatchRelease", LateNativeMergeTests.CycleAfterAuthoritativePassSurvivesBatchRelease),
    ("LateNativeMerge_RepeatedCyclesCoalesceAndBusyWorkerRetainsRequest", LateNativeMergeTests.RepeatedCyclesCoalesceAndBusyWorkerRetainsRequest),
    ("LateNativeMerge_NormalizedUpdatesDoNotQueueAndStopUnsubscribes", LateNativeMergeTests.NormalizedUpdatesDoNotQueueAndStopUnsubscribes),
    ("LateNativeMerge_IncompleteCycleUsesSplitProviderAndTopologyWitnesses", LateNativeMergeTests.IncompleteCycleUsesSplitProviderAndTopologyWitnesses),
    ("LateNativeMerge_CycleDuringBatchRepairsOnlyTargetDespitePendingGeneralMerge", LateNativeMergeTests.CycleDuringBatchRepairsOnlyTargetDespitePendingGeneralMerge),
    ("LateNativeMerge_QueuedCycleAlreadyRepairedCausesNoWrites", LateNativeMergeTests.QueuedCycleAlreadyRepairedCausesNoWrites),
    ("NativeEpisodeResolver_RepeatedResolutionPreservesSelectedOwnerWithoutMutatingStoredItems", NativeEpisodeResolverTests.RepeatedResolutionPreservesSelectedOwnerWithoutMutatingStoredItems),
    ("NativeEpisodeResolver_AmbiguousOrMixedSeasonsFallBackWithoutMutation", NativeEpisodeResolverTests.AmbiguousOrMixedSeasonsFallBackWithoutMutation),
    ("NativeEpisodeResolver_SidecarsAndSingletonsArePreservedAndResolverIsRegisteredBeforeHost", NativeEpisodeResolverTests.SidecarsAndSingletonsArePreservedAndResolverIsRegisteredBeforeHost),
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
    ("AllohaBrowserIdentity_CoalescesAndPersistsVerifiedIdentity", AllohaBrowserIdentityTests.CoalescesAndPersistsVerifiedIdentity),
    ("AllohaBrowserIdentity_RejectsBadMetadataAndBacksOff", AllohaBrowserIdentityTests.RejectsBadMetadataAndBacksOff),
    ("AllohaBrowserIdentity_FailedVideoAndCancellationKeepCurrent", AllohaBrowserIdentityTests.FailedVideoAndCancellationKeepCurrent),
    ("AllohaBrowserIdentity_IgnoresCorruptCache", AllohaBrowserIdentityTests.IgnoresCorruptCache),
    ("AllohaBrowserIdentity_BoundsMetadataTimeout", AllohaBrowserIdentityTests.BoundsMetadataTimeout),
    ("AllohaBrowserIdentity_Repairs403AndPinsExistingSessions", AllohaBrowserIdentityTests.Repairs403AndPinsExistingSessions),
    ("AllohaBrowserIdentity_RecoversRejectedCachedIdentity", AllohaBrowserIdentityTests.RecoversRejectedCachedIdentity),
    ("AllohaBrowserIdentity_FailedBootstrapDoesNotReplaceCache", AllohaBrowserIdentityTests.FailedBootstrapDoesNotReplaceCache),
    ("AllohaResolution_BoundsAndCoalescesFailedResolution", AllohaResolutionTests.BoundsAndCoalescesFailedResolution),
    ("AllohaBrowserIdentity_RecoversImmediatelyAfterPromotion", AllohaBrowserIdentityTests.RecoversImmediatelyAfterPromotion),
    ("AllohaReadiness_RenewalCancellationDoesNotRetryToken", AllohaReadinessTests.RenewalCancellationDoesNotRetryToken),
    ("AllohaReadiness_RenewalDeadlineStopsFailedRecovery", AllohaReadinessTests.RenewalDeadlineStopsFailedRecovery),
    ("AllohaReadiness_RenewalDeadlineIncludesTokenProof", AllohaReadinessTests.RenewalDeadlineIncludesTokenProof),
    ("AllohaReadiness_ChecksRootRelativeProxyUrls", AllohaReadinessTests.ChecksRootRelativeProxyUrls),
    ("AllohaReadiness_RejectsForbiddenVideo", AllohaReadinessTests.RejectsForbiddenVideo),
    ("AllohaReadiness_ChecksDependenciesAndReusesFirstFragment", AllohaReadinessTests.ChecksDependenciesAndReusesFirstFragment),
    ("AllohaReadiness_RecoversVideoWithFreshToken", AllohaReadinessTests.RecoversVideoWithFreshToken),
    ("AllohaReadiness_FailedRetryStartsCooldown", AllohaReadinessTests.FailedRetryStartsCooldown),
    ("AllohaReadiness_FailedNetworkRetryStartsCooldown", AllohaReadinessTests.FailedNetworkRetryStartsCooldown),
    ("AllohaReadiness_CancellationDoesNotPoisonSession", AllohaReadinessTests.CancellationDoesNotPoisonSession),
    ("AllohaReadiness_BoundsPlaylistTraversal", AllohaReadinessTests.BoundsPlaylistTraversal),
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
