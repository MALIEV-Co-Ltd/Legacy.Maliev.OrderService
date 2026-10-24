using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.OrderService.Data.Migrations.Order;

/// <inheritdoc />
public partial class NormalizeCatalogTextSourceStorage : Migration
{
    private const string CategoryBody = """
        DECLARE units integer := 0; character_index integer := 0; width integer; scalar text;
        BEGIN
            IF NEW."Name" IS NOT NULL AND
                char_length(NEW."Name") + char_length(regexp_replace(NEW."Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 50 THEN
                WHILE units < 50 LOOP
                    scalar := substr(NEW."Name", character_index + 1, 1);
                    IF scalar COLLATE "C" = '' THEN EXIT; END IF;
                    width := CASE WHEN octet_length(scalar) = 4 THEN 2 ELSE 1 END;
                    IF units + width > 50 THEN EXIT; END IF;
                    units := units + width;
                    character_index := character_index + 1;
                END LOOP;
                IF btrim(substr(NEW."Name", character_index + 1), ' ') COLLATE "C" = '' THEN
                    NEW."Name" := substr(NEW."Name", 1, character_index);
                END IF;
            END IF;
            RETURN NEW;
        END;
        """;

    private const string FileFormatBody = """
        DECLARE units integer := 0; character_index integer := 0; width integer; scalar text;
        BEGIN
            IF NEW."Name" IS NOT NULL AND
                char_length(NEW."Name") + char_length(regexp_replace(NEW."Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 50 THEN
                WHILE units < 50 LOOP
                    scalar := substr(NEW."Name", character_index + 1, 1);
                    IF scalar COLLATE "C" = '' THEN EXIT; END IF;
                    width := CASE WHEN octet_length(scalar) = 4 THEN 2 ELSE 1 END;
                    IF units + width > 50 THEN EXIT; END IF;
                    units := units + width;
                    character_index := character_index + 1;
                END LOOP;
                IF btrim(substr(NEW."Name", character_index + 1), ' ') COLLATE "C" = '' THEN
                    NEW."Name" := substr(NEW."Name", 1, character_index);
                END IF;
            END IF;
            units := 0;
            character_index := 0;
            IF NEW."Extension" IS NOT NULL AND
                char_length(NEW."Extension") + char_length(regexp_replace(NEW."Extension" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 50 THEN
                WHILE units < 50 LOOP
                    scalar := substr(NEW."Extension", character_index + 1, 1);
                    IF scalar COLLATE "C" = '' THEN EXIT; END IF;
                    width := CASE WHEN octet_length(scalar) = 4 THEN 2 ELSE 1 END;
                    IF units + width > 50 THEN EXIT; END IF;
                    units := units + width;
                    character_index := character_index + 1;
                END LOOP;
                IF btrim(substr(NEW."Extension", character_index + 1), ' ') COLLATE "C" = '' THEN
                    NEW."Extension" := substr(NEW."Extension", 1, character_index);
                END IF;
            END IF;
            RETURN NEW;
        END;
        """;

