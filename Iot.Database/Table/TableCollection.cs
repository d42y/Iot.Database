﻿using Iot.Database;
using Iot.Database.Attributes;
using Iot.Database.Base;
using Iot.Database.Blockchain;
using Iot.Database.Helper;
using Iot.Database.Table;
using Iot.Database.TimeSeries;
using System.Collections.Concurrent;
using System.Data;
using System.Linq.Expressions;
using static Iot.Database.IotValue.Units;

namespace Iot.Database
{
    internal class TableCollection<T> : BaseDatabase, ITableCollection<T> where T : class
    {

        #region Thread Safe
        private readonly object _lockObject = new object();

        #endregion

        #region Global Variables
        private readonly string _collectionName = "Collection";
        private ILiteCollection<T> _collection;
        private bool _processingQueue = false;
        private ConcurrentQueue<T> _updateEntityQueue = new ConcurrentQueue<T>();
        private IotDatabase _iotDb;
        private List<ColumnInfo> _blockChainAttributes = new();
        private List<ColumnInfo> _timeSeriesAttributes = new();
        private ConcurrentDictionary<string, BlockCollection> _blocks = new();
        private ConcurrentDictionary<string, TsCollection> _ts = new();

        private readonly ConcurrentQueue<T> _attributeQueue = new ConcurrentQueue<T>();
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private readonly ConcurrentQueue<(T Entity, TaskCompletionSource<BsonValue> Tcs)> _insertAsyncQueue = new ConcurrentQueue<(T, TaskCompletionSource<BsonValue>)>();
        public TableInfo TableInfo { get; private set; }
        #endregion

        #region Constructors
        /// <summary>
        /// Create new table
        /// </summary>
        /// <param name="iotDb"></param>
        /// <param name="dbPath"></param>
        /// <param name="tblName"></param>
        public TableCollection(IotDatabase iotDb, string tblName = "") : base(iotDb.TableDbPath, string.IsNullOrEmpty(tblName) ? typeof(T).Name : tblName, iotDb._password)
        {
            PreCheck();
            SetGlobalIgnore<T>();
            _blockChainAttributes = ReflectionHelper.GetTypeColumnsWithAttribute<BlockChainValueAttribute>(typeof(T)).ToList();
            _timeSeriesAttributes = ReflectionHelper.GetTypeColumnsWithAttribute<TimeSeriesAttribute>(typeof(T)).ToList();
            _iotDb = iotDb;
            TableInfo = new TableInfo(typeof(T));
            foreach (var ft in TableInfo.ForeignTables)
            {
                if (ft.Name.EndsWith("Table"))
                {
                    var name = ft.Name.Substring(0, ft.Name.Length - "Table".Length);
                    var idName = $"{name}Id";
                    if (!TableInfo.ForeignKeys.Any(x => x.Name == idName))
                    {
                        throw new InvalidConstraintException($"Table doesn't have Foreign Key for referenced Foreign Table name {ft.Name}.");
                    }
                }
            }
            iotDb._tableInfos[TableInfo.Name] = TableInfo;
            foreach (var table in iotDb._tableInfos)
            {
                foreach (var fk in table.Value.ForeignKeys)
                {
                    if (fk.Name.EndsWith("Id"))
                    {
                        var name = fk.Name.Substring(0, fk.Name.Length - "Id".Length);
                        var tf = iotDb._tableInfos.FirstOrDefault(x => x.Key == name).Value;
                        if (tf == null) continue;
                        if (!tf.ChildTables.Any(x => x.Name == table.Key))
                        {
                            tf.ChildTables.Add(table.Value);
                        }

                    }
                }
            }

            _collection = Database.GetCollection<T>(_collectionName);

            Task.Run(() => ProcessAttributeQueue(_cts.Token));

        }




        private void PreCheck()
        {
            if (!HasIdProperty(typeof(T)))
            {
                throw new KeyNotFoundException("Table missing Id property with int, long, or Guid data type.");
            }
        }


        #endregion

        #region A
        /// <summary>
        /// Get collection auto id type
        /// </summary>
        public BsonAutoId AutoId
        {
            get
            {

                return Database.GetCollection<T>(_collectionName).AutoId;

            }
        }
        #endregion

