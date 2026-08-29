namespace Turnstile.Storage;

/// <summary>
/// The database-specific face of <see cref="CanonicalPath"/> (a #202 follow-up). Turnstile's ownership contract
/// rests on one thing: every process opening a given SQLite file must agree on <em>which file it is</em>, so
/// the <see cref="ModeLock"/> sidecar a daemon takes and the sidecar a direct <see cref="LocalStore"/> takes
/// name the same lock the instant they open the same database. This delegates to the shared canonicalization
/// helper — which resolves every intermediate and final symlink through <c>realpath(3)</c> to one identity —
/// and exists only to pin the resource label ("database") and give database callers a stable name; the
/// supported-alias and out-of-scope contract lives on <see cref="CanonicalPath"/>.
/// </summary>
internal static class DatabasePath
{
    /// <summary>
    /// Resolves <paramref name="dbPath"/> to the single canonical absolute path every opener of that same file
    /// will agree on. Creates the parent directory if absent (both SQLite and the canonicalization need it
    /// present). Resolve once here and hand the result to both the <see cref="ModeLock"/> and
    /// <see cref="KvStore.Open"/>, so the lock and the SQLite open can never disagree about the file's identity.
    /// </summary>
    /// <exception cref="IOException">
    /// If the final component is a dangling symlink (its target absent), or the parent directory cannot be
    /// canonicalized — either case would otherwise derive a lock identity that differs from the opened file.
    /// </exception>
    public static string Canonicalize(string dbPath) => CanonicalPath.Resolve(dbPath, "database");
}
