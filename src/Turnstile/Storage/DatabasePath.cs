namespace Turnstile.Storage;

using System.Runtime.InteropServices;

/// <summary>
/// The single source of a database's filesystem identity (a #202 follow-up). Turnstile's ownership contract
/// rests on one thing: every process opening a given SQLite file must agree on <em>which file it is</em>, so
/// the <see cref="ModeLock"/> sidecar a daemon takes and the sidecar a direct <see cref="LocalStore"/> takes
/// name the same lock the instant they open the same database.
///
/// <para>Lexical normalization (<see cref="Path.GetFullPath(string)"/>) is not enough: it collapses <c>.</c>
/// and <c>..</c> but never follows a symlink, so two ordinary aliases of one file — a symlinked database file,
/// or a database under a symlinked directory — normalize to two <em>different</em> strings, take two different
/// sidecar locks, and let a direct writer commit behind a daemon's live watch through the very same inode.
/// Canonicalizing through <c>realpath(3)</c> — which resolves every intermediate <em>and</em> final symlink to
/// the one real path — makes those aliases converge on a single identity, so the lock and the SQLite open
/// always agree.</para>
///
/// <para><b>Supported alias contract.</b> Any symlink aliasing of the database path — the final component, any
/// intermediate directory, or both — resolves to one canonical path shared by every opener. A database that
/// does not yet exist canonicalizes its (existing) parent directory and appends the filename, so parent-dir
/// aliases converge <em>before</em> the file is created. A path whose final component is a <em>dangling</em>
/// symlink (its target absent) cannot be canonicalized honestly — appending the name to the resolved parent
/// would lock the link's own path while SQLite followed it elsewhere — so it is refused with a visible
/// <see cref="IOException"/> rather than silently given a lock identity that differs from the opened file.</para>
///
/// <para><b>Out of scope.</b> This is coordination correctness, not filesystem-security containment. Hardlink
/// aliasing (two directory entries for one inode, with no symlink) is invisible to <c>realpath</c> and not
/// covered; an adversary swapping a path component after resolution is host-local tampering the
/// <see cref="ModeLock"/> docs already place out of scope. The contract is exactly: <em>ordinary supported
/// symlink aliasing of the same file resolves to the same lock identity.</em></para>
///
/// <para>The <c>realpath</c>/<c>readlink</c> P/Invokes are source-generated (<see cref="LibraryImport"/>), so
/// they stay NativeAOT- and trim-safe with no added dependency, matching <see cref="ModeLock"/>. The buffer
/// <c>realpath</c> allocates — it is called with a NULL second argument, the POSIX.1-2008 / BSD form that
/// <c>malloc</c>s the resolved path — is always released through libc <c>free</c>, so no native memory
/// leaks.</para>
/// </summary>
internal static partial class DatabasePath
{
    /// <summary>
    /// Resolves <paramref name="dbPath"/> to the single canonical absolute path every opener of that same file
    /// will agree on. Creates the parent directory if absent (both SQLite and the not-yet-created resolution
    /// below need it present). Resolve once here and hand the result to both the <see cref="ModeLock"/> and
    /// <see cref="KvStore.Open"/>, so the lock and the SQLite open can never disagree about the file's identity.
    /// </summary>
    /// <exception cref="IOException">
    /// If the final component is a dangling symlink (its target absent), or the parent directory cannot be
    /// canonicalized — either case would otherwise derive a lock identity that differs from the opened file.
    /// </exception>
    public static string Canonicalize(string dbPath)
    {
        string full = Path.GetFullPath(dbPath);

        // Ensure the parent exists before resolving: a not-yet-created database has no leaf to realpath, so its
        // identity comes from the real parent directory — which must exist for realpath to resolve it — and
        // SQLite needs the directory present to create the file anyway.
        string? dir = Path.GetDirectoryName(full);
        if (dir is { Length: > 0 })
        {
            Directory.CreateDirectory(dir);
        }

        // Existing database (or any symlink chain that fully resolves to an existing target): realpath follows
        // every intermediate directory symlink and the final-component symlink to the one real path, so two
        // ordinary aliases of the same file return the identical string here.
        string? resolved = Realpath(full, out _);
        if (resolved is not null)
        {
            return resolved;
        }

        // The whole path did not resolve. If its final component is itself a symlink, it is dangling (the
        // parent resolves but the target does not): appending its name to the resolved parent would lock the
        // link's own path while SQLite followed it elsewhere — the exact split identity this helper removes.
        // Refuse visibly instead of deriving a lock that disagrees with the file that would be opened.
        if (LeafIsSymlink(full))
        {
            throw new IOException(
                $"turnstile: database path '{dbPath}' is a dangling symlink — its target does not exist, so a "
                + "canonical identity cannot be derived without risking an ownership lock that names a different "
                + "file than SQLite would open. Create the target file first, or pass the real path.");
        }

        // A not-yet-created database under an existing directory: resolve that directory (so parent-directory
        // aliases converge) and append the filename. The parent was created above, so it must resolve.
        if (dir is { Length: > 0 })
        {
            string? realDir = Realpath(dir, out int dirErrno);
            if (realDir is null)
            {
                throw new IOException(
                    $"turnstile: cannot canonicalize database directory '{dir}' for '{dbPath}' (errno {dirErrno})");
            }

            return Path.Combine(realDir, Path.GetFileName(full));
        }

        return full;
    }

    // Resolves an existing path to its canonical absolute form via realpath(3), following every symlink. On
    // success returns the resolved string and frees the libc-allocated buffer; on failure returns null and
    // reports the errno through <paramref name="errno"/>, captured immediately after the failing syscall so no
    // interposed managed work can clobber it. realpath is called with a NULL second argument so libc mallocs a
    // right-sized buffer (POSIX.1-2008 / BSD), which is always freed here.
    private static string? Realpath(string path, out int errno)
    {
        nint result = realpath(path, 0);
        if (result == 0)
        {
            errno = Marshal.GetLastPInvokeError();
            return null;
        }

        try
        {
            errno = 0;
            return Marshal.PtrToStringUTF8(result);
        }
        finally
        {
            free(result);
        }
    }

    // Reports whether the final component of <paramref name="path"/> is itself a symlink. readlink(2) resolves
    // intermediate directory symlinks but never the final component, so a non-negative return means the leaf is
    // a symlink (dangling or not); EINVAL means it is not a symlink and ENOENT means no such entry — both
    // return negative. A one-byte buffer suffices: the return value, not the contents, is the signal, and
    // readlink silently truncates rather than failing when the buffer is too small to hold the target.
    private static bool LeafIsSymlink(string path)
    {
        byte[] scratch = new byte[1];
        return readlink(path, scratch, (nuint)scratch.Length) >= 0;
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint realpath(string path, nint resolved);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint readlink(string path, byte[] buffer, nuint bufferSize);

    [LibraryImport("libc")]
    private static partial void free(nint ptr);
}
