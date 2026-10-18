using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace ViviTune.Windows
{
    public class Runner : ISingBox, IDisposable
    {
        readonly string _exe;
        readonly IntPtr _job;
        Process? _p;

        public event Action<string>? Line;
        public event Action? Exited;

        public bool Running { get { return _p != null && !_p.HasExited; } }

        public Runner(string singBoxExe)
        {
            _exe = singBoxExe;
            _job = CreateJob();
        }

        public void Start(string configPath)
        {
            if (!File.Exists(_exe)) throw new FileNotFoundException("Не найден sing-box.exe рядом с программой", _exe);
            var psi = new ProcessStartInfo(_exe, "run -c \"" + configPath + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(configPath)
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            DataReceivedEventHandler onLine = (s, e) => { if (e.Data != null) Line?.Invoke(e.Data); };
            p.OutputDataReceived += onLine;
            p.ErrorDataReceived += onLine;
            p.Exited += (s, e) => Exited?.Invoke();
            if (_p != null) _p.Dispose(); // предыдущий запуск уже завершён (UI не даёт стартовать дважды)
            p.Start();
            AssignProcessToJobObject(_job, p.Handle); // sing-box умрёт вместе с нами, даже если нас убьют
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _p = p;
        }

        public void Stop()
        {
            if (!Running) return;
            try { _p!.Kill(); _p.WaitForExit(3000); }
            catch (Exception e) when (e is InvalidOperationException || e is System.ComponentModel.Win32Exception) { } // уже завершился или не дали убить
        }

        public void Dispose()
        {
            Stop();
            CloseHandle(_job); // KILL_ON_JOB_CLOSE
        }

        static IntPtr CreateJob()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                SetInformationJobObject(job, 9, ptr, (uint)size); // JobObjectExtendedLimitInformation
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return job;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateJobObject(IntPtr attrs, string? name);
        [DllImport("kernel32.dll")]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);
        [DllImport("kernel32.dll")]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoCounters;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
