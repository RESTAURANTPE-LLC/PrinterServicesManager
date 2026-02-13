using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Config;

namespace PrinterServices.Discovery
{
    /// <summary>
    /// Servidor UDP para auto-descubrimiento de PrinterServices en la red local.
    /// 
    /// Protocolo (definido en vibe_engeneering_printerservices.md, sección 11):
    ///   1. PrinterServices.exe arranca → escucha UDP en puerto 9999 (configurable)
    ///   2. Quipunet.exe (Servidor POS) envía broadcast UDP: "QUIPU_PRINTER_DISCOVERY"
    ///   3. PrinterServices responde unicast al remitente: "QUIPU_PRINTER_SERVICE|IP|HttpPort|GrpcPort"
    ///   4. El Servidor POS guarda IP:puertos y los usa para HTTP REST + gRPC
    ///   5. Si no hay respuesta en 3s → el POS usa IP fija de configuración (fallback)
    ///   6. El POS re-descubre cada 60s (heartbeat) para detectar cambios de IP
    /// 
    /// Características:
    ///   - Hilo background con CancellationToken (start/stop desde PrinterServicesHost)
    ///   - Puerto configurable via ConfigManager ("UdpDiscoveryPort", default 9999)
    ///   - Habilitado/deshabilitado via ConfigManager ("UdpDiscoveryEnabled", default true)
    ///   - Detecta automáticamente la IP local para incluirla en la respuesta
    ///   - Sin dependencias externas: solo System.Net.Sockets.UdpClient (.NET Framework)
    /// 
    /// Ciclo de vida:
    ///   PrinterServicesHost.Start() → _udpDiscovery.Start()
    ///   PrinterServicesHost.Stop()  → _udpDiscovery.Stop() → CancellationToken cancela el loop
    /// </summary>
    public class UdpDiscoveryServer
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(UdpDiscoveryServer));

        /// <summary>
        /// Mensaje mágico que el Servidor POS envía por broadcast para descubrir PrinterServices.
        /// Debe coincidir exactamente con lo que envía Quipunet.exe.
        /// </summary>
        private const string DISCOVERY_REQUEST = "QUIPU_PRINTER_DISCOVERY";

        /// <summary>
        /// Prefijo de la respuesta unicast que PrinterServices envía al POS.
        /// Formato completo: "QUIPU_PRINTER_SERVICE|IP|HttpPort|GrpcPort"
        /// Ejemplo: "QUIPU_PRINTER_SERVICE|10.0.0.21|8090|50051"
        /// </summary>
        private const string DISCOVERY_RESPONSE_PREFIX = "QUIPU_PRINTER_SERVICE";

        private CancellationTokenSource _cts;  // Token para cancelar el loop de escucha
        private Task _listenTask;              // Tarea background que escucha UDP

        /// <summary>
        /// Inicia el servidor UDP Discovery en un hilo background.
        /// Lee la configuración de ConfigManager para determinar puerto y estado habilitado.
        /// Si UdpDiscoveryEnabled == false, no arranca (log informativo).
        /// </summary>
        public void Start()
        {
            var cfg = ConfigManager.Instance;

            // Verificar si el discovery está habilitado en la configuración
            bool enabled = cfg.GetBool("UdpDiscoveryEnabled", true);
            if (!enabled)
            {
                Log.Info("[UDP] Discovery deshabilitado por configuración (UdpDiscoveryEnabled=false)");
                return; // No arrancar — salir silenciosamente
            }

            int port = cfg.GetInt("UdpDiscoveryPort", 9999);

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            // Lanzar tarea background para escuchar UDP
            // Task.Run + loop infinito — patrón igual que StatusMonitor
            _listenTask = Task.Run(() => ListenLoop(port, ct), ct);

            Log.InfoFormat("[UDP] Discovery Server iniciado en puerto {0}", port);
        }

        /// <summary>
        /// Detiene el servidor UDP Discovery.
        /// Cancela el CancellationToken, lo que cierra el UdpClient.ReceiveAsync()
        /// y termina el loop de escucha.
        /// </summary>
        public void Stop()
        {
            if (_cts != null)
            {
                _cts.Cancel();  // Señalar al loop que debe terminar

                // Esperar a que el loop termine (máximo 3s para no bloquear el shutdown)
                if (_listenTask != null)
                {
                    try { _listenTask.Wait(TimeSpan.FromSeconds(3)); }
                    catch { } // Ignorar excepciones durante shutdown
                }

                _cts.Dispose();
                _cts = null;
                Log.Info("[UDP] Discovery Server detenido");
            }
        }

        /// <summary>
        /// Loop principal de escucha UDP. Corre en un hilo background.
        /// 
        /// Flujo:
        ///   1. Abrir UdpClient en el puerto configurado
        ///   2. Esperar paquetes UDP (ReceiveAsync)
        ///   3. Si el contenido es "QUIPU_PRINTER_DISCOVERY" → responder unicast
        ///   4. Repetir hasta que el CancellationToken se cancele
        /// 
        /// El UdpClient se configura con ReuseAddress=true para permitir
        /// re-binding rápido si el servicio se reinicia.
        /// </summary>
        private async Task ListenLoop(int port, CancellationToken ct)
        {
            UdpClient udp = null;

            try
            {
                // Crear UdpClient con binding en todas las interfaces (0.0.0.0:port)
                udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));

                Log.DebugFormat("[UDP] Escuchando broadcast en 0.0.0.0:{0}", port);

                // Loop infinito de escucha — se cancela via CancellationToken
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        // Esperar un paquete UDP (blocking async)
                        // Nota: UdpClient no soporta CancellationToken nativo en .NET 4.5.2,
                        // por eso usamos un timeout + chequeo manual del token.
                        var receiveTask = udp.ReceiveAsync();

                        // Esperar con timeout de 2s para poder chequear cancellation periódicamente
                        if (await Task.WhenAny(receiveTask, Task.Delay(2000, ct)) != receiveTask)
                        {
                            continue; // Timeout — volver a chequear ct y esperar
                        }

                        var result = receiveTask.Result;
                        string message = Encoding.UTF8.GetString(result.Buffer).Trim();

                        // Solo responder al mensaje mágico de discovery
                        if (message == DISCOVERY_REQUEST)
                        {
                            Log.InfoFormat("[UDP] Discovery request recibido de {0}", result.RemoteEndPoint);
                            await SendDiscoveryResponse(udp, result.RemoteEndPoint);
                        }
                        else
                        {
                            // Paquete UDP desconocido — ignorar silenciosamente
                            Log.DebugFormat("[UDP] Paquete ignorado de {0}: '{1}'",
                                result.RemoteEndPoint, message.Length > 50 ? message.Substring(0, 50) : message);
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        break; // UdpClient fue cerrado — salir del loop
                    }
                    catch (OperationCanceledException)
                    {
                        break; // Cancellation — salir del loop
                    }
                    catch (Exception ex)
                    {
                        // Error en recepción — loguear y continuar (no romper el loop)
                        if (!ct.IsCancellationRequested)
                        {
                            Log.WarnFormat("[UDP] Error en recepción: {0}", ex.Message);
                            await Task.Delay(1000, ct); // Pausa antes de reintentar
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal: el servicio se está deteniendo
            }
            catch (Exception ex)
            {
                Log.Error("[UDP] Error fatal en Discovery Server: " + ex.Message, ex);
            }
            finally
            {
                // Cerrar UdpClient al salir del loop
                if (udp != null)
                {
                    try { udp.Close(); } catch { }
                }
                Log.Debug("[UDP] Loop de escucha finalizado");
            }
        }

        /// <summary>
        /// Envía la respuesta de discovery unicast al Servidor POS que la solicitó.
        /// 
        /// Formato: "QUIPU_PRINTER_SERVICE|IP_LOCAL|HTTP_PORT|GRPC_PORT"
        /// Ejemplo: "QUIPU_PRINTER_SERVICE|10.0.0.21|8090|50051"
        /// 
        /// La IP local se detecta automáticamente usando la IP del endpoint del socket.
        /// Si no se puede determinar, se usa "127.0.0.1" como fallback.
        /// </summary>
        private async Task SendDiscoveryResponse(UdpClient udp, IPEndPoint remoteEndPoint)
        {
            try
            {
                var cfg = ConfigManager.Instance;
                int httpPort = cfg.GetInt("HttpPort", 8090);
                int grpcPort = cfg.GetInt("GrpcPort", 50051);

                // Detectar la IP local que puede alcanzar al remitente.
                // Esto funciona abriendo un socket UDP temporal hacia la IP del POS
                // y leyendo la IP local que el OS asignó al socket.
                string localIp = GetLocalIpFor(remoteEndPoint.Address);

                // Construir respuesta: PREFIJO|IP|HTTP_PORT|GRPC_PORT
                string response = string.Format("{0}|{1}|{2}|{3}",
                    DISCOVERY_RESPONSE_PREFIX, localIp, httpPort, grpcPort);

                byte[] data = Encoding.UTF8.GetBytes(response);

                // Enviar unicast al remitente (no broadcast — respuesta directa)
                await udp.SendAsync(data, data.Length, remoteEndPoint);

                Log.InfoFormat("[UDP] Respuesta enviada a {0}: {1}", remoteEndPoint, response);
            }
            catch (Exception ex)
            {
                Log.WarnFormat("[UDP] Error enviando respuesta a {0}: {1}", remoteEndPoint, ex.Message);
            }
        }

        /// <summary>
        /// Detecta la IP local que el sistema operativo usaría para comunicarse
        /// con una dirección remota específica.
        /// 
        /// Técnica: Crear un socket UDP (sin enviar datos realmente), hacer Connect()
        /// hacia la IP remota, y leer la IP local asignada por el OS.
        /// Esto funciona incluso sin enviar paquetes — solo consulta la tabla de ruteo.
        /// 
        /// Fallback: si falla, devuelve "127.0.0.1".
        /// </summary>
        private string GetLocalIpFor(IPAddress remoteAddress)
        {
            try
            {
                // Crear socket temporal UDP — no se envían datos
                using (var tempSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    // Connect en UDP no envía nada — solo configura la ruta en el OS
                    tempSocket.Connect(remoteAddress, 1);

                    // Leer la IP local que el OS eligió para esta ruta
                    var localEndpoint = tempSocket.LocalEndPoint as IPEndPoint;
                    if (localEndpoint != null)
                    {
                        return localEndpoint.Address.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WarnFormat("[UDP] No se pudo detectar IP local: {0}", ex.Message);
            }

            // Fallback: IP de loopback
            return "127.0.0.1";
        }
    }
}
