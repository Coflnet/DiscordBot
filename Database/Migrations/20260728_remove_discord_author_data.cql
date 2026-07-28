-- One-time destructive cleanup. Read Database/Migrations/README.md before running.
-- Expected table/column names are unquoted because the current driver mapping is
-- case-insensitive and Cassandra stores them in lower case.
-- Run only in the verified DiscordBot keyspace.

ALTER TABLE discord_messages DROP authorid;
