using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Scoop
{
    public class Program {
        [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]

        static extern bool CreateProcess(string lpApplicationName,
            string lpCommandLine, IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes, bool bInheritHandles,
            uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory,
            [In] ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);
        const int ERROR_ELEVATION_REQUIRED = 740;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetConsoleWindow();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct STARTUPINFO
        {
            public Int32 cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public Int32 dwX;
            public Int32 dwY;
            public Int32 dwXSize;
            public Int32 dwYSize;
            public Int32 dwXCountChars;
            public Int32 dwYCountChars;
            public Int32 dwFillAttribute;
            public Int32 dwFlags;
            public Int16 wShowWindow;
            public Int16 cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern UInt32 WaitForSingleObject(IntPtr hHandle, UInt32 dwMilliseconds);
        const UInt32 INFINITE = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        // Job Object APIs
        #region Job Object API
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr hJob, JOBOBJECTINFOCLASS JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        enum JOBOBJECTINFOCLASS
        {
            JobObjectBasicLimitInformation = 2,
            JobObjectExtendedLimitInformation = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public Int64 PerProcessUserTimeLimit;
            public Int64 PerJobUserTimeLimit;
            public Int32 LimitFlags;
            public IntPtr MinimumWorkingSetSize;
            public IntPtr MaximumWorkingSetSize;
            public Int32 ActiveProcessLimit;
            public Int64 Affinity;
            public Int32 PriorityClass;
            public Int32 SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public UInt64 ReadOperationCount;
            public UInt64 WriteOperationCount;
            public UInt64 OtherOperationCount;
            public UInt64 ReadTransferCount;
            public UInt64 WriteTransferCount;
            public UInt64 OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public IntPtr ProcessMemoryLimit;
            public IntPtr JobMemoryLimit;
            public IntPtr PeakProcessMemoryUsed;
            public IntPtr PeakJobMemoryUsed;
        }

        const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        #endregion

        #region event handler
        // Define a delegate that matches the signature of the handler.
        delegate bool ConsoleCtrlHandler(uint ctrlType);

        // P/Invoke declaration for SetConsoleCtrlHandler.
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetConsoleCtrlHandler(ConsoleCtrlHandler handler, bool add);

        // Define control event constants.
        const uint CTRL_C_EVENT = 0;
        const uint CTRL_BREAK_EVENT = 1;
        const uint CTRL_CLOSE_EVENT = 2;
        const uint CTRL_LOGOFF_EVENT = 5;
        const uint CTRL_SHUTDOWN_EVENT = 6;

        #endregion

        static int Main(string[] args)
        {
            var exe = Assembly.GetExecutingAssembly().Location;
            var dir = Path.GetDirectoryName(exe);
            var name = Path.GetFileNameWithoutExtension(exe);

            var configPath = Path.Combine(dir, name + ".local-shim");
            // check for local shim
            if (!File.Exists(configPath))
            {
                configPath = Path.Combine(dir, name + ".shim");
            }
            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine("Couldn't find " + Path.GetFileName(configPath) + " in " + dir);
                return 1;
            }

            var (config, envs) = Config(configPath);

            ShimExtensions.VersionManager.SetShimPath(configPath);
            var path = ShimExtensions.VersionManager.ExtendPath(Get(config, "path"), configPath);
            var add_args = ShimExtensions.VersionManager.ExtendEnv(Get(config, "args"));

            ShimExtensions.VersionManager.ExportEnvs(envs, path, add_args);

            var si = new STARTUPINFO();
            var pi = new PROCESS_INFORMATION();

            // create command line
            var cmd_args = add_args ?? "";
            var pass_args = GetArgs(Environment.CommandLine);
            if (!string.IsNullOrEmpty(pass_args))
            {
                if (!string.IsNullOrEmpty(cmd_args)) cmd_args += " ";
                cmd_args += pass_args;
            }

            if(!string.IsNullOrEmpty(cmd_args)) cmd_args = " " + cmd_args;
            var cmd = path + cmd_args;

            // Fix when GUI applications want to write to a console
            if (GetConsoleWindow() == IntPtr.Zero) {
                AttachConsole(-1);
            }

            #region delegate cancel event handler
            // Create a handler delegate instance.
            ConsoleCtrlHandler handler = new ConsoleCtrlHandler(MyCtrlHandler);
            // Register the handler.
            if(!SetConsoleCtrlHandler(handler, true))
            {
                Console.Error.WriteLine("Shim: Could not set control handler; Ctrl-C behavior may be invalid");
            }
            #endregion

            #region creat job object
            // Create job object by default
            IntPtr jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (jobHandle != IntPtr.Zero)
            {
                // Configure job to terminate all processes when job is closed
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

                int infoSize = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr extendedInfoPtr = Marshal.AllocHGlobal(infoSize);
                try
                {
                    Marshal.StructureToPtr(info, extendedInfoPtr, false);

                    if (!SetInformationJobObject(
                        jobHandle,
                        JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                        extendedInfoPtr,
                        (uint)infoSize))
                    {
                        Debug.WriteLine("Failed to set job object information: " + Marshal.GetLastWin32Error());
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(extendedInfoPtr);
                }
            }
            else
            {
                Debug.WriteLine("Failed to create job object: " + Marshal.GetLastWin32Error());
            }
            #endregion
            if (!CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero,
                bInheritHandles: true,
                dwCreationFlags: 0,
                lpEnvironment: IntPtr.Zero, // inherit parent
                lpCurrentDirectory: null, // inherit parent
                lpStartupInfo: ref si,
                lpProcessInformation: out pi))
            {

                var error = Marshal.GetLastWin32Error();
                if (error == ERROR_ELEVATION_REQUIRED)
                {
                    // Unfortunately, ShellExecute() does not allow us to run program without
                    // CREATE_NEW_CONSOLE, so we can not replace CreateProcess() completely.
                    // The good news is we are okay with CREATE_NEW_CONSOLE when we run program with elevation.
                    Process process = new Process();
                    process.StartInfo = new ProcessStartInfo(path, cmd_args);
                    process.StartInfo.UseShellExecute = true;
                    try
                    {
                        process.Start();
                    }
                    catch (Win32Exception exception)
                    {
                        if (jobHandle != IntPtr.Zero) CloseHandle(jobHandle);
                        return exception.ErrorCode;
                    }
                    process.WaitForExit();
                    if (jobHandle != IntPtr.Zero) CloseHandle(jobHandle);
                    return process.ExitCode;
                }
                if (jobHandle != IntPtr.Zero) CloseHandle(jobHandle);
                return error;
            }

            #region assign Job Object
            // Assign process to job object
            if (jobHandle != IntPtr.Zero)
            {
                if (!AssignProcessToJobObject(jobHandle, pi.hProcess))
                {
                    Debug.WriteLine("Failed to assign process to job object: " + Marshal.GetLastWin32Error());
                }
            }
            #endregion

            WaitForSingleObject(pi.hProcess, INFINITE);

            uint exit_code = 0;
            GetExitCodeProcess(pi.hProcess, out exit_code);

            // Close process and thread handles.
            CloseHandle(pi.hProcess);
            CloseHandle(pi.hThread);

            #region Close Job handle
            // Close job handle - this will also terminate any remaining child processes
            if (jobHandle != IntPtr.Zero)
            {
                CloseHandle(jobHandle);
            }
            #endregion
            return (int)exit_code;
        }

        // now uses GetArgs instead
        static string Serialize(string[] args)
        {
            return string.Join(" ", args.Select(a => a.Contains(' ') ? '"' + a + '"' : a));
        }

        // strips the program name from the command line, returns just the arguments
        static string GetArgs(string cmdLine)
        {
            if (cmdLine.StartsWith("\""))
            {
                var endQuote = cmdLine.IndexOf("\" ", 1);
                if (endQuote < 0) return "";
                return cmdLine.Substring(endQuote + 1);
            }
            var space = cmdLine.IndexOf(' ');
            if (space < 0 || space == cmdLine.Length - 1) return "";
            return cmdLine.Substring(space + 1);
        }

        static string Get(Dictionary<string, string> dic, string key)
        {
            string value = null;
            dic.TryGetValue(key, out value);
            return value;
        }


        static (Dictionary<string, string>, List<ShimExtensions.EnvOption>) Config(string path)
        {
            var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var env = new List<ShimExtensions.EnvOption>();
            // Use RegexOptions.Compiled for better performance
            var regex = new Regex(@"^\s*([^\s?=]+)\s*(\?\?=|=)\s*(.*)", RegexOptions.Compiled);

            foreach (var line in File.ReadAllLines(path))
            {
                var m = regex.Match(line);
                if (m.Success)
                {
                    var key = m.Groups[1].Value.Trim();
                    if (key.StartsWith("ENV:"))
                    {
                        env.Add(new ShimExtensions.EnvOption(m.Groups[1].Value,
                                                             m.Groups[3].Value.Trim(),
                                                             m.Groups[2].Value));
                    }
                    else
                    {
                        config[key] = m.Groups[3].Value.Trim();
                    }
                }
            }
            return (config, env);
        }

        static bool MyCtrlHandler(uint ctrlType)
        {
            // Cancel all control events.
            switch (ctrlType)
            {
                case CTRL_C_EVENT:
                case CTRL_BREAK_EVENT:
                case CTRL_CLOSE_EVENT:
                case CTRL_LOGOFF_EVENT:
                case CTRL_SHUTDOWN_EVENT:
                    //Console.WriteLine($"Control event {ctrlType} cancelled.");
                    // Return true to indicate the event is handled and should not be propagated.
                    return true;
                default:
                    return false;
            }
        }
    }
}