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
                // ExecuteScalar porque PRAGMA journal_mode devuelve una fila con el modo actual
                var walResult = _instance.ExecuteScalar<string>("PRAGMA journal_mode=WAL");
                Log.InfoFormat("[DB] WAL mode: {0}", walResult);
                // busy_timeout: si otro hilo tiene el lock, esperar hasta 5s antes de fallar
                var busyResult = _instance.ExecuteScalar<string>("PRAGMA busy_timeout=5000");
                Log.InfoFormat("[DB] busy_timeout: {0}", busyResult);

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
            CreateTable<Models.IpChangeNotificationEntity>();

            // Fase 23: Tabla de callbacks de estado de jobs para QuipuNetX
            CreateTable<Models.JobStatusCallbackEntity>();

            // Fase 8: Tablas de monitoreo de red y latencias
            CreateTable<Models.NetworkSnapshotEntity>();
            CreateTable<Models.NetworkCurrentEntity>();
            CreateTable<Models.NetworkAlertEntity>();
            CreateTable<Models.PrintLatencyLogEntity>();
            CreateTable<Models.PrinterLatencyStatsEntity>();
            CreateTable<Models.NetworkLatencyBaselineEntity>();

            // Migración: agregar columnas si no existen
            try
            {
                // Verificar columnas existentes consultando pragma
                var columns = Query<dynamic>("PRAGMA table_info(printers)");
                bool hasDisponibleColumn = false;
                bool hasSnmpEnabledColumn = false;
                bool hasSnmpCommunityColumn = false;
                
                foreach (var col in columns)
                {
                    var colDict = col as System.Collections.Generic.IDictionary<string, object>;
                    if (colDict != null && colDict.ContainsKey("name"))
                    {
                        string colName = colDict["name"].ToString();
                        if (colName == "disponible_para_imprimir")
                            hasDisponibleColumn = true;
                        else if (colName == "snmp_enabled")
                            hasSnmpEnabledColumn = true;
                        else if (colName == "snmp_community")
                            hasSnmpCommunityColumn = true;
                    }
                }

                // Agregar columna disponible_para_imprimir si no existe
                if (!hasDisponibleColumn)
                {
                    Execute("ALTER TABLE printers ADD COLUMN disponible_para_imprimir INTEGER DEFAULT 0");
                    Log.Info("[DB] Columna 'disponible_para_imprimir' agregada a tabla printers");
                }

                // Agregar columna snmp_enabled si no existe
                if (!hasSnmpEnabledColumn)
                {
                    Execute("ALTER TABLE printers ADD COLUMN snmp_enabled INTEGER DEFAULT 0");
                    Log.Info("[DB] Columna 'snmp_enabled' agregada a tabla printers");
                }

                // Agregar columna snmp_community si no existe
                if (!hasSnmpCommunityColumn)
                {
                    Execute("ALTER TABLE printers ADD COLUMN snmp_community TEXT DEFAULT 'public'");
                    Log.Info("[DB] Columna 'snmp_community' agregada a tabla printers");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[DB] Error en migraciones de columnas (printers): " + ex.Message);
            }

            // Migración: agregar columna ip_servidor a print_jobs si no existe
            // RAZÓN: Nuevo campo para guardar la IP del servidor QuipuNet (siempre se notifica aquí)
            // La columna ip_origen ya existe y ahora guarda la IP del cliente real que originó la impresión
            try
            {
                var jobColumns = Query<dynamic>("PRAGMA table_info(print_jobs)");  // Consultar columnas de print_jobs
                bool hasIpServidor = false;                                        // Flag para verificar si existe
                foreach (var col in jobColumns)                                    // Iterar cada columna
                {
                    var colDict = col as System.Collections.Generic.IDictionary<string, object>; // Cast a diccionario
                    if (colDict != null && colDict.ContainsKey("name"))            // Verificar que tenga campo name
                    {
                        string colName = colDict["name"].ToString();              // Obtener nombre de columna
                        if (colName == "ip_servidor")                              // Verificar si es ip_servidor
                            hasIpServidor = true;                                  // Marcar como encontrada
                    }
                }
                if (!hasIpServidor)                                                // Si la columna no existe
                {
                    Execute("ALTER TABLE print_jobs ADD COLUMN ip_servidor TEXT DEFAULT ''"); // Agregar columna
                    Log.Info("[DB] Columna 'ip_servidor' agregada a tabla print_jobs");      // Log de migración
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[DB] Error en migraciones de columnas (print_jobs): " + ex.Message); // Log de error
            }

            // Fase 9: Migración — agregar columna es_cliente a job_status_callbacks si no existe
            // RAZÓN: RetryCallbackAsync necesita saber si el destino es cliente (:8083) o servidor (:8081)
            try
            {
                var cbColumns = Query<dynamic>("PRAGMA table_info(job_status_callbacks)"); // Consultar columnas
                bool hasEsCliente = false;                                                 // Flag para verificar
                foreach (var col in cbColumns)                                              // Iterar columnas
                {
                    var colDict = col as System.Collections.Generic.IDictionary<string, object>;
                    if (colDict != null && colDict.ContainsKey("name"))
                    {
                        if (colDict["name"].ToString() == "es_cliente")                    // Verificar si existe
                            hasEsCliente = true;
                    }
                }
                if (!hasEsCliente)                                                         // Si no existe
                {
                    Execute("ALTER TABLE job_status_callbacks ADD COLUMN es_cliente INTEGER DEFAULT 0"); // Agregar columna
                    Log.Info("[DB] Columna 'es_cliente' agregada a tabla job_status_callbacks");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[DB] Error en migración es_cliente (job_status_callbacks): " + ex.Message);
            }

            // Crear índices adicionales
            try
            {
                Execute("CREATE INDEX IF NOT EXISTS idx_jobs_estado ON print_jobs(estado)");
                Execute("CREATE INDEX IF NOT EXISTS idx_jobs_impresora ON print_jobs(impresora_id)");
                Execute("CREATE INDEX IF NOT EXISTS idx_notif_device ON notifications(device_id, entregada)");
                Execute("CREATE INDEX IF NOT EXISTS idx_log_fecha ON print_log(fecha)");
                Execute("CREATE INDEX IF NOT EXISTS idx_printers_mac ON printers(mac_address)");
                Execute("CREATE INDEX IF NOT EXISTS idx_notif_estado ON notificacionescambiosip(estado)");
                Execute("CREATE INDEX IF NOT EXISTS idx_notif_mac ON notificacionescambiosip(mac_address)");

                // Fase 23: Índices para callbacks de estado de jobs
                Execute("CREATE INDEX IF NOT EXISTS idx_jscb_estado ON job_status_callbacks(estado_envio)");
                Execute("CREATE INDEX IF NOT EXISTS idx_jscb_jobid ON job_status_callbacks(job_id)");

                // Fase 8: Índices para network snapshots y latencias
                Execute("CREATE INDEX IF NOT EXISTS idx_snapshots_gateway ON network_snapshots(gateway_mac)");
                Execute("CREATE INDEX IF NOT EXISTS idx_snapshots_network ON network_snapshots(network_id)");
                Execute("CREATE INDEX IF NOT EXISTS idx_alerts_notified ON network_alerts(notified, detected_at)");
                Execute("CREATE INDEX IF NOT EXISTS idx_latency_impresora ON print_latency_log(impresora_id, created_at)");
                Execute("CREATE INDEX IF NOT EXISTS idx_latency_success ON print_latency_log(success, created_at)");
                Execute("CREATE INDEX IF NOT EXISTS idx_latency_total ON print_latency_log(total_print_ms)");
                Execute("CREATE INDEX IF NOT EXISTS idx_network_baseline ON network_latency_baseline(gateway_mac, checked_at)");
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
