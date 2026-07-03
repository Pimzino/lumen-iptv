-- 0001: initial schema.
-- Conventions: snake_case names, unix-seconds INTEGER timestamps (UTC), enum values stored as INTEGER.

CREATE TABLE profiles (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  name TEXT NOT NULL,
  kind INTEGER NOT NULL,
  server_url TEXT NULL,
  username TEXT NULL,
  password_protected BLOB NULL,
  playlist_source TEXT NULL,
  playlist_is_file INTEGER NOT NULL DEFAULT 0,
  epg_source TEXT NULL,
  epg_is_file INTEGER NOT NULL DEFAULT 0,
  prefer_hls INTEGER NOT NULL DEFAULT 0,
  avatar_color TEXT NULL,
  created_utc INTEGER NOT NULL,
  last_used_utc INTEGER NULL
);

CREATE TABLE categories (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  profile_id INTEGER NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
  provider_category_id TEXT NOT NULL,
  kind INTEGER NOT NULL,
  name TEXT NOT NULL,
  sort_order INTEGER NOT NULL DEFAULT 0,
  content_kind_override INTEGER NULL
);

CREATE INDEX ix_categories_profile_kind ON categories(profile_id, kind);

CREATE TABLE channels (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  profile_id INTEGER NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
  category_id INTEGER NULL REFERENCES categories(id) ON DELETE SET NULL,
  provider_stream_id TEXT NULL,
  number INTEGER NULL,
  name TEXT NOT NULL,
  logo_url TEXT NULL,
  stream_url TEXT NULL,
  epg_channel_id TEXT NULL,
  tvg_shift_minutes INTEGER NOT NULL DEFAULT 0,
  user_agent TEXT NULL,
  referrer TEXT NULL,
  is_hidden INTEGER NOT NULL DEFAULT 0,
  added_utc INTEGER NOT NULL
);

CREATE INDEX ix_channels_profile_category ON channels(profile_id, category_id);
CREATE INDEX ix_channels_profile_name ON channels(profile_id, name COLLATE NOCASE);

CREATE TABLE epg_channels (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  profile_id INTEGER NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
  xmltv_id TEXT NOT NULL,
  display_name TEXT NULL,
  icon_url TEXT NULL,
  UNIQUE (profile_id, xmltv_id)
);

CREATE TABLE programmes (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  profile_id INTEGER NOT NULL,
  channel_xmltv_id TEXT NOT NULL,
  start_utc INTEGER NOT NULL,
  stop_utc INTEGER NOT NULL,
  title TEXT NOT NULL,
  description TEXT NULL,
  category TEXT NULL,
  episode_number TEXT NULL,
  icon_url TEXT NULL
);

CREATE INDEX ix_programmes_channel_time ON programmes(profile_id, channel_xmltv_id, start_utc, stop_utc);
CREATE INDEX ix_programmes_stop ON programmes(profile_id, stop_utc);

CREATE TABLE channel_epg_map (
  channel_id INTEGER PRIMARY KEY REFERENCES channels(id) ON DELETE CASCADE,
  xmltv_id TEXT NOT NULL,
  is_manual INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE favorites (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  profile_id INTEGER NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
  item_kind INTEGER NOT NULL,
  item_key TEXT NOT NULL,
  added_utc INTEGER NOT NULL,
  UNIQUE (profile_id, item_kind, item_key)
);

CREATE TABLE watch_history (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  profile_id INTEGER NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
  item_kind INTEGER NOT NULL,
  item_key TEXT NOT NULL,
  title TEXT NOT NULL,
  poster_url TEXT NULL,
  position_seconds REAL NOT NULL DEFAULT 0,
  duration_seconds REAL NOT NULL DEFAULT 0,
  watched_utc INTEGER NOT NULL,
  UNIQUE (profile_id, item_kind, item_key)
);

-- profile_id 0 holds app-global settings; per-profile rows use the profile id.
CREATE TABLE settings (
  profile_id INTEGER NOT NULL DEFAULT 0,
  key TEXT NOT NULL,
  value TEXT NOT NULL,
  PRIMARY KEY (profile_id, key)
);
