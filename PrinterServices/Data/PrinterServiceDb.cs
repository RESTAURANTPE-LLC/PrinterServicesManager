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

                string folder = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
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

            // Tabla de log de transiciones de estado de impresoras (ONLINE/OFFLINE)
            CreateTable<Models.PrinterStatusLogEntity>();

            // Anti-Duplicación: Hash de trabajos REALMENTE impresos físicamente
            CreateTable<Models.PrintedJobHashEntity>();
            try
            {
                Execute("CREATE INDEX IF NOT EXISTS idx_printed_hash ON printed_jobs_hash(contenido_hash)");
                Execute("CREATE INDEX IF NOT EXISTS idx_printed_impresora ON printed_jobs_hash(impresora_id, fecha_impresion)");
            }
            catch { }

            // Fase 8: Tablas de monitoreo de red y latencias
            CreateTable<Models.NetworkSnapshotEntity>();
            CreateTable<Models.NetworkCurrentEntity>();
            CreateTable<Models.NetworkAlertEntity>();
            CreateTable<Models.PrintLatencyLogEntity>();
            CreateTable<Models.PrinterLatencyStatsEntity>();
            CreateTable<Models.NetworkLatencyBaselineEntity>();

            // Health Dashboard: tablas nuevas (idempotente via CreateTable)
            CreateTable<Models.NetworkSpeedLogEntity>();
            try { Execute("CREATE INDEX IF NOT EXISTS idx_speed_measured ON network_speed_log(measured_at)"); } catch { }
            CreateTable<Models.DeviceOnNetworkEntity>();
            try { Execute("CREATE UNIQUE INDEX IF NOT EXISTS idx_device_mac ON devices_on_network(mac_address)"); Execute("CREATE INDEX IF NOT EXISTS idx_device_online ON devices_on_network(is_online)"); } catch { }

            // Diagnóstico de falsos positivos: intentos de impresión y transcript de comandos
            // RAZÓN: cada intento guarda su propio diagnóstico independiente (no se pisa en retry);
            // el transcript graba byte-por-byte qué se envió a la impresora y qué respondió,
            // para poder diagnosticar DONE que no imprimió físicamente.
            CreateTable<Models.PrintJobAttemptEntity>();
            CreateTable<Models.PrintJobAttemptCommandEntity>();
            // Transcript del ÚLTIMO probe de capacidades por impresora.
            // A diferencia del transcript por intento, acá guardamos sólo el último probe
            // (se borran las filas viejas antes de insertar) para que la BD no crezca.
            CreateTable<Models.PrinterProbeLogEntity>();
            // Metadata de bitmaps generados (cuando GuardarBitmapsGenerados=1).
            // El archivo JPG vive en %ProgramData%\QuipuNet\bitmaps\; acá sólo el registro
            // con width/height/dark_ratio/bytes para diagnóstico rápido.
            CreateTable<Models.PrintJobBitmapEntity>();
            try
            {
                Execute("CREATE INDEX IF NOT EXISTS idx_attempts_job ON print_job_attempts(job_id)");
                Execute("CREATE INDEX IF NOT EXISTS idx_attempts_start ON print_job_attempts(started_at)");
                Execute("CREATE INDEX IF NOT EXISTS idx_attempts_outcome ON print_job_attempts(outcome)");
                Execute("CREATE INDEX IF NOT EXISTS idx_attempts_suspicious ON print_job_attempts(suspicious_fast_send)");
                Execute("CREATE INDEX IF NOT EXISTS idx_cmd_attempt ON print_job_attempt_commands(attempt_id, sequence_num)");
                Execute("CREATE INDEX IF NOT EXISTS idx_cmd_ts_utc ON print_job_attempt_commands(timestamp_utc)");
                Execute("CREATE INDEX IF NOT EXISTS idx_probe_log_printer ON printer_capability_probe_log(impresora_id, sequence_num)");
                Execute("CREATE INDEX IF NOT EXISTS idx_bitmap_job ON print_job_bitmaps(job_id)");
                Execute("CREATE INDEX IF NOT EXISTS idx_bitmap_generated ON print_job_bitmaps(generated_at_utc)");
            }
            catch (Exception ex)
            {
                Log.Warn("[DB] Error creando índices de print_job_attempts/commands/probe_log: " + ex.Message);
            }

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

                // ─── Migración USB: columnas para identificación de impresoras USB ───
                bool hasTipoConexion = false;
                bool hasUsbUniqueKey = false;
                bool hasUsbDevicePath = false;
                bool hasUsbFriendlyName = false;

                foreach (var col in columns)
                {
                    var colDict2 = col as System.Collections.Generic.IDictionary<string, object>;
                    if (colDict2 != null && colDict2.ContainsKey("name"))
                    {
                        string colName2 = colDict2["name"].ToString();
                        if (colName2 == "tipo_conexion") hasTipoConexion = true;
                        else if (colName2 == "usb_unique_key") hasUsbUniqueKey = true;
                        else if (colName2 == "usb_device_path") hasUsbDevicePath = true;
                        else if (colName2 == "usb_friendly_name") hasUsbFriendlyName = true;
                    }
                }

                if (!hasTipoConexion)
                {
                    Execute("ALTER TABLE printers ADD COLUMN tipo_conexion TEXT DEFAULT 'RED'");
                    Log.Info("[DB] Columna 'tipo_conexion' agregada a tabla printers");
                }
                if (!hasUsbUniqueKey)
                {
                    Execute("ALTER TABLE printers ADD COLUMN usb_unique_key TEXT");
                    Log.Info("[DB] Columna 'usb_unique_key' agregada a tabla printers");
                }
                if (!hasUsbDevicePath)
                {
                    Execute("ALTER TABLE printers ADD COLUMN usb_device_path TEXT");
                    Log.Info("[DB] Columna 'usb_device_path' agregada a tabla printers");
                }
                if (!hasUsbFriendlyName)
                {
                    Execute("ALTER TABLE printers ADD COLUMN usb_friendly_name TEXT");
                    Log.Info("[DB] Columna 'usb_friendly_name' agregada a tabla printers");
                }

                // ─── Migración Capabilities: 12 columnas para el PrinterCapabilityProbe ───
                // RAZÓN: saber por impresora si soporta GS ( H fn48 (confirmación de procesamiento
                // real vs. solo recepción TCP). Sin esto, PrintWorker no puede elegir el método
                // de confirmación adecuado y queda expuesto a falsos positivos.
                bool hasCapProfile = false, hasSupDleEot = false, hasSupDleEotBits = false;
                bool hasSupAsb = false, hasSupProcessId = false;
                bool hasFirmwareRaw = false, hasFirmwareParsed = false;
                bool hasCapDetectedAt = false, hasCapProbeCount = false;
                bool hasCapProbeDuration = false, hasCapLastError = false, hasCapLastTrigger = false;

                foreach (var col in columns)
                {
                    var colDict3 = col as System.Collections.Generic.IDictionary<string, object>;
                    if (colDict3 == null || !colDict3.ContainsKey("name")) continue;
                    string colName3 = colDict3["name"].ToString();
                    if (colName3 == "capabilities_profile") hasCapProfile = true;
                    else if (colName3 == "supports_dle_eot") hasSupDleEot = true;
                    else if (colName3 == "supports_dle_eot_bits") hasSupDleEotBits = true;
                    else if (colName3 == "supports_asb") hasSupAsb = true;
                    else if (colName3 == "supports_process_id_response") hasSupProcessId = true;
                    else if (colName3 == "firmware_raw") hasFirmwareRaw = true;
                    else if (colName3 == "firmware_parsed") hasFirmwareParsed = true;
                    else if (colName3 == "capabilities_detected_at") hasCapDetectedAt = true;
                    else if (colName3 == "capabilities_probe_count") hasCapProbeCount = true;
                    else if (colName3 == "capabilities_probe_duration_ms") hasCapProbeDuration = true;
                    else if (colName3 == "capabilities_last_error") hasCapLastError = true;
                    else if (colName3 == "capabilities_last_trigger") hasCapLastTrigger = true;
                }

                if (!hasCapProfile)
                {
                    Execute("ALTER TABLE printers ADD COLUMN capabilities_profile TEXT DEFAULT 'unknown'");
                    Log.Info("[DB] Columna 'capabilities_profile' agregada a tabla printers");
                }
                if (!hasSupDleEot)
                {
                    Execute("ALTER TABLE printers ADD COLUMN supports_dle_eot INTEGER DEFAULT 1");
                    Log.Info("[DB] Columna 'supports_dle_eot' agregada a tabla printers");
                }
                if (!hasSupDleEotBits)
                {
                    Execute("ALTER TABLE printers ADD COLUMN supports_dle_eot_bits TEXT");
                    Log.Info("[DB] Columna 'supports_dle_eot_bits' agregada a tabla printers");
                }
                if (!hasSupAsb)
                {
                    Execute("ALTER TABLE printers ADD COLUMN supports_asb INTEGER DEFAULT 0");
                    Log.Info("[DB] Columna 'supports_asb' agregada a tabla printers");
                }
                if (!hasSupProcessId)
                {
                    Execute("ALTER TABLE printers ADD COLUMN supports_process_id_response INTEGER DEFAULT 0");
                    Log.Info("[DB] Columna 'supports_process_id_response' agregada a tabla printers");
                }
                if (!hasFirmwareRaw)
                {
                    Execute("ALTER TABLE printers ADD COLUMN firmware_raw TEXT");
                    Log.Info("[DB] Columna 'firmware_raw' agregada a tabla printers");
                }
                if (!hasFirmwareParsed)
                {
                    Execute("ALTER TABLE printers ADD COLUMN firmware_parsed TEXT");
                    Log.Info("[DB] Columna 'firmware_parsed' agregada a tabla printers");
                }
                if (!hasCapDetectedAt)
                {
                    Execute("ALTER TABLE printers ADD COLUMN capabilities_detected_at TEXT");
                    Log.Info("[DB] Columna 'capabilities_detected_at' agregada a tabla printers");
                }
                if (!hasCapProbeCount)
                {
                    Execute("ALTER TABLE printers ADD COLUMN capabilities_probe_count INTEGER DEFAULT 0");
                    Log.Info("[DB] Columna 'capabilities_probe_count' agregada a tabla printers");
                }
                if (!hasCapProbeDuration)
                {
                    Execute("ALTER TABLE printers ADD COLUMN capabilities_probe_duration_ms INTEGER");
                    Log.Info("[DB] Columna 'capabilities_probe_duration_ms' agregada a tabla printers");
                }
                if (!hasCapLastError)
                {
                    Execute("ALTER TABLE printers ADD COLUMN capabilities_last_error TEXT");
                    Log.Info("[DB] Columna 'capabilities_last_error' agregada a tabla printers");
                }
                if (!hasCapLastTrigger)
                {
                    Execute("ALTER TABLE printers ADD COLUMN capabilities_last_trigger TEXT");
                    Log.Info("[DB] Columna 'capabilities_last_trigger' agregada a tabla printers");
                }
            }
            catch (Exception ex)
            {
                LogMigracion("migraciones de columnas (printers)", ex);
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
                LogMigracion("migraciones de columnas (print_jobs)", ex);
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
                LogMigracion("migración es_cliente (job_status_callbacks)", ex);
            }

            // Migración: agregar columna printer_response a print_jobs si no existe
            // RAZÓN: Guardar respuesta DLE EOT raw en cada intento de impresión para diagnóstico
            try
            {
                var prCols = Query<dynamic>("PRAGMA table_info(print_jobs)");
                bool hasPrinterResponse = false;
                foreach (var col in prCols)
                {
                    var colDict = col as System.Collections.Generic.IDictionary<string, object>;
                    if (colDict != null && colDict.ContainsKey("name") && colDict["name"]?.ToString() == "printer_response")
                    {
                        hasPrinterResponse = true;
                        break;
                    }
                }
                if (!hasPrinterResponse)
                {
                    Execute("ALTER TABLE print_jobs ADD COLUMN printer_response TEXT");
                    Log.Info("[DB] Columna 'printer_response' agregada a tabla print_jobs");
                }
            }
            catch (Exception ex)
            {
                LogMigracion("migración printer_response (print_jobs)", ex);
            }

            // Migración: agregar columnas del diseñador de comandas a print_jobs
            try
            {
                var disCols = Query<dynamic>("PRAGMA table_info(print_jobs)");
                bool hasUtilizarDisenador = false;
                bool hasDocumentoJson = false;
                foreach (var col in disCols)
                {
                    var colDict = col as System.Collections.Generic.IDictionary<string, object>;
                    if (colDict != null && colDict.ContainsKey("name"))
                    {
                        string colName = colDict["name"].ToString();
                        if (colName == "utilizar_disenador_comandas") hasUtilizarDisenador = true;
                        else if (colName == "documento_json") hasDocumentoJson = true;
                    }
                }
                if (!hasUtilizarDisenador)
                {
                    Execute("ALTER TABLE print_jobs ADD COLUMN utilizar_disenador_comandas INTEGER DEFAULT 0");
                    Log.Info("[DB] Columna 'utilizar_disenador_comandas' agregada a tabla print_jobs");
                }
                if (!hasDocumentoJson)
                {
                    Execute("ALTER TABLE print_jobs ADD COLUMN documento_json TEXT");
                    Log.Info("[DB] Columna 'documento_json' agregada a tabla print_jobs");
                }
            }
            catch (Exception ex)
            {
                LogMigracion("migración diseñador comandas (print_jobs)", ex);
            }

            // Migración: columnas printer_response + printer_response_legend en printer_status_log
            // RAZÓN: Cada transición ahora persiste el raw DLE EOT y su decodificación humana
            // para diagnóstico (qué dijo la impresora exactamente al momento del evento).
            try
            {
                var statusLogCols = Query<dynamic>("PRAGMA table_info(printer_status_log)");
                bool hasPrinterResponse = false;
                bool hasPrinterResponseLegend = false;
                foreach (var col in statusLogCols)
                {
                    var colDict = col as System.Collections.Generic.IDictionary<string, object>;
                    if (colDict != null && colDict.ContainsKey("name"))
                    {
                        string colName = colDict["name"].ToString();
                        if (colName == "printer_response") hasPrinterResponse = true;
                        else if (colName == "printer_response_legend") hasPrinterResponseLegend = true;
                    }
                }
                if (!hasPrinterResponse)
                {
                    Execute("ALTER TABLE printer_status_log ADD COLUMN printer_response TEXT");
                    Log.Info("[DB] Columna 'printer_response' agregada a tabla printer_status_log");
                }
                if (!hasPrinterResponseLegend)
                {
                    Execute("ALTER TABLE printer_status_log ADD COLUMN printer_response_legend TEXT");
                    Log.Info("[DB] Columna 'printer_response_legend' agregada a tabla printer_status_log");
                }
            }
            catch (Exception ex)
            {
                LogMigracion("migración printer_response (printer_status_log)", ex);
            }

            // Crear índices adicionales
            try
            {
                Execute("CREATE INDEX IF NOT EXISTS idx_jobs_estado ON print_jobs(estado)");
                Execute("CREATE INDEX IF NOT EXISTS idx_jobs_impresora ON print_jobs(impresora_id)");
                Execute("CREATE INDEX IF NOT EXISTS idx_notif_device ON notifications(device_id, entregada)");
                Execute("CREATE INDEX IF NOT EXISTS idx_log_fecha ON print_log(fecha)");
                // UNIQUE index: una MAC física = una sola impresora en BD.
                // SQLite permite múltiples NULLs en UNIQUE, así que impresoras sin MAC no conflictúan.
                // Migración: eliminar índice viejo no-unique si existe, crear el nuevo UNIQUE.
                try { Execute("DROP INDEX IF EXISTS idx_printers_mac"); } catch { }
                Execute("CREATE UNIQUE INDEX IF NOT EXISTS idx_printers_mac_unique ON printers(mac_address)");
                Execute("CREATE INDEX IF NOT EXISTS idx_printer_status_log_fecha ON printer_status_log(fecha)");
                Execute("CREATE INDEX IF NOT EXISTS idx_printer_status_log_imp ON printer_status_log(impresora_id, fecha)");
                // Índice UNIQUE para usb_unique_key: una identidad USB = una sola impresora
                // SQLite permite múltiples NULLs en UNIQUE, así que impresoras de RED no conflictúan
                Execute("CREATE UNIQUE INDEX IF NOT EXISTS idx_printers_usb_key ON printers(usb_unique_key)");
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

        /// <summary>
        /// Loguea la excepción de una migración de columna distinguiendo el caso
        /// "la columna ya existe" (normal en todo arranque posterior al primero)
        /// del resto (error real que merece atención).
        /// </summary>
        private static void LogMigracion(string contextoMigracion, Exception ex)
        {
            string mensaje = ex?.Message ?? "";
            bool yaExiste = mensaje.IndexOf("duplicate column", StringComparison.OrdinalIgnoreCase) >= 0
                         || mensaje.IndexOf("already exists", StringComparison.OrdinalIgnoreCase) >= 0;

            if (yaExiste)
            {
                // No es error — la migración ya se aplicó en un arranque anterior.
                Log.DebugFormat("[DB] {0}: ya aplicada anteriormente", contextoMigracion);
            }
            else
            {
                Log.WarnFormat("[DB] Error en {0}: {1}", contextoMigracion, mensaje);
            }
        }
    }
}

