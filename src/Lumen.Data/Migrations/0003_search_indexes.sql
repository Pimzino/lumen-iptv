-- 0003: indexes supporting global search.

-- The EPG-programme search joins programmes to channels through channel_epg_map by xmltv_id;
-- without this index that join scans the whole map per programme.
CREATE INDEX ix_channel_epg_map_xmltv ON channel_epg_map(xmltv_id);
