using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using log4net;
using PrinterServices.Data;
using PrinterServices.Data.Models;

namespace PrinterServices.Config
{
    public class ConfigManager
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(ConfigManager));

        private static ConfigManager _instance;
        private static readonly object _lock = new object();

        private readonly PrinterServiceDb _db;
        private readonly ConcurrentDictionary<string, string> _cache;

        private ConfigManager(PrinterServiceDb db)
        {
            _db = db;
            _cache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public static ConfigManager GetInstance(PrinterServiceDb db)
        {
            if (_instance != null) return _instance;

            lock (_lock)
            {
                if (_instance != null) return _instance;

                _instance = new ConfigManager(db);
                _instance.SeedDefaults();
                _instance.LoadCache();

                Log.Info("[CONFIG] ConfigManager inicializado con " + _instance._cache.Count + " settings");
                return _instance;
            }
        }

        public static ConfigManager Instance
        {
            get
            {
                if (_instance == null)
                    throw new InvalidOperationException("ConfigManager no ha sido inicializado. Llame GetInstance(db) primero.");
                return _instance;
            }
        }

        // ── Read ──

        public string GetString(string key, string defaultValue = null)
        {
            string value;
            if (_cache.TryGetValue(key, out value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
            return defaultValue;
        }

        public int GetInt(string key, int defaultValue)
        {
            string value = GetString(key);
            int result;
            if (!string.IsNullOrEmpty(value) && int.TryParse(value, out result))
            {
                return result;
            }
            return defaultValue;
        }

        public bool GetBool(string key, bool defaultValue)
        {
            string value = GetString(key);
            if (string.IsNullOrEmpty(value)) return defaultValue;

            if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;

            return defaultValue;
        }

        // ── Write ──

        public void Set(string key, string value)
        {
            _cache.AddOrUpdate(key, value, (k, old) => value);

            try
            {
                var entity = _db.Query<ConfigSettingEntity>(
                    "SELECT * FROM config_settings WHERE key = ?", key).FirstOrDefault();

                if (entity != null)
                {
                    entity.Value = value;
                    entity.UpdatedAt = DateTime.Now.ToString("o");
                    _db.Update(entity);
                }
                else
                {
                    _db.Insert(new ConfigSettingEntity
                    {
                        Key = key,
                        Value = value,
                        DefaultValue = value,
                        Category = "custom",
                        ValueType = "string",
                        UpdatedAt = DateTime.Now.ToString("o")
                    });
                }

                Log.DebugFormat("[CONFIG] {0} = {1}", key, value);
            }
            catch (Exception ex)
            {
                Log.Error("[CONFIG] Error guardando " + key + ": " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Actualiza la entidad completa en BD (incluye MinValue, MaxValue, etc.)
        /// </summary>
        public void UpdateEntity(ConfigSettingEntity entity)
        {
            try
            {
                entity.UpdatedAt = DateTime.Now.ToString("o");
                _db.Update(entity);
            }
            catch (Exception ex)
            {
                Log.Error("[CONFIG] Error actualizando entidad " + entity.Key + ": " + ex.Message, ex);
            }
        }

        public void SetInt(string key, int value)
        {
            Set(key, value.ToString());
        }

        public void SetBool(string key, bool value)
        {
            Set(key, value ? "1" : "0");
        }

        // ── Bulk ──

        public List<ConfigSettingEntity> GetAll()
        {
            try
            {
                return _db.Query<ConfigSettingEntity>("SELECT * FROM config_settings ORDER BY category, key");
            }
            catch (Exception ex)
            {
                Log.Error("[CONFIG] Error obteniendo todos los settings: " + ex.Message);
                return new List<ConfigSettingEntity>();
            }
        }

        public List<ConfigSettingEntity> GetByCategory(string category)
        {
            try
            {
                return _db.Query<ConfigSettingEntity>(
                    "SELECT * FROM config_settings WHERE category = ? ORDER BY key", category);
            }
            catch (Exception ex)
            {
                Log.Error("[CONFIG] Error obteniendo settings de " + category + ": " + ex.Message);
                return new List<ConfigSettingEntity>();
            }
        }

        public void ResetToDefault(string key)
        {
            try
            {
                var entity = _db.Query<ConfigSettingEntity>(
                    "SELECT * FROM config_settings WHERE key = ?", key).FirstOrDefault();

                if (entity != null && !string.IsNullOrEmpty(entity.DefaultValue))
                {
                    entity.Value = entity.DefaultValue;
                    entity.UpdatedAt = DateTime.Now.ToString("o");
                    _db.Update(entity);
                    _cache.AddOrUpdate(key, entity.DefaultValue, (k, old) => entity.DefaultValue);
                    Log.InfoFormat("[CONFIG] {0} reseteado a default: {1}", key, entity.DefaultValue);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[CONFIG] Error reseteando " + key + ": " + ex.Message, ex);
            }
        }

        public void ResetAllToDefaults()
        {
            try
            {
                var all = _db.Query<ConfigSettingEntity>("SELECT * FROM config_settings");
                foreach (var entity in all)
                {
                    if (!string.IsNullOrEmpty(entity.DefaultValue))
                    {
                        entity.Value = entity.DefaultValue;
                        entity.UpdatedAt = DateTime.Now.ToString("o");
                        _db.Update(entity);
                        _cache.AddOrUpdate(entity.Key, entity.DefaultValue, (k, old) => entity.DefaultValue);
                    }
                }
                Log.Info("[CONFIG] Todos los settings reseteados a defaults");
            }
            catch (Exception ex)
            {
                Log.Error("[CONFIG] Error reseteando todos: " + ex.Message, ex);
            }
        }

        public void RefreshCache()
        {
            LoadCache();
            Log.Debug("[CONFIG] Cache refrescada");
        }

        // ── Internal ──

        private void LoadCache()
        {
            try
            {
                var all = _db.Query<ConfigSettingEntity>("SELECT * FROM config_settings");
                foreach (var entity in all)
                {
                    string effectiveValue = !string.IsNullOrEmpty(entity.Value) ? entity.Value : entity.DefaultValue;
                    if (!string.IsNullOrEmpty(effectiveValue))
                    {
                        _cache.AddOrUpdate(entity.Key, effectiveValue, (k, old) => effectiveValue);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("[CONFIG] Error cargando cache: " + ex.Message, ex);
            }
        }

        private void SeedDefaults()
        {
            var defaults = new[]
            {
                // ── Network / TCP ──
                new ConfigSettingEntity
                {
                    Key = "TcpConnectTimeoutMs", Value = "1500", DefaultValue = "1500",
                    Description = "Timeout de conexión TCP a impresoras en ms (1.5s óptimo para LAN, 3s para WAN)",
                    Category = "network", ValueType = "int", MinValue = "500", MaxValue = "30000"
                },
                new ConfigSettingEntity
                {
                    Key = "TcpSendTimeoutMs", Value = "5000", DefaultValue = "5000",
                    Description = "Timeout de envío TCP (ms)",
                    Category = "network", ValueType = "int", MinValue = "1000", MaxValue = "30000"
                },
                new ConfigSettingEntity
                {
                    Key = "DefaultPrinterPort", Value = "9100", DefaultValue = "9100",
                    Description = "Puerto TCP por defecto de impresoras",
                    Category = "network", ValueType = "int", MinValue = "1", MaxValue = "65535"
                },

                // ── Retry / Queue ──
                new ConfigSettingEntity
                {
                    Key = "MaxRetries", Value = "3", DefaultValue = "3",
                    Description = "Número máximo de reintentos por job de impresión",
                    Category = "queue", ValueType = "int", MinValue = "0", MaxValue = "10"
                },
                new ConfigSettingEntity
                {
                    Key = "RetryBackoffBaseMs", Value = "500", DefaultValue = "500",
                    Description = "Base en ms para backoff exponencial de reintentos",
                    Category = "queue", ValueType = "int", MinValue = "100", MaxValue = "10000"
                },

                // ── HTTP API ──
                new ConfigSettingEntity
                {
                    Key = "HttpPort", Value = "8090", DefaultValue = "8090",
                    Description = "Puerto del servidor HTTP API",
                    Category = "api", ValueType = "int", MinValue = "1024", MaxValue = "65535"
                },

                // ── Monitoring ──
                new ConfigSettingEntity
                {
                    Key = "StatusCheckIntervalSeconds", Value = "2", DefaultValue = "2",
                    Description = "Intervalo en segundos entre checks de estado de impresoras",
                    Category = "monitoring", ValueType = "int", MinValue = "1", MaxValue = "300"
                },

                // ── gRPC / Discovery (futuro) ──
                new ConfigSettingEntity
                {
                    Key = "GrpcPort", Value = "50051", DefaultValue = "50051",
                    Description = "Puerto del servidor gRPC de notificaciones",
                    Category = "api", ValueType = "int", MinValue = "1024", MaxValue = "65535"
                },
                new ConfigSettingEntity
                {
                    Key = "GrpcBindAddress", Value = "0.0.0.0", DefaultValue = "0.0.0.0",
                    Description = "Dirección de binding del servidor gRPC",
                    Category = "api", ValueType = "string"
                },
                new ConfigSettingEntity
                {
                    Key = "UdpDiscoveryPort", Value = "9999", DefaultValue = "9999",
                    Description = "Puerto UDP para descubrimiento de servicio",
                    Category = "network", ValueType = "int", MinValue = "1024", MaxValue = "65535"
                },
                new ConfigSettingEntity
                {
                    Key = "UdpDiscoveryEnabled", Value = "true", DefaultValue = "true",
                    Description = "Habilitar/deshabilitar el servidor UDP Discovery",
                    Category = "network", ValueType = "bool"
                },

                // ── Network Watcher (futuro) ──
                new ConfigSettingEntity
                {
                    Key = "NetworkWatcherIntervalSeconds", Value = "30", DefaultValue = "30",
                    Description = "Intervalo en segundos para escaneo de red",
                    Category = "network", ValueType = "int", MinValue = "10", MaxValue = "600"
                },
                new ConfigSettingEntity
                {
                    Key = "ArpScanIntervalSeconds", Value = "60", DefaultValue = "60",
                    Description = "Intervalo en segundos para escaneo ARP",
                    Category = "network", ValueType = "int", MinValue = "15", MaxValue = "600"
                },
                new ConfigSettingEntity
                {
                    Key = "AutoLearnGatewayOnFirstRun", Value = "1", DefaultValue = "1",
                    Description = "Aprender gateway automáticamente en primera ejecución",
                    Category = "network", ValueType = "bool"
                },

                // ── QuipuNetX Synchronization ──
                new ConfigSettingEntity
                {
                    Key = "QuipuNetXUrl", Value = "http://localhost:8081", DefaultValue = "http://localhost:8081",
                    Description = "URL base del servidor QuipuNetX para sincronización de cambios de IP",
                    Category = "integration", ValueType = "string"
                },
                new ConfigSettingEntity
                {
                    Key = "QuipuNetXTimeoutMs", Value = "5000", DefaultValue = "5000",
                    Description = "Timeout en ms para requests HTTP a QuipuNetX",
                    Category = "integration", ValueType = "int", MinValue = "1000", MaxValue = "30000"
                },
                new ConfigSettingEntity
                {
                    Key = "NotificationRetryIntervalSeconds", Value = "30", DefaultValue = "30",
                    Description = "Intervalo en segundos para reintentar notificaciones pendientes a QuipuNetX",
                    Category = "integration", ValueType = "int", MinValue = "10", MaxValue = "300"
                },
                new ConfigSettingEntity
                {
                    Key = "NotificationMaxRetries", Value = "10", DefaultValue = "10",
                    Description = "Número máximo de reintentos para notificaciones a QuipuNetX antes de marcar como FALLIDO",
                    Category = "integration", ValueType = "int", MinValue = "1", MaxValue = "50"
                },

                // ── Fase 23: Expiración de jobs WAITING ──
                new ConfigSettingEntity
                {
                    Key = "ExpirarImpresionDespuesDe", Value = "0", DefaultValue = "0",
                    Description = "Segundos máximos en WAITING antes de expirar. 0 = desactivado. Recibido desde QuipuNetX via /api/config/sync.",
                    Category = "integration", ValueType = "int", MinValue = "0", MaxValue = "86400"
                },
                new ConfigSettingEntity
                {
                    Key = "QuipuNetXPort", Value = "8081", DefaultValue = "8081",
                    Description = "Puerto HTTP del servidor QuipuNetX para callbacks de estado de jobs",
                    Category = "integration", ValueType = "int", MinValue = "1", MaxValue = "65535"
                },

                // ── Emulación de bitmap (ESC/POS vs ESC * para impresoras CUSTOM/POS) ──
                new ConfigSettingEntity
                {
                    Key = "BitmapEmulacion", Value = "escpos", DefaultValue = "escpos",
                    Description = "Modo de envío de bitmaps: 'escpos' usa GS v 0 (raster estándar Epson), 'escasterisc' usa ESC * (bit image por bandas de 24 líneas, compatible con CUSTOM/POS y emulaciones no-Epson).",
                    Category = "emulacion", ValueType = "string"
                },

                // ── TCP Chunked Send (fix bitmap corruption en impresoras con buffer pequeño) ──
                new ConfigSettingEntity
                {
                    Key = "TcpChunkEnabled", Value = "0", DefaultValue = "0",
                    Description = "Enviar datos a impresora en fragmentos (chunks) en vez de un solo bloque. Soluciona corrupción de productos en comandas bitmap cuando la impresora tiene buffer pequeño.",
                    Category = "network", ValueType = "bool"
                },
                new ConfigSettingEntity
                {
                    Key = "TcpChunkSizeBytes", Value = "4096", DefaultValue = "4096",
                    Description = "Tamaño de cada fragmento en bytes al enviar a impresora (solo si TcpChunkEnabled=1). Valores típicos: 1024, 2048, 4096.",
                    Category = "network", ValueType = "int", MinValue = "256", MaxValue = "65536"
                },
                new ConfigSettingEntity
                {
                    Key = "TcpChunkDelayMs", Value = "5", DefaultValue = "5",
                    Description = "Pausa en ms entre fragmentos para que la impresora procese cada chunk (solo si TcpChunkEnabled=1). Valores típicos: 2-20ms.",
                    Category = "network", ValueType = "int", MinValue = "1", MaxValue = "200"
                },

                // ── Espera post-send proporcional a negro del bitmap ──
                // Evita que el siguiente job envíe ESC @ (reset) mientras la impresora
                // todavía está imprimiendo físicamente — lo que corta el ticket a la mitad
                // y lo cruza con el siguiente. Fórmula estimada desde alto + densidad de negro.
                new ConfigSettingEntity
                {
                    Key = "PostPrintWaitEnabled", Value = "1", DefaultValue = "1",
                    Description = "Activar espera post-send proporcional al negro del bitmap. Previene que jobs consecutivos se crucen en el papel cuando hay imágenes con mucho negro (throttling térmico).",
                    Category = "timing", ValueType = "bool"
                },
                new ConfigSettingEntity
                {
                    Key = "PostPrintWaitBaseMsPerRow", Value = "2", DefaultValue = "2",
                    Description = "Ms estimados por fila del bitmap en condiciones normales (papel claro). Base: ~150 mm/s a 8 dots/mm ≈ 0.8 ms/fila; default 2 ms conservador.",
                    Category = "timing", ValueType = "int", MinValue = "0", MaxValue = "50"
                },
                new ConfigSettingEntity
                {
                    Key = "PostPrintWaitExtraMsPerBlackRow", Value = "10", DefaultValue = "10",
                    Description = "Ms extra por fila 100% negra (se multiplica por la fracción de negro de cada fila). Compensa el throttling térmico del cabezal con alta densidad.",
                    Category = "timing", ValueType = "int", MinValue = "0", MaxValue = "100"
                },
                new ConfigSettingEntity
                {
                    Key = "PostPrintWaitMinMs", Value = "100", DefaultValue = "100",
                    Description = "Piso de la espera post-send en ms (se aplica si el bitmap es pequeño o tiene poco negro).",
                    Category = "timing", ValueType = "int", MinValue = "0", MaxValue = "10000"
                },
                new ConfigSettingEntity
                {
                    Key = "PostPrintWaitMaxMs", Value = "4000", DefaultValue = "4000",
                    Description = "Techo de seguridad de la espera post-send en ms. Protege contra estimaciones excesivas en tickets muy largos con mucho negro.",
                    Category = "timing", ValueType = "int", MinValue = "0", MaxValue = "60000"
                },

                // ── Port lock: cuánto espera StatusMonitor por el lock de una impresora antes de skipear el ciclo ──
                new ConfigSettingEntity
                {
                    Key = "StatusCheckPortLockTimeoutMs", Value = "1500", DefaultValue = "1500",
                    Description = "Tiempo máximo en ms que StatusMonitor espera el port lock de una impresora antes de salt el check de este ciclo. Bajarlo si el check de estado empieza a tardar; subirlo si hay muchos checks salteados.",
                    Category = "timing", ValueType = "int", MinValue = "100", MaxValue = "30000"
                },

                // ── Diagnóstico: guardar bitmaps generados como JPG ──
                new ConfigSettingEntity
                {
                    Key = "GuardarBitmapsGenerados", Value = "0", DefaultValue = "0",
                    Description = "Si es 1, cada bitmap renderizado para impresión se guarda como JPG en %ProgramData%\\QuipuNet\\bitmaps\\YYYY-MM-DD\\<jobId>.jpg, con metadata (ancho, alto, densidad de negro, bytes ESC/POS, emulación usada). Permite revisar desde el dashboard qué se intentó imprimir cuando la impresora saca basura o tickets truncados. Desactívalo cuando termines de diagnosticar para no llenar el disco.",
                    Category = "diagnostics", ValueType = "int", MinValue = "0", MaxValue = "1"
                },
                new ConfigSettingEntity
                {
                    Key = "BitmapsRetentionDays", Value = "7", DefaultValue = "7",
                    Description = "Días de retención de los JPG guardados por GuardarBitmapsGenerados. Al guardar un bitmap nuevo, se borran los archivos más viejos que este umbral para acotar uso de disco.",
                    Category = "diagnostics", ValueType = "int", MinValue = "1", MaxValue = "90"
                }
            };

            try
            {
                foreach (var setting in defaults)
                {
                    var existing = _db.Query<ConfigSettingEntity>(
                        "SELECT * FROM config_settings WHERE key = ?", setting.Key).FirstOrDefault();

                    if (existing == null)
                    {
                        setting.UpdatedAt = DateTime.Now.ToString("o");
                        _db.Insert(setting);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("[CONFIG] Error sembrando defaults: " + ex.Message, ex);
            }
        }
    }
}
