using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using log4net;
using Newtonsoft.Json;
using PrinterServices.Config;
using PrinterServices.Data;
using PrinterServices.Data.Models;

namespace PrinterServices.Notifications
{
    /// <summary>
    /// Notificador para enviar cambios de IP de impresoras a QuipuNetX.
    /// Si QuipuNetX está offline, persiste la notificación en BD para reintentos posteriores.
    /// </summary>
    public class QuipuNetXNotifier
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(QuipuNetXNotifier));

        private readonly PrinterServiceDb _db;
        private readonly ConfigManager _config;
        private readonly HttpClient _httpClient;

        public QuipuNetXNotifier(PrinterServiceDb db, ConfigManager config)
        {
            _db = db;
            _config = config;

            _httpClient = new HttpClient();
            int timeoutMs = _config.GetInt("QuipuNetXTimeoutMs", 5000);
            _httpClient.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
        }

        public async Task<bool> NotifyIpChangeAsync(string macAddress, string oldIp, string newIp)
        {
            try
            {
                var notification = new IpChangeNotificationDto(macAddress, oldIp, newIp);

                Log.InfoFormat("[NOTIFIER] Notificando cambio de IP: MAC={0} | {1} → {2}",
                    macAddress, oldIp, newIp);

                bool sent = await TrySendNotificationAsync(notification);

                if (sent)
                {
                    Log.InfoFormat("[NOTIFIER] ✅ Notificación enviada exitosamente a QuipuNetX: MAC={0}", macAddress);
                    return true;
                }
                else
                {
                    PersistFailedNotification(macAddress, oldIp, newIp, "QuipuNetX offline o timeout");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error("[NOTIFIER] Error al notificar cambio de IP", ex);
                PersistFailedNotification(macAddress, oldIp, newIp, ex.Message);
                return false;
            }
        }

        private async Task<bool> TrySendNotificationAsync(IpChangeNotificationDto notification)
        {
            try
            {
                string baseUrl = _config.GetString("QuipuNetXUrl", "http://localhost:8081");
                string endpoint = baseUrl.TrimEnd('/') + "/api/rest/printers/update-ip";

                string jsonBody = JsonConvert.SerializeObject(notification);
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                Log.DebugFormat("[NOTIFIER] POST {0} | Body: {1}", endpoint, jsonBody);

                HttpResponseMessage response = await _httpClient.PostAsync(endpoint, content);

                if (response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    Log.DebugFormat("[NOTIFIER] Respuesta de QuipuNetX: {0}", responseBody);
                    return true;
                }
                else
                {
                    string errorBody = await response.Content.ReadAsStringAsync();
                    Log.WarnFormat("[NOTIFIER] QuipuNetX respondió con error {0}: {1}",
                        (int)response.StatusCode, errorBody);
                    return false;
                }
            }
            catch (TaskCanceledException)
            {
                Log.Warn("[NOTIFIER] Timeout al intentar conectar con QuipuNetX");
                return false;
            }
            catch (HttpRequestException ex)
            {
                Log.WarnFormat("[NOTIFIER] Error de conexión con QuipuNetX: {0}", ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("[NOTIFIER] Error al enviar HTTP request", ex);
                return false;
            }
        }

        private void PersistFailedNotification(string macAddress, string oldIp, string newIp, string error)
        {
            try
            {
                var existing = _db.Query<IpChangeNotificationEntity>(
                    "SELECT * FROM notificacionescambiosip WHERE mac_address = ? AND estado = 'PENDIENTE'",
                    macAddress).FirstOrDefault();

                if (existing != null)
                {
                    existing.NewIp = newIp;
                    existing.UltimoError = error;
                    _db.Update(existing);

                    Log.InfoFormat("[NOTIFIER] 📝 Notificación PENDIENTE actualizada: MAC={0} | Nueva IP={1}",
                        macAddress, newIp);
                }
                else
                {
                    var newNotification = new IpChangeNotificationEntity
                    {
                        MacAddress = macAddress,
                        OldIp = oldIp,
                        NewIp = newIp,
                        Estado = "PENDIENTE",
                        FechaCreacion = DateTime.Now,
                        FechaEnvio = null,
                        Intentos = 0,
                        UltimoError = error
                    };

                    _db.Insert(newNotification);

                    Log.InfoFormat("[NOTIFIER] 📝 Notificación persistida en BD: MAC={0} | {1} → {2}",
                        macAddress, oldIp, newIp);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[NOTIFIER] ❌ ERROR CRÍTICO: No se pudo persistir notificación en BD", ex);
            }
        }

        public async Task<bool> RetryNotificationAsync(IpChangeNotificationEntity notification)
        {
            try
            {
                var dto = new IpChangeNotificationDto(
                    notification.MacAddress,
                    notification.OldIp,
                    notification.NewIp);

                Log.InfoFormat("[NOTIFIER] Reintentando notificación #{0}: MAC={1} (intento {2})",
                    notification.Id, notification.MacAddress, notification.Intentos + 1);

                bool sent = await TrySendNotificationAsync(dto);

                notification.Intentos++;

                if (sent)
                {
                    notification.Estado = "ENVIADO";
                    notification.FechaEnvio = DateTime.Now;
                    notification.UltimoError = null;
                    _db.Update(notification);

                    Log.InfoFormat("[NOTIFIER] ✅ Notificación #{0} enviada exitosamente (tras {1} intentos)",
                        notification.Id, notification.Intentos);
                    return true;
                }
                else
                {
                    int maxRetries = _config.GetInt("NotificationMaxRetries", 10);

                    if (notification.Intentos >= maxRetries)
                    {
                        notification.Estado = "FALLIDO";
                        notification.UltimoError = string.Format("Máximo de {0} reintentos alcanzado", maxRetries);
                        _db.Update(notification);

                        Log.ErrorFormat("[NOTIFIER] ❌ Notificación #{0} marcada como FALLIDO (tras {1} intentos)",
                            notification.Id, notification.Intentos);
                    }
                    else
                    {
                        notification.UltimoError = "QuipuNetX offline o timeout";
                        _db.Update(notification);

                        Log.WarnFormat("[NOTIFIER] ⏳ Notificación #{0} sigue PENDIENTE (intento {1}/{2})",
                            notification.Id, notification.Intentos, maxRetries);
                    }

                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error("[NOTIFIER] Error al reintentar notificación #" + notification.Id, ex);
                
                notification.Intentos++;
                notification.UltimoError = ex.Message;
                _db.Update(notification);

                return false;
            }
        }
    }
}
