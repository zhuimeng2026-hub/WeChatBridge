-- 0003_account_avatar.down.sql — reverse of 0003_account_avatar.up.sql.
--
-- This drops the avatar index. It does not delete the files under
-- WCB_AVATAR_DIR: they are content-addressed, so a rollback that removed them
-- would break an avatar copied under an older schema. An operator who wants
-- the disk back deletes the directory by hand.

DROP TABLE IF EXISTS account_avatars;
