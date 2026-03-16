using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using log4net;

namespace PrinterServices.Core.Network
{
    /// <summary>
    /// Helper para obtener direcciones MAC desde tabla ARP de Windows.
    /// Usa APIs nativas (iphlpapi.dll) — NO ejecuta procesos externos.
    /// PRINCIPIO CLAVE: La MAC es el identificador físico REAL de una impresora Ethernet,
    /// la IP puede cambiar por DHCP pero la MAC es inmutable.
    /// </summary>
    public static class ArpHelper
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(ArpHelper));

        // ═══════════════════════════════════════════════════════════
        // API 1: SendARP (envía ARP request y obtiene MAC)
        // Referencia: https://docs.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-sendarp
        // ═══════════════════════════════════════════════════════════
        
        /// <summary>
        /// Envía un ARP request a una IP y obtiene su MAC (Windows API nativa).
        /// </summary>
        /// <param name="destIp">IP destino en formato uint little-endian.</param>
        /// <param name="srcIp">IP origen (0 = auto-detectar interfaz).</param>
        /// <param name="macAddr">Buffer de salida para MAC (6 bytes).</param>
        /// <param name="macAddrLen">Longitud del buffer (entrada/salida).</param>
        /// <returns>0 si éxito, código de error Windows si falla.</returns>
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(
            uint destIp,
            uint srcIp,
            byte[] macAddr,
            ref uint macAddrLen
        );

        /// <summary>
        /// Obtiene la MAC de una IP específica enviando ARP request.
        /// MUY RÁPIDO: ~5-50ms por IP (depende de latencia de red).
        /// Retorna null si no hay respuesta (timeout ~500ms).
        /// </summary>
        /// <param name="ipAddress">IP en formato string (ej: "192.168.1.100").</param>
        /// <returns>PhysicalAddress con la MAC, o null si no se pudo obtener.</returns>
        public static PhysicalAddress GetMacFromIp(string ipAddress)
        {
            if (string.IsNullOrEmpty(ipAddress)) // Validar IP no vacía
            {
                return null;
            }

            try
            {
                // Convertir IP string → IPAddress object
                IPAddress ip = IPAddress.Parse(ipAddress);
                byte[] ipBytes = ip.GetAddressBytes(); // Obtener bytes de la IP
                
                if (ipBytes.Length != 4) // Solo soportamos IPv4
                {
                    return null;
                }

                // Convertir a uint little-endian (formato que espera Windows API)
                uint destIp = BitConverter.ToUInt32(ipBytes, 0);

                // Buffer para recibir MAC (6 bytes)
                byte[] macAddr = new byte[6];
                uint macAddrLen = (uint)macAddr.Length; // Longitud inicial del buffer

                // Llamar a API nativa de Windows (envía ARP request)
                int result = SendARP(destIp, 0, macAddr, ref macAddrLen);

                // Códigos de retorno:
                // 0 = NO_ERROR (éxito)
                // 67 = ERROR_BAD_NET_PATH (IP no alcanzable en la red)
                // 87 = ERROR_INVALID_PARAMETER (IP inválida)
                if (result == 0 && macAddrLen == 6) // Éxito y MAC válida (6 bytes)
                {
                    return new PhysicalAddress(macAddr); // Retornar MAC como PhysicalAddress
                }

                // Si falló, loguear para diagnóstico
                if (result != 0)
                {
                    Log.DebugFormat("[ARP] SendARP falló para {0}: código error {1}", ipAddress, result);
                }

                return null; // No se pudo obtener MAC
            }
            catch (Exception ex)
            {
                Log.WarnFormat("[ARP] Error obteniendo MAC para {0}: {1}", ipAddress, ex.Message);
                return null;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // API 2: GetIpNetTable (lee tabla ARP completa del sistema)
        // Referencia: https://docs.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getipnettable
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Lee la tabla ARP de Windows (Windows API nativa).
        /// </summary>
        /// <param name="pIpNetTable">Puntero al buffer donde se escribirán los datos.</param>
        /// <param name="pdwSize">Tamaño del buffer (entrada/salida).</param>
        /// <param name="bOrder">Si true, ordena por IP (más lento).</param>
        /// <returns>0 si éxito, código de error Windows si falla.</returns>
        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern int GetIpNetTable(
            IntPtr pIpNetTable,
            ref int pdwSize,
            bool bOrder
        );

        /// <summary>
        /// Estructura que representa una entrada en la tabla ARP de Windows.
        /// Mapea directamente a MIB_IPNETROW de iphlpapi.h
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_IPNETROW
        {
            public uint dwIndex;        // Índice del adaptador de red (interfaz)
            public uint dwPhysAddrLen;  // Longitud de la dirección física (MAC) — debe ser 6
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] bPhysAddr;    // Dirección MAC (8 bytes de buffer, solo 6 usados)
            public uint dwAddr;         // Dirección IP en formato uint little-endian
            public uint dwType;         // Tipo de entrada: 1=other, 2=invalid, 3=dynamic, 4=static
        }

        /// <summary>
        /// Lee la tabla ARP completa de Windows (todas las entradas en caché).
        /// MUY RÁPIDO: ~10-30ms para leer toda la tabla (cientos de entradas).
        /// Retorna diccionario IP → MAC de todas las entradas válidas.
        /// Solo lee caché existente, NO envía ARP requests (para eso usar SendARP).
        /// </summary>
        /// <returns>Diccionario con IP como clave y MAC como valor.</returns>
        public static Dictionary<string, PhysicalAddress> GetArpTable()
        {
            var table = new Dictionary<string, PhysicalAddress>(); // Diccionario resultado

            try
            {
                int bufferSize = 0; // Tamaño necesario del buffer (desconocido aún)
                
                // Primera llamada: obtener tamaño necesario del buffer
                GetIpNetTable(IntPtr.Zero, ref bufferSize, false);

                if (bufferSize == 0) // Si no hay entradas, retornar tabla vacía
                {
                    return table;
                }

                // Alocar memoria no administrada para el buffer
                IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    // Segunda llamada: obtener los datos reales
                    int result = GetIpNetTable(buffer, ref bufferSize, false);
                    
                    if (result != 0) // Si falló, retornar tabla vacía
                    {
                        Log.WarnFormat("[ARP] GetIpNetTable falló: código error {0}", result);
                        return table;
                    }

                    // Leer número de entradas (primer int32 del buffer)
                    int entries = Marshal.ReadInt32(buffer);

                    // Calcular offset de la primera entrada (después del contador)
                    IntPtr current = new IntPtr(buffer.ToInt64() + 4);
                    int rowSize = Marshal.SizeOf(typeof(MIB_IPNETROW)); // Tamaño de cada fila

                    // Iterar por cada entrada en la tabla ARP
                    for (int i = 0; i < entries; i++)
                    {
                        // Deserializar estructura MIB_IPNETROW desde memoria no administrada
                        MIB_IPNETROW row = (MIB_IPNETROW)Marshal.PtrToStructure(
                            current, typeof(MIB_IPNETROW));

                        // Solo procesar entradas válidas:
                        // - MAC de 6 bytes (Ethernet estándar)
                        // - Tipo dynamic (3) o static (4) — ignorar invalid (2) y other (1)
                        if (row.dwPhysAddrLen == 6 && (row.dwType == 3 || row.dwType == 4))
                        {
                            // Convertir IP de uint little-endian → string
                            byte[] ipBytes = BitConverter.GetBytes(row.dwAddr);
                            string ip = new IPAddress(ipBytes).ToString();

                            // Copiar MAC (solo primeros 6 bytes del buffer de 8)
                            byte[] macBytes = new byte[6];
                            Array.Copy(row.bPhysAddr, macBytes, 6);
                            PhysicalAddress mac = new PhysicalAddress(macBytes);

                            // Agregar al diccionario (si hay duplicado, último gana)
                            table[ip] = mac;
                        }

                        // Mover puntero a la siguiente entrada
                        current = new IntPtr(current.ToInt64() + rowSize);
                    }

                    Log.DebugFormat("[ARP] Tabla ARP leída: {0} entradas válidas", table.Count);
                }
                finally
                {
                    // Liberar memoria no administrada (crítico para evitar memory leak)
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[ARP] Error leyendo tabla ARP de Windows", ex);
            }

            return table;
        }

        // ═══════════════════════════════════════════════════════════
        // HELPERS DE FORMATO Y COMPARACIÓN
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Formatea PhysicalAddress como string (AA:BB:CC:DD:EE:FF).
        /// Usa dos-puntos como separador (estándar IEEE).
        /// </summary>
        public static string FormatMac(PhysicalAddress mac)
        {
            if (mac == null) // Validar entrada
            {
                return null;
            }
            
            // Convertir bytes a hex con guiones, luego reemplazar por dos-puntos
            return BitConverter.ToString(mac.GetAddressBytes()).Replace("-", ":");
        }

        /// <summary>
        /// Parsea string MAC a PhysicalAddress.
        /// Acepta formatos: AA:BB:CC:DD:EE:FF, AA-BB-CC-DD-EE-FF, AABBCCDDEEFF
        /// </summary>
        public static PhysicalAddress ParseMac(string macString)
        {
            if (string.IsNullOrEmpty(macString)) // Validar entrada
            {
                return null;
            }

            try
            {
                // Normalizar: quitar separadores
                string normalized = macString.Replace(":", "").Replace("-", "").Replace(" ", "");
                
                if (normalized.Length != 12) // MAC debe ser exactamente 12 caracteres hex
                {
                    return null;
                }

                // Convertir cada par de caracteres hex a byte
                byte[] bytes = new byte[6];
                for (int i = 0; i < 6; i++)
                {
                    bytes[i] = Convert.ToByte(normalized.Substring(i * 2, 2), 16);
                }

                return new PhysicalAddress(bytes);
            }
            catch
            {
                return null; // Formato inválido
            }
        }

        /// <summary>
        /// Compara dos MACs ignorando formato (case-insensitive, con/sin separadores).
        /// Retorna true si representan la misma dirección física.
        /// </summary>
        public static bool MacEquals(string mac1, string mac2)
        {
            if (string.IsNullOrEmpty(mac1) || string.IsNullOrEmpty(mac2)) // Validar entradas
            {
                return false;
            }

            // Normalizar ambas MACs: quitar separadores y convertir a uppercase
            string normalized1 = mac1.Replace(":", "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
            string normalized2 = mac2.Replace(":", "").Replace("-", "").Replace(" ", "").ToUpperInvariant();

            return normalized1 == normalized2; // Comparar strings normalizados
        }

        /// <summary>
        /// Compara PhysicalAddress con string MAC.
        /// </summary>
        public static bool MacEquals(PhysicalAddress mac1, string mac2String)
        {
            if (mac1 == null || string.IsNullOrEmpty(mac2String)) // Validar entradas
            {
                return false;
            }

            string mac1String = FormatMac(mac1); // Convertir PhysicalAddress a string
            return MacEquals(mac1String, mac2String); // Comparar strings
        }

        // ═══════════════════════════════════════════════════════════
        // BÚSQUEDA INVERSA: IP POR MAC
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Busca la IP actual de una impresora dada su MAC (búsqueda inversa en tabla ARP).
        /// CRÍTICO: Permite detectar cambios de IP por DHCP.
        /// Si la MAC no está en la tabla ARP, retorna null (impresora apagada o en otra red).
        /// </summary>
        /// <param name="macAddress">MAC a buscar (en cualquier formato).</param>
        /// <returns>IP actual de la impresora, o null si no se encontró.</returns>
        public static string FindIpByMac(string macAddress)
        {
            if (string.IsNullOrEmpty(macAddress)) // Validar entrada
            {
                return null;
            }

            try
            {
                // Leer tabla ARP completa del sistema (10-30ms)
                var arpTable = GetArpTable();

                // Buscar MAC en el diccionario (comparación normalizada)
                foreach (var entry in arpTable)
                {
                    if (MacEquals(entry.Value, macAddress)) // Comparar ignorando formato
                    {
                        Log.DebugFormat("[ARP] MAC {0} encontrada con IP {1}", macAddress, entry.Key);
                        return entry.Key; // Retornar IP encontrada
                    }
                }

                Log.DebugFormat("[ARP] MAC {0} NO encontrada en tabla ARP", macAddress);
                return null; // MAC no encontrada en la red
            }
            catch (Exception ex)
            {
                Log.WarnFormat("[ARP] Error buscando IP para MAC {0}: {1}", macAddress, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Fuerza un ARP request enviando ping silencioso.
        /// Útil cuando la IP no está en caché ARP de Windows.
        /// Timeout muy corto para no bloquear el hilo.
        /// </summary>
        public static void PopulateArpCache(string ipAddress, int timeoutMs = 100)
        {
            if (string.IsNullOrEmpty(ipAddress)) // Validar entrada
            {
                return;
            }

            try
            {
                // Enviar ping con timeout corto para poblar caché ARP
                using (var ping = new Ping())
                {
                    ping.Send(ipAddress, timeoutMs); // No nos importa el resultado
                }
            }
            catch
            {
                // Ignorar errores (timeout, destino inalcanzable, etc.)
                // Solo nos interesa que Windows envíe ARP request
            }
        }

        // ═══════════════════════════════════════════════════════════
        // SCAN ACTIVO DE SUBRED (pobla caché ARP)
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Escanea una subred completa enviando ARP requests paralelos.
        /// POBLA la caché ARP de Windows con todas las MACs que respondan.
        /// 
        /// Ejemplo: ScanSubnet("192.168.1", 1, 254, 50)
        ///   → Envía ARP requests a 192.168.1.1 hasta 192.168.1.254
        ///   → Timeout 50ms por IP
        ///   → Paralelo usando Tasks (máx 20 concurrentes)
        ///   → Tiempo total: ~500ms para 254 IPs
        /// </summary>
        /// <param name="subnet">Primeros 3 octetos de la subred (ej: "192.168.1").</param>
        /// <param name="startHost">Primer host a escanear (default 1).</param>
        /// <param name="endHost">Último host a escanear (default 254).</param>
        /// <param name="timeoutMs">Timeout por IP en ms (default 50ms).</param>
        /// <param name="cancellationToken">Token para cancelar el scan en progreso.</param>
        public static void ScanSubnet(string subnet, int startHost = 1, int endHost = 254, int timeoutMs = 50, System.Threading.CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(subnet))
            {
                return;
            }

            try
            {
                Log.InfoFormat("[ARP] Iniciando scan de subred {0}.{1}-{2} (timeout {3}ms por host)",
                    subnet, startHost, endHost, timeoutMs);

                var tasks = new System.Collections.Generic.List<System.Threading.Tasks.Task>();
                var discoveredHosts = new System.Collections.Generic.Dictionary<string, string>(); // IP → MAC
                object lockObj = new object(); // Lock para diccionario thread-safe

                // Crear task por cada IP en el rango
                for (int host = startHost; host <= endHost; host++)
                {
                    // Verificar cancelación antes de crear cada task
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Log.InfoFormat("[ARP] Scan cancelado en host {0}.{1} (impresora volvió online)", subnet, host);
                        break; // Abortar loop inmediatamente
                    }
                    
                    string ip = $"{subnet}.{host}"; // Construir IP completa

                    // Crear task que envía ARP request a esta IP
                    var task = System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            // Enviar ARP request usando API nativa (más rápido que ping)
                            var mac = GetMacFromIp(ip);
                            
                            if (mac != null) // Si respondió, agregar a diccionario IP → MAC
                            {
                                lock (lockObj)
                                {
                                    discoveredHosts[ip] = FormatMac(mac); // Guardar IP → MAC
                                }
                            }
                        }
                        catch
                        {
                            // Ignorar errores individuales (IP no alcanzable, timeout, etc.)
                            // El scan debe continuar con las demás IPs
                        }
                    });

                    tasks.Add(task);

                    // Limitar concurrencia a 20 tasks simultáneos (evitar saturar red)
                    if (tasks.Count >= 20)
                    {
                        // Esperar a que termine el más antiguo antes de agregar más
                        System.Threading.Tasks.Task.WaitAny(tasks.ToArray());
                        // Limpiar tasks completados
                        tasks.RemoveAll(t => t.IsCompleted);
                    }
                }

                // Esperar a que terminen todos los tasks restantes
                System.Threading.Tasks.Task.WaitAll(tasks.ToArray());

                // Loguear resultado del scan con detalle
                if (discoveredHosts.Count > 0)
                {
                    Log.InfoFormat("[ARP] Scan completado: {0} hosts descubiertos en {1}.{2}-{3}",
                        discoveredHosts.Count, subnet, startHost, endHost);
                    
                    // Ordenar por IP (último octeto) para mejor legibilidad
                    var sortedHosts = discoveredHosts.OrderBy(kvp =>
                    {
                        var parts = kvp.Key.Split('.');
                        return int.Parse(parts[3]);
                    }).ToList();
                    
                    // Loguear cada host con su MAC
                    foreach (var host in sortedHosts)
                    {
                        Log.InfoFormat("[ARP]   {0} → {1}", host.Key.PadRight(15), host.Value);
                    }
                }
                else
                {
                    Log.WarnFormat("[ARP] Scan completado: 0 hosts descubiertos en {0}.{1}-{2}",
                        subnet, startHost, endHost);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[ARP] Error durante scan de subred: " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Extrae la subred (primeros 3 octetos) de una IP completa.
        /// Ejemplo: "192.168.1.100" → "192.168.1"
        /// </summary>
        public static string GetSubnetFromIp(string ipAddress)
        {
            if (string.IsNullOrEmpty(ipAddress)) // Validar entrada
            {
                return null;
            }

            try
            {
                // Parsear IP y extraer primeros 3 octetos
                string[] parts = ipAddress.Split('.');
                
                if (parts.Length >= 3) // Validar que tenga al menos 3 octetos
                {
                    return $"{parts[0]}.{parts[1]}.{parts[2]}"; // Retornar subred
                }

                return null; // IP inválida
            }
            catch
            {
                return null; // Error al parsear
            }
        }
    }
}
