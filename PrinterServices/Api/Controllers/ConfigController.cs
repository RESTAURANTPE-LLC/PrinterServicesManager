using System;
using System.Collections.Generic;
using System.Linq;
using log4net;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PrinterServices.Config;

namespace PrinterServices.Api.Controllers
{
    public class ConfigController
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(ConfigController));

        private readonly ConfigManager _config;

        public ConfigController(ConfigManager config)
        {
            _config = config;
        }

        public ApiResult GetAll()
        {
            try
            {
                var settings = _config.GetAll();
                var response = settings.Select(s => new
                {
                    key = s.Key,
                    value = s.Value,
                    defaultValue = s.DefaultValue,
                    description = s.Description,
                    category = s.Category,
                    valueType = s.ValueType,
                    minValue = s.MinValue,
                    maxValue = s.MaxValue,
                    updatedAt = s.UpdatedAt
                });

                return ApiResult.Ok(JsonConvert.SerializeObject(new { count = settings.Count, settings = response }));
            }
            catch (Exception ex)
            {
                Log.Error("[CONFIG-API] Error obteniendo settings", ex);
                return ApiResult.Error(ex.Message);
            }
        }

        public ApiResult GetByCategory(string category)
        {
            try
            {
                var settings = _config.GetByCategory(category);
                var response = settings.Select(s => new
                {
                    key = s.Key,
                    value = s.Value,
                    defaultValue = s.DefaultValue,
                    description = s.Description,
                    valueType = s.ValueType,
                    minValue = s.MinValue,
                    maxValue = s.MaxValue
                });

                return ApiResult.Ok(JsonConvert.SerializeObject(new { category = category, settings = response }));
            }
            catch (Exception ex)
            {
                Log.Error("[CONFIG-API] Error obteniendo settings de " + category, ex);
                return ApiResult.Error(ex.Message);
            }
        }

        public ApiResult GetSetting(string key)
        {
            try
            {
                var all = _config.GetAll();
                var setting = all.FirstOrDefault(s =>
                    s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

                if (setting == null)
                {
                    return ApiResult.NotFound();
                }

                return ApiResult.Ok(JsonConvert.SerializeObject(new
                {
                    key = setting.Key,
                    value = setting.Value,
                    defaultValue = setting.DefaultValue,
                    description = setting.Description,
                    category = setting.Category,
                    valueType = setting.ValueType,
                    minValue = setting.MinValue,
                    maxValue = setting.MaxValue
                }));
            }
            catch (Exception ex)
            {
                Log.Error("[CONFIG-API] Error obteniendo setting " + key, ex);
                return ApiResult.Error(ex.Message);
            }
        }

        public ApiResult UpdateSetting(string body)
        {
            try
            {
                if (string.IsNullOrEmpty(body))
                {
                    return ApiResult.BadRequest("Body vacío");
                }

                var json = JObject.Parse(body);

                JToken keyToken;
                if (!json.TryGetValue("key", out keyToken) || keyToken.Type == JTokenType.Null)
                {
                    return ApiResult.BadRequest("key es requerido");
                }
                string key = keyToken.ToString();

                JToken valueToken;
                if (!json.TryGetValue("value", out valueToken) || valueToken.Type == JTokenType.Null)
                {
                    return ApiResult.BadRequest("value es requerido");
                }
                string value = valueToken.ToString();

                // Validar rango si existe
                var all = _config.GetAll();
                var existing = all.FirstOrDefault(s =>
                    s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

                if (existing != null && existing.ValueType == "int")
                {
                    int intValue;
                    if (!int.TryParse(value, out intValue))
                    {
                        return ApiResult.BadRequest("El valor debe ser un número entero");
                    }

                    int minVal;
                    if (!string.IsNullOrEmpty(existing.MinValue) && int.TryParse(existing.MinValue, out minVal) && intValue < minVal)
                    {
                        return ApiResult.BadRequest(string.Format("Valor mínimo permitido: {0}", minVal));
                    }

                    int maxVal;
                    if (!string.IsNullOrEmpty(existing.MaxValue) && int.TryParse(existing.MaxValue, out maxVal) && intValue > maxVal)
                    {
                        return ApiResult.BadRequest(string.Format("Valor máximo permitido: {0}", maxVal));
                    }
                }

                _config.Set(key, value);

                Log.InfoFormat("[CONFIG-API] Setting actualizado: {0} = {1}", key, value);

                return ApiResult.Ok(JsonConvert.SerializeObject(new
                {
                    status = "UPDATED",
                    key = key,
                    value = value
                }));
            }
            catch (Exception ex)
            {
                Log.Error("[CONFIG-API] Error actualizando setting", ex);
                return ApiResult.Error(ex.Message);
            }
        }

        public ApiResult UpdateBatch(string body)
        {
            try
            {
                if (string.IsNullOrEmpty(body))
                {
                    return ApiResult.BadRequest("Body vacío");
                }

                var json = JObject.Parse(body);

                JToken settingsToken;
                if (!json.TryGetValue("settings", out settingsToken) || settingsToken.Type != JTokenType.Object)
                {
                    return ApiResult.BadRequest("settings (object) es requerido");
                }

                var settingsObj = (JObject)settingsToken;
                int updated = 0;

                foreach (var prop in settingsObj.Properties())
                {
                    string key = prop.Name;
                    string value = prop.Value.ToString();
                    _config.Set(key, value);
                    updated++;
                }

                Log.InfoFormat("[CONFIG-API] Batch update: {0} settings actualizados", updated);

                return ApiResult.Ok(JsonConvert.SerializeObject(new
                {
                    status = "UPDATED",
                    count = updated
                }));
            }
            catch (Exception ex)
            {
                Log.Error("[CONFIG-API] Error en batch update", ex);
                return ApiResult.Error(ex.Message);
            }
        }

        public ApiResult ResetSetting(string key)
        {
            try
            {
                _config.ResetToDefault(key);
                string newValue = _config.GetString(key);

                return ApiResult.Ok(JsonConvert.SerializeObject(new
                {
                    status = "RESET",
                    key = key,
                    value = newValue
                }));
            }
            catch (Exception ex)
            {
                Log.Error("[CONFIG-API] Error reseteando " + key, ex);
                return ApiResult.Error(ex.Message);
            }
        }

        public ApiResult ResetAll()
        {
            try
            {
                _config.ResetAllToDefaults();
                return ApiResult.Ok(JsonConvert.SerializeObject(new { status = "ALL_RESET" }));
            }
            catch (Exception ex)
            {
                Log.Error("[CONFIG-API] Error reseteando todos", ex);
                return ApiResult.Error(ex.Message);
            }
        }

        /// <summary>
        /// Fase 23: POST /api/config/sync — Recibe configuraciones desde QuipuNetX.
        /// QuipuNetX envía settings como pares clave-valor para sincronizar con PrinterServices.
        /// Usado para enviar ExpirarImpresionDespuesDe y otros feature flags.
        /// Body: { "settings": { "ExpirarImpresionDespuesDe": "300", "OtroConfig": "valor" } }
        /// Response: { "status": "SYNCED", "updated": 1, "keys": ["ExpirarImpresionDespuesDe"] }
        /// </summary>
        public ApiResult SyncConfig(string body)
        {
            try
            {
                if (string.IsNullOrEmpty(body))                        // Validar body no vacío
                {
                    return ApiResult.BadRequest("Body JSON es requerido");
                }

                var parsed = JObject.Parse(body);                      // Parsear JSON
                var settingsToken = parsed["settings"];                 // Obtener objeto settings

                if (settingsToken == null || settingsToken.Type != JTokenType.Object) // Validar objeto
                {
                    return ApiResult.BadRequest("Se requiere campo 'settings' como objeto"); // Error
                }

                var settings = settingsToken.ToObject<Dictionary<string, string>>(); // Convertir a diccionario
                if (settings == null || settings.Count == 0)           // Validar no vacío
                {
                    return ApiResult.BadRequest("No hay settings para sincronizar"); // Error
                }

                Log.InfoFormat("[CONFIG-API] Sync de {0} settings desde QuipuNetX", settings.Count);

                var updatedKeys = new List<string>();                   // Lista de keys actualizadas

                foreach (var kvp in settings)                          // Iterar cada setting recibido
                {
                    try
                    {
                        _config.Set(kvp.Key, kvp.Value);              // Actualizar en ConfigManager
                        updatedKeys.Add(kvp.Key);                     // Registrar key actualizada
                        Log.InfoFormat("[CONFIG-API] ✅ Sync: {0} = {1}", kvp.Key, kvp.Value);
                    }
                    catch (Exception ex)
                    {
                        Log.WarnFormat("[CONFIG-API] ⚠ Error al sincronizar {0}: {1}", kvp.Key, ex.Message);
                    }
                }

                var response = new                                     // Construir respuesta
                {
                    status = "SYNCED",                                 // Estado exitoso
                    updated = updatedKeys.Count,                       // Cantidad de settings actualizados
                    keys = updatedKeys                                 // Lista de keys actualizadas
                };

                return ApiResult.Ok(JsonConvert.SerializeObject(response)); // Retornar JSON
            }
            catch (Exception ex)
            {
                Log.Error("[CONFIG-API] Error en sync de config", ex);
                return ApiResult.Error("Error al sincronizar config: " + ex.Message);
            }
        }
    }
}
