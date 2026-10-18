using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.OrderService.Data.Migrations.Order;

/// <inheritdoc />
public partial class EnforceNullableOrderTextSourceLengths : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '30s';
            LOCK TABLE public."Order" IN ACCESS EXCLUSIVE MODE;
            DO $guard$
            DECLARE definition text; expected text;
            BEGIN
                IF current_setting('server_encoding') <> 'UTF8'
                    OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."Order"'::regclass
                    AND attname = 'Name' AND NOT attnotnull AND atttypid = 'varchar'::regtype AND atttypmod = 104 AND NOT attisdropped)
                    OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."Order"'::regclass
                    AND attname = 'Description' AND NOT attnotnull AND atttypid = 'varchar'::regtype AND atttypmod = 254 AND NOT attisdropped)
                    OR EXISTS (SELECT 1 FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum
                    WHERE d.adrelid = 'public."Order"'::regclass AND a.attname = 'Description' AND NOT a.attisdropped)
                    OR EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public."Order"'::regclass
                    AND conname IN ('CK_Order_Name_SourceLength', 'CK_Order_Description_SourceLength'))
                    THEN
                    RAISE EXCEPTION 'Order source text upgrade schema is incompatible.';
                END IF;
                IF (SELECT count(*) FROM (SELECT 1 FROM public."Order" LIMIT 10001) bounded) > 10000 THEN
                    RAISE EXCEPTION 'Order source text upgrade row limit exceeded.';
                END IF;
                IF EXISTS (SELECT 1 FROM public."Order" WHERE NOT (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 100) OR NOT (char_length("Description") + char_length(regexp_replace("Description" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 250)) THEN
                    RAISE EXCEPTION 'Order retained rows violate source text bounds during upgrade.';
                END IF;
                CREATE TEMP TABLE "__OrderTextSourceGuard" ("Name" character varying(100) DEFAULT 'unnamed') ON COMMIT DROP;
                SELECT pg_get_expr(adbin, adrelid) INTO expected FROM pg_attrdef
                    WHERE adrelid = 'pg_temp."__OrderTextSourceGuard"'::regclass
                    AND adnum = (SELECT attnum FROM pg_attribute WHERE attrelid = 'pg_temp."__OrderTextSourceGuard"'::regclass AND attname = 'Name');
                SELECT pg_get_expr(adbin, adrelid) INTO definition FROM pg_attrdef
                    WHERE adrelid = 'public."Order"'::regclass
                    AND adnum = (SELECT attnum FROM pg_attribute WHERE attrelid = 'public."Order"'::regclass AND attname = 'Name');
                IF definition IS NULL OR definition IS DISTINCT FROM expected THEN
                    RAISE EXCEPTION 'Order source text upgrade name default is incompatible.';
                END IF;
                DROP TABLE "__OrderTextSourceGuard";
            END $guard$;
            ALTER TABLE public."Order" ALTER COLUMN "Name" TYPE text;
            ALTER TABLE public."Order" ALTER COLUMN "Description" TYPE text;
            ALTER TABLE public."Order" ALTER COLUMN "Name" SET DEFAULT 'unnamed';
            ALTER TABLE public."Order" ADD CONSTRAINT "CK_Order_Name_SourceLength" CHECK (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 100) NOT VALID;
            ALTER TABLE public."Order" ADD CONSTRAINT "CK_Order_Description_SourceLength" CHECK (char_length("Description") + char_length(regexp_replace("Description" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 250) NOT VALID;
            ALTER TABLE public."Order" VALIDATE CONSTRAINT "CK_Order_Name_SourceLength";
            ALTER TABLE public."Order" VALIDATE CONSTRAINT "CK_Order_Description_SourceLength";
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '30s';
            LOCK TABLE public."Order" IN ACCESS EXCLUSIVE MODE;
            DO $guard$
            DECLARE definition text; expected text;
            BEGIN
                IF current_setting('server_encoding') <> 'UTF8'
                    OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."Order"'::regclass
                    AND attname = 'Name' AND NOT attnotnull AND atttypid = 'text'::regtype AND NOT attisdropped)
                    OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."Order"'::regclass
                    AND attname = 'Description' AND NOT attnotnull AND atttypid = 'text'::regtype AND NOT attisdropped)
                    OR EXISTS (SELECT 1 FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum
                    WHERE d.adrelid = 'public."Order"'::regclass AND a.attname = 'Description' AND NOT a.attisdropped)
                    THEN
                    RAISE EXCEPTION 'Order source text downgrade schema is incompatible.';
                END IF;
                IF (SELECT count(*) FROM (SELECT 1 FROM public."Order" LIMIT 10001) bounded) > 10000 THEN
                    RAISE EXCEPTION 'Order source text downgrade row limit exceeded.';
                END IF;
                IF EXISTS (SELECT 1 FROM public."Order" WHERE NOT (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 100) OR NOT (char_length("Description") + char_length(regexp_replace("Description" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 250)) THEN
                    RAISE EXCEPTION 'Order retained rows violate source text bounds during downgrade.';
                END IF;
                CREATE TEMP TABLE "__OrderTextSourceGuard" (
                    "Name" text DEFAULT 'unnamed',
                    "Description" text,
                    CONSTRAINT "expected_Name" CHECK (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 100),
                    CONSTRAINT "expected_Description" CHECK (char_length("Description") + char_length(regexp_replace("Description" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 250)
                ) ON COMMIT DROP;
                SELECT pg_get_expr(adbin, adrelid) INTO expected FROM pg_attrdef
                    WHERE adrelid = 'pg_temp."__OrderTextSourceGuard"'::regclass
                    AND adnum = (SELECT attnum FROM pg_attribute WHERE attrelid = 'pg_temp."__OrderTextSourceGuard"'::regclass AND attname = 'Name');
                SELECT pg_get_expr(adbin, adrelid) INTO definition FROM pg_attrdef
                    WHERE adrelid = 'public."Order"'::regclass
                    AND adnum = (SELECT attnum FROM pg_attribute WHERE attrelid = 'public."Order"'::regclass AND attname = 'Name');
                IF definition IS NULL OR definition IS DISTINCT FROM expected THEN
                    RAISE EXCEPTION 'Order source text downgrade name default is incompatible.';
                END IF;
                SELECT pg_get_constraintdef(oid) INTO expected FROM pg_constraint
                    WHERE conrelid = 'pg_temp."__OrderTextSourceGuard"'::regclass AND conname = 'expected_Name';
                SELECT pg_get_constraintdef(oid) INTO definition FROM pg_constraint
                    WHERE conrelid = 'public."Order"'::regclass AND conname = 'CK_Order_Name_SourceLength' AND contype = 'c'
                    AND convalidated AND conislocal AND coninhcount = 0 AND NOT connoinherit
                    AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'public."Order"'::regclass AND attname = 'Name')]::smallint[];
                IF definition IS NULL OR definition IS DISTINCT FROM expected THEN
                    RAISE EXCEPTION 'Order.Name downgrade constraint is incompatible.';
                END IF;
                SELECT pg_get_constraintdef(oid) INTO expected FROM pg_constraint
                    WHERE conrelid = 'pg_temp."__OrderTextSourceGuard"'::regclass AND conname = 'expected_Description';
                SELECT pg_get_constraintdef(oid) INTO definition FROM pg_constraint
                    WHERE conrelid = 'public."Order"'::regclass AND conname = 'CK_Order_Description_SourceLength' AND contype = 'c'
                    AND convalidated AND conislocal AND coninhcount = 0 AND NOT connoinherit
                    AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'public."Order"'::regclass AND attname = 'Description')]::smallint[];
                IF definition IS NULL OR definition IS DISTINCT FROM expected THEN
                    RAISE EXCEPTION 'Order.Description downgrade constraint is incompatible.';
                END IF;
                DROP TABLE "__OrderTextSourceGuard";
            END $guard$;
            ALTER TABLE public."Order" DROP CONSTRAINT "CK_Order_Name_SourceLength";
            ALTER TABLE public."Order" DROP CONSTRAINT "CK_Order_Description_SourceLength";
            ALTER TABLE public."Order" ALTER COLUMN "Name" TYPE character varying(100);
            ALTER TABLE public."Order" ALTER COLUMN "Description" TYPE character varying(250);
            ALTER TABLE public."Order" ALTER COLUMN "Name" SET DEFAULT 'unnamed';
            """);
    }
}
