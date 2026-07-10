-- 0011: user-editable recording titles (display name overrides the captured metadata).

ALTER TABLE recordings ADD COLUMN custom_title TEXT NULL;
