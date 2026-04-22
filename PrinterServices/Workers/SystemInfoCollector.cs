using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using log4net;

namespace PrinterServices.Workers
{
    /// <summary>
    /// SERVICIO: Recopilacion de informacion del sistema/terminal.
    /// PROPOSITO: Proveer hostname, OS, CPU, RAM, disco al dashboard.
    /// PRINCIPIO SRP: Solo recopila info, no la persiste ni la muestra.
    /// Cache: CPU y OS son permanentes, RAM cada 60s, disco cada 1 hora.
    /// </summary>
    public static class SystemInfoCollector
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(SystemInfoCollector));

        // ─── Cache permanente (no cambian durante la vida del proceso) ───
        private static string _cpuName;
        private static string _osVersion;
        private static long _totalRamMb;

        // ─── Cache temporal: RAM disponible (60s) ───
        private static long _availableRamMb;
        private static DateTime _lastRamCheck = DateTime.MinValue;
        private static readonly object _ramLock = new object();

        // ─── Cache temporal: Disco (1 hora) ───
        private static long _diskTotalGb;
        private static long _diskFreeGb;
        private static string _diskDrive;
        private static DateTime _lastDiskCheck = DateTime.MinValue;
        private static readonly object _diskLock = new object();

        /// <summary>
        /// Nombre del procesador. Cache permanente (primera llamada usa WMI).
        /// </summary>
        public static string GetCpuName()
        {
            if (_cpuName != null) return _cpuName;

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        _cpuName = obj["Name"]?.ToString()?.Trim() ?? "Desconocido";
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[SYSINFO] Error obteniendo CPU: " + ex.Message);
                _cpuName = "No disponible";
            }

            return _cpuName;
        }

        /// <summary>
        /// Version del sistema operativo. Cache permanente.
        /// </summary>
        public static string GetOsVersion()
        {
            if (_osVersion != null) return _osVersion;
            _osVersion = Environment.OSVersion.ToString();
            return _osVersion;
        }

        /// <summary>
        /// RAM total del sistema en MB. Cache permanente (WMI primera llamada).
        /// </summary>
        public static long GetTotalRamMb()
        {
            if (_totalRamMb > 0) return _totalRamMb;

            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        // TotalVisibleMemorySize viene en KB
                        var totalKb = Convert.ToInt64(obj["TotalVisibleMemorySize"]);
                        _totalRamMb = totalKb / 1024;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[SYSINFO] Error obteniendo RAM total: " + ex.Message);
                _totalRamMb = 0;
            }

            return _totalRamMb;
        }

        /// <summary>
        /// RAM disponible del sistema en MB. Cache 60 segundos (WMI).
        /// </summary>
        public static long GetAvailableRamMb()
        {
            lock (_ramLock)
            {
                if ((DateTime.Now - _lastRamCheck).TotalSeconds < 60)
                    return _availableRamMb;

                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT FreePhysicalMemory FROM Win32_OperatingSystem"))
                    {
                        foreach (ManagementObject obj in searcher.Get())
                        {
                            var freeKb = Convert.ToInt64(obj["FreePhysicalMemory"]);
                            _availableRamMb = freeKb / 1024;
                            break;
                        }
                    }
                    _lastRamCheck = DateTime.Now;
                }
                catch (Exception ex)
                {
                    Log.Warn("[SYSINFO] Error obteniendo RAM disponible: " + ex.Message);
                }

                return _availableRamMb;
            }
        }

        /// <summary>
        /// Espacio total del disco del sistema en GB. Cache 1 hora.
        /// </summary>
        public static long GetDiskTotalGb()
        {
            RefreshDiskIfNeeded();
            return _diskTotalGb;
        }

        /// <summary>
        /// Espacio libre del disco del sistema en GB. Cache 1 hora.
        /// </summary>
        public static long GetDiskFreeGb()
        {
            RefreshDiskIfNeeded();
            return _diskFreeGb;
        }

        /// <summary>
        /// Letra del disco del sistema (ej: "C:\"). Cache permanente.
        /// </summary>
        public static string GetDiskDrive()
        {
            RefreshDiskIfNeeded();
            return _diskDrive;
        }

        private static void RefreshDiskIfNeeded()
        {
            lock (_diskLock)
            {
                if ((DateTime.Now - _lastDiskCheck).TotalMinutes < 60)
                    return;

                try
                {
                    // Disco donde esta instalado el sistema
                    string systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
                    var driveInfo = new DriveInfo(systemDrive);

                    _diskDrive = systemDrive;
                    _diskTotalGb = driveInfo.TotalSize / (1024L * 1024 * 1024);
                    _diskFreeGb = driveInfo.AvailableFreeSpace / (1024L * 1024 * 1024);
                    _lastDiskCheck = DateTime.Now;
                }
                catch (Exception ex)
                {
                    Log.Warn("[SYSINFO] Error obteniendo info disco: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Recopila toda la info del sistema en un solo objeto anonimo.
        /// RAZON: Evitar multiples llamadas WMI desde el dashboard.
        /// </summary>
        public static object CollectAll(DateTime serviceStartTime)
        {
            var uptime = DateTime.Now - serviceStartTime;

            return new
            {
                hostname = Environment.MachineName,
                osVersion = GetOsVersion(),
                cpu = GetCpuName(),
                ramTotalMb = GetTotalRamMb(),
                ramAvailableMb = GetAvailableRamMb(),
                diskDrive = GetDiskDrive(),
                diskTotalGb = GetDiskTotalGb(),
                diskFreeGb = GetDiskFreeGb(),
                dotNetVersion = Environment.Version.ToString(),
                serviceUptime = string.Format("{0}d {1}h {2}m {3}s", uptime.Days, uptime.Hours, uptime.Minutes, uptime.Seconds),
                serviceUptimeSeconds = (int)uptime.TotalSeconds,
                serviceStartTime = serviceStartTime.ToString("o"),
                processId = Process.GetCurrentProcess().Id
            };
        }
    }
}
