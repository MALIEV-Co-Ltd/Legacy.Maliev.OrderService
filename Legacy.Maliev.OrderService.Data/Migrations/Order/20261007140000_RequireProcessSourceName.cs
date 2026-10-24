using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.OrderService.Data.Migrations.Order;

/// <inheritdoc />
public partial class RequireProcessSourceName : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '30s';
            LOCK TABLE public."Process" IN ACCESS EXCLUSIVE MODE;
            DO $guard$
            BEGIN
                IF current_setting('server_encoding') <> 'UTF8'
                    OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."Process"'::regclass
                    AND attname = 'Name' AND attnotnull AND atttypid = 'varchar'::regtype AND atttypmod = 54 AND NOT attisdropped)
                    OR EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public."Process"'::regclass
                    AND conname = 'CK_Process_Name_SourceLength') THEN
                    RAISE EXCEPTION 'Process source name schema is incompatible.';
                END IF;
                IF (SELECT count(*) FROM (SELECT 1 FROM public."Process" LIMIT 10001) bounded) > 10000 THEN
                    RAISE EXCEPTION 'Process source name preflight row limit exceeded.';
                END IF;
                IF EXISTS (SELECT 1 FROM public."Process" WHERE "Name" IS NULL OR NOT (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50)) THEN
                    RAISE EXCEPTION 'Process retained rows violate source name constraints.';
                END IF;
            END $guard$;
            ALTER TABLE public."Process" ALTER COLUMN "Name" TYPE text;
            ALTER TABLE public."Process" ADD CONSTRAINT "CK_Process_Name_SourceLength" CHECK (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50);
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '30s';
            LOCK TABLE public."Process" IN ACCESS EXCLUSIVE MODE;
            DO $guard$
            DECLARE definition text; expected text;
            BEGIN
                IF current_setting('server_encoding') <> 'UTF8'
                    OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."Process"'::regclass
                    AND attname = 'Name' AND attnotnull AND atttypid = 'text'::regtype AND NOT attisdropped) THEN
                    RAISE EXCEPTION 'Process downgrade schema is incompatible.';
                END IF;
                IF (SELECT count(*) FROM (SELECT 1 FROM public."Process" LIMIT 10001) bounded) > 10000 THEN
                    RAISE EXCEPTION 'Process downgrade row limit exceeded.';
                END IF;
                CREATE TEMP TABLE "__ProcessSourceNameGuard" (
                    "Name" text,
                    CONSTRAINT "expected_Name" CHECK (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50)
                ) ON COMMIT DROP;
                SELECT pg_get_constraintdef(oid) INTO expected FROM pg_constraint
                    WHERE conrelid = 'pg_temp."__ProcessSourceNameGuard"'::regclass AND conname = 'expected_Name';
                SELECT pg_get_constraintdef(oid) INTO definition FROM pg_constraint
                    WHERE conrelid = 'public."Process"'::regclass
                    AND conname = 'CK_Process_Name_SourceLength' AND contype = 'c'
                    AND convalidated AND conislocal AND coninhcount = 0 AND NOT connoinherit
                    AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'public."Process"'::regclass AND attname = 'Name')]::smallint[];
                IF definition IS NULL OR definition IS DISTINCT FROM expected THEN
                    RAISE EXCEPTION 'Process downgrade constraint is incompatible.';
                END IF;
                DROP TABLE pg_temp."__ProcessSourceNameGuard";
            END $guard$;
            ALTER TABLE public."Process" DROP CONSTRAINT "CK_Process_Name_SourceLength";
            ALTER TABLE public."Process" ALTER COLUMN "Name" TYPE character varying(50);
            """);
    }
}
