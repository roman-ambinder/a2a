# Schema migrations

Each file here is one migration. Files are embedded in the package and applied in number order.

## Add a migration

1. Take the next free number. Create `NNNN_short_name.sql` (four digits, lower-case snake_case name).
2. Write `{s}` where the schema name goes, for example `ALTER TABLE {s}.a2a_tasks ...`.
3. Use idempotent forms only: `CREATE TABLE IF NOT EXISTS`, `CREATE INDEX IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`, `DROP INDEX IF EXISTS`. The exported script may run twice.
4. No `CONCURRENTLY`. Every migration runs in one transaction.
5. New columns are nullable or have a default. During a rolling upgrade, older instances still write to the table and must keep working.
6. Do not touch `a2a_schema_version`. The runner stamps the number after your file.
7. Never edit a file that is merged. Add a new migration instead.

If two branches use the same number, a test fails. Rename the later one to the next free number.

## Why the numbers start at 2

Every older build stamped version `1` with a different script. A `1` row does not prove the tables are complete. So there is no `0001` file. The baseline is `0002_baseline.sql`: it is idempotent, and a database at `1` gets it like any other upgrade.
