using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

// Codex's CODEX_NODE_REPL_PATH entry point. No plugin or runtime version is pinned.
internal static class RuntimeProxy
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobLimits info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    struct JobLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    // Windows command-line quoting preserves spaces, quotes and trailing backslashes.
    static string Quote(string value)
    {
        return "\"" + System.Text.RegularExpressions.Regex.Replace(value, @"(\\*)\""", "$1$1\\\"")
            .TrimEnd('\\') + new string('\\', value.Reverse().TakeWhile(c => c == '\\').Count() * 2) + "\"";
    }

    static async System.Threading.Tasks.Task Pump(Stream source, Stream destination)
    {
        var buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer, 0, buffer.Length)) != 0)
        {
            await destination.WriteAsync(buffer, 0, count);
            await destination.FlushAsync(); // MCP must receive requests before stdin closes.
        }
    }

    static int Main(string[] args)
    {
        IntPtr job = IntPtr.Zero;
        Process child = null;
        try
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            string proxy = File.ReadAllText(Path.Combine(directory, "proxy-url.txt")).Trim();
            Uri uri;
            if (!Uri.TryCreate(proxy, UriKind.Absolute, out uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
                throw new InvalidOperationException("proxy-url.txt must contain an HTTP(S) proxy origin without credentials.");

            string node = Environment.GetEnvironmentVariable("NODE_REPL_NODE_PATH");
            if (String.IsNullOrEmpty(node) || !Path.IsPathRooted(node) || !File.Exists(node) ||
                !String.Equals(Path.GetFileName(node), "node.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Codex did not supply a valid NODE_REPL_NODE_PATH; runtime layout may have changed.");
            string runtime = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(node), "node_repl.exe"));
            if (!File.Exists(runtime) || String.Equals(runtime, Process.GetCurrentProcess().MainModule.FileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cannot resolve Codex's original node_repl.exe next to node.exe.");

            var start = new ProcessStartInfo(runtime, String.Join(" ", args.Select(Quote))) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.EnvironmentVariables["HTTP_PROXY"] = proxy;
            start.EnvironmentVariables["HTTPS_PROXY"] = proxy;
            start.EnvironmentVariables["ALL_PROXY"] = proxy;
            start.EnvironmentVariables["NO_PROXY"] = "localhost,127.0.0.1,::1";
            start.EnvironmentVariables["NODE_USE_ENV_PROXY"] = "1";
            start.EnvironmentVariables["BROWSER_USE_DISABLE_AMBIENT_NETWORK"] = "0";

            // The real runtime must die with this wrapper, including forced termination.
            job = CreateJobObject(IntPtr.Zero, null);
            var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2000 } };
            if (job == IntPtr.Zero || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(typeof(JobLimits))))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            child = Process.Start(start);
            if (!AssignProcessToJobObject(job, child.Handle))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            // Copy bytes: text readers could alter MCP framing or Unicode payloads.
            var inputTarget = child.StandardInput;
            Pump(Console.OpenStandardInput(), inputTarget.BaseStream).ContinueWith(task => {
                try { inputTarget.Close(); } catch { }
            });
            var output = Pump(child.StandardOutput.BaseStream, Console.OpenStandardOutput());
            var errors = Pump(child.StandardError.BaseStream, Console.OpenStandardError());
            child.WaitForExit();
            System.Threading.Tasks.Task.WaitAll(output, errors);
            return child.ExitCode;
        }
        catch (Exception error)
        {
            // stdout belongs to MCP; diagnostics must only go to stderr.
            Console.Error.WriteLine("Codex runtime proxy: " + error.Message);
            if (child != null) { try { if (!child.HasExited) child.Kill(); } catch { } }
            return 1;
        }
        finally
        {
            if (job != IntPtr.Zero) CloseHandle(job);
            if (child != null) child.Dispose();
        }
    }
}