namespace PrinterServices.Data.Models
{
    [PSQLite.Table("network_speed_log")]
    public class NetworkSpeedLogEntity
    {
        [PSQLite.PrimaryKey, PSQLite.AutoIncrement, PSQLite.Column("id")]
        public int Id { get; set; }
        [PSQLite.Column("download_speed_kbps")]
        public double DownloadSpeedKbps { get; set; }
        [PSQLite.Column("latency_ms")]
        public int LatencyMs { get; set; }
        [PSQLite.Column("gateway_ip")]
        public string GatewayIp { get; set; }
        [PSQLite.Column("network_id")]
        public string NetworkId { get; set; }
        [PSQLite.Column("measured_at")]
        public string MeasuredAt { get; set; }
    }

    [PSQLite.Table("devices_on_network")]
    public class DeviceOnNetworkEntity
    {
        [PSQLite.PrimaryKey, PSQLite.AutoIncrement, PSQLite.Column("id")]
        public int Id { get; set; }
        [PSQLite.Column("ip_address")]
        public string IpAddress { get; set; }
        [PSQLite.Column("mac_address")]
        public string MacAddress { get; set; }
        [PSQLite.Column("hostname")]
        public string Hostname { get; set; }
        [PSQLite.Column("vendor")]
        public string Vendor { get; set; }
        [PSQLite.Column("device_type")]
        public string DeviceType { get; set; }
        [PSQLite.Column("first_seen_at")]
        public string FirstSeenAt { get; set; }
        [PSQLite.Column("last_seen_at")]
        public string LastSeenAt { get; set; }
        [PSQLite.Column("is_online")]
        public int IsOnline { get; set; }
        [PSQLite.Column("network_id")]
        public string NetworkId { get; set; }
        [PSQLite.Column("gateway_mac")]
        public string GatewayMac { get; set; }
    }
}
