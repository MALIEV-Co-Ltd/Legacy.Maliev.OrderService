using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.OrderService.Data.Migrations.Order;

/// <inheritdoc />
public partial class EnforceOrderFileBucketSourceLength : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '30s';
            LOCK TABLE public."OrderFile" IN ACCESS EXCLUSIVE MODE;
            DO $guard$
            BEGIN
                IF current_setting('server_encoding') <> 'UTF8'
                    OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."OrderFile"'::regclass
                    AND attname = 'Bucket' AND attnotnull AND atttypid = 'varchar'::regtype AND atttypmod = 54 AND NOT attisdropped)
                    OR EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public."OrderFile"'::regclass
                    AND conname = 'CK_OrderFile_Bucket_SourceLength') THEN
                    RAISE EXCEPTION 'OrderFile source bucket schema is incompatible.';
                END IF;
                IF (SELECT count(*) FROM (SELECT 1 FROM public."OrderFile" LIMIT 10001) bounded) > 10000 THEN
                    RAISE EXCEPTION 'OrderFile source bucket preflight row limit exceeded.';
                END IF;
                IF EXISTS (SELECT 1 FROM public."OrderFile" WHERE "Bucket" IS NULL OR NOT (char_length("Bucket") + char_length(regexp_replace("Bucket" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50)) THEN
                    RAISE EXCEPTION 'OrderFile retained rows violate source bucket constraints.';
                END IF;
            END $guard$;
            ALTER TABLE public."OrderFile" ALTER COLUMN "Bucket" TYPE text;
            ALTER TABLE public."OrderFile" ADD CONSTRAINT "CK_OrderFile_Bucket_SourceLength" CHECK (char_length("Bucket") + char_length(regexp_replace("Bucket" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50) NOT VALID;
            ALTER TABLE public."OrderFile" VALIDATE CONSTRAINT "CK_OrderFile_Bucket_SourceLength";
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '30s';
            LOCK TABLE public."OrderFile" IN ACCESS EXCLUSIVE MODE;
            DO $guard$
            DECLARE definition text; expected text;
            BEGIN
                IF current_setting('server_encoding') <> 'UTF8'
                    OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."OrderFile"'::regclass
                    AND attname = 'Bucket' AND attnotnull AND atttypid = 'text'::regtype AND NOT attisdropped) THEN
                    RAISE EXCEPTION 'OrderFile bucket downgrade schema is incompatible.';
                END IF;
                IF (SELECT count(*) FROM (SELECT 1 FROM public."OrderFile" LIMIT 10001) bounded) > 10000 THEN
                    RAISE EXCEPTION 'OrderFile bucket downgrade row limit exceeded.';
                END IF;
                IF EXISTS (SELECT 1 FROM public."OrderFile" WHERE "Bucket" IS NULL OR NOT (char_length("Bucket") + char_length(regexp_replace("Bucket" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50)) THEN
                    RAISE EXCEPTION 'OrderFile retained rows violate source bucket constraints during downgrade.';
                END IF;
                CREATE TEMP TABLE "__OrderFileBucketSourceGuard" (
                    "Bucket" text,
                    CONSTRAINT "expected_Bucket" CHECK (char_length("Bucket") + char_length(regexp_replace("Bucket" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50)
                ) ON COMMIT DROP;
                SELECT pg_get_constraintdef(oid) INTO expected FROM pg_constraint
                    WHERE conrelid = 'pg_temp."__OrderFileBucketSourceGuard"'::regclass AND conname = 'expected_Bucket';
                SELECT pg_get_constraintdef(oid) INTO definition FROM pg_constraint
                    WHERE conrelid = 'public."OrderFile"'::regclass
                    AND conname = 'CK_OrderFile_Bucket_SourceLength' AND contype = 'c'
                    AND convalidated AND conislocal AND coninhcount = 0 AND NOT connoinherit
                    AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'public."OrderFile"'::regclass AND attname = 'Bucket')]::smallint[];
                IF definition IS NULL OR definition IS DISTINCT FROM expected THEN
                    RAISE EXCEPTION 'OrderFile bucket downgrade constraint is incompatible.';
                END IF;
                DROP TABLE "__OrderFileBucketSourceGuard";
            END $guard$;
            ALTER TABLE public."OrderFile" DROP CONSTRAINT "CK_OrderFile_Bucket_SourceLength";
            ALTER TABLE public."OrderFile" ALTER COLUMN "Bucket" TYPE character varying(50);
            """);
    }
}
