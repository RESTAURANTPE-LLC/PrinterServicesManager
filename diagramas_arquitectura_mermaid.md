# Diagramas de Arquitectura — PrinterServices + QuipuNet (Mermaid)

> Generado: 2026-03-13

---

## 1. Diagrama de Arquitectura de Aplicación — PrinterServices con QuipuNet (Modo Cliente y Servidor)

```mermaid
graph TB
    subgraph CLOUD["☁️ Cloud - Restaurantpe"]
        CloudAPI["MonitoreoRemotoDeImpresiones<br/>api/getStatusPrinters"]
    end

    subgraph CLIENTES["🖥️ Terminales Cliente (QuipuNet Modo Cliente)"]
        direction TB
        PC1["PC1 Tomador 1<br/>10.0.0.2<br/>QuipuNet.exe Modo Cliente"]
        PC2["PC2 Tomador 2<br/>10.0.0.3<br/>QuipuNet.exe Modo Cliente"]
        PC3["PC3 Delivery<br/>10.0.0.4<br/>QuipuNet.exe Modo Cliente"]
        TABLET["Tablet Android<br/>10.0.0.6<br/>QuipuNet Modo Cliente"]

        subgraph PC1_DETAIL["Componentes internos PC1"]
            direction TB
            PC1_PJSM["PrintJobStatusManager<br/>(RAM only, sin BD)"]
            PC1_CALLBACK["PrintJobCallbackServer<br/>Mini Nancy :8083<br/>POST /api/ps/callback"]
            PC1_FLAG["UtilFront.FEATURE_FLAG<br/>_FROM_SERVER_PRINTER_SERVICES"]
        end
    end

    subgraph SERVIDOR["🏢 Servidor (10.0.0.100)"]
        direction TB
        subgraph QN_SERVER["QuipuNet.exe — Modo Servidor"]
            direction TB
            WEBSERVER["WebServer_New<br/>Nancy HTTP :8081"]
            PEDIDO_CTRL["PedidoController<br/>VentaController<br/>DeliveryController"]
            FLAG_CHECK{"esModoServidor() &&<br/>USAR_PRINTER_SERVICE?"}
            PS_CLIENT["PrinterServiceClient<br/>(HTTP Client)"]
            STRATEGIES["Server*Strategy<br/>ServerComandasStrategy<br/>ServerVentaRapidaStrategy<br/>ServerVentaSalonStrategy<br/>ServerDeliveryStrategy"]
            PJSM_SERVER["PrintJobStatusManager<br/>(RAM + BD)"]
            PS_MODULE["PrinterServiceModule<br/>:8081 updateJobStatus<br/>+ endpoints proxy"]
            QUIPUDB[("quipunet.db<br/>pedidoprinterjob")]
        end

        subgraph PS["PrinterServices.exe — Servicio Windows"]
            direction TB
            HTTP_API["HTTP API :8090<br/>HttpListener Self-hosted"]
            ENDPOINTS["POST /api/print/comandas<br/>POST /api/print/comanda<br/>POST /api/print/venta<br/>POST /api/print/precuenta<br/>GET /api/printer/status<br/>GET /api/health"]
            JOB_MGR["PrintJobManager<br/>ConcurrentQueue + SemaphoreSlim<br/>Event-driven (<5ms latencia)"]
            PRINT_WORKER["PrintWorker<br/>(consume cola async)"]
            STATUS_MON["StatusMonitor<br/>SNMP + DLE EOT híbrido<br/>(polling 5s)"]
            NET_WATCH["NetworkWatcher<br/>ARP scan + MAC gateway<br/>(cada 30s)"]
            NOTIFY["JobStatusCallbackNotifier<br/>HTTP callbacks"]
            DRIVERS["IPrinterDriver<br/>EpsonDriver │ StarDriver<br/>BixolonDriver │ GenericEscPos"]
            TRANSPORT["ITransport<br/>TcpTransport (retry 3x)<br/>SerialTransport │ UsbTransport"]
            RENDERER["HtmlBitmapRenderer<br/>HTML → Bitmap (GDI+)"]
            PSDB[("printerservice.db<br/>print_jobs, printers<br/>print_log, notifications")]
            UDP_DISC["UDP Discovery :9999"]
        end
    end

    subgraph IMPRESORAS["🖨️ Impresoras Térmicas"]
        IMP_COCINA["Impresora Cocina<br/>10.0.0.13<br/>TCP:9100"]
        IMP_BAR["Impresora Barra<br/>10.0.0.12<br/>TCP:9100"]
        IMP_CAJA["Impresora Caja<br/>10.0.0.14<br/>TCP:9100"]
    end

    %% Flujo Cliente → Servidor
    PC1 -->|"REST POST :8081<br/>enviarPedidos()"| WEBSERVER
    PC2 -->|"REST POST :8081<br/>enviarPedidos()"| WEBSERVER
    PC3 -->|"REST POST :8081<br/>enviarPedidos()"| WEBSERVER
    TABLET -->|"REST POST :8081<br/>enviarPedidos()"| WEBSERVER

    %% Flujo interno Servidor
    WEBSERVER -->|"GetClientIp(request)<br/>ipClienteRest=10.0.0.2"| PEDIDO_CTRL
    PEDIDO_CTRL --> FLAG_CHECK
    FLAG_CHECK -->|"ON: delega a PS<br/>ImpresionList vacía"| STRATEGIES
    FLAG_CHECK -->|"OFF: flujo antiguo<br/>ImpresionList con datos"| WEBSERVER
    STRATEGIES --> PS_CLIENT
    PS_CLIENT -->|"HTTP POST :8090<br/>/api/print/comandas<br/>ip_origen + ip_servidor"| HTTP_API

    %% Flujo interno PrinterServices
    HTTP_API --> ENDPOINTS
    ENDPOINTS --> JOB_MGR
    JOB_MGR -->|"SemaphoreSlim.Release()"| PRINT_WORKER
    PRINT_WORKER --> STATUS_MON
    PRINT_WORKER --> DRIVERS
    DRIVERS --> TRANSPORT
    PRINT_WORKER --> RENDERER
    JOB_MGR --> PSDB
    STATUS_MON --> PSDB
    NET_WATCH --> PSDB

    %% PrinterServices → Impresoras
    TRANSPORT -->|"TCP:9100<br/>ESC/POS bytes"| IMP_COCINA
    TRANSPORT -->|"TCP:9100<br/>ESC/POS bytes"| IMP_BAR
    TRANSPORT -->|"TCP:9100<br/>ESC/POS bytes"| IMP_CAJA

    %% Callbacks de notificación
    PRINT_WORKER -->|"job DONE/FAILED"| NOTIFY
    NOTIFY -->|"1. SIEMPRE<br/>POST :8081 updateJobStatus"| PS_MODULE
    NOTIFY -->|"2. Si ip_origen ≠ ip_servidor<br/>POST :8083 /api/ps/callback"| PC1_CALLBACK
    PS_MODULE --> PJSM_SERVER
    PS_MODULE --> QUIPUDB
    PC1_CALLBACK --> PC1_PJSM

    %% Cloud
    CloudAPI -->|"GET api/getStatusPrinters"| HTTP_API

    %% Feature flag detection
    PC1 -.->|"GET :8081<br/>/api/rest/featureflags/listar"| WEBSERVER
    WEBSERVER -.->|"USAR_PRINTER_SERVICE=true"| PC1_FLAG

    %% Estilos
    classDef cloud fill:#e1f5fe,stroke:#0288d1,stroke-width:2px
    classDef cliente fill:#fff3e0,stroke:#f57c00,stroke-width:2px
    classDef servidor fill:#e8f5e9,stroke:#388e3c,stroke-width:2px
    classDef ps fill:#f3e5f5,stroke:#7b1fa2,stroke-width:2px
    classDef impresora fill:#fce4ec,stroke:#c62828,stroke-width:2px
    classDef db fill:#fff9c4,stroke:#f9a825,stroke-width:2px

    class CloudAPI cloud
    class PC1,PC2,PC3,TABLET,PC1_DETAIL,PC1_PJSM,PC1_CALLBACK,PC1_FLAG cliente
    class WEBSERVER,PEDIDO_CTRL,FLAG_CHECK,PS_CLIENT,STRATEGIES,PJSM_SERVER,PS_MODULE servidor
    class HTTP_API,ENDPOINTS,JOB_MGR,PRINT_WORKER,STATUS_MON,NET_WATCH,NOTIFY,DRIVERS,TRANSPORT,RENDERER,UDP_DISC ps
    class IMP_COCINA,IMP_BAR,IMP_CAJA impresora
    class QUIPUDB,PSDB db
```