    private const string SchemaGuard = """
        SET LOCAL lock_timeout = '5s';
        SET LOCAL statement_timeout = '30s';
        LOCK TABLE public."Category", public."FileFormat" IN ACCESS EXCLUSIVE MODE;
        DO $guard$
        DECLARE table_name text; relation regclass; fields text[]; field text; definition text; expected text;
        BEGIN
            IF current_setting('server_encoding') <> 'UTF8'
                OR (SELECT count(*) FROM pg_class WHERE oid IN ('public."Category"'::regclass,'public."FileFormat"'::regclass)
                    AND relkind = 'r' AND relpersistence = 'p' AND NOT relrowsecurity AND NOT relforcerowsecurity
                    AND relowner = (SELECT oid FROM pg_roles WHERE rolname = current_user)) <> 2
                OR EXISTS (SELECT 1 FROM pg_inherits WHERE inhrelid IN ('public."Category"'::regclass,'public."FileFormat"'::regclass)
                    OR inhparent IN ('public."Category"'::regclass,'public."FileFormat"'::regclass))
                OR EXISTS (SELECT 1 FROM pg_rewrite WHERE ev_class IN ('public."Category"'::regclass,'public."FileFormat"'::regclass))
                OR to_regclass('pg_temp."__CatalogTextPaddingGuard"') IS NOT NULL THEN
                RAISE EXCEPTION 'Catalog text normalizer table custody is incompatible.';
            END IF;
            FOREACH table_name IN ARRAY ARRAY['Category','FileFormat'] LOOP
                relation := CASE table_name WHEN 'Category' THEN 'public."Category"'::regclass ELSE 'public."FileFormat"'::regclass END;
                fields := CASE table_name WHEN 'Category' THEN ARRAY['Name'] ELSE ARRAY['Name','Extension'] END;
                IF (SELECT count(*) FROM pg_attribute WHERE attrelid = relation AND attname = ANY(fields)
                    AND NOT attnotnull AND atttypid = 'text'::regtype AND attgenerated = '' AND attidentity = '' AND NOT attisdropped) <> cardinality(fields)
                    OR EXISTS (SELECT 1 FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum
                        WHERE d.adrelid = relation AND a.attname = ANY(fields) AND NOT a.attisdropped) THEN
                    RAISE EXCEPTION 'Catalog text normalizer columns are incompatible.';
                END IF;
            END LOOP;
            IF (SELECT count(*) FROM (SELECT 1 FROM public."Category" LIMIT 10001) bounded) > 10000
                OR (SELECT count(*) FROM (SELECT 1 FROM public."FileFormat" LIMIT 10001) bounded) > 10000 THEN
                RAISE EXCEPTION 'Catalog text normalizer row limit exceeded.';
            END IF;
            IF EXISTS (SELECT 1 FROM public."Category" WHERE
                char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 50)
                OR EXISTS (SELECT 1 FROM public."FileFormat" WHERE
                char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 50
                OR char_length("Extension") + char_length(regexp_replace("Extension" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 50) THEN
                RAISE EXCEPTION 'Retained catalog text exceeds source storage capacity.';
            END IF;
            CREATE TEMP TABLE "__CatalogTextPaddingGuard" (
                "Name" text, "Extension" text,
                CONSTRAINT "expected_Name" CHECK (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50),
                CONSTRAINT "expected_Extension" CHECK (char_length("Extension") + char_length(regexp_replace("Extension" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50)
            ) ON COMMIT DROP;
            FOREACH table_name IN ARRAY ARRAY['Category','FileFormat'] LOOP
                relation := CASE table_name WHEN 'Category' THEN 'public."Category"'::regclass ELSE 'public."FileFormat"'::regclass END;
                fields := CASE table_name WHEN 'Category' THEN ARRAY['Name'] ELSE ARRAY['Name','Extension'] END;
                FOREACH field IN ARRAY fields LOOP
                    SELECT pg_get_constraintdef(oid) INTO expected FROM pg_constraint
                        WHERE conrelid = 'pg_temp."__CatalogTextPaddingGuard"'::regclass AND conname = 'expected_' || field;
                    SELECT pg_get_constraintdef(oid) INTO definition FROM pg_constraint
                        WHERE conrelid = relation AND conname = 'CK_' || table_name || '_' || field || '_SourceLength'
                        AND contype = 'c' AND convalidated AND conislocal AND coninhcount = 0 AND NOT connoinherit
                        AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = relation AND attname = field)]::smallint[];
                    IF definition IS NULL OR definition IS DISTINCT FROM expected THEN
                        RAISE EXCEPTION 'Catalog text normalizer constraint is incompatible.';
                    END IF;
                END LOOP;
            END LOOP;
            DROP TABLE pg_temp."__CatalogTextPaddingGuard";
        END $guard$;

        """;

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(SchemaGuard + """
            DO $guard$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_proc WHERE pronamespace = 'public'::regnamespace
                    AND proname IN ('NormalizeCategoryTextSource','NormalizeFileFormatTextSource'))
                    OR EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid IN ('public."Category"'::regclass,'public."FileFormat"'::regclass) AND NOT tgisinternal) THEN
                    RAISE EXCEPTION 'Catalog text normalizer upgrade objects already exist.';
                END IF;
            END $guard$;
            CREATE FUNCTION public."NormalizeCategoryTextSource"() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $source$
            """ + CategoryBody + """
            $source$;
            CREATE TRIGGER "NormalizeCategoryTextSource" BEFORE INSERT OR UPDATE OF "Name" ON public."Category"
                FOR EACH ROW EXECUTE FUNCTION public."NormalizeCategoryTextSource"();
            CREATE FUNCTION public."NormalizeFileFormatTextSource"() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $source$
            """ + FileFormatBody + """
            $source$;
            CREATE TRIGGER "NormalizeFileFormatTextSource" BEFORE INSERT OR UPDATE OF "Name","Extension" ON public."FileFormat"
                FOR EACH ROW EXECUTE FUNCTION public."NormalizeFileFormatTextSource"();
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(SchemaGuard
            + CustodyGuard("Category", "NormalizeCategoryTextSource", CategoryBody, "'Name'")
            + CustodyGuard("FileFormat", "NormalizeFileFormatTextSource", FileFormatBody, "'Name','Extension'")
            + """
            DROP TRIGGER "NormalizeCategoryTextSource" ON public."Category";
            DROP TRIGGER "NormalizeFileFormatTextSource" ON public."FileFormat";
            DROP FUNCTION public."NormalizeCategoryTextSource"();
            DROP FUNCTION public."NormalizeFileFormatTextSource"();
            """);
    }

    private static string CustodyGuard(string table, string function, string body, string columns) => $$"""
        DO $guard$
        BEGIN
            IF (SELECT count(*) FROM pg_proc WHERE pronamespace = 'public'::regnamespace AND proname = '{{function}}') <> 1
                OR NOT EXISTS (SELECT 1 FROM pg_proc WHERE oid = to_regprocedure('public."{{function}}"()')
                AND prorettype = 'trigger'::regtype AND prolang = (SELECT oid FROM pg_language WHERE lanname = 'plpgsql')
                AND proowner = (SELECT oid FROM pg_roles WHERE rolname = current_user)
                AND NOT prosecdef AND NOT proisstrict AND NOT proleakproof AND prokind = 'f' AND proparallel = 'u' AND provolatile = 'v'
                AND proconfig = ARRAY['search_path=pg_catalog'] AND prosrc = $source${{body}}$source$)
                OR (SELECT count(*) FROM pg_trigger WHERE tgfoid = to_regprocedure('public."{{function}}"()')) <> 1
                OR (SELECT count(*) FROM pg_trigger WHERE tgrelid = 'public."{{table}}"'::regclass AND NOT tgisinternal) <> 1
                OR NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'public."{{table}}"'::regclass
                AND tgname = '{{function}}' AND tgfoid = to_regprocedure('public."{{function}}"()')
                AND tgtype = 23 AND tgenabled = 'O' AND NOT tgisinternal AND tgconstraint = 0 AND tgqual IS NULL
                AND tgnargs = 0 AND octet_length(tgargs) = 0
                AND tgattr::text = (SELECT string_agg(attnum::text,' ' ORDER BY CASE attname WHEN 'Name' THEN 0 ELSE 1 END)
                    FROM pg_attribute WHERE attrelid = 'public."{{table}}"'::regclass AND attname IN ({{columns}}) AND NOT attisdropped)) THEN
                RAISE EXCEPTION 'Catalog text normalizer downgrade objects are incompatible.';
            END IF;
        END $guard$;

        """;
}
