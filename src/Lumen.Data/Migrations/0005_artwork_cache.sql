-- 0005: external artwork lookups (TMDB / iTunes / TVMaze), cached by cleaned title so every
-- profile and catalog refresh reuses them. Rows with null urls are negative entries (the
-- lookup ran and found nothing) and are retried after a TTL by the artwork service.

CREATE TABLE artwork_cache (
  kind INTEGER NOT NULL,
  title_key TEXT NOT NULL,
  year INTEGER NOT NULL DEFAULT 0,
  poster_url TEXT NULL,
  backdrop_url TEXT NULL,
  provider TEXT NULL,
  resolved_utc INTEGER NOT NULL,
  PRIMARY KEY (kind, title_key, year)
) WITHOUT ROWID;