        #region C
        /// <summary>
        /// Get document count using property on _collection.
        /// </summary>
        public long Count()
        {

            //lock(SyncRoot)
            //lock(_lockObject)
            //{
            return Database.GetCollection<T>(_collectionName).LongCount();
            //}

        }
        /// <summary>
        /// Count documents matching a query. This method does not deserialize any document. Needs indexes on query expression
        /// </summary>
        public long Count(BsonExpression predicate)
        {

            //lock(SyncRoot)
            //{
            return _collection.LongCount(predicate);
            //}

        }
        /// <summary>
        /// Count documents matching a query. This method does not deserialize any document. Needs indexes on query expression
        /// </summary>
        public long Count(string predicate, BsonDocument parameters)
        {

            //lock(SyncRoot)
            //{
            return _collection.LongCount(predicate, parameters);
            //}

        }
        /// <summary>
        /// Count documents matching a query. This method does not deserialize any document. Needs indexes on query expression
        /// </summary>
        public long Count(string predicate, params BsonValue[] args)
        {

            //lock(SyncRoot)
            //{
            return _collection.LongCount(predicate, args);
            //}

        }
        /// <summary>
        /// Count documents matching a query. This method does not deserialize any documents. Needs indexes on query expression
        /// </summary>
        public long Count(Expression<Func<T, bool>> predicate)
        {

            //lock(SyncRoot)
            //{
            return _collection.LongCount(predicate);
            //}

        }
        /// <summary>
        /// Count documents matching a query. This method does not deserialize any documents. Needs indexes on query expression
        /// </summary>
        public long Count(Query query)
        {

            //lock(SyncRoot)
            //{
            return _collection.LongCount(query);
            //}

        }

        #endregion

        #region BlockChain
        public List<ColumnInfo> BlockChainAttributes
        {
            get
            {
                return _blockChainAttributes;
            }
        }

        public ColumnInfo TagColumnAsBlockChain(string name, string description = "")
        {
            var prop = ReflectionHelper.GetProperty(typeof(T), name);
            if (prop == null) throw new KeyNotFoundException(name);
            BlockChainValueAttribute attribute = new BlockChainValueAttribute(description);
            ColumnInfo bi = new(prop, attribute);
            _blockChainAttributes.Add(bi);
            return bi;
        }

        public IBlockCollection? Blocks(string name)
        {
            if (!_blockChainAttributes.Any(x => x.Name == name))
            {
                return null; // throw new EntryPointNotFoundException($"{typeof(T).Name} does not have BlockChainValueAttribute with name {name}.");
            }

            if (!_blocks.ContainsKey(name))
            {
                var blockPath = Path.Combine(DbPath, "BlockChain");
                Helper.MachineInfo.CreateDirectory(blockPath);
                _blocks[name] = new BlockCollection(blockPath, $"{DbName}_{name}");
                _blocks[name].ExceptionOccurred += OnBlockExceptionOccurred;
            }

            return _blocks[name];
        }

        private void WriteToBlocks(T entity)
        {
            if (entity is IotValue val)
            {
                if (val.IsBlockChain && val.Value != null)
                {
                    _blocks[val.Name].Insert(new BsonValue(val.Value));
                }
            }
            else
            {
                foreach (var bi in _blockChainAttributes)
                {
                    var value = bi.PropertyInfo.GetValue(entity);
                    if (value == null) continue;
                    _blocks[bi.Name].Insert(new BsonValue(value));
                }
            }

        }

        #endregion

        #region D

        /// <summary>
        /// Drop index and release slot for another index
        /// </summary>
        public bool DropIndex(string name)
        {
            return _collection.DropIndex(name);

        }

        /// <summary>
        /// Delete a single document on collection based on _id index. Returns true if document was deleted
        /// </summary>
        public bool Delete(BsonValue id)
        {

            lock (SyncRoot)
            {
                if (_iotDb._tableInfos[typeof(T).Name].ChildTables.Count > 0)
                {
                    //no child has multiple foreign keys. Now try to delete
                    foreach (var child in _iotDb._tableInfos[typeof(T).Name].ChildTables)
                    {
                        //get child table
                        var table = _iotDb.GetTable(child.Name);
                        if (table == null) continue;

                        //get child foreign key
                        var fk = child.ForeignKeys.FirstOrDefault(x => x.Name == $"{typeof(T).Name}Id");
                        if (fk == null) continue;

                        //get records in child table referenced to parent
                        var childRecords = table.Find($"{fk.Name}", id, Base.Comparison.Equals);
                        if (childRecords.Count > 0) //check Restrictive constraint if have record
                        {
                            foreach (var column in child.ForeignKeys)
                            {
                                if (column.Attribute is TableForeignKeyAttribute tfa)
                                {
                                    if (tfa.Constraint == TableConstraint.Restrictive)
                                    {
                                        throw new InvalidOperationException($"Child table {child.Name} with Restrictive constraint foreign key {column.Name} prevent deletion. Please delete child record first before deleting from table {Name}.");
                                    }
                                }
                            }
                        }

                        //no restrictive constraints
                        //atemp to delete child records
                        foreach (var item in childRecords)
                        {
                            BsonValue? childId = null;
                            try
                            {
                                var val = child?.Id?.PropertyInfo.GetValue(item, null);
                                if (val != null) childId = new BsonValue(val);
                                else throw new Exception();
                            }
                            catch
                            {
                                if (child != null && child.Id != null && item.ContainsKey(child.Id.Name))
                                {
                                    childId = item[child.Id.Name];

                                }
                                else continue;
                            }

                            if (childId.IsNull) continue;
                            if (fk.Attribute is TableForeignKeyAttribute tfa)
                            {
                                if (tfa.Constraint == TableConstraint.Cascading)
                                {
                                    var good = table.Delete(childId);
                                    if (!good) throw new InvalidConstraintException($"Failed cascade delete record in child table {table.Name}.");
                                }
                                else if (tfa.Constraint == TableConstraint.SetNull)
                                {
                                    fk.PropertyInfo.SetValue(item, null, null);
                                }
                                else if (tfa.Constraint == TableConstraint.SetDefault)
                                {
                                    fk.PropertyInfo.SetValue(item, GetDefaultValue(fk.PropertyInfo.PropertyType), null);
                                }
                            }
                        }
                    }
                }
                var result = _collection.Delete(id);
                return result;
            }

        }

