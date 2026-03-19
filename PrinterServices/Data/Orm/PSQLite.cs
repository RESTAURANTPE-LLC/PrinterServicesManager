// PSQLite - Clean sqlite-net ORM for PrinterServices
// Based on sqlite-net by Krueger Systems (MIT License)
// NO QuipuNetX dependencies - fully autonomous
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Sqlite3DatabaseHandle = System.IntPtr;
using Sqlite3Statement = System.IntPtr;

#pragma warning disable 1591

namespace PSQLite
{
    #region Attributes
    [AttributeUsage(AttributeTargets.Class)]
    public class TableAttribute : Attribute
    {
        public string Name { get; set; }
        public TableAttribute(string name) { Name = name; }
    }
    [AttributeUsage(AttributeTargets.Property)]
    public class ColumnAttribute : Attribute
    {
        public string Name { get; set; }
        public ColumnAttribute(string name) { Name = name; }
    }
    [AttributeUsage(AttributeTargets.Property)]
    public class PrimaryKeyAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Property)]
    public class AutoIncrementAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Property)]
    public class IndexedAttribute : Attribute
    {
        public string Name { get; set; }
        public int Order { get; set; }
        public virtual bool Unique { get; set; }
        public IndexedAttribute() { }
        public IndexedAttribute(string name, int order) { Name = name; Order = order; }
    }
    [AttributeUsage(AttributeTargets.Property)]
    public class IgnoreAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Property)]
    public class UniqueAttribute : IndexedAttribute
    {
        public override bool Unique { get { return true; } set { } }
    }
    [AttributeUsage(AttributeTargets.Property)]
    public class MaxLengthAttribute : Attribute
    {
        public int Value { get; private set; }
        public MaxLengthAttribute(int length) { Value = length; }
    }
    [AttributeUsage(AttributeTargets.Property)]
    public class CollationAttribute : Attribute
    {
        public string Value { get; private set; }
        public CollationAttribute(string collation) { Value = collation; }
    }
    [AttributeUsage(AttributeTargets.Property)]
    public class NotNullAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.All)]
    public class PreserveAttribute : Attribute
    {
        public bool AllMembers;
        public bool Conditional;
    }
    #endregion

    #region Enums
    [Flags]
    public enum SQLiteOpenFlags
    {
        ReadOnly = 1, ReadWrite = 2, Create = 4,
        NoMutex = 0x8000, FullMutex = 0x10000,
        SharedCache = 0x20000, PrivateCache = 0x40000,
    }
    [Flags]
    public enum CreateFlags
    {
        None = 0x000, ImplicitPK = 0x001, ImplicitIndex = 0x002,
        AllImplicit = 0x003, AutoIncPK = 0x004,
        FullTextSearch3 = 0x100, FullTextSearch4 = 0x200
    }
    public enum CreateTableResult { Created, Migrated }
    public class CreateTablesResult
    {
        public Dictionary<Type, CreateTableResult> Results { get; private set; }
        public CreateTablesResult() { Results = new Dictionary<Type, CreateTableResult>(); }
    }
    public enum NotifyTableChangedAction { Insert, Update, Delete }
    public class NotifyTableChangedEventArgs : EventArgs
    {
        public TableMapping Table { get; private set; }
        public NotifyTableChangedAction Action { get; private set; }
        public NotifyTableChangedEventArgs(TableMapping table, NotifyTableChangedAction action) { Table = table; Action = action; }
    }
    #endregion

    #region Exceptions
    public class SQLiteException : Exception
    {
        public SQLite3.Result Result { get; private set; }
        protected SQLiteException(SQLite3.Result r, string message) : base(message) { Result = r; }
        public static SQLiteException New(SQLite3.Result r, string message) { return new SQLiteException(r, message); }
    }
    public class NotNullConstraintViolationException : SQLiteException
    {
        public IEnumerable<TableMapping.Column> Columns { get; protected set; }
        protected NotNullConstraintViolationException(SQLite3.Result r, string message) : base(r, message) { }
        protected NotNullConstraintViolationException(SQLite3.Result r, string message, TableMapping mapping, object obj)
            : base(r, message)
        {
            if (mapping != null && obj != null)
            {
                Columns = from c in mapping.Columns
                          where c.IsNullable == false && c.GetValue(obj) == null
                          select c;
            }
        }
        public static new NotNullConstraintViolationException New(SQLite3.Result r, string message) { return new NotNullConstraintViolationException(r, message); }
        public static NotNullConstraintViolationException New(SQLite3.Result r, string message, TableMapping mapping, object obj) { return new NotNullConstraintViolationException(r, message, mapping, obj); }
        public static NotNullConstraintViolationException New(SQLiteException ex, TableMapping mapping, object obj) { return new NotNullConstraintViolationException(ex.Result, ex.Message, mapping, obj); }
    }
    #endregion

    #region SQLiteConnection
    [Preserve(AllMembers = true)]
    public partial class SQLiteConnection : IDisposable
    {
        private bool _open;
        private TimeSpan _busyTimeout;
        readonly static Dictionary<string, TableMapping> _mappings = new Dictionary<string, TableMapping>();
        private Stopwatch _sw;
        private long _elapsedMilliseconds = 0;
        private int _transactionDepth = 0;
        private Random _rand = new Random();

        public Sqlite3DatabaseHandle Handle { get; private set; }
        static readonly Sqlite3DatabaseHandle NullHandle = default(Sqlite3DatabaseHandle);
        public string DatabasePath { get; private set; }
        public int LibVersionNumber { get; private set; }
        public bool TimeExecution { get; set; }
        public bool Trace { get; set; }
        public Action<string> Tracer { get; set; }
        public bool StoreDateTimeAsTicks { get; private set; }

        public SQLiteConnection(string databasePath, bool storeDateTimeAsTicks = true)
            : this(databasePath, SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create, storeDateTimeAsTicks)
        {
        }

        public SQLiteConnection(string databasePath, SQLiteOpenFlags openFlags, bool storeDateTimeAsTicks = true)
        {
            if (databasePath == null)
                throw new ArgumentException("Must be specified", "databasePath");

            DatabasePath = databasePath;
            LibVersionNumber = SQLite3.LibVersionNumber();

            Sqlite3DatabaseHandle handle;
            var databasePathAsBytes = GetNullTerminatedUtf8(DatabasePath);
            var r = SQLite3.Open(databasePathAsBytes, out handle, (int)openFlags, IntPtr.Zero);

            Handle = handle;
            if (r != SQLite3.Result.OK)
                throw SQLiteException.New(r, String.Format("Could not open database file: {0} ({1})", DatabasePath, r));

            _open = true;
            StoreDateTimeAsTicks = storeDateTimeAsTicks;
            BusyTimeout = TimeSpan.FromSeconds(1.0);
            Tracer = line => Debug.WriteLine(line);

            if (openFlags.HasFlag(SQLiteOpenFlags.ReadWrite))
            {
                ExecuteScalar<string>("PRAGMA journal_mode=WAL");
                ExecuteScalar<string>("PRAGMA synchronous=NORMAL");
                ExecuteScalar<string>("PRAGMA cache_size=10000");
            }
        }

        static byte[] GetNullTerminatedUtf8(string s)
        {
            var utf8Length = Encoding.UTF8.GetByteCount(s);
            var bytes = new byte[utf8Length + 1];
            Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
            return bytes;
        }

        static string Quote(string unsafeString)
        {
            if (unsafeString == null) return "NULL";
            return "'" + unsafeString.Replace("'", "''") + "'";
        }

        public TimeSpan BusyTimeout
        {
            get { return _busyTimeout; }
            set
            {
                _busyTimeout = value;
                if (Handle != NullHandle)
                    SQLite3.BusyTimeout(Handle, (int)_busyTimeout.TotalMilliseconds);
            }
        }

        public IEnumerable<TableMapping> TableMappings
        {
            get { lock (_mappings) { return new List<TableMapping>(_mappings.Values); } }
        }

        public TableMapping GetMapping(Type type, CreateFlags createFlags = CreateFlags.None)
        {
            TableMapping map;
            var key = type.FullName;
            lock (_mappings)
            {
                if (_mappings.TryGetValue(key, out map))
                {
                    if (createFlags != CreateFlags.None && createFlags != map.CreateFlags)
                    {
                        map = new TableMapping(type, createFlags);
                        _mappings[key] = map;
                    }
                }
                else
                {
                    map = new TableMapping(type, createFlags);
                    _mappings.Add(key, map);
                }
            }
            return map;
        }

        public TableMapping GetMapping<T>(CreateFlags createFlags = CreateFlags.None)
        {
            return GetMapping(typeof(T), createFlags);
        }

        private struct IndexedColumn { public int Order; public string ColumnName; }
        private struct IndexInfo { public string IndexName; public string TableName; public bool Unique; public List<IndexedColumn> Columns; }

        public int DropTable<T>() { return DropTable(GetMapping(typeof(T))); }
        public int DropTable(TableMapping map) { return Execute("drop table if exists \"" + map.TableName + "\""); }

        public CreateTableResult CreateTable<T>(CreateFlags createFlags = CreateFlags.None) { return CreateTable(typeof(T), createFlags); }

        public CreateTableResult CreateTable(Type ty, CreateFlags createFlags = CreateFlags.None)
        {
            var map = GetMapping(ty, createFlags);
            if (map.Columns.Length == 0)
                throw new Exception(string.Format("Cannot create a table without columns (does '{0}' have public properties?)", ty.FullName));

            var result = CreateTableResult.Created;
            var existingCols = GetTableInfo(map.TableName);

            if (existingCols.Count == 0)
            {
                bool fts3 = (createFlags & CreateFlags.FullTextSearch3) != 0;
                bool fts4 = (createFlags & CreateFlags.FullTextSearch4) != 0;
                bool fts = fts3 || fts4;
                var @virtual = fts ? "virtual " : string.Empty;
                var @using = fts3 ? "using fts3 " : fts4 ? "using fts4 " : string.Empty;

                var query = "create " + @virtual + "table if not exists \"" + map.TableName + "\" " + @using + "(\n";
                var decls = map.Columns.Select(p => Orm.SqlDecl(p, StoreDateTimeAsTicks));
                query += string.Join(",\n", decls.ToArray());
                query += ")";
                if (map.WithoutRowId) query += " without rowid";
                Execute(query);
            }
            else
            {
                result = CreateTableResult.Migrated;
                MigrateTable(map, existingCols);
            }

            var indexes = new Dictionary<string, IndexInfo>();
            foreach (var c in map.Columns)
            {
                foreach (var i in c.Indices)
                {
                    var iname = i.Name ?? map.TableName + "_" + c.Name;
                    IndexInfo iinfo;
                    if (!indexes.TryGetValue(iname, out iinfo))
                    {
                        iinfo = new IndexInfo { IndexName = iname, TableName = map.TableName, Unique = i.Unique, Columns = new List<IndexedColumn>() };
                        indexes.Add(iname, iinfo);
                    }
                    if (i.Unique != iinfo.Unique)
                        throw new Exception("All the columns in an index must have the same value for their Unique property");
                    iinfo.Columns.Add(new IndexedColumn { Order = i.Order, ColumnName = c.Name });
                }
            }
            foreach (var indexName in indexes.Keys)
            {
                var index = indexes[indexName];
                var columns = index.Columns.OrderBy(i => i.Order).Select(i => i.ColumnName).ToArray();
                CreateIndex(indexName, index.TableName, columns, index.Unique);
            }
            return result;
        }

        public CreateTablesResult CreateTables(CreateFlags createFlags = CreateFlags.None, params Type[] types)
        {
            var result = new CreateTablesResult();
            foreach (Type type in types)
                result.Results[type] = CreateTable(type, createFlags);
            return result;
        }

        public int CreateIndex(string indexName, string tableName, string[] columnNames, bool unique = false)
        {
            const string sqlFormat = "create {2} index if not exists \"{3}\" on \"{0}\"(\"{1}\")";
            var sql = String.Format(sqlFormat, tableName, string.Join("\", \"", columnNames), unique ? "unique" : "", indexName);
            return Execute(sql);
        }
        public int CreateIndex(string indexName, string tableName, string columnName, bool unique = false) { return CreateIndex(indexName, tableName, new string[] { columnName }, unique); }
        public int CreateIndex(string tableName, string columnName, bool unique = false) { return CreateIndex(tableName + "_" + columnName, tableName, columnName, unique); }

        [Preserve(AllMembers = true)]
        public class ColumnInfo
        {
            [Column("name")]
            public string Name { get; set; }
            public int notnull { get; set; }
            public override string ToString() { return Name; }
        }

        public List<ColumnInfo> GetTableInfo(string tableName)
        {
            return Query<ColumnInfo>("pragma table_info(\"" + tableName + "\")");
        }

        void MigrateTable(TableMapping map, List<ColumnInfo> existingCols)
        {
            var toBeAdded = new List<TableMapping.Column>();
            foreach (var p in map.Columns)
            {
                var found = false;
                foreach (var c in existingCols)
                {
                    found = (string.Compare(p.Name, c.Name, StringComparison.OrdinalIgnoreCase) == 0);
                    if (found) break;
                }
                if (!found) toBeAdded.Add(p);
            }
            foreach (var p in toBeAdded)
                Execute("alter table \"" + map.TableName + "\" add column " + Orm.SqlDecl(p, StoreDateTimeAsTicks));
        }

        protected virtual SQLiteCommand NewCommand() { return new SQLiteCommand(this); }

        public SQLiteCommand CreateCommand(string cmdText, params object[] ps)
        {
            if (!_open)
                throw SQLiteException.New(SQLite3.Result.Error, "Cannot create commands from unopened database");
            var cmd = NewCommand();
            cmd.CommandText = cmdText;
            foreach (var o in ps) cmd.Bind(o);
            return cmd;
        }

        public int Execute(string query, params object[] args)
        {
            var cmd = CreateCommand(query, args);
            if (TimeExecution)
            {
                if (_sw == null) _sw = new Stopwatch();
                _sw.Reset(); _sw.Start();
            }
            var r = cmd.ExecuteNonQuery();
            if (TimeExecution)
            {
                _sw.Stop();
                _elapsedMilliseconds += _sw.ElapsedMilliseconds;
                if (Tracer != null) Tracer(string.Format("Finished in {0} ms ({1:0.0} s total)", _sw.ElapsedMilliseconds, _elapsedMilliseconds / 1000.0));
            }
            return r;
        }

        public T ExecuteScalar<T>(string query, params object[] args)
        {
            var cmd = CreateCommand(query, args);
            if (TimeExecution)
            {
                if (_sw == null) _sw = new Stopwatch();
                _sw.Reset(); _sw.Start();
            }
            var r = cmd.ExecuteScalar<T>();
            if (TimeExecution)
            {
                _sw.Stop();
                _elapsedMilliseconds += _sw.ElapsedMilliseconds;
            }
            return r;
        }

        public List<T> Query<T>(string query, params object[] args) where T : new()
        {
            var cmd = CreateCommand(query, args);
            return cmd.ExecuteQuery<T>();
        }

        public IEnumerable<T> DeferredQuery<T>(string query, params object[] args) where T : new()
        {
            var cmd = CreateCommand(query, args);
            return cmd.ExecuteDeferredQuery<T>();
        }

        public List<object> Query(TableMapping map, string query, params object[] args)
        {
            var cmd = CreateCommand(query, args);
            return cmd.ExecuteQuery<object>(map);
        }

        public IEnumerable<object> DeferredQuery(TableMapping map, string query, params object[] args)
        {
            var cmd = CreateCommand(query, args);
            return cmd.ExecuteDeferredQuery<object>(map);
        }

        public TableQuery<T> Table<T>() where T : new() { return new TableQuery<T>(this); }

        public T Get<T>(object pk) where T : new()
        {
            var map = GetMapping(typeof(T));
            return Query<T>(map.GetByPrimaryKeySql, pk).First();
        }

        public T Get<T>(Expression<Func<T, bool>> predicate) where T : new()
        {
            return Table<T>().Where(predicate).First();
        }

        public T Find<T>(object pk) where T : new()
        {
            var map = GetMapping(typeof(T));
            return Query<T>(map.GetByPrimaryKeySql, pk).FirstOrDefault();
        }

        public T Find<T>(Expression<Func<T, bool>> predicate) where T : new()
        {
            return Table<T>().Where(predicate).FirstOrDefault();
        }

        public T FindWithQuery<T>(string query, params object[] args) where T : new()
        {
            return Query<T>(query, args).FirstOrDefault();
        }

        public bool IsInTransaction { get { return _transactionDepth > 0; } }

        public void BeginTransaction()
        {
            if (_transactionDepth == 0)
            {
                try
                {
                    Execute("begin transaction");
                    _transactionDepth = 1;
                }
                catch (Exception)
                {
                    throw;
                }
            }
            else
            {
                throw new InvalidOperationException("Cannot begin a transaction while already in a transaction.");
            }
        }

        public string SaveTransactionPoint()
        {
            int depth = Interlocked.Increment(ref _transactionDepth) - 1;
            string retVal = "S" + _rand.Next(short.MaxValue) + "D" + depth;
            try
            {
                Execute("savepoint " + retVal);
            }
            catch (Exception)
            {
                Interlocked.Decrement(ref _transactionDepth);
                throw;
            }
            return retVal;
        }

        public void Rollback() { RollbackTo(null, false); }
        public void RollbackTo(string savepoint) { RollbackTo(savepoint, false); }

        void RollbackTo(string savepoint, bool noThrow)
        {
            try
            {
                if (String.IsNullOrEmpty(savepoint))
                {
                    if (Interlocked.Exchange(ref _transactionDepth, 0) > 0)
                        Execute("rollback");
                }
                else
                {
                    DoSavePointExecute(savepoint, "rollback to ");
                }
            }
            catch (SQLiteException)
            {
                if (!noThrow) throw;
            }
        }

        public void Release(string savepoint)
        {
            try
            {
                DoSavePointExecute(savepoint, "release ");
            }
            catch (SQLiteException ex)
            {
                if (ex.Result == SQLite3.Result.Busy)
                {
                    try { Execute("rollback"); } catch { }
                }
                throw;
            }
        }

        void DoSavePointExecute(string savepoint, string cmd)
        {
            int firstLen = savepoint.IndexOf('D');
            if (firstLen >= 2 && savepoint.Length > firstLen + 1)
            {
                int depth;
                if (Int32.TryParse(savepoint.Substring(firstLen + 1), out depth))
                {
                    if (0 <= depth && depth < _transactionDepth)
                    {
                        Thread.VolatileWrite(ref _transactionDepth, depth);
                        Execute(cmd + savepoint);
                        return;
                    }
                }
            }
            throw new ArgumentException("savePoint is not valid, and should be the result of a call to SaveTransactionPoint.", "savePoint");
        }

        public void Commit()
        {
            if (_transactionDepth > 0)
            {
                try
                {
                    Execute("commit");
                    Interlocked.Exchange(ref _transactionDepth, 0);
                }
                catch (Exception)
                {
                    try
                    {
                        Execute("rollback");
                        Interlocked.Exchange(ref _transactionDepth, 0);
                    }
                    catch
                    {
                        Interlocked.Exchange(ref _transactionDepth, 0);
                    }
                    throw;
                }
            }
        }

        public void RunInTransaction(Action action)
        {
            try
            {
                string savePoint = SaveTransactionPoint();
                action();
                Release(savePoint);
            }
            catch (Exception)
            {
                Rollback();
                throw;
            }
        }

        public long Insert(object obj)
        {
            if (obj == null) return 0;
            return Insert(obj, "", Orm.GetType(obj));
        }

        public long InsertOrReplace(object obj)
        {
            if (obj == null) return 0;
            return Insert(obj, "OR REPLACE", Orm.GetType(obj));
        }

        public long Insert(object obj, string extra)
        {
            if (obj == null) return 0;
            return Insert(obj, extra, Orm.GetType(obj));
        }

        public long Insert(object obj, Type objType) { return Insert(obj, "", objType); }
        public long InsertOrReplace(object obj, Type objType) { return Insert(obj, "OR REPLACE", objType); }

        public long Insert(object obj, string extra, Type objType)
        {
            if (obj == null || objType == null) return 0;

            var map = GetMapping(objType);

            if (map.PK != null && map.PK.IsAutoGuid)
            {
                if (map.PK.GetValue(obj).Equals(Guid.Empty))
                    map.PK.SetValue(obj, Guid.NewGuid());
            }

            var replacing = string.Compare(extra, "OR REPLACE", StringComparison.OrdinalIgnoreCase) == 0;
            var cols = replacing ? map.InsertOrReplaceColumns : map.InsertColumns;

            var vals = new object[cols.Length];
            for (var i = 0; i < vals.Length; i++)
                vals[i] = cols[i].GetValue(obj);

            var insertCmd = GetInsertCommand(map, extra);
            long count;
            lock (insertCmd)
            {
                try
                {
                    count = insertCmd.ExecuteNonQuery(vals);
                }
                catch (SQLiteException ex)
                {
                    if (SQLite3.ExtendedErrCode(this.Handle) == SQLite3.ExtendedResult.ConstraintNotNull)
                        throw NotNullConstraintViolationException.New(ex, map, obj);
                    throw;
                }
            }

            if (map.HasAutoIncPK)
            {
                var id = SQLite3.LastInsertRowid(Handle);
                map.SetAutoIncPK(obj, id);
            }

            if (count > 0)
                OnTableChanged(map, NotifyTableChangedAction.Insert);

            return count;
        }

        public long InsertAll(IEnumerable objects, bool runInTransaction = true)
        {
            long c = 0;
            if (runInTransaction)
            {
                RunInTransaction(() => { foreach (var r in objects) c += Insert(r); });
            }
            else
            {
                foreach (var r in objects) c += Insert(r);
            }
            return c;
        }

        readonly Dictionary<Tuple<string, string>, PreparedSqlLiteInsertCommand> _insertCommandMap = new Dictionary<Tuple<string, string>, PreparedSqlLiteInsertCommand>();

        PreparedSqlLiteInsertCommand GetInsertCommand(TableMapping map, string extra)
        {
            PreparedSqlLiteInsertCommand prepCmd;
            var key = Tuple.Create(map.MappedType.FullName, extra);

            lock (_insertCommandMap)
            {
                if (_insertCommandMap.TryGetValue(key, out prepCmd))
                    return prepCmd;
            }

            var replacing = string.Compare(extra, "OR REPLACE", StringComparison.OrdinalIgnoreCase) == 0;
            var cols = replacing ? map.InsertOrReplaceColumns : map.InsertColumns;
            string insertSql;
            if (cols.Length == 0 && map.Columns.Length == 1 && map.Columns[0].IsAutoInc)
            {
                insertSql = string.Format("insert {1} into \"{0}\" default values", map.TableName, extra);
            }
            else
            {
                insertSql = string.Format("insert {3} into \"{0}\"({1}) values ({2})", map.TableName,
                    string.Join(",", (from c in cols select "\"" + c.Name + "\"").ToArray()),
                    string.Join(",", (from c in cols select "?").ToArray()), extra);
            }

            prepCmd = new PreparedSqlLiteInsertCommand(this, insertSql);
            lock (_insertCommandMap)
            {
                PreparedSqlLiteInsertCommand existing;
                if (_insertCommandMap.TryGetValue(key, out existing))
                {
                    prepCmd.Dispose();
                    return existing;
                }
                _insertCommandMap.Add(key, prepCmd);
            }
            return prepCmd;
        }

        public int Update(object obj)
        {
            if (obj == null) return 0;
            return Update(obj, Orm.GetType(obj));
        }

        public int Update(object obj, Type objType)
        {
            int rowsAffected = 0;
            if (obj == null || objType == null) return 0;

            var map = GetMapping(objType);
            var pk = map.PK;
            if (pk == null)
                throw new NotSupportedException("Cannot update " + map.TableName + ": it has no PK");

            var cols = (from p in map.Columns where p != pk select p).ToList();
            var vals = (from c in cols select c.GetValue(obj)).ToList();
            vals.Add(pk.GetValue(obj));

            var q = string.Format("update \"{0}\" set {1} where \"{2}\" = ? ", map.TableName,
                string.Join(",", (from c in cols select "\"" + c.Name + "\" = ? ").ToArray()), pk.Name);

            try
            {
                rowsAffected = Execute(q, vals.ToArray());
            }
            catch (SQLiteException ex)
            {
                if (ex.Result == SQLite3.Result.Constraint && SQLite3.ExtendedErrCode(this.Handle) == SQLite3.ExtendedResult.ConstraintNotNull)
                    throw NotNullConstraintViolationException.New(ex, map, obj);
                throw;
            }

            if (rowsAffected > 0)
                OnTableChanged(map, NotifyTableChangedAction.Update);

            return rowsAffected;
        }

        public int UpdateAll(IEnumerable objects, bool runInTransaction = true)
        {
            var c = 0;
            if (runInTransaction)
            {
                RunInTransaction(() => { foreach (var r in objects) c += Update(r); });
            }
            else
            {
                foreach (var r in objects) c += Update(r);
            }
            return c;
        }

        public int Delete(object objectToDelete)
        {
            var map = GetMapping(Orm.GetType(objectToDelete));
            var pk = map.PK;
            if (pk == null)
                throw new NotSupportedException("Cannot delete " + map.TableName + ": it has no PK");
            var q = string.Format("delete from \"{0}\" where \"{1}\" = ?", map.TableName, pk.Name);
            var count = Execute(q, pk.GetValue(objectToDelete));
            if (count > 0) OnTableChanged(map, NotifyTableChangedAction.Delete);
            return count;
        }

        public int Delete<T>(object primaryKey)
        {
            return Delete(primaryKey, GetMapping(typeof(T)));
        }

        public int Delete(object primaryKey, TableMapping map)
        {
            var pk = map.PK;
            if (pk == null)
                throw new NotSupportedException("Cannot delete " + map.TableName + ": it has no PK");
            var q = string.Format("delete from \"{0}\" where \"{1}\" = ?", map.TableName, pk.Name);
            var count = Execute(q, primaryKey);
            if (count > 0) OnTableChanged(map, NotifyTableChangedAction.Delete);
            return count;
        }

        public int DeleteAll<T>()
        {
            var map = GetMapping(typeof(T));
            var query = string.Format("delete from \"{0}\"", map.TableName);
            var count = Execute(query);
            if (count > 0) OnTableChanged(map, NotifyTableChangedAction.Delete);
            return count;
        }

        public event EventHandler<NotifyTableChangedEventArgs> TableChanged;
        void OnTableChanged(TableMapping table, NotifyTableChangedAction action)
        {
            var ev = TableChanged;
            if (ev != null) ev(this, new NotifyTableChangedEventArgs(table, action));
        }

        ~SQLiteConnection() { Dispose(false); }
        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        public void Close() { Dispose(true); }

        protected virtual void Dispose(bool disposing)
        {
            var useClose2 = LibVersionNumber >= 3007014;
            if (_open && Handle != NullHandle)
            {
                try
                {
                    if (disposing)
                    {
                        lock (_insertCommandMap)
                        {
                            foreach (var sqlInsertCommand in _insertCommandMap.Values)
                                sqlInsertCommand.Dispose();
                            _insertCommandMap.Clear();
                        }
                        var r = useClose2 ? SQLite3.Close2(Handle) : SQLite3.Close(Handle);
                        if (r != SQLite3.Result.OK)
                        {
                            string msg = SQLite3.GetErrmsg(Handle);
                            throw SQLiteException.New(r, msg);
                        }
                    }
                    else
                    {
                        var r = useClose2 ? SQLite3.Close2(Handle) : SQLite3.Close(Handle);
                    }
                }
                finally
                {
                    Handle = NullHandle;
                    _open = false;
                }
            }
        }
    }
    #endregion

    #region TableMapping
    [Preserve(AllMembers = true)]
    public class TableMapping
    {
        public Type MappedType { get; set; }
        public string TableName { get; set; }
        public bool WithoutRowId { get; set; }
        public Column[] Columns { get; set; }
        public Column PK { get; set; }
        public string GetByPrimaryKeySql { get; set; }
        public CreateFlags CreateFlags { get; set; }
        internal bool _autoPk;
        internal Column[] _insertColumns;
        internal Column[] _insertOrReplaceColumns;

        public Column[] InsertColumns
        {
            get { return _insertColumns ?? (_insertColumns = Columns.Where(c => !c.IsAutoInc).ToArray()); }
            set { _insertColumns = value; }
        }
        public Column[] InsertOrReplaceColumns
        {
            get { return _insertOrReplaceColumns ?? (_insertOrReplaceColumns = Columns.ToArray()); }
            set { _insertOrReplaceColumns = value; }
        }

        public bool HasAutoIncPK { get; private set; }

        public TableMapping() { }

        public TableMapping(Type type, CreateFlags createFlags = CreateFlags.None)
        {
            MappedType = type;
            CreateFlags = createFlags;

            var tableAttr = type.GetCustomAttributes(typeof(TableAttribute), true).FirstOrDefault() as TableAttribute;
            TableName = (tableAttr != null && !string.IsNullOrEmpty(tableAttr.Name)) ? tableAttr.Name : MappedType.Name;

            var props = MappedType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.SetProperty);
            var cols = new List<Column>();
            foreach (var p in props)
            {
                bool ignore = p.GetCustomAttributes(typeof(IgnoreAttribute), true).Length > 0;
                if (p.CanWrite && !ignore)
                    cols.Add(new Column(p, createFlags));
            }
            Columns = cols.ToArray();

            foreach (var c in Columns)
            {
                if (c.IsAutoInc && c.IsPK) { _autoPk = true; }
                if (c.IsPK) { PK = c; }
            }

            HasAutoIncPK = _autoPk;

            if (PK != null)
            {
                GetByPrimaryKeySql = string.Format("select * from \"{0}\" where \"{1}\" = ?", TableName, PK.Name);
            }
            else
            {
                GetByPrimaryKeySql = string.Format("select * from \"{0}\" limit 1", TableName);
            }
        }

        public void SetAutoIncPK(object obj, long id)
        {
            if (PK != null && PK.IsAutoInc)
            {
                PK.SetValue(obj, Convert.ChangeType(id, PK.ColumnType));
            }
        }

        public Column FindColumnWithPropertyName(string propertyName)
        {
            var exact = Columns.FirstOrDefault(c => c.PropertyName == propertyName);
            return exact;
        }

        public Column FindColumn(string columnName)
        {
            var exact = Columns.FirstOrDefault(c => c.Name.ToLower() == columnName.ToLower());
            return exact;
        }

        [Preserve(AllMembers = true)]
        public class Column
        {
            PropertyInfo _prop;
            public string Name { get; private set; }
            public string PropertyName { get { return _prop.Name; } }
            public Type ColumnType { get; private set; }
            public string Collation { get; private set; }
            public bool IsAutoInc { get; private set; }
            public bool IsAutoGuid { get; private set; }
            public bool IsPK { get; private set; }
            public IEnumerable<IndexedAttribute> Indices { get; set; }
            public bool IsNullable { get; private set; }
            public int? MaxStringLength { get; private set; }

            public Column(PropertyInfo prop, CreateFlags createFlags = CreateFlags.None)
            {
                _prop = prop;
                var colAttr = prop.GetCustomAttributes(typeof(ColumnAttribute), true).FirstOrDefault() as ColumnAttribute;
                Name = (colAttr != null && !string.IsNullOrEmpty(colAttr.Name)) ? colAttr.Name : prop.Name;

                ColumnType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                Collation = Orm.Collation(prop);

                IsPK = Orm.IsPK(prop) || (((createFlags & CreateFlags.ImplicitPK) == CreateFlags.ImplicitPK) && string.Compare(prop.Name, Orm.ImplicitPkName, StringComparison.OrdinalIgnoreCase) == 0);
                var isAuto = Orm.IsAutoInc(prop) || (IsPK && ((createFlags & CreateFlags.AutoIncPK) == CreateFlags.AutoIncPK));
                IsAutoGuid = isAuto && ColumnType == typeof(Guid);
                IsAutoInc = isAuto && !IsAutoGuid;

                Indices = Orm.GetIndices(prop);
                if (!Indices.Any() && !IsPK && ((createFlags & CreateFlags.ImplicitIndex) == CreateFlags.ImplicitIndex) && Name.EndsWith(Orm.ImplicitIndexSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    Indices = new IndexedAttribute[] { new IndexedAttribute() };
                }
                IsNullable = !(IsPK || Orm.IsMarkedNotNull(prop));
                MaxStringLength = Orm.MaxStringLength(prop);
            }

            public void SetValue(object obj, object val)
            {
                if (val != null && ColumnType != val.GetType())
                {
                    val = ConvertValue(val);
                }
                _prop.SetValue(obj, val, null);
            }

            object ConvertValue(object val)
            {
                if (val == null) return null;
                var t = ColumnType;
                if (t.IsEnum) return Enum.ToObject(t, val);
                if (t == typeof(bool)) return Convert.ToInt64(val) != 0;
                if (t == typeof(TimeSpan)) return new TimeSpan((long)val);
                if (t == typeof(DateTime)) return new DateTime((long)val);
                if (t == typeof(DateTimeOffset)) return new DateTimeOffset((long)val, TimeSpan.Zero);
                if (t == typeof(Guid)) return new Guid((string)val);
                return Convert.ChangeType(val, t);
            }

            public object GetValue(object obj) { return _prop.GetValue(obj, null); }
        }
    }
    #endregion

    #region SQLiteCommand
    [Preserve(AllMembers = true)]
    public partial class SQLiteCommand
    {
        SQLiteConnection _conn;
        private List<Binding> _bindings;

        public string CommandText { get; set; }

        internal SQLiteCommand(SQLiteConnection conn)
        {
            _conn = conn;
            _bindings = new List<Binding>();
            CommandText = "";
        }

        public int ExecuteNonQuery()
        {
            if (_conn.Trace && _conn.Tracer != null) _conn.Tracer("Executing: " + this);

            var stmt = Prepare();
            try
            {
                var r = SQLite3.Step(stmt);
                if (r == SQLite3.Result.Done) { }
                else if (r == SQLite3.Result.Error)
                {
                    string msg = SQLite3.GetErrmsg(_conn.Handle);
                    throw SQLiteException.New(r, msg);
                }
                else if (r == SQLite3.Result.Constraint)
                {
                    if (SQLite3.ExtendedErrCode(_conn.Handle) == SQLite3.ExtendedResult.ConstraintNotNull)
                        throw NotNullConstraintViolationException.New(r, SQLite3.GetErrmsg(_conn.Handle));
                    string msg = SQLite3.GetErrmsg(_conn.Handle);
                    throw SQLiteException.New(r, msg);
                }
                else
                {
                    throw SQLiteException.New(r, SQLite3.GetErrmsg(_conn.Handle));
                }
                return SQLite3.Changes(_conn.Handle);
            }
            finally
            {
                SQLite3.Finalize(stmt);
            }
        }

        public IEnumerable<T> ExecuteDeferredQuery<T>() where T : new()
        {
            return ExecuteDeferredQuery<T>(_conn.GetMapping(typeof(T)));
        }

        public List<T> ExecuteQuery<T>() where T : new()
        {
            return ExecuteDeferredQuery<T>(_conn.GetMapping(typeof(T))).ToList();
        }

        public List<T> ExecuteQuery<T>(TableMapping map)
        {
            return ExecuteDeferredQuery<T>(map).ToList();
        }

        public IEnumerable<T> ExecuteDeferredQuery<T>(TableMapping map)
        {
            if (_conn.Trace && _conn.Tracer != null) _conn.Tracer("Executing Query: " + this);

            var stmt = Prepare();
            try
            {
                var cols = new TableMapping.Column[SQLite3.ColumnCount(stmt)];
                for (int i = 0; i < cols.Length; i++)
                {
                    var name = SQLite3.ColumnName16(stmt, i);
                    cols[i] = map.FindColumn(name);
                }

                while (SQLite3.Step(stmt) == SQLite3.Result.Row)
                {
                    var obj = Activator.CreateInstance(map.MappedType);
                    for (int i = 0; i < cols.Length; i++)
                    {
                        if (cols[i] == null) continue;
                        var colType = SQLite3.ColumnType(stmt, i);
                        var val = ReadCol(stmt, i, colType, cols[i].ColumnType);
                        cols[i].SetValue(obj, val);
                    }
                    yield return (T)obj;
                }
            }
            finally
            {
                SQLite3.Finalize(stmt);
            }
        }

        public T ExecuteScalar<T>()
        {
            if (_conn.Trace && _conn.Tracer != null) _conn.Tracer("Executing Scalar: " + this);

            T val = default(T);
            var stmt = Prepare();
            try
            {
                var r = SQLite3.Step(stmt);
                if (r == SQLite3.Result.Row)
                {
                    var colType = SQLite3.ColumnType(stmt, 0);
                    var clrType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
                    val = (T)ReadCol(stmt, 0, colType, clrType);
                }
                else if (r == SQLite3.Result.Done) { }
                else
                {
                    throw SQLiteException.New(r, SQLite3.GetErrmsg(_conn.Handle));
                }
            }
            finally
            {
                SQLite3.Finalize(stmt);
            }
            return val;
        }

        public void Bind(string name, object val)
        {
            _bindings.Add(new Binding { Name = name, Value = val });
        }

        public void Bind(object val)
        {
            _bindings.Add(new Binding { Name = null, Value = val });
        }

        public override string ToString()
        {
            var parts = new string[1 + _bindings.Count];
            parts[0] = CommandText;
            var i = 1;
            foreach (var b in _bindings)
            {
                parts[i] = string.Format("  {0}: {1}", i, b.Value);
                i++;
            }
            return string.Join(Environment.NewLine, parts);
        }

        Sqlite3Statement Prepare()
        {
            var stmt = SQLite3.Prepare2(_conn.Handle, CommandText);
            BindAll(stmt);
            return stmt;
        }

        void BindAll(Sqlite3Statement stmt)
        {
            int nextIdx = 1;
            foreach (var b in _bindings)
            {
                if (b.Name != null)
                    b.Index = SQLite3.BindParameterIndex(stmt, b.Name);
                else
                {
                    b.Index = nextIdx++;
                }
                BindParameter(stmt, b.Index, b.Value, _conn.StoreDateTimeAsTicks);
            }
        }

        internal static void BindParameter(Sqlite3Statement stmt, int index, object value, bool storeDateTimeAsTicks)
        {
            if (value == null)
            {
                SQLite3.BindNull(stmt, index);
            }
            else
            {
                if (value is Int32) SQLite3.BindInt(stmt, index, (int)value);
                else if (value is String) SQLite3.BindText(stmt, index, (string)value, -1, new IntPtr(-1));
                else if (value is Byte || value is UInt16 || value is SByte || value is Int16) SQLite3.BindInt(stmt, index, Convert.ToInt32(value));
                else if (value is Boolean) SQLite3.BindInt(stmt, index, (bool)value ? 1 : 0);
                else if (value is UInt32 || value is Int64) SQLite3.BindInt64(stmt, index, Convert.ToInt64(value));
                else if (value is Single || value is Double || value is Decimal) SQLite3.BindDouble(stmt, index, Convert.ToDouble(value));
                else if (value is TimeSpan) SQLite3.BindInt64(stmt, index, ((TimeSpan)value).Ticks);
                else if (value is DateTime)
                {
                    if (storeDateTimeAsTicks) SQLite3.BindInt64(stmt, index, ((DateTime)value).Ticks);
                    else SQLite3.BindText(stmt, index, ((DateTime)value).ToString("yyyy-MM-dd HH:mm:ss"), -1, new IntPtr(-1));
                }
                else if (value is DateTimeOffset) SQLite3.BindInt64(stmt, index, ((DateTimeOffset)value).UtcTicks);
                else if (value is byte[]) SQLite3.BindBlob(stmt, index, (byte[])value, ((byte[])value).Length, new IntPtr(-1));
                else if (value is Guid) SQLite3.BindText(stmt, index, ((Guid)value).ToString(), 72, new IntPtr(-1));
                else if (value.GetType().IsEnum) SQLite3.BindInt(stmt, index, Convert.ToInt32(value));
                else
                    throw new NotSupportedException("Cannot store type: " + value.GetType());
            }
        }

        object ReadCol(Sqlite3Statement stmt, int index, SQLite3.ColType type, Type clrType)
        {
            if (type == SQLite3.ColType.Null) return null;

            if (clrType == typeof(String)) return SQLite3.ColumnString(stmt, index);
            if (clrType == typeof(Int32)) return SQLite3.ColumnInt(stmt, index);
            if (clrType == typeof(Boolean)) return SQLite3.ColumnInt(stmt, index) == 1;
            if (clrType == typeof(double)) return SQLite3.ColumnDouble(stmt, index);
            if (clrType == typeof(float)) return (float)SQLite3.ColumnDouble(stmt, index);
            if (clrType == typeof(TimeSpan)) return new TimeSpan(SQLite3.ColumnInt64(stmt, index));
            if (clrType == typeof(DateTime))
            {
                if (_conn.StoreDateTimeAsTicks) return new DateTime(SQLite3.ColumnInt64(stmt, index));
                return DateTime.Parse(SQLite3.ColumnString(stmt, index));
            }
            if (clrType == typeof(DateTimeOffset)) return new DateTimeOffset(SQLite3.ColumnInt64(stmt, index), TimeSpan.Zero);
            if (clrType == typeof(Int64)) return SQLite3.ColumnInt64(stmt, index);
            if (clrType == typeof(UInt32)) return (uint)SQLite3.ColumnInt64(stmt, index);
            if (clrType == typeof(decimal)) return (decimal)SQLite3.ColumnDouble(stmt, index);
            if (clrType == typeof(Byte)) return (byte)SQLite3.ColumnInt(stmt, index);
            if (clrType == typeof(UInt16)) return (ushort)SQLite3.ColumnInt(stmt, index);
            if (clrType == typeof(Int16)) return (short)SQLite3.ColumnInt(stmt, index);
            if (clrType == typeof(SByte)) return (sbyte)SQLite3.ColumnInt(stmt, index);
            if (clrType == typeof(byte[])) return SQLite3.ColumnByteArray(stmt, index);
            if (clrType == typeof(Guid))
            {
                var text = SQLite3.ColumnString(stmt, index);
                return new Guid(text);
            }
            if (clrType.IsEnum) return SQLite3.ColumnInt(stmt, index);
            throw new NotSupportedException("Don't know how to read " + clrType);
        }

        class Binding
        {
            public string Name { get; set; }
            public object Value { get; set; }
            public int Index { get; set; }
        }
    }
    #endregion

    #region PreparedSqlLiteInsertCommand
    [Preserve(AllMembers = true)]
    public class PreparedSqlLiteInsertCommand : IDisposable
    {
        internal bool Initialized;
        protected SQLiteConnection Connection;
        public string CommandText { get; set; }
        protected Sqlite3Statement Statement;
        internal static readonly Sqlite3Statement NullStatement = default(Sqlite3Statement);

        internal PreparedSqlLiteInsertCommand(SQLiteConnection conn, string cmdText)
        {
            Connection = conn;
            CommandText = cmdText;
        }

        public int ExecuteNonQuery(object[] source)
        {
            if (Initialized && Statement == NullStatement)
                throw new ObjectDisposedException("PreparedSqlLiteInsertCommand");

            if (Connection.Trace && Connection.Tracer != null) Connection.Tracer("Executing: " + CommandText);

            if (!Initialized)
            {
                Statement = SQLite3.Prepare2(Connection.Handle, CommandText);
                Initialized = true;
            }

            if (source != null)
            {
                for (int i = 0; i < source.Length; i++)
                    SQLiteCommand.BindParameter(Statement, i + 1, source[i], Connection.StoreDateTimeAsTicks);
            }

            var r = SQLite3.Step(Statement);
            if (r == SQLite3.Result.Done)
            {
                int rowsAffected = SQLite3.Changes(Connection.Handle);
                SQLite3.Reset(Statement);
                return rowsAffected;
            }
            else if (r == SQLite3.Result.Error)
            {
                string msg = SQLite3.GetErrmsg(Connection.Handle);
                SQLite3.Reset(Statement);
                throw SQLiteException.New(r, msg);
            }
            else if (r == SQLite3.Result.Constraint)
            {
                SQLite3.Reset(Statement);
                if (SQLite3.ExtendedErrCode(Connection.Handle) == SQLite3.ExtendedResult.ConstraintNotNull)
                    throw NotNullConstraintViolationException.New(r, SQLite3.GetErrmsg(Connection.Handle));
                throw SQLiteException.New(r, SQLite3.GetErrmsg(Connection.Handle));
            }
            else
            {
                SQLite3.Reset(Statement);
                throw SQLiteException.New(r, SQLite3.GetErrmsg(Connection.Handle));
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        void Dispose(bool disposing)
        {
            var s = Statement;
            Statement = NullStatement;
            Connection = null;
            if (s != NullStatement) SQLite3.Finalize(s);
        }

        ~PreparedSqlLiteInsertCommand() { Dispose(false); }
    }
    #endregion

    #region TableQuery
    public abstract class BaseTableQuery
    {
        protected class Ordering { public string ColumnName { get; set; } public bool Ascending { get; set; } }
    }

    public class TableQuery<T> : BaseTableQuery, IEnumerable<T> where T : new()
    {
        public SQLiteConnection Connection { get; private set; }
        public TableMapping Table { get; private set; }

        Expression _where;
        List<Ordering> _orderBys;
        int? _limit;
        int? _offset;
        BaseTableQuery _joinInner;
        BaseTableQuery _joinOuter;

        public TableQuery(SQLiteConnection conn, TableMapping table)
        {
            Connection = conn;
            Table = table;
        }

        public TableQuery(SQLiteConnection conn)
        {
            Connection = conn;
            Table = Connection.GetMapping(typeof(T));
        }

        TableQuery<U> Clone<U>() where U : new()
        {
            var q = new TableQuery<U>(Connection, Table);
            q._where = _where;
            q._orderBys = _orderBys == null ? null : new List<Ordering>(_orderBys);
            q._limit = _limit;
            q._offset = _offset;
            q._joinInner = _joinInner;
            q._joinOuter = _joinOuter;
            return q;
        }

        public TableQuery<T> Where(Expression<Func<T, bool>> predExpr)
        {
            if (predExpr.NodeType == ExpressionType.Lambda)
            {
                var lambda = (LambdaExpression)predExpr;
                var pred = lambda.Body;
                var q = Clone<T>();
                q.AddWhere(pred);
                return q;
            }
            else
            {
                throw new NotSupportedException("Must be a predicate");
            }
        }

        public TableQuery<T> Take(int n) { var q = Clone<T>(); q._limit = n; return q; }
        public TableQuery<T> Skip(int n) { var q = Clone<T>(); q._offset = n; return q; }

        public TableQuery<T> OrderBy<U>(Expression<Func<T, U>> orderExpr) { return AddOrderBy(orderExpr, true); }
        public TableQuery<T> OrderByDescending<U>(Expression<Func<T, U>> orderExpr) { return AddOrderBy(orderExpr, false); }
        public TableQuery<T> ThenBy<U>(Expression<Func<T, U>> orderExpr) { return AddOrderBy(orderExpr, true); }
        public TableQuery<T> ThenByDescending<U>(Expression<Func<T, U>> orderExpr) { return AddOrderBy(orderExpr, false); }

        TableQuery<T> AddOrderBy<U>(Expression<Func<T, U>> orderExpr, bool asc)
        {
            if (orderExpr.NodeType == ExpressionType.Lambda)
            {
                var lambda = (LambdaExpression)orderExpr;
                MemberExpression mem = null;
                var unary = lambda.Body as UnaryExpression;
                if (unary != null && unary.NodeType == ExpressionType.Convert) mem = unary.Operand as MemberExpression;
                else mem = lambda.Body as MemberExpression;

                if (mem != null && (mem.Expression.NodeType == ExpressionType.Parameter))
                {
                    var q = Clone<T>();
                    if (q._orderBys == null) q._orderBys = new List<Ordering>();
                    q._orderBys.Add(new Ordering { ColumnName = Table.FindColumnWithPropertyName(mem.Member.Name).Name, Ascending = asc });
                    return q;
                }
                else throw new NotSupportedException("Order By does not support: " + orderExpr);
            }
            else throw new NotSupportedException("Must be a predicate");
        }

        private void AddWhere(Expression pred)
        {
            if (_where == null) _where = pred;
            else _where = Expression.AndAlso(_where, pred);
        }

        private SQLiteCommand GenerateCommand(string selectionList)
        {
            var cmdText = "select " + selectionList + " from \"" + Table.TableName + "\"";
            var args = new List<object>();
            if (_where != null)
            {
                var w = CompileExpr(_where, args);
                cmdText += " where " + w.CommandText;
            }
            if ((_orderBys != null) && (_orderBys.Count > 0))
            {
                var t = string.Join(", ", _orderBys.Select(o => "\"" + o.ColumnName + "\"" + (o.Ascending ? "" : " desc")).ToArray());
                cmdText += " order by " + t;
            }
            if (_limit.HasValue) cmdText += " limit " + _limit.Value;
            if (_offset.HasValue)
            {
                if (!_limit.HasValue) cmdText += " limit -1 ";
                cmdText += " offset " + _offset.Value;
            }
            return Connection.CreateCommand(cmdText, args.ToArray());
        }

        class CompileResult { public string CommandText { get; set; } public object Value { get; set; } }

        CompileResult CompileExpr(Expression expr, List<object> queryArgs)
        {
            if (expr is BinaryExpression)
            {
                var bin = (BinaryExpression)expr;
                var leftr = CompileExpr(bin.Left, queryArgs);
                var rightr = CompileExpr(bin.Right, queryArgs);

                string text;
                if (leftr.CommandText == "?" && leftr.Value == null)
                    text = CompileNullBinaryExpression(bin, rightr);
                else if (rightr.CommandText == "?" && rightr.Value == null)
                    text = CompileNullBinaryExpression(bin, leftr);
                else
                    text = "(" + leftr.CommandText + " " + GetSqlName(bin) + " " + rightr.CommandText + ")";
                return new CompileResult { CommandText = text };
            }
            else if (expr.NodeType == ExpressionType.Not)
            {
                var operandExpr = ((UnaryExpression)expr).Operand;
                var opr = CompileExpr(operandExpr, queryArgs);
                object val = opr.Value;
                if (val is bool) val = !((bool)val);
                return new CompileResult { CommandText = "NOT(" + opr.CommandText + ")", Value = val };
            }
            else if (expr.NodeType == ExpressionType.Call)
            {
                var call = (MethodCallExpression)expr;
                var args = new CompileResult[call.Arguments.Count];
                var obj = call.Object != null ? CompileExpr(call.Object, queryArgs) : null;

                for (var i = 0; i < args.Length; i++) args[i] = CompileExpr(call.Arguments[i], queryArgs);

                string sqlCall = "";
                if (call.Method.Name == "Like" && args.Length == 2)
                {
                    sqlCall = "(" + args[0].CommandText + " like " + args[1].CommandText + ")";
                }
                else if (call.Method.Name == "Contains" && args.Length == 2)
                {
                    sqlCall = "(" + args[1].CommandText + " in " + args[0].CommandText + ")";
                }
                else if (call.Method.Name == "Contains" && args.Length == 1)
                {
                    if (call.Object != null && call.Object.Type == typeof(string))
                    {
                        sqlCall = "(" + obj.CommandText + " like ('%' || " + args[0].CommandText + " || '%'))";
                    }
                    else
                    {
                        sqlCall = "(" + args[0].CommandText + " in " + obj.CommandText + ")";
                    }
                }
                else if (call.Method.Name == "StartsWith" && args.Length >= 1)
                {
                    sqlCall = "(" + obj.CommandText + " like (" + args[0].CommandText + " || '%'))";
                }
                else if (call.Method.Name == "EndsWith" && args.Length >= 1)
                {
                    sqlCall = "(" + obj.CommandText + " like ('%' || " + args[0].CommandText + "))";
                }
                else if (call.Method.Name == "Equals" && args.Length == 1)
                {
                    sqlCall = "(" + obj.CommandText + " = (" + args[0].CommandText + "))";
                }
                else if (call.Method.Name == "ToLower")
                {
                    sqlCall = "(lower(" + obj.CommandText + "))";
                }
                else if (call.Method.Name == "ToUpper")
                {
                    sqlCall = "(upper(" + obj.CommandText + "))";
                }
                else if (call.Method.Name == "Replace" && args.Length == 2)
                {
                    sqlCall = "(replace(" + obj.CommandText + "," + args[0].CommandText + "," + args[1].CommandText + "))";
                }
                else
                {
                    sqlCall = call.Method.Name.ToLower() + "(" + string.Join(",", args.Select(a => a.CommandText).ToArray()) + ")";
                }
                return new CompileResult { CommandText = sqlCall };
            }
            else if (expr.NodeType == ExpressionType.Constant)
            {
                var c = (ConstantExpression)expr;
                queryArgs.Add(c.Value);
                return new CompileResult { CommandText = "?", Value = c.Value };
            }
            else if (expr.NodeType == ExpressionType.Convert)
            {
                var u = (UnaryExpression)expr;
                var ty = u.Type;
                var valr = CompileExpr(u.Operand, queryArgs);
                return new CompileResult { CommandText = valr.CommandText, Value = valr.Value != null ? ConvertTo(valr.Value, ty) : null };
            }
            else if (expr.NodeType == ExpressionType.MemberAccess)
            {
                var mem = (MemberExpression)expr;

                if (mem.Expression != null && mem.Expression.NodeType == ExpressionType.Parameter)
                {
                    var columnName = Table.FindColumnWithPropertyName(mem.Member.Name).Name;
                    return new CompileResult { CommandText = "\"" + columnName + "\"" };
                }
                else
                {
                    object val = null;
                    if (mem.Expression != null)
                    {
                        var r = CompileExpr(mem.Expression, queryArgs);
                        if (r.Value == null)
                        {
                            throw new NotSupportedException("Member access failed to compile expression");
                        }
                        if (r.CommandText == "?")
                            queryArgs.RemoveAt(queryArgs.Count - 1);
                        val = r.Value;
                    }

                    if (mem.Member is PropertyInfo)
                    {
                        var m = (PropertyInfo)mem.Member;
                        val = m.GetValue(val, null);
                    }
                    else if (mem.Member is FieldInfo)
                    {
                        var m = (FieldInfo)mem.Member;
                        val = m.GetValue(val);
                    }
                    else
                    {
                        throw new NotSupportedException("MemberExpr: " + mem.Member.GetType());
                    }

                    if (val != null && val is IEnumerable && !(val is string) && !(val is byte[]))
                    {
                        var sb = new StringBuilder("(");
                        var head = "";
                        foreach (var a in (IEnumerable)val)
                        {
                            queryArgs.Add(a);
                            sb.Append(head);
                            sb.Append("?");
                            head = ",";
                        }
                        sb.Append(")");
                        return new CompileResult { CommandText = sb.ToString(), Value = val };
                    }
                    else
                    {
                        queryArgs.Add(val);
                        return new CompileResult { CommandText = "?", Value = val };
                    }
                }
            }
            throw new NotSupportedException("Cannot compile: " + expr.NodeType.ToString());
        }

        static object ConvertTo(object val, Type t)
        {
            if (val == null) return null;
            var nut = Nullable.GetUnderlyingType(t) ?? t;
            if (nut.IsEnum) return Enum.ToObject(nut, val);
            return Convert.ChangeType(val, nut);
        }

        string CompileNullBinaryExpression(BinaryExpression expression, CompileResult parameter)
        {
            if (expression.NodeType == ExpressionType.Equal)
                return "(" + parameter.CommandText + " is ?)";
            else if (expression.NodeType == ExpressionType.NotEqual)
                return "(" + parameter.CommandText + " is not ?)";
            else
                throw new NotSupportedException("Cannot compile Null-BinaryExpression with type " + expression.NodeType);
        }

        string GetSqlName(Expression expr)
        {
            var n = expr.NodeType;
            if (n == ExpressionType.GreaterThan) return ">";
            if (n == ExpressionType.GreaterThanOrEqual) return ">=";
            if (n == ExpressionType.LessThan) return "<";
            if (n == ExpressionType.LessThanOrEqual) return "<=";
            if (n == ExpressionType.And || n == ExpressionType.AndAlso) return "and";
            if (n == ExpressionType.Or || n == ExpressionType.OrElse) return "or";
            if (n == ExpressionType.Equal) return "=";
            if (n == ExpressionType.NotEqual) return "!=";
            if (n == ExpressionType.Multiply) return "*";
            if (n == ExpressionType.Add) return "+";
            if (n == ExpressionType.Subtract) return "-";
            if (n == ExpressionType.Divide) return "/";
            if (n == ExpressionType.Modulo) return "%";
            throw new NotSupportedException("Cannot get SQL for: " + n);
        }

        public int Count()
        {
            return GenerateCommand("count(*)").ExecuteScalar<int>();
        }

        public int Count(Expression<Func<T, bool>> predExpr)
        {
            return Where(predExpr).Count();
        }

        public IEnumerator<T> GetEnumerator()
        {
            return GenerateCommand("*").ExecuteDeferredQuery<T>(Table).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }

        public List<T> ToList() { return GenerateCommand("*").ExecuteQuery<T>(); }
        public T[] ToArray() { return GenerateCommand("*").ExecuteQuery<T>().ToArray(); }
        public T First() { return Take(1).ToList().First(); }
        public T FirstOrDefault() { return Take(1).ToList().FirstOrDefault(); }
        public T First(Expression<Func<T, bool>> predExpr) { return Where(predExpr).First(); }
        public T FirstOrDefault(Expression<Func<T, bool>> predExpr) { return Where(predExpr).FirstOrDefault(); }

        public int Delete() { return Delete(null); }
        public int Delete(Expression<Func<T, bool>> predExpr)
        {
            if (_where == null && predExpr == null)
                throw new InvalidOperationException("No condition specified");

            var pred = _where;
            if (predExpr != null && predExpr.NodeType == ExpressionType.Lambda)
            {
                var lambda = (LambdaExpression)predExpr;
                pred = pred != null ? Expression.AndAlso(pred, lambda.Body) : lambda.Body;
            }

            var args = new List<object>();
            var cmdText = "delete from \"" + Table.TableName + "\"";
            var w = CompileExpr(pred, args);
            cmdText += " where " + w.CommandText;
            var command = Connection.CreateCommand(cmdText, args.ToArray());
            return command.ExecuteNonQuery();
        }
    }
    #endregion

    #region Orm
    public static class Orm
    {
        public const string ImplicitPkName = "Id";
        public const string ImplicitIndexSuffix = "Id";
        public const int DefaultMaxStringLength = 140;

        public static Type GetType(object obj) { return obj.GetType(); }

        public static string SqlDecl(TableMapping.Column p, bool storeDateTimeAsTicks)
        {
            string decl = "\"" + p.Name + "\" " + SqlType(p, storeDateTimeAsTicks);
            if (p.IsPK) decl += " primary key";
            if (p.IsAutoInc) decl += " autoincrement";
            if (!p.IsNullable) decl += " not null";
            if (!string.IsNullOrEmpty(p.Collation)) decl += " collate " + p.Collation;
            return decl;
        }

        public static string SqlType(TableMapping.Column p, bool storeDateTimeAsTicks)
        {
            var clrType = p.ColumnType;

            if (clrType == typeof(Boolean) || clrType == typeof(Byte) || clrType == typeof(UInt16) ||
                clrType == typeof(SByte) || clrType == typeof(Int16) || clrType == typeof(Int32) ||
                clrType == typeof(UInt32) || clrType == typeof(Int64))
                return "integer";
            if (clrType == typeof(Single) || clrType == typeof(Double) || clrType == typeof(Decimal))
                return "float";
            if (clrType == typeof(String) || clrType == typeof(Guid))
            {
                int? len = p.MaxStringLength;
                if (len.HasValue) return "varchar(" + len.Value + ")";
                return "text";
            }
            if (clrType == typeof(TimeSpan)) return "bigint";
            if (clrType == typeof(DateTime)) return storeDateTimeAsTicks ? "bigint" : "varchar";
            if (clrType == typeof(DateTimeOffset)) return "bigint";
            if (clrType == typeof(byte[])) return "blob";
            if (clrType.IsEnum) return "integer";
            throw new NotSupportedException("Don't know about " + clrType);
        }

        public static bool IsPK(MemberInfo p) { return p.GetCustomAttributes(typeof(PrimaryKeyAttribute), true).Any(); }
        public static string Collation(MemberInfo p) { var a = p.GetCustomAttributes(typeof(CollationAttribute), true).FirstOrDefault() as CollationAttribute; return a != null ? a.Value : string.Empty; }
        public static bool IsAutoInc(MemberInfo p) { return p.GetCustomAttributes(typeof(AutoIncrementAttribute), true).Any(); }
        public static IEnumerable<IndexedAttribute> GetIndices(MemberInfo p) { return p.GetCustomAttributes(typeof(IndexedAttribute), true).Cast<IndexedAttribute>(); }
        public static int? MaxStringLength(PropertyInfo p) { var a = p.GetCustomAttributes(typeof(MaxLengthAttribute), true).FirstOrDefault() as MaxLengthAttribute; return a != null ? (int?)a.Value : null; }
        public static bool IsMarkedNotNull(MemberInfo p) { return p.GetCustomAttributes(typeof(NotNullAttribute), true).Any(); }
    }
    #endregion

    #region SQLite3 P/Invoke
    public static class SQLite3
    {
        public enum Result : int
        {
            OK = 0, Error = 1, Internal = 2, Perm = 3, Abort = 4,
            Busy = 5, Locked = 6, NoMem = 7, ReadOnly = 8, Interrupt = 9,
            IOError = 10, Corrupt = 11, NotFound = 12, Full = 13,
            CannotOpen = 14, LockErr = 15, Empty = 16, SchemaChngd = 17,
            TooBig = 18, Constraint = 19, Mismatch = 20, Misuse = 21,
            NotImplementedLFS = 22, AccessDenied = 23, Format = 24,
            Range = 25, NonDBFile = 26, Notice = 27, Warning = 28,
            Row = 100, Done = 101
        }

        public enum ExtendedResult : int
        {
            IOErrorRead = (Result.IOError | (1 << 8)),
            IOErrorShortRead = (Result.IOError | (2 << 8)),
            IOErrorWrite = (Result.IOError | (3 << 8)),
            IOErrorFsync = (Result.IOError | (4 << 8)),
            IOErrorDirFSync = (Result.IOError | (5 << 8)),
            IOErrorTruncate = (Result.IOError | (6 << 8)),
            IOErrorFStat = (Result.IOError | (7 << 8)),
            IOErrorUnlock = (Result.IOError | (8 << 8)),
            IOErrorRdlock = (Result.IOError | (9 << 8)),
            IOErrorDelete = (Result.IOError | (10 << 8)),
            IOErrorBlocked = (Result.IOError | (11 << 8)),
            IOErrorNoMem = (Result.IOError | (12 << 8)),
            IOErrorAccess = (Result.IOError | (13 << 8)),
            IOErrorCheckReservedLock = (Result.IOError | (14 << 8)),
            IOErrorLock = (Result.IOError | (15 << 8)),
            IOErrorClose = (Result.IOError | (16 << 8)),
            IOErrorDirClose = (Result.IOError | (17 << 8)),
            IOErrorSHMOpen = (Result.IOError | (18 << 8)),
            IOErrorSHMSize = (Result.IOError | (19 << 8)),
            IOErrorSHMLock = (Result.IOError | (20 << 8)),
            IOErrorSHMMap = (Result.IOError | (21 << 8)),
            IOErrorSeek = (Result.IOError | (22 << 8)),
            IOErrorDeleteNoEnt = (Result.IOError | (23 << 8)),
            IOErrorMMap = (Result.IOError | (24 << 8)),
            LockedSharedcache = (Result.Locked | (1 << 8)),
            BusyRecovery = (Result.Busy | (1 << 8)),
            CannottOpenNoTempDir = (Result.CannotOpen | (1 << 8)),
            CannotOpenIsDir = (Result.CannotOpen | (2 << 8)),
            CannotOpenFullPath = (Result.CannotOpen | (3 << 8)),
            CorruptVTab = (Result.Corrupt | (1 << 8)),
            ReadonlyRecovery = (Result.ReadOnly | (1 << 8)),
            ReadonlyCannotLock = (Result.ReadOnly | (2 << 8)),
            ReadonlyRollback = (Result.ReadOnly | (3 << 8)),
            AbortRollback = (Result.Abort | (2 << 8)),
            ConstraintCheck = (Result.Constraint | (1 << 8)),
            ConstraintCommitHook = (Result.Constraint | (2 << 8)),
            ConstraintForeignKey = (Result.Constraint | (3 << 8)),
            ConstraintFunction = (Result.Constraint | (4 << 8)),
            ConstraintNotNull = (Result.Constraint | (5 << 8)),
            ConstraintPrimaryKey = (Result.Constraint | (6 << 8)),
            ConstraintTrigger = (Result.Constraint | (7 << 8)),
            ConstraintUnique = (Result.Constraint | (8 << 8)),
            ConstraintVTab = (Result.Constraint | (9 << 8)),
            NoticeRecoverWAL = (Result.Notice | (1 << 8)),
            NoticeRecoverRollback = (Result.Notice | (2 << 8))
        }

        public enum ConfigOption : int { SingleThread = 1, MultiThread = 2, Serialized = 3 }
        public enum ColType : int { Integer = 1, Float = 2, Text = 3, Blob = 4, Null = 5 }

        const string LibraryPath = "sqlite3";

        [DllImport(LibraryPath, EntryPoint = "sqlite3_threadsafe", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Threadsafe();

        [DllImport(LibraryPath, EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
        public static extern Result Open([MarshalAs(UnmanagedType.LPStr)] string filename, out IntPtr db, int flags, IntPtr zvfs);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
        public static extern Result Open(byte[] filename, out IntPtr db, int flags, IntPtr zvfs);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_enable_load_extension", CallingConvention = CallingConvention.Cdecl)]
        public static extern Result EnableLoadExtension(IntPtr db, int onoff);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
        public static extern Result Close(IntPtr db);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_close_v2", CallingConvention = CallingConvention.Cdecl)]
        public static extern Result Close2(IntPtr db);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_initialize", CallingConvention = CallingConvention.Cdecl)]
        public static extern Result Initialize();

        [DllImport(LibraryPath, EntryPoint = "sqlite3_shutdown", CallingConvention = CallingConvention.Cdecl)]
        public static extern Result Shutdown();

        [DllImport(LibraryPath, EntryPoint = "sqlite3_config", CallingConvention = CallingConvention.Cdecl)]
        public static extern Result Config(ConfigOption option);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_busy_timeout", CallingConvention = CallingConvention.Cdecl)]
        public static extern Result BusyTimeout(IntPtr db, int milliseconds);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_changes", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Changes(IntPtr db);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
        public static extern Result Prepare2(IntPtr db, [MarshalAs(UnmanagedType.LPStr)] string sql, int numBytes, out IntPtr stmt, IntPtr pzTail);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
        public static extern Result Prepare2(IntPtr db, byte[] queryBytes, int numBytes, out IntPtr stmt, IntPtr pzTail);

        public static IntPtr Prepare2(IntPtr db, string query)
        {
            IntPtr stmt;
            byte[] queryBytes = UTF8Encoding.UTF8.GetBytes(query);
            var r = Prepare2(db, queryBytes, queryBytes.Length, out stmt, IntPtr.Zero);
            if (r != Result.OK)
                throw SQLiteException.New(r, GetErrmsg(db));
            return stmt;
        }

        [DllImport(LibraryPath, EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
        public static extern Result Step(IntPtr stmt);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_reset", CallingConvention = CallingConvention.Cdecl)]
        public static extern Result Reset(IntPtr stmt);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
        public static extern Result Finalize(IntPtr stmt);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_last_insert_rowid", CallingConvention = CallingConvention.Cdecl)]
        public static extern long LastInsertRowid(IntPtr db);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_errmsg16", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr Errmsg(IntPtr db);

        public static string GetErrmsg(IntPtr db) { return Marshal.PtrToStringUni(Errmsg(db)); }

        [DllImport(LibraryPath, EntryPoint = "sqlite3_bind_parameter_index", CallingConvention = CallingConvention.Cdecl)]
        public static extern int BindParameterIndex(IntPtr stmt, [MarshalAs(UnmanagedType.LPStr)] string name);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_bind_null", CallingConvention = CallingConvention.Cdecl)]
        public static extern int BindNull(IntPtr stmt, int index);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_bind_int", CallingConvention = CallingConvention.Cdecl)]
        public static extern int BindInt(IntPtr stmt, int index, int val);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_bind_int64", CallingConvention = CallingConvention.Cdecl)]
        public static extern int BindInt64(IntPtr stmt, int index, long val);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_bind_double", CallingConvention = CallingConvention.Cdecl)]
        public static extern int BindDouble(IntPtr stmt, int index, double val);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_bind_text16", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        public static extern int BindText(IntPtr stmt, int index, [MarshalAs(UnmanagedType.LPWStr)] string val, int n, IntPtr free);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_bind_blob", CallingConvention = CallingConvention.Cdecl)]
        public static extern int BindBlob(IntPtr stmt, int index, byte[] val, int n, IntPtr free);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_column_count", CallingConvention = CallingConvention.Cdecl)]
        public static extern int ColumnCount(IntPtr stmt);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_column_name16", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr ColumnName16Internal(IntPtr stmt, int index);
        public static string ColumnName16(IntPtr stmt, int index) { return Marshal.PtrToStringUni(ColumnName16Internal(stmt, index)); }

        [DllImport(LibraryPath, EntryPoint = "sqlite3_column_type", CallingConvention = CallingConvention.Cdecl)]
        public static extern ColType ColumnType(IntPtr stmt, int index);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_column_int", CallingConvention = CallingConvention.Cdecl)]
        public static extern int ColumnInt(IntPtr stmt, int index);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)]
        public static extern long ColumnInt64(IntPtr stmt, int index);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_column_double", CallingConvention = CallingConvention.Cdecl)]
        public static extern double ColumnDouble(IntPtr stmt, int index);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_column_text16", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ColumnText16(IntPtr stmt, int index);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_column_blob", CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ColumnBlob(IntPtr stmt, int index);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.Cdecl)]
        public static extern int ColumnBytes(IntPtr stmt, int index);

        public static string ColumnString(IntPtr stmt, int index) { return Marshal.PtrToStringUni(ColumnText16(stmt, index)); }

        public static byte[] ColumnByteArray(IntPtr stmt, int index)
        {
            int length = ColumnBytes(stmt, index);
            var result = new byte[length];
            if (length > 0)
                Marshal.Copy(ColumnBlob(stmt, index), result, 0, length);
            return result;
        }

        [DllImport(LibraryPath, EntryPoint = "sqlite3_extended_errcode", CallingConvention = CallingConvention.Cdecl)]
        public static extern ExtendedResult ExtendedErrCode(IntPtr db);

        [DllImport(LibraryPath, EntryPoint = "sqlite3_libversion_number", CallingConvention = CallingConvention.Cdecl)]
        public static extern int LibVersionNumber();
    }
    #endregion
}