---

## 2. Diagrama de Flujo de Comunicación — Modo Servidor vs Modo Cliente

```mermaid
sequenceDiagram
    autonumber
    box rgb(255,243,224) Terminal Cliente (10.0.0.2)
        participant CLI as QuipuNet.exe<br/>Modo Cliente
        participant CLI_CB as PrintJobCallbackServer<br/>:8083
        participant CLI_PSM as PrintJobStatusManager<br/>(RAM)
    end

    box rgb(232,245,233) Servidor (10.0.0.100)
        participant WS as WebServer_New<br/>:8081
        participant CTRL as PedidoController<br/>+ ServerComandasStrategy
        participant PSC as PrinterServiceClient
        participant SRV_PSM as PrintJobStatusManager<br/>(RAM + BD)
    end

    box rgb(243,229,245) PrinterServices.exe
        participant PS as HTTP API :8090
        participant PW as PrintWorker
        participant NOTIF as JobStatusCallbackNotifier
    end

    box rgb(252,228,236) Impresora
        participant IMP as Impresora Cocina<br/>TCP:9100
    end

    Note over CLI,IMP: === FLUJO MODO CLIENTE (PC1 envía pedido) ===

    CLI->>WS: REST POST /api/rest/pedido/enviarPedidos
    Note right of CLI: PedidoClient.enviarPedidos()

    WS->>WS: ipClienteRest = GetClientIp(request) = "10.0.0.2"
    WS->>CTRL: enviarPedidos(data, ipClienteRest="10.0.0.2")

    CTRL->>CTRL: esModoServidor() && USAR_PRINTER_SERVICE → true
    CTRL->>PSC: EnviarComandasAsync(impresionList, ipClienteRest="10.0.0.2")

    PSC->>PSC: EnriquecerImpresion()<br/>ip_origen="10.0.0.2"<br/>ip_servidor="10.0.0.100"
    PSC->>PS: HTTP POST /api/print/comandas<br/>[JSON con ip_origen + ip_servidor]

    PS->>PS: ParsePrintJob() → Enqueue(job)<br/>job.IpOrigen="10.0.0.2"<br/>job.IpServidor="10.0.0.100"
    PS-->>PSC: 200 OK {jobs: [{job_id:"714", pedido_ids:["1772","1773"]}]}

    PSC-->>CTRL: Respuesta(SUCCESS, PrintJobResults)
    CTRL-->>WS: Respuesta con ImpresionList VACÍA + PrintJobResults
    WS-->>CLI: HTTP Response (ImpresionList vacía)

    CLI->>CLI_PSM: RegisterJobs([{714, pedido_ids}])<br/>Status=PENDING en RAM

    Note over PS,IMP: === IMPRESIÓN ASÍNCRONA ===

    PW->>PW: WaitAsync() se desbloquea
    PW->>IMP: StatusMonitor DLE EOT → OK
    PW->>IMP: TCP:9100 ESC/POS bytes
    IMP-->>PW: Impresión exitosa

    PW->>NOTIF: MarkDone(job) → NotifyStatusChangeAsync

    Note over NOTIF,SRV_PSM: 1. SIEMPRE notificar al servidor
    NOTIF->>WS: POST :8081/api/rest/printerservice/updateJobStatus<br/>{job_id:"714", status:"DONE"}
    WS->>SRV_PSM: UpdateStatus("714", "DONE")<br/>+ ActualizarEstadoEnBD()

    Note over NOTIF,CLI_CB: 2. Notificar al cliente (ip_origen ≠ ip_servidor)
    NOTIF->>CLI_CB: POST :8083/api/ps/callback<br/>{job_id:"714", status:"DONE"}
    CLI_CB->>CLI_PSM: UpdateStatus("714", "DONE")<br/>NO accede a BD (jamás)
    CLI_PSM->>CLI: Evento AllJobsDone → UI actualizada ✅
```

---

## 3. Diagrama de Flujo — Modo Servidor Directo (sin cliente REST)

