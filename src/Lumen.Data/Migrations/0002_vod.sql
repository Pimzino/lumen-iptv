-- 0002: cached VOD catalog (movies and series lists) for fast grids, search, and offline browse.

CREATE TABLE vod_items (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  profile_id INTEGER NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
  kind INTEGER NOT NULL,
  provider_item_id TEXT NOT NULL,
  category_id INTEGER NULL REFERENCES categories(id) ON DELETE SET NULL,
  name TEXT NOT NULL,
  poster_url TEXT NULL,
  rating REAL NULL,
  year INTEGER NULL,
  provider_added_utc INTEGER NULL,
  container_extension TEXT NULL,
  stream_url TEXT NULL,
  UNIQUE (profile_id, kind, provider_item_id)
);

CREATE INDEX ix_vod_items_browse ON vod_items(profile_id, kind, category_id, name COLLATE NOCASE);
CREATE INDEX ix_vod_items_added ON vod_items(profile_id, kind, provider_added_utc DESC);
