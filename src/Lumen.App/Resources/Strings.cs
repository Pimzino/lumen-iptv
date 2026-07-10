using System.Globalization;
using System.Resources;

namespace Lumen.App.Resources;

/// <summary>
/// Strongly-typed access to Strings.resx. Localization-ready: values come from the
/// ResourceManager (satellite assemblies would plug in transparently); en-US ships.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager Resources =
        new("Lumen.App.Resources.Strings", typeof(Strings).Assembly);

    private static string Get(string key) =>
        Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string Format(string format, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, format, args);

    public static string App_Title => Get(nameof(App_Title));

    public static string Settings_Artwork => Get(nameof(Settings_Artwork));
    public static string Settings_ArtworkOnline => Get(nameof(Settings_ArtworkOnline));
    public static string Settings_ArtworkOnlineHint => Get(nameof(Settings_ArtworkOnlineHint));
    public static string Settings_ArtworkTmdbKey => Get(nameof(Settings_ArtworkTmdbKey));
    public static string Settings_ArtworkTmdbKeyHint => Get(nameof(Settings_ArtworkTmdbKeyHint));
    public static string Crash_Title => Get(nameof(Crash_Title));
    public static string Crash_Body => Get(nameof(Crash_Body));
    public static string Crash_OpenLogs => Get(nameof(Crash_OpenLogs));
    public static string Crash_StartupFailed => Get(nameof(Crash_StartupFailed));

    public static string Nav_Home => Get(nameof(Nav_Home));
    public static string Nav_LiveTv => Get(nameof(Nav_LiveTv));
    public static string Nav_Guide => Get(nameof(Nav_Guide));
    public static string Nav_Movies => Get(nameof(Nav_Movies));
    public static string Nav_Series => Get(nameof(Nav_Series));
    public static string Nav_Search => Get(nameof(Nav_Search));
    public static string Nav_Favorites => Get(nameof(Nav_Favorites));
    public static string Nav_Downloads => Get(nameof(Nav_Downloads));
    public static string Nav_Settings => Get(nameof(Nav_Settings));

    public static string Shell_Menu => Get(nameof(Shell_Menu));
    public static string Shell_SearchPlaceholder => Get(nameof(Shell_SearchPlaceholder));
    public static string Shell_Profiles => Get(nameof(Shell_Profiles));
    public static string Shell_AddProfile => Get(nameof(Shell_AddProfile));

    public static string Common_Cancel => Get(nameof(Common_Cancel));
    public static string Common_Back => Get(nameof(Common_Back));
    public static string Common_Edit => Get(nameof(Common_Edit));
    public static string Common_Continue => Get(nameof(Common_Continue));
    public static string Common_Done => Get(nameof(Common_Done));
    public static string Common_Remove => Get(nameof(Common_Remove));
    public static string Common_Retry => Get(nameof(Common_Retry));
    public static string Common_Close => Get(nameof(Common_Close));

    public static string Window_Minimize => Get(nameof(Window_Minimize));
    public static string Window_Maximize => Get(nameof(Window_Maximize));
    public static string Window_Restore => Get(nameof(Window_Restore));
    public static string Window_Close => Get(nameof(Window_Close));
    public static string Player_ExitFullscreen => Get(nameof(Player_ExitFullscreen));
    public static string Player_PictureInPicture => Get(nameof(Player_PictureInPicture));
    public static string Player_Pin => Get(nameof(Player_Pin));
    public static string Player_Unpin => Get(nameof(Player_Unpin));
    public static string Common_Refresh => Get(nameof(Common_Refresh));
    public static string Common_Browse => Get(nameof(Common_Browse));
    public static string Common_Loading => Get(nameof(Common_Loading));
    public static string Common_ComingSoon => Get(nameof(Common_ComingSoon));

    public static string Onboarding_WelcomeTitle => Get(nameof(Onboarding_WelcomeTitle));
    public static string Onboarding_WelcomeBody => Get(nameof(Onboarding_WelcomeBody));
    public static string Onboarding_GetStarted => Get(nameof(Onboarding_GetStarted));
    public static string Onboarding_AddServiceTitle => Get(nameof(Onboarding_AddServiceTitle));
    public static string Onboarding_AddServiceBody => Get(nameof(Onboarding_AddServiceBody));
    public static string Onboarding_TabXtream => Get(nameof(Onboarding_TabXtream));
    public static string Onboarding_TabM3u => Get(nameof(Onboarding_TabM3u));
    public static string Onboarding_ProfileName => Get(nameof(Onboarding_ProfileName));
    public static string Onboarding_Server => Get(nameof(Onboarding_Server));
    public static string Onboarding_Username => Get(nameof(Onboarding_Username));
    public static string Onboarding_Password => Get(nameof(Onboarding_Password));
    public static string Onboarding_PlaylistSource => Get(nameof(Onboarding_PlaylistSource));
    public static string Onboarding_TestConnection => Get(nameof(Onboarding_TestConnection));
    public static string Onboarding_Testing => Get(nameof(Onboarding_Testing));
    public static string Onboarding_XtreamOkFormat => Get(nameof(Onboarding_XtreamOkFormat));
    public static string Onboarding_XtreamOkNoExpiry => Get(nameof(Onboarding_XtreamOkNoExpiry));
    public static string Onboarding_XtreamExpired => Get(nameof(Onboarding_XtreamExpired));
    public static string Onboarding_M3uOkFormat => Get(nameof(Onboarding_M3uOkFormat));
    public static string Onboarding_EpgTitle => Get(nameof(Onboarding_EpgTitle));
    public static string Onboarding_EpgBodyXtream => Get(nameof(Onboarding_EpgBodyXtream));
    public static string Onboarding_EpgBodyM3u => Get(nameof(Onboarding_EpgBodyM3u));
    public static string Onboarding_EpgSource => Get(nameof(Onboarding_EpgSource));
    public static string Onboarding_ImportEpgNow => Get(nameof(Onboarding_ImportEpgNow));
    public static string Onboarding_Finish => Get(nameof(Onboarding_Finish));
    public static string Onboarding_WorkingTitle => Get(nameof(Onboarding_WorkingTitle));
    public static string Onboarding_StepChannels => Get(nameof(Onboarding_StepChannels));
    public static string Onboarding_StepEpg => Get(nameof(Onboarding_StepEpg));
    public static string Onboarding_SyncSummaryFormat => Get(nameof(Onboarding_SyncSummaryFormat));

    public static string ProfileEdit_Title => Get(nameof(ProfileEdit_Title));
    public static string ProfileEdit_AvatarColor => Get(nameof(ProfileEdit_AvatarColor));
    public static string ProfileEdit_PasswordHint => Get(nameof(ProfileEdit_PasswordHint));
    public static string ProfileEdit_PasswordRequired => Get(nameof(ProfileEdit_PasswordRequired));
    public static string ProfileEdit_EpgHintXtream => Get(nameof(ProfileEdit_EpgHintXtream));
    public static string ProfileEdit_Save => Get(nameof(ProfileEdit_Save));
    public static string ProfileEdit_RefreshingChannels => Get(nameof(ProfileEdit_RefreshingChannels));
    public static string ProfileEdit_RefreshingGuide => Get(nameof(ProfileEdit_RefreshingGuide));

    public static string Home_ContinueWatching => Get(nameof(Home_ContinueWatching));
    public static string Home_FavoriteChannels => Get(nameof(Home_FavoriteChannels));
    public static string Home_RecentlyAddedMovies => Get(nameof(Home_RecentlyAddedMovies));
    public static string Home_RecentlyAddedSeries => Get(nameof(Home_RecentlyAddedSeries));
    public static string Home_EmptyBody => Get(nameof(Home_EmptyBody));
    public static string Home_RecentlyAdded => Get(nameof(Home_RecentlyAdded));
    public static string Home_RecentlyWatched => Get(nameof(Home_RecentlyWatched));
    public static string Home_JumpBackIn => Get(nameof(Home_JumpBackIn));
    public static string Home_EmptyTitle => Get(nameof(Home_EmptyTitle));
    public static string Home_GreetingMorning => Get(nameof(Home_GreetingMorning));
    public static string Home_GreetingAfternoon => Get(nameof(Home_GreetingAfternoon));
    public static string Home_GreetingEvening => Get(nameof(Home_GreetingEvening));
    public static string Home_GreetingNight => Get(nameof(Home_GreetingNight));
    public static string Home_TimeLeftFormat => Get(nameof(Home_TimeLeftFormat));
    public static string Home_KindLive => Get(nameof(Home_KindLive));
    public static string Home_KindMovie => Get(nameof(Home_KindMovie));
    public static string Home_KindSeries => Get(nameof(Home_KindSeries));
    public static string Home_AgoJustNow => Get(nameof(Home_AgoJustNow));
    public static string Home_AgoMinutesFormat => Get(nameof(Home_AgoMinutesFormat));
    public static string Home_AgoHoursFormat => Get(nameof(Home_AgoHoursFormat));
    public static string Home_AgoYesterday => Get(nameof(Home_AgoYesterday));
    public static string Home_AgoDaysFormat => Get(nameof(Home_AgoDaysFormat));

    public static string Settings_Title => Get(nameof(Settings_Title));

    // Settings sidebar navigation labels.
    public static string SettingsNav_Account => Get(nameof(SettingsNav_Account));
    public static string SettingsNav_Playback => Get(nameof(SettingsNav_Playback));
    public static string SettingsNav_Guide => Get(nameof(SettingsNav_Guide));
    public static string SettingsNav_Integrations => Get(nameof(SettingsNav_Integrations));
    public static string SettingsNav_Storage => Get(nameof(SettingsNav_Storage));
    public static string SettingsNav_Appearance => Get(nameof(SettingsNav_Appearance));
    public static string SettingsNav_About => Get(nameof(SettingsNav_About));
    public static string Settings_Profiles => Get(nameof(Settings_Profiles));
    public static string Settings_ProfileActive => Get(nameof(Settings_ProfileActive));
    public static string Settings_ProfileSwitch => Get(nameof(Settings_ProfileSwitch));
    public static string Settings_RemoveProfileTitle => Get(nameof(Settings_RemoveProfileTitle));
    public static string Settings_RemoveProfileBodyFormat => Get(nameof(Settings_RemoveProfileBodyFormat));
    public static string Settings_Epg => Get(nameof(Settings_Epg));
    public static string Settings_EpgRefreshNow => Get(nameof(Settings_EpgRefreshNow));
    public static string Settings_EpgCountsFormat => Get(nameof(Settings_EpgCountsFormat));
    public static string Settings_EpgProgressFormat => Get(nameof(Settings_EpgProgressFormat));
    public static string Settings_EpgInterval => Get(nameof(Settings_EpgInterval));
    public static string Settings_EpgInterval6h => Get(nameof(Settings_EpgInterval6h));
    public static string Settings_EpgInterval12h => Get(nameof(Settings_EpgInterval12h));
    public static string Settings_EpgInterval24h => Get(nameof(Settings_EpgInterval24h));
    public static string Settings_EpgIntervalManual => Get(nameof(Settings_EpgIntervalManual));
    public static string Settings_CatalogRefreshNow => Get(nameof(Settings_CatalogRefreshNow));
    public static string Settings_Playback => Get(nameof(Settings_Playback));
    public static string Settings_PreferHls => Get(nameof(Settings_PreferHls));
    public static string Settings_HardwareAcceleration => Get(nameof(Settings_HardwareAcceleration));
    public static string Settings_StreamUserAgent => Get(nameof(Settings_StreamUserAgent));
    public static string Settings_StreamUserAgentHint => Get(nameof(Settings_StreamUserAgentHint));
    public static string Settings_Storage => Get(nameof(Settings_Storage));
    public static string Settings_ImageCacheFormat => Get(nameof(Settings_ImageCacheFormat));
    public static string Settings_ClearImageCache => Get(nameof(Settings_ClearImageCache));
    public static string Settings_ParentalControls => Get(nameof(Settings_ParentalControls));
    public static string Settings_Theme => Get(nameof(Settings_Theme));
    public static string Settings_ThemeDark => Get(nameof(Settings_ThemeDark));
    public static string Settings_ThemeLight => Get(nameof(Settings_ThemeLight));
    public static string Settings_About => Get(nameof(Settings_About));
    public static string Settings_VersionFormat => Get(nameof(Settings_VersionFormat));
    public static string Settings_Licenses => Get(nameof(Settings_Licenses));

    public static string Settings_Account => Get(nameof(Settings_Account));
    public static string Settings_AccountStatus => Get(nameof(Settings_AccountStatus));
    public static string Settings_AccountExpiry => Get(nameof(Settings_AccountExpiry));
    public static string Settings_AccountConnections => Get(nameof(Settings_AccountConnections));
    public static string Settings_AccountTrial => Get(nameof(Settings_AccountTrial));
    public static string Settings_AccountCreated => Get(nameof(Settings_AccountCreated));
    public static string Settings_AccountFormats => Get(nameof(Settings_AccountFormats));
    public static string Settings_AccountServerTime => Get(nameof(Settings_AccountServerTime));
    public static string Settings_AccountServerTimeFormat => Get(nameof(Settings_AccountServerTimeFormat));
    public static string Settings_AccountConnectionsFormat => Get(nameof(Settings_AccountConnectionsFormat));
    public static string Settings_AccountConnectionsAvailableFormat => Get(nameof(Settings_AccountConnectionsAvailableFormat));
    public static string Settings_AccountConnectionsInUseOnlyFormat => Get(nameof(Settings_AccountConnectionsInUseOnlyFormat));
    public static string Settings_AccountExpiryNever => Get(nameof(Settings_AccountExpiryNever));
    public static string Settings_AccountExpiryExpiredFormat => Get(nameof(Settings_AccountExpiryExpiredFormat));
    public static string Settings_AccountExpiryInDaysFormat => Get(nameof(Settings_AccountExpiryInDaysFormat));
    public static string Settings_AccountExpiryTomorrowFormat => Get(nameof(Settings_AccountExpiryTomorrowFormat));
    public static string Settings_AccountExpiresSoonWarning => Get(nameof(Settings_AccountExpiresSoonWarning));
    public static string Settings_AccountAllConnectionsWarning => Get(nameof(Settings_AccountAllConnectionsWarning));
    public static string Settings_AccountLoadFailed => Get(nameof(Settings_AccountLoadFailed));
    public static string Settings_AccountTrialYes => Get(nameof(Settings_AccountTrialYes));
    public static string Settings_AccountTrialNo => Get(nameof(Settings_AccountTrialNo));

    public static string Toast_ProfileConnectedFormat => Get(nameof(Toast_ProfileConnectedFormat));
    public static string Toast_ProfileUpdatedFormat => Get(nameof(Toast_ProfileUpdatedFormat));
    public static string Toast_ProfileSwitchedFormat => Get(nameof(Toast_ProfileSwitchedFormat));
    public static string Toast_EpgRefreshedFormat => Get(nameof(Toast_EpgRefreshedFormat));
    public static string Toast_EpgFailed => Get(nameof(Toast_EpgFailed));
    public static string Toast_CatalogRefreshedFormat => Get(nameof(Toast_CatalogRefreshedFormat));
    public static string Toast_CacheCleared => Get(nameof(Toast_CacheCleared));
    public static string Toast_SyncFailedFormat => Get(nameof(Toast_SyncFailedFormat));

    public static string Placeholder_Body => Get(nameof(Placeholder_Body));

    public static string Badge_Live => Get(nameof(Badge_Live));
    public static string LiveTv_Watch => Get(nameof(LiveTv_Watch));
    public static string LiveTv_MutedPreview => Get(nameof(LiveTv_MutedPreview));
    public static string LiveTv_NoChannels => Get(nameof(LiveTv_NoChannels));
    public static string LiveTv_NoFavorites => Get(nameof(LiveTv_NoFavorites));
    public static string LiveTv_NowLabel => Get(nameof(LiveTv_NowLabel));
    public static string LiveTv_NextLabel => Get(nameof(LiveTv_NextLabel));
    public static string LiveTv_NoGuideData => Get(nameof(LiveTv_NoGuideData));
    public static string LiveTv_ChannelCountOfFormat => Get(nameof(LiveTv_ChannelCountOfFormat));
    public static string Player_ReconnectingFormat => Get(nameof(Player_ReconnectingFormat));
    public static string Player_ErrorTitle => Get(nameof(Player_ErrorTitle));
    public static string Player_Audio => Get(nameof(Player_Audio));
    public static string Player_Subtitles => Get(nameof(Player_Subtitles));
    public static string Player_Channels => Get(nameof(Player_Channels));
    public static string Player_GoToLive => Get(nameof(Player_GoToLive));
    public static string Player_RestartProgramme => Get(nameof(Player_RestartProgramme));
    public static string Player_LiveSeek => Get(nameof(Player_LiveSeek));
    public static string Player_Opening => Get(nameof(Player_Opening));
    public static string Player_BufferingFormat => Get(nameof(Player_BufferingFormat));

    public static string Guide_JumpToNow => Get(nameof(Guide_JumpToNow));
    public static string Guide_NoData => Get(nameof(Guide_NoData));
    public static string Guide_WatchChannel => Get(nameof(Guide_WatchChannel));
    public static string Guide_AddReminder => Get(nameof(Guide_AddReminder));
    public static string Guide_ReminderSetFormat => Get(nameof(Guide_ReminderSetFormat));
    public static string Settings_ChannelMapping => Get(nameof(Settings_ChannelMapping));
    public static string Settings_ChannelMappingBody => Get(nameof(Settings_ChannelMappingBody));
    public static string Settings_MappingAllMatched => Get(nameof(Settings_MappingAllMatched));
    public static string Settings_MappingUnmatchedFormat => Get(nameof(Settings_MappingUnmatchedFormat));
    public static string Settings_MappingNone => Get(nameof(Settings_MappingNone));

    public static string Vod_Sort => Get(nameof(Vod_Sort));
    public static string Vod_SearchPlaceholder => Get(nameof(Vod_SearchPlaceholder));
    public static string Vod_Play => Get(nameof(Vod_Play));
    public static string Vod_StartOver => Get(nameof(Vod_StartOver));
    public static string Vod_AddFavorite => Get(nameof(Vod_AddFavorite));
    public static string Vod_RemoveFavorite => Get(nameof(Vod_RemoveFavorite));
    public static string Vod_NoItems => Get(nameof(Vod_NoItems));
    public static string Vod_Episodes => Get(nameof(Vod_Episodes));
    public static string Vod_SeasonFormat => Get(nameof(Vod_SeasonFormat));
    public static string Vod_Specials => Get(nameof(Vod_Specials));
    public static string Vod_OneSeason => Get(nameof(Vod_OneSeason));
    public static string Vod_SeasonsCountFormat => Get(nameof(Vod_SeasonsCountFormat));
    public static string Vod_OneEpisode => Get(nameof(Vod_OneEpisode));
    public static string Vod_EpisodesCountFormat => Get(nameof(Vod_EpisodesCountFormat));
    public static string Vod_ResumeEpisodeFormat => Get(nameof(Vod_ResumeEpisodeFormat));
    public static string Vod_PlayEpisodeFormat => Get(nameof(Vod_PlayEpisodeFormat));
    public static string Vod_NoEpisodes => Get(nameof(Vod_NoEpisodes));
    public static string Vod_CastLabel => Get(nameof(Vod_CastLabel));
    public static string Vod_DirectorLabel => Get(nameof(Vod_DirectorLabel));
    public static string Vod_Watched => Get(nameof(Vod_Watched));
    public static string Vod_WatchedOnFormat => Get(nameof(Vod_WatchedOnFormat));
    public static string Vod_MarkWatched => Get(nameof(Vod_MarkWatched));
    public static string Vod_MarkUnwatched => Get(nameof(Vod_MarkUnwatched));
    public static string Vod_WatchedCountFormat => Get(nameof(Vod_WatchedCountFormat));
    public static string Vod_MarkSeasonWatched => Get(nameof(Vod_MarkSeasonWatched));
    public static string Vod_MarkEpisodeWatched => Get(nameof(Vod_MarkEpisodeWatched));
    public static string Vod_MarkEpisodeUnwatched => Get(nameof(Vod_MarkEpisodeUnwatched));
    public static string Home_ContinueWatchingResume => Get(nameof(Home_ContinueWatchingResume));

    // Downloads (offline VOD).
    public static string Downloads_Title => Get(nameof(Downloads_Title));
    public static string Downloads_Empty => Get(nameof(Downloads_Empty));
    public static string Downloads_InProgress => Get(nameof(Downloads_InProgress));
    public static string Downloads_Completed => Get(nameof(Downloads_Completed));
    public static string Downloads_Download => Get(nameof(Downloads_Download));
    public static string Downloads_Downloading => Get(nameof(Downloads_Downloading));
    public static string Downloads_DownloadingPercentFormat => Get(nameof(Downloads_DownloadingPercentFormat));
    public static string Downloads_Queued => Get(nameof(Downloads_Queued));
    public static string Downloads_Paused => Get(nameof(Downloads_Paused));
    public static string Downloads_Downloaded => Get(nameof(Downloads_Downloaded));
    public static string Downloads_Failed => Get(nameof(Downloads_Failed));
    public static string Downloads_DownloadSeason => Get(nameof(Downloads_DownloadSeason));
    public static string Downloads_Pause => Get(nameof(Downloads_Pause));
    public static string Downloads_Resume => Get(nameof(Downloads_Resume));
    public static string Downloads_PlayOffline => Get(nameof(Downloads_PlayOffline));
    public static string Downloads_OpenFolder => Get(nameof(Downloads_OpenFolder));
    public static string Downloads_RemoveTitle => Get(nameof(Downloads_RemoveTitle));
    public static string Downloads_RemoveBodyFormat => Get(nameof(Downloads_RemoveBodyFormat));
    public static string Toast_DownloadCompleteFormat => Get(nameof(Toast_DownloadCompleteFormat));
    public static string Toast_DownloadFailedFormat => Get(nameof(Toast_DownloadFailedFormat));
    public static string Settings_Downloads => Get(nameof(Settings_Downloads));
    public static string Settings_DownloadsFormat => Get(nameof(Settings_DownloadsFormat));
    public static string Settings_ClearDownloads => Get(nameof(Settings_ClearDownloads));
    public static string Settings_ClearDownloadsBody => Get(nameof(Settings_ClearDownloadsBody));
    public static string Toast_DownloadsCleared => Get(nameof(Toast_DownloadsCleared));

    // Live TV recordings.
    public static string Nav_Recordings => Get(nameof(Nav_Recordings));
    public static string Recordings_Title => Get(nameof(Recordings_Title));
    public static string Recordings_Empty => Get(nameof(Recordings_Empty));
    public static string Recordings_InProgress => Get(nameof(Recordings_InProgress));
    public static string Recordings_Saved => Get(nameof(Recordings_Saved));
    public static string Recordings_Completed => Get(nameof(Recordings_Completed));
    public static string Recordings_Stop => Get(nameof(Recordings_Stop));
    public static string Recordings_RemoveTitle => Get(nameof(Recordings_RemoveTitle));
    public static string Recordings_RemoveBodyFormat => Get(nameof(Recordings_RemoveBodyFormat));
    public static string Recordings_Rename => Get(nameof(Recordings_Rename));
    public static string Recordings_RenameTitle => Get(nameof(Recordings_RenameTitle));
    public static string Player_Record => Get(nameof(Player_Record));
    public static string Player_StopRecording => Get(nameof(Player_StopRecording));
    public static string Toast_RecordingStartedFormat => Get(nameof(Toast_RecordingStartedFormat));
    public static string Toast_RecordingSavedFormat => Get(nameof(Toast_RecordingSavedFormat));
    public static string Toast_RecordingFailedFormat => Get(nameof(Toast_RecordingFailedFormat));
    public static string Toast_RecordingBusyFormat => Get(nameof(Toast_RecordingBusyFormat));
    public static string Toast_RecordingUnresolvable => Get(nameof(Toast_RecordingUnresolvable));
    public static string Settings_Recordings => Get(nameof(Settings_Recordings));
    public static string Settings_ClearRecordings => Get(nameof(Settings_ClearRecordings));
    public static string Settings_ClearRecordingsBody => Get(nameof(Settings_ClearRecordingsBody));
    public static string Toast_RecordingsCleared => Get(nameof(Toast_RecordingsCleared));

    public static string Settings_Trakt => Get(nameof(Settings_Trakt));
    public static string Settings_TraktBody => Get(nameof(Settings_TraktBody));
    public static string Settings_TraktClientId => Get(nameof(Settings_TraktClientId));
    public static string Settings_TraktClientSecret => Get(nameof(Settings_TraktClientSecret));
    public static string Settings_TraktAppHint => Get(nameof(Settings_TraktAppHint));
    public static string Settings_TraktConnect => Get(nameof(Settings_TraktConnect));
    public static string Settings_TraktDisconnect => Get(nameof(Settings_TraktDisconnect));
    public static string Settings_TraktConnectedFormat => Get(nameof(Settings_TraktConnectedFormat));
    public static string Settings_TraktCodeFormat => Get(nameof(Settings_TraktCodeFormat));
    public static string Settings_TraktWaiting => Get(nameof(Settings_TraktWaiting));
    public static string Settings_TraktOpenActivate => Get(nameof(Settings_TraktOpenActivate));
    public static string Settings_TraktSyncNow => Get(nameof(Settings_TraktSyncNow));
    public static string Settings_TraktLastSyncFormat => Get(nameof(Settings_TraktLastSyncFormat));
    public static string Settings_TraktNeverSynced => Get(nameof(Settings_TraktNeverSynced));
    public static string Settings_TraktScrobbleToggle => Get(nameof(Settings_TraktScrobbleToggle));
    public static string Settings_TraktSyncToggle => Get(nameof(Settings_TraktSyncToggle));
    public static string Settings_TraktUnwatchHint => Get(nameof(Settings_TraktUnwatchHint));
    public static string Toast_TraktConnected => Get(nameof(Toast_TraktConnected));
    public static string Toast_TraktConnectFailed => Get(nameof(Toast_TraktConnectFailed));
    public static string Toast_TraktDisconnected => Get(nameof(Toast_TraktDisconnected));
    public static string Toast_TraktSyncDone => Get(nameof(Toast_TraktSyncDone));
    public static string Toast_TraktSyncFailed => Get(nameof(Toast_TraktSyncFailed));
    public static string Toast_TraktNeedCredentials => Get(nameof(Toast_TraktNeedCredentials));

    public static string Search_GroupProgrammes => Get(nameof(Search_GroupProgrammes));
    public static string Search_Searching => Get(nameof(Search_Searching));
    public static string Search_NoResultsFormat => Get(nameof(Search_NoResultsFormat));
    public static string Filter_CategoriesPlaceholder => Get(nameof(Filter_CategoriesPlaceholder));
    public static string Filter_ChannelsPlaceholder => Get(nameof(Filter_ChannelsPlaceholder));
    public static string Filter_NoMatches => Get(nameof(Filter_NoMatches));
    public static string Filter_TypeToFilterPlaceholder => Get(nameof(Filter_TypeToFilterPlaceholder));
    public static string Favorites_Empty => Get(nameof(Favorites_Empty));
    public static string Favorites_Remove => Get(nameof(Favorites_Remove));

    public static string Support_BuyCoffee => Get(nameof(Support_BuyCoffee));
    public static string Support_MaybeLater => Get(nameof(Support_MaybeLater));
    public static string Support_PromptTitle => Get(nameof(Support_PromptTitle));
    public static string Support_PromptBody => Get(nameof(Support_PromptBody));
    public static string Support_AboutHeading => Get(nameof(Support_AboutHeading));
    public static string Support_AboutBody => Get(nameof(Support_AboutBody));
    public static string Support_ReminderToggle => Get(nameof(Support_ReminderToggle));
    public static string Support_ReminderHint => Get(nameof(Support_ReminderHint));

    public static string Update_RailName => Get(nameof(Update_RailName));
    public static string Update_RailAvailableTooltip => Get(nameof(Update_RailAvailableTooltip));
    public static string Update_RailDownloadingTooltip => Get(nameof(Update_RailDownloadingTooltip));
    public static string Update_RailReadyTooltip => Get(nameof(Update_RailReadyTooltip));
    public static string Update_DialogTitle => Get(nameof(Update_DialogTitle));
    public static string Update_AvailableFormat => Get(nameof(Update_AvailableFormat));
    public static string Update_CurrentFormat => Get(nameof(Update_CurrentFormat));
    public static string Update_ReleaseNotesHeading => Get(nameof(Update_ReleaseNotesHeading));
    public static string Update_StatusChecking => Get(nameof(Update_StatusChecking));
    public static string Update_StatusDownloadingFormat => Get(nameof(Update_StatusDownloadingFormat));
    public static string Update_StatusReady => Get(nameof(Update_StatusReady));
    public static string Update_StatusAvailable => Get(nameof(Update_StatusAvailable));
    public static string Update_StatusFailed => Get(nameof(Update_StatusFailed));
    public static string Update_InstallNow => Get(nameof(Update_InstallNow));
    public static string Update_SkipVersion => Get(nameof(Update_SkipVersion));
    public static string Update_Later => Get(nameof(Update_Later));
    public static string Update_OpenReleasePage => Get(nameof(Update_OpenReleasePage));
    public static string Update_ToastAvailableFormat => Get(nameof(Update_ToastAvailableFormat));
    public static string Update_ToastReadyFormat => Get(nameof(Update_ToastReadyFormat));
    public static string Update_ToastPostponed => Get(nameof(Update_ToastPostponed));
    public static string Update_ToastFailed => Get(nameof(Update_ToastFailed));

    public static string Settings_Updates => Get(nameof(Settings_Updates));
    public static string Settings_UpdatesAutoCheck => Get(nameof(Settings_UpdatesAutoCheck));
    public static string Settings_UpdatesAutoCheckHint => Get(nameof(Settings_UpdatesAutoCheckHint));
    public static string Settings_UpdatesFrequency => Get(nameof(Settings_UpdatesFrequency));
    public static string Settings_UpdatesFrequencyDaily => Get(nameof(Settings_UpdatesFrequencyDaily));
    public static string Settings_UpdatesFrequencyWeekly => Get(nameof(Settings_UpdatesFrequencyWeekly));
    public static string Settings_UpdatesPrerelease => Get(nameof(Settings_UpdatesPrerelease));
    public static string Settings_UpdatesPrereleaseHint => Get(nameof(Settings_UpdatesPrereleaseHint));
    public static string Settings_UpdatesCheckNow => Get(nameof(Settings_UpdatesCheckNow));
    public static string Settings_UpdatesChecking => Get(nameof(Settings_UpdatesChecking));
    public static string Settings_UpdatesStatusUpToDateFormat => Get(nameof(Settings_UpdatesStatusUpToDateFormat));
    public static string Settings_UpdatesStatusAvailableFormat => Get(nameof(Settings_UpdatesStatusAvailableFormat));
    public static string Settings_UpdatesLastCheckedFormat => Get(nameof(Settings_UpdatesLastCheckedFormat));
    public static string Settings_UpdatesPortableNote => Get(nameof(Settings_UpdatesPortableNote));
}
