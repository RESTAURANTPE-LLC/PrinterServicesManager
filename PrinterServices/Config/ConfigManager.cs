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
                    Key = "TcpConnectTimeoutMs", Value = "3000", DefaultValue = "3000",
                    Description = "Timeout de conexión TCP a impresoras (ms)",
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
                    Key = "StatusCheckIntervalSeconds", Value = "15", DefaultValue = "15",
                    Description = "Intervalo en segundos entre checks de estado de impresoras",
                    Category = "monitoring", ValueType = "int", MinValue = "5", MaxValue = "300"
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
                    Key = "UdpDiscoveryPort", Value = "9999", DefaultValue = "9999",
                    Description = "Puerto UDP para descubrimiento de servicio",
                    Category = "network", ValueType = "int", MinValue = "1024", MaxValue = "65535"
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
