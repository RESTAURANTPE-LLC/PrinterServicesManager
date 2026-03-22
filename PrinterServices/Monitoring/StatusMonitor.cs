using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using PrinterServices.Config;
using PrinterServices.Core.Network;
using PrinterServices.Data;
using PrinterServices.Data.Models;
using PrinterServices.Notifications;
using PrinterServices.Queue;
using PrinterServices.Services.Printers;
using PrinterServices.Workers;
using static PrinterServices.Core.Network.SnmpHelper;

namespace PrinterServices.Monitoring
{
    /// <summary>
    /// Monitor de estado de impresoras - refactorizado con principios SOLID.
    /// PRINCIPIO DIP (Dependency Inversion): Depende de abstracciones (interfaces), no de implementaciones.
    /// PRINCIPIO SRP (Single Responsibility): Solo orquesta verificación, delega operaciones a servicios.
    /// </summary>
    public class StatusMonitor
    {
        // Logger para diagnóstico de ciclos de monitoreo
        // RAZÓN: Rastrear cuándo inicia/detiene monitoreo y errores
        private static readonly ILog Log = LogManager.GetLogger(typeof(StatusMonitor));

        // Dependencia: Acceso a BD para consultar impresoras registradas
        // RAZÓN: Necesitamos leer lista de impresoras a monitorear
        private readonly PrinterServiceDb _db;

        // Dependencia: Gestor de cola para re-encolar jobs cuando impresora vuelve online
        // RAZÓN: Jobs en WAITING deben reintentarse cuando impresora está disponible
        private readonly PrintJobManager _jobManager;

        // Dependencia: Worker para búsquedas ARP asíncronas (no bloquea monitoreo)
        // RAZÓN: Resolución de IP por MAC puede tardar ~500ms, no debe bloquear verificación de otras impresoras
        private readonly ArpScanWorker _arpWorker;

        // Dependencia: Servicio para enriquecer impresoras con MAC vía ARP (PRINCIPIO DIP)
        // RAZÓN: Delega lógica de obtención de MAC a servicio especializado
        private readonly IPrinterMacEnricher _macEnricher;

        // Dependencia: Servicio para agrupar impresoras por dispositivo físico (PRINCIPIO DIP)
        // RAZÓN: Delega lógica de agrupamiento por MAC a servicio especializado
        private readonly IPhysicalDeviceGrouper _deviceGrouper;

        // Dependencia: Servicio para propagar estado entre impresoras relacionadas (PRINCIPIO DIP)
        // RAZÓN: Delega lógica de sincronización de estado a servicio especializado
        private readonly IPrinterStateSync _stateSync;

        // Fase 23: Notificador HTTP de estado de jobs a QuipuNetX
        // RAZÓN: Al expirar un job, se envía callback HTTP a QuipuNetX (además de gRPC)
        private readonly JobStatusCallbackNotifier _callbackNotifier;

        // Token de cancelación para detener ciclo de monitoreo
        // RAZÓN: Permite shutdown limpio del servicio
        private readonly CancellationTokenSource _cts;

        // RAZÓN: Señal para despertar el MonitorLoop INMEDIATAMENTE sin esperar intervalo completo.
        // Componentes externos (NetworkWatcher, ArpScanWorker) llaman TriggerImmediateCheck()
        // cuando detectan que la red cambió o volvió, evitando esperar 3s del intervalo.
        private readonly SemaphoreSlim _immediateCheckSignal = new SemaphoreSlim(0, 1);

        /// <summary>
        /// Constructor con inyección de dependencias (PRINCIPIO DIP).
        /// RAZÓN: No crear instancias dentro de la clase, recibirlas desde fuera.
        /// BENEFICIO: Permite testing con mocks, cambiar implementaciones sin modificar StatusMonitor.
        /// </summary>
        public StatusMonitor(
            PrinterServiceDb db, 
            PrintJobManager jobManager, 
            ArpScanWorker arpWorker,
            IPrinterMacEnricher macEnricher,
            IPhysicalDeviceGrouper deviceGrouper,
            IPrinterStateSync stateSync,
            JobStatusCallbackNotifier callbackNotifier = null) // Fase 23: Notificador de callbacks (opcional para compatibilidad)
        {
            // Validar dependencias inyectadas no nulas
            // RAZÓN: Fail-fast si no se configuró correctamente
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _jobManager = jobManager ?? throw new ArgumentNullException(nameof(jobManager));
            _arpWorker = arpWorker ?? throw new ArgumentNullException(nameof(arpWorker));
            _macEnricher = macEnricher ?? throw new ArgumentNullException(nameof(macEnricher));
            _deviceGrouper = deviceGrouper ?? throw new ArgumentNullException(nameof(deviceGrouper));
            _stateSync = stateSync ?? throw new ArgumentNullException(nameof(stateSync));
            _callbackNotifier = callbackNotifier; // Fase 23: Puede ser null si no se configuró
            
            // Crear token de cancelación
            // RAZÓN: Para poder detener el loop de monitoreo limpiamente
            _cts = new CancellationTokenSource();
        }