        private BsonValue GetDefaultValue(Type type)
        {
            if (!type.IsValueType || Nullable.GetUnderlyingType(type) != null)
            {
                return null;
            }
            return new(Activator.CreateInstance(type));
        }


        /// <summary>
        /// Delete all documents inside _collection. Returns how many documents was deleted. Run inside current transaction
        /// </summary>
        public int DeleteAll()
        {

            lock (SyncRoot)
            {
                var col = Collection;
                if (_iotDb._tableInfos[typeof(T).Name].ChildTables.Count > 0)
                {
                    //no child has multiple foreign keys. Now try to delete
                    foreach (var child in _iotDb._tableInfos[typeof(T).Name].ChildTables)
                    {
                        var table = _iotDb.GetTable(child.Name);
                        if (table == null) continue;
                        var fk = child.ForeignKeys.FirstOrDefault(x => x.Name == $"{typeof(T).Name}Id");
                        if (fk == null) continue;

                        if (table.Count() > 0)
                        {
                            foreach (var key in child.ForeignKeys)
                            {
                                if (key.Attribute is TableForeignKeyAttribute tfa)
                                {
                                    if (tfa.Constraint == TableConstraint.Restrictive)
                                    {
                                        throw new InvalidOperationException($"Child table {child.Name} with Restrictive constraint foreign key {key.Name} prevent deletion. Please delete child record first before deleting from table {Name}.");
                                    }
                                }
                            }

                            if (fk.Attribute is TableForeignKeyAttribute key2)
                            {
                                if (key2.Constraint == TableConstraint.Cascading)
                                {
                                    var deletedItemCount = table.DeleteAll();
                                    if (deletedItemCount < 1) throw new InvalidConstraintException($"Failed cascade delete record in child table {table.Name}.");
                                }
                                else if (key2.Constraint == TableConstraint.SetNull)
                                {
                                    table.SetAll(fk.Name, null);
                                }
                                else if (key2.Constraint == TableConstraint.SetDefault)
                                {
                                    table.SetAll(fk.Name, GetDefaultValue(fk.PropertyInfo.PropertyType));
                                }
                            }
                        }
                    }
                }
                return col.DeleteAll();
            }

        }

        /// <summary>
        /// Delete all documents based on predicate expression. Returns how many documents was deleted
        /// </summary>
        public int DeleteMany(BsonExpression predicate)
        {

            lock (SyncRoot)
            {
                return _collection.DeleteMany(predicate);
            }

        }
        /// <summary>
        /// Delete all documents based on predicate expression. Returns how many documents was deleted
        /// </summary>
        public int DeleteMany(string predicate, BsonDocument parameters)
        {

            lock (SyncRoot)
            {
                return _collection.DeleteMany(predicate, parameters);
            }

        }
        /// <summary>
        /// Delete all documents based on predicate expression. Returns how many documents was deleted
        /// </summary>
        public int DeleteMany(string predicate, params BsonValue[] args)
        {

            lock (SyncRoot)
            {
                return _collection.DeleteMany(predicate, args);
            }

        }
        /// <summary>
        /// Delete all documents based on predicate expression. Returns how many documents was deleted
        /// </summary>
        public int DeleteMany(Expression<Func<T, bool>> predicate)
        {

            lock (SyncRoot)
            {
                return _collection.DeleteMany(predicate);
            }

        }
        #endregion

