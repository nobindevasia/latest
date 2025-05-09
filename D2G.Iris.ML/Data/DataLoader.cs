﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.ML;
using Microsoft.ML.Data;
using D2G.Iris.ML.Core.Enums;

namespace D2G.Iris.ML.Data
{
    /// <summary>
    /// Streams data from a SQL table directly into an ML.NET IDataView via DatabaseLoader,
    /// tracks and prints how many rows were loaded, handles schema-qualified names,
    /// and can print a preview of loaded rows.
    /// </summary>
    public class DatabaseDataLoader 
    {
        private readonly MLContext _mlContext;
        private IDataView _lastLoadedDataView;
        private long? _lastLoadedRowCount;

        /// <summary>
        /// Allows injecting an MLContext (e.g. for testing) or uses a new one by default.
        /// </summary>
        public DatabaseDataLoader(MLContext mlContext = null)
        {
            _mlContext = mlContext ?? new MLContext();
        }

        /// <inheritdoc />
        public IDataView LoadDataFromSql(
            string sqlConnectionString,
            string tableName,
            IEnumerable<string> featureColumns,
            ModelType modelType,
            string targetColumn,
            string whereSyntax = "",
            int previewRowCount = 5)
        {
            Console.WriteLine("=============== Loading Data into IDataView ===============");

            // Handle schema-qualified table names: "schema.table"
            string fullTableName = tableName.Contains('.')
                ? string.Join('.', tableName.Split('.').Select(part => $"[{part}]"))
                : $"[{tableName}]";

            // 0) Get total row count via COUNT(*)
            using (var conn = new SqlConnection(sqlConnectionString))
            {
                conn.Open();
                var countSql = $"SELECT COUNT(*) FROM {fullTableName}" +
                               (!string.IsNullOrWhiteSpace(whereSyntax)
                                    ? $" WHERE {whereSyntax}" : string.Empty);
                using (var countCmd = new SqlCommand(countSql, conn))
                {
                    _lastLoadedRowCount = Convert.ToInt64(countCmd.ExecuteScalar());
                }
            }

            // 1) Build the SELECT clause
            var allCols = featureColumns.Concat(new[] { targetColumn })
                                        .Select(c => $"[{c}]");
            var sql = $"SELECT {string.Join(", ", allCols)} FROM {fullTableName}" +
                      (!string.IsNullOrWhiteSpace(whereSyntax)
                            ? $" WHERE {whereSyntax}" : string.Empty);

            // 2) Define loader schema (feature columns)
            var loaderCols = new List<DatabaseLoader.Column>();
            int idx = 0;
            foreach (var feat in featureColumns)
            {
                loaderCols.Add(new DatabaseLoader.Column(
                    name: feat,
                    dbType: DbType.Single,
                    index: idx++
                ));
            }

            // 3) Add label column with numeric DbType (to match actual SQL type)
            //    Use Int64 to accommodate SQL bigint, then transform downstream as needed
            DbType labelDbType = modelType switch
            {
                ModelType.BinaryClassification => DbType.Int64,
                ModelType.MultiClassClassification => DbType.Int64,
                ModelType.Regression => DbType.Single,
                _ => DbType.Int64
            };
            loaderCols.Add(new DatabaseLoader.Column(
                name: targetColumn,
                dbType: labelDbType,
                index: idx
            ));

            // 4) Create DatabaseLoader and source
            var dbLoader = _mlContext.Data.CreateDatabaseLoader(loaderCols.ToArray());
            var dbSource = new DatabaseSource(
                providerFactory: SqlClientFactory.Instance,
                connectionString: sqlConnectionString,
                commandText: sql
            );

            // 5) Load IDataView and cache
            var dataView = dbLoader.Load(dbSource);
            _lastLoadedDataView = dataView;

            // 6) Print row count
            Console.WriteLine($">> Loaded {_lastLoadedRowCount ?? 0} rows of data.");

            // 7) Print schema
            Console.WriteLine(">> Loaded Schema:");
            foreach (var col in dataView.Schema)
                Console.WriteLine($"   • {col.Name} ({col.Type.RawType.Name})");

            // 8) Preview rows
            Console.WriteLine($">> Previewing first {previewRowCount} rows:");
            var preview = dataView.Preview(maxRows: previewRowCount);

            // Print header row
            Console.WriteLine(string.Join("\t", preview.Schema.Select(c => c.Name)));
            // Print data rows
            foreach (var row in preview.RowView)
            {
                Console.WriteLine(string.Join("\t", row.Values.Select(v => v.Value?.ToString() ?? string.Empty)));
            }

            Console.WriteLine("==========================================================");
            return dataView;
        }

        /// <summary>
        /// Returns the number of rows in the most recently loaded dataset.
        /// </summary>
        public long? GetLastLoadedRowCount()
        {
            return _lastLoadedRowCount;
        }
    }
}
