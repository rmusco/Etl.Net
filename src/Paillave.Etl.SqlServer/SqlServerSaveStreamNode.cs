﻿using Paillave.Etl.Core;
using System;
using System.Collections.Generic;
using System.Text;
using Paillave.Etl.Reactive.Operators;
using System.Linq;
using System.Data.SqlClient;
using System.Reflection;
using System.Linq.Expressions;

namespace Paillave.Etl.SqlServer
{

    public class SqlServerSaveCommandArgsBuilder<TIn, TValue> where TIn : class
    {
        internal Func<TIn, TValue> GetValue { get; }
        internal string ConnectionName { get; private set; }
        internal string Table { get; private set; } = typeof(TValue).Name;
        internal Expression<Func<TValue, object>> Pivot { get; private set; } = null;
        internal Expression<Func<TValue, object>> Computed { get; private set; } = null;
        internal SqlServerSaveCommandArgsBuilder(Func<TIn, TValue> getValue) => (GetValue) = (getValue);
        public SqlServerSaveCommandArgsBuilder<TIn, TValue> ToTable(string table)
        {
            this.Table = table;
            return this;
        }
        public SqlServerSaveCommandArgsBuilder<TIn, TValue> SeekOn(Expression<Func<TValue, object>> pivot)
        {
            this.Pivot = pivot;
            return this;
        }
        public SqlServerSaveCommandArgsBuilder<TIn, TValue> DoNotSave(Expression<Func<TValue, object>> computed)
        {
            this.Computed = computed;
            return this;
        }
        public SqlServerSaveCommandArgsBuilder<TIn, TValue> WithConnection(string connectionName)
        {
            this.ConnectionName = connectionName;
            return this;
        }
        internal SqlServerSaveCommandArgs<TIn, TStream, TValue> GetArgs<TStream>(TStream sourceStream) where TStream : IStream<TIn>
            => new SqlServerSaveCommandArgs<TIn, TStream, TValue>
            {
                Table = this.Table,
                Computed = this.Computed,
                ConnectionName = this.ConnectionName,
                GetValue = this.GetValue,
                Pivot = this.Pivot,
                SourceStream = sourceStream
            };
    }


