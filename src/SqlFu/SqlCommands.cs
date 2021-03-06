﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SqlFu.Internals;

namespace SqlFu
{
    public static class SqlCommands
    {
        
        public static T FirstOrDefault<T>(this DbAccess db,string sql,params object[] args)
        {
            return db.Query<T>(sql,args).FirstOrDefault();
        }
        
        #region Insert

        public static LastInsertId Insert(this DbAccess db, string table, object data)
        {
            table.MustNotBeEmpty();
            data.MustNotBeNull();
            return Insert(db, new TableInfo(table), data);
        }

        public static LastInsertId Insert<T>(this DbAccess db, T data) where T : class
        {
            data.MustNotBeNull();
            return Insert(db, TableInfo.ForType(typeof(T)), data);
        }

        static List<object> FillArgs(IDictionary<string,object> dict,TableInfo ti,IHaveDbProvider p ,StringBuilder sb=null)
        {
            var args = new List<object>();
            foreach (var col in dict)
            {
                if (col.Key == ti.PrimaryKey)
                {
                    if (ti.AutoGenerated)continue;
                }

                if (ti.Excludes.Any(n => n.Equals(col.Key, StringComparison.InvariantCulture))) continue;
                if (sb!=null) sb.AppendFormat("{0},", p.EscapeName(col.Key));
                if (ti.ConvertToString.Any(t => t == col.Key))
                {
                    args.Add(col.Value.ToString());
                }
                else
                {
                    args.Add(col.Value);
                }

            }
            return args;
        }

        static LastInsertId Insert(DbAccess db, TableInfo ti, object data)
        {
            var p = db.Provider;
            List<object> args=null;
            var d = data.ToDictionary();
            if (ti.InsertSql == null)
            {
                var sb = new StringBuilder("Insert into");
                sb.AppendFormat(" {0} (", p.EscapeName(ti.Name));

                args = FillArgs(d, ti, p, sb);
              
                sb.Remove(sb.Length - 1, 1);

                sb.Append(") values(");

                for (var i = 0; i < args.Count; i++)
                {
                    sb.Append("@" + i + ",");
                }
                sb.Remove(sb.Length - 1, 1);
                sb.Append(")");
                ti.InsertSql = sb.ToString();
            }
            if (args == null)
            {
                args = FillArgs(d, ti, p);
            }

            var st = db.WithSql(ti.InsertSql, args.ToArray());
            st.ReuseCommand = true;
            LastInsertId rez;
            try
            {
                rez = db.Provider.ExecuteInsert(st,ti.PrimaryKey);
            }
            finally
            {
                db.CloseConnection();
            }

            return rez;
        }
        #endregion

        #region Update

        /// <summary>
        /// If both poco has id property and the Id arg is specified, the arg is used
        /// </summary>
        /// <param name="db"></param>
        /// <param name="table"></param>
        /// <param name="data"></param>
        /// <param name="id"></param>
        /// <returns></returns>
        public static int Update(this DbAccess db, string table, object data, object id = null)
        {
            var ti = new TableInfo(table);
            return Update(db, ti, data, id);
        }

        /// <summary>
        /// If both poco has id property and the Id arg is specified, the arg is used
        /// </summary>
        public static int Update<T>(this DbAccess db, object data, object id = null)
        {
            var ti = TableInfo.ForType(typeof(T));
            return Update(db, ti, data, id);
        }

        private static int Update(DbAccess db, TableInfo ti, object data, object id = null)
        {
            var sb = new StringBuilder();
            sb.AppendFormat("update {0} set", db.Provider.EscapeName(ti.Name));
            var d = data.ToDictionary();
            bool hasId = false;
            int i = 0;
            var args = new List<object>();
            foreach (var k in d)
            {
                if (k.Key == ti.PrimaryKey)
                {
                    hasId = true;
                    continue;
                }
                if (ti.Excludes.Any(c => c == k.Key)) continue;
                sb.AppendFormat(" {0}={1},", db.Provider.EscapeName(k.Key), db.Provider.ParamPrefix + i);
                if (ti.ConvertToString.Any(s=>s==k.Key))
                {
                    args.Add(k.Value.ToString());
                }
                else
                {
                    args.Add(k.Value);
                }                
                i++;
            }
            sb.Remove(sb.Length - 1, 1);
            if (id != null || hasId)
            {
                sb.AppendFormat(" where {0}={1}", db.Provider.EscapeName(ti.PrimaryKey), db.Provider.ParamPrefix + i);
                hasId = true;
                if (id == null) id = d[ti.PrimaryKey];
            }

            if (hasId) args.Add(id);

            return db.ExecuteCommand(sb.ToString(), args.ToArray());
        }

        #endregion

        public static int Delete<T>(this DbAccess db, string condition, params object[] args)
        {
            var ti = TableInfo.ForType(typeof(T));
            return db.ExecuteCommand(string.Format("delete from {0} where {1}", db.Provider.EscapeName(ti.Name), condition), args);
        }
    }
}