using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.OrderService.Data.Migrations.OrderStatus;

/// <inheritdoc />
public partial class RequireOrderStatusSourceStrings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '30s';
            LOCK TABLE public."OrderStatus" IN ACCESS EXCLUSIVE MODE;
            DO $guard$
            BEGIN
                IF current_setting('server_encoding') <> 'UTF8' THEN
                    RAISE EXCEPTION 'OrderStatus source string schema is incompatible.';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."OrderStatus"'::regclass
                    AND attname = 'Name' AND NOT attnotnull AND atttypid = 'varchar'::regtype AND atttypmod = 54 AND NOT attisdropped)
                    OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."OrderStatus"'::regclass
                    AND attname = 'Description' AND NOT attnotnull AND atttypid = 'text'::regtype AND NOT attisdropped)
                    OR EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public."OrderStatus"'::regclass
                    AND conname IN ('CK_OrderStatus_Name_SourceLength', 'CK_OrderStatus_Description_SourceLength')) THEN
                    RAISE EXCEPTION 'OrderStatus source string schema is incompatible.';
                END IF;
                IF (SELECT count(*) FROM (SELECT 1 FROM public."OrderStatus" LIMIT 10001) bounded) > 10000 THEN
                    RAISE EXCEPTION 'OrderStatus source string preflight row limit exceeded.';
                END IF;
                IF EXISTS (SELECT 1 FROM public."OrderStatus" WHERE "Name" IS NULL OR NOT (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50) OR NOT (char_length("Description") + char_length(regexp_replace("Description" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 100)) THEN
                    RAISE EXCEPTION 'OrderStatus retained rows violate source string constraints.';
                END IF;
            END $guard$;
            ALTER TABLE public."OrderStatus" ALTER COLUMN "Name" TYPE text;
            ALTER TABLE public."OrderStatus" ALTER COLUMN "Name" SET NOT NULL;
            ALTER TABLE public."OrderStatus" ADD CONSTRAINT "CK_OrderStatus_Name_SourceLength" CHECK (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50);
            ALTER TABLE public."OrderStatus" ADD CONSTRAINT "CK_OrderStatus_Description_SourceLength" CHECK (char_length("Description") + char_length(regexp_replace("Description" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 100);
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '30s';
            LOCK TABLE public."OrderStatus" IN ACCESS EXCLUSIVE MODE;
            DO $guard$
            DECLARE field text; definition text; expected text;
            BEGIN
                IF current_setting('server_encoding') <> 'UTF8'
                    OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."OrderStatus"'::regclass
                    AND attname = 'Name' AND attnotnull AND atttypid = 'text'::regtype AND NOT attisdropped)
                    OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."OrderStatus"'::regclass
                    AND attname = 'Description' AND NOT attnotnull AND atttypid = 'text'::regtype AND NOT attisdropped) THEN
                    RAISE EXCEPTION 'OrderStatus downgrade schema is incompatible.';
                END IF;
                IF (SELECT count(*) FROM (SELECT 1 FROM public."OrderStatus" LIMIT 10001) bounded) > 10000 THEN
                    RAISE EXCEPTION 'OrderStatus downgrade row limit exceeded.';
                END IF;
                CREATE TEMP TABLE "__OrderStatusSourceStringGuard" (
                    "Name" text, "Description" text,
                    CONSTRAINT "expected_Name" CHECK (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50),
                    CONSTRAINT "expected_Description" CHECK (char_length("Description") + char_length(regexp_replace("Description" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 100)
                ) ON COMMIT DROP;
                FOREACH field IN ARRAY ARRAY['Name', 'Description'] LOOP
                    SELECT pg_get_constraintdef(oid) INTO expected FROM pg_constraint
                        WHERE conrelid = 'pg_temp."__OrderStatusSourceStringGuard"'::regclass AND conname = 'expected_' || field;
                    SELECT pg_get_constraintdef(oid) INTO definition FROM pg_constraint
                        WHERE conrelid = 'public."OrderStatus"'::regclass
                        AND conname = 'CK_OrderStatus_' || field || '_SourceLength' AND contype = 'c'
                        AND convalidated AND conislocal AND coninhcount = 0 AND NOT connoinherit
                        AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'public."OrderStatus"'::regclass AND attname = field)]::smallint[];
                    IF definition IS NULL OR definition IS DISTINCT FROM expected THEN
                        RAISE EXCEPTION 'OrderStatus downgrade constraint is incompatible.';
                    END IF;
                END LOOP;
                DROP TABLE "__OrderStatusSourceStringGuard";
            END $guard$;
            ALTER TABLE public."OrderStatus" DROP CONSTRAINT "CK_OrderStatus_Name_SourceLength";
            ALTER TABLE public."OrderStatus" DROP CONSTRAINT "CK_OrderStatus_Description_SourceLength";
            ALTER TABLE public."OrderStatus" ALTER COLUMN "Name" DROP NOT NULL;
            ALTER TABLE public."OrderStatus" ALTER COLUMN "Name" TYPE character varying(50);
            """);
    }
}