    public class SqlServerSaveCommandArgs<TIn, TStream, TValue>
        where TIn : class
        where TStream : IStream<TIn>
    {
        public string Table { get; set; }
        public Expression<Func<TValue, object>> Pivot { get; set; }
        public Expression<Func<TValue, object>> Computed { get; set; }
        public string ConnectionName { get; set; }
        public TStream SourceStream { get; set; }
        public Func<TIn, TValue> GetValue { get; set; }
    }
    public class SqlServerSaveStreamNode<TIn, TStream, TValue> : StreamNodeBase<TIn, TStream, SqlServerSaveCommandArgs<TIn, TStream, TValue>>
        where TIn : class
        where TStream : IStream<TIn>
    {
        private static IDictionary<string, PropertyInfo> _inPropertyInfos = typeof(TIn).GetProperties().ToDictionary(i => i.Name, StringComparer.InvariantCultureIgnoreCase);
        public override ProcessImpact PerformanceImpact => ProcessImpact.Heavy;
        public override ProcessImpact MemoryFootPrint => ProcessImpact.Light;
        public SqlServerSaveStreamNode(string name, SqlServerSaveCommandArgs<TIn, TStream, TValue> args) : base(name, args) { }
        protected override TStream CreateOutputStream(SqlServerSaveCommandArgs<TIn, TStream, TValue> args)
        {
            var ret = args.SourceStream.Observable.Do(i => ProcessItem(args.GetValue(i), args.ConnectionName));
            return base.CreateMatchingStream(ret, args.SourceStream);
        }
        private string _sqlStatement = null;
        private List<PropertyInfo> _pivot = null;
        private List<PropertyInfo> _computed = null;
        private string GetSqlStatement()
        {
            if (_sqlStatement == null)
            {
                _pivot = base.Args.Pivot == null ? new List<PropertyInfo>() : base.Args.Pivot.GetPropertyInfos();
                _computed = base.Args.Computed == null ? new List<PropertyInfo>() : base.Args.Computed.GetPropertyInfos();
                _sqlStatement = CreateSqlQuery(base.Args.Table, typeof(TIn).GetProperties().ToList(), _pivot, _computed);
            }
            return _sqlStatement;
        }
        private void ProcessItem(TValue item, string connectionName)
        {
            var resolver = this.ExecutionContext.DependencyResolver;
            var sqlConnection = connectionName == null ? resolver.Resolve<SqlConnection>() : resolver.Resolve<SqlConnection>(connectionName);
            // List<PropertyInfo> pivot = base.Args.Pivot == null ? new List<PropertyInfo>() : base.Args.Pivot.GetPropertyInfos();
            // List<PropertyInfo> computed = base.Args.Computed == null ? new List<PropertyInfo>() : base.Args.Computed.GetPropertyInfos();
            // var sqlQuery = CreateSqlQuery(base.Args.Table, typeof(TIn).GetProperties().ToList(), pivot, computed);
            var sqlStatement = GetSqlStatement();
            var command = new SqlCommand(sqlStatement, sqlConnection);
            // Regex getParamRegex = new Regex(@"@(?<param>\w*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
            // var allMatches = getParamRegex.Matches(base.Args.SqlQuery).ToList().Select(match => match.Groups["param"].Value).Distinct().ToList();
            foreach (var parameterName in _inPropertyInfos.Keys.Except(_computed.Select(i => i.Name)))
            {
                command.Parameters.Add(new SqlParameter($"@{parameterName}", _inPropertyInfos[parameterName].GetValue(item)));
            }
            using (var reader = command.ExecuteReader())
                if (reader.Read())
                    UpdateRecord(reader, item);
        }

        private void UpdateRecord(SqlDataReader record, TValue item)
        {
            IDictionary<string, object> values = new Dictionary<string, object>();
            for (int i = 0; i < record.FieldCount; i++)
                values[record.GetName(i)] = Convert.ChangeType(record.GetValue(i), record.GetFieldType(i));
            var updates = _inPropertyInfos.Join(values, i => i.Key, i => i.Key, (l, r) => new { Target = l.Value, NewValue = r.Value }, StringComparer.InvariantCultureIgnoreCase).ToList();
            foreach (var update in updates)
                update.Target.SetValue(item, update.NewValue);
        }

        private string CreateSqlQuery(string table, List<PropertyInfo> allProperties, List<PropertyInfo> pivot, List<PropertyInfo> computed)
        {
            var pivotsNames = pivot.Select(i => i.Name).ToList();
            var computedNames = computed.Select(i => i.Name).ToList();
            var allPropertyNames = allProperties.Select(i => i.Name).ToList();
            StringBuilder sb = new StringBuilder();
            if (pivot.Count > 0)
            {
                var pivotCondition = string.Join(" AND ", pivot.Select(p => $"p.{p.Name} = @{p.Name}"));
                sb.AppendLine($"if(exists(select 1 from {table} as p where {pivotCondition} ))");
                var setStatement = string.Join(", ", allPropertyNames.Except(pivotsNames).Except(computedNames).Select(i => $"{i} = @{i}").ToList());
                sb.AppendLine($"update p set {setStatement} output inserted.* from {table} as p where {pivotCondition};");
                sb.AppendLine("else");
            }
            var propsToInsert = allPropertyNames.Except(computedNames).ToList();
            sb.AppendLine($"insert into {table} ({string.Join(", ", propsToInsert)}) output inserted.*");
            sb.AppendLine($"values ({string.Join(", ", propsToInsert.Select(i => $"@{i}"))});");
            return sb.ToString();
        }
    }
}