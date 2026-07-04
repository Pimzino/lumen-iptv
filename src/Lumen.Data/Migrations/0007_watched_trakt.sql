-- 0007: persistent watched state on watch history, plus Trakt identity and watched-history caches.

-- Watched/completion state. play_count accumulates via upsert deltas; season/episode_number are
-- filled for series episode rows so Trakt sync can address them without refetching series details.
ALTER TABLE watch_history ADD COLUMN completed INTEGER NOT NULL DEFAULT 0;
ALTER TABLE watch_history ADD COLUMN play_count INTEGER NOT NULL DEFAULT 0;
ALTER TABLE watch_history ADD COLUMN completed_utc INTEGER NULL;
ALTER TABLE watch_history ADD COLUMN season INTEGER NULL;
ALTER TABLE watch_history ADD COLUMN episode_number INTEGER NULL;

-- Provider item -> Trakt/TMDB identity, per profile. Rows with all ids NULL are negative matches
-- (nothing found); they are retried after a cooldown and flushed when Trakt credentials change.
CREATE TABLE trakt_match (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  profile_id INTEGER NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
  item_kind INTEGER NOT NULL,
  item_key TEXT NOT NULL,
  trakt_id INTEGER NULL,
  tmdb_id INTEGER NULL,
  imdb_id TEXT NULL,
  matched_title TEXT NULL,
  matched_year INTEGER NULL,
  method INTEGER NOT NULL,
  matched_utc INTEGER NOT NULL,
  UNIQUE (profile_id, item_kind, item_key)
);

-- Snapshot of the connected Trakt account's watched history (app-global; replaced on each pull).
-- Movies use season/episode_number 0; episodes carry the show's trakt id in trakt_id.
CREATE TABLE trakt_watched (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  media_type INTEGER NOT NULL,
  trakt_id INTEGER NOT NULL,
  tmdb_id INTEGER NULL,
  imdb_id TEXT NULL,
  title TEXT NOT NULL,
  year INTEGER NULL,
  season INTEGER NOT NULL DEFAULT 0,
  episode_number INTEGER NOT NULL DEFAULT 0,
  plays INTEGER NOT NULL,
  last_watched_utc INTEGER NOT NULL,
  UNIQUE (media_type, trakt_id, season, episode_number)
);
