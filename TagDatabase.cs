﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Data.SQLite;
using Squared.Task;
using System.Data;
using Squared.Task.Data;
using Squared.Util;

namespace Ndexer {
    public class TagDatabase {
        public struct Filter {
            public long ID;
            public string Pattern;
            public string Language;
        }

        public struct Change {
            public string Filename;
            public bool Deleted;
        }

        public struct Folder {
            public long ID;
            public string Path;
        }

        public struct SourceFile {
            public long ID;
            public string Path;
            public long Timestamp;
        }

        private Query
            _GetContextID,
            _GetKindID,
            _GetLanguageID,
            _GetSourceFileID,
            _GetSourceFileTimestamp,
            _GetPreference,
            _SetPreference,
            _MakeContextID,
            _MakeKindID,
            _MakeLanguageID,
            _MakeSourceFileID,
            _DeleteTagsForFile,
            _DeleteSourceFile,
            _DeleteSourceFilesAndTagsForFolder,
            _LastInsertID,
            _InsertTag,
            _SetFullTextContentForFile;

        private const int MemoizationLRUSize = 128;
        private int _MemoizationHits = 0;
        private int _MemoizationMisses = 0;

        private Dictionary<string, LRUCache<string, object>> _MemoizationCache = new Dictionary<string, LRUCache<string, object>>();
        private Dictionary<string, object> _PreferenceCache = new Dictionary<string, object>();
        private Dictionary<string, Func<string, IEnumerator<object>>> _TaskMap = new Dictionary<string, Func<string, IEnumerator<object>>>();

        public TaskScheduler Scheduler;
        public SQLiteConnection NativeConnection;
        public ConnectionWrapper Connection;

        public TagDatabase (TaskScheduler scheduler, string filename) {
            Scheduler = scheduler;

            string connectionString = String.Format("Data Source={0}", filename);
            NativeConnection = new SQLiteConnection(connectionString);
            NativeConnection.Open();
            Connection = new ConnectionWrapper(scheduler, NativeConnection);

            CompileQueries();

            _TaskMap["GetSourceFileID"] = GetSourceFileID;
            _TaskMap["GetKindID"] = GetKindID;
            _TaskMap["GetLanguageID"] = GetLanguageID;
            _TaskMap["GetContextID"] = GetContextID;

#if DEBUG
            scheduler.Start(
                MemoizationHitRateLogger(), TaskExecutionPolicy.RunAsBackgroundTask
            );
#endif
        }

        private IEnumerator<object> MemoizationHitRateLogger () {
            while (true) {
                double hitRate = double.NaN;
                int total = _MemoizationHits + _MemoizationMisses;
                if (total > 0) {
                    hitRate = (_MemoizationHits / (double)total) * 100;

                    System.Diagnostics.Debug.WriteLine(String.Format("MemoizedGetID avg. hit rate: {0}% of {1} request(s)", Math.Round(hitRate, 2), total));
                }

                yield return new Sleep(60.0);
            }
        }

