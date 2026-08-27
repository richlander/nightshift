namespace Octoshift.GitHub;

using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Runs a child process inside a durable OS containment boundary and guarantees that boundary is torn down —
/// and positively verified extinct — before the call returns, whatever the child's own lifetime.
/// </summary>
/// <remarks>
/// On Unix the boundary is a fresh process group. The child is <c>posix_spawn</c>ed with
/// <c>POSIX_SPAWN_SETPGROUP</c> and a group id of 0, which places it in a new group whose id equals its own
/// pid — set atomically at spawn, with none of the parent-side <c>setpgid</c> race a post-hoc call would
/// carry. The group id is a stable number that stays a valid <c>kill(-pgid)</c> target for as long as any
/// member survives, so tearing the group down does not depend on the root staying alive to be walked
/// (unlike <c>Process.Kill(entireProcessTree)</c>, a best-effort snapshot rooted at a process that may
/// already be gone). Killing the group closes every inherited copy of the output pipes, which is what lets
/// the drains reach EOF instead of hanging on a descendant that inherited a write end — but pipe EOF is not
/// treated as proof of death: teardown re-signals and probes with <c>kill(-pgid, 0)</c> until the group is
/// empty.
///
/// Windows is handled by the caller with a best-effort <see cref="System.Diagnostics.Process"/> tree kill;
/// the process-group guarantee here is Unix-only and not claimed elsewhere.
/// </remarks>
internal static partial class ContainedProcess
{
    private const int POSIX_SPAWN_SETPGROUP = 0x02;
    private const int SIGKILL = 9;
    private const int EINTR = 4;
    private const int ESRCH = 3;
    private const int F_DUPFD = 0;
    private const int F_SETFD = 2;
    private const int FD_CLOEXEC = 1;

    // The opaque spawn types are pointer-sized on macOS and larger structs on glibc (~80 and ~336 bytes).
    // Over-allocating a fixed, suitably aligned block covers both ABIs: init writes only within the real
    // struct size, and the surplus is never touched. An alignment of 16 satisfies every field these types
    // are documented to contain (pointers, longs, sigset_t, sched_param).
    private const nuint SpawnObjectBufferSize = 1024;
    private const nuint SpawnObjectAlignment = 16;

    /// <summary>
    /// Raised when the child could not be started at all (a pipe/spawn setup failure, or the program was not
    /// found on PATH), so the caller can report it as a 127 exit rather than a crash.
    /// </summary>
    internal sealed class SpawnFailedException(string message) : Exception(message);

    /// <summary>
    /// Spawns <paramref name="file"/> (searched on PATH) in a new process group, runs it to completion, and
    /// returns its exit code and captured stdout/stderr. On cancellation the entire group is killed, verified
    /// extinct, and both pipes drained before the original cancellation propagates.
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
        // pipe stops making progress, so both are read on their own threads, concurrently with the wait. Each
        // reader owns and closes its fd; if a reader task fails to even start, its fd is closed here so the
        // descriptor never leaks, and the group is still torn down.
        Task<string> stdoutTask;
        Task<string> stderrTask;
        try
        {
            stdoutTask = Task.Run(() => ReadAllUtf8(outRead), CancellationToken.None);
        }
        catch
        {
            SafeClose(outRead);
            SafeClose(errRead);
            AwaitGroupExtinct(pid);
            throw;
        }

        try
        {
            stderrTask = Task.Run(() => ReadAllUtf8(errRead), CancellationToken.None);
        }
        catch
        {
            // outRead is already owned by stdoutTask, which will close it; only errRead is orphaned here.
            SafeClose(errRead);
            AwaitGroupExtinct(pid);
            await ObserveAsync(stdoutTask).ConfigureAwait(false);
            throw;
        }

        var teardown = new TeardownState { Pid = pid };
        int exitCode;
        using (ct.Register(static state => ((TeardownState)state!).ObserveCancellation(), teardown))
        {
            // Reaps the direct child. If the caller cancels, the registration kills the group, which makes
            // this return with the signalled status; the extinction sweep below then confirms every
            // descendant is gone.
            exitCode = await Task.Run(() => WaitForExit(pid), CancellationToken.None).ConfigureAwait(false);
        }

        // Uncancellable cleanup: whether the child exited on its own or was cancelled, kill the group and do
        // not return until it is positively confirmed extinct (kill(-pgid, 0) == ESRCH). Pipe EOF alone does
        // not prove a descendant that closed its own pipes is dead.
        AwaitGroupExtinct(pid);

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        // Only surface cancellation if the registered callback actually observed it and tore the tree down.
        // A token cancelled after the registration was disposed did not cancel this operation, so a child
        // that already completed on its own is reported as the success it was.
        if (teardown.CancellationObserved)
        {
            throw new OperationCanceledException(ct);
        }

