-- 0008: cache each series' total episode count (learned when its details load) so the
-- library grid can draw a whole-series watched-fraction bar without an episodes table.
-- Survives catalog refreshes: the vod_items upsert lists its columns explicitly.

ALTER TABLE vod_items ADD COLUMN episode_total INTEGER NULL;
