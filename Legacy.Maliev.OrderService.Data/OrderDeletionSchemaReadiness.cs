using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Legacy.Maliev.OrderService.Data;

/// <summary>Uncached physical-schema admission; migration history alone is not authority.</summary>
public static class OrderDeletionSchemaReadiness
{
    public static async Task<bool> CheckAsync(OrderDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            return await context.Database.SqlQueryRaw<bool>(
                """
                SELECT (
                    EXISTS (SELECT 1 FROM pg_class t JOIN pg_namespace n ON n.oid = t.relnamespace
                            WHERE n.nspname = 'public' AND t.relname = 'OrderDeletionIntent'
                              AND t.relkind = 'r' AND NOT t.relrowsecurity AND NOT t.relforcerowsecurity)
                    AND NOT EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = to_regclass('public."OrderDeletionIntent"')
                                    AND a.attnum > 0 AND NOT a.attisdropped AND a.atthasdef)
                    AND NOT EXISTS (SELECT 1 FROM pg_attrdef d WHERE d.adrelid = to_regclass('public."OrderDeletionIntent"'))
                    AND NOT EXISTS (SELECT 1 FROM pg_rewrite r WHERE r.ev_class = to_regclass('public."OrderDeletionIntent"'))
                    AND NOT EXISTS (SELECT 1 FROM pg_trigger t WHERE t.tgrelid = to_regclass('public."OrderDeletionIntent"') AND NOT t.tgisinternal)
                    AND
                    (SELECT count(*) FROM pg_attribute a
                     JOIN pg_class t ON t.oid = a.attrelid JOIN pg_namespace n ON n.oid = t.relnamespace
                     JOIN (VALUES
                       ('OrderId', 'integer', true), ('DeletionId', 'uuid', true),
                       ('RequestedAtUtc', 'timestamp with time zone', true),
                       ('StatusCleanupCompletedAtUtc', 'timestamp with time zone', false),
                       ('CompletedAtUtc', 'timestamp with time zone', false),
                       ('AttemptCount', 'integer', true), ('NextAttemptAtUtc', 'timestamp with time zone', true)
                     ) expected(name, type, required) ON a.attname = expected.name
                     WHERE n.nspname = 'public' AND t.relname = 'OrderDeletionIntent'
                       AND a.attnum > 0 AND NOT a.attisdropped AND a.attnotnull = expected.required
                       AND a.atttypid = expected.type::regtype AND a.attidentity = '' AND a.attgenerated = '') = 7
                    AND (SELECT count(*) FROM pg_attribute a WHERE a.attrelid = to_regclass('public."OrderDeletionIntent"')
                         AND a.attnum > 0 AND NOT a.attisdropped) = 7
                    AND (SELECT count(*) FROM pg_index i JOIN pg_class idx ON idx.oid = i.indexrelid
                         JOIN pg_am method ON method.oid = idx.relam
                         WHERE i.indrelid = to_regclass('public."OrderDeletionIntent"') AND i.indisvalid AND i.indisready
                         AND method.amname = 'btree' AND i.indimmediate
                         AND NOT EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conindid = i.indexrelid
                                         AND (NOT c.convalidated OR c.condeferrable OR c.condeferred))
                         AND (
                           (idx.relname = 'PK_OrderDeletionIntent' AND i.indisprimary AND i.indisunique AND i.indpred IS NULL
                            AND pg_get_indexdef(i.indexrelid, 1, true) = '"OrderId"' AND i.indnkeyatts = 1 AND i.indoption = '0'::int2vector)
                           OR (idx.relname = 'IX_OrderDeletionIntent_DeletionId' AND i.indisunique AND i.indpred IS NULL
                            AND pg_get_indexdef(i.indexrelid, 1, true) = '"DeletionId"' AND i.indnkeyatts = 1 AND i.indoption = '0'::int2vector)
                           OR (idx.relname = 'IX_OrderDeletionIntent_PendingDue' AND NOT i.indisunique AND i.indnkeyatts = 2
                            AND i.indoption = '0 0'::int2vector
                            AND pg_get_indexdef(i.indexrelid, 1, true) = '"NextAttemptAtUtc"'
                            AND pg_get_indexdef(i.indexrelid, 2, true) = '"OrderId"'
                            AND pg_get_expr(i.indpred, i.indrelid) = '("CompletedAtUtc" IS NULL)')
                         )) = 3
                    AND NOT EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conrelid = to_regclass('public."OrderDeletionIntent"') AND c.contype = 'f')
                    AND EXISTS (SELECT 1 FROM pg_constraint c WHERE c.conrelid = to_regclass('public."OrderDeletionIntent"')
                                AND c.conname = 'CK_OrderDeletionIntent_AttemptCount' AND c.contype = 'c' AND c.convalidated
                                AND pg_get_expr(c.conbin, c.conrelid) = '("AttemptCount" >= 0)')
                ) AS "Value"
                """).SingleAsync(cancellationToken);
        }
        catch (DbException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