        public void Start()
        {
            var cfg = ConfigManager.Instance;
            Task.Factory.StartNew(() => MonitorLoop(_cts.Token), _cts.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Log.InfoFormat("[MONITOR] StatusMonitor iniciado (intervalo={0}s, timeout={1}ms)",
                cfg.GetInt("StatusCheckIntervalSeconds", 15),
                cfg.GetInt("TcpConnectTimeoutMs", 3000));
        }

        public void Stop()
        {
            _cts.Cancel();
            _immediateCheckSignal.Release(); // Despertar el loop para que termine
            Log.Info("[MONITOR] StatusMonitor detenido");
        }

        /// <summary>
        /// Despertar el MonitorLoop INMEDIATAMENTE para re-verificar impresoras.
        /// Llamado por NetworkWatcher cuando detecta que la red volvió,
        /// o por ArpScanWorker cuando resolvió una nueva IP.
        /// RAZÓN: Elimina la espera de hasta 3s del intervalo normal.
        /// </summary>
        public void TriggerImmediateCheck(string reason)
        {
            Log.InfoFormat("[MONITOR] ⚡ Check inmediato solicitado: {0}", reason);
            // Release con try-catch porque si ya hay 1 señal pendiente, lanza excepción
            try { _immediateCheckSignal.Release(); }
            catch (SemaphoreFullException) { /* Ya hay un check pendiente, OK */ }
        }

        private async Task MonitorLoop(CancellationToken ct)
        {
            // Espera inicial para que el servicio arranque
            await Task.Delay(5000, ct);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await CheckAllPrintersAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error("[MONITOR] Error en ciclo de monitoreo", ex);
                }

                try
                {
                    int intervalSeconds = ConfigManager.Instance.GetInt("StatusCheckIntervalSeconds", 15);
                    // RAZÓN: Esperar el intervalo normal O hasta que alguien llame TriggerImmediateCheck().
                    // WhenAny garantiza que si la red vuelve, no esperamos los 3s completos.
                    var delayTask = Task.Delay(intervalSeconds * 1000, ct);
                    var signalTask = _immediateCheckSignal.WaitAsync(ct);
                    await Task.WhenAny(delayTask, signalTask);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Verifica estado de todas las impresoras registradas aplicando SOLID.
        /// FLUJO: Enriquecer MACs → Agrupar por dispositivo físico → Verificar → Propagar estado
        /// PRINCIPIO SRP: Delega cada responsabilidad a un servicio especializado.
        /// </summary>
        private async Task CheckAllPrintersAsync(CancellationToken ct)
        {
            // ═══════════════════════════════════════════════════════════════════════════════
            // PASO 1: CONSULTAR TODAS LAS IMPRESORAS REGISTRADAS
            // ═══════════════════════════════════════════════════════════════════════════════
            
            List<PrinterEntity> printers;
            try
            {
                // Consultar impresoras con IP válida (RED) o con USB key (USB)
                // RAZÓN: Sin IP ni USB key no podemos verificar conectividad
                // FILTRO: Impresoras de RED necesitan IP, impresoras USB necesitan usb_unique_key
                printers = _db.Query<PrinterEntity>(
                    "SELECT * FROM printers WHERE (ip IS NOT NULL AND ip != '') OR (usb_unique_key IS NOT NULL AND usb_unique_key != '')");
            }
            catch (Exception ex)
            {
                // Error de BD (archivo bloqueado, corrupción, etc.)
                // RAZÓN: No dejar que caiga el ciclo completo de monitoreo
                Log.Error("[MONITOR] Error consultando impresoras: " + ex.Message);
                return; // Salir temprano, reintentar en próximo ciclo
            }

            // Si no hay impresoras registradas, no hacer nada
            // RAZÓN: PrinterServices sin impresoras = configuración inicial
            if (printers == null || printers.Count == 0)
            {
                return; // Salir temprano, no loguear cada ciclo (spam)
            }

            // Loguear inicio de ciclo con cantidad TOTAL de registros
            // RAZÓN: Diagnóstico de cuántos registros lógicos existen antes de agrupar
            Log.DebugFormat("[MONITOR] Consultadas {0} impresora(s) registrada(s)...", printers.Count);

            // ═══════════════════════════════════════════════════════════════════════════════
            // PASO 2: ENRIQUECER IMPRESORAS SIN MAC (PRINCIPIO DIP)
            // ═══════════════════════════════════════════════════════════════════════════════
            
            // Iterar sobre todas las impresoras y enriquecer/normalizar MACs
            // RAZÓN: Necesitamos MAC para agrupar por dispositivo físico
            // DELEGACIÓN: IPrinterMacEnricher maneja lógica de ARP y normalización
            foreach (var printer in printers)
            {
                // Si cancelación solicitada, abortar enriquecimiento
                // RAZÓN: Shutdown del servicio debe ser inmediato
                if (ct.IsCancellationRequested) break;

                // Intentar enriquecer si no tiene MAC (obtener vía ARP desde IP)
                // RAZÓN: QuipuNetX puede enviar impresoras sin MAC, solo con IP
                // PRINCIPIO DIP: Delega a servicio especializado en lugar de lógica inline
                if (string.IsNullOrEmpty(printer.MacAddress))
                {
                    _macEnricher.TryEnrichMac(printer); // Servicio maneja todo (ARP, normalización, persistencia)
                }
                // Si ya tiene MAC, normalizarla (convertir AA:BB:CC a AABBCC)
                // RAZÓN: Formatos inconsistentes impiden agrupar correctamente
                // PRINCIPIO DIP: Delega normalización a servicio
                else
                {
                    _macEnricher.TryNormalizeMac(printer); // Servicio maneja normalización y persistencia
                }
            }

            // ═══════════════════════════════════════════════════════════════════════════════
            // PASO 3: AGRUPAR POR DISPOSITIVO FÍSICO ÚNICO (PRINCIPIO DIP + SRP)
            // ═══════════════════════════════════════════════════════════════════════════════
            
            // Delegar agrupamiento a servicio especializado
            // RAZÓN: Si BARRA y BARRA 2 tienen misma MAC, solo verificar UNA VEZ
            // BENEFICIO: Reduce overhead de red de N registros a M dispositivos (N >= M)
            // PRINCIPIO DIP: No implementar lógica aquí, usar abstracción
            Dictionary<string, PrinterEntity> uniqueDevices = _deviceGrouper.GroupByPhysicalDevice(printers);

            // Si agrupamiento resultó en diccionario vacío, no hacer nada
            // RAZÓN: Todas las impresoras son inválidas (sin MAC ni IP)
            if (uniqueDevices.Count == 0)
            {
                Log.Warn("[MONITOR] No hay dispositivos físicos válidos para monitorear");
                return; // Salir temprano
            }

            // Loguear resultado de agrupamiento para diagnóstico
            // RAZÓN: Ver efectividad de reducción (ej: 10 registros → 5 dispositivos)
            Log.DebugFormat("[MONITOR] Verificando {0} dispositivo(s) físico(s) (de {1} registro(s))...", 
                uniqueDevices.Count, printers.Count);

            // ═══════════════════════════════════════════════════════════════════════════════
            // PASO 4: VERIFICAR ESTADO DE CADA DISPOSITIVO FÍSICO ÚNICO
            // ═══════════════════════════════════════════════════════════════════════════════
            
            // Iterar solo sobre dispositivos ÚNICOS (representantes de cada grupo)
            // RAZÓN: Evitar verificar BARRA y BARRA 2 (misma MAC) dos veces
            foreach (var kvp in uniqueDevices)
            {
                // Extraer impresora representante del diccionario
                // RAZÓN: kvp.Value es el dispositivo físico seleccionado para verificación
                PrinterEntity printer = kvp.Value;

                // Si cancelación solicitada, abortar verificación
                // RAZÓN: Shutdown del servicio debe ser inmediato
                if (ct.IsCancellationRequested) break;

                // Guardar estado anterior para detectar TRANSICIONES
                // RAZÓN: Solo notificar cuando hay CAMBIOS (OFFLINE→ONLINE, etc.)
                bool wasOnline = printer.EstadoOnline == 1;
                bool wasDisponible = printer.DisponibleParaImprimir == 1;

                try
                {
                    // ═══════════════════════════════════════════════════════════════════════════════
                    // ESTRATEGIA INTELIGENTE: DLE EOT primero → Auto-detección SNMP → Optimización
                    // Para USB: UsbPrinterStatusChecker (SetupAPI + DLE EOT vía USB)
                    // ═══════════════════════════════════════════════════════════════════════════════

                    int checkTimeoutMs = ConfigManager.Instance.GetInt("TcpConnectTimeoutMs", 3000);
                    PrinterStatus dleStatus;
                    bool isUsbPrinter = printer.TipoConexion == "USB"
                        && !string.IsNullOrEmpty(printer.UsbUniqueKey);

                    if (isUsbPrinter)
                    {
                        // ═══════════════════════════════════════════════════════════
                        // IMPRESORA USB: verificar por UsbUniqueKey (VID+PID+Serial)
                        // ═══════════════════════════════════════════════════════════
                        dleStatus = await UsbPrinterStatusChecker.CheckAsync(
                            printer.UsbUniqueKey, checkTimeoutMs, ct);

                        // Detectar si el DevicePath cambió (usuario movió de puerto USB)
                        // Usa el DevicePath ya resuelto por UsbPrinterStatusChecker (sin re-enumerar)
                        if (dleStatus.Online && !string.IsNullOrEmpty(dleStatus.ResolvedUsbDevicePath)
                            && dleStatus.ResolvedUsbDevicePath != printer.UsbDevicePath)
                        {
                            Log.InfoFormat("[MONITOR] Impresora USB {0} cambió de puerto: {1} → {2}",
                                printer.Nombre ?? printer.ImpresoraId,
                                printer.UsbDevicePath ?? "(inicial)", dleStatus.ResolvedUsbDevicePath);
                            printer.UsbDevicePath = dleStatus.ResolvedUsbDevicePath;
                            // Se persiste más abajo con _db.Update(printer)
                        }
                    }
                    else
                    {
                        // ═══════════════════════════════════════════════════════════
                        // IMPRESORA RED: verificar por IP (comportamiento existente)
                        // ═══════════════════════════════════════════════════════════
                        dleStatus = await PrinterStatusChecker.CheckAsync(
                            printer.Ip, printer.Puerto, checkTimeoutMs, ct);
                    }

                    // Variable para resultado final (puede ser DLE EOT o SNMP)
                    PrinterStatus status = dleStatus;

                    // SNMP solo aplica a impresoras de RED (USB no usa SNMP)
                    // Si impresora está OFFLINE, no intentar SNMP
                    // RAZÓN: Evitar timeout innecesario de 2 segundos en impresora apagada
                    // PRINCIPIO: No marcar SnmpEnabled=0 aquí (falso negativo si está apagada temporalmente)
                    if (!dleStatus.Online || isUsbPrinter)
                    {
                        if (isUsbPrinter)
                        {
                            Log.DebugFormat("[MONITOR] {0} (USB:{1}) {2} - SNMP no aplica a USB",
                                printer.Nombre ?? printer.ImpresoraId, printer.UsbUniqueKey,
                                dleStatus.Online ? "ONLINE" : "OFFLINE");
                        }
                        else
                        {
                            Log.DebugFormat("[MONITOR] {0} ({1}) OFFLINE - omitiendo auto-detección SNMP",
                                printer.Nombre ?? printer.ImpresoraId, printer.Ip);
                        }
                        // status ya tiene resultado DLE EOT (offline)
                    }
                    else
                    {
                        // ═══════════════════════════════════════════════════════════════════════════════
                        // PASO 2: Impresora está ONLINE → Estrategia según SnmpEnabled
                        // ═══════════════════════════════════════════════════════════════════════════════
                        
                        // CASO A: Nunca se intentó SNMP (-1) → AUTO-DETECCIÓN
                        // RAZÓN: Usuario no técnico no sabe si su impresora soporta SNMP
                        // BENEFICIO: Sistema plug-and-play, sin configuración manual
                        if (printer.SnmpEnabled == -1) 
                        {
                            Log.InfoFormat("[MONITOR] Auto-detectando SNMP para {0} (online confirmado vía DLE EOT)...", 
                                printer.Nombre ?? printer.ImpresoraId);
                            
                            // Intentar SNMP con community "public" (default estándar IEEE)
                            // RAZÓN: 99% de impresoras con SNMP usan "public" por defecto
                            var snmpStatus = await Task.Run(() => 
                                SnmpHelper.CheckPrinter(printer.Ip, "public", 2000));
                            
                            if (snmpStatus != null)
                            {
                                // SNMP funciona → Guardar para futuras verificaciones
                                // RAZÓN: Próximos ciclos usarán SNMP directamente (más rápido)
                                printer.SnmpEnabled = 1;
                                printer.SnmpCommunity = "public";
                                
                                // Usar resultado SNMP en lugar de DLE EOT
                                // RAZÓN: SNMP es más preciso (nivel de papel, estado detallado)
                                status = new PrinterStatus
                                {
                                    Online = snmpStatus.Online,
                                    TienePapel = snmpStatus.TienePapel,
                                    TapaAbierta = snmpStatus.TapaAbierta,
                                    DisponibleParaImprimir = snmpStatus.DisponibleParaImprimir,
                                    ErrorRecuperable = false,
                                    RawStatus = $"SNMP: Paper={snmpStatus.PaperLevel}% Device={snmpStatus.DeviceStatus}",
                                    ErrorMessage = null
                                };
                                
                                Log.InfoFormat("[MONITOR] SNMP detectado y activado para {0}", printer.Nombre);
                            }
                            else
                            {
                                // SNMP no soportado → Marcar para no reintentar
                                // RAZÓN: Impresora económica sin SNMP (Epson TM-T20II, etc.)
                                // IMPORTANTE: Solo marcamos =0 porque DLE EOT confirmó que está online
                                printer.SnmpEnabled = 0;
                                
                                Log.InfoFormat("[MONITOR] SNMP no soportado en {0} - usando DLE EOT permanentemente",
                                    printer.Nombre ?? printer.ImpresoraId);
                                // status ya tiene resultado DLE EOT
                            }
                            
                            // Persistir resultado de auto-detección en BD
                            // RAZÓN: Próximos ciclos consultarán SnmpEnabled para optimizar
                            _db.Update(printer);
                        }
                        // CASO B: Ya confirmado que tiene SNMP (SnmpEnabled=1) → Usar SNMP directamente
                        // RAZÓN: Auto-detección previa fue exitosa, usar método optimizado
                        else if (printer.SnmpEnabled == 1 && !string.IsNullOrEmpty(printer.SnmpCommunity))
                        {
                            var snmpStatus = await Task.Run(() => 
                                SnmpHelper.CheckPrinter(printer.Ip, printer.SnmpCommunity, 2000));

                            if (snmpStatus != null)
                            {
                                // Usar resultado SNMP
                                // RAZÓN: 80% más rápido que DLE EOT (1 paquete UDP vs 3-4 TCP)
                                status = new PrinterStatus
                                {
                                    Online = snmpStatus.Online,
                                    TienePapel = snmpStatus.TienePapel,
                                    TapaAbierta = snmpStatus.TapaAbierta,
                                    DisponibleParaImprimir = snmpStatus.DisponibleParaImprimir,
                                    ErrorRecuperable = false,
                                    RawStatus = $"SNMP: Paper={snmpStatus.PaperLevel}% Device={snmpStatus.DeviceStatus}",
                                    ErrorMessage = null
                                };
                                
                                Log.DebugFormat("[MONITOR] {0} verificada vía SNMP (optimizado, {1}% papel)",
                                    printer.Nombre ?? printer.ImpresoraId, snmpStatus.PaperLevel);
                            }
                            else
                            {
                                // SNMP falló pero impresora está online (DLE EOT respondió)
                                // RAZÓN: Puede ser timeout momentáneo de red o SNMP deshabilitado
                                Log.WarnFormat("[MONITOR] SNMP falló en {0}, usando DLE EOT como fallback",
                                    printer.Nombre ?? printer.ImpresoraId);
                                // status ya tiene resultado DLE EOT
                            }
                        }
                        // CASO C: Confirmado que NO tiene SNMP (SnmpEnabled=0) → Solo DLE EOT
                        // RAZÓN: Auto-detección previa confirmó que no soporta SNMP
                        else
                        {
                            Log.DebugFormat("[MONITOR] {0} verificada vía DLE EOT (SNMP no soportado)",
                                printer.Nombre ?? printer.ImpresoraId);
                            // status ya tiene resultado DLE EOT
                        }
                    }

                    // Actualizar todos los campos de estado del dispositivo representante
                    // RAZÓN: Este dispositivo fue verificado vía SNMP/DLE EOT, guardar resultado
                    printer.EstadoOnline = status.Online ? 1 : 0;
                    printer.DisponibleParaImprimir = status.DisponibleParaImprimir ? 1 : 0;
                    printer.TienePapel = status.TienePapel ? 1 : 0;
                    printer.TapaAbierta = status.TapaAbierta ? 1 : 0;
                    printer.UltimoCheck = DateTime.Now.ToString("o");

                    // Persistir estado actualizado en BD
                    // RAZÓN: PrintWorker consulta estos campos antes de imprimir
                    _db.Update(printer);

                    // ═══════════════════════════════════════════════════════════════════════════════
                    // PASO 5: PROPAGAR ESTADO A IMPRESORAS RELACIONADAS (PRINCIPIO DIP + SRP)
                    // ═══════════════════════════════════════════════════════════════════════════════
                    
                    // Sincronizar estado a TODAS las impresoras con misma MAC
                    // RAZÓN: Si BARRA está online, BARRA 2 también debe estarlo (mismo dispositivo físico)
                    // PRINCIPIO DIP: Delegar propagación a servicio especializado
                    int syncedCount = _stateSync.SyncStateToRelatedPrinters(printer, printers);
                    
                    // Si se sincronizaron impresoras, loguear para diagnóstico
                    // RAZÓN: Rastrear cuándo se propaga estado (ej: BARRA → BARRA 2, BARRA 3)
                    if (syncedCount > 0)
                    {
                        Log.DebugFormat("[MONITOR] Estado propagado de {0} a {1} impresora(s) relacionada(s)",
                            printer.Nombre ?? printer.ImpresoraId, syncedCount);
                    }

                    // TRANSICIÓN 1: Volvió a la red (conectividad restaurada)
                    if (!wasOnline && status.Online)
                    {
                        Log.InfoFormat("[MONITOR] ✓ {0} ({1}) ONLINE — conectividad restaurada",
                            printer.Nombre ?? printer.ImpresoraId, printer.Ip);
                        
                        // Registrar transición en printer_status_log
                        LogStatusTransition(printer, "OFFLINE", "ONLINE", "Conectividad restaurada");
                        
                        // ★ CANCELAR búsqueda ARP si estaba en progreso (ya no es necesaria)
                        _arpWorker?.CancelScan(printer.ImpresoraId);
                        
                        if (status.DisponibleParaImprimir)
                        {
                            // También está disponible para imprimir → re-encolar jobs
                            Log.InfoFormat("[MONITOR] ✓ {0} ({1}) DISPONIBLE — re-encolando jobs en espera",
                                printer.Nombre ?? printer.ImpresoraId, printer.Ip);
                            RequeueWaitingJobs(printer.ImpresoraId);
                            NotifyPrinterChange(printer.ImpresoraId, printer.Nombre,
                                NotificationType.Online, "Impresora en línea y disponible para imprimir");
                        }
                        else
                        {
                            // Conectada pero no disponible
                            string razon = status.TapaAbierta ? "tapa abierta" : 
                                           !status.TienePapel ? "sin papel" : "no lista";
                            Log.WarnFormat("[MONITOR] ⚠ {0} ({1}) online pero NO DISPONIBLE — {2}",
                                printer.Nombre ?? printer.ImpresoraId, printer.Ip, razon);
                        }
                    }
                    // TRANSICIÓN 2: Perdió conectividad de red
                    else if (wasOnline && !status.Online)
                    {
                        Log.WarnFormat("[MONITOR] ✗ {0} ({1}) OFFLINE — perdió conectividad de red",
                            printer.Nombre ?? printer.ImpresoraId, printer.Ip);
                        
                        // Registrar transición en printer_status_log
                        LogStatusTransition(printer, "ONLINE", "OFFLINE", "Perdió conectividad de red");
                        
                        // ★ DELEGAR búsqueda ARP a worker independiente (NO BLOQUEAR)
                        // ArpScanWorker procesará async en su propio hilo (~500ms)
                        // StatusMonitor continúa verificando otras impresoras inmediatamente
                        // NOTA: ARP solo aplica a impresoras de RED, USB no tiene ARP
                        if (!isUsbPrinter)
                        {
                            if (!string.IsNullOrEmpty(printer.MacAddress))
                            {
                                Log.InfoFormat("[MONITOR] Delegando búsqueda ARP a worker: {0} (MAC: {1})",
                                    printer.Nombre ?? printer.ImpresoraId, printer.MacAddress);
                                _arpWorker?.EnqueueScan(printer.ImpresoraId);
                            }
                            else
                            {
                                Log.WarnFormat("[MONITOR] Impresora {0} sin MAC — auto-resolución imposible",
                                    printer.Nombre ?? printer.ImpresoraId);
                            }
                        }
                        else
                        {
                            Log.WarnFormat("[MONITOR] Impresora USB {0} OFFLINE — desconectada del puerto USB",
                                printer.Nombre ?? printer.ImpresoraId);
                        }
                        
                        // Notificar offline inmediatamente (no esperar resultado de ARP)
                        NotifyPrinterChange(printer.ImpresoraId, printer.Nombre,
                            NotificationType.Offline, "Impresora offline (búsqueda ARP en progreso si tiene MAC)");
                    }
                    // TRANSICIÓN 3: Volvió a estar disponible (estaba online pero no disponible)
                    else if (status.Online && !wasDisponible && status.DisponibleParaImprimir)
                    {
                        Log.InfoFormat("[MONITOR] ✓ {0} ({1}) DISPONIBLE — re-encolando jobs en espera",
                            printer.Nombre ?? printer.ImpresoraId, printer.Ip);
                        
                        // Registrar transición en printer_status_log
                        LogStatusTransition(printer, "NO_DISPONIBLE", "DISPONIBLE", "Disponible para imprimir");
                        RequeueWaitingJobs(printer.ImpresoraId);
                        NotifyPrinterChange(printer.ImpresoraId, printer.Nombre,
                            NotificationType.Online, "Impresora disponible para imprimir");
                    }
                    // TRANSICIÓN 4: Dejó de estar disponible (online pero con problema)
                    else if (status.Online && wasDisponible && !status.DisponibleParaImprimir)
                    {
                        string razon = status.TapaAbierta ? "tapa abierta" : 
                                       !status.TienePapel ? "sin papel" : "no lista";
                        Log.WarnFormat("[MONITOR] ⚠ {0} ({1}) NO DISPONIBLE — {2}",
                            printer.Nombre ?? printer.ImpresoraId, printer.Ip, razon);
                        
                        // Registrar transición en printer_status_log
                        LogStatusTransition(printer, "DISPONIBLE", "NO_DISPONIBLE", razon);
                        
                        if (!status.TienePapel)
                        {
                            NotifyPrinterChange(printer.ImpresoraId, printer.Nombre,
                                NotificationType.SinPapel, "Sin papel");
                        }
                        else if (status.TapaAbierta)
                        {
                            NotifyPrinterChange(printer.ImpresoraId, printer.Nombre,
                                NotificationType.Offline, "Tapa abierta");
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Error inesperado al verificar impresora (timeout, socket exception, etc.)
                    // Esto indica problema de red o configuración, NO intento de auto-resolución aquí
                    printer.EstadoOnline = 0; // Marcar OFFLINE
                    printer.DisponibleParaImprimir = 0; // No disponible
                    printer.UltimoCheck = DateTime.Now.ToString("o");
                    try { _db.Update(printer); } catch { }

                    Log.WarnFormat("[MONITOR] ✗ {0} ({1}) ERROR al verificar: {2}",
                        printer.Nombre ?? printer.ImpresoraId, printer.Ip, ex.Message);
                }
            }
        }

        private void RequeueWaitingJobs(string impresoraId)
        {
            try
            {
                var waitingJobs = _db.Query<PrintJobEntity>(
                    "SELECT * FROM print_jobs WHERE impresora_id = ? AND estado = 'WAITING' ORDER BY prioridad ASC, fecha_creacion ASC",
                    impresoraId);

                if (waitingJobs == null || waitingJobs.Count == 0) return;

                // Fase 23: Obtener configuración de expiración (0 = desactivado)
                int expirarDespuesDe = ConfigManager.Instance.GetInt("ExpirarImpresionDespuesDe", 0);

                int requeuedCount = 0;  // Contador de jobs re-encolados
                int expiredCount = 0;   // Contador de jobs expirados

                foreach (var entity in waitingJobs)
                {
                    var job = PrintJob.FromEntity(entity);

                    // Fase 23: Verificar si el job superó el tiempo máximo en WAITING
                    if (expirarDespuesDe > 0 && IsJobExpired(job, expirarDespuesDe))
                    {
                        ExpireJob(job, expirarDespuesDe); // Marcar como expirado + notificar
                        expiredCount++;                    // Incrementar contador de expirados
                    }
                    else
                    {
                        _jobManager.ReEnqueue(job);        // Re-encolar normalmente
                        requeuedCount++;                   // Incrementar contador de re-encolados
                    }
                }

                if (requeuedCount > 0)
                {
                    Log.InfoFormat("[MONITOR] Re-encolados {0} jobs WAITING para {1}",
                        requeuedCount, impresoraId);
                }
                if (expiredCount > 0)
                {
                    Log.WarnFormat("[MONITOR] Expirados {0} jobs WAITING para {1} (superaron {2}s)",
                        expiredCount, impresoraId, expirarDespuesDe);
                }
            }
            catch (Exception ex)
            {
                Log.Error("[MONITOR] Error re-encolando jobs WAITING: " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Fase 23: Verifica si un job en WAITING superó el tiempo máximo configurado.
        /// Compara FechaCreacion del job contra el tiempo actual.
        /// </summary>
        /// <param name="job">Job en estado WAITING</param>
        /// <param name="maxSeconds">Segundos máximos permitidos en WAITING</param>
        /// <returns>true si el job expiró</returns>
        private bool IsJobExpired(PrintJob job, int maxSeconds)
        {
            var elapsed = DateTime.Now - job.FechaCreacion; // Tiempo transcurrido desde creación del job
            return elapsed.TotalSeconds > maxSeconds;        // true si superó el límite
        }

        /// <summary>
        /// Fase 23: Marca un job como EXPIRED, notifica via gRPC y HTTP callback.
        /// Responsabilidad: orquestar marcado + notificaciones para un job expirado.
        /// </summary>
        /// <param name="job">Job a expirar</param>
        /// <param name="maxSeconds">Segundos configurados (para mensaje descriptivo)</param>
        private void ExpireJob(PrintJob job, int maxSeconds)
        {
            string reason = string.Format("Superó {0} segundos en WAITING", maxSeconds); // Mensaje descriptivo

            _jobManager.MarkExpired(job, reason); // Marcar como EXPIRED en BD (estado terminal)

            // Notificar via gRPC a servidores + cliente origen
            try
            {
                NotificationManager.Instance.NotifyPrintExpired(
                    job.JobId, job.ComandaId, job.ImpresoraId,
                    job.ImpresoraNombre, job.DeviceIdOrigen, job.IpOrigen, reason);
            }
            catch (Exception ex)
            {
                Log.Warn("[MONITOR] Error al notificar expiración via gRPC: " + ex.Message); // No fatal
            }

            // Fase 23: Callback HTTP a QuipuNetX (fire-and-forget)
            if (_callbackNotifier != null)
            {
                _callbackNotifier.NotifyStatusChangeFireAndForget(job, "EXPIRED", reason);
            }

            Log.WarnFormat("[MONITOR] Job {0} EXPIRADO → {1}", job.JobId, reason); // Log de advertencia
        }

        /// <summary>
        /// Registra una transición de estado de impresora en printer_status_log.
        /// RAZÓN: Generar reporte de disponibilidad (a qué hora se desconectó, a qué hora volvió).
        /// </summary>
        private void LogStatusTransition(PrinterEntity printer, string estadoAnterior, string estadoNuevo, string detalle)
        {
            try
            {
                var logEntry = new PrinterStatusLogEntity
                {
                    ImpresoraId = printer.ImpresoraId,
                    ImpresoraNombre = printer.Nombre,
                    ImpresoraIp = printer.Ip,
                    MacAddress = printer.MacAddress,
                    EstadoAnterior = estadoAnterior,
                    EstadoNuevo = estadoNuevo,
                    Detalle = detalle,
                    Fecha = DateTime.Now.ToString("o")
                };
                _db.Insert(logEntry);
            }
            catch (Exception ex)
            {
                Log.Warn("[MONITOR] Error registrando transición de estado: " + ex.Message);
            }
        }

        /// <summary>
        /// Helper para enviar notificaciones de cambio de estado de impresora via gRPC.
        /// Mismo patrón que NotifyIfAvailable en PrintWorker: captura errores
        /// silenciosamente para no afectar el ciclo de monitoreo.
        /// Solo envía a servidores suscritos (los clientes no reciben eventos de infraestructura).
        /// </summary>
        private void NotifyPrinterChange(string impresoraId, string nombre, string tipo, string mensaje)
        {
            try
            {
                // Delegar al NotificationManager que difunde a servidores suscritos via gRPC
                NotificationManager.Instance.NotifyPrinterStatusChange(
                    impresoraId, nombre, tipo, mensaje);
            }
            catch (InvalidOperationException)
            {
                // NotificationManager aún no inicializado (GetInstance no fue llamado) — ignorar
            }
            catch (Exception ex)
            {
                // Error al notificar — loguear pero NO relanzar, el monitoreo debe continuar
                Log.Warn("[MONITOR] Error al notificar cambio de estado: " + ex.Message);
            }
        }

        public PrinterStatus CheckPrinterNow(string ip, int port)
        {
            int timeoutMs = ConfigManager.Instance.GetInt("TcpConnectTimeoutMs", 3000);
            return PrinterStatusChecker.CheckSync(ip, port, timeoutMs);
        }

        /// <summary>
        /// Verifica estado de una impresora USB por su UniqueKey.
        /// Análogo a CheckPrinterNow para impresoras de red.
        /// </summary>
        public PrinterStatus CheckUsbPrinterNow(string usbUniqueKey)
        {
            int timeoutMs = ConfigManager.Instance.GetInt("TcpConnectTimeoutMs", 3000);
            return UsbPrinterStatusChecker.CheckSync(usbUniqueKey, timeoutMs);
        }
    }
}