        private void CompileQueries () {
            _GetContextID = Connection.BuildQuery(@"SELECT TagContexts_ID FROM TagContexts WHERE TagContexts_Text = ?");
            _GetKindID = Connection.BuildQuery(@"SELECT TagKinds_ID FROM TagKinds WHERE TagKinds_Name = ?");
            _GetLanguageID = Connection.BuildQuery(@"SELECT TagLanguages_ID FROM TagLanguages WHERE TagLanguages_Name = ?");
            _GetSourceFileID = Connection.BuildQuery(@"SELECT SourceFiles_ID FROM SourceFiles WHERE SourceFiles_Path = ?");
            _GetSourceFileTimestamp = Connection.BuildQuery(@"SELECT SourceFiles_Timestamp FROM SourceFiles WHERE SourceFiles_Path = ?");
            _GetPreference = Connection.BuildQuery(@"SELECT Preferences_Value FROM Preferences WHERE Preferences_Name = ?");
            _SetPreference = Connection.BuildQuery(@"INSERT OR REPLACE INTO Preferences (Preferences_Name, Preferences_Value) VALUES (?, ?)");
            _LastInsertID = Connection.BuildQuery(@"SELECT last_insert_rowid()");
            _MakeContextID = Connection.BuildQuery(
                @"INSERT OR REPLACE INTO TagContexts (TagContexts_Text) VALUES (?)"
            );
            _MakeKindID = Connection.BuildQuery(
                @"INSERT OR REPLACE INTO TagKinds (TagKinds_Name) VALUES (?)"
            );
            _MakeLanguageID = Connection.BuildQuery(
                @"INSERT OR REPLACE INTO TagLanguages (TagLanguages_Name) VALUES (?)"
            );
            _MakeSourceFileID = Connection.BuildQuery(
                @"INSERT OR REPLACE INTO SourceFiles (SourceFiles_Path, SourceFiles_Timestamp) VALUES (?, ?)"
            );
            _DeleteTagsForFile = Connection.BuildQuery(@"DELETE FROM Tags WHERE SourceFiles_ID = ?");
            _DeleteSourceFile = Connection.BuildQuery(
                @"DELETE FROM Tags WHERE SourceFiles_ID = @p0;" +
                @"DELETE FROM SourceFiles WHERE SourceFiles_ID = @p0");
            _DeleteSourceFilesAndTagsForFolder = Connection.BuildQuery(
                @"DELETE FROM Tags WHERE " +
                @"Tags.SourceFiles_ID IN ( " +
                @"SELECT SourceFiles_ID FROM SourceFiles WHERE " +
                @"SourceFiles.SourceFiles_Path LIKE @p0 );" +
                @"DELETE FROM SourceFiles WHERE SourceFiles_Path LIKE @p0"
            );
            _InsertTag = Connection.BuildQuery(
                @"INSERT INTO Tags (" +
                @"Tags_Name, SourceFiles_ID, Tags_LineNumber, TagKinds_ID, TagContexts_ID, TagLanguages_ID" +
                @") VALUES (" +
                @"?, ?, ?, ?, ?, ?" +
                @");" +
                @"SELECT last_insert_rowid()"
            );
            _SetFullTextContentForFile = Connection.BuildQuery(
                @"INSERT OR REPLACE INTO FullText (SourceFiles_ID, FileText) VALUES (@p0, @p1);" + 
                @"UPDATE FullText_content SET c1FileText = '' WHERE c0SourceFiles_ID = @p0"
            );
        }

        public IEnumerator<object> Initialize () {
            yield return Connection.ExecuteSQL("PRAGMA synchronous=0");
            yield return Connection.ExecuteSQL("PRAGMA auto_vacuum=none");
            yield return Connection.ExecuteSQL("PRAGMA journal_mode=MEMORY");
            yield return Connection.ExecuteSQL("PRAGMA read_uncommitted=1");
            yield return Connection.ExecuteSQL("PRAGMA cache_size=5000");
        }

        public IEnumerator<object> Compact () {
            long timeStart = Time.Ticks;

            yield return Connection.ExecuteSQL(
                @"DELETE FROM TagContexts WHERE (" +
                @"SELECT COUNT(*) FROM Tags WHERE " +
                @"TagContexts.TagContexts_ID = Tags.TagContexts_ID ) < 1"
            );

            yield return Connection.ExecuteSQL("VACUUM");

            long timeEnd = Time.Ticks;
            long elapsed = timeEnd - timeStart;

            Console.WriteLine("Database compaction took {0:000.00} second(s).", (double)elapsed / Time.SecondInTicks);
        }

        public IEnumerator<object> OpenReadConnection () {
            var connectionString = NativeConnection.ConnectionString + ";Read Only=True";

            var f = Future.RunInThread(
                (Func<SQLiteConnection>)(() => {
                    var conn = new SQLiteConnection(connectionString);
                    conn.Open();
                    return conn;
                })
            );
            yield return f;

            var result = new ConnectionWrapper(
                Scheduler,
                f.Result as SQLiteConnection
            );
            yield return new Result(result);
        }

        public IEnumerator<object> GetFilters () {
            var filter = new Filter();

            Future<ConnectionWrapper> f;
            yield return OpenReadConnection().Run(out f);

            using (var conn = f.Result)
            using (var query = conn.BuildQuery(@"SELECT Filters_ID, Filters_Pattern, Filters_Language FROM Filters"))
            using (var iter = query.Execute())
            while (!iter.Disposed) {
                yield return iter.Fetch();

                foreach (var item in iter) {
                    filter.ID = item.GetInt64(0);
                    filter.Pattern = item.GetString(1);
                    filter.Language = item.GetString(2);
                    yield return new NextValue(filter);
                }
            }
        }

        public IEnumerator<object> GetFolders () {
            var folder = new Folder();

            Future<ConnectionWrapper> f;
            yield return OpenReadConnection().Run(out f);

            using (var conn = f.Result)
            using (var query = conn.BuildQuery(@"SELECT Folders_ID, Folders_Path FROM Folders"))
            using (var iter = query.Execute())
            while (!iter.Disposed) {
                yield return iter.Fetch();

                foreach (var item in iter) {
                    folder.ID = item.GetInt64(0);
                    folder.Path = item.GetString(1);
                    yield return new NextValue(folder);
                }
            }
        }

        public IEnumerator<object> GetSourceFiles () {
            var sf = new SourceFile();

            Future<ConnectionWrapper> f;
            yield return OpenReadConnection().Run(out f);

            using (var conn = f.Result)
            using (var query = conn.BuildQuery(@"SELECT SourceFiles_ID, SourceFiles_Path, SourceFiles_Timestamp FROM SourceFiles"))
            using (var iter = query.Execute())
            while (!iter.Disposed) {
                yield return iter.Fetch();

                foreach (var item in iter) {
                    sf.ID = item.GetInt64(0);
                    sf.Path = item.GetString(1);
                    sf.Timestamp = item.GetInt64(2);
                    yield return new NextValue(sf);
                }
            }
        }

        public IEnumerator<object> GetPreference (string name) {
            object result;
            if (!_PreferenceCache.TryGetValue(name, out result)) {
                var f = _GetPreference.ExecuteScalar(name);
                yield return f;
                result = f.Result;
                _PreferenceCache[name] = result;
            }

            yield return new Result(result);
        }

        public IEnumerator<object> SetPreference (string name, string value) {
            var f = _SetPreference.ExecuteScalar(name, value);
            yield return f;
            var result = f.Result;
            _PreferenceCache[name] = value;
        }

        public IEnumerator<object> DeleteSourceFile (string filename) {
            FlushMemoizedIDs();

            var f = _GetSourceFileID.ExecuteScalar(filename);
            yield return f;

            if (f.Result is long)
                yield return _DeleteSourceFile.ExecuteNonQuery(f.Result);
        }

        public IEnumerator<object> DeleteTagsForFile (string filename) {
            var f = _GetSourceFileID.ExecuteScalar(filename);
            yield return f;

            if (f.Result is long)
                yield return _DeleteTagsForFile.ExecuteNonQuery(f.Result);
        }

        public IEnumerator<object> DeleteSourceFileOrFolder (string filename) {
            FlushMemoizedIDs();

            var f = _GetSourceFileID.ExecuteScalar(filename);
            yield return f;

            if (f.Result is long) {
                yield return _DeleteSourceFile.ExecuteNonQuery(f.Result);
            } else {
                if (!filename.EndsWith("\\"))
                    filename += "\\";
                filename += "%";

                yield return _DeleteSourceFilesAndTagsForFolder.ExecuteNonQuery(filename);
            }
        }

        public IEnumerator<object> GetFilterPatterns () {
            var iter = new TaskEnumerator<Filter>(GetFilters());
            var f = Scheduler.Start(iter.GetArray());

            yield return f;

            string[] filters = (from _ in (Filter[])f.Result select _.Pattern).ToArray();

            yield return new Result(filters);
        }

        public IEnumerator<object> GetFolderPaths () {
            var iter = new TaskEnumerator<Folder>(GetFolders());
            var f = Scheduler.Start(iter.GetArray());

            yield return f;

            string[] folders = (from _ in (Folder[])f.Result select _.Path).ToArray();

            yield return new Result(folders);
        }

        public IEnumerator<object> UpdateFileListAndGetChangeSet (BlockingQueue<Change> changeSet) {
            string filters;
            string[] folders;

            {
                Future<string[]> f;
                yield return GetFilterPatterns().Run(out f);
                filters = String.Join(";", f.Result);

                yield return GetFolderPaths().Run(out f);
                folders = f.Result;
            }

            using (var iterator = new TaskEnumerator<SourceFile>(GetSourceFiles()))
            while (!iterator.Disposed) {
                yield return iterator.Fetch();

                foreach (var file in iterator) {
                    bool validFolder = false;
                    bool fileExists = false;

                    foreach (var folder in folders) {
                        if (file.Path.StartsWith(folder)) {
                            validFolder = true;
                            break;
                        }
                    }

                    if (validFolder)
                        fileExists = System.IO.File.Exists(file.Path);

                    if (!validFolder || !fileExists)
                        changeSet.Enqueue(
                            new Change { Filename = file.Path, Deleted = true }
                        );
                }
            }

            foreach (var folder in folders) {
                var enumerator = Squared.Util.IO.EnumDirectoryEntries(
                    folder, filters, true, Squared.Util.IO.IsFile
                );

                using (var dirEntries = TaskEnumerator<IO.DirectoryEntry>.FromEnumerable(enumerator))
                while (!dirEntries.Disposed) {
                    yield return dirEntries.Fetch();

                    foreach (var entry in dirEntries) {
                        long newTimestamp = entry.LastWritten;
                        long oldTimestamp = 0;

                        Future f;
                        yield return GetSourceFileTimestamp(entry.Name).Run(out f);
                        if (f.Result is long)
                            oldTimestamp = (long)f.Result;

                        if (newTimestamp > oldTimestamp)
                            changeSet.Enqueue(
                                new Change { Filename = entry.Name, Deleted = false }
                            );
                    }
                }
            }

            yield break;
        }

        public IEnumerator<object> GetSourceFileTimestamp (string path) {
            var f = _GetSourceFileTimestamp.ExecuteScalar(path);
            yield return f;
            yield return new Result(f.Result);
        }

        public IEnumerator<object> GetSourceFileID (string path) {
            Future<object> f = _GetSourceFileID.ExecuteScalar(path);
            yield return f;

            if (f.Result is long) {
                yield return new Result(f.Result);
            } else {
                yield return MakeSourceFileID(path, 0).Run(out f);
                yield return new Result(f.Result);
            }
        }

        internal IEnumerator<object> MakeSourceFileID (string path, long timestamp) {
            IFuture f = _MakeSourceFileID.ExecuteNonQuery(path, timestamp);
            yield return f;

            f = _GetSourceFileID.ExecuteScalar(path);
            yield return f;

            FlushMemoizedIDsForTask("GetSourceFileID", path);

            yield return new Result(f.Result);
        }

        public IEnumerator<object> GetKindID (string kind) {
            if (kind == null)
                yield return new Result(0);

            var f = _GetKindID.ExecuteScalar(kind);
            yield return f;

            if (f.Result is long) {
                yield return new Result(f.Result);
            } else {
                f = _MakeKindID.ExecuteScalar(kind);
                yield return f;

                f = _GetKindID.ExecuteScalar(kind);
                yield return f;

                yield return new Result(f.Result);
            }
        }

        public IEnumerator<object> GetContextID (string context) {
            if (context == null)
                yield return new Result(0);

            var f = _GetContextID.ExecuteScalar(context);
            yield return f;

            if (f.Result is long) {
                yield return new Result(f.Result);
            } else {
                f = _MakeContextID.ExecuteScalar(context);
                yield return f;

                f = _GetContextID.ExecuteScalar(context);
                yield return f;

                yield return new Result(f.Result);
            }
        }

        public IEnumerator<object> GetLanguageID (string language) {
            if (language == null)
                yield return new Result(0);

            var f = _GetLanguageID.ExecuteScalar(language);
            yield return f;

            if (f.Result is long) {
                yield return new Result(f.Result);
            } else {
                f = _MakeLanguageID.ExecuteScalar(language);
                yield return f;

                f = _GetLanguageID.ExecuteScalar(language);
                yield return f;

                yield return new Result(f.Result);
            }
        }

        public void FlushMemoizedIDsForTask (string taskName, string argument) {
            if (argument == null) {
                if (_MemoizationCache.ContainsKey(taskName))
                    _MemoizationCache.Remove(taskName);
            } else {
                LRUCache<string, object> resultCache = null;
                if (_MemoizationCache.TryGetValue(taskName, out resultCache)) {
                    if (resultCache.ContainsKey(argument))
                        resultCache.Remove(argument);
                }
            }
        }

        public void FlushMemoizedIDsForTask (string taskName) {
            FlushMemoizedIDsForTask(taskName, null);
        }

        public void FlushMemoizedIDs () {
            _MemoizationCache.Clear();
        }

        public IEnumerator<object> MemoizedGetID (string taskName, string argument) {
            if (argument == null)
                yield return new Result(0);

            var task = _TaskMap[taskName];
            LRUCache<string, object> resultCache = null;
            if (!_MemoizationCache.TryGetValue(taskName, out resultCache)) {
                resultCache = new LRUCache<string, object>(MemoizationLRUSize);
                _MemoizationCache[taskName] = resultCache;
            }

            object result = null;
            if (resultCache.TryGetValue(argument, out result)) {
                _MemoizationHits += 1;
            } else {
                _MemoizationMisses += 1;
                Future f;
                yield return task(argument).Run(out f);
                result = f.Result;

                resultCache[argument] = result;
            }

            yield return new Result(result);
        }

        public IEnumerator<object> SetFullTextContentForFile (string filename, string content) {
            Future<Int64> f;
            yield return MemoizedGetID("GetSourceFileID", filename).Run(out f);
            var sourceFileID = f.Result;

            yield return _SetFullTextContentForFile.ExecuteNonQuery(
                sourceFileID, content
            );
        }

        public IEnumerator<object> AddTag (Tag tag) {
            Future<object> f;
            yield return MemoizedGetID("GetSourceFileID", tag.SourceFile).Run(out f);
            var sourceFileID = (Int64)(f.Result);

            yield return MemoizedGetID("GetKindID", tag.Kind).Run(out f);
            var kindID = Convert.ToInt64(f.Result);

            yield return MemoizedGetID("GetContextID", tag.Context).Run(out f);
            var contextID = Convert.ToInt64(f.Result);

            yield return MemoizedGetID("GetLanguageID", tag.Language).Run(out f);
            var languageID = Convert.ToInt64(f.Result);

            f = _InsertTag.ExecuteScalar(
                tag.Name, sourceFileID, tag.LineNumber,
                kindID, contextID, languageID
            );
            yield return f;
            yield return new Result(f.Result);
        }

        public IEnumerator<object> Clear () {
            yield return Connection.ExecuteSQL("DELETE FROM Tags");
        }

        public IEnumerator<object> Dispose () {
            yield return Connection.Dispose();

            NativeConnection.Dispose();
        }
    }
}