        return new GhResult(exitCode, stdout, stderr);
    }

    /// <summary>Mutable state shared with the cancellation callback: the group to kill and whether it fired.</summary>
    private sealed class TeardownState
    {
        private volatile bool _observed;

        public int Pid { get; init; }

        public bool CancellationObserved => _observed;

        public void ObserveCancellation()
        {
            _observed = true;
            KillGroup(Pid);
        }
    }

    private static unsafe (int Pid, int OutRead, int ErrRead) Spawn(
        string file,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?>? environmentOverrides)
    {
        (int outRead, int outWrite) = MakePipe();
        int errRead;
        int errWrite;
        try
        {
            (errRead, errWrite) = MakePipe();
        }
        catch
        {
            SafeClose(outRead);
            SafeClose(outWrite);
            throw;
        }

        byte** argv = null;
        byte** envp = null;
        void* faBuffer = NativeMemory.AlignedAlloc(SpawnObjectBufferSize, SpawnObjectAlignment);
        void* attrBuffer = NativeMemory.AlignedAlloc(SpawnObjectBufferSize, SpawnObjectAlignment);
        new Span<byte>(faBuffer, (int)SpawnObjectBufferSize).Clear();
        new Span<byte>(attrBuffer, (int)SpawnObjectBufferSize).Clear();
        bool faInit = false;
        bool attrInit = false;

        try
        {
            argv = BuildNativeStringArray(BuildArgv(file, args));
            envp = BuildNativeStringArray(BuildEnvironment(environmentOverrides));

            CheckSpawn(posix_spawn_file_actions_init(faBuffer), "init file actions");
            faInit = true;

            // Route the pipe write ends onto the child's stdout/stderr, then drop every descriptor the child
            // should not keep. Because the pipe ends were normalised above fd 2, no dup2 target ever collides
            // with a descriptor a later addclose removes.
            CheckSpawn(posix_spawn_file_actions_adddup2(faBuffer, outWrite, 1), "dup2 stdout");
            CheckSpawn(posix_spawn_file_actions_adddup2(faBuffer, errWrite, 2), "dup2 stderr");
            CheckSpawn(posix_spawn_file_actions_addclose(faBuffer, outRead), "close stdout read end");
            CheckSpawn(posix_spawn_file_actions_addclose(faBuffer, errRead), "close stderr read end");
            CheckSpawn(posix_spawn_file_actions_addclose(faBuffer, outWrite), "close stdout write end");
            CheckSpawn(posix_spawn_file_actions_addclose(faBuffer, errWrite), "close stderr write end");

            CheckSpawn(posix_spawnattr_init(attrBuffer), "init attributes");
            attrInit = true;
            CheckSpawn(posix_spawnattr_setflags(attrBuffer, POSIX_SPAWN_SETPGROUP), "set flags");
            CheckSpawn(posix_spawnattr_setpgroup(attrBuffer, 0), "set process group");

            byte* path = Utf8(file);
            try
            {
                int pid;
                CheckSpawn(posix_spawnp(&pid, path, faBuffer, attrBuffer, argv, envp), $"start '{file}'");

                // The parent never writes to the child; closing its write ends is what lets the reader see EOF
                // once the last group member releases them.
                SafeClose(outWrite);
                SafeClose(errWrite);
                return (pid, outRead, errRead);
            }
            finally
            {
                NativeMemory.Free(path);
            }
        }
        catch
        {
            SafeClose(outRead);
            SafeClose(outWrite);
            SafeClose(errRead);
            SafeClose(errWrite);
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

            NativeMemory.AlignedFree(faBuffer);
            NativeMemory.AlignedFree(attrBuffer);
            FreeNativeStringArray(argv);
            FreeNativeStringArray(envp);
        }
    }

    /// <summary>
    /// Creates a pipe, moves both ends above the standard descriptors, and marks them close-on-exec. Returns
    /// the read and write fds; throws <see cref="SpawnFailedException"/> (having closed any it opened) on any
    /// failure.
    /// </summary>
    private static unsafe (int Read, int Write) MakePipe()
    {
        Span<int> fds = stackalloc int[2];
        fixed (int* p = fds)
        {
            if (pipe(p) != 0)
            {
                throw Errno("create pipe");
            }
        }

        try
        {
            // pipe() can hand back fd 0/1/2 if the parent had a standard descriptor closed. Move both ends
            // above fd 2 so the child's stdout/stderr (the dup2 targets) can never be an addclose target.
            MoveAboveStandardDescriptors(ref fds[0]);
            MoveAboveStandardDescriptors(ref fds[1]);

            // Close-on-exec so a concurrent, unrelated spawn cannot inherit these fds and hold a pipe open
            // past its owner's exit. The child receives fds 1 and 2 through explicit dup2, which clears the
            // flag on the duplicated descriptors, so the program still gets working stdout/stderr.
            SetCloseOnExec(fds[0]);
            SetCloseOnExec(fds[1]);
            return (fds[0], fds[1]);
        }
        catch
        {
            SafeClose(fds[0]);
            SafeClose(fds[1]);
            throw;
        }
    }

    private static void MoveAboveStandardDescriptors(ref int fd)
    {
        if (fd > 2)
        {
            return;
        }

        int moved = fcntl(fd, F_DUPFD, 3);
        if (moved < 0)
        {
            throw Errno("duplicate descriptor above standard streams");
        }

        SafeClose(fd);
        fd = moved;
    }

    private static void SetCloseOnExec(int fd)
    {
        if (fcntl(fd, F_SETFD, FD_CLOEXEC) < 0)
        {
            throw Errno("set close-on-exec");
        }
    }

    private static void CheckSpawn(int rc, string what)
    {
        if (rc != 0)
        {
            throw new SpawnFailedException($"octoshift: failed to {what} ({Marshal.GetPInvokeErrorMessage(rc)}).");
        }
    }

    private static SpawnFailedException Errno(string what)
        => new($"octoshift: failed to {what} ({Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())}).");

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
        try
        {
            var buffer = new MemoryStream();
            byte* chunk = stackalloc byte[8192];
            while (true)
            {
                nint n = read(fd, chunk, 8192);
                if (n < 0)
                {
                    int err = Marshal.GetLastPInvokeError();
                    if (err == EINTR)
                    {
                        continue;
                    }

                    // A read failure must not masquerade as a truncated-but-successful capture: the caller
                    // parses this output, and silently dropping the tail would corrupt that.
                    throw new IOException($"octoshift: reading child output failed ({Marshal.GetPInvokeErrorMessage(err)}).");
                }

                if (n == 0)
                {
                    break;
                }

                buffer.Write(new ReadOnlySpan<byte>(chunk, (int)n));
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        finally
        {
            SafeClose(fd);
        }
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
        // Negative pid targets the whole process group. The group id equals the leader's pid and stays a
        // valid target while any member lives, so this reaches descendants even after the leader has exited.
        _ = kill(-pid, SIGKILL);
    }

    /// <summary>
    /// Kills the process group and does not return until it is positively confirmed extinct — every member
    /// reaped — by probing with signal 0 until <c>ESRCH</c>. Re-sends <c>SIGKILL</c> each round so a
    /// descendant that was mid-fork when the first signal landed is still taken down. Uncancellable: this is
    /// the cleanup that must not leave a token-bearing process alive.
    /// </summary>
    private static void AwaitGroupExtinct(int pid)
    {
        while (true)
        {
            _ = kill(-pid, SIGKILL);

            if (kill(-pid, 0) != 0)
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == ESRCH)
                {
                    // No process remains in the group: extinct.
                    return;
                }

                if (err == EINTR)
                {
                    // Interrupted by a signal; probe again without sleeping.
                    continue;
                }

                // Any other error (for example EPERM against a member we cannot signal — not expected for a
                // group we spawned as the same user) means we cannot yet prove extinction, so we keep trying
                // rather than silently returning past a possibly-live process.
            }

            Thread.Sleep(1);
        }
    }

    private static void SafeClose(int fd)
    {
        if (fd >= 0)
        {
            _ = close(fd);
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The reader is being abandoned on an error path; its result is discarded, so observe and move on.
        }
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
    private static unsafe partial int posix_spawnp(int* pid, byte* file, void* fileActions, void* attrp, byte** argv, byte** envp);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawn_file_actions_init(void* fileActions);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawn_file_actions_destroy(void* fileActions);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawn_file_actions_adddup2(void* fileActions, int fd, int newFd);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawn_file_actions_addclose(void* fileActions, int fd);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawnattr_init(void* attr);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawnattr_destroy(void* attr);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawnattr_setflags(void* attr, short flags);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawnattr_setpgroup(void* attr, int pgroup);
}