        #region E

        /// <summary>
        /// Getting entity mapper from current _collection. Returns null if collection are BsonDocument type
        /// </summary>
        public EntityMapper EntityMapper
        {
            get
            {
                //lock(SyncRoot)
                lock (SyncRoot)
                {
                    return _collection.EntityMapper;
                }
            }
        }

        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already. Returns true if index was created or false if already exits
        /// </summary>
        /// <param name="name">Index name - unique name for this collection</param>
        /// <param name="expression">Create a custom expression function to be indexed</param>
        /// <param name="unique">If is a unique index</param>
        public bool EnsureIndex(string name, BsonExpression expression, bool unique = false)
        {

            //lock(SyncRoot)
            lock (SyncRoot)
            {
                return _collection.EnsureIndex(name, expression, unique);
            }

        }
        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already. Returns true if index was created or false if already exits
        /// </summary>
        /// <param name="expression">Document field/expression</param>
        /// <param name="unique">If is a unique index</param>
        public bool EnsureIndex(BsonExpression expression, bool unique = false)
        {

            //lock(SyncRoot)
            //lock(SyncRoot)
            //{
            return _collection.EnsureIndex(expression, unique);
            //}

        }

        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already.
        /// </summary>
        /// <param name="name">Index name - unique name for this collection</param>
        /// <param name="keySelector">LinqExpression to be converted into BsonExpression to be indexed</param>
        /// <param name="unique">Create a unique keys index?</param>
        public bool EnsureIndex<K>(string name, Expression<Func<T, K>> keySelector, bool unique = false)
        {

            //lock(SyncRoot)
            //lock(SyncRoot)
            //{
            return _collection.EnsureIndex(name, keySelector, unique);
            //}

        }

        /// <summary>
        /// Create a new permanent index in all documents inside this collections if index not exists already.
        /// </summary>
        /// <param name="keySelector">LinqExpression to be converted into BsonExpression to be indexed</param>
        /// <param name="unique">Create a unique keys index?</param>
        public bool EnsureIndex<K>(Expression<Func<T, K>> keySelector, bool unique = false)
        {

            //lock(SyncRoot)
            //{
            return _collection.EnsureIndex(keySelector, unique);
            //}

        }

        /// <summary>
        /// Returns true if query returns any document. This method does not deserialize any document. Needs indexes on query expression
        /// </summary>
        public bool Exists(BsonExpression predicate)
        {

            //lock(SyncRoot)
            //{
            return _collection.Exists(predicate);
            //}

        }
        /// <summary>
        /// Returns true if query returns any document. This method does not deserialize any document. Needs indexes on query expression
        /// </summary>
        public bool Exists(string predicate, BsonDocument parameters)
        {

            //lock(SyncRoot)
            //{
            return _collection.Exists(predicate, parameters);
            //}

        }
        /// <summary>
        /// Returns true if query returns any document. This method does not deserialize any document. Needs indexes on query expression
        /// </summary>
        public bool Exists(string predicate, params BsonValue[] args)
        {

            //lock(SyncRoot)
            //{
            return _collection.Exists(predicate, args);
            //}

        }
        /// <summary>
        /// Returns true if query returns any document. This method does not deserialize any document. Needs indexes on query expression
        /// </summary>
        public bool Exists(Expression<Func<T, bool>> predicate)
        {

            //lock(SyncRoot)
            //{
            return _collection.Exists(predicate);
            //}

        }
        /// <summary>
        /// Returns true if query returns any document. This method does not deserialize any document. Needs indexes on query expression
        /// </summary>
        public bool Exists(Query query)
        {

            //lock(SyncRoot)
            //{
            return _collection.Exists(query);
            //}

        }

        #endregion

        #region F
        /// <summary>
        /// Performs a search within a collection for documents where the specified column matches a given value, according to a specified comparison type.
        /// The method can handle different types of string comparisons, such as Equals, StartsWith, EndsWith, and Contains. If the column name provided is "Id", it is internally treated as "_id" to match LiteDB's document ID field naming convention. For each document found, if it contains an "_id" field, this field is renamed to "Id". The comparison type defaults to Equals if not specified.
        /// </summary>
        /// <param name="columnName">The name of the column to search in. Automatically handles "Id" as "_id".</param>
        /// <param name="value">The value to search for in the specified column.</param>
        /// <param name="comparisonType">The type of comparison to perform, with the default being Equals. Other options include StartsWith, EndsWith, and Contains.</param>
        /// <returns>A list of BsonDocument objects that match the search criteria, with any "_id" field renamed to "Id".</returns>

