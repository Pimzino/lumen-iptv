-- Per-profile stream User-Agent override. NULL means "use the app default".
-- Many IPTV panels only serve a whitelisted player UA and reject others with HTTP 403,
-- so streams that work in a mobile IPTV app fail elsewhere until the UA matches.
ALTER TABLE profiles ADD COLUMN stream_user_agent TEXT;
