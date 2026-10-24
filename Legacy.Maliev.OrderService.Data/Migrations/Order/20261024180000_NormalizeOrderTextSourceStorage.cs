using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.OrderService.Data.Migrations.Order;

/// <inheritdoc />
public partial class NormalizeOrderTextSourceStorage : Migration
{
    private const string TriggerBody = """
        DECLARE units integer := 0; character_index integer := 0; width integer; scalar text;
        BEGIN
            IF NEW."Name" IS NOT NULL AND
                char_length(NEW."Name") + char_length(regexp_replace(NEW."Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 100 THEN
                WHILE units < 100 LOOP
                    scalar := substr(NEW."Name", character_index + 1, 1);
                    IF scalar = '' THEN EXIT; END IF;
                    width := CASE WHEN octet_length(scalar) = 4 THEN 2 ELSE 1 END;
                    IF units + width > 100 THEN EXIT; END IF;
                    units := units + width;
                    character_index := character_index + 1;
                END LOOP;
                IF btrim(substr(NEW."Name", character_index + 1), ' ') = '' THEN
                    NEW."Name" := substr(NEW."Name", 1, character_index);
                END IF;
            END IF;
            units := 0;
            character_index := 0;
            IF NEW."Description" IS NOT NULL AND
                char_length(NEW."Description") + char_length(regexp_replace(NEW."Description" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 250 THEN
                WHILE units < 250 LOOP
                    scalar := substr(NEW."Description", character_index + 1, 1);
                    IF scalar = '' THEN EXIT; END IF;
                    width := CASE WHEN octet_length(scalar) = 4 THEN 2 ELSE 1 END;
                    IF units + width > 250 THEN EXIT; END IF;
                    units := units + width;
                    character_index := character_index + 1;
                END LOOP;
                IF btrim(substr(NEW."Description", character_index + 1), ' ') = '' THEN
                    NEW."Description" := substr(NEW."Description", 1, character_index);
                END IF;
            END IF;
            RETURN NEW;
        END;
        """;