        public List<BsonDocument> Find(string columnName, string value, Comparison comparisonType = Comparison.Equals)
        {

            var col = Database.GetCollection<BsonDocument>(_collectionName);

            IEnumerable<BsonDocument> query = col.FindAll().ToList();
            if (columnName == "Id") columnName = "_id";

            switch (comparisonType)
            {
                case Comparison.Equals:
                    query = query.Where(doc => doc[columnName].AsString.Equals(value, StringComparison.Ordinal));
                    break;
                case Comparison.StartsWith:
                    query = query.Where(doc => doc[columnName].AsString.StartsWith(value, StringComparison.Ordinal));
                    break;
                case Comparison.EndsWith:
                    query = query.Where(doc => doc[columnName].AsString.EndsWith(value, StringComparison.Ordinal));
                    break;
                case Comparison.Contains:
                    query = query.Where(doc => doc[columnName].AsString.Contains(value, StringComparison.Ordinal));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(comparisonType), comparisonType, null);
            }

            var results = query.Select(doc =>
            {
                if (doc.ContainsKey("_id"))
                {
                    doc["Id"] = doc["_id"];
                }
                return doc; // Return the modified document
            });

            return results.ToList();

        }


        /// <summary>
        /// Find documents inside a collection using predicate expression.
        /// </summary>
        public List<T> Find(BsonExpression predicate, int skip = 0, int limit = int.MaxValue)
        {
            return _collection.Find(predicate, skip, limit).ToList();

        }
        /// <summary>
        /// Find documents inside a collection using query definition.
        /// </summary>
        public List<T> Find(Query query, int skip = 0, int limit = int.MaxValue)
        {

            return _collection.Find(query, skip, limit).ToList();

        }
        /// <summary>
        /// Find documents inside a collection using predicate expression.
        /// </summary>
        public List<T> Find(Expression<Func<T, bool>> predicate, int skip = 0, int limit = int.MaxValue)
        {


            //lock(SyncRoot)
            //{
            return _collection.Find(predicate, skip, limit).ToList();
            //}

        }
        /// <summary>
        /// Find a document using Document Id. Returns null if not found.
        /// </summary>
        public T FindById(BsonValue id)
        {


            return _collection.FindById(id);


        }
        /// <summary>
        /// Find the first document using predicate expression. Returns null if not found
        /// </summary>
        public T FindOne(BsonExpression predicate)
        {


            return _collection.FindOne(predicate);


        }
        /// <summary>
        /// Find the first document using predicate expression. Returns null if not found
        /// </summary>
        public T FindOne(string predicate, BsonDocument parameters)
        {

            return _collection.FindOne(predicate, parameters);

        }
        /// <summary>
        /// Find the first document using predicate expression. Returns null if not found
        /// </summary>
        public T FindOne(BsonExpression predicate, params BsonValue[] args)
        {


            return _collection.FindOne(predicate, args);


        }
        /// <summary>
        /// Find the first document using predicate expression. Returns null if not found
        /// </summary>
        public T FindOne(Expression<Func<T, bool>> predicate)
        {

            return _collection.FindOne(predicate);

        }
        /// <summary>
        /// Find the first document using defined query structure. Returns null if not found
        /// </summary>
        public T FindOne(Query query)
        {

            return _collection.FindOne(query);

        }
        /// <summary>
        /// Returns all documents inside collection order by _id index.
        /// </summary>
        public List<T> FindAll()
        {

            return _collection.FindAll().ToList();

        }

        /// <summary>
        /// Finds all documents or a specified number of documents in a _collection. 
        /// If take is 0, all documents are returned.
        /// </summary>
        /// <param name="take">The maximum number of documents to return, or 0 to return all.</param>
        /// <returns>A list of BSON documents with '_id' field renamed to 'Id'.</returns>
        public List<BsonDocument> FindAll(int take = 1000, TakeOrder takeOrder = TakeOrder.Last)
        {

            var col = Database.GetCollection<BsonDocument>(_collectionName);

            // Initially get all documents
            IEnumerable<BsonDocument> query = new List<BsonDocument>();

            // If take is greater than 0, apply the Take operation
            if (take > 0)
            {
                if (takeOrder == TakeOrder.First)
                {
                    query = col.FindAll().Take(take).ToList();
                }
                else
                {
                    var totalDocuments = col.Count();
                    var skipAmount = totalDocuments > take ? totalDocuments - take : 0;

                    // Retrieve the last N documents
                    query = col.FindAll().Skip((int)skipAmount).Take(take).ToList();
                }
            }
            else
            {
                query = col.FindAll().ToList();
            }

            var results = query.Select(doc =>
            {
                if (doc.ContainsKey("_id"))
                {
                    // Rename '_id' to 'Id' and remove the original '_id'
                    doc["Id"] = doc["_id"];
                    doc.Remove("_id");
                }
                return doc;
            }).ToList();

            return results;

        }
        #endregion

