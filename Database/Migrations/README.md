# Discord update-cache cleanup

`20260728_remove_discord_author_data.cql` is a one-time operator migration. It
is intentionally not run automatically at process startup.

1. Build the new DiscordBot image, but do not start it.
2. Scale every old DiscordBot instance to zero so no old process can repopulate
   author IDs or messages from channels outside `devlog` and `test`.
3. Back up the `discord_messages` table.
4. Select the DiscordBot keyspace and run the following read-only inspection.
   Confirm that the target is the `discord_messages` table, `authorid` is the
   exact existing ID column, and `authorname` remains present for intentional
   developer attribution. Stop on any mismatch.

   ```cql
   SELECT column_name
   FROM system_schema.columns
   WHERE keyspace_name = '<DISCORD_BOT_KEYSPACE>'
     AND table_name = 'discord_messages';
   ```

5. Run the `ALTER TABLE ... DROP authorid` statement once. It preserves the
   existing messages and their developer attribution.
6. Start only the new DiscordBot build.
7. Verify the API emits each developer's `authorName`, never emits `authorId`,
   and new writes are limited to channels `888932870318612490` and
   `870408637204553758`.
8. Compact the affected Cassandra/Scylla table according to the cluster's normal
   maintenance procedure, and let any encrypted backups containing the old
   author IDs expire under the applicable backup-retention schedule.