```mermaid
sequenceDiagram
    autonumber
    box rgb(232,245,233) Servidor (10.0.0.100)
        participant FRONT as QuipuNet.exe Front<br/>(Presenter → Iterator → Router)
        participant CTRL as PedidoController<br/>+ ServerComandasStrategy
        participant PSC as PrinterServiceClient
        participant SRV_PSM as PrintJobStatusManager<br/>(RAM + BD)
        participant WS as WebServer_New :8081
    end

    box rgb(243,229,245) PrinterServices.exe
        participant PS as HTTP API :8090
        participant PW as PrintWorker
        participant NOTIF as JobStatusCallbackNotifier
    end

    box rgb(252,228,236) Impresora
        participant IMP as Impresora Bar<br/>TCP:9100
    end

    Note over FRONT,IMP: === FLUJO MODO SERVIDOR DIRECTO ===

    FRONT->>CTRL: Router detecta esModoServidor()=true<br/>→ Controller.addLista() LOCAL

    CTRL->>CTRL: esModoServidor() && USAR_PRINTER_SERVICE → true
    CTRL->>PSC: EnviarComandasAsync(impresionList, ipClienteRest="")

    PSC->>PSC: EnriquecerImpresion()<br/>ip_origen="10.0.0.100" (servidor)<br/>ip_servidor="10.0.0.100"
    PSC->>PS: HTTP POST /api/print/comandas

    PS->>PS: Enqueue(job)<br/>job.IpOrigen="10.0.0.100"<br/>job.IpServidor="10.0.0.100"
    PS-->>PSC: 200 OK {jobs: [{job_id:"720"}]}

    PSC-->>CTRL: Respuesta(SUCCESS, PrintJobResults)
    CTRL-->>FRONT: Respuesta con ImpresionList VACÍA
    FRONT->>SRV_PSM: RegisterJobs → PENDING

    Note over PW,IMP: === IMPRESIÓN ASÍNCRONA ===

    PW->>IMP: TCP:9100 ESC/POS bytes
    IMP-->>PW: OK

    PW->>NOTIF: MarkDone(job)

    Note over NOTIF,WS: Solo 1 notificación (ip_origen == ip_servidor)
    NOTIF->>WS: POST :8081 updateJobStatus<br/>{job_id:"720", status:"DONE"}
    WS->>SRV_PSM: UpdateStatus("720", "DONE") + BD
    SRV_PSM->>FRONT: Evento AllJobsDone → UI ✅

    Note right of NOTIF: IsLoopbackOrSameAsServer()=true<br/>→ NO envía segundo callback
```

---

## 4. Diagrama de Eventos Visuales (UI) — Modo Servidor

```mermaid
stateDiagram-v2
    direction TB

    state "QuipuNet Modo SERVIDOR" as SERVER {

        state "Presenter (Front)" as PRES_S {
            [*] --> EnviaPedido_S: Usuario confirma pedido
            EnviaPedido_S --> VerificaFlag_S: Respuesta del Backend

            state VerificaFlag_S <<choice>>
            VerificaFlag_S --> FlujoAntiguo_S: FLAG OFF<br/>ImpresionList con datos
            VerificaFlag_S --> FlujoPrinterService_S: FLAG ON<br/>ImpresionList vacía

            state "Flujo Antiguo (local)" as FlujoAntiguo_S {
                ImprimirLocal_S: ImpresionService.ImprimirAsync()
                ImprimirLocal_S --> PrintUtil_S: PrintUtil.imprimirComandasEthernet()
                PrintUtil_S --> TCP_S: ESCPOSPrinterEthernet → TCP:9100
            }

            state "Flujo PrinterServices" as FlujoPrinterService_S {
                DetectaPJR_S: Detecta PrintJobResults en Respuesta.Data
                DetectaPJR_S --> RegisterJobs_S: PrintJobStatusManager.RegisterJobs()
                RegisterJobs_S --> ShowModal_S: showImprimiendoPrinterService()
            }
        }

        state "Modal de Progreso (PSProgressService)" as MODAL_S {
            [*] --> Enviando0_S: "Enviando 0 de N documento(s)"
            Enviando0_S --> EnviandoX_S: Callback DONE llega<br/>CantidadCompletados++

            state EnviandoX_S <<choice>>
            EnviandoX_S --> BarraAzul_S: Parcial<br/>"Enviando X de N"
            EnviandoX_S --> BarraVerde_S: Todos OK<br/>"Enviando N de N"
            EnviandoX_S --> BarraRoja_S: Alguno FAILED/EXPIRED

            BarraAzul_S --> EnviandoX_S: Siguiente callback
            BarraVerde_S --> CierraExito_S: Auto-cierra modal
            BarraRoja_S --> ModalError_S: showFailedPrinterService()
        }

        state "Modal Error" as ModalError_S {
            MuestraFallos_S: Muestra jobs FAILED/EXPIRED
            MuestraFallos_S --> Reintentar_S: Usuario presiona Reintentar
            MuestraFallos_S --> Cerrar_S: Usuario cierra
            Reintentar_S --> MODAL_S: POST /api/job/{id}/retry
        }

        state "Cabecera (UtilesCabeceraViewModel)" as CAB_S {
            [*] --> ConfigVisibilidad_S: ConfigurarVisibilidadGestionImpresiones()
            ConfigVisibilidad_S --> BtnVisible_S: FLAG ON → Botón gestión visible
            ConfigVisibilidad_S --> BtnOculto_S: FLAG OFF → Botón oculto

            BtnVisible_S --> SubscribeQueue_S: SubscribeToQueueEvents()
            SubscribeQueue_S --> EscuchaEventos_S: PrintJobStatusManager eventos

            state "Eventos de Estado" as EscuchaEventos_S {
                JobStatusChanged_S: JobStatusChanged
                AllJobsDone_S: AllJobsDone
                SomeJobsFailed_S: SomeJobsFailed
            }

            JobStatusChanged_S --> BadgeUpdate_S: Actualizar badge
            SomeJobsFailed_S --> BadgeRojo_S: Badge "Impr. Fallida" ❌
            AllJobsDone_S --> BadgeVerde_S: Badge desaparece ✅
        }

        state "Callbacks desde PrinterServices" as CB_S {
            [*] --> RecibeCallback_S: POST :8081 updateJobStatus
            RecibeCallback_S --> PSModule_S: PrinterServiceModule procesa
            PSModule_S --> UpdateBD_S: ActualizarEstadoEnBD()
            PSModule_S --> UpdateRAM_S: PrintJobStatusManager.UpdateStatus()
            UpdateRAM_S --> EventoUI_S: Dispara eventos → UI se actualiza
        }
    }
```

---

## 5. Diagrama de Eventos Visuales (UI) — Modo Cliente

```mermaid
stateDiagram-v2
    direction TB

    state "QuipuNet Modo CLIENTE" as CLIENT {

        state "Inicialización (MenuPrincipalview)" as INIT_C {
            [*] --> WindowLoaded_C: Window_Loaded (async)
            WindowLoaded_C --> CheckFlag_C: checkearSiServidorTienePrinterServicesActivo()
            CheckFlag_C --> FetchFlag_C: GET :8081/api/rest/featureflags/listar
            FetchFlag_C --> SetFlag_C: UtilFront.FEATURE_FLAG = true/false

            state SetFlag_C <<choice>>
            SetFlag_C --> InitClient_C: FLAG=true → InitClientMode()
            SetFlag_C --> NoInit_C: FLAG=false → No hace nada

            InitClient_C --> StartMiniNancy_C: PrintJobCallbackServer.Start() :8083
            StartMiniNancy_C --> Ready_C: Listo para recibir callbacks
        }

        state "Presenter (Front)" as PRES_C {
            [*] --> EnviaPedido_C: Usuario confirma pedido
            EnviaPedido_C --> RESTServidor_C: PedidoClient.enviarPedidos()<br/>REST POST :8081

            RESTServidor_C --> RespuestaServidor_C: Servidor procesa + responde

            state VerificaFlag_C <<choice>>
            RespuestaServidor_C --> VerificaFlag_C

            VerificaFlag_C --> FlujoAntiguo_C: ImpresionList CON datos<br/>(servidor FLAG OFF)
            VerificaFlag_C --> FlujoPSCliente_C: ImpresionList VACÍA +<br/>esModoCliente() && FLAG_FROM_SERVER

            state "Flujo Antiguo (local)" as FlujoAntiguo_C {
                ImprimirLocal_C: Imprime localmente<br/>PrintUtil → TCP:9100
            }

            state "Flujo PrinterServices (delegado)" as FlujoPSCliente_C {
                DetectaPJR_C: Detecta PrintJobResults
                DetectaPJR_C --> RegisterJobs_C: PrintJobStatusManager.RegisterJobs()
                RegisterJobs_C --> ShowModal_C: showImprimiendoPrinterService()
            }
        }

        state "Modal de Progreso (idéntico al servidor)" as MODAL_C {
            [*] --> Enviando0_C: "Enviando 0 de N documento(s)"
            Enviando0_C --> EnviandoX_C: Callback DONE vía :8083

            state EnviandoX_C <<choice>>
            EnviandoX_C --> BarraAzul_C: Parcial
            EnviandoX_C --> BarraVerde_C: Todos OK
            EnviandoX_C --> BarraRoja_C: Alguno FAILED

            BarraAzul_C --> EnviandoX_C: Siguiente callback
            BarraVerde_C --> CierraExito_C: ✅ Éxito
            BarraRoja_C --> ModalError_C: ❌ Error
        }

        state "Callbacks desde PrinterServices" as CB_C {
            [*] --> RecibeCallback_C: POST :8083 /api/ps/callback
            RecibeCallback_C --> MiniNancy_C: PrintJobCallbackModule procesa
            MiniNancy_C --> UpdateRAM_C: PrintJobStatusManager.UpdateStatus()
            Note_C: ⚠️ JAMÁS accede a BD
            UpdateRAM_C --> EventoUI_C: Dispara eventos → UI se actualiza
        }

        state "Consulta Históricos (REST al servidor)" as HIST_C {
            [*] --> NecesitaHist_C: Entrar a mesa / ver estado
            NecesitaHist_C --> RESTProxy_C: PrintJobStatusClient.getFailedPedidoIds()
            RESTProxy_C --> ServidorProxy_C: GET :8081/api/rest/printerservice/jobs/failed-pedidos
            ServidorProxy_C --> RespProxy_C: Servidor consulta BD → responde
            RespProxy_C --> MarkBadge_C: p.IsPrinterComandaFailed = true<br/>→ Badge rojo visible
        }

        state "Cabecera (UtilesCabeceraViewModel)" as CAB_C {
            [*] --> ConfigVis_C: ConfigurarVisibilidadGestionImpresiones()

            state ConfigCheck_C <<choice>>
            ConfigVis_C --> ConfigCheck_C
            ConfigCheck_C --> BtnVisible_C: FeatureFlag local ON<br/>|| (esModoCliente() && FLAG_FROM_SERVER)
            ConfigCheck_C --> BtnOculto_C: Ambos OFF

            BtnVisible_C --> SubscribeQ_C: SubscribeToQueueEvents()
            SubscribeQ_C --> Eventos_C: PrintJobStatusManager eventos

            state "Eventos de Estado" as Eventos_C {
                JSC_C: JobStatusChanged
                AJD_C: AllJobsDone
                SJF_C: SomeJobsFailed
            }

            JSC_C --> BadgeUpd_C: Actualizar badge
            SJF_C --> BadgeR_C: Badge rojo ❌
            AJD_C --> BadgeG_C: Badge desaparece ✅
        }

        state "Cierre de sesión" as CLOSE_C {
            [*] --> WindowClosed_C: Window_Closed
            WindowClosed_C --> StopClient_C: PrintJobStatusManager.StopClientMode()
            StopClient_C --> StopNancy_C: PrintJobCallbackServer.Stop()
            StopNancy_C --> FreePort_C: Libera puerto :8083
        }
    }
```

---

## 6. Diagrama Comparativo — Puertos y Protocolos (Servidor vs Cliente)

```mermaid
graph LR
    subgraph PS["PrinterServices.exe<br/>(10.0.0.100)"]
        PS_HTTP[":8090 HTTP API"]
        PS_NOTIF["JobStatusCallbackNotifier"]
    end

    subgraph SRV["QuipuNet Servidor<br/>(10.0.0.100)"]
        SRV_NANCY[":8081 WebServer_New<br/>(Nancy completo)"]
        SRV_PSM["PrinterServiceModule<br/>updateJobStatus"]
        SRV_BD[("BD SQLite<br/>pedidoprinterjob")]
        SRV_PROXY["Endpoints proxy<br/>para clientes"]
    end

    subgraph CLI1["QuipuNet Cliente PC1<br/>(10.0.0.2)"]
        CLI1_MINI[":8083 Mini Nancy<br/>(PrintJobCallbackServer)"]
        CLI1_RAM["PrintJobStatusManager<br/>(RAM only)"]
    end

    subgraph CLI2["QuipuNet Cliente PC2<br/>(10.0.0.3)"]
        CLI2_MINI[":8083 Mini Nancy<br/>(PrintJobCallbackServer)"]
        CLI2_RAM["PrintJobStatusManager<br/>(RAM only)"]
    end

    %% PrinterServices → Servidor (siempre)
    PS_NOTIF -->|"POST :8081<br/>updateJobStatus<br/>(SIEMPRE)"| SRV_PSM
    SRV_PSM --> SRV_BD

    %% PrinterServices → Clientes (si ip_origen ≠ ip_servidor)
    PS_NOTIF -->|"POST :8083<br/>/api/ps/callback"| CLI1_MINI
    PS_NOTIF -->|"POST :8083<br/>/api/ps/callback"| CLI2_MINI
    CLI1_MINI --> CLI1_RAM
    CLI2_MINI --> CLI2_RAM

    %% Clientes consultan históricos al servidor
    CLI1_RAM -.->|"GET :8081<br/>jobs/failed-pedidos<br/>(históricos)"| SRV_PROXY
    CLI2_RAM -.->|"GET :8081<br/>jobs/failed-pedidos<br/>(históricos)"| SRV_PROXY
    SRV_PROXY --> SRV_BD

    %% Servidor envía a PS
    SRV_NANCY -->|"POST :8090<br/>/api/print/comandas"| PS_HTTP

    style PS fill:#f3e5f5,stroke:#7b1fa2,stroke-width:2px
    style SRV fill:#e8f5e9,stroke:#388e3c,stroke-width:2px
    style CLI1 fill:#fff3e0,stroke:#f57c00,stroke-width:2px
    style CLI2 fill:#fff3e0,stroke:#f57c00,stroke-width:2px
```

---

## 7. Diagrama Detallado — Modal Progresivo en Modo Cliente

### 7.1 Flujo completo: Desde el pedido hasta el cierre del modal

```mermaid
sequenceDiagram
    autonumber
    box rgb(255,243,224) PC1 — Terminal Cliente (10.0.0.2)
        participant USER as Usuario<br/>(toca "Enviar Pedido")
        participant PRES as ListaPedidosTemporalesPresenter
        participant VIEW as ListaPedidosTemporales<br/>(IListaPedidosTemporalesView)
        participant PJSM as PrintJobStatusManager<br/>(RAM only, IsClientMode=true)
        participant PSPROG as PSProgressService<br/>(ViewModel del Modal)
        participant MODAL as CustomModalImprimiendoNewView<br/>(Window WPF)
        participant MINI as PrintJobCallbackServer<br/>Mini Nancy :8083
    end

    box rgb(232,245,233) Servidor (10.0.0.100)
        participant SRV as QuipuNet Servidor<br/>:8081 → PedidoController
    end

    box rgb(243,229,245) PrinterServices.exe
        participant PS as PrinterServices<br/>:8090
        participant NOTIF as JobStatusCallbackNotifier
    end

    Note over USER,PS: === FASE 1: Envío del pedido y registro de jobs ===

    USER->>PRES: Click "Enviar Pedido"
    PRES->>SRV: PedidoClient.enviarPedidos()<br/>REST POST :8081

    Note right of SRV: Servidor: Controller → PrinterServiceClient<br/>→ POST :8090 /api/print/comandas<br/>ip_origen="10.0.0.2", ip_servidor="10.0.0.100"

    SRV-->>PRES: Respuesta HTTP:<br/>ImpresionList = VACÍA<br/>PrintJobResults = [{job_id:"714", pedido_ids:["1772","1773"]},<br/>{job_id:"715", pedido_ids:["1774"]}]

    PRES->>PRES: Evalúa condición:<br/>esModoCliente() && FLAG_FROM_SERVER = true<br/>→ verificarPrintJobsPorComandas()

    PRES->>PRES: tieneJobs = PrintJobResults.Count > 0 → true

    PRES->>PJSM: RegisterJobs([{714,["1772","1773"]}, {715,["1774"]}])<br/>Status = PENDING en RAM

    Note over PRES,MODAL: === FASE 2: Creación del Modal Progresivo ===

    PRES->>VIEW: view.showImprimiendoPrinterService(<br/>onSuccess, onFail, cantidadJobs=2)

    VIEW->>PSPROG: new PSProgressService(cantidadJobsEsperados=2)

    Note over PSPROG: Constructor PSProgressService:<br/>_cantidadTotal = 2 (del caller, NO de PJSM.Count)<br/>_cantidadCompletados = 0<br/>CantidadPendiente = 2<br/>Progreso = 0%<br/>timeout = 300s (modo cliente, 5 min)<br/><br/>Se suscribe a:<br/>• PJSM.JobStatusChanged<br/>• PJSM.AllJobsDone<br/>• PJSM.AnyJobFailed<br/><br/>Verifica race condition:<br/>allJobs terminados? → 0 de 2 → continúa normal

    PSPROG->>PSPROG: YaFinalizado = false

    VIEW->>MODAL: new CustomModalImprimiendoNewView(psProgress)<br/>DataContext = PSProgressService

    VIEW->>MODAL: modal.Show()<br/>Posición: esquina inferior derecha

    VIEW->>PSPROG: psProgress.Completado += (huboErrores) => { ... }

    Note over MODAL: Modal visible:<br/>"Enviando 0 de 2 documento(s)"<br/>Barra: 0% azul<br/>Timer: 00:00

    Note over MINI,PS: === FASE 3: Callbacks llegan desde PrinterServices ===

    PS->>PS: PrintWorker imprime Job 714<br/>→ TCP:9100 → Impresora Cocina → OK

    NOTIF->>SRV: POST :8081/updateJobStatus<br/>{job_id:"714", status:"DONE"}
    NOTIF->>MINI: POST :8083/api/ps/callback<br/>{job_id:"714", status:"DONE"}

    MINI->>PJSM: UpdateStatus("714", "DONE")<br/>⚠️ NO accede a BD (jamás)

    PJSM->>PSPROG: evento JobStatusChanged("714", "DONE")

    Note over PSPROG: OnJobStatusChanged (en UI thread via Dispatcher):<br/>terminados = 1, _cantidadTotal = 2<br/>CantidadCompletados = 1 → OnPropertyChanged<br/>CantidadPendiente = 1<br/>Progreso = (1/2)*100 = 50%

    PSPROG->>MODAL: Binding Update:<br/>CantidadCompletados=1, Progreso=50%

    Note over MODAL: Modal actualizado:<br/>"Enviando 1 de 2 documento(s)"<br/>Barra: 50% azul<br/>Timer: 00:01

    PS->>PS: PrintWorker imprime Job 715<br/>→ TCP:9100 → Impresora Bar → OK

    NOTIF->>SRV: POST :8081/updateJobStatus<br/>{job_id:"715", status:"DONE"}
    NOTIF->>MINI: POST :8083/api/ps/callback<br/>{job_id:"715", status:"DONE"}

    MINI->>PJSM: UpdateStatus("715", "DONE")

    PJSM->>PSPROG: evento JobStatusChanged("715", "DONE")

    Note over PSPROG: OnJobStatusChanged:<br/>terminados = 2, _cantidadTotal = 2<br/>CantidadCompletados = 2<br/>Progreso = 100%

    PSPROG->>MODAL: Binding Update:<br/>CantidadCompletados=2, Progreso=100%

    Note over MODAL: Modal actualizado:<br/>"Enviando 2 de 2 documento(s)"<br/>Barra: 100% VERDE ✅<br/>Timer: 00:02

    PJSM->>PSPROG: evento AllJobsDone(["1772","1773","1774"])

    Note over PSPROG,MODAL: === FASE 4: Cierre del modal y resultado ===

    Note over PSPROG: OnAllJobsDone (en UI thread):<br/>_finalizado = true<br/>HayErroresEnCola = false (todos DONE)<br/>Progreso = 100%<br/>_timer.Stop()<br/>Desuscribirse() de PJSM eventos<br/><br/>DispatcherTimer delay 800ms<br/>(para que usuario vea barra verde 100%)

    PSPROG-->>PSPROG: 800ms delay...

    PSPROG->>VIEW: Completado.Invoke(huboErrores=false)

    VIEW->>MODAL: modalImprimiendo.Close()

    Note over VIEW: DispatcherTimer 500ms (transición visual)

    VIEW->>VIEW: onSuccess callback:<br/>view.showSuccessPrinterService() → Toast verde "¡Éxito!" 3s<br/>view.showSuccessRegister() → Navegar a lista pedidos
```

### 7.2 Flujo con error: Un job falla en modo cliente

```mermaid
sequenceDiagram
    autonumber
    box rgb(255,243,224) PC1 — Terminal Cliente (10.0.0.2)
        participant PSPROG as PSProgressService
        participant MODAL as Modal Progreso
        participant VIEW as View
        participant PJSM as PrintJobStatusManager
        participant MINI as Mini Nancy :8083
    end

    box rgb(243,229,245) PrinterServices
        participant NOTIF as JobStatusCallbackNotifier
    end

    Note over PSPROG,MODAL: Estado actual: "Enviando 0 de 3 documento(s)" — Barra 0%

    NOTIF->>MINI: callback {714, "DONE"}
    MINI->>PJSM: UpdateStatus("714","DONE")
    PJSM->>PSPROG: JobStatusChanged("714","DONE")

    Note over MODAL: "Enviando 1 de 3 documento(s)" — Barra 33% azul

    NOTIF->>MINI: callback {715, "FAILED", error:"Impresora offline"}
    MINI->>PJSM: UpdateStatus("715","FAILED")
    PJSM->>PSPROG: AnyJobFailed("715", "Impresora offline")

    Note over PSPROG: OnAnyJobFailed:<br/>HayErroresEnCola = true → barra ROJA

    PJSM->>PSPROG: JobStatusChanged("715","FAILED")

    Note over PSPROG: OnJobStatusChanged:<br/>1. FAILED → HayErroresEnCola = true (ya estaba)<br/>2. terminados=2, CantidadCompletados=2<br/>3. Progreso = 66%

    Note over MODAL: "Enviando 2 de 3 documento(s)" — Barra 66% ROJA ❌

    NOTIF->>MINI: callback {716, "DONE"}
    MINI->>PJSM: UpdateStatus("716","DONE")
    PJSM->>PSPROG: JobStatusChanged("716","DONE")

    Note over PSPROG: terminados=3, Progreso=100%

    Note over MODAL: "Enviando 3 de 3 documento(s)" — Barra 100% ROJA ❌

    PJSM->>PSPROG: AllJobsDone(pedidoIds)

    Note over PSPROG: OnAllJobsDone:<br/>HayErroresEnCola = true<br/>Progreso=100%, _finalizado=true<br/>Desuscribirse()<br/>Delay 800ms...

    PSPROG->>VIEW: Completado.Invoke(huboErrores=true)

    VIEW->>MODAL: modal.Close()

    Note over VIEW: Delay 500ms transición

    VIEW->>VIEW: onFail callback

    Note over VIEW: showFailedPrinterService():<br/>1. CustomModalImpresionFallidaNew.PuedeAbrir()?<br/>2. new PSModaImpresionFallidaViewModel()<br/>   → Lee PJSM.GetAllJobs() → encuentra 715 FAILED<br/>   → Cuenta errores, extrae motivos<br/>3. Muestra modal error con botón "Reintentar"<br/>4. showSuccessRegister() → navega a lista pedidos
```

### 7.3 Diagrama de clases y bindings del modal progresivo

```mermaid
classDiagram
    class PSProgressService {
        <<ViewModel - INotifyPropertyChanged>>
        -DispatcherTimer _timer
        -DateTime _inicio
        -double _progreso
        -int _cantidadTotal
        -int _cantidadCompletados
        -int _cantidadPendiente
        -bool _hayErroresEnCola
        -bool _finalizado
        -bool _yaFinalizadoConErrores
        +string FechaInicio
        +string HoraInicio
        +int CantidadTotal
        +int CantidadCompletados
        +int CantidadPendiente
        +double Progreso
        +string TiempoTranscurrido
        +bool HayErroresEnCola
        +bool YaFinalizado
        +bool YaFinalizadoConErrores
        +event Completado(bool huboErrores)
        +PSProgressService(cantidadJobsEsperados, ownerWindow)
        +Detener()
        -OnJobStatusChanged(jobId, status)
        -OnAllJobsDone(pedidoIds)
        -OnAnyJobFailed(jobId, error)
        -Timer_Tick()
        -Desuscribirse()
    }

    class CustomModalImprimiendoNewView {
        <<Window WPF>>
        -ProgressService _progressService
        +CustomModalImprimiendoNewView()
        +CustomModalImprimiendoNewView(externalDataContext)
    }

    class PrintJobStatusManager {
        <<Singleton - RAM only en cliente>>
        -PrintJobCallbackServer _callbackServer
        -bool _isClientMode
        +bool IsClientMode
        +int Count
        +RegisterJobs(jobs)
        +UpdateStatus(jobId, status)
        +GetAllJobs() List~PrintJobInfo~
        +GetPendingCount() int
        +InitClientMode()
        +StopClientMode()
        +event JobStatusChanged(jobId, status)
        +event AllJobsDone(pedidoIds)
        +event AnyJobFailed(jobId, error)
    }

    class PrintJobCallbackServer {
        <<Mini Nancy :8083>>
        +int CALLBACK_PORT = 8083
        -NancyHost _host
        +bool IsRunning
        +Start()
        +Stop()
        +Dispose()
    }

    class PrintJobCallbackModule {
        <<NancyModule /api/ps>>
        +POST /callback
        -ProcessCallback()
    }

    class CallbackOnlyBootstrapper {
        <<NancyBootstrapper>>
        -BeforeRequest pipeline
        +Solo permite POST /api/ps/callback
        +Todo lo demás → 404
    }

    class ListaPedidosTemporales {
        <<View - IListaPedidosTemporalesView>>
        +showImprimiendoPrinterService(onSuccess, onFail, cantidadJobs)
        +showFailedPrinterService()
        +showSuccessPrinterService()
        +showSuccessRegister()
    }

    class ListaPedidosTemporalesPresenter {
        <<Presenter>>
        -verificarPrintJobsPorComandas(respuesta, view)
        +enviarPedidos()
    }

    PSProgressService --> PrintJobStatusManager : se suscribe a eventos
    CustomModalImprimiendoNewView --> PSProgressService : DataContext (MVVM Binding)
    PrintJobCallbackServer --> PrintJobCallbackModule : hospeda
    PrintJobCallbackServer --> CallbackOnlyBootstrapper : usa
    PrintJobCallbackModule --> PrintJobStatusManager : UpdateStatus()
    PrintJobStatusManager --> PrintJobCallbackServer : _callbackServer
    ListaPedidosTemporales --> PSProgressService : crea instancia
    ListaPedidosTemporales --> CustomModalImprimiendoNewView : crea y muestra
    ListaPedidosTemporalesPresenter --> ListaPedidosTemporales : view.showImprimiendoPrinterService()
```

### 7.4 Bindings XAML del modal

```mermaid
graph LR
    subgraph XAML["CustomModalImprimiendoNewView.xaml"]
        TXT1["TextBlock: 'Enviando '"]
        TXT2["TextBlock: Binding CantidadCompletados"]
        TXT3["TextBlock: ' de '"]
        TXT4["TextBlock: Binding CantidadTotal"]
        TXT5["TextBlock: ' documento(s)'"]
        FECHA["TextBlock: Binding FechaInicio"]
        HORA["TextBlock: Binding HoraInicio"]
        BARRA["ProgressBar: Binding Progreso<br/>+ TiempoTranscurrido<br/>+ EsErrorProgress = HayErroresEnCola"]
    end

    subgraph VM["PSProgressService (DataContext)"]
        CC["CantidadCompletados<br/>0 → 1 → 2 → N"]
        CT["CantidadTotal<br/>N (fijo)"]
        FI["FechaInicio<br/>(fijo)"]
        HI["HoraInicio<br/>(fijo)"]
        PR["Progreso<br/>0% → 50% → 100%"]
        TT["TiempoTranscurrido<br/>00:00 → 00:01 → ..."]
        HE["HayErroresEnCola<br/>false/true"]
    end

    subgraph EVENTOS["Eventos PrintJobStatusManager"]
        JSC["JobStatusChanged<br/>(jobId, status)"]
        AJD["AllJobsDone<br/>(pedidoIds)"]
        AJF["AnyJobFailed<br/>(jobId, error)"]
    end

    CC -->|Binding| TXT2
    CT -->|Binding| TXT4
    FI -->|Binding| FECHA
    HI -->|Binding| HORA
    PR -->|Binding| BARRA
    TT -->|Binding| BARRA
    HE -->|Binding| BARRA

    JSC -->|"OnJobStatusChanged<br/>→ CantidadCompletados++<br/>→ Progreso = (comp/total)*100"| CC
    JSC -->|actualiza| PR
    AJF -->|"OnAnyJobFailed<br/>→ HayErroresEnCola=true<br/>→ Barra ROJA"| HE
    AJD -->|"OnAllJobsDone<br/>→ Progreso=100%<br/>→ Completado(huboErrores)<br/>→ Cierra modal"| PR

    style XAML fill:#e3f2fd,stroke:#1565c0
    style VM fill:#e8f5e9,stroke:#388e3c
    style EVENTOS fill:#fff3e0,stroke:#f57c00
```

### 7.5 Ciclo de vida completo del modal — Estados y transiciones

```mermaid
stateDiagram-v2
    direction TB

    state "Modal Progresivo — Modo Cliente" as MODAL_CLIENT {

        state "INICIALIZACIÓN" as INIT {
            [*] --> CrearPSProgress: Presenter llama view.showImprimiendoPrinterService(onSuccess, onFail, cantidadJobs)
            CrearPSProgress --> SetCantidad: _cantidadTotal = cantidadJobsEsperados (del caller)
            SetCantidad --> VerificarRace: Verificar si jobs ya terminaron (race condition PS <5ms)

            state VerificarRace <<choice>>
            VerificarRace --> YaTermino: terminados >= _cantidadTotal
            VerificarRace --> ContinuaNormal: terminados < _cantidadTotal

            YaTermino --> SkipModal: YaFinalizado=true → NO mostrar modal

            state SkipModal <<choice>>
            SkipModal --> DirectoSuccess: Sin errores → onSuccess()
            SkipModal --> DirectoFail: Con errores → onFail()

            ContinuaNormal --> Suscribirse: Suscribirse a PJSM eventos
            Suscribirse --> IniciarTimer: DispatcherTimer 500ms
            IniciarTimer --> CrearModal: new CustomModalImprimiendoNewView(psProgress)
            CrearModal --> MostrarModal: modal.Show() esquina inferior derecha
        }

        state "MODAL VISIBLE — Esperando callbacks" as VISIBLE {
            [*] --> Enviando0: "Enviando 0 de N documento(s)"
            Enviando0 --> EsperaCallback: Barra 0% azul — Timer 00:00

            state "Recibe Callback vía Mini Nancy :8083" as EsperaCallback {
                [*] --> CallbackLlega: POST /api/ps/callback
                CallbackLlega --> PJSM_Update: PJSM.UpdateStatus(jobId, status)
                PJSM_Update --> DisparaEvento: Dispara JobStatusChanged / AnyJobFailed
            }

            EsperaCallback --> EvaluaStatus: OnJobStatusChanged en UI thread

            state EvaluaStatus <<choice>>
            EvaluaStatus --> StatusDONE: status == "DONE"
            EvaluaStatus --> StatusFAILED: status == "FAILED" o "EXPIRED"

            StatusDONE --> ActualizaProgreso: CantidadCompletados++<br/>Progreso = (comp/total)*100
            StatusFAILED --> MarcarError: HayErroresEnCola = true<br/>→ Barra cambia a ROJA
            MarcarError --> ActualizaProgreso

            ActualizaProgreso --> VerificaTodos: ¿terminados >= _cantidadTotal?

            state VerificaTodos <<choice>>
            VerificaTodos --> EsperaCallback: Faltan más callbacks
            VerificaTodos --> TodosTerminaron: AllJobsDone disparado
        }

        state "TIMEOUT de seguridad" as TIMEOUT {
            [*] --> CheckTimeout: Timer_Tick cada 500ms
            CheckTimeout --> TimeoutAlcanzado: tiempo >= 300s (5 min en cliente)
            TimeoutAlcanzado --> ForzarCierre: _finalizado=true<br/>HayErroresEnCola=true<br/>Progreso=100% (barra roja)<br/>Completado(huboErrores=true)
        }

        state "FINALIZACIÓN" as FIN {
            TodosTerminaron --> SetFinalizado: _finalizado = true
            SetFinalizado --> CalculaErrores: Recorre allJobs → ¿alguno FAILED/EXPIRED?
            CalculaErrores --> SetProgreso100: Progreso = 100%
            SetProgreso100 --> StopTimer: _timer.Stop()
            StopTimer --> Desuscribir: Desuscribirse de PJSM eventos
            Desuscribir --> Delay800ms: DispatcherTimer 800ms<br/>(usuario ve barra verde/roja al 100%)

            state EvalResultado <<choice>>
            Delay800ms --> EvalResultado
            EvalResultado --> CompletadoOK: HayErroresEnCola=false
            EvalResultado --> CompletadoError: HayErroresEnCola=true

            CompletadoOK --> InvokeSuccess: Completado.Invoke(false)
            CompletadoError --> InvokeFail: Completado.Invoke(true)
        }

        state "CIERRE DEL MODAL" as CIERRE {
            InvokeSuccess --> CerrarModal: modalImprimiendo.Close()
            InvokeFail --> CerrarModal
            ForzarCierre --> CerrarModal
            CerrarModal --> Delay500ms: DispatcherTimer 500ms transición visual

            state ResultadoFinal <<choice>>
            Delay500ms --> ResultadoFinal

            ResultadoFinal --> OnSuccess: huboErrores=false
            ResultadoFinal --> OnFail: huboErrores=true

            OnSuccess --> ToastVerde: showSuccessPrinterService()<br/>Toast "¡Éxito!" 3s
            ToastVerde --> Navegar: showSuccessRegister()

            OnFail --> ModalFallida: showFailedPrinterService()
            ModalFallida --> CrearVM: new PSModaImpresionFallidaViewModel()
            CrearVM --> LeerPJSM: Lee PJSM.GetAllJobs()<br/>Cuenta FAILED/EXPIRED<br/>Extrae motivos de error
            LeerPJSM --> MostrarModalError: CustomModalImpresionFallidaNew<br/>con botón "Reintentar"
            MostrarModalError --> Navegar
        }
    }
```

### 7.6 Diferencias clave del timeout: Servidor vs Cliente

```mermaid
graph TD
    subgraph TIMEOUT["Timeout de seguridad en PSProgressService"]
        CHECK{"PrintJobStatusManager<br/>.IsClientMode?"}

        CHECK -->|"true (CLIENTE)"| CLIENT_T["Timeout = 300s (5 minutos)<br/><br/>Razones:<br/>• Callbacks viajan por RED (no localhost)<br/>• Más latencia de red<br/>• ExpirarImpresionDespuesDe puede ser 240s<br/>• Mini Nancy :8083 es quien controla flujo"]

        CHECK -->|"false (SERVIDOR)"| SERVER_T["Timeout = 30s<br/><br/>Razones:<br/>• PS corre en LOCALHOST<br/>• Callbacks llegan en menos de 5ms<br/>• Si no llega en 30s, algo está muy mal"]

        CLIENT_T --> TIMEOUT_ACTION["Si timeout alcanzado:<br/>_finalizado = true<br/>HayErroresEnCola = true<br/>Progreso = 100% (barra ROJA)<br/>Completado(huboErrores=true)<br/>→ Modal error con reintento"]

        SERVER_T --> TIMEOUT_ACTION
    end

    style CHECK fill:#e3f2fd,stroke:#1565c0,stroke-width:2px
    style CLIENT_T fill:#fff3e0,stroke:#f57c00,stroke-width:2px
    style SERVER_T fill:#e8f5e9,stroke:#388e3c,stroke-width:2px
    style TIMEOUT_ACTION fill:#ffebee,stroke:#c62828,stroke-width:2px
```

---

## 8. Tabla Resumen — Decisiones por Escenario

```mermaid
graph TD
    START{"¿Quién origina<br/>la impresión?"}

    START -->|"SERVIDOR directo<br/>(FLAG ON)"| E2["ip_origen = 10.0.0.100<br/>ip_servidor = 10.0.0.100"]
    START -->|"CLIENTE vía REST<br/>(FLAG ON en servidor)"| E4["ip_origen = 10.0.0.2<br/>ip_servidor = 10.0.0.100"]
    START -->|"FLAG OFF<br/>(cualquier terminal)"| E_OFF["Flujo antiguo<br/>Imprime localmente<br/>PrintUtil → TCP:9100"]

    E2 --> N2{"¿A quién notifica<br/>PrinterServices?"}
    N2 -->|"1 callback"| N2A["Solo servidor :8081<br/>(ip_origen == ip_servidor)"]

    E4 --> N4{"¿A quién notifica<br/>PrinterServices?"}
    N4 -->|"2 callbacks"| N4A["1. Servidor :8081<br/>2. Cliente :8083"]

    E_OFF --> NO_PS["PrinterServices<br/>no interviene"]

    style START fill:#e3f2fd,stroke:#1565c0,stroke-width:2px
    style E2 fill:#e8f5e9,stroke:#2e7d32
    style E4 fill:#fff3e0,stroke:#ef6c00
    style E_OFF fill:#efebe9,stroke:#795548
    style N2A fill:#c8e6c9,stroke:#388e3c
    style N4A fill:#ffe0b2,stroke:#f57c00
    style NO_PS fill:#d7ccc8,stroke:#795548
```
