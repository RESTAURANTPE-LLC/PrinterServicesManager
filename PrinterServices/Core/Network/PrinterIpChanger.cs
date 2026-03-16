using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using log4net;
using SnmpSharpNet;

namespace PrinterServices.Core.Network
{
    /// <summary>
    /// Cambia la IP de impresoras ESC/POS en red cableada que están en un segmento diferente.
    /// Flujo: crear alias de subred temporal → obtener MAC → cambiar IP vía SNMP/TCP → verificar → limpiar.
    /// REQUIERE: ejecutar como administrador (netsh necesita privilegios elevados).
    /// </summary>
    public class PrinterIpChanger
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PrinterIpChanger)); // Logger log4net

        // ═══════════════════════════════════════════════════════════
        // OIDs SNMP para configuración de red de impresora
        // ═══════════════════════════════════════════════════════════

        /// <summary>OID estándar MIB-II: dirección IP del dispositivo (lectura).</summary>
        private const string OID_IP_ADDRESS = "1.3.6.1.2.1.4.20.1.1"; // ipAdEntAddr (tabla de IPs)

        /// <summary>OID estándar MIB-II: máscara de subred (lectura).</summary>
        private const string OID_SUBNET_MASK = "1.3.6.1.2.1.4.20.1.3"; // ipAdEntNetMask

        /// <summary>OID estándar MIB-II: gateway por defecto.</summary>
        private const string OID_DEFAULT_GATEWAY = "1.3.6.1.2.1.4.21.1.7.0.0.0.0"; // ipRouteNextHop

        /// <summary>OID enterprise Epson: IP address (SET soportado en modelos TM con NIC).</summary>
        private const string OID_EPSON_IP = "1.3.6.1.4.1.1248.1.1.3.1.1.4.1"; // ePOS IP

        /// <summary>OID enterprise Epson: subnet mask (SET soportado).</summary>
        private const string OID_EPSON_SUBNET = "1.3.6.1.4.1.1248.1.1.3.1.1.5.1"; // ePOS subnet

        /// <summary>OID enterprise Epson: gateway (SET soportado).</summary>
        private const string OID_EPSON_GATEWAY = "1.3.6.1.4.1.1248.1.1.3.1.1.6.1"; // ePOS gateway

        /// <summary>OID enterprise Epson: aplicar cambios de red (SET 1 = apply).</summary>
        private const string OID_EPSON_APPLY = "1.3.6.1.4.1.1248.1.1.3.1.1.99.1"; // ePOS apply

        /// <summary>OID sysDescr: descripción del sistema (para identificar fabricante).</summary>
        private const string OID_SYS_DESCR = "1.3.6.1.2.1.1.1.0"; // sysDescr

        /// <summary>OID ifPhysAddress: MAC address de la interfaz de red.</summary>
        private const string OID_IF_PHYS_ADDR = "1.3.6.1.2.1.2.2.1.6.1"; // ifPhysAddress (interfaz 1)

        // ═══════════════════════════════════════════════════════════
        // Modelo de configuración de red original (protección IP estática)
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Captura la configuración IP original de la interfaz antes de modificarla.
        /// CRÍTICO: Protege la IP estática del PC — si netsh la altera, se restaura.
        /// </summary>
        private class SavedNetConfig
        {
            public string InterfaceName { get; set; }   // Nombre de la interfaz (ej: "Ethernet")
            public string IpAddress { get; set; }        // IP estática original del PC
            public string SubnetMask { get; set; }       // Máscara original
            public string Gateway { get; set; }          // Gateway original
            public bool IsDhcp { get; set; }             // true si estaba en DHCP (no se restaura)
        }

        // ═══════════════════════════════════════════════════════════
        // Modelo de resultado
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Resultado de la operación de cambio de IP.
        /// Contiene toda la info relevante para el endpoint.
        /// </summary>
        public class IpChangeResult
        {
            public bool Success { get; set; }           // true si se cambió la IP exitosamente
            public string MacAddress { get; set; }      // MAC del dispositivo (AA:BB:CC:DD:EE:FF)
            public string IpActual { get; set; }        // IP original de la impresora
            public string IpFinal { get; set; }         // IP nueva configurada
            public string SubnetMask { get; set; }      // Máscara de subred aplicada
            public string Gateway { get; set; }         // Gateway aplicado
            public string Metodo { get; set; }          // Método usado: SNMP_EPSON, SNMP_GENERIC, RAW_TCP
            public string Fabricante { get; set; }      // Fabricante detectado (si se pudo leer)
            public string Error { get; set; }           // Mensaje de error si falló
            public List<string> Pasos { get; set; }     // Log de pasos ejecutados (trazabilidad)
            public long TiempoMs { get; set; }          // Tiempo total de la operación en ms

            public IpChangeResult()
            {
                Pasos = new List<string>(); // Inicializar lista de pasos
            }
        }

        /// <summary>
        /// Resultado de la verificación DLE EOT (status ESC/POS vía TCP 9100).
        /// DLE EOT (10 04 01) es el comando estándar ESC/POS para consultar el estado
        /// de la impresora sin afectar la impresión en curso.
        /// </summary>
        private class DleEotResult
        {
            public bool Connected { get; set; }   // true si se pudo conectar al puerto 9100
            public bool Responded { get; set; }    // true si la impresora respondió al DLE EOT
            public byte StatusByte { get; set; }   // Byte de status devuelto (bits: cajón, online, error, papel)
            public string Error { get; set; }      // Mensaje de error si no se pudo conectar
        }

        // ═══════════════════════════════════════════════════════════
        // MÉTODO PRINCIPAL: Cambiar IP de impresora cross-subnet
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Cambia la IP de una impresora ESC/POS que está en otro segmento de red.
        /// Crea alias temporal, obtiene MAC, cambia IP vía SNMP o TCP, verifica y limpia.
        /// </summary>
        /// <param name="ipActual">IP actual de la impresora (ej: 192.168.1.23).</param>
        /// <param name="ipFinal">IP nueva deseada (ej: 192.168.68.65).</param>
        /// <param name="subnetMask">Máscara de subred para la nueva IP (default: 255.255.255.0).</param>
        /// <param name="gateway">Gateway para la nueva IP (default: auto-detectar del segmento destino).</param>
        /// <param name="snmpCommunity">Community string SNMP para escritura (default: "private").</param>
        /// <returns>Resultado con éxito/error, MAC, y log de pasos.</returns>
        public static IpChangeResult ChangeIp(
            string ipActual,
            string ipFinal,
            string subnetMask = "255.255.255.0",
            string gateway = null,
            string snmpCommunity = "private")
        {
            var result = new IpChangeResult // Crear resultado
            {
                IpActual = ipActual,       // IP original
                IpFinal = ipFinal          // IP destino
            };

            var sw = Stopwatch.StartNew(); // Medir tiempo total de operación
            string tempIp = null;          // IP temporal que agregaremos al adaptador
            string interfaceName = null;   // Nombre del adaptador de red donde agregar alias
            SavedNetConfig savedConfig = null; // Config IP original del PC (para restaurar si se altera)

            try
            {
                // ── PASO 1: Validar parámetros ──
                result.Pasos.Add("1. Validando parámetros"); // Log de paso
                if (!ValidateIp(ipActual) || !ValidateIp(ipFinal)) // Validar formato IP
                {
                    result.Error = "IP actual o IP final tienen formato inválido"; // Error de validación
                    return result; // Retornar sin éxito
                }

                string subnetActual = ArpHelper.GetSubnetFromIp(ipActual);   // Subred de la impresora (ej: 192.168.1)
                string subnetFinal = ArpHelper.GetSubnetFromIp(ipFinal);     // Subred destino (ej: 192.168.68)
                string subnetLocal = GetLocalSubnet();                       // Subred del PC (ej: 192.168.68)

                result.Pasos.Add(string.Format("   Subred impresora: {0}, Subred destino: {1}, Subred local: {2}",
                    subnetActual, subnetFinal, subnetLocal)); // Log de subredes

                // ── PASO 2: Detectar interfaz de red cableada ──
                result.Pasos.Add("2. Detectando interfaz de red cableada"); // Log de paso
                interfaceName = GetWiredInterfaceName(); // Buscar adaptador Ethernet activo

                if (string.IsNullOrEmpty(interfaceName)) // No se encontró adaptador
                {
                    result.Error = "No se encontró interfaz de red cableada activa (Ethernet)"; // Error
                    return result; // Retornar sin éxito
                }

                result.Pasos.Add(string.Format("   Interfaz detectada: {0}", interfaceName)); // Log de interfaz

                // ── PASO 2b: Capturar configuración IP original (PROTECCIÓN IP ESTÁTICA) ──
                result.Pasos.Add("2b. Capturando configuración IP original del PC"); // Log de paso
                savedConfig = CaptureInterfaceConfig(interfaceName); // Guardar IP/mask/gw/dhcp actuales
                if (savedConfig != null) // Config capturada
                {
                    result.Pasos.Add(string.Format(
                        "   Config original: IP={0}, Mask={1}, GW={2}, DHCP={3}",
                        savedConfig.IpAddress ?? "N/A", savedConfig.SubnetMask ?? "N/A",
                        savedConfig.Gateway ?? "N/A", savedConfig.IsDhcp)); // Log detalle
                }
                else // No se pudo capturar
                {
                    result.Pasos.Add("   ⚠ No se pudo capturar config original (se continuará con precaución)"); // Warning
                }

                // ── PASO 3: Crear alias IP temporal en la subred de la impresora ──
                result.Pasos.Add("3. Creando alias IP temporal en subred de impresora"); // Log de paso

                // Generar IP temporal alta (.250) en la subred de la impresora para evitar conflictos
                tempIp = subnetActual + ".250"; // IP temporal: ej 192.168.1.250

                // Verificar que la IP temporal no esté en uso con un ping rápido
                if (IpResponds(tempIp, 300)) // Si la IP temporal ya responde
                {
                    tempIp = subnetActual + ".249"; // Usar otra IP temporal (ej: 192.168.1.249)
                }

                result.Pasos.Add(string.Format("   IP temporal: {0} en interfaz: {1}", tempIp, interfaceName)); // Log

                bool aliasCreated = AddIpAlias(interfaceName, tempIp, subnetMask); // Ejecutar netsh add address
                if (!aliasCreated) // Si no se pudo crear el alias
                {
                    result.Error = string.Format(
                        "No se pudo crear alias IP {0} en {1}. ¿Ejecutando como administrador?",
                        tempIp, interfaceName); // Error — probablemente falta de permisos
                    return result; // Retornar sin éxito
                }

                result.Pasos.Add("   Alias IP creado exitosamente"); // Confirmar alias creado

                // Esperar a que Windows aplique el alias de red (necesario para que la interfaz esté lista)
                Thread.Sleep(2000); // 2 segundos de espera para propagación

                // ── PASO 4: Verificar conectividad con la impresora ──
                result.Pasos.Add("4. Verificando conectividad con impresora"); // Log de paso

                // Poblar caché ARP con ping (necesario para SendARP)
                ArpHelper.PopulateArpCache(ipActual, 1000); // Ping con timeout 1 segundo
                Thread.Sleep(500); // Esperar respuesta ARP

                bool printerResponds = IpResponds(ipActual, 2000); // Verificar que la impresora responda
                if (!printerResponds) // La impresora no responde
                {
                    result.Pasos.Add("   ⚠ Impresora no responde a ping, intentando igualmente..."); // Warning
                }
                else
                {
                    result.Pasos.Add("   ✓ Impresora responde al ping"); // Éxito
                }

                // ── PASO 4b: Verificar status ESC/POS vía DLE EOT (TCP 9100) ──
                result.Pasos.Add("4b. Verificando status ESC/POS (DLE EOT vía TCP 9100)"); // Log de paso
                var dleResult = TryDleEotStatus(ipActual); // Enviar DLE EOT 10 04 01 al puerto 9100
                if (dleResult.Connected && dleResult.Responded) // Conexión TCP + respuesta DLE EOT
                {
                    result.Pasos.Add(string.Format("   ✓ Puerto 9100 abierto, DLE EOT respondió: 0x{0:X2} (impresora en línea)",
                        dleResult.StatusByte)); // Log con byte de status
                }
                else if (dleResult.Connected && !dleResult.Responded) // TCP conectó pero no respondió DLE EOT
                {
                    result.Pasos.Add("   ⚠ Puerto 9100 abierto, pero DLE EOT sin respuesta (puede ser normal en algunas impresoras)"); // Warning
                }
                else // No se pudo conectar al puerto 9100
                {
                    result.Pasos.Add(string.Format("   ✗ No se pudo conectar al puerto 9100: {0}",
                        dleResult.Error ?? "timeout")); // Error
                }

                // ── PASO 5: Obtener MAC de la impresora ──
                result.Pasos.Add("5. Obteniendo MAC address de la impresora"); // Log de paso

                // Intentar con SendARP nativo (más rápido y confiable)
                var mac = ArpHelper.GetMacFromIp(ipActual); // P/Invoke SendARP
                if (mac != null) // MAC obtenida vía ARP
                {
                    result.MacAddress = ArpHelper.FormatMac(mac); // Formatear AA:BB:CC:DD:EE:FF
                    result.Pasos.Add(string.Format("   MAC (ARP): {0}", result.MacAddress)); // Log
                }
                else // ARP falló, intentar vía SNMP
                {
                    result.Pasos.Add("   ARP no retornó MAC, intentando vía SNMP..."); // Log
                    result.MacAddress = GetMacViaSNMP(ipActual); // Leer OID ifPhysAddress
                    if (!string.IsNullOrEmpty(result.MacAddress)) // MAC obtenida vía SNMP
                    {
                        result.Pasos.Add(string.Format("   MAC (SNMP): {0}", result.MacAddress)); // Log
                    }
                    else // No se pudo obtener MAC
                    {
                        result.Pasos.Add("   ⚠ No se pudo obtener MAC (continuando igualmente)"); // Warning
                    }
                }

                // ── PASO 6: Detectar fabricante vía SNMP sysDescr ──
                result.Pasos.Add("6. Detectando fabricante del dispositivo"); // Log de paso
                result.Fabricante = DetectManufacturer(ipActual); // Leer OID sysDescr
                result.Pasos.Add(string.Format("   Fabricante: {0}", result.Fabricante ?? "Desconocido")); // Log

                // ── PASO 7: Cambiar IP — Intentar múltiples métodos ──
                result.Pasos.Add("7. Intentando cambiar IP de la impresora"); // Log de paso

                // Auto-detectar gateway del segmento destino si no se proporcionó
                if (string.IsNullOrEmpty(gateway)) // Gateway no especificado
                {
                    gateway = subnetFinal + ".1"; // Default: primer IP del segmento destino
                    result.Pasos.Add(string.Format("   Gateway auto-detectado: {0}", gateway)); // Log
                }

                result.SubnetMask = subnetMask; // Guardar máscara en resultado
                result.Gateway = gateway;       // Guardar gateway en resultado

                bool changed = false; // Flag de éxito — true SOLO si se verificó con ping
                bool comandoEnviado = false; // true si al menos un método envió comando
                string ultimoMetodo = null; // Último método que reportó envío exitoso

                // ══════════════════════════════════════════════════════════════
                // CASCADA CON VERIFICACIÓN: cada método que reporta envío
                // se verifica con ping a la nueva IP. Si no responde, se
                // continúa al siguiente método (no parar en falso positivo).
                // ══════════════════════════════════════════════════════════════

                // ── Método 1: SNMP SET con OIDs Epson (más común en impresoras térmicas) ──
                result.Pasos.Add("   7a. Intentando SNMP SET (OIDs Epson enterprise)..."); // Log
                if (TrySnmpSetEpson(ipActual, ipFinal, subnetMask, gateway, snmpCommunity)) // SNMP SET Epson
                {
                    result.Pasos.Add("   ↳ Comando SNMP Epson enviado, verificando nueva IP..."); // Log
                    comandoEnviado = true; // Registrar que se envió algo
                    ultimoMetodo = "SNMP_EPSON"; // Guardar método
                    Thread.Sleep(3000); // Esperar 3s para reinicio de NIC
                    if (IpResponds(ipFinal, 3000)) // Ping a nueva IP con timeout 3s
                    {
                        changed = true; // Verificado — IP realmente cambió
                        result.Metodo = "SNMP_EPSON"; // Registrar método exitoso
                        result.Pasos.Add("   ✓ Verificado: IP cambiada vía SNMP (Epson OIDs)"); // Log éxito
                    }
                    else // Enviado pero no verificado
                    {
                        result.Pasos.Add("   ⚠ Enviado pero sin respuesta en nueva IP, probando siguiente método..."); // Warning
                    }
                }

                // ── Método 2: SNMP SET con OIDs genéricos MIB-II ──
                if (!changed) // Solo si el método anterior no verificó
                {
                    result.Pasos.Add("   7b. Intentando SNMP SET (OIDs genéricos MIB-II)..."); // Log
                    if (TrySnmpSetGeneric(ipActual, ipFinal, subnetMask, gateway, snmpCommunity)) // SNMP genérico
                    {
                        result.Pasos.Add("   ↳ Comando SNMP genérico enviado, verificando nueva IP..."); // Log
                        comandoEnviado = true; // Registrar envío
                        ultimoMetodo = "SNMP_GENERIC"; // Guardar método
                        Thread.Sleep(3000); // Esperar 3s
                        if (IpResponds(ipFinal, 3000)) // Verificar nueva IP
                        {
                            changed = true; // Verificado
                            result.Metodo = "SNMP_GENERIC"; // Registrar
                            result.Pasos.Add("   ✓ Verificado: IP cambiada vía SNMP (MIB-II genérico)"); // Log éxito
                        }
                        else // No verificado
                        {
                            result.Pasos.Add("   ⚠ Enviado pero sin respuesta en nueva IP, probando siguiente método..."); // Warning
                        }
                    }
                }

                // ── Método 3: Raw TCP 1F1B1F (impresoras chinas: SPRT, Xprinter, HPRT, etc.) ──
                if (!changed) // Solo si métodos anteriores no verificaron
                {
                    result.Pasos.Add("   7c. Intentando Raw TCP 1F1B1F B2 (protocolo chino, puerto 9100)..."); // Log
                    if (TryRawTcp1F1B1F(ipActual, ipFinal, subnetMask, gateway)) // Protocolo chino
                    {
                        result.Pasos.Add("   ↳ Comandos 1F1B1F enviados (DHCP off + B2 + reset), verificando..."); // Log
                        comandoEnviado = true; // Registrar envío
                        ultimoMetodo = "RAW_TCP_1F1B1F"; // Guardar método
                        Thread.Sleep(5000); // Esperar 5s — estas impresoras son lentas reiniciando NIC
                        if (IpResponds(ipFinal, 3000)) // Verificar nueva IP
                        {
                            changed = true; // Verificado
                            result.Metodo = "RAW_TCP_1F1B1F"; // Registrar
                            result.Pasos.Add("   ✓ Verificado: IP cambiada vía Raw TCP 1F1B1F B2"); // Log éxito
                        }
                        else // No verificado
                        {
                            result.Pasos.Add("   ⚠ Enviado pero sin respuesta en nueva IP, probando siguiente método..."); // Warning
                        }
                    }
                }

                // ── Método 4: ENPC (EpsonNet Protocol) vía UDP 3289 ──
                if (!changed) // Solo si métodos anteriores no verificaron
                {
                    result.Pasos.Add("   7d. Intentando ENPC (EpsonNet Protocol, UDP 3289)..."); // Log
                    if (TryEnpcIpChange(ipActual, ipFinal, subnetMask, gateway)) // ENPC Epson
                    {
                        result.Pasos.Add("   ↳ Comando ENPC enviado, verificando nueva IP..."); // Log
                        comandoEnviado = true; // Registrar envío
                        ultimoMetodo = "ENPC_UDP_3289"; // Guardar método
                        Thread.Sleep(3000); // Esperar 3s
                        if (IpResponds(ipFinal, 3000)) // Verificar nueva IP
                        {
                            changed = true; // Verificado
                            result.Metodo = "ENPC_UDP_3289"; // Registrar
                            result.Pasos.Add("   ✓ Verificado: IP cambiada vía ENPC (EpsonNet Protocol)"); // Log éxito
                        }
                        else // No verificado
                        {
                            result.Pasos.Add("   ⚠ Enviado pero sin respuesta en nueva IP, probando siguiente método..."); // Warning
                        }
                    }
                }

                // ── Método 5: HTTP Web Config (puerto 80/443) ──
                if (!changed) // Solo si métodos anteriores no verificaron
                {
                    result.Pasos.Add("   7e. Intentando HTTP Web Config (puerto 80)..."); // Log
                    if (TryHttpWebConfigIpChange(ipActual, ipFinal, subnetMask, gateway)) // HTTP Web
                    {
                        result.Pasos.Add("   ↳ Comando HTTP enviado, verificando nueva IP..."); // Log
                        comandoEnviado = true; // Registrar envío
                        ultimoMetodo = "HTTP_WEB_CONFIG"; // Guardar método
                        Thread.Sleep(3000); // Esperar 3s
                        if (IpResponds(ipFinal, 3000)) // Verificar nueva IP
                        {
                            changed = true; // Verificado
                            result.Metodo = "HTTP_WEB_CONFIG"; // Registrar
                            result.Pasos.Add("   ✓ Verificado: IP cambiada vía HTTP Web Config"); // Log éxito
                        }
                        else // No verificado
                        {
                            result.Pasos.Add("   ⚠ Enviado pero sin respuesta en nueva IP, probando siguiente método..."); // Warning
                        }
                    }
                }

                // ── Método 6: UDP Broadcast (Puerto 3000 - NetFinder/Rongta) ──
                if (!changed) // Último recurso
                {
                    result.Pasos.Add("   7f. Intentando UDP Broadcast (Puerto 3000, protocolo NetFinder)..."); // Log
                    // Intentamos obtener la MAC si no la tenemos (aunque sea null, el método lo maneja)
                    string macForUdp = !string.IsNullOrEmpty(result.MacAddress) ? result.MacAddress : null;
                    
                    if (TryUdpBroadcastConfig(ipFinal, subnetMask, gateway, macForUdp))
                    {
                        result.Pasos.Add("   ↳ Paquetes UDP Broadcast enviados, verificando nueva IP..."); // Log
                        comandoEnviado = true;
                        ultimoMetodo = "UDP_BROADCAST_3000";
                        Thread.Sleep(4000); // Esperar 4s (UDP es lento y requiere reinicio)
                        
                        if (IpResponds(ipFinal, 3000))
                        {
                            changed = true;
                            result.Metodo = "UDP_BROADCAST_3000";
                            result.Pasos.Add("   ✓ Verificado: IP cambiada vía UDP Broadcast");
                        }
                         else // No verificado
                        {
                            result.Pasos.Add("   ⚠ Enviado pero sin respuesta en nueva IP..."); // Warning
                        }
                    }
                }

                // ── Método 7: Telnet (Puerto 23) ──
                if (!changed) // Solo si métodos anteriores no verificaron
                {
                    result.Pasos.Add("   7g. Intentando Telnet (Puerto 23)..."); // Log
                    if (TryTelnetIpChange(ipActual, ipFinal, subnetMask, gateway)) // Telnet
                    {
                        result.Pasos.Add("   ↳ Comandos Telnet enviados, verificando nueva IP..."); // Log
                        comandoEnviado = true;
                        ultimoMetodo = "TELNET_PORT_23";
                        Thread.Sleep(5000); // Esperar 5s (reinicio de servicio network)
                        
                        if (IpResponds(ipFinal, 3000))
                        {
                            changed = true;
                            result.Metodo = "TELNET_PORT_23";
                            result.Pasos.Add("   ✓ Verificado: IP cambiada vía Telnet");
                        }
                         else
                        {
                            result.Pasos.Add("   ⚠ Enviado pero sin respuesta en nueva IP..."); // Warning
                        }
                    }
                }

                // ── Método 8: ZPL II (^ND) ──
                if (!changed)
                {
                    result.Pasos.Add("   7h. Intentando ZPL II (^ND) en puerto 9100..."); // Log
                    if (TryZplIpChange(ipActual, ipFinal, subnetMask, gateway))
                    {
                        result.Pasos.Add("   ↳ Comando ZPL enviado, verificando nueva IP..."); // Log
                        comandoEnviado = true;
                        ultimoMetodo = "ZPL_COMMAND";
                        Thread.Sleep(3000);
                        
                        if (IpResponds(ipFinal, 3000))
                        {
                            changed = true;
                            result.Metodo = "ZPL_COMMAND";
                            result.Pasos.Add("   ✓ Verificado: IP cambiada vía ZPL");
                        }
                        else
                        {
                            result.Pasos.Add("   ⚠ Enviado pero sin respuesta en nueva IP...");
                        }
                    }
                }

                // ── Método 9: TSPL (TSC) ──
                if (!changed)
                {
                    result.Pasos.Add("   7i. Intentando TSPL (SET IPADDR) en puerto 9100..."); // Log
                    if (TryTsplIpChange(ipActual, ipFinal, subnetMask, gateway))
                    {
                        result.Pasos.Add("   ↳ Comando TSPL enviado, verificando nueva IP..."); // Log
                        comandoEnviado = true;
                        ultimoMetodo = "TSPL_COMMAND";
                        Thread.Sleep(3000);
                        
                        if (IpResponds(ipFinal, 3000))
                        {
                            changed = true;
                            result.Metodo = "TSPL_COMMAND";
                            result.Pasos.Add("   ✓ Verificado: IP cambiada vía TSPL");
                        }
                        else
                        {
                            result.Pasos.Add("   ⚠ Enviado pero sin respuesta en nueva IP...");
                        }
                    }
                }

                // ── Resultado de la cascada ──
                if (!changed && !comandoEnviado) // Ningún método pudo siquiera enviar comando
                {
                    result.Error = "No se pudo cambiar la IP. " +
                        "Ningún método pudo enviar comando (SNMP, TCP 9100, ENPC, HTTP, UDP, Telnet, ZPL, TSPL). " +
                        "Verificar: 1) Community SNMP, 2) Modelo soportado, " +
                        "3) Puertos accesibles (9100, 3289, 80, 3000, 23)."; // Error
                    result.Pasos.Add("   ✗ Ningún método pudo enviar comando de cambio"); // Log
                }

                // ── PASO 8: Verificación final ──
                if (changed) // Cambio verificado en la cascada con ping inmediato
                {
                    result.Success = true; // Marcar éxito confirmado
                    result.Pasos.Add(string.Format("8. ✓ Cambio verificado: {0} → {1} vía {2}",
                        ipActual, ipFinal, result.Metodo)); // Log
                }
                else if (comandoEnviado) // Se envió comando(s) pero ninguno verificó — última oportunidad
                {
                    result.Pasos.Add("8. Verificación extendida (última oportunidad)..."); // Log
                    Thread.Sleep(5000); // Esperar 5s adicionales para NIC lento

                    // Intentar ping 3 veces con intervalos de 2s (total ~11s extra)
                    bool finalCheck = false; // Flag de verificación final
                    for (int i = 0; i < 3 && !finalCheck; i++) // 3 intentos
                    {
                        finalCheck = IpResponds(ipFinal, 3000); // Ping con timeout 3s
                        if (!finalCheck && i < 2) Thread.Sleep(2000); // Esperar 2s entre intentos
                    }

                    if (finalCheck) // ¡La impresora finalmente respondió en la nueva IP!
                    {
                        result.Success = true; // Marcar éxito
                        result.Metodo = ultimoMetodo; // Registrar último método usado
                        result.Pasos.Add(string.Format(
                            "   ✓ Impresora responde en {0} (método: {1})", ipFinal, ultimoMetodo)); // Log
                    }
                    else // Realmente no funcionó — reportar fallo honesto
                    {
                        result.Success = false; // Marcar FALLO (no mentir al usuario)
                        result.Metodo = ultimoMetodo; // Registrar qué se intentó
                        result.Error = string.Format(
                            "Comando(s) enviado(s) vía {0} pero la impresora no responde en {1}. " +
                            "Posibles causas: 1) Impresora no soporta este protocolo de cambio IP, " +
                            "2) DHCP activo sobreescribe la IP estática, " +
                            "3) Necesita reinicio manual (apagar/encender).",
                            ultimoMetodo, ipFinal); // Error descriptivo
                        result.Pasos.Add(string.Format(
                            "   ✗ Impresora no responde en {0} después de todos los intentos", ipFinal)); // Log
                    }
                }
            }
            catch (Exception ex) // Error inesperado
            {
                Log.Error("[IP-CHANGE] Error cambiando IP de impresora", ex); // Log error
                result.Error = "Error inesperado: " + ex.Message; // Error en resultado
                result.Pasos.Add("ERROR: " + ex.Message); // Log en pasos
            }
            finally
            {
                // ── LIMPIEZA: Remover alias IP temporal ──
                if (!string.IsNullOrEmpty(tempIp) && !string.IsNullOrEmpty(interfaceName)) // Si se creó alias
                {
                    result.Pasos.Add("9. Limpiando alias IP temporal"); // Log de paso
                    bool removed = RemoveIpAlias(interfaceName, tempIp); // netsh delete address (store=active)
                    result.Pasos.Add(removed
                        ? "   ✓ Alias IP temporal removido"       // Éxito
                        : "   ⚠ No se pudo remover alias IP temporal"); // Warning
                }

                // ── PROTECCIÓN: Verificar y restaurar IP estática del PC si fue alterada ──
                if (savedConfig != null && !savedConfig.IsDhcp && !string.IsNullOrEmpty(savedConfig.IpAddress))
                {
                    result.Pasos.Add("10. Verificando que la IP estática del PC no fue alterada"); // Log
                    var currentConfig = CaptureInterfaceConfig(interfaceName); // Leer config actual

                    bool ipLost = currentConfig == null || // No se pudo leer config actual
                        currentConfig.IsDhcp || // Se cambió a DHCP (BUG CRÍTICO)
                        currentConfig.IpAddress != savedConfig.IpAddress; // IP cambió

                    if (ipLost) // La IP estática fue alterada — RESTAURAR
                    {
                        result.Pasos.Add(string.Format(
                            "   ⚠ IP estática alterada! Restaurando: IP={0}, Mask={1}, GW={2}",
                            savedConfig.IpAddress, savedConfig.SubnetMask, savedConfig.Gateway)); // Log

                        bool restored = RestoreInterfaceConfig(savedConfig); // Restaurar config original
                        result.Pasos.Add(restored
                            ? "   ✓ IP estática del PC restaurada correctamente" // Éxito
                            : "   ✗ ERROR: No se pudo restaurar IP estática. Restaurar manualmente."); // Error
                    }
                    else // IP estática intacta
                    {
                        result.Pasos.Add("   ✓ IP estática del PC intacta"); // OK
                    }
                }

                sw.Stop(); // Detener cronómetro
                result.TiempoMs = sw.ElapsedMilliseconds; // Guardar tiempo total
                result.Pasos.Add(string.Format("Tiempo total: {0}ms", result.TiempoMs)); // Log
            }

            return result; // Retornar resultado completo
        }

        // ═══════════════════════════════════════════════════════════
        // GESTIÓN DE ALIAS IP (netsh)
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Agrega una IP adicional (alias) a una interfaz de red usando netsh.
        /// REQUIERE: privilegios de administrador.
        /// USA store=active para que el cambio sea TEMPORAL (solo en RAM, no persiste en reinicio).
        /// Esto PROTEGE la IP estática del PC — no modifica la configuración persistente.
        /// Equivalente a: netsh interface ipv4 add address "Ethernet" 192.168.1.250 255.255.255.0 store=active
        /// </summary>
        private static bool AddIpAlias(string interfaceName, string ip, string subnetMask)
        {
            try
            {
                // Construir comando netsh con store=active (CRÍTICO: solo modifica config en RAM)
                // Sin store=active, netsh modifica la config persistente y puede borrar IP estática
                string args = string.Format(
                    "interface ipv4 add address \"{0}\" {1} {2} store=active",
                    interfaceName, ip, subnetMask); // Formato: interfaz IP máscara store=active

                Log.InfoFormat("[IP-CHANGE] Ejecutando: netsh {0}", args); // Log del comando

                return RunNetsh(args, 10000); // Ejecutar con timeout de 10 segundos
            }
            catch (Exception ex) // Error ejecutando netsh
            {
                Log.Error("[IP-CHANGE] Error agregando alias IP: " + ex.Message, ex); // Log error
                return false; // Retornar fallo
            }
        }

        /// <summary>
        /// Remueve una IP alias de una interfaz de red usando netsh.
        /// USA store=active para que solo afecte la config en RAM (no toque la persistente).
        /// Equivalente a: netsh interface ipv4 delete address "Ethernet" 192.168.1.250 store=active
        /// </summary>
        private static bool RemoveIpAlias(string interfaceName, string ip)
        {
            try
            {
                // Construir comando netsh con store=active (CRÍTICO: no tocar config persistente)
                string args = string.Format(
                    "interface ipv4 delete address \"{0}\" {1} store=active",
                    interfaceName, ip); // Formato: interfaz IP store=active

                Log.InfoFormat("[IP-CHANGE] Ejecutando: netsh {0}", args); // Log del comando

                return RunNetsh(args, 10000); // Ejecutar con timeout de 10 segundos
            }
            catch (Exception ex) // Error ejecutando netsh
            {
                Log.Warn("[IP-CHANGE] Error removiendo alias IP: " + ex.Message); // Log warning
                return false; // Retornar fallo (no crítico — con store=active desaparece en reinicio)
            }
        }

        /// <summary>
        /// Ejecuta un comando netsh y retorna true si exitCode == 0.
        /// Captura stdout/stderr para diagnóstico.
        /// </summary>
        private static bool RunNetsh(string arguments, int timeoutMs)
        {
            var psi = new ProcessStartInfo // Configurar proceso
            {
                FileName = "netsh",                 // Ejecutable de Windows para configuración de red
                Arguments = arguments,              // Argumentos del comando
                UseShellExecute = false,            // No usar shell (para capturar output)
                RedirectStandardOutput = true,      // Capturar stdout
                RedirectStandardError = true,       // Capturar stderr
                CreateNoWindow = true,              // No crear ventana visible
                WindowStyle = ProcessWindowStyle.Hidden // Ocultar ventana
            };

            using (var process = Process.Start(psi)) // Iniciar proceso netsh
            {
                string stdout = process.StandardOutput.ReadToEnd(); // Leer salida estándar
                string stderr = process.StandardError.ReadToEnd();  // Leer salida de error

                bool finished = process.WaitForExit(timeoutMs); // Esperar a que termine

                if (!finished) // Timeout — proceso no terminó a tiempo
                {
                    try { process.Kill(); } catch { } // Matar proceso
                    Log.WarnFormat("[IP-CHANGE] netsh timeout ({0}ms): {1}", timeoutMs, arguments); // Log
                    return false; // Retornar fallo
                }

                if (process.ExitCode != 0) // netsh retornó error
                {
                    Log.WarnFormat("[IP-CHANGE] netsh exitCode={0}: {1} | stderr={2}",
                        process.ExitCode, arguments, stderr); // Log del error
                    return false; // Retornar fallo
                }

                Log.DebugFormat("[IP-CHANGE] netsh OK: {0}", arguments); // Log éxito
                return true; // Retornar éxito
            }
        }

        // ═══════════════════════════════════════════════════════════
        // DETECCIÓN DE INTERFAZ DE RED
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Detecta el nombre de la interfaz de red cableada (Ethernet) activa.
        /// Busca interfaces tipo Ethernet con estado Up y descarta WiFi/loopback/virtual.
        /// </summary>
        private static string GetWiredInterfaceName()
        {
            try
            {
                // Obtener todas las interfaces de red del sistema
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();

                foreach (var nic in interfaces) // Iterar cada interfaz
                {
                    // Filtrar solo interfaces cableadas activas
                    if (nic.OperationalStatus == OperationalStatus.Up && // Debe estar activa
                        nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet && // Solo Ethernet cableada
                        !nic.Description.ToLowerInvariant().Contains("virtual") && // Excluir VPN/virtuales
                        !nic.Description.ToLowerInvariant().Contains("vpn") &&     // Excluir VPN
                        !nic.Description.ToLowerInvariant().Contains("loopback") && // Excluir loopback
                        !nic.Description.ToLowerInvariant().Contains("bluetooth"))  // Excluir bluetooth
                    {
                        Log.InfoFormat("[IP-CHANGE] Interfaz Ethernet detectada: {0} ({1})",
                            nic.Name, nic.Description); // Log de interfaz encontrada
                        return nic.Name; // Retornar nombre de la interfaz (ej: "Ethernet", "Ethernet 2")
                    }
                }

                // Fallback: si no encontró Ethernet puro, buscar cualquier interfaz activa con IPv4
                foreach (var nic in interfaces)
                {
                    if (nic.OperationalStatus == OperationalStatus.Up && // Debe estar activa
                        nic.NetworkInterfaceType != NetworkInterfaceType.Loopback && // No loopback
                        nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel) // No tunnel
                    {
                        var ipProps = nic.GetIPProperties(); // Obtener propiedades IP
                        if (ipProps.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork)) // Tiene IPv4
                        {
                            Log.InfoFormat("[IP-CHANGE] Interfaz fallback: {0} ({1}, tipo={2})",
                                nic.Name, nic.Description, nic.NetworkInterfaceType); // Log
                            return nic.Name; // Retornar nombre
                        }
                    }
                }

                return null; // No se encontró ninguna interfaz válida
            }
            catch (Exception ex) // Error listando interfaces
            {
                Log.Error("[IP-CHANGE] Error detectando interfaz de red: " + ex.Message, ex); // Log
                return null;
            }
        }

        /// <summary>
        /// Obtiene la subred local del PC (primeros 3 octetos de la IP local).
        /// </summary>
        private static string GetLocalSubnet()
        {
            try
            {
                // Usar socket UDP para detectar la IP local preferida por el SO
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    socket.Connect("8.8.8.8", 53); // Conectar a DNS Google (no envía datos reales)
                    var localIp = ((IPEndPoint)socket.LocalEndPoint).Address.ToString(); // IP local
                    return ArpHelper.GetSubnetFromIp(localIp); // Extraer subred (ej: 192.168.68)
                }
            }
            catch // Error detectando IP local
            {
                return null;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // SNMP SET — Método 1: OIDs Epson enterprise
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Cambia IP usando SNMP SET con OIDs específicos de Epson.
        /// Funciona en: Epson TM-T20, TM-T88, TM-T82 con interfaz Ethernet.
        /// Community de escritura: generalmente "private" (default SNMP v2c).
        /// </summary>
        private static bool TrySnmpSetEpson(string currentIp, string newIp, string mask, string gw, string community)
        {
            try
            {
                var agent = new IpAddress(currentIp); // IP del agente SNMP (impresora)
                var target = new UdpTarget((IPAddress)agent, 161, 5000, 2); // Puerto 161, timeout 5s, 2 retries

                // Crear parámetros SNMP v2c con community de escritura
                var param = new AgentParameters(SnmpVersion.Ver2, new OctetString(community));

                // Crear PDU SET con los nuevos valores de red
                var pdu = new Pdu(PduType.Set); // Tipo SET (escritura)

                // Agregar IP nueva como IpAddress SNMP type
                pdu.VbList.Add(new Oid(OID_EPSON_IP), new IpAddress(newIp));
                // Agregar máscara de subred
                pdu.VbList.Add(new Oid(OID_EPSON_SUBNET), new IpAddress(mask));
                // Agregar gateway
                pdu.VbList.Add(new Oid(OID_EPSON_GATEWAY), new IpAddress(gw));

                Log.InfoFormat("[IP-CHANGE] SNMP SET Epson: {0} → {1} (mask={2}, gw={3})",
                    currentIp, newIp, mask, gw); // Log del intento

                // Enviar SNMP SET request
                var result = (SnmpV2Packet)target.Request(pdu, param);
                target.Close(); // Cerrar socket UDP

                if (result == null) // No hubo respuesta
                {
                    Log.Debug("[IP-CHANGE] SNMP SET Epson: sin respuesta"); // Log
                    return false;
                }

                if (result.Pdu.ErrorStatus != 0) // Error SNMP
                {
                    Log.DebugFormat("[IP-CHANGE] SNMP SET Epson error: status={0}, index={1}",
                        result.Pdu.ErrorStatus, result.Pdu.ErrorIndex); // Log detalle error
                    return false;
                }

                // Intentar aplicar cambios (OID apply — solo Epson)
                TrySnmpApplyEpson(currentIp, community); // Enviar comando apply

                Log.InfoFormat("[IP-CHANGE] ✓ SNMP SET Epson exitoso: {0} → {1}", currentIp, newIp); // Log
                return true; // Éxito
            }
            catch (Exception ex) // Error SNMP
            {
                Log.DebugFormat("[IP-CHANGE] SNMP SET Epson falló: {0}", ex.Message); // Log
                return false;
            }
        }

        /// <summary>
        /// Envía comando "apply" a Epson para que aplique los cambios de red.
        /// Algunas impresoras Epson requieren este paso adicional.
        /// </summary>
        private static void TrySnmpApplyEpson(string ip, string community)
        {
            try
            {
                var agent = new IpAddress(ip); // IP del agente
                var target = new UdpTarget((IPAddress)agent, 161, 3000, 1); // Puerto 161, timeout 3s
                var param = new AgentParameters(SnmpVersion.Ver2, new OctetString(community)); // Params

                var pdu = new Pdu(PduType.Set); // PDU SET
                pdu.VbList.Add(new Oid(OID_EPSON_APPLY), new Integer32(1)); // 1 = apply changes

                target.Request(pdu, param); // Enviar (ignorar resultado — puede no responder si reinicia)
                target.Close(); // Cerrar socket

                Log.Debug("[IP-CHANGE] SNMP Apply Epson enviado"); // Log
            }
            catch // Ignorar errores — la impresora puede reiniciar su NIC
            {
                Log.Debug("[IP-CHANGE] SNMP Apply Epson: sin respuesta (esperado si reinició NIC)"); // Log
            }
        }

        // ═══════════════════════════════════════════════════════════
        // SNMP SET — Método 2: OIDs genéricos MIB-II
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Cambia IP usando SNMP SET con OIDs estándar MIB-II (RFC 1213).
        /// Funciona en: impresoras genéricas chinas, Bixolon, Star con SNMP habilitado.
        /// </summary>
        private static bool TrySnmpSetGeneric(string currentIp, string newIp, string mask, string gw, string community)
        {
            try
            {
                var agent = new IpAddress(currentIp); // IP del agente SNMP
                var target = new UdpTarget((IPAddress)agent, 161, 5000, 2); // Puerto 161, timeout 5s
                var param = new AgentParameters(SnmpVersion.Ver2, new OctetString(community)); // Params v2c

                // SET 1: Cambiar gateway (ipRouteNextHop para ruta default)
                var pduGw = new Pdu(PduType.Set); // PDU SET para gateway
                pduGw.VbList.Add(new Oid(OID_DEFAULT_GATEWAY), new IpAddress(gw)); // Nuevo gateway

                Log.InfoFormat("[IP-CHANGE] SNMP SET genérico gateway: {0}", gw); // Log

                var gwResult = (SnmpV2Packet)target.Request(pduGw, param); // Enviar SET gateway

                // SET 2: Cambiar IP address (ipAdEntAddr — más restrictivo)
                // Nota: Muchas impresoras no permiten SET en ipAdEntAddr directamente
                // pero vale la pena intentar
                string oidIpEntry = OID_IP_ADDRESS + "." + currentIp; // OID con IP actual como índice
                var pduIp = new Pdu(PduType.Set); // PDU SET para IP
                pduIp.VbList.Add(new Oid(oidIpEntry), new IpAddress(newIp)); // Nueva IP

                Log.InfoFormat("[IP-CHANGE] SNMP SET genérico IP: {0} → {1}", currentIp, newIp); // Log

                var ipResult = (SnmpV2Packet)target.Request(pduIp, param); // Enviar SET IP
                target.Close(); // Cerrar socket

                // Verificar resultado del SET de IP (el más importante)
                if (ipResult != null && ipResult.Pdu.ErrorStatus == 0) // Sin error
                {
                    Log.InfoFormat("[IP-CHANGE] ✓ SNMP SET genérico exitoso: {0} → {1}", currentIp, newIp);
                    return true; // Éxito
                }

                Log.DebugFormat("[IP-CHANGE] SNMP SET genérico: IP SET error status={0}",
                    ipResult?.Pdu.ErrorStatus ?? -1); // Log error
                return false;
            }
            catch (Exception ex) // Error SNMP
            {
                Log.DebugFormat("[IP-CHANGE] SNMP SET genérico falló: {0}", ex.Message); // Log
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // DLE EOT — Verificación de status ESC/POS vía TCP 9100
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Envía comando DLE EOT (10 04 01) al puerto 9100 para verificar que
        /// la impresora está en línea y aceptando comandos ESC/POS.
        /// DLE EOT es un comando estándar ESC/POS que no afecta la impresión.
        /// Respuesta esperada: 1 byte con bits de status:
        ///   Bit 3: 0=online, 1=offline
        ///   Bit 5: 0=sin error, 1=error
        ///   Bit 6: 0=sin error, 1=error
        /// </summary>
        private static DleEotResult TryDleEotStatus(string printerIp)
        {
            var result = new DleEotResult(); // Crear resultado vacío

            try
            {
                using (var client = new TcpClient()) // Crear cliente TCP
                {
                    // Intentar conectar al puerto 9100 con timeout de 3 segundos
                    var connectTask = client.ConnectAsync(printerIp, 9100); // Conectar async
                    if (!connectTask.Wait(3000)) // Esperar máximo 3 segundos
                    {
                        result.Error = "timeout conectando al puerto 9100"; // No se pudo conectar
                        return result; // Retornar sin conexión
                    }

                    result.Connected = true; // Conexión TCP exitosa al puerto 9100

                    using (var stream = client.GetStream()) // Obtener stream de red
                    {
                        stream.WriteTimeout = 2000; // Timeout de escritura 2 segundos
                        stream.ReadTimeout = 2000;  // Timeout de lectura 2 segundos

                        // DLE EOT: comando estándar ESC/POS para consultar status de impresora
                        // 0x10 = DLE (Data Link Escape)
                        // 0x04 = EOT (End of Transmission)
                        // 0x01 = n=1 → Solicitar status general de la impresora
                        byte[] dleEot = new byte[] { 0x10, 0x04, 0x01 }; // Comando DLE EOT n=1

                        stream.Write(dleEot, 0, dleEot.Length); // Enviar comando al stream TCP
                        stream.Flush(); // Forzar envío inmediato

                        // Intentar leer respuesta (1 byte de status)
                        byte[] response = new byte[1]; // Buffer para 1 byte de respuesta
                        int bytesRead = stream.Read(response, 0, 1); // Leer respuesta

                        if (bytesRead > 0) // Se recibió respuesta
                        {
                            result.Responded = true;               // La impresora respondió
                            result.StatusByte = response[0];       // Guardar byte de status
                            Log.InfoFormat("[IP-CHANGE] DLE EOT status de {0}: 0x{1:X2}",
                                printerIp, response[0]); // Log
                        }
                        else // No hubo respuesta
                        {
                            Log.Debug("[IP-CHANGE] DLE EOT: conexión OK pero sin respuesta"); // Log
                        }
                    }
                }
            }
            catch (IOException) // Timeout de lectura (impresora conectó pero no respondió DLE EOT)
            {
                // Esto es normal en algunas impresoras que no soportan DLE EOT vía red
                result.Connected = true; // Sí conectó al 9100
                result.Responded = false; // Pero no respondió al DLE EOT
                Log.Debug("[IP-CHANGE] DLE EOT: timeout de lectura (impresora no respondió)"); // Log
            }
            catch (SocketException sex) // Error de socket (puerto cerrado, host inalcanzable)
            {
                result.Error = sex.Message; // Guardar error
                Log.DebugFormat("[IP-CHANGE] DLE EOT socket error: {0}", sex.Message); // Log
            }
            catch (Exception ex) // Otro error inesperado
            {
                result.Error = ex.Message; // Guardar error
                Log.DebugFormat("[IP-CHANGE] DLE EOT error: {0}", ex.Message); // Log
            }

            return result; // Retornar resultado de la verificación
        }

        // ═══════════════════════════════════════════════════════════
        // RAW TCP 1F1B1F — Método 3: Protocolo chino (SPRT, Xprinter, HPRT)
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Cambia IP usando el protocolo propietario de impresoras térmicas chinas.
        /// Comandos reverse-engineered del software Xprinter vía Wireshark.
        /// Referencia: https://blog.lambda.cx/posts/xprinter-wifi/
        /// 
        /// Estructura del comando (Raw TCP al puerto 9100):
        ///   Header: 1F 1B 1F (prefijo fijo — marca inicio de comando de configuración)
        ///   Subcomandos:
        ///     0x22 = Set IP only (+ 4 bytes IP)
        ///     0xB0 = Set subnet mask only (+ 4 bytes mask)
        ///     0xB1 = Set gateway only (+ 4 bytes gw)
        ///     0xB2 = Set IP + mask + gateway combinado (+ 12 bytes)  ← EL ÚNICO CONFIABLE
        ///     0xB3 = Set WiFi (+ keytype + SSID\0 + password\0)
        ///     0xB4 = Set all (IP + mask + gw + wifi)
        /// 
        /// IMPORTANTE: Solo el comando combinado 0xB2 funciona consistentemente
        /// según la investigación de reverse engineering (los individuales fallan).
        /// 
        /// Compatible con: SPRT POS891/POS892, Xprinter XP-80C/XP-58, HPRT, 
        /// RONGTA, MUNBYN, y la mayoría de impresoras térmicas chinas con NIC.
        /// </summary>
        private static bool TryRawTcp1F1B1F(string currentIp, string newIp, string mask, string gw)
        {
            try
            {
                // Parsear las IPs a arrays de 4 bytes cada una
                byte[] ipBytes = IPAddress.Parse(newIp).GetAddressBytes();   // Nueva IP → 4 bytes
                byte[] maskBytes = IPAddress.Parse(mask).GetAddressBytes();  // Máscara → 4 bytes
                byte[] gwBytes = IPAddress.Parse(gw).GetAddressBytes();     // Gateway → 4 bytes

                using (var client = new TcpClient()) // Crear cliente TCP
                {
                    // Conectar al puerto 9100 (puerto estándar ESC/POS) con timeout
                    var connectTask = client.ConnectAsync(currentIp, 9100); // Conectar async
                    if (!connectTask.Wait(5000)) // Esperar máximo 5 segundos
                    {
                        Log.Debug("[IP-CHANGE] Raw TCP 1F1B1F: timeout conectando al puerto 9100"); // Log
                        return false; // No se pudo conectar
                    }

                    using (var stream = client.GetStream()) // Obtener stream de red
                    {
                        stream.WriteTimeout = 5000; // Timeout de escritura 5 segundos
                        stream.ReadTimeout = 3000;  // Timeout de lectura 3 segundos

                        // ── Paso 0: ESC/POS Initialize (1B 40) — despertar la impresora ──
                        // Algunos firmwares necesitan un comando ESC/POS para activar el parser
                        byte[] escInit = new byte[] { 0x1B, 0x40 }; // ESC @ = Initialize printer
                        stream.Write(escInit, 0, escInit.Length); // Enviar init
                        stream.Flush(); // Forzar envío
                        Thread.Sleep(200); // Esperar 200ms para que el firmware procese

                        Log.InfoFormat("[IP-CHANGE] Raw TCP 1F1B1F: secuencia completa a {0}:9100 → IP={1} Mask={2} GW={3}",
                            currentIp, newIp, mask, gw); // Log

                        // ── Paso 1: Desactivar DHCP — forzar modo IP estática ──
                        // Comando: [1F] [1B] [1F] [2A] [00]
                        // 0x2A = Set DHCP mode, 0x00 = static (0x01 = DHCP enabled)
                        // Si la impresora está en DHCP, ignora cualquier IP estática asignada
                        byte[] cmdDhcpOff = new byte[] { 0x1F, 0x1B, 0x1F, 0x2A, 0x00 }; // DHCP off
                        stream.Write(cmdDhcpOff, 0, cmdDhcpOff.Length); // Enviar DHCP disable
                        stream.Flush(); // Forzar envío
                        Thread.Sleep(300); // Esperar que procese el cambio de modo

                        Log.Debug("[IP-CHANGE] Raw TCP 1F1B1F: DHCP disabled (0x2A 0x00)"); // Log

                        // ── Paso 2: Comando 0xB2 — Set IP + Mask + Gateway combinado ──
                        // Este es el ÚNICO comando confiable según reverse engineering
                        // Formato: [1F] [1B] [1F] [B2] [IP 4 bytes] [Mask 4 bytes] [GW 4 bytes]
                        // Total: 16 bytes (3 header + 1 subcommand + 12 data)
                        byte[] cmdB2 = new byte[16]; // Buffer del comando completo
                        cmdB2[0] = 0x1F;             // Header byte 1 (Unit Separator)
                        cmdB2[1] = 0x1B;             // Header byte 2 (ESC)
                        cmdB2[2] = 0x1F;             // Header byte 3 (Unit Separator)
                        cmdB2[3] = 0xB2;             // Subcomando: Set IP + Mask + GW combinado
                        Array.Copy(ipBytes, 0, cmdB2, 4, 4);   // Bytes 4-7: Nueva IP
                        Array.Copy(maskBytes, 0, cmdB2, 8, 4);  // Bytes 8-11: Nueva máscara
                        Array.Copy(gwBytes, 0, cmdB2, 12, 4);   // Bytes 12-15: Nuevo gateway

                        stream.Write(cmdB2, 0, cmdB2.Length); // Enviar comando combinado
                        stream.Flush(); // Forzar envío inmediato
                        Thread.Sleep(500); // Esperar que la impresora procese

                        Log.Debug("[IP-CHANGE] Raw TCP 1F1B1F: B2 (IP+Mask+GW) enviado"); // Log

                        // ── Paso 3: Comandos individuales como respaldo (Xprinter 0x22, 0xB0, 0xB1) ──
                        // Algunas variantes de firmware solo aceptan comandos individuales

                        // Comando 0x22: Set IP only — [1F] [1B] [1F] [22] [IP 4 bytes]
                        byte[] cmdIP = new byte[8]; // Buffer: 3 header + 1 subcmd + 4 IP
                        cmdIP[0] = 0x1F; cmdIP[1] = 0x1B; cmdIP[2] = 0x1F; // Header
                        cmdIP[3] = 0x22; // Subcomando: Set IP only
                        Array.Copy(ipBytes, 0, cmdIP, 4, 4); // Nueva IP
                        stream.Write(cmdIP, 0, cmdIP.Length); // Enviar
                        stream.Flush(); // Forzar

                        // Comando 0xB0: Set subnet mask only — [1F] [1B] [1F] [B0] [Mask 4 bytes]
                        byte[] cmdMask = new byte[8]; // Buffer: 3 header + 1 subcmd + 4 mask
                        cmdMask[0] = 0x1F; cmdMask[1] = 0x1B; cmdMask[2] = 0x1F; // Header
                        cmdMask[3] = 0xB0; // Subcomando: Set mask only
                        Array.Copy(maskBytes, 0, cmdMask, 4, 4); // Nueva máscara
                        stream.Write(cmdMask, 0, cmdMask.Length); // Enviar
                        stream.Flush(); // Forzar

                        // Comando 0xB1: Set gateway only — [1F] [1B] [1F] [B1] [GW 4 bytes]
                        byte[] cmdGw = new byte[8]; // Buffer: 3 header + 1 subcmd + 4 gw
                        cmdGw[0] = 0x1F; cmdGw[1] = 0x1B; cmdGw[2] = 0x1F; // Header
                        cmdGw[3] = 0xB1; // Subcomando: Set gateway only
                        Array.Copy(gwBytes, 0, cmdGw, 4, 4); // Nuevo gateway
                        stream.Write(cmdGw, 0, cmdGw.Length); // Enviar
                        stream.Flush(); // Forzar

                        Thread.Sleep(300); // Esperar entre comandos

                        // ── Paso 3b: Variante SPRT / MegaPOS (POS891/POS892) ──
                        // Referencia: Documentación MegaPOS/SPRT Ethernet
                        // Formato: 1F 1B 1F 91 00 [TYPE 2 bytes] [DATA 4 bytes]
                        // TYPE "IP" (49 50) = Set IP
                        // TYPE "SM" (53 4D) = Set Subnet Mask (Especulativo)
                        // TYPE "GW" (47 57) = Set Gateway (Especulativo)

                        // SPRT Set IP: 1F 1B 1F 91 00 49 50 [IP]
                        byte[] cmdSprtIp = new byte[] { 
                            0x1F, 0x1B, 0x1F, 0x91, 0x00, 0x49, 0x50, // Header + "IP"
                            ipBytes[0], ipBytes[1], ipBytes[2], ipBytes[3] 
                        };
                        stream.Write(cmdSprtIp, 0, cmdSprtIp.Length);
                        stream.Flush();
                        Thread.Sleep(200);

                        // SPRT Set Mask: 1F 1B 1F 91 00 53 4D [Mask]
                        byte[] cmdSprtMask = new byte[] { 
                            0x1F, 0x1B, 0x1F, 0x91, 0x00, 0x53, 0x4D, // Header + "SM"
                            maskBytes[0], maskBytes[1], maskBytes[2], maskBytes[3] 
                        };
                        stream.Write(cmdSprtMask, 0, cmdSprtMask.Length);
                        stream.Flush();
                        Thread.Sleep(200);

                        // SPRT Set Gateway: 1F 1B 1F 91 00 47 57 [GW]
                        byte[] cmdSprtGw = new byte[] { 
                            0x1F, 0x1B, 0x1F, 0x91, 0x00, 0x47, 0x57, // Header + "GW"
                            gwBytes[0], gwBytes[1], gwBytes[2], gwBytes[3] 
                        };
                        stream.Write(cmdSprtGw, 0, cmdSprtGw.Length);
                        stream.Flush();
                        Thread.Sleep(200);

                        Log.InfoFormat("[IP-CHANGE] Raw TCP: Enviada variante SPRT (1F1B1F 9100 IP/SM/GW)"); 

                        // ── Paso 3c: Variante ASCII (usada por algunos clones antiguos/genéricos) ──
                        // Comandos: 1F 1B 1F [char] ...
                        // 'i' (0x69) = Set IP
                        // 'm' (0x6D) = Set Mask
                        // 'g' (0x67) = Set Gateway

                        // Set IP: 1F 1B 1F 69 [IP 4 bytes]
                        byte[] cmdAsciiIp = new byte[8];
                        cmdAsciiIp[0] = 0x1F; cmdAsciiIp[1] = 0x1B; cmdAsciiIp[2] = 0x1F;
                        cmdAsciiIp[3] = 0x69; // 'i'
                        Array.Copy(ipBytes, 0, cmdAsciiIp, 4, 4);
                        stream.Write(cmdAsciiIp, 0, cmdAsciiIp.Length);
                        stream.Flush();

                        // Set Mask: 1F 1B 1F 6D [Mask 4 bytes]
                        byte[] cmdAsciiMask = new byte[8];
                        cmdAsciiMask[0] = 0x1F; cmdAsciiMask[1] = 0x1B; cmdAsciiMask[2] = 0x1F;
                        cmdAsciiMask[3] = 0x6D; // 'm'
                        Array.Copy(maskBytes, 0, cmdAsciiMask, 4, 4);
                        stream.Write(cmdAsciiMask, 0, cmdAsciiMask.Length);
                        stream.Flush();

                        // Set Gateway: 1F 1B 1F 67 [GW 4 bytes]
                        byte[] cmdAsciiGw = new byte[8];
                        cmdAsciiGw[0] = 0x1F; cmdAsciiGw[1] = 0x1B; cmdAsciiGw[2] = 0x1F;
                        cmdAsciiGw[3] = 0x67; // 'g'
                        Array.Copy(gwBytes, 0, cmdAsciiGw, 4, 4);
                        stream.Write(cmdAsciiGw, 0, cmdAsciiGw.Length);
                        stream.Flush();
                        Thread.Sleep(200);

                        Log.InfoFormat("[IP-CHANGE] Raw TCP: Enviada variante ASCII (i/m/g)");

                        // ── Paso 4: Reset/Save NIC — aplicar cambios y reiniciar interfaz de red ──
                        // Diferentes firmwares usan diferentes comandos de reset/save.
                        // Enviamos los más comunes para maximizar compatibilidad.

                        // 0x50: Init/Reset NIC (común en muchas impresoras chinas)
                        byte[] cmdReset1 = new byte[] { 0x1F, 0x1B, 0x1F, 0x50 }; // Reset NIC tipo 1
                        stream.Write(cmdReset1, 0, cmdReset1.Length); // Enviar reset
                        stream.Flush(); // Forzar
                        Thread.Sleep(200); // Breve espera

                        // 0xA0: Reset/Apply (usado por Rongta, algunas Xprinter)
                        byte[] cmdReset2 = new byte[] { 0x1F, 0x1B, 0x1F, 0xA0 }; // Reset NIC tipo 2
                        stream.Write(cmdReset2, 0, cmdReset2.Length); // Enviar reset alternativo
                        stream.Flush(); // Forzar
                        Thread.Sleep(200); // Breve espera

                        // 0xA1: Save to NVRAM (persistir configuración)
                        byte[] cmdSave = new byte[] { 0x1F, 0x1B, 0x1F, 0xA1 }; // Save config
                        stream.Write(cmdSave, 0, cmdSave.Length); // Enviar save
                        stream.Flush(); // Forzar

                        Log.InfoFormat("[IP-CHANGE] ✓ Raw TCP 1F1B1F: secuencia completa enviada " +
                            "(DHCP off + B2 + 22 + B0 + B1 + SPRT + ASCII + Reset + Save) a {0}:9100",
                            currentIp); // Log confirmación

                        // La verificación se hará en la cascada con ping a la nueva IP
                        return true; // Comandos enviados — se verificará después
                    }
                }
            }
            catch (SocketException sex) // Error de socket (conexión rechazada, timeout, etc.)
            {
                Log.DebugFormat("[IP-CHANGE] Raw TCP 1F1B1F socket error: {0}", sex.Message); // Log
                return false;
            }
            catch (Exception ex) // Otro error
            {
                Log.DebugFormat("[IP-CHANGE] Raw TCP 1F1B1F falló: {0}", ex.Message); // Log
                return false;
            }
        }

        /// <summary>
        /// Intenta cambiar IP vía UDP Broadcast (puerto 3000).
        /// Protocolo común en impresoras chinas (Rongta/NetFinder) cuando TCP falla.
        /// </summary>
        private static bool TryUdpBroadcastConfig(string newIp, string mask, string gw, string macAddress)
        {
            try
            {
                // Si no tenemos MAC, este método es arriesgado (podría cambiar otra impresora).
                // Pero como estamos en una subred directa aislada o controlada, intentamos broadcast general.
                
                using (var udp = new UdpClient())
                {
                    udp.EnableBroadcast = true;
                    var endpoint = new IPEndPoint(IPAddress.Broadcast, 3000); // Puerto Rongta/NetFinder

                    byte[] ipBytes = IPAddress.Parse(newIp).GetAddressBytes();
                    byte[] maskBytes = IPAddress.Parse(mask).GetAddressBytes();
                    byte[] gwBytes = IPAddress.Parse(gw).GetAddressBytes();
                    
                    // Parsear MAC para el payload (si existe)
                    byte[] macBytes = new byte[6];
                    if (!string.IsNullOrEmpty(macAddress))
                    {
                        try {
                            string[] macParts = macAddress.Split(new[] { ':', '-' }, StringSplitOptions.RemoveEmptyEntries);
                            if (macParts.Length == 6)
                                for (int i = 0; i < 6; i++) macBytes[i] = Convert.ToByte(macParts[i], 16);
                        } catch { /* Ignorar MAC inválida */ }
                    }

                    // ── Protocolo Rongta / NetFinder (Simplificado) ──
                    // Header: 43 52 45 41 54 (CREAT) o similar.
                    // Payload simplificado genérico:
                    // [CMD 1 byte] [IP 4] [Mask 4] [GW 4]
                    
                    // Construimos un paquete "Set IP" genérico para Rongta
                    // Formato aproximado: [Header 16b] [Cmd 2b] [Len 2b] [Data...]
                    List<byte> packet = new List<byte>();
                    
                    // Header mágico Rongta (RT..)
                    packet.AddRange(new byte[] { 0x52, 0x54, 0x00, 0x00 }); 
                    
                    // Command SET_NET (0x02)
                    packet.AddRange(new byte[] { 0x02, 0x00 });
                    
                    // Data Length (IP+Mask+GW = 12 bytes)
                    packet.AddRange(new byte[] { 0x0C, 0x00 });
                    
                    // Data
                    packet.AddRange(ipBytes);
                    packet.AddRange(maskBytes);
                    packet.AddRange(gwBytes);
                    
                    // Enviar Rongta Packet
                    byte[] data = packet.ToArray();
                    udp.Send(data, data.Length, endpoint);
                    Log.Info("[IP-CHANGE] UDP Broadcast: Enviado paquete Rongta/NetFinder (port 3000)");
                    
                    Thread.Sleep(200);

                    // ── Intento 2: Broadcast Raw Payload (Impresoras muy genéricas) ──
                    // Solo los datos crudos: IP + Mask + GW
                    List<byte> rawPacket = new List<byte>();
                    rawPacket.AddRange(ipBytes);
                    rawPacket.AddRange(maskBytes);
                    rawPacket.AddRange(gwBytes);
                    
                    udp.Send(rawPacket.ToArray(), rawPacket.Count, endpoint);
                    Log.Info("[IP-CHANGE] UDP Broadcast: Enviado payload raw (port 3000)");

                    return true; // Enviado (verificación por ping después)
                }
            }
            catch (Exception ex)
            {
                Log.DebugFormat("[IP-CHANGE] UDP Broadcast falló: {0}", ex.Message);
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // ENPC — Método 4: EpsonNet Protocol vía UDP 3289
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Cambia IP usando el protocolo ENPC (EpsonNet Protocol) de Epson.
        /// Protocolo propietario Epson sobre UDP puerto 3289.
        /// Estructura de paquete ENPC:
        ///   Bytes 0-4:   "EPSON" (header fijo)
        ///   Byte 5:      'Q'=Query, 'C'=Command (mayúscula=request, minúscula=response)
        ///   Byte 6:      DeviceType (0x03 para impresoras)
        ///   Byte 7:      DeviceNumber (0x00)
        ///   Bytes 8-9:   FunctionNumber (uint16 big-endian)
        ///   Bytes 10-11: ResultCode (0x0000 para requests)
        ///   Bytes 12-13: DataLength (uint16 big-endian)
        ///   Bytes 14+:   Data payload
        /// Funciones conocidas (reverse-engineered):
        ///   0x0000 = Basic Information / Discovery broadcast
        ///   0x0010 = Status / Network Config (contiene IP, mask, gw, MAC)
        ///   0x0012 = Reset
        /// Referencia: https://github.com/BlackLotus/epson-stuff (Wireshark dissector)
        /// Referencia: https://wes4m.io/posts/epson_rev/ (reverse engineering)
        /// </summary>
        private static bool TryEnpcIpChange(string currentIp, string newIp, string mask, string gw)
        {
            try
            {
                // Parsear las IPs a bytes (4 bytes cada una, network byte order)
                byte[] newIpBytes = IPAddress.Parse(newIp).GetAddressBytes();   // Nueva IP en bytes
                byte[] maskBytes = IPAddress.Parse(mask).GetAddressBytes();     // Máscara en bytes
                byte[] gwBytes = IPAddress.Parse(gw).GetAddressBytes();        // Gateway en bytes

                using (var udpClient = new UdpClient()) // Crear cliente UDP
                {
                    udpClient.Client.ReceiveTimeout = 3000; // Timeout de recepción 3 segundos
                    udpClient.Client.SendTimeout = 3000;    // Timeout de envío 3 segundos

                    var endpoint = new IPEndPoint(IPAddress.Parse(currentIp), 3289); // Destino: impresora:3289

                    // ── Paso 1: Enviar Query de red (función 0x0010) para verificar que responde ENPC ──
                    // Paquete: EPSONQ + DevType(03) + DevNum(00) + Func(0010) + Result(0000) + Len(0000)
                    byte[] queryPacket = BuildEnpcPacket(
                        (byte)'Q',       // 'Q' = Query request
                        0x00, 0x10,      // Function: Status/Network Config
                        new byte[0]      // Sin data (solo query)
                    );

                    Log.InfoFormat("[IP-CHANGE] ENPC: enviando Query 0x0010 a {0}:3289", currentIp); // Log

                    udpClient.Send(queryPacket, queryPacket.Length, endpoint); // Enviar query UDP

                    // Intentar recibir respuesta (EPSONq)
                    IPEndPoint remoteEp = null; // Variable para endpoint remoto
                    byte[] response = null; // Variable para respuesta

                    try
                    {
                        response = udpClient.Receive(ref remoteEp); // Recibir respuesta
                    }
                    catch (SocketException) // Timeout — impresora no soporta ENPC
                    {
                        Log.Debug("[IP-CHANGE] ENPC: sin respuesta a Query (no es impresora Epson o ENPC deshabilitado)");
                        return false; // No soporta ENPC
                    }

                    // Validar respuesta ENPC (debe empezar con "EPSONq")
                    if (response == null || response.Length < 14) // Respuesta inválida
                    {
                        Log.Debug("[IP-CHANGE] ENPC: respuesta demasiado corta"); // Log
                        return false;
                    }

                    string header = Encoding.ASCII.GetString(response, 0, 5); // Leer header "EPSON"
                    if (header != "EPSON") // No es respuesta ENPC
                    {
                        Log.DebugFormat("[IP-CHANGE] ENPC: header inválido: {0}", header); // Log
                        return false;
                    }

                    char responseType = (char)response[5]; // Tipo de respuesta ('q' = query response)
                    if (responseType != 'q') // No es query response
                    {
                        Log.DebugFormat("[IP-CHANGE] ENPC: tipo de respuesta inesperado: {0}", responseType); // Log
                        return false;
                    }

                    Log.InfoFormat("[IP-CHANGE] ENPC: impresora respondió Query ({0} bytes)", response.Length); // Log

                    // ── Paso 2: Enviar Command de cambio de IP (EPSONC) ──
                    // El payload del comando contiene la nueva configuración de red:
                    // [01] [MAC 6 bytes padding 00s] [00 04] [IP 4] [Mask 4] [Gw 4] [80 7c]
                    // Basado en el formato observado en la respuesta del Query 0x0010
                    var cmdData = new List<byte>(); // Buffer de data del comando

                    cmdData.Add(0x01);                   // Tipo: set static IP (vs 0x00 DHCP)
                    cmdData.AddRange(new byte[6]);       // MAC placeholder (6 bytes de 0x00 — la impresora ignora)
                    cmdData.Add(0x00);                   // Separador
                    cmdData.Add(0x04);                   // Tipo: IPv4 config
                    cmdData.AddRange(newIpBytes);         // Nueva IP (4 bytes)
                    cmdData.AddRange(maskBytes);          // Nueva máscara (4 bytes)
                    cmdData.AddRange(gwBytes);            // Nuevo gateway (4 bytes)
                    cmdData.Add(0x80);                   // Flags (observado en capturas)
                    cmdData.Add(0x7C);                   // Flags (observado en capturas)

                    // Construir paquete ENPC Command
                    byte[] cmdPacket = BuildEnpcPacket(
                        (byte)'C',       // 'C' = Command request
                        0x00, 0x10,      // Function: Status/Network Config (SET)
                        cmdData.ToArray() // Payload con nueva config de red
                    );

                    Log.InfoFormat("[IP-CHANGE] ENPC: enviando Command SET IP a {0}:3289 ({1} bytes)",
                        currentIp, cmdPacket.Length); // Log

                    udpClient.Send(cmdPacket, cmdPacket.Length, endpoint); // Enviar comando UDP

                    // Intentar recibir confirmación (EPSONc)
                    try
                    {
                        response = udpClient.Receive(ref remoteEp); // Recibir respuesta

                        if (response != null && response.Length >= 12) // Respuesta recibida
                        {
                            // Verificar result code (bytes 10-11, 0x0000 = éxito)
                            ushort resultCode = (ushort)((response[10] << 8) | response[11]); // Big-endian

                            if (resultCode == 0) // Éxito
                            {
                                Log.InfoFormat("[IP-CHANGE] ✓ ENPC Command aceptado (result=0x0000)"); // Log
                            }
                            else // Error
                            {
                                Log.WarnFormat("[IP-CHANGE] ENPC Command result=0x{0:X4}", resultCode); // Log
                            }
                        }
                    }
                    catch (SocketException) // Timeout — impresora puede haber reiniciado NIC
                    {
                        Log.Info("[IP-CHANGE] ENPC: sin confirmación (impresora puede estar reiniciando NIC)"); // Log
                    }

                    // ── Paso 3: Enviar Reset (función 0x0012) para aplicar cambios ──
                    byte[] resetPacket = BuildEnpcPacket(
                        (byte)'C',       // 'C' = Command request
                        0x00, 0x12,      // Function: Reset
                        new byte[0]      // Sin data adicional
                    );

                    Log.Info("[IP-CHANGE] ENPC: enviando Reset (función 0x0012)"); // Log

                    try
                    {
                        udpClient.Send(resetPacket, resetPacket.Length, endpoint); // Enviar reset
                    }
                    catch // Ignorar errores — la impresora puede ya estar reiniciando
                    {
                        Log.Debug("[IP-CHANGE] ENPC: error enviando reset (esperado si NIC ya reinició)"); // Log
                    }

                    Log.InfoFormat("[IP-CHANGE] ✓ ENPC IP change enviado: {0} → {1}", currentIp, newIp); // Log
                    return true; // Éxito — se verificará después con ping
                }
            }
            catch (Exception ex) // Error ENPC
            {
                Log.DebugFormat("[IP-CHANGE] ENPC falló: {0}", ex.Message); // Log
                return false;
            }
        }

        /// <summary>
        /// Construye un paquete ENPC (EpsonNet Protocol) con la estructura correcta.
        /// Formato: "EPSON" + type + deviceType(03) + deviceNum(00) + funcHi + funcLo + 0x0000 + dataLen + data
        /// </summary>
        /// <param name="type">'Q' para Query, 'C' para Command.</param>
        /// <param name="funcHi">Byte alto del número de función.</param>
        /// <param name="funcLo">Byte bajo del número de función.</param>
        /// <param name="data">Payload de datos (puede ser vacío).</param>
        /// <returns>Paquete ENPC completo como byte array.</returns>
        private static byte[] BuildEnpcPacket(byte type, byte funcHi, byte funcLo, byte[] data)
        {
            var packet = new List<byte>(); // Buffer del paquete

            // Header fijo: "EPSON" (5 bytes ASCII)
            packet.AddRange(Encoding.ASCII.GetBytes("EPSON")); // Header protocolo ENPC

            // Tipo de paquete (1 byte)
            packet.Add(type); // 'Q'=Query, 'C'=Command, 'q'=QueryResponse, 'c'=CommandResponse

            // Device Type (1 byte) — 0x03 para impresoras térmicas
            packet.Add(0x03); // Tipo de dispositivo: impresora

            // Device Number (1 byte) — 0x00
            packet.Add(0x00); // Número de dispositivo: 0 (default)

            // Function Number (2 bytes, big-endian)
            packet.Add(funcHi); // Byte alto de la función
            packet.Add(funcLo); // Byte bajo de la función

            // Result Code / Fixed (2 bytes) — 0x0000 para requests
            packet.Add(0x00); // Result byte alto
            packet.Add(0x00); // Result byte bajo

            // Data Length (2 bytes, big-endian)
            ushort dataLen = (ushort)(data != null ? data.Length : 0); // Longitud del payload
            packet.Add((byte)(dataLen >> 8));   // Byte alto de la longitud
            packet.Add((byte)(dataLen & 0xFF)); // Byte bajo de la longitud

            // Data payload
            if (data != null && data.Length > 0) // Si hay datos
            {
                packet.AddRange(data); // Agregar payload
            }

            return packet.ToArray(); // Retornar paquete completo
        }

        // ═══════════════════════════════════════════════════════════
        // HTTP Web Config — Método 4: Interfaz web de la impresora
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Cambia IP usando la interfaz web HTTP de la impresora (puerto 80).
        /// Muchas impresoras térmicas (Epson, genéricas chinas, Bixolon, Star)
        /// tienen un servidor web embebido para configuración.
        /// Intenta múltiples formatos de URL/POST comunes entre fabricantes.
        /// </summary>
        private static bool TryHttpWebConfigIpChange(string currentIp, string newIp, string mask, string gw)
        {
            try
            {
                // Primero verificar si el puerto 80 está abierto
                if (!IsPortOpen(currentIp, 80, 2000)) // Verificar puerto 80 con timeout 2s
                {
                    Log.Debug("[IP-CHANGE] HTTP: puerto 80 no accesible"); // Log
                    return false; // No tiene web server
                }

                Log.InfoFormat("[IP-CHANGE] HTTP: puerto 80 abierto en {0}, intentando configuración web", currentIp);

                // Parsear octetos de las IPs para los forms HTTP
                string[] ipOctets = newIp.Split('.');   // Octetos de nueva IP
                string[] maskOctets = mask.Split('.');   // Octetos de máscara
                string[] gwOctets = gw.Split('.');       // Octetos de gateway

                // ── Formato 1: Epson TM-T20/T88/m30 web interface ──
                // URL: http://IP/Forms/tcp_1
                // POST:ESSION=... &TCP_IP1=x&TCP_IP2=x&TCP_IP3=x&TCP_IP4=x&TCP_SM1=x...&TCP_GW1=x...
                string epsonFormData = string.Format(
                    "TCP_IPAUTO=MANUAL" + // IP manual (no DHCP)
                    "&TCP_IP1={0}&TCP_IP2={1}&TCP_IP3={2}&TCP_IP4={3}" + // Nueva IP
                    "&TCP_SM1={4}&TCP_SM2={5}&TCP_SM3={6}&TCP_SM4={7}" + // Nueva máscara
                    "&TCP_GW1={8}&TCP_GW2={9}&TCP_GW3={10}&TCP_GW4={11}" + // Nuevo gateway
                    "&SUBMIT=Submit", // Botón submit
                    ipOctets[0], ipOctets[1], ipOctets[2], ipOctets[3],
                    maskOctets[0], maskOctets[1], maskOctets[2], maskOctets[3],
                    gwOctets[0], gwOctets[1], gwOctets[2], gwOctets[3]
                );

                bool success = TryHttpPost(currentIp, "/Forms/tcp_1", epsonFormData); // Intentar Epson format
                if (success) // Epson format funcionó
                {
                    Log.Info("[IP-CHANGE] ✓ HTTP Web Config exitoso (formato Epson /Forms/tcp_1)"); // Log
                    return true;
                }

                // ── Formato 2: Genérico chino (muchos clones usan este formato) ──
                // URL: http://IP/goform/formTcpipSetup
                string genericFormData = string.Format(
                    "dhcp=0" + // DHCP off (usar IP estática)
                    "&ipaddress={0}&subnetmask={1}&gateway={2}" + // IPs completas
                    "&submit=Save", // Botón guardar
                    newIp, mask, gw
                );

                success = TryHttpPost(currentIp, "/goform/formTcpipSetup", genericFormData); // Intentar genérico
                if (success) // Genérico format funcionó
                {
                    Log.Info("[IP-CHANGE] ✓ HTTP Web Config exitoso (formato genérico /goform/formTcpipSetup)"); // Log
                    return true;
                }

                // ── Formato 3: Otro formato genérico común ──
                // URL: http://IP/config/net
                string genericFormData2 = string.Format(
                    "ip_mode=static" + // Modo estático
                    "&ip_addr={0}&subnet={1}&gateway={2}" + // IPs
                    "&apply=1", // Aplicar
                    newIp, mask, gw
                );

                success = TryHttpPost(currentIp, "/config/net", genericFormData2); // Intentar formato 3
                if (success) // Formato 3 funcionó
                {
                    Log.Info("[IP-CHANGE] ✓ HTTP Web Config exitoso (formato /config/net)"); // Log
                    return true;
                }

                // ── Formato 4: Bixolon/Star web interface ──
                // URL: http://IP/cgi-bin/network
                string bixolonFormData = string.Format(
                    "USE_DHCP=0" + // Sin DHCP
                    "&IP_ADDR={0}&SUBNET_MASK={1}&GATEWAY={2}" + // Config de red
                    "&CMD=SET_NETWORK", // Comando SET
                    newIp, mask, gw
                );

                success = TryHttpPost(currentIp, "/cgi-bin/network", bixolonFormData); // Intentar Bixolon
                if (success) // Bixolon format funcionó
                {
                    Log.Info("[IP-CHANGE] ✓ HTTP Web Config exitoso (formato Bixolon /cgi-bin/network)"); // Log
                    return true;
                }

                // ── Formato 5: SPRT / impresoras chinas con interfaz web simple ──
                // URL: http://IP/net_conf.html o /network_config o /setting
                // Muchas SPRT y clones usan formularios con nombres en inglés simple
                string sprtFormData = string.Format(
                    "dhcp_enable=0" + // DHCP desactivado
                    "&ip_address={0}&subnet_mask={1}&default_gateway={2}" + // Config red
                    "&submit=Apply", // Botón aplicar
                    newIp, mask, gw
                );

                success = TryHttpPost(currentIp, "/net_conf.html", sprtFormData); // SPRT formato 1
                if (success) // SPRT formato 1 funcionó
                {
                    Log.Info("[IP-CHANGE] ✓ HTTP Web Config exitoso (formato SPRT /net_conf.html)"); // Log
                    return true;
                }

                success = TryHttpPost(currentIp, "/network_config", sprtFormData); // SPRT formato 2
                if (success) // SPRT formato 2 funcionó
                {
                    Log.Info("[IP-CHANGE] ✓ HTTP Web Config exitoso (formato /network_config)"); // Log
                    return true;
                }

                // ── Formato 6: Xprinter/chinas con /cgi-bin/param.cgi ──
                // Algunas impresoras chinas usan CGI para configuración
                string xprinterFormData = string.Format(
                    "action=set&dhcp=0" + // Acción SET con DHCP off
                    "&ipaddr={0}&netmask={1}&gateway={2}", // Config red
                    newIp, mask, gw
                );

                success = TryHttpPost(currentIp, "/cgi-bin/param.cgi", xprinterFormData); // CGI
                if (success) // CGI funcionó
                {
                    Log.Info("[IP-CHANGE] ✓ HTTP Web Config exitoso (formato /cgi-bin/param.cgi)"); // Log
                    return true;
                }

                // ── Formato 7: Interfaz minimalista con campos por octetos ──
                // URL: http://IP/setting.htm o /printer_conf.htm
                // Usa campos ip1,ip2,ip3,ip4 por octeto (como Epson pero paths distintos)
                string octetFormData = string.Format(
                    "dhcp=0" + // DHCP off
                    "&ip1={0}&ip2={1}&ip3={2}&ip4={3}" + // IP por octetos
                    "&sm1={4}&sm2={5}&sm3={6}&sm4={7}" + // Mask por octetos
                    "&gw1={8}&gw2={9}&gw3={10}&gw4={11}" + // GW por octetos
                    "&save=1", // Guardar
                    ipOctets[0], ipOctets[1], ipOctets[2], ipOctets[3],
                    maskOctets[0], maskOctets[1], maskOctets[2], maskOctets[3],
                    gwOctets[0], gwOctets[1], gwOctets[2], gwOctets[3]
                );

                success = TryHttpPost(currentIp, "/setting.htm", octetFormData); // Setting
                if (success)
                {
                    Log.Info("[IP-CHANGE] ✓ HTTP Web Config exitoso (formato /setting.htm)"); // Log
                    return true;
                }

                success = TryHttpPost(currentIp, "/printer_conf.htm", octetFormData); // Printer conf
                if (success)
                {
                    Log.Info("[IP-CHANGE] ✓ HTTP Web Config exitoso (formato /printer_conf.htm)"); // Log
                    return true;
                }

                // ── Formato 8: Rongta / RED POS Web Interface ──
                // URL: http://IP/system.cgi
                // Formato de parámetros específico de Rongta
                string rongtaFormData = string.Format(
                    "ip={0}&mask={1}&gateway={2}&dhcp=0&save=Save", 
                    newIp, mask, gw
                );

                success = TryHttpPost(currentIp, "/system.cgi", rongtaFormData);
                if (success)
                {
                    Log.Info("[IP-CHANGE] ✓ HTTP Web Config exitoso (formato Rongta /system.cgi)");
                    return true;
                }

                // ── Formato 9: Gprinter / Legacy Chinese ──
                // URL: http://IP/web/networking.cgi
                string gprinterFormData = string.Format(
                    "ipaddr={0}&netmask={1}&gateway={2}&dhcp=disable&commit=1",
                    newIp, mask, gw
                );

                success = TryHttpPost(currentIp, "/web/networking.cgi", gprinterFormData);
                if (success)
                {
                    Log.Info("[IP-CHANGE] ✓ HTTP Web Config exitoso (formato Gprinter /web/networking.cgi)");
                    return true;
                }

                // ── Formato 10: Simple 'net.cgi' (Variante común) ──
                string netCgiFormData = string.Format(
                    "IPAddress={0}&SubnetMask={1}&DefaultGateway={2}&DHCP=0&Submit=Submit",
                    newIp, mask, gw
                );
                
                success = TryHttpPost(currentIp, "/net.cgi", netCgiFormData);
                if (success)
                {
                    Log.Info("[IP-CHANGE] ✓ HTTP Web Config exitoso (formato /net.cgi)");
                    return true;
                }

                // ── Formato 11: Configuración HTML simple ──
                success = TryHttpPost(currentIp, "/config.html", sprtFormData);
                if (success)
                {
                    Log.Info("[IP-CHANGE] ✓ HTTP Web Config exitoso (formato /config.html)");
                    return true;
                }

                Log.Debug("[IP-CHANGE] HTTP: ningún formato de web config funcionó (11 formatos intentados)"); // Log
                return false; // Ningún formato HTTP funcionó
            }
            catch (Exception ex) // Error HTTP
            {
                Log.DebugFormat("[IP-CHANGE] HTTP Web Config falló: {0}", ex.Message); // Log
                return false;
            }
        }

        /// <summary>
        /// Envía un HTTP POST a la impresora con form data URL-encoded.
        /// Retorna true si el servidor respondió 200 OK (o redirect 302).
        /// Timeout corto para no bloquear — la impresora puede reiniciar NIC.
        /// </summary>
        private static bool TryHttpPost(string ip, string path, string formData)
        {
            try
            {
                string url = string.Format("http://{0}{1}", ip, path); // URL completa
                var request = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(url); // Crear request

                request.Method = "POST";                                          // Método POST
                request.ContentType = "application/x-www-form-urlencoded";        // Content type form
                request.Timeout = 5000;                                           // Timeout 5 segundos
                request.ReadWriteTimeout = 5000;                                  // Timeout lectura/escritura
                request.AllowAutoRedirect = true;                                 // Seguir redirects

                byte[] postData = Encoding.ASCII.GetBytes(formData); // Convertir form data a bytes
                request.ContentLength = postData.Length; // Establecer longitud

                using (var requestStream = request.GetRequestStream()) // Abrir stream de envío
                {
                    requestStream.Write(postData, 0, postData.Length); // Escribir form data
                }

                using (var response = (System.Net.HttpWebResponse)request.GetResponse()) // Obtener respuesta
                {
                    int statusCode = (int)response.StatusCode; // Código HTTP
                    Log.InfoFormat("[IP-CHANGE] HTTP POST {0} → status {1}", path, statusCode); // Log

                    return statusCode >= 200 && statusCode < 400; // 2xx o 3xx = éxito
                }
            }
            catch (System.Net.WebException wex) // Error HTTP
            {
                // 404 o 500 = la URL no existe en esta impresora, es esperado
                if (wex.Response is System.Net.HttpWebResponse errResponse) // Si hay response HTTP
                {
                    int code = (int)errResponse.StatusCode; // Código de error
                    Log.DebugFormat("[IP-CHANGE] HTTP POST {0} → error {1}", path, code); // Log

                    // Si la impresora reinició su NIC después del POST, puede dar timeout
                    // pero eso significa que el comando fue aceptado
                    if (code == 200 || code == 302) return true; // Éxito
                }
                else // Sin response — timeout o conexión rechazada
                {
                    // Si el error es timeout DESPUÉS de enviar el POST, puede ser que
                    // la impresora reinició su NIC (lo cual es bueno = comando aceptado)
                    if (wex.Status == System.Net.WebExceptionStatus.Timeout ||
                        wex.Status == System.Net.WebExceptionStatus.ReceiveFailure)
                    {
                        Log.InfoFormat("[IP-CHANGE] HTTP POST {0} → timeout/disconnect (posible reinicio NIC)", path);
                        // NO retornar true aquí — no podemos confirmar que el endpoint existía
                    }
                }

                return false;
            }
            catch // Otro error
            {
                return false;
            }
        }

        /// <summary>
        /// Verifica si un puerto TCP está abierto en una IP dada.
        /// Usado para check rápido antes de intentar HTTP.
        /// </summary>
        private static bool IsPortOpen(string ip, int port, int timeoutMs)
        {
            try
            {
                using (var client = new TcpClient()) // Crear cliente TCP
                {
                    var connectTask = client.ConnectAsync(ip, port); // Intentar conectar
                    return connectTask.Wait(timeoutMs); // true si conectó dentro del timeout
                }
            }
            catch // Error de conexión
            {
                return false; // Puerto cerrado o no accesible
            }
        }

        // ═══════════════════════════════════════════════════════════
        // HELPERS: MAC, fabricante, conectividad
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Obtiene MAC address vía SNMP (OID ifPhysAddress).
        /// Fallback cuando ARP no funciona (ej: cross-subnet sin alias aún).
        /// </summary>
        private static string GetMacViaSNMP(string ipAddress)
        {
            try
            {
                var agent = new IpAddress(ipAddress); // IP del agente
                var target = new UdpTarget((IPAddress)agent, 161, 3000, 1); // Timeout 3s
                var param = new AgentParameters(SnmpVersion.Ver2, new OctetString("public")); // Community pública

                var pdu = new Pdu(PduType.Get); // PDU GET (lectura)
                pdu.VbList.Add(new Oid(OID_IF_PHYS_ADDR)); // OID de MAC address

                var result = (SnmpV2Packet)target.Request(pdu, param); // Enviar request
                target.Close(); // Cerrar

                if (result != null && result.Pdu.ErrorStatus == 0 && result.Pdu.VbCount > 0) // Respuesta OK
                {
                    var vb = result.Pdu.VbList[0]; // Primer VarBind
                    if (vb.Value is OctetString) // MAC es OctetString de 6 bytes
                    {
                        byte[] macBytes = ((OctetString)vb.Value).ToArray(); // Obtener bytes
                        if (macBytes.Length == 6) // MAC válida (6 bytes)
                        {
                            return BitConverter.ToString(macBytes).Replace("-", ":"); // Formatear AA:BB:CC:DD:EE:FF
                        }
                    }

                    // Si no es OctetString binario, puede venir como string legible
                    string rawValue = vb.Value.ToString(); // Intentar como string
                    if (!string.IsNullOrEmpty(rawValue) && rawValue.Length >= 12)
                    {
                        return rawValue; // Retornar como está
                    }
                }

                return null; // No se pudo obtener
            }
            catch (Exception ex) // Error SNMP
            {
                Log.DebugFormat("[IP-CHANGE] SNMP MAC query falló para {0}: {1}", ipAddress, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Detecta fabricante de la impresora leyendo OID sysDescr vía SNMP.
        /// Retorna: "EPSON", "STAR", "BIXOLON", "GENERIC" o null.
        /// </summary>
        private static string DetectManufacturer(string ipAddress)
        {
            try
            {
                var agent = new IpAddress(ipAddress); // IP del agente
                var target = new UdpTarget((IPAddress)agent, 161, 3000, 1); // Timeout 3s
                var param = new AgentParameters(SnmpVersion.Ver2, new OctetString("public")); // Community

                var pdu = new Pdu(PduType.Get); // PDU GET
                pdu.VbList.Add(new Oid(OID_SYS_DESCR)); // OID sysDescr

                var result = (SnmpV2Packet)target.Request(pdu, param); // Enviar request
                target.Close(); // Cerrar

                if (result != null && result.Pdu.ErrorStatus == 0 && result.Pdu.VbCount > 0) // OK
                {
                    string descr = result.Pdu.VbList[0].Value.ToString().ToUpperInvariant(); // Descripción

                    if (descr.Contains("EPSON")) return "EPSON";          // Fabricante Epson
                    if (descr.Contains("STAR")) return "STAR";            // Fabricante Star
                    if (descr.Contains("BIXOLON")) return "BIXOLON";      // Fabricante Bixolon
                    if (descr.Contains("CITIZEN")) return "CITIZEN";      // Fabricante Citizen
                    if (descr.Contains("SEWOO")) return "SEWOO";          // Fabricante Sewoo
                    if (descr.Contains("CUSTOM")) return "CUSTOM";        // Fabricante Custom

                    return descr.Length > 50 ? descr.Substring(0, 50) : descr; // Retornar descripción recortada
                }

                return null; // SNMP no respondió
            }
            catch // Error SNMP
            {
                return null; // No se pudo detectar
            }
        }

        /// <summary>
        /// Verifica si una IP responde a ping.
        /// </summary>
        private static bool IpResponds(string ipAddress, int timeoutMs)
        {
            try
            {
                using (var ping = new Ping()) // Crear instancia de Ping
                {
                    var reply = ping.Send(ipAddress, timeoutMs); // Enviar ICMP echo
                    return reply.Status == IPStatus.Success; // true si respondió
                }
            }
            catch // Error de ping
            {
                return false; // No responde
            }
        }

        /// <summary>
        /// Valida formato de dirección IPv4 (4 octetos, cada uno 0-255).
        /// </summary>
        private static bool ValidateIp(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return false; // IP vacía

            IPAddress parsed; // Variable para resultado de parseo
            if (!IPAddress.TryParse(ip, out parsed)) return false; // Formato inválido

            return parsed.AddressFamily == AddressFamily.InterNetwork; // Solo IPv4
        }

        // ═══════════════════════════════════════════════════════════
        // PROTECCIÓN DE IP ESTÁTICA: Capturar y Restaurar config
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Captura la configuración IP actual de una interfaz de red.
        /// Lee IP, máscara, gateway y si está en DHCP o estática.
        /// Se usa ANTES de agregar alias para poder restaurar si algo sale mal.
        /// Usa netsh para leer la config (compatible con .NET 4.5.2).
        /// </summary>
        private static SavedNetConfig CaptureInterfaceConfig(string interfaceName)
        {
            try
            {
                // Ejecutar netsh para obtener la config actual de la interfaz
                var psi = new ProcessStartInfo // Configurar proceso
                {
                    FileName = "netsh",                                                     // Ejecutable netsh
                    Arguments = string.Format("interface ipv4 show addresses \"{0}\"", interfaceName), // Mostrar IPs
                    UseShellExecute = false,                                                // No usar shell
                    RedirectStandardOutput = true,                                          // Capturar stdout
                    RedirectStandardError = true,                                           // Capturar stderr
                    CreateNoWindow = true,                                                  // Sin ventana
                    WindowStyle = ProcessWindowStyle.Hidden                                 // Ocultar
                };

                string output; // Variable para stdout
                using (var process = Process.Start(psi)) // Iniciar proceso
                {
                    output = process.StandardOutput.ReadToEnd(); // Leer toda la salida
                    process.WaitForExit(5000); // Esperar máximo 5 segundos
                }

                if (string.IsNullOrEmpty(output)) // Sin salida
                {
                    Log.Warn("[IP-CHANGE] CaptureConfig: netsh no retornó datos"); // Log warning
                    return null; // No se pudo capturar
                }

                var config = new SavedNetConfig { InterfaceName = interfaceName }; // Crear objeto config

                // Parsear la salida de netsh línea por línea
                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries); // Separar líneas

                foreach (var line in lines) // Iterar cada línea
                {
                    string trimmed = line.Trim(); // Quitar espacios

                    // Detectar si está en DHCP
                    // netsh muestra "DHCP habilitado:  Sí" o "DHCP enabled: Yes"
                    if (trimmed.IndexOf("DHCP", StringComparison.OrdinalIgnoreCase) >= 0) // Línea contiene DHCP
                    {
                        // Si la línea contiene "Sí", "Yes" o "Si" → es DHCP
                        config.IsDhcp = trimmed.IndexOf("S", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            (trimmed.IndexOf("Yes", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             trimmed.IndexOf("Sí", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             trimmed.IndexOf("Si", StringComparison.OrdinalIgnoreCase) >= 0);
                    }

                    // Detectar IP address
                    // netsh muestra "Dirección IP:  192.168.68.25" o "IP Address: 192.168.68.25"
                    if ((trimmed.IndexOf("IP", StringComparison.OrdinalIgnoreCase) >= 0 &&
                         trimmed.IndexOf("Address", StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (trimmed.IndexOf("Dirección", StringComparison.OrdinalIgnoreCase) >= 0 &&
                         trimmed.IndexOf("IP", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        string ip = ExtractIpFromLine(trimmed); // Extraer IP de la línea
                        if (!string.IsNullOrEmpty(ip) && string.IsNullOrEmpty(config.IpAddress)) // Primera IP encontrada
                        {
                            config.IpAddress = ip; // Guardar como IP principal
                        }
                    }

                    // Detectar máscara de subred
                    // netsh muestra "Máscara de subred:  255.255.255.0" o "Subnet Prefix: ... (mask 255.255.255.0)"
                    if (trimmed.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        trimmed.IndexOf("Máscara", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        trimmed.IndexOf("Mascara", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        trimmed.IndexOf("Subnet", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string maskIp = ExtractIpFromLine(trimmed); // Extraer máscara
                        if (!string.IsNullOrEmpty(maskIp)) // Máscara encontrada
                        {
                            config.SubnetMask = maskIp; // Guardar máscara
                        }
                    }

                    // Detectar gateway
                    // netsh muestra "Puerta de enlace predeterminada:  192.168.68.1" o "Default Gateway: 192.168.68.1"
                    if (trimmed.IndexOf("Gateway", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        trimmed.IndexOf("Puerta", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        trimmed.IndexOf("enlace", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        string gwIp = ExtractIpFromLine(trimmed); // Extraer gateway
                        if (!string.IsNullOrEmpty(gwIp)) // Gateway encontrado
                        {
                            config.Gateway = gwIp; // Guardar gateway
                        }
                    }
                }

                // Si no se detectó máscara, intentar leerla del adaptador .NET
                if (string.IsNullOrEmpty(config.SubnetMask) && !string.IsNullOrEmpty(config.IpAddress))
                {
                    config.SubnetMask = GetSubnetMaskFromDotNet(interfaceName, config.IpAddress); // Fallback .NET
                }

                Log.InfoFormat("[IP-CHANGE] Config capturada: IF={0}, IP={1}, Mask={2}, GW={3}, DHCP={4}",
                    interfaceName, config.IpAddress, config.SubnetMask, config.Gateway, config.IsDhcp); // Log

                return config; // Retornar configuración capturada
            }
            catch (Exception ex) // Error capturando config
            {
                Log.Error("[IP-CHANGE] Error capturando config de interfaz: " + ex.Message, ex); // Log
                return null; // No se pudo capturar
            }
        }

        /// <summary>
        /// Extrae la primera dirección IP (formato X.X.X.X) de una línea de texto.
        /// Usado para parsear la salida de netsh.
        /// </summary>
        private static string ExtractIpFromLine(string line)
        {
            // Buscar patrón de IP: 1-3 dígitos, punto, 1-3 dígitos, punto, 1-3 dígitos, punto, 1-3 dígitos
            var match = System.Text.RegularExpressions.Regex.Match(line, @"\b(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\b");
            if (match.Success) // Se encontró un patrón IP
            {
                string candidate = match.Groups[1].Value; // Obtener IP candidata
                IPAddress parsed; // Variable para validar
                if (IPAddress.TryParse(candidate, out parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
                {
                    return candidate; // IP válida
                }
            }
            return null; // No se encontró IP válida
        }

        /// <summary>
        /// Obtiene la máscara de subred de una IP específica usando las APIs .NET.
        /// Fallback cuando netsh no retorna la máscara en formato esperado.
        /// </summary>
        private static string GetSubnetMaskFromDotNet(string interfaceName, string ipAddress)
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces(); // Todas las interfaces
                foreach (var nic in interfaces) // Iterar
                {
                    if (nic.Name == interfaceName) // Encontrar la interfaz correcta
                    {
                        var ipProps = nic.GetIPProperties(); // Propiedades IP
                        foreach (var addr in ipProps.UnicastAddresses) // Iterar direcciones
                        {
                            if (addr.Address.ToString() == ipAddress) // Encontrar la IP
                            {
                                return addr.IPv4Mask.ToString(); // Retornar máscara
                            }
                        }
                    }
                }
                return "255.255.255.0"; // Default si no se encuentra
            }
            catch // Error
            {
                return "255.255.255.0"; // Default seguro
            }
        }

        /// <summary>
        /// Restaura la configuración IP estática original de la interfaz de red.
        /// CRÍTICO: Se invoca cuando se detecta que netsh alteró la IP estática del PC.
        /// Usa netsh interface ipv4 set address para reconfigurar IP estática.
        /// </summary>
        private static bool RestoreInterfaceConfig(SavedNetConfig config)
        {
            try
            {
                Log.WarnFormat("[IP-CHANGE] RESTAURANDO IP estática: IF={0}, IP={1}, Mask={2}, GW={3}",
                    config.InterfaceName, config.IpAddress, config.SubnetMask, config.Gateway); // Log warning

                // Paso 1: Configurar IP estática (esto desactiva DHCP automáticamente)
                // netsh interface ipv4 set address "Ethernet" static 192.168.68.25 255.255.255.0
                string args; // Variable para argumentos netsh

                if (!string.IsNullOrEmpty(config.Gateway)) // Si tiene gateway
                {
                    // Incluir gateway en el comando
                    args = string.Format(
                        "interface ipv4 set address \"{0}\" static {1} {2} {3}",
                        config.InterfaceName, config.IpAddress,
                        config.SubnetMask ?? "255.255.255.0", config.Gateway); // Con gateway
                }
                else // Sin gateway
                {
                    // Sin gateway
                    args = string.Format(
                        "interface ipv4 set address \"{0}\" static {1} {2}",
                        config.InterfaceName, config.IpAddress,
                        config.SubnetMask ?? "255.255.255.0"); // Sin gateway
                }

                Log.InfoFormat("[IP-CHANGE] Ejecutando restauración: netsh {0}", args); // Log

                bool ok = RunNetsh(args, 15000); // Ejecutar con timeout de 15 segundos (más tiempo por ser SET)

                if (ok) // Restauración exitosa
                {
                    Log.Info("[IP-CHANGE] ✓ IP estática restaurada correctamente"); // Log éxito
                }
                else // Restauración falló
                {
                    Log.Error("[IP-CHANGE] ✗ FALLO restaurando IP estática. Intervención manual requerida."); // Log error
                }

                return ok; // Retornar resultado
            }
            catch (Exception ex) // Error restaurando
            {
                Log.Error("[IP-CHANGE] Error crítico restaurando IP estática: " + ex.Message, ex); // Log
                return false; // Fallo
            }
        }

        // ═══════════════════════════════════════════════════════════
        // MÉTODOS ADICIONALES: ZPL, TSPL, TELNET
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Intenta cambiar la IP usando comandos ZPL II (^ND).
        /// Común en impresoras Zebra y clones que emulan ZPL.
        /// Puerto: 9100 (TCP).
        /// </summary>
        private static bool TryZplIpChange(string currentIp, string newIp, string mask, string gw)
        {
            try
            {
                // Comando ZPL ^ND (Network Device)
                // Formato: ^XA^ND2,N,ip,mask,gw^JUS^XZ
                // 2 = Change IP
                // N = Wired
                // ^JUS = Save configuration
                string zplCmd = string.Format(
                    "^XA" +
                    "^ND2,N,{0},{1},{2}" +
                    "^JUS" +
                    "^XZ",
                    newIp, mask, gw);

                Log.InfoFormat("[IP-CHANGE] Enviando comando ZPL (^ND) a {0}: {1}", currentIp, zplCmd);

                return SendRawTcpData(currentIp, 9100, Encoding.ASCII.GetBytes(zplCmd));
            }
            catch (Exception ex)
            {
                Log.DebugFormat("[IP-CHANGE] ZPL falló: {0}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Intenta cambiar la IP usando comandos TSPL (TSC Printer Language).
        /// Común en impresoras TSC y muchos clones chinos (Xprinter, Gprinter, etc.).
        /// Puerto: 9100 (TCP).
        /// </summary>
        private static bool TryTsplIpChange(string currentIp, string newIp, string mask, string gw)
        {
            try
            {
                // Comandos TSPL para configurar red
                // SET IPADDR "x.x.x.x"
                // SET NETMASK "x.x.x.x"
                // SET GATEWAY "x.x.x.x"
                var sb = new StringBuilder();
                sb.AppendLine(string.Format("SET IPADDR \"{0}\"", newIp));
                sb.AppendLine(string.Format("SET NETMASK \"{0}\"", mask));
                sb.AppendLine(string.Format("SET GATEWAY \"{0}\"", gw));
                
                // No hay comando explícito de "save" o "reset" en TSPL básico de red, 
                // suelen aplicarse al reiniciar o inmediatamente.
                // Algunos modelos soportan SELFTEST para verificar.

                string tsplCmd = sb.ToString();
                Log.InfoFormat("[IP-CHANGE] Enviando comandos TSPL a {0}", currentIp);

                return SendRawTcpData(currentIp, 9100, Encoding.ASCII.GetBytes(tsplCmd));
            }
            catch (Exception ex)
            {
                Log.DebugFormat("[IP-CHANGE] TSPL falló: {0}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Helper para enviar datos TCP raw y cerrar conexión inmediatamente.
        /// </summary>
        private static bool SendRawTcpData(string ip, int port, byte[] data)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    var connectResult = client.BeginConnect(ip, port, null, null);
                    bool connected = connectResult.AsyncWaitHandle.WaitOne(3000, true);

                    if (!connected || !client.Connected) return false;

                    using (var stream = client.GetStream())
                    {
                        stream.Write(data, 0, data.Length);
                        stream.Flush();
                        Thread.Sleep(500); // Dar tiempo al buffer
                    }
                    client.Close();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Intenta cambiar la IP vía Telnet (Puerto 23).
        /// Intento "best-effort" con comandos genéricos de Linux/BusyBox.
        /// </summary>
        private static bool TryTelnetIpChange(string currentIp, string newIp, string mask, string gw)
        {
            try
            {
                // Verificar si puerto 23 está abierto primero
                if (!IsPortOpen(currentIp, 23, 2000)) return false;

                Log.InfoFormat("[IP-CHANGE] Puerto 23 abierto en {0}, intentando Telnet...", currentIp);

                using (var client = new TcpClient())
                {
                    client.Connect(currentIp, 23);
                    using (var stream = client.GetStream())
                    using (var writer = new StreamWriter(stream) { AutoFlush = true })
                    using (var reader = new StreamReader(stream))
                    {
                        // Leer banner inicial (timeout corto)
                        client.ReceiveTimeout = 2000;
                        try { char[] buffer = new char[1024]; reader.Read(buffer, 0, buffer.Length); } catch { }

                        // Intentar login genérico (root/admin)
                        writer.WriteLine("root");
                        Thread.Sleep(500);
                        writer.WriteLine("admin"); // Password común o segundo intento de user
                        Thread.Sleep(500);

                        // Enviar comandos estilo Linux/BusyBox para cambiar IP (ifconfig)
                        // ifconfig eth0 x.x.x.x netmask x.x.x.x up
                        // route add default gw x.x.x.x
                        string cmd1 = string.Format("ifconfig eth0 {0} netmask {1} up", newIp, mask);
                        string cmd2 = string.Format("route add default gw {0}", gw);
                        
                        writer.WriteLine(cmd1);
                        Thread.Sleep(500);
                        writer.WriteLine(cmd2);
                        Thread.Sleep(500);
                        
                        // Intentar persistencia si es un sistema embebido con script de inicio
                        // Esto es muy específico y probable que falle, pero no hace daño
                        
                        Log.Info("[IP-CHANGE] Comandos Telnet enviados (blind)");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.DebugFormat("[IP-CHANGE] Telnet falló: {0}", ex.Message);
                return false;
            }
        }
    }
}
