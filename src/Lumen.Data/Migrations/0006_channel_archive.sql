-- 0006: provider catch-up (timeshift) metadata on channels.
-- has_archive: the provider archives this channel (Xtream tv_archive).
-- archive_days: how far back the archive reaches (0 = unknown).

ALTER TABLE channels ADD COLUMN has_archive INTEGER NOT NULL DEFAULT 0;
ALTER TABLE channels ADD COLUMN archive_days INTEGER NOT NULL DEFAULT 0;
