using System;
using System.IO;
using log4net;
using PSQLite;

namespace PrinterServices.Data
{
    public class PrinterServiceDb : SQLiteConnection
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PrinterServiceDb));

        private static PrinterServiceDb _instance;
        private static readonly object _lock = new object();

        public PrinterServiceDb(string databasePath) : base(databasePath)
        {
        }

        public static PrinterServiceDb GetInstance()
        {
            if (_instance != null)
            {
                return _instance;
            }

            lock (_lock)
            {
                if (_instance != null)
                {
                    return _instance;
                }

                string folder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string specificFolder = Path.Combine(folder, "QuipuNet");
                Directory.CreateDirectory(specificFolder);

                string dbPath = Path.Combine(specificFolder, "printerservice.db");
                _instance = new PrinterServiceDb(dbPath);

                // Habilitar WAL mode: permite lecturas concurrentes + 1 escritor simultáneo.
                // Sin esto, accesos simultáneos desde hilos gRPC/StatusMonitor/PrintWorker
                // causan deadlock o "database is locked".
                _instance.Execute("PRAGMA journal_mode=WAL");
                // busy_timeout: si otro hilo tiene el lock, esperar hasta 5s antes de fallar
                _instance.Execute("PRAGMA busy_timeout=5000");

                _instance.CreateTables();

                Log.InfoFormat("[DB] Base de datos creada/abierta en: {0}", dbPath);
                return _instance;
            }
        }

        private void CreateTables()
        {
            CreateTable<Models.PrintJobEntity>();
            CreateTable<Models.PrintLogEntity>();
            CreateTable<Models.PrinterEntity>();
            CreateTable<Models.NotificationEntity>();
            CreateTable<Models.NetworkConfigEntity>();
            CreateTable<Models.ConfigSettingEntity>();

            // Crear índices adicionales
            try
            {
                Execute("CREATE INDEX IF NOT EXISTS idx_jobs_estado ON print_jobs(estado)");
                Execute("CREATE INDEX IF NOT EXISTS idx_jobs_impresora ON print_jobs(impresora_id)");
                Execute("CREATE INDEX IF NOT EXISTS idx_notif_device ON notifications(device_id, entregada)");
                Execute("CREATE INDEX IF NOT EXISTS idx_log_fecha ON print_log(fecha)");
                Execute("CREATE INDEX IF NOT EXISTS idx_printers_mac ON printers(mac_address)");
            }
            catch (Exception ex)
            {
                Log.Warn("[DB] Error creando índices (pueden existir ya): " + ex.Message);
            }

            Log.Info("[DB] Tablas e índices verificados");
        }

        public new void Close()
        {
            lock (_lock)
            {
                base.Close();
                _instance = null;
            }
        }
    }
}