        #region I

        private void CheckConstraints(T entity)
        {
            // Cache property values to minimize reflection overhead
            var propertyValues = new Dictionary<string, object>();

            foreach (var fk in TableInfo.ForeignKeys)
            {
                propertyValues[fk.Name] = fk.PropertyInfo.GetValue(entity, null);
            }

            // Check foreign keys constraints
            foreach (var fk in TableInfo.ForeignKeys)
            {
                if (fk.Attribute is TableForeignKeyAttribute tfk)
                {
                    if (tfk.Constraint == TableConstraint.Cascading || tfk.Constraint == TableConstraint.Restrictive)
                    {
                        var val = propertyValues[fk.Name];
                        if (val == null) throw new NullReferenceException($"Foreign key {fk.Name} value is null");

                        BsonValue bv = new(val);
                        if (bv.IsNumber && bv < 1) throw new ArgumentException($"Numeric foreign key {fk.Name} value {val} is invalid Id.");

                        var tableName = fk.Name.Substring(0, fk.Name.Length - 2);
                        var table = _iotDb.GetTable(tableName);
                        if (table == null) throw new FileNotFoundException($"Foreign key table {tableName} is not found.");

                        var parentRecords = table.Find("Id", bv, Base.Comparison.Equals);
                        if (parentRecords.Count < 1) throw new MissingMemberException($"Table {tableName} doesn't have record with foreign key Id.");
                    }

                    if (tfk.RelationshipOneTo == RelationshipOneTo.One)
                    {
                        var val = propertyValues[fk.Name];
                        if (val == null) throw new NullReferenceException($"Foreign key {fk.Name} value is null");

                        var items = Find(fk.Name, val?.ToString() ?? string.Empty, Comparison.Equals);
                        if (items.Count > 0) throw new ConstraintException($"Relationship One to One Constraint: TRUE. One item existed.");
                    }
                }
            }

            // Check unique constraints
            foreach (var unique in TableInfo.Uniques)
            {
                if (unique.Attribute is UniqueValueAttribute uva)
                {
                    var val = unique.PropertyInfo.GetValue(entity, null);

                    var records = Find(unique.Name, val?.ToString() ?? string.Empty);
                    if (records.Count > 0) throw new InvalidDataException($"Unique Constraint: Column name {unique.Name} with unique attribute has record with same value.");
                }
            }
        }


        /// <summary>
        /// Insert a new entity to this _collection. Document Id must be a new value in collection - Returns document Id
        /// </summary>
        public BsonValue Insert(T entity)
        {
            CheckConstraints(entity);


            lock (SyncRoot)
            {

                var id = _collection.Insert(entity);
                if (id.IsNull) return id;

                _attributeQueue.Enqueue(entity);

                return id;
            }

        }

        /// <summary>
        /// Insert a new document to this collection using passed id value.
        /// </summary>
        public void Insert(BsonValue id, T entity)
        {
            CheckConstraints(entity);


            lock (SyncRoot)
            {

                _collection.Insert(id, entity);
                _attributeQueue.Enqueue(entity);
            }

        }

        /// <summary>
        /// Insert an array of new documents to this _collection. Document Id must be a new value in _collection. Can be set buffer size to commit at each N documents
        /// </summary>
        public int Insert(IEnumerable<T> entities)
        {
            foreach (var entity in entities)
            {
                CheckConstraints(entity);
            }
            lock (SyncRoot)
            {
                
                int count = _collection.Insert(entities);
                foreach (var entity in entities)
                {
                    _attributeQueue.Enqueue(entity);
                }
                return count;
            }

        }

        /// <summary>
        /// Implements bulk insert documents in a _collection. Usefull when need lots of documents.
        /// </summary>
        public int InsertBulk(IEnumerable<T> entities, int batchSize = 5000)
        {
            foreach (var entity in entities)
            {
                CheckConstraints(entity);
            }

            lock (SyncRoot)
            {
                
                int count = _collection.InsertBulk(entities, batchSize);
                foreach (var entity in entities)
                {
                    _attributeQueue.Enqueue(entity);
                }
                return count;
            }

        }
        #endregion

