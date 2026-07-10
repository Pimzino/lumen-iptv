-- 0010: live TV recordings (in progress and finished captures).

CREATE TABLE recordings (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  profile_id INTEGER NOT NULL REFERENCES profiles(id) ON DELETE CASCADE,
  channel_id INTEGER NULL,
  channel_name TEXT NOT NULL,
  programme_title TEXT NULL,
  logo_url TEXT NULL,
  file_path TEXT NOT NULL,
  status INTEGER NOT NULL,
  error TEXT NULL,
  started_utc INTEGER NOT NULL,
  stopped_utc INTEGER NULL,
  duration_seconds INTEGER NULL,
  size_bytes INTEGER NOT NULL DEFAULT 0
);

-- Browse the current profile's recordings, newest first.
CREATE INDEX ix_recordings_browse ON recordings(profile_id, started_utc DESC);
-- Cross-profile scan for the startup reconciliation of interrupted recordings.
CREATE INDEX ix_recordings_status ON recordings(status);
