using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.OrderService.Data.Migrations.Order;

/// <inheritdoc />
public partial class NormalizeOrderFileBucketSourceStorage : Migration
{
    private const string Body = """
        DECLARE units integer := 0; character_index integer := 0; width integer; scalar text;
        BEGIN
            IF NEW."Bucket" IS NOT NULL AND
                char_length(NEW."Bucket") + char_length(regexp_replace(NEW."Bucket" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 50 THEN
                WHILE units < 50 LOOP
                    scalar := substr(NEW."Bucket", character_index + 1, 1);
                    IF scalar COLLATE "C" = '' THEN EXIT; END IF;
                    width := CASE WHEN octet_length(scalar) = 4 THEN 2 ELSE 1 END;
                    IF units + width > 50 THEN EXIT; END IF;
                    units := units + width;
                    character_index := character_index + 1;
                END LOOP;
                IF btrim(substr(NEW."Bucket", character_index + 1), ' ') COLLATE "C" = '' THEN
                    NEW."Bucket" := substr(NEW."Bucket", 1, character_index);
                END IF;
            END IF;
            RETURN NEW;
        END;
        """;

    private const string SchemaGuard = """
        SET LOCAL lock_timeout = '5s';
        SET LOCAL statement_timeout = '30s';
        LOCK TABLE public."OrderFile" IN ACCESS EXCLUSIVE MODE;
        DO $guard$
        DECLARE field text; definition text; expected text;
        BEGIN
            IF current_setting('server_encoding') <> 'UTF8'
                OR NOT EXISTS (SELECT 1 FROM pg_class WHERE oid = 'public."OrderFile"'::regclass
                    AND relkind = 'r' AND relpersistence = 'p' AND NOT relrowsecurity AND NOT relforcerowsecurity
                    AND relowner = (SELECT oid FROM pg_roles WHERE rolname = current_user))
                OR EXISTS (SELECT 1 FROM pg_inherits WHERE inhrelid = 'public."OrderFile"'::regclass OR inhparent = 'public."OrderFile"'::regclass)
                OR EXISTS (SELECT 1 FROM pg_rewrite WHERE ev_class = 'public."OrderFile"'::regclass)
                OR to_regclass('pg_temp."__OrderFileBucketPaddingGuard"') IS NOT NULL THEN
                RAISE EXCEPTION 'OrderFile Bucket normalizer table custody is incompatible.';
            END IF;
            IF (SELECT count(*) FROM pg_attribute WHERE attrelid = 'public."OrderFile"'::regclass AND attname IN ('Bucket','ObjectName')
                AND attnotnull AND atttypid = 'text'::regtype AND atttypmod = -1
                AND attgenerated = '' AND attidentity = '' AND NOT attisdropped) <> 2
                OR EXISTS (SELECT 1 FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum
                    WHERE d.adrelid = 'public."OrderFile"'::regclass AND a.attname IN ('Bucket','ObjectName') AND NOT a.attisdropped) THEN
                RAISE EXCEPTION 'OrderFile Bucket normalizer columns are incompatible.';
            END IF;
            IF (SELECT count(*) FROM (SELECT 1 FROM public."OrderFile" LIMIT 10001) bounded) > 10000 THEN
                RAISE EXCEPTION 'OrderFile Bucket normalizer row limit exceeded.';
            END IF;
            IF EXISTS (SELECT 1 FROM public."OrderFile" WHERE char_length("Bucket") + char_length(regexp_replace("Bucket" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 50) THEN
                RAISE EXCEPTION 'Retained OrderFile Bucket exceeds source storage capacity.';
            END IF;
            CREATE TEMP TABLE "__OrderFileBucketPaddingGuard" (
                "Bucket" text,
                CONSTRAINT "expected_Bucket" CHECK (char_length("Bucket") + char_length(regexp_replace("Bucket" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 50)
            ) ON COMMIT DROP;
            FOREACH field IN ARRAY ARRAY['Bucket'] LOOP
                SELECT pg_get_constraintdef(oid) INTO expected FROM pg_constraint
                    WHERE conrelid = 'pg_temp."__OrderFileBucketPaddingGuard"'::regclass AND conname = 'expected_' || field;
                SELECT pg_get_constraintdef(oid) INTO definition FROM pg_constraint
                    WHERE conrelid = 'public."OrderFile"'::regclass AND conname = 'CK_OrderFile_' || field || '_SourceLength'
                    AND contype = 'c' AND convalidated AND conislocal AND coninhcount = 0 AND NOT connoinherit
                    AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'public."OrderFile"'::regclass AND attname = field)]::smallint[];
                IF definition IS NULL OR definition IS DISTINCT FROM expected THEN
                    RAISE EXCEPTION 'OrderFile Bucket normalizer constraint is incompatible.';
                END IF;
            END LOOP;
            DROP TABLE pg_temp."__OrderFileBucketPaddingGuard";
        END $guard$;

        """;

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(SchemaGuard + """
            DO $guard$
            BEGIN
                IF EXISTS (SELECT 1 FROM pg_proc WHERE pronamespace = 'public'::regnamespace AND proname = 'NormalizeOrderFileBucketSource')
                    OR EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'public."OrderFile"'::regclass AND NOT tgisinternal) THEN
                    RAISE EXCEPTION 'OrderFile Bucket normalizer upgrade objects already exist.';
                END IF;
            END $guard$;
            CREATE FUNCTION public."NormalizeOrderFileBucketSource"() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $source$
            """ + Body + """
            $source$;
            CREATE TRIGGER "NormalizeOrderFileBucketSource" BEFORE INSERT OR UPDATE OF "Bucket" ON public."OrderFile"
                FOR EACH ROW EXECUTE FUNCTION public."NormalizeOrderFileBucketSource"();
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(SchemaGuard
            + CustodyGuard("OrderFile", "NormalizeOrderFileBucketSource", Body, "'Bucket'")
            + """
            DROP TRIGGER "NormalizeOrderFileBucketSource" ON public."OrderFile";
            DROP FUNCTION public."NormalizeOrderFileBucketSource"();
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
                AND tgattr::text = (SELECT string_agg(attnum::text,' ' ORDER BY CASE attname WHEN 'Bucket' THEN 0 ELSE 1 END)
                    FROM pg_attribute WHERE attrelid = 'public."{{table}}"'::regclass AND attname IN ({{columns}}) AND NOT attisdropped)) THEN
                RAISE EXCEPTION 'OrderFile Bucket normalizer downgrade objects are incompatible.';
            END IF;
        END $guard$;

        """;
}
