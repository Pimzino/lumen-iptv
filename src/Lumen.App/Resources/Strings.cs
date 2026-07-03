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
    public static string Nav_Settings => Get(nameof(Nav_Settings));

    public static string Shell_Menu => Get(nameof(Shell_Menu));
    public static string Shell_SearchPlaceholder => Get(nameof(Shell_SearchPlaceholder));
    public static string Shell_Profiles => Get(nameof(Shell_Profiles));
    public static string Shell_AddProfile => Get(nameof(Shell_AddProfile));

    public static string Common_Cancel => Get(nameof(Common_Cancel));
    public static string Common_Back => Get(nameof(Common_Back));
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

    public static string Home_ContinueWatching => Get(nameof(Home_ContinueWatching));
    public static string Home_FavoriteChannels => Get(nameof(Home_FavoriteChannels));
    public static string Home_RecentlyAddedMovies => Get(nameof(Home_RecentlyAddedMovies));
    public static string Home_RecentlyAddedSeries => Get(nameof(Home_RecentlyAddedSeries));
    public static string Home_EmptyBody => Get(nameof(Home_EmptyBody));
    public static string Home_RecentlyAdded => Get(nameof(Home_RecentlyAdded));

    public static string Settings_Title => Get(nameof(Settings_Title));
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

    public static string Toast_ProfileConnectedFormat => Get(nameof(Toast_ProfileConnectedFormat));
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
    public static string LiveTv_NowLabel => Get(nameof(LiveTv_NowLabel));
    public static string LiveTv_NextLabel => Get(nameof(LiveTv_NextLabel));
    public static string LiveTv_NoGuideData => Get(nameof(LiveTv_NoGuideData));
    public static string Player_ReconnectingFormat => Get(nameof(Player_ReconnectingFormat));
    public static string Player_ErrorTitle => Get(nameof(Player_ErrorTitle));
    public static string Player_Audio => Get(nameof(Player_Audio));
    public static string Player_Subtitles => Get(nameof(Player_Subtitles));
    public static string Player_Channels => Get(nameof(Player_Channels));
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
    public static string Vod_Play => Get(nameof(Vod_Play));
    public static string Vod_StartOver => Get(nameof(Vod_StartOver));
    public static string Vod_AddFavorite => Get(nameof(Vod_AddFavorite));
    public static string Vod_RemoveFavorite => Get(nameof(Vod_RemoveFavorite));
    public static string Vod_NoItems => Get(nameof(Vod_NoItems));
    public static string Vod_Episodes => Get(nameof(Vod_Episodes));
    public static string Home_ContinueWatchingResume => Get(nameof(Home_ContinueWatchingResume));

    public static string Search_Placeholder => Get(nameof(Search_Placeholder));
    public static string Search_GroupProgrammes => Get(nameof(Search_GroupProgrammes));
    public static string Search_Prompt => Get(nameof(Search_Prompt));
    public static string Search_NoResultsFormat => Get(nameof(Search_NoResultsFormat));
    public static string Favorites_Empty => Get(nameof(Favorites_Empty));
    public static string Favorites_Remove => Get(nameof(Favorites_Remove));
}
