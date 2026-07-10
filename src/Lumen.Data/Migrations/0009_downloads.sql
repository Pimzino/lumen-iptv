-- 0009: offline downloads/recordings of movies and series episodes.

CREATE TABLE downloads (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  profile_id INTEGER NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
  kind INTEGER NOT NULL,
  item_key TEXT NOT NULL,
  series_item_key TEXT NULL,
  provider_item_id TEXT NOT NULL,
  container_extension TEXT NULL,
  stream_url TEXT NULL,
  title TEXT NOT NULL,
  poster_url TEXT NULL,
  season INTEGER NULL,
  episode_number INTEGER NULL,
  is_hls INTEGER NOT NULL DEFAULT 0,
  file_path TEXT NOT NULL,
  status INTEGER NOT NULL,
  total_bytes INTEGER NULL,
  downloaded_bytes INTEGER NOT NULL DEFAULT 0,
  progress_permille INTEGER NOT NULL DEFAULT 0,
  error TEXT NULL,
  created_utc INTEGER NOT NULL,
  completed_utc INTEGER NULL,
  UNIQUE (profile_id, kind, item_key)
);

-- Browse the current profile's downloads, newest first.
CREATE INDEX ix_downloads_browse ON downloads(profile_id, status, created_utc DESC);
-- Cross-profile scan for the startup resume of interrupted jobs.
CREATE INDEX ix_downloads_status ON downloads(status);