    private const string SchemaGuard = """
        SET LOCAL lock_timeout = '5s';
        SET LOCAL statement_timeout = '30s';
        LOCK TABLE public."Order" IN ACCESS EXCLUSIVE MODE;
        DO $guard$
        DECLARE definition text; expected text; field text;
        BEGIN
            IF current_setting('server_encoding') <> 'UTF8'
                OR (SELECT count(*) FROM pg_attribute WHERE attrelid = 'public."Order"'::regclass
                    AND attname IN ('Name','Description') AND NOT attnotnull AND atttypid = 'text'::regtype
                    AND attgenerated = '' AND attidentity = '' AND NOT attisdropped) <> 2
                OR EXISTS (SELECT 1 FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum
                    WHERE d.adrelid = 'public."Order"'::regclass AND a.attname = 'Description' AND NOT a.attisdropped)
                OR to_regclass('pg_temp."__OrderTextPaddingGuard"') IS NOT NULL THEN
                RAISE EXCEPTION 'Order text normalizer schema is incompatible.';
            END IF;
            IF (SELECT count(*) FROM (SELECT 1 FROM public."Order" LIMIT 10001) bounded) > 10000 THEN
                RAISE EXCEPTION 'Order text normalizer row limit exceeded.';
            END IF;
            IF EXISTS (SELECT 1 FROM public."Order" WHERE
                char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 100
                OR char_length("Description") + char_length(regexp_replace("Description" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 250) THEN
                RAISE EXCEPTION 'Retained order text exceeds source storage capacity.';
            END IF;
            CREATE TEMP TABLE "__OrderTextPaddingGuard" (
                "Name" text DEFAULT 'unnamed', "Description" text,
                CONSTRAINT "expected_Name" CHECK (char_length("Name") + char_length(regexp_replace("Name" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 100),
                CONSTRAINT "expected_Description" CHECK (char_length("Description") + char_length(regexp_replace("Description" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 250)
            ) ON COMMIT DROP;
            SELECT pg_get_expr(adbin, adrelid) INTO expected FROM pg_attrdef
                WHERE adrelid = 'pg_temp."__OrderTextPaddingGuard"'::regclass
                AND adnum = (SELECT attnum FROM pg_attribute WHERE attrelid = 'pg_temp."__OrderTextPaddingGuard"'::regclass AND attname = 'Name');
            SELECT pg_get_expr(adbin, adrelid) INTO definition FROM pg_attrdef
                WHERE adrelid = 'public."Order"'::regclass
                AND adnum = (SELECT attnum FROM pg_attribute WHERE attrelid = 'public."Order"'::regclass AND attname = 'Name');
            IF definition IS NULL OR definition IS DISTINCT FROM expected THEN
                RAISE EXCEPTION 'Order text normalizer name default is incompatible.';
            END IF;
            FOREACH field IN ARRAY ARRAY['Name','Description'] LOOP
                SELECT pg_get_constraintdef(oid) INTO expected FROM pg_constraint
                    WHERE conrelid = 'pg_temp."__OrderTextPaddingGuard"'::regclass AND conname = 'expected_' || field;
                SELECT pg_get_constraintdef(oid) INTO definition FROM pg_constraint
                    WHERE conrelid = 'public."Order"'::regclass AND conname = 'CK_Order_' || field || '_SourceLength'
                    AND contype = 'c' AND convalidated AND conislocal AND coninhcount = 0 AND NOT connoinherit
                    AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'public."Order"'::regclass AND attname = field)]::smallint[];
                IF definition IS NULL OR definition IS DISTINCT FROM expected THEN
                    RAISE EXCEPTION 'Order text normalizer constraint is incompatible.';
                END IF;
            END LOOP;
            DROP TABLE "__OrderTextPaddingGuard";
        END $guard$;

        """;

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(SchemaGuard + """
            DO $guard$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_proc WHERE pronamespace = 'public'::regnamespace AND proname = 'NormalizeOrderTextSource')
                    OR EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'public."Order"'::regclass AND tgname = 'NormalizeOrderTextSource') THEN
                    RAISE EXCEPTION 'Order text normalizer upgrade objects already exist.';
                END IF;
            END $guard$;
            CREATE FUNCTION public."NormalizeOrderTextSource"() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $source$
            """ + TriggerBody + """
            $source$;
            CREATE TRIGGER "NormalizeOrderTextSource" BEFORE INSERT OR UPDATE OF "Name","Description" ON public."Order"
                FOR EACH ROW EXECUTE FUNCTION public."NormalizeOrderTextSource"();
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(SchemaGuard + """
            DO $guard$
            BEGIN
                IF (SELECT count(*) FROM pg_proc WHERE pronamespace = 'public'::regnamespace AND proname = 'NormalizeOrderTextSource') <> 1
                    OR NOT EXISTS (SELECT 1 FROM pg_proc WHERE oid = to_regprocedure('public."NormalizeOrderTextSource"()')
                    AND prorettype = 'trigger'::regtype AND prolang = (SELECT oid FROM pg_language WHERE lanname = 'plpgsql')
                    AND proowner = (SELECT oid FROM pg_roles WHERE rolname = current_user)
                    AND NOT prosecdef AND NOT proisstrict AND NOT proleakproof AND prokind = 'f' AND proparallel = 'u' AND provolatile = 'v'
                    AND proconfig = ARRAY['search_path=pg_catalog'] AND prosrc = $source$
            """ + TriggerBody + """
            $source$) OR (SELECT count(*) FROM pg_trigger WHERE tgfoid = to_regprocedure('public."NormalizeOrderTextSource"()')) <> 1
                    OR NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'public."Order"'::regclass
                    AND tgname = 'NormalizeOrderTextSource' AND tgfoid = to_regprocedure('public."NormalizeOrderTextSource"()')
                    AND tgtype = 23 AND tgenabled = 'O' AND NOT tgisinternal AND tgconstraint = 0 AND tgqual IS NULL
                    AND tgnargs = 0 AND octet_length(tgargs) = 0
                    AND tgattr::text = (SELECT string_agg(attnum::text,' ' ORDER BY CASE attname WHEN 'Name' THEN 0 ELSE 1 END)
                        FROM pg_attribute WHERE attrelid = 'public."Order"'::regclass AND attname IN ('Name','Description') AND NOT attisdropped)) THEN
                    RAISE EXCEPTION 'Order text normalizer downgrade objects are incompatible.';
                END IF;
            END $guard$;
            DROP TRIGGER "NormalizeOrderTextSource" ON public."Order";
            DROP FUNCTION public."NormalizeOrderTextSource"();
            """);
    }
}