        #region M
        /// <summary>
        /// Returns the min value from specified key value in collection
        /// </summary>
        public BsonValue Min(BsonExpression keySelector)
        {

            return _collection.Min(keySelector);

        }
        /// <summary>
        /// Returns the min value of _id index
        /// </summary>
        public BsonValue Min()
        {
            return _collection.Min();

        }
        /// <summary>
        /// Returns the min value from specified key value in collection
        /// </summary>
        public K Min<K>(Expression<Func<T, K>> keySelector)
        {
            return _collection.Min(keySelector);

        }
        /// <summary>
        /// Returns the max value from specified key value in collection
        /// </summary>
        public BsonValue Max(BsonExpression keySelector)
        {
            return _collection.Max(keySelector);
        }
        /// <summary>
        /// Returns the max _id index key value
        /// </summary>
        public BsonValue Max()
        {
            return _collection.Max();
        }
        /// <summary>
        /// Returns the last/max field using a linq expression
        /// </summary>
        public K Max<K>(Expression<Func<T, K>> keySelector)
        {
            return _collection.Max(keySelector);
        }
        #endregion

        #region N
        /// <summary>
        /// Get collection name
        /// </summary>
        public string Name => _collectionName;
        #endregion

        #region O
        protected void OnBlockExceptionOccurred(object? sender, ExceptionEventArgs e)
        {
            OnExceptionOccurred(e);
        }
        #endregion

        #region Q
        

        public QueryBuilder<T> Query()
        {
            return new QueryBuilder<T>(_iotDb);
        }


        #endregion

        #region Set
        public long SetAll(string columnName, BsonValue? value)
        {
            if (_blockChainAttributes.Count > 0) { throw new NotSupportedException("UpdateMany is not supported for T with BlockChainValue attributes"); }

            // Calling the UpdateMany method with the specified transform and predicate expressions.
            var updatedCount = UpdateMany($"{{ {columnName}: '{value}' }}", "_id > 0");

            return updatedCount;
        }
        #endregion


        #region TimeSeries
        public List<ColumnInfo> TimeSeriesAttributes
        {
            get
            {
                return _timeSeriesAttributes;
            }
        }

        public ColumnInfo TagColumnAsTimeSeries(string name, string description = "")
        {
            var prop = ReflectionHelper.GetProperty(typeof(T), name);
            if (prop == null) throw new KeyNotFoundException(name);
            TimeSeriesAttribute attribute = new TimeSeriesAttribute(description);
            ColumnInfo bi = new(prop, attribute);
            _blockChainAttributes.Add(bi);
            return bi;
        }

        public ITsCollection? TimeSeries(string name)
        {
            if (!_timeSeriesAttributes.Any(x => x.Name == name))
            {
                return null; // throw new EntryPointNotFoundException($"{typeof(T).Name} does not have BlockChainValueAttribute with name {name}.");
            }

            if (!_ts.ContainsKey(name))
            {
                var tsPath = Path.Combine(DbPath, "TimeSeries");
                Helper.MachineInfo.CreateDirectory(tsPath);
                _ts[name] = new TsCollection(tsPath, $"{DbName}_{name}");
                _ts[name].ExceptionOccurred += OnBlockExceptionOccurred;
            }
            return _ts[name];
        }

        private void WriteToTimeSeries(T entity)
        {
            var idProp = GetIdProperty(typeof(T));
            if (idProp == null) return;
            var idVal = idProp.GetValue(entity);
            if (idVal == null) return;
            if (entity is IotValue val)
            {
                if (val.IsTimeSeries && val.Value != null)
                {
                    _ts[val.Name].Insert(idVal.ToString() ?? val.Name, new BsonValue(val.Value), val.Timestamp);
                }
            }
            else
            {
                foreach (var tsi in _timeSeriesAttributes)
                {
                    var value = tsi.PropertyInfo.GetValue(entity);
                    if (value == null) continue;
                    _ts[tsi.Name].Insert(idVal.ToString() ?? tsi.Name, new BsonValue(value), DateTime.UtcNow);
                }
            }
        }

        
        #endregion

        #region U
        /// <summary>
        /// Insert or Update a document in this _collection.
        /// </summary>
        public bool Upsert(T entity)
        {
            lock (SyncRoot)
            {
                WriteToBlocks(entity);
                return _collection.Upsert(entity);
            }

        }
        /// <summary>
        /// Insert or Update all documents
        /// </summary>
        public int Upsert(IEnumerable<T> entities)
        {

            lock (SyncRoot)
            {
                foreach (var entity in entities)
                {
                    WriteToBlocks(entity);
                }
                return _collection.Upsert(entities);
            }

        }

        /// <summary>
        /// Insert or Update a document in this _collection.
        /// </summary>
        public bool Upsert(BsonValue id, T entity)
        {


            lock (SyncRoot)
            {
                WriteToBlocks(entity);
                return _collection.Upsert(id, entity);
            }

        }

