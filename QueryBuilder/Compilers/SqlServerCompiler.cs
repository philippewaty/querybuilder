using System.Linq;

namespace SqlKata.Compilers
{
    public class SqlServerCompiler : Compiler
    {
        public SqlServerCompiler()
        {
            OpeningIdentifier = "[";
            ClosingIdentifier = "]";
            LastId = "SELECT scope_identity() as Id";
        }

        public override string EngineCode { get; } = EngineCodes.SqlServer;
        public bool UseLegacyPagination { get; set; } = false;

        protected override SqlResult CompileSelectQuery(Query query)
        {
            if (!UseLegacyPagination || !query.HasOffset(EngineCode))
            {
                return base.CompileSelectQuery(query);
            }

            query = query.Clone();

            var ctx = new SqlResult(parameterPlaceholder, EscapeCharacter)
            {
                Query = query,
            };

            var limit = query.GetLimit(EngineCode);
            var offset = query.GetOffset(EngineCode);


            if (!query.HasComponent("select"))
            {
                query.Select("*");
            }

            var order = CompileOrders(ctx) ?? "ORDER BY (SELECT 0)";

            query.SelectRaw($"ROW_NUMBER() OVER ({order}) AS [row_num]", ctx.Bindings.ToArray());

            query.ClearComponent("order");


            var result = base.CompileSelectQuery(query);

            if (limit == 0)
            {
                result.RawSql = $"SELECT * FROM ({result.RawSql}) AS [results_wrapper] WHERE [row_num] >= {parameterPlaceholder}";
                result.Bindings.Add(offset + 1);
            }
            else
            {
                result.RawSql = $"SELECT * FROM ({result.RawSql}) AS [results_wrapper] WHERE [row_num] BETWEEN {parameterPlaceholder} AND {parameterPlaceholder}";
                result.Bindings.Add(offset + 1);
                result.Bindings.Add(limit + offset);
            }

            return result;
        }

        protected override string CompileColumns(SqlResult ctx)
        {
            var compiled = base.CompileColumns(ctx);

            if (!UseLegacyPagination)
            {
                return compiled;
            }

            // If there is a limit on the query, but not an offset, we will add the top
            // clause to the query, which serves as a "limit" type clause within the
            // SQL Server system similar to the limit keywords available in MySQL.
            var limit = ctx.Query.GetLimit(EngineCode);
            var offset = ctx.Query.GetOffset(EngineCode);

            if (limit > 0 && offset == 0)
            {
                // top bindings should be inserted first
                ctx.Bindings.Insert(0, limit);

                ctx.Query.ClearComponent("limit");

                // handle distinct
                if (compiled.IndexOf("SELECT DISTINCT") == 0)
                {
                    return $"SELECT DISTINCT TOP ({parameterPlaceholder}){compiled.Substring(15)}";
                }

                return $"SELECT TOP ({parameterPlaceholder}){compiled.Substring(6)}";
            }

            return compiled;
        }

        public override string CompileLimit(SqlResult ctx)
        {
            if (UseLegacyPagination)
            {
                // in legacy versions of Sql Server, limit is handled by TOP
                // and ROW_NUMBER techniques
                return null;
            }

            var limit = ctx.Query.GetLimit(EngineCode);
            var offset = ctx.Query.GetOffset(EngineCode);

            if (limit == 0 && offset == 0)
            {
                return null;
            }

            var safeOrder = "";
            if (!ctx.Query.HasComponent("order"))
            {
                safeOrder = "ORDER BY (SELECT 0) ";
            }

            if (limit == 0)
            {
                ctx.Bindings.Add(offset);
                return $"{safeOrder}OFFSET {parameterPlaceholder} ROWS";
            }

            ctx.Bindings.Add(offset);
            ctx.Bindings.Add(limit);

            return $"{safeOrder}OFFSET {parameterPlaceholder} ROWS FETCH NEXT {parameterPlaceholder} ROWS ONLY";
        }

        public override string CompileRandom(string seed)
        {
            return "NEWID()";
        }

        public override string CompileTrue()
        {
            return "cast(1 as bit)";
        }

        public override string CompileFalse()
        {
            return "cast(0 as bit)";
        }

        protected override string CompileBasicDateCondition(SqlResult ctx, BasicDateCondition condition)
        {
            var column = Wrap(condition.Column);
            var part = condition.Part.ToUpperInvariant();

            string left;

            if (part == "TIME" || part == "DATE")
            {
                left = $"CAST({column} AS {part.ToUpperInvariant()})";
            }
            else
            {
                left = $"DATEPART({part.ToUpperInvariant()}, {column})";
            }

            var sql = $"{left} {condition.Operator} {Parameter(ctx, condition.Value)}";

            if (condition.IsNot)
            {
                return $"NOT ({sql})";
            }

            return sql;
        }

        protected override SqlResult CompileAdHocQuery(AdHocTableFromClause adHoc)
        {
            var ctx = new SqlResult(parameterPlaceholder, EscapeCharacter);

            var colNames = string.Join(", ", adHoc.Columns.Select(Wrap));

            var valueRow = string.Join(", ", Enumerable.Repeat(parameterPlaceholder, adHoc.Columns.Count));
            var valueRows = string.Join(", ", Enumerable.Repeat($"({valueRow})", adHoc.Values.Count / adHoc.Columns.Count));
            var sql = $"SELECT {colNames} FROM (VALUES {valueRows}) AS tbl ({colNames})";

            ctx.RawSql = sql;
            ctx.Bindings = adHoc.Values;

            return ctx;
        }
    }
}
