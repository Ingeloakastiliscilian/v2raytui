using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace V2RayTui.Engine;

/// <summary>
/// Linux file-descriptor plumbing for attaching a terminal to the running instance:
/// passing descriptors over a Unix socket (SCM_RIGHTS) and swapping stdin/stdout/stderr.
/// </summary>
internal static unsafe class UnixFd
{
    private const int SOL_SOCKET = 1;
    private const int SCM_RIGHTS = 1;
    private const int O_RDWR = 2;

    [DllImport("libc", SetLastError = true)]
    public static extern int dup(int fd);

    [DllImport("libc", SetLastError = true)]
    public static extern int dup2(int oldfd, int newfd);

    [DllImport("libc", SetLastError = true)]
    public static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    public static extern int isatty(int fd);

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int sys_open([MarshalAs(UnmanagedType.LPStr)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern nint sendmsg(int sockfd, MsgHdr* msg, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern nint recvmsg(int sockfd, MsgHdr* msg, int flags);

    [DllImport("libc")]
    public static extern uint getuid();

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVec
    {
        public void* Base;
        public nuint Len;
    }

    // glibc struct msghdr (x86_64 / aarch64): 56 bytes, same field order.
    [StructLayout(LayoutKind.Sequential)]
    private struct MsgHdr
    {
        public void* Name;
        public uint NameLen;
        public IoVec* Iov;
        public nuint IovLen;
        public void* Control;
        public nuint ControlLen;
        public int Flags;
    }

    public static bool IsTerminal(int fd) => isatty(fd) == 1;

    public static int OpenDevNull() => sys_open("/dev/null", O_RDWR);

    /// <summary>Points stdin/stdout/stderr at /dev/null (a detached process must not hold a terminal).</summary>
    public static void StdioToDevNull()
    {
        var nul = OpenDevNull();
        if (nul < 0)
        {
            return;
        }
        dup2(nul, 0);
        dup2(nul, 1);
        dup2(nul, 2);
        if (nul > 2)
        {
            close(nul);
        }
    }

    private static int Fd(Socket s) => (int)s.SafeHandle.DangerousGetHandle();

    /// <summary>Sends <paramref name="data"/> together with open descriptors.</summary>
    public static void Send(Socket socket, byte[] data, IReadOnlyList<int> fds)
    {
        var cmsgLen = 16 + 4 * fds.Count;
        var space = (cmsgLen + 7) & ~7;
        var control = stackalloc byte[space];
        new Span<byte>(control, space).Clear();
        *(nuint*)control = (nuint)cmsgLen;
        *(int*)(control + 8) = SOL_SOCKET;
        *(int*)(control + 12) = SCM_RIGHTS;
        for (var i = 0; i < fds.Count; i++)
        {
            ((int*)(control + 16))[i] = fds[i];
        }
        fixed (byte* p = data)
        {
            var iov = new IoVec { Base = p, Len = (nuint)data.Length };
            var msg = new MsgHdr { Iov = &iov, IovLen = 1, Control = control, ControlLen = (nuint)space };
            if (sendmsg(Fd(socket), &msg, 0) < 0)
            {
                throw new IOException($"sendmsg failed: errno {Marshal.GetLastPInvokeError()}");
            }
        }
    }

    /// <summary>Receives a message and the descriptors attached to it.</summary>
    public static int Receive(Socket socket, byte[] buffer, out int[] fds)
    {
        const int space = 64;
        var control = stackalloc byte[space];
        new Span<byte>(control, space).Clear();
        fixed (byte* p = buffer)
        {
            var iov = new IoVec { Base = p, Len = (nuint)buffer.Length };
            var msg = new MsgHdr { Iov = &iov, IovLen = 1, Control = control, ControlLen = space };
            var n = (int)recvmsg(Fd(socket), &msg, 0);
            if (n < 0)
            {
                throw new IOException($"recvmsg failed: errno {Marshal.GetLastPInvokeError()}");
            }
            fds = [];
            if (msg.ControlLen >= 16 && *(int*)(control + 8) == SOL_SOCKET && *(int*)(control + 12) == SCM_RIGHTS)
            {
                var count = ((int)*(nuint*)control - 16) / 4;
                fds = new int[Math.Max(0, count)];
                for (var i = 0; i < fds.Length; i++)
                {
                    fds[i] = ((int*)(control + 16))[i];
                }
            }
            return n;
        }
    }
}
