using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.OrderService.Data.Migrations.Order;

/// <inheritdoc />
public partial class EnforceNullableCatalogSourceStrings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '30s';
            LOCK TABLE public."Category", public."FileFormat" IN ACCESS EXCLUSIVE MODE;
            DO $guard$
            BEGIN
                IF current_setting('server_encoding') <> 'UTF8'
                    OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."Category"'::regclass
                    AND attname = 'Name' AND NOT attnotnull AND atttypid = 'varchar'::regtype AND atttypmod = 54 AND NOT attisdropped)
                    OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."FileFormat"'::regclass
                    AND attname = 'Name' AND NOT attnotnull AND atttypid = 'varchar'::regtype AND atttypmod = 54 AND NOT attisdropped)
                    OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."FileFormat"'::regclass
                    AND attname = 'Extension' AND NOT attnotnull AND atttypid = 'varchar'::regtype AND atttypmod = 54 AND NOT attisdropped)
                    OR EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public."Category"'::regclass
                    AND conname = 'CK_Category_Name_SourceLength')
                    OR EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public."FileFormat"'::regclass
                    AND conname = 'CK_FileFormat_Name_SourceLength')
                    OR EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public."FileFormat"'::regclass
                    AND conname = 'CK_FileFormat_Extension_SourceLength')
                    THEN
                    RAISE EXCEPTION 'Nullable catalog source upgrade schema is incompatible.';
                END IF;
                IF (SELECT count(*) FROM (SELECT 1 FROM public."Category" LIMIT 10001) bounded) > 10000 THEN
                    RAISE EXCEPTION 'Category nullable source upgrade row limit exceeded.';
                END IF;
                IF (SELECT count(*) FROM (SELECT 1 FROM public."FileFormat" LIMIT 10001) bounded) > 10000 THEN
                    RAISE EXCEPTION 'FileFormat nullable source upgrade row limit exceeded.';
                END IF;
                IF EXISTS (SELECT 1 FROM public."Category" WHERE NOT (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50)) THEN
                    RAISE EXCEPTION 'Category retained rows violate nullable source strings during upgrade.';
                END IF;
                IF EXISTS (SELECT 1 FROM public."FileFormat" WHERE NOT (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50) OR NOT (char_length("Extension") + char_length(regexp_replace("Extension" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50)) THEN
                    RAISE EXCEPTION 'FileFormat retained rows violate nullable source strings during upgrade.';
                END IF;
            END $guard$;
            ALTER TABLE public."Category" ALTER COLUMN "Name" TYPE text;
            ALTER TABLE public."FileFormat" ALTER COLUMN "Name" TYPE text;
            ALTER TABLE public."FileFormat" ALTER COLUMN "Extension" TYPE text;
            ALTER TABLE public."Category" ADD CONSTRAINT "CK_Category_Name_SourceLength" CHECK (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50) NOT VALID;
            ALTER TABLE public."FileFormat" ADD CONSTRAINT "CK_FileFormat_Name_SourceLength" CHECK (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50) NOT VALID;
            ALTER TABLE public."FileFormat" ADD CONSTRAINT "CK_FileFormat_Extension_SourceLength" CHECK (char_length("Extension") + char_length(regexp_replace("Extension" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50) NOT VALID;
            ALTER TABLE public."Category" VALIDATE CONSTRAINT "CK_Category_Name_SourceLength";
            ALTER TABLE public."FileFormat" VALIDATE CONSTRAINT "CK_FileFormat_Name_SourceLength";
            ALTER TABLE public."FileFormat" VALIDATE CONSTRAINT "CK_FileFormat_Extension_SourceLength";
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '30s';
            LOCK TABLE public."Category", public."FileFormat" IN ACCESS EXCLUSIVE MODE;
            DO $guard$
            DECLARE definition text; expected text;
            BEGIN
                IF current_setting('server_encoding') <> 'UTF8'
                    OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."Category"'::regclass
                    AND attname = 'Name' AND NOT attnotnull AND atttypid = 'text'::regtype AND NOT attisdropped)
                    OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."FileFormat"'::regclass
                    AND attname = 'Name' AND NOT attnotnull AND atttypid = 'text'::regtype AND NOT attisdropped)
                    OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."FileFormat"'::regclass
                    AND attname = 'Extension' AND NOT attnotnull AND atttypid = 'text'::regtype AND NOT attisdropped)
                    THEN
                    RAISE EXCEPTION 'Nullable catalog source downgrade schema is incompatible.';
                END IF;
                IF (SELECT count(*) FROM (SELECT 1 FROM public."Category" LIMIT 10001) bounded) > 10000 THEN
                    RAISE EXCEPTION 'Category nullable source downgrade row limit exceeded.';
                END IF;
                IF (SELECT count(*) FROM (SELECT 1 FROM public."FileFormat" LIMIT 10001) bounded) > 10000 THEN
                    RAISE EXCEPTION 'FileFormat nullable source downgrade row limit exceeded.';
                END IF;
                IF EXISTS (SELECT 1 FROM public."Category" WHERE NOT (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50)) THEN
                    RAISE EXCEPTION 'Category retained rows violate nullable source strings during downgrade.';
                END IF;
                IF EXISTS (SELECT 1 FROM public."FileFormat" WHERE NOT (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50) OR NOT (char_length("Extension") + char_length(regexp_replace("Extension" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50)) THEN
                    RAISE EXCEPTION 'FileFormat retained rows violate nullable source strings during downgrade.';
                END IF;
                CREATE TEMP TABLE "__NullableCatalogSourceStringsGuard" (
                    "Name" text,
                    "Extension" text,
                    CONSTRAINT "expected_Name" CHECK (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50),
                    CONSTRAINT "expected_Extension" CHECK (char_length("Extension") + char_length(regexp_replace("Extension" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50)
                ) ON COMMIT DROP;
                SELECT pg_get_constraintdef(oid) INTO expected FROM pg_constraint
                    WHERE conrelid = 'pg_temp."__NullableCatalogSourceStringsGuard"'::regclass AND conname = 'expected_Name';
                SELECT pg_get_constraintdef(oid) INTO definition FROM pg_constraint
                    WHERE conrelid = 'public."Category"'::regclass
                    AND conname = 'CK_Category_Name_SourceLength' AND contype = 'c'
                    AND convalidated AND conislocal AND coninhcount = 0 AND NOT connoinherit
                    AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'public."Category"'::regclass AND attname = 'Name')]::smallint[];
                IF definition IS NULL OR definition IS DISTINCT FROM expected THEN
                    RAISE EXCEPTION 'Category.Name downgrade constraint is incompatible.';
                END IF;
                SELECT pg_get_constraintdef(oid) INTO expected FROM pg_constraint
                    WHERE conrelid = 'pg_temp."__NullableCatalogSourceStringsGuard"'::regclass AND conname = 'expected_Name';
                SELECT pg_get_constraintdef(oid) INTO definition FROM pg_constraint
                    WHERE conrelid = 'public."FileFormat"'::regclass
                    AND conname = 'CK_FileFormat_Name_SourceLength' AND contype = 'c'
                    AND convalidated AND conislocal AND coninhcount = 0 AND NOT connoinherit
                    AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'public."FileFormat"'::regclass AND attname = 'Name')]::smallint[];
                IF definition IS NULL OR definition IS DISTINCT FROM expected THEN
                    RAISE EXCEPTION 'FileFormat.Name downgrade constraint is incompatible.';
                END IF;
                SELECT pg_get_constraintdef(oid) INTO expected FROM pg_constraint
                    WHERE conrelid = 'pg_temp."__NullableCatalogSourceStringsGuard"'::regclass AND conname = 'expected_Extension';
                SELECT pg_get_constraintdef(oid) INTO definition FROM pg_constraint
                    WHERE conrelid = 'public."FileFormat"'::regclass
                    AND conname = 'CK_FileFormat_Extension_SourceLength' AND contype = 'c'
                    AND convalidated AND conislocal AND coninhcount = 0 AND NOT connoinherit
                    AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'public."FileFormat"'::regclass AND attname = 'Extension')]::smallint[];
                IF definition IS NULL OR definition IS DISTINCT FROM expected THEN
                    RAISE EXCEPTION 'FileFormat.Extension downgrade constraint is incompatible.';
                END IF;
                DROP TABLE pg_temp."__NullableCatalogSourceStringsGuard";
            END $guard$;
            ALTER TABLE public."Category" DROP CONSTRAINT "CK_Category_Name_SourceLength";
            ALTER TABLE public."FileFormat" DROP CONSTRAINT "CK_FileFormat_Name_SourceLength";
            ALTER TABLE public."FileFormat" DROP CONSTRAINT "CK_FileFormat_Extension_SourceLength";
            ALTER TABLE public."Category" ALTER COLUMN "Name" TYPE character varying(50);
            ALTER TABLE public."FileFormat" ALTER COLUMN "Name" TYPE character varying(50);
            ALTER TABLE public."FileFormat" ALTER COLUMN "Extension" TYPE character varying(50);
            """);
    }
}