        /// <summary>
        /// Enque update for background processing. No return.
        /// </summary>
        /// <param name="entity"></param>
        public void UpdateQueue(T entity)
        {
            if (_blockChainAttributes.Count > 0) { throw new NotSupportedException("UpdateQueue is not supported for T with BlockChainValue attributes"); }
            _updateEntityQueue.Enqueue(entity);

        }
        /// <summary
        /// <summary>
        /// Update a document in this _collection. Returns false if not found document in collection
        /// </summary>
        public bool Update(T entity)
        {
            lock (SyncRoot)
            {
                WriteToBlocks(entity);
                return _collection.Update(entity);
            }

        }


        /// <summary>
        /// Update a document in this _collection. Returns false if not found document in collection
        /// </summary>
        public bool Update(BsonValue id, T entity)
        {
            lock (SyncRoot)
            {
                WriteToBlocks(entity);
                return _collection.Update(id, entity);
            }

        }

        /// <summary>
        /// Update all documents
        /// </summary>
        public int Update(IEnumerable<T> entities)
        {
            lock (SyncRoot)
            {
                foreach (var entity in entities)
                {
                    WriteToBlocks(entity);
                }
                return _collection.Update(entities);
            }

        }

        /// <summary>
        /// Update many documents based on transform expression. This expression must return a new document that will be replaced over current document (according with predicate).
        /// Eg: col.UpdateMany("{ Name: UPPER($.Name), Age }", "_id > 0")
        /// </summary>
        public int UpdateMany(BsonExpression transform, BsonExpression predicate)
        {
            if (_blockChainAttributes.Count > 0) { throw new NotSupportedException("UpdateMany is not supported for T with BlockChainValue attributes"); }
            lock (SyncRoot)
            {
                return _collection.UpdateMany(transform, predicate);
            }

        }

        /// <summary>
        /// Update many document based on merge current document with extend expression. Use your class with initializers. 
        /// Eg: col.UpdateMany(x => new Customer { Name = x.Name.ToUpper(), Salary: 100 }, x => x.Name == "John")
        /// </summary>
        public int UpdateMany(Expression<Func<T, T>> extend, Expression<Func<T, bool>> predicate)
        {
            if (_blockChainAttributes.Count > 0) { throw new NotSupportedException("UpdateMany is not supported for T with BlockChainValue attributes"); }

            lock (SyncRoot)
            {

                return _collection.UpdateMany(extend, predicate);
            }

        }

        #endregion

        #region Base Functions

        private ILiteCollection<T> Collection
        {
            get
            {

                return Database.GetCollection<T>(_collectionName);
            }
        }

        protected override void InitializeDatabase()
        {

        }

        protected override void PerformBackgroundWork(CancellationToken cancellationToken)
        {
            if (!_updateEntityQueue.IsEmpty && !_processingQueue)
            {
                lock (SyncRoot)
                {
                    _processingQueue = true;
                    try
                    {
                        CommitUpdate();

                    }
                    catch (Exception ex)
                    {
                        OnExceptionOccurred(new(ex));
                    }
                    finally
                    {
                        _processingQueue = false;
                    }
                }
            }
        }

        private void CommitUpdate()
        {
            int count = 0;
            Dictionary<long, T> entityList = new();
            List<long> id = new List<long>();
            while (_updateEntityQueue.TryDequeue(out var entity) && count++ <= 1000)
            {
                var idProperty = GetIdProperty(typeof(T));
                if (idProperty == null)
                {
                    OnExceptionOccurred(new(new InvalidOperationException("Type T must have a property named 'Id'.")));
                }
                else
                {
                    long entityId = Convert.ToInt64(idProperty.GetValue(entity));
                    if (!entityList.ContainsKey(entityId))
                    {
                        entityList.Add(entityId, entity);
                    }
                    else
                    {
                        entityList[entityId] = entity;
                    }
                }
            }
            if (entityList.Count > 0)
            {
                lock (SyncRoot)
                {
                    var entities = Collection;
                    entities.Update(entityList.Select(x => x.Value).ToList());
                }
            }
        }

        #endregion base functions

        #region Queue
        
        private void ProcessAttributeQueue(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (_attributeQueue.TryDequeue(out var entity))
                {
                    WriteToBlocks(entity);
                    WriteToTimeSeries(entity);
                }
                else
                {
                    Thread.Sleep(100); // Adjust sleep time as needed
                }
            }
        }

        // Make sure to call this method when you want to stop the background processing
        public void StopProcessing()
        {
            _cts.Cancel();
        }
        #endregion
    }
}