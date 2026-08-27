namespace Octoshift.GitHub;

using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Runs a child process inside a durable OS containment boundary and guarantees that boundary is torn down
/// before the call returns — nothing the child spawned outlives it, whatever the child's own lifetime.
/// </summary>
/// <remarks>
/// On Unix the boundary is a fresh process group. The child is <c>posix_spawn</c>ed with
/// <c>POSIX_SPAWN_SETPGROUP</c> and a group id of 0, which places it in a new group whose id equals its own
/// pid — set atomically at spawn, with none of the parent-side <c>setpgid</c> race a post-hoc call would
/// carry. The group id is a stable number that stays a valid <c>kill(-pgid)</c> target for as long as any
/// member survives, so tearing the group down does not depend on the root staying alive to be walked
/// (unlike <c>Process.Kill(entireProcessTree)</c>, a best-effort snapshot rooted at a process that may
/// already be gone). Killing the group closes every inherited copy of the output pipes, which is what lets
/// the drains reach EOF instead of hanging on a descendant that inherited a write end.
///
/// Windows is handled by the caller with a best-effort <see cref="System.Diagnostics.Process"/> tree kill;
/// the process-group guarantee here is Unix-only and not claimed elsewhere.
/// </remarks>
internal static partial class ContainedProcess
{
    private const int POSIX_SPAWN_SETPGROUP = 0x02;
    private const int SIGKILL = 9;
    private const int EINTR = 4;
    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;

    // The opaque spawn types are pointer-sized on macOS and larger structs on glibc (~80 and ~336 bytes).
    // Over-allocating a fixed buffer covers both: init writes only within the real size, and the surplus is
    // never touched.
    private const int SpawnObjectBufferSize = 1024;

    /// <summary>
    /// Raised when the child could not be started at all (for example the program was not found on PATH),
    /// so the caller can report it as a 127 exit rather than a crash.
    /// </summary>
    internal sealed class SpawnFailedException(string message) : Exception(message);

    /// <summary>
    /// Spawns <paramref name="file"/> (searched on PATH) in a new process group, runs it to completion, and
    /// returns its exit code and captured stdout/stderr. On cancellation the entire group is killed and
    /// reaped, and both pipes drained, before the original cancellation propagates.
    /// </summary>
    internal static async Task<GhResult> RunAsync(
        string file,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?>? environmentOverrides,
        CancellationToken ct)
    {
        int pid;
        int outRead;
        int errRead;
        try
        {
            (pid, outRead, errRead) = Spawn(file, args, environmentOverrides);
        }
        catch (SpawnFailedException ex)
        {
            return new GhResult(127, string.Empty, ex.Message);
        }

        // The drains run without the caller's token: killing the group is what unblocks them, so cancelling
        // the reads would only abandon the pipes while the tree is coming down. A child that fills its stdout
        // pipe stops making progress, so both are read on their own threads, concurrently with the wait.
        Task<string> stdoutTask = Task.Run(() => ReadAllUtf8(outRead), CancellationToken.None);
        Task<string> stderrTask = Task.Run(() => ReadAllUtf8(errRead), CancellationToken.None);

        int exitCode;
        using (ct.Register(static state => KillGroup((int)state!), pid))
        {
            // Reaps the direct child. If the caller cancels, the registration kills the group, which makes
            // this return with the signalled status; the group teardown below then sweeps any descendants.
            exitCode = await Task.Run(() => WaitForExit(pid), CancellationToken.None).ConfigureAwait(false);
        }

        // Whether the child exited on its own or was cancelled, sweep the group so any descendant that
        // inherited a pipe is killed and the drains can reach EOF. Empty groups are a harmless no-op.
        KillGroup(pid);

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        // Only now that the tree is confirmed down and both pipes are drained does cancellation surface, with
        // its original identity.
        ct.ThrowIfCancellationRequested();

        return new GhResult(exitCode, stdout, stderr);
    }

    private static unsafe (int Pid, int OutRead, int ErrRead) Spawn(
        string file,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?>? environmentOverrides)
    {
        Span<int> outPipe = stackalloc int[2];
        Span<int> errPipe = stackalloc int[2];
        if (MakePipe(outPipe) != 0)
        {
            throw new SpawnFailedException($"octoshift: failed to create stdout pipe (errno {Marshal.GetLastPInvokeError()}).");
        }

        if (MakePipe(errPipe) != 0)
        {
            close(outPipe[0]);
            close(outPipe[1]);
            throw new SpawnFailedException($"octoshift: failed to create stderr pipe (errno {Marshal.GetLastPInvokeError()}).");
        }

        byte** argv = null;
        byte** envp = null;
        byte* faBuffer = stackalloc byte[SpawnObjectBufferSize];
        byte* attrBuffer = stackalloc byte[SpawnObjectBufferSize];
        new Span<byte>(faBuffer, SpawnObjectBufferSize).Clear();
        new Span<byte>(attrBuffer, SpawnObjectBufferSize).Clear();
        bool faInit = false;
        bool attrInit = false;

        try
        {
            argv = BuildNativeStringArray(BuildArgv(file, args));
            envp = BuildNativeStringArray(BuildEnvironment(environmentOverrides));

            if (posix_spawn_file_actions_init(faBuffer) != 0)
            {
                throw new SpawnFailedException("octoshift: failed to init posix_spawn file actions.");
            }

            faInit = true;
            posix_spawn_file_actions_adddup2(faBuffer, outPipe[1], 1);
            posix_spawn_file_actions_adddup2(faBuffer, errPipe[1], 2);
            posix_spawn_file_actions_addclose(faBuffer, outPipe[0]);
            posix_spawn_file_actions_addclose(faBuffer, errPipe[0]);
            posix_spawn_file_actions_addclose(faBuffer, outPipe[1]);
            posix_spawn_file_actions_addclose(faBuffer, errPipe[1]);

            if (posix_spawnattr_init(attrBuffer) != 0)
            {
                throw new SpawnFailedException("octoshift: failed to init posix_spawn attributes.");
            }

            attrInit = true;
            posix_spawnattr_setflags(attrBuffer, POSIX_SPAWN_SETPGROUP);
            posix_spawnattr_setpgroup(attrBuffer, 0);

            byte* path = Utf8(file);
            try
            {
                int pid;
                int rc = posix_spawnp(&pid, path, faBuffer, attrBuffer, argv, envp);
                if (rc != 0)
                {
                    throw new SpawnFailedException(
                        $"octoshift: failed to start '{file}' ({Marshal.GetPInvokeErrorMessage(rc)}).");
                }

                // The parent never writes to the child; closing its write ends is what lets the reader see EOF
                // once the last group member releases them.
                close(outPipe[1]);
                close(errPipe[1]);
                return (pid, outPipe[0], errPipe[0]);
            }
            finally
            {
                NativeMemory.Free(path);
            }
        }
        catch
        {
            close(outPipe[0]);
            close(outPipe[1]);
            close(errPipe[0]);
            close(errPipe[1]);
            throw;
        }
        finally
        {
            if (faInit)
            {
                posix_spawn_file_actions_destroy(faBuffer);
            }

            if (attrInit)
            {
                posix_spawnattr_destroy(attrBuffer);
            }

            FreeNativeStringArray(argv);
            FreeNativeStringArray(envp);
        }
    }

    private static unsafe int MakePipe(Span<int> fds)
    {
        fixed (int* p = fds)
        {
            if (pipe(p) != 0)
            {
                return -1;
            }
        }

        // Close-on-exec so a concurrent, unrelated spawn cannot inherit these fds and hold a pipe open past
        // its owner's exit. The child receives fds 1 and 2 through explicit dup2, which clears the flag on the
        // duplicated descriptors, so the program still gets working stdout/stderr.
        fcntl(fds[0], F_SETFD, FD_CLOEXEC);
        fcntl(fds[1], F_SETFD, FD_CLOEXEC);
        return 0;
    }

    private static string[] BuildArgv(string file, IReadOnlyList<string> args)
    {
        var argv = new string[args.Count + 1];
        argv[0] = file;
        for (int i = 0; i < args.Count; i++)
        {
            argv[i + 1] = args[i];
        }

        return argv;
    }

    private static string[] BuildEnvironment(IReadOnlyDictionary<string, string?>? overrides)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            merged[(string)entry.Key] = entry.Value as string ?? string.Empty;
        }

        if (overrides is not null)
        {
            foreach ((string key, string? value) in overrides)
            {
                if (value is null)
                {
                    merged.Remove(key);
                }
                else
                {
                    merged[key] = value;
                }
            }
        }

        var lines = new string[merged.Count];
        int i = 0;
        foreach ((string key, string value) in merged)
        {
            lines[i++] = string.Concat(key, "=", value);
        }

        return lines;
    }

    private static unsafe byte** BuildNativeStringArray(string[] values)
    {
        byte** array = (byte**)NativeMemory.Alloc((nuint)(values.Length + 1), (nuint)sizeof(byte*));
        for (int i = 0; i < values.Length; i++)
        {
            array[i] = Utf8(values[i]);
        }

        array[values.Length] = null;
        return array;
    }

    private static unsafe void FreeNativeStringArray(byte** array)
    {
        if (array is null)
        {
            return;
        }

        for (int i = 0; array[i] is not null; i++)
        {
            NativeMemory.Free(array[i]);
        }

        NativeMemory.Free(array);
    }

    private static unsafe byte* Utf8(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        byte* buffer = (byte*)NativeMemory.Alloc((nuint)(byteCount + 1));
        var span = new Span<byte>(buffer, byteCount + 1);
        Encoding.UTF8.GetBytes(value, span);
        span[byteCount] = 0;
        return buffer;
    }

    private static unsafe string ReadAllUtf8(int fd)
    {
        var buffer = new MemoryStream();
        byte* chunk = stackalloc byte[8192];
        while (true)
        {
            nint n = read(fd, chunk, 8192);
            if (n < 0)
            {
                if (Marshal.GetLastPInvokeError() == EINTR)
                {
                    continue;
                }

                break;
            }

            if (n == 0)
            {
                break;
            }

            buffer.Write(new ReadOnlySpan<byte>(chunk, (int)n));
        }

        close(fd);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static unsafe int WaitForExit(int pid)
    {
        int status;
        while (waitpid(pid, &status, 0) < 0)
        {
            if (Marshal.GetLastPInvokeError() != EINTR)
            {
                return -1;
            }
        }

        // Classic wait-status encoding, identical on Linux and macOS: a zero low-7-bits means a normal exit
        // whose code is in the next byte; otherwise the child was terminated by the signal in the low bits.
        if ((status & 0x7f) == 0)
        {
            return (status >> 8) & 0xff;
        }

        return 128 + (status & 0x7f);
    }

    private static void KillGroup(int pid)
    {
        // Negative pid targets the whole process group. The group id equals the leader's pid, and it stays a
        // valid target while any member lives, so this reaches descendants even after the leader has exited.
        // ESRCH (an already-empty group) is the expected no-op after a clean exit.
        _ = kill(-pid, SIGKILL);
    }

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int pipe(int* fildes);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fcntl(int fd, int cmd, int arg);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial nint read(int fd, byte* buf, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int sig);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int waitpid(int pid, int* status, int options);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial int posix_spawnp(int* pid, byte* file, byte* fileActions, byte* attrp, byte** argv, byte** envp);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawn_file_actions_init(byte* fileActions);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawn_file_actions_destroy(byte* fileActions);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawn_file_actions_adddup2(byte* fileActions, int fd, int newFd);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawn_file_actions_addclose(byte* fileActions, int fd);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawnattr_init(byte* attr);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawnattr_destroy(byte* attr);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawnattr_setflags(byte* attr, short flags);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawnattr_setpgroup(byte* attr, int pgroup);
}
