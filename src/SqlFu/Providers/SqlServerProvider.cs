using System;
using System.Collections.Concurrent;
using System.Data;
using System.Text.RegularExpressions;

namespace SqlFu.Providers
{
    public class SqlServerProvider:AbstractProvider
    {
       class PagingInfo
       {
           public string countString;
           public string selectString;
       }
        public const string ProviderName = "System.Data.SqlClient";
        
        internal SqlServerProvider(string provider):base(provider)
        {
            
        }

        public SqlServerProvider():base(ProviderName)
        {
            
        }
        public override string FormatSql(string sql, params string[] paramNames)
        {
            return sql;
        }

        public override string EscapeName(string s)
        {
            return "[" + s + "]";
        }

        static Regex rxOrderBy = new Regex(@"\bORDER\s+BY\s+(?:\((?>\((?<depth>)|\)(?<-depth>)|.?)*(?(depth)(?!))\)|[\w\(\)\.])+(?:\s+(?:ASC|DESC))?(?:\s*,\s*(?:\((?>\((?<depth>)|\)(?<-depth>)|.?)*(?(depth)(?!))\)|[\w\(\)\.])+(?:\s+(?:ASC|DESC))?)*", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.Compiled);

        private ConcurrentDictionary<int, PagingInfo> _pagingCache;

        public override void MakePaged(string sql, out string selecSql, out string countSql)
        {
            if (_pagingCache==null)
            {
                _pagingCache= new ConcurrentDictionary<int, PagingInfo>();
            }
            PagingInfo info;
            var key = sql.GetHashCode();
            if (_pagingCache.TryGetValue(key,out info))
            {
                selecSql = info.selectString;
                countSql = info.countString;
                return;
            }

            int fromidx;
            var body = GetPagingBody(sql,out fromidx);
            selecSql = sql;
            var all = rxOrderBy.Matches(body);
            string orderBy = "select null";
            if (all.Count>0)
            {
                var m = all[all.Count-1];
                orderBy = m.Captures[0].Value;
                body = body.Substring(0, m.Index);
            }
            countSql = "select count(*) " + body;
            var sidx = sql.IndexOf("select", StringComparison.InvariantCultureIgnoreCase);
            if (sidx<0) throw new InvalidPagedSqlException(sql);
            var columns = sql.Substring(sidx+7, fromidx-sidx-7);
            selecSql =
                string.Format(
                    @"SELECT * FROM 
(SELECT ROW_NUMBER() OVER ({0}) sqlfu_rn, {1} {2}) 
sqlfu_paged WHERE sqlfu_rn>@{3} AND sqlfu_rn<=(@{3}+@{4})",orderBy,columns,body,PagedSqlStatement.SkipParameterName,PagedSqlStatement.TakeParameterName);
            //cache it
            info=new PagingInfo();
            info.countString = countSql;
            info.selectString = selecSql;
            _pagingCache.TryAdd(key, info);            
        }

        public override void SetupParameter(IDbDataParameter param, string name, object value)
        {
            base.SetupParameter(param, name, value);
            if (value==null) return;
            var tp = value.GetType();
            if (tp==typeof(string))
            {
                param.Size = Math.Max((value as string).Length + 1, 4000);
            }

            if (tp.Name == "SqlGeography") //SqlGeography is a CLR Type
            {
                dynamic p = param;
                p.UdtTypeName = "geography";
                
            }

            else if (tp.Name == "SqlGeometry") //SqlGeometry is a CLR Type
            {
                dynamic p = param;
                p.UdtTypeName = "geometry";                
            }
        }

        public override LastInsertId ExecuteInsert(SqlStatement sql, string idKey)
        {
            sql.Sql += ";Select SCOPE_IDENTITY() as id";
           
            using(sql)
            {
                var rez = sql.ExecuteScalar();
                return new LastInsertId(rez);
            }                        
        }

        public override string ParamPrefix
        {
            get { return "@"; }
        }
    }
}