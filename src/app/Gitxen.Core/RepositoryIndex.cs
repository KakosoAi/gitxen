// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Gitxen.Git;
using Microsoft.Data.Sqlite;

namespace Gitxen.Services;

/// <summary>A commit found by <see cref="RepositoryIndex.Search"/>.</summary>
internal sealed record IndexedCommit(string RepositoryPath, string RepositoryName, string Hash, string Author, DateTimeOffset Date, string Subject)
{
    public string ShortId => Hash.Length > 8 ? Hash[..8] : Hash;
}

/// <summary>What the index holds.</summary>
internal sealed record IndexStatus(int Repositories, long Commits, long Bytes);

/// <summary>What an update did.</summary>
internal enum IndexOutcome
{
    /// <summary>No ref has moved since the last time.</summary>
    Unchanged,

    /// <summary>Only the commits new since the last time were read.</summary>
    Added,

    /// <summary>History was read again from the start.</summary>
    Rebuilt
}

/// <summary>
///  A local full-text index of every repository's commits, so one search can look across all of them.
/// </summary>
/// <remarks>
///  <para>
///   One SQLite file, <c>%LOCALAPPDATA%\Gitxen\index.db</c>, with an FTS5 table of subject, body and
///   author. It is a cache and nothing else: everything in it can be read again from git, so a
///   corrupt file, or one written by another version, is deleted and rebuilt rather than repaired.
///  </para>
///  <para>
///   Kept up to date cheaply. A repository's refs are fingerprinted (two git reads); only when the
///   fingerprint changed is history read, and then only the commits not reachable from the refs as
///   they were last time (<c>log --all --not &lt;old tips&gt;</c>). A count taken afterwards catches what
///   an increment cannot — history rewritten, a branch deleted — and falls back to a full read.
///  </para>
///  <para>
///   Commit bodies are kept to 2,000 characters and a repository to 100,000 commits: it is a search
///   aid, not a copy of git. Written with WAL and a busy timeout so the window and <c>gx</c> can use
///   it at the same time.
///  </para>
/// </remarks>
internal sealed partial class RepositoryIndex
{
    public const int MaxCommitsPerRepository = 100_000;

    private const int SchemaVersion = 2;
    private const int MaxBodyCharacters = 2000;
    private const int MaxIncrementalTips = 300;
    private const char FieldSeparator = '\u001f';

    private readonly string _path;

    public RepositoryIndex(string? path = null)
    {
        _path = path ?? DefaultPath;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Gitxen", "index.db");

    /// <summary>Brings one repository's entry up to date with git.</summary>
    public IndexOutcome Update(GitCli git, string repositoryPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);

        return WithRecovery(() => UpdateCore(git, repositoryPath, cancellationToken));
    }

    /// <summary>Commits matching <paramref name="text"/>, best first. Every word is matched as a prefix; a hash prefix works too.</summary>
    public IReadOnlyList<IndexedCommit> Search(string text, int limit = 20, string? repositoryPath = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        string[] words = [.. Words().Matches(text).Select(match => match.Value)];

        if (words.Length == 0 || !File.Exists(_path))
        {
            return [];
        }

        return WithRecovery(() =>
        {
            using SqliteConnection connection = Open();
            List<IndexedCommit> found = [];
            HashSet<string> seen = new(StringComparer.Ordinal);

            void Collect(SqliteCommand command)
            {
                using SqliteDataReader reader = command.ExecuteReader();

                while (reader.Read() && found.Count < limit)
                {
                    string hash = reader.GetString(0);

                    if (seen.Add(reader.GetString(4) + hash))
                    {
                        found.Add(new IndexedCommit(
                            reader.GetString(4),
                            reader.GetString(5),
                            hash,
                            reader.GetString(2),
                            DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
                            reader.GetString(1)));
                    }
                }
            }

            string scope = repositoryPath is null ? "" : " AND commits.repo = @repo";

            // A hash, or the start of one.
            if (words.Length == 1 && words[0].Length >= 6 && words[0].All(Uri.IsHexDigit))
            {
                using SqliteCommand byHash = connection.CreateCommand();
                byHash.CommandText =
                    "SELECT commits.hash, commits.subject, commits.author, commits.time, commits.repo, repos.name " +
                    "FROM commits JOIN repos ON repos.path = commits.repo " +
                    $"WHERE commits.hash LIKE @prefix{scope} LIMIT @limit";
                byHash.Parameters.AddWithValue("@prefix", words[0].ToLowerInvariant() + "%");
                byHash.Parameters.AddWithValue("@limit", limit);

                if (repositoryPath is not null)
                {
                    byHash.Parameters.AddWithValue("@repo", repositoryPath);
                }

                Collect(byHash);
            }

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT commits.hash, commits.subject, commits.author, commits.time, commits.repo, repos.name " +
                "FROM commits JOIN repos ON repos.path = commits.repo " +
                $"WHERE commits MATCH @match{scope} " +
                "ORDER BY bm25(commits, 10.0, 2.0, 4.0) LIMIT @limit";
            command.Parameters.AddWithValue("@match", string.Join(' ', words.Select(word => $"\"{word}\"*")));
            command.Parameters.AddWithValue("@limit", limit);

            if (repositoryPath is not null)
            {
                command.Parameters.AddWithValue("@repo", repositoryPath);
            }

            Collect(command);
            return (IReadOnlyList<IndexedCommit>)found;
        });
    }

    public IndexStatus Status()
    {
        if (!File.Exists(_path))
        {
            return new IndexStatus(0, 0, 0);
        }

        return WithRecovery(() =>
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT count(*), coalesce(sum(commit_count), 0) FROM repos";
            using SqliteDataReader reader = command.ExecuteReader();
            reader.Read();
            return new IndexStatus(reader.GetInt32(0), reader.GetInt64(1), new FileInfo(_path).Length);
        });
    }

    /// <summary>Drops one repository from the index, as when it is removed from Gitxen.</summary>
    public void Forget(string repositoryPath)
    {
        if (!File.Exists(_path))
        {
            return;
        }

        WithRecovery(() =>
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();
            Execute(connection, transaction, "DELETE FROM commits WHERE repo = @repo", ("@repo", repositoryPath));
            Execute(connection, transaction, "DELETE FROM repos WHERE path = @repo", ("@repo", repositoryPath));
            transaction.Commit();
            return 0;
        });
    }

    /// <summary>Deletes the whole index. The next updates rebuild it.</summary>
    public void Clear() => Delete();

    private IndexOutcome UpdateCore(GitCli git, string repositoryPath, CancellationToken cancellationToken)
    {
        string name = Path.GetFileName(repositoryPath.TrimEnd('\\', '/'));
        string[] tips = ReadTips(git);

        using SqliteConnection connection = Open();

        if (tips.Length == 0)
        {
            // No commits yet, or the folder is not a repository any more.
            using SqliteTransaction empty = connection.BeginTransaction();
            Execute(connection, empty, "DELETE FROM commits WHERE repo = @repo", ("@repo", repositoryPath));
            Execute(connection, empty, "DELETE FROM repos WHERE path = @repo", ("@repo", repositoryPath));
            empty.Commit();
            return IndexOutcome.Unchanged;
        }

        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', tips.Order(StringComparer.Ordinal))))).ToLowerInvariant();
        (string? storedFingerprint, string[] storedTips) = ReadRepository(connection, repositoryPath);

        if (storedFingerprint == fingerprint)
        {
            return IndexOutcome.Unchanged;
        }

        GitResult counted = git.Read(new GitArgs("rev-list").Add("--all", "--count"), cancellationToken);
        int total = counted.Succeeded && int.TryParse(counted.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? Math.Min(parsed, MaxCommitsPerRepository)
            : 0;

        bool incremental = storedFingerprint is not null && storedTips.Length is > 0 and <= MaxIncrementalTips;
        IndexOutcome outcome = incremental ? IndexOutcome.Added : IndexOutcome.Rebuilt;

        if (incremental)
        {
            // Not reachable from any tip as it was last time: exactly what has been added since.
            GitArgs args = LogArguments(new GitArgs("log").Add("--all", "--not").Add([.. storedTips]));
            incremental = ReadInto(connection, git, args, repositoryPath, replace: false, cancellationToken);
        }

        if (incremental)
        {
            // A rewritten branch or a deleted one leaves commits behind that an increment cannot see.
            incremental = CountRows(connection, repositoryPath) == total;
        }

        if (!incremental)
        {
            outcome = IndexOutcome.Rebuilt;
            GitArgs args = LogArguments(new GitArgs("log").Add("--all", $"--max-count={MaxCommitsPerRepository.ToString(CultureInfo.InvariantCulture)}"));

            if (!ReadInto(connection, git, args, repositoryPath, replace: true, cancellationToken))
            {
                throw new IOException($"git could not read the history of {repositoryPath}.");
            }
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        Execute(
            connection,
            transaction,
            "INSERT INTO repos(path, name, fingerprint, tips, commit_count, indexed_at) VALUES(@path, @name, @fingerprint, @tips, @commits, @at) " +
            "ON CONFLICT(path) DO UPDATE SET name = @name, fingerprint = @fingerprint, tips = @tips, commit_count = @commits, indexed_at = @at",
            ("@path", repositoryPath),
            ("@name", name),
            ("@fingerprint", fingerprint),
            ("@tips", string.Join('\n', tips)),
            ("@commits", CountRows(connection, repositoryPath, transaction)),
            ("@at", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        transaction.Commit();

        return outcome;
    }

    /// <summary>
    ///  Every ref's object name, plus <c>HEAD</c> for a detached one: what changes when anything is
    ///  committed, fetched, pushed, deleted or rewritten.
    /// </summary>
    private static string[] ReadTips(GitCli git)
    {
        GitResult refs = git.Read(new GitArgs("for-each-ref").Add("--format=%(objectname)"));
        GitResult head = git.Read(new GitArgs("rev-parse").Add("--verify", "--quiet", "HEAD"));

        return
        [
            .. (refs.Succeeded ? refs.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [])
                .Concat(head.Succeeded ? [head.Output.Trim()] : [])
                .Distinct(StringComparer.Ordinal)
        ];
    }

    private static GitArgs LogArguments(GitArgs args) =>
        args.Add("-z", "--encoding=UTF-8", "--format=%H%x1f%an%x1f%at%x1f%s%x1f%b");

    /// <summary>
    ///  Streams a log into the table inside one transaction. False when git failed, in which case
    ///  nothing is kept.
    /// </summary>
    private bool ReadInto(SqliteConnection connection, GitCli git, GitArgs args, string repositoryPath, bool replace, CancellationToken cancellationToken)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();

        HashSet<string> existing = replace ? [] : ReadHashes(connection, repositoryPath, transaction);

        if (replace)
        {
            Execute(connection, transaction, "DELETE FROM commits WHERE repo = @repo", ("@repo", repositoryPath));
        }

        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO commits(subject, body, author, repo, hash, time) VALUES(@subject, @body, @author, @repo, @hash, @time)";
        SqliteParameter subject = insert.Parameters.Add("@subject", SqliteType.Text);
        SqliteParameter body = insert.Parameters.Add("@body", SqliteType.Text);
        SqliteParameter author = insert.Parameters.Add("@author", SqliteType.Text);
        SqliteParameter repo = insert.Parameters.Add("@repo", SqliteType.Text);
        SqliteParameter hash = insert.Parameters.Add("@hash", SqliteType.Text);
        SqliteParameter time = insert.Parameters.Add("@time", SqliteType.Integer);
        repo.Value = repositoryPath;

        int written = 0;

        GitResult result = git.ReadRecords(
            args,
            batch =>
            {
                foreach (string record in batch)
                {
                    string[] fields = record.TrimStart('\n').Split(FieldSeparator, 5);

                    if (fields.Length < 4 || fields[0].Length is not (40 or 64) || !existing.Add(fields[0]))
                    {
                        continue;
                    }

                    if (!long.TryParse(fields[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long seconds))
                    {
                        continue;
                    }

                    string text = fields.Length > 4 ? fields[4].Trim() : "";

                    hash.Value = fields[0];
                    author.Value = fields[1];
                    time.Value = seconds;
                    subject.Value = fields[3];
                    body.Value = text.Length > MaxBodyCharacters ? text[..MaxBodyCharacters] : text;
                    insert.ExecuteNonQuery();
                    written++;
                }

                return written < MaxCommitsPerRepository;
            },
            cancellationToken);

        // "No commits reachable" is exit 0 with nothing printed; --not with a vanished tip is not.
        if (!result.Succeeded && written == 0)
        {
            transaction.Rollback();
            return false;
        }

        transaction.Commit();
        return true;
    }

    private static HashSet<string> ReadHashes(SqliteConnection connection, string repositoryPath, SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT hash FROM commits WHERE repo = @repo";
        command.Parameters.AddWithValue("@repo", repositoryPath);
        HashSet<string> hashes = new(StringComparer.Ordinal);

        using SqliteDataReader reader = command.ExecuteReader();

        while (reader.Read())
        {
            hashes.Add(reader.GetString(0));
        }

        return hashes;
    }

    private static long CountRows(SqliteConnection connection, string repositoryPath, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM commits WHERE repo = @repo";
        command.Parameters.AddWithValue("@repo", repositoryPath);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static (string? Fingerprint, string[] Tips) ReadRepository(SqliteConnection connection, string repositoryPath)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT fingerprint, tips FROM repos WHERE path = @path";
        command.Parameters.AddWithValue("@path", repositoryPath);
        using SqliteDataReader reader = command.ExecuteReader();

        return reader.Read()
            ? (reader.GetString(0), reader.GetString(1).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            : (null, []);
    }

    /// <summary>
    ///  Runs <paramref name="work"/>, and if SQLite says the file is not a database it understands —
    ///  corrupt, or from another version — deletes it and runs once more against a fresh one.
    /// </summary>
    private T WithRecovery<T>(Func<T> work)
    {
        try
        {
            return work();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26 or 1)
        {
            Delete();
            return work();
        }
    }

    private SqliteConnection Open()
    {
        string? folder = Path.GetDirectoryName(_path);

        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        // No pooling: a pooled connection keeps the file open, and Clear() has to be able to delete it.
        SqliteConnection connection = new($"Data Source={_path};Pooling=False");
        connection.Open();

        try
        {
            using (SqliteCommand pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
                pragma.ExecuteNonQuery();
            }

            EnsureSchema(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using (SqliteCommand version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version";

            if (Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture) == SchemaVersion)
            {
                return;
            }
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand create = connection.CreateCommand();
        create.Transaction = transaction;
        create.CommandText =
            "DROP TABLE IF EXISTS commits; DROP TABLE IF EXISTS repos;" +
            "CREATE TABLE repos(path TEXT PRIMARY KEY, name TEXT NOT NULL, fingerprint TEXT NOT NULL, tips TEXT NOT NULL, commit_count INTEGER NOT NULL, indexed_at INTEGER NOT NULL);" +
            "CREATE VIRTUAL TABLE commits USING fts5(subject, body, author, repo UNINDEXED, hash UNINDEXED, time UNINDEXED, tokenize = 'unicode61 remove_diacritics 2');" +
            $"PRAGMA user_version = {SchemaVersion.ToString(CultureInfo.InvariantCulture)};";
        create.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private void Delete()
    {
        SqliteConnection.ClearAllPools();

        foreach (string file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held open by another process; the schema check will reset it on the next open.
            }
        }
    }

    /// <summary>Letters and digits only, so nothing a person types can be read as FTS5 syntax.</summary>
    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex Words();
}
