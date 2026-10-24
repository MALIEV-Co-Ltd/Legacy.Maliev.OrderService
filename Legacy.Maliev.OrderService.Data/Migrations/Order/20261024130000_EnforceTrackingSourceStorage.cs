using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Legacy.Maliev.OrderService.Data.Migrations.Order;

/// <inheritdoc />
public partial class EnforceTrackingSourceStorage : Migration
{
    private const string TriggerBody = """
        DECLARE units integer := 0; character_index integer := 0; width integer; scalar text;
        BEGIN
            IF NEW."TrackingNumber" IS NULL OR
                char_length(NEW."TrackingNumber") + char_length(regexp_replace(NEW."TrackingNumber" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 250 THEN
                RETURN NEW;
            END IF;
            WHILE units < 250 LOOP
                scalar := substr(NEW."TrackingNumber", character_index + 1, 1);
                IF scalar = '' THEN EXIT; END IF;
                width := CASE WHEN octet_length(scalar) = 4 THEN 2 ELSE 1 END;
                IF units + width > 250 THEN EXIT; END IF;
                units := units + width;
                character_index := character_index + 1;
            END LOOP;
            IF btrim(substr(NEW."TrackingNumber", character_index + 1), ' ') = '' THEN
                NEW."TrackingNumber" := substr(NEW."TrackingNumber", 1, character_index);
            END IF;
            RETURN NEW;
        END;
        """;

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '5s';
            SET LOCAL statement_timeout = '30s';
            LOCK TABLE public."Order" IN ACCESS EXCLUSIVE MODE;
            DO $guard$
            BEGIN
                IF current_setting('server_encoding') <> 'UTF8'
                    OR NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = 'public."Order"'::regclass
                        AND attname = 'TrackingNumber' AND NOT attnotnull AND atttypid = 'varchar'::regtype AND atttypmod = 254 AND NOT attisdropped)
                    OR EXISTS (SELECT 1 FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum
                        WHERE d.adrelid = 'public."Order"'::regclass AND a.attname = 'TrackingNumber' AND NOT a.attisdropped)
                    OR EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = 'public."Order"'::regclass AND conname = 'CK_Order_TrackingNumber_SourceLength')
                    OR EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'public."Order"'::regclass AND tgname = 'NormalizeOrderTrackingSource')
                    OR EXISTS (SELECT 1 FROM pg_proc WHERE pronamespace = 'public'::regnamespace AND proname = 'NormalizeOrderTrackingSource') THEN
                    RAISE EXCEPTION 'Order tracking upgrade schema is incompatible.';
                END IF;
                IF (SELECT count(*) FROM (SELECT 1 FROM public."Order" LIMIT 10001) bounded) > 10000 THEN
                    RAISE EXCEPTION 'Order tracking upgrade row limit exceeded.';
                END IF;
                IF EXISTS (SELECT 1 FROM public."Order" WHERE
                    char_length("TrackingNumber") + char_length(regexp_replace("TrackingNumber" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 250) THEN
                    RAISE EXCEPTION 'Retained tracking values exceed the source storage capacity.';
                END IF;
            END $guard$;
            ALTER TABLE public."Order" ALTER COLUMN "TrackingNumber" TYPE text;
            ALTER TABLE public."Order" ADD CONSTRAINT "CK_Order_TrackingNumber_SourceLength" CHECK
                (char_length("TrackingNumber") + char_length(regexp_replace("TrackingNumber" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 250) NOT VALID;
            ALTER TABLE public."Order" VALIDATE CONSTRAINT "CK_Order_TrackingNumber_SourceLength";
            CREATE FUNCTION public."NormalizeOrderTrackingSource"() RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $source$
            """ + TriggerBody + """
            $source$;
            CREATE TRIGGER "NormalizeOrderTrackingSource" BEFORE INSERT OR UPDATE OF "TrackingNumber" ON public."Order"
                FOR EACH ROW EXECUTE FUNCTION public."NormalizeOrderTrackingSource"();
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
                        AND attname = 'TrackingNumber' AND NOT attnotnull AND atttypid = 'text'::regtype AND NOT attisdropped)
                    OR EXISTS (SELECT 1 FROM pg_attrdef d JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum
                        WHERE d.adrelid = 'public."Order"'::regclass AND a.attname = 'TrackingNumber' AND NOT a.attisdropped) THEN
                    RAISE EXCEPTION 'Order tracking downgrade schema is incompatible.';
                END IF;
                IF (SELECT count(*) FROM (SELECT 1 FROM public."Order" LIMIT 10001) bounded) > 10000 THEN
                    RAISE EXCEPTION 'Order tracking downgrade row limit exceeded.';
                END IF;
                IF EXISTS (SELECT 1 FROM public."Order" WHERE
                    char_length("TrackingNumber") + char_length(regexp_replace("TrackingNumber" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) > 250) THEN
                    RAISE EXCEPTION 'Retained tracking values exceed the source storage capacity.';
                END IF;
                CREATE TEMP TABLE "__OrderTrackingSourceGuard" ("TrackingNumber" text, CONSTRAINT "expected"
                    CHECK (char_length("TrackingNumber") + char_length(regexp_replace("TrackingNumber" COLLATE "C", U&'[\0001-\FFFF]', '', 'g')) <= 250)) ON COMMIT DROP;
                SELECT pg_get_constraintdef(oid) INTO expected FROM pg_constraint
                    WHERE conrelid = 'pg_temp."__OrderTrackingSourceGuard"'::regclass AND conname = 'expected';
                SELECT pg_get_constraintdef(oid) INTO definition FROM pg_constraint
                    WHERE conrelid = 'public."Order"'::regclass AND conname = 'CK_Order_TrackingNumber_SourceLength'
                    AND contype = 'c' AND convalidated AND conislocal AND coninhcount = 0 AND NOT connoinherit
                    AND conkey = ARRAY[(SELECT attnum FROM pg_attribute WHERE attrelid = 'public."Order"'::regclass AND attname = 'TrackingNumber')]::smallint[];
                IF definition IS NULL OR definition IS DISTINCT FROM expected THEN
                    RAISE EXCEPTION 'Order tracking downgrade constraint is incompatible.';
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_proc WHERE oid = to_regprocedure('public."NormalizeOrderTrackingSource"()')
                    AND prorettype = 'trigger'::regtype AND prolang = (SELECT oid FROM pg_language WHERE lanname = 'plpgsql')
                    AND NOT prosecdef AND NOT proisstrict AND NOT proleakproof AND prokind = 'f' AND proparallel = 'u' AND provolatile = 'v'
                    AND proconfig = ARRAY['search_path=pg_catalog'] AND prosrc = $source$
            """ + TriggerBody + """
            $source$) OR NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'public."Order"'::regclass
                    AND tgname = 'NormalizeOrderTrackingSource' AND tgfoid = to_regprocedure('public."NormalizeOrderTrackingSource"()')
                    AND tgtype = 23 AND tgenabled = 'O' AND NOT tgisinternal AND tgconstraint = 0 AND tgqual IS NULL
                    AND tgnargs = 0 AND octet_length(tgargs) = 0
                    AND tgattr::text = (SELECT attnum::text FROM pg_attribute WHERE attrelid = 'public."Order"'::regclass AND attname = 'TrackingNumber')) THEN
                    RAISE EXCEPTION 'Order tracking downgrade normalizer is incompatible.';
                END IF;
                DROP TABLE pg_temp."__OrderTrackingSourceGuard";
            END $guard$;
            DROP TRIGGER "NormalizeOrderTrackingSource" ON public."Order";
            DROP FUNCTION public."NormalizeOrderTrackingSource"();
            ALTER TABLE public."Order" DROP CONSTRAINT "CK_Order_TrackingNumber_SourceLength";
            ALTER TABLE public."Order" ALTER COLUMN "TrackingNumber" TYPE character varying(250);
            """);
    }
}
