# Vibe Engineering — Arquitectura Cliente/Servidor QuipuNet

## 1. Componentes del ecosistema

### 1.1 Proyectos

| Proyecto | Descripción | Tipo |
|---|---|---|
| **QuipuNet.exe** | Aplicación WPF - Front (UI + lógica de presentación) | Ejecutable |
| **QuipuNetX.dll** | Backend - Controladores, entidades, lógica de negocio | DLL |

### 1.2 Configuración de una terminal

Una **terminal** es una instalación física de QuipuNet que puede operar en dos modos:

- **Modo CLIENTE**: Terminal que se conecta a un servidor remoto
- **Modo SERVIDOR**: Terminal que procesa su propia lógica de negocio Y atiende peticiones de clientes

**IMPORTANTE**: Una misma terminal **SIEMPRE tiene ambos proyectos** (QuipuNet.exe + QuipuNetX.dll). El modo se determina en tiempo de ejecución mediante `Util.esModoServidor()`.

---

## 2. Flujo de peticiones según el modo

### 2.1 Terminal en MODO SERVIDOR

```
┌─────────────────────────────────────────────────────────────┐
│  TERMINAL SERVIDOR (QuipuNet.exe + QuipuNetX.dll)           │
│                                                             │
│  ┌─────────────────┐                                        │
│  │ QuipuNet.exe    │                                        │
│  │ (Front/UI)      │                                        │
│  │                 │                                        │
│  │  Presenter      │                                        │
│  │    └→ Iterator  │                                        │
│  │         └→ Router                                        │
│  └─────────┬───────┘                                        │
│            │                                                │
│            │ Util.esModoServidor() = TRUE                   │
│            │ → Router llama DIRECTO a Controller local      │
│            │                                                │
│            ▼                                                │
│  ┌─────────────────┐                                        │
│  │ QuipuNetX.dll   │                                        │
│  │ (Backend)       │                                        │
│  │                 │                                        │
│  │  Controller     │                                        │
│  │    └→ Procesa lógica de negocio                          │
│  │    └→ Retorna Respuesta                                  │
│  └─────────────────┘                                        │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

**Características**:
- Router detecta `Util.esModoServidor() == true`
- Llama **directamente** a los Controllers locales (ej: `PedidoController.addLista()`)
- NO hace peticiones REST
- Procesa la lógica de negocio localmente
- **TAMBIÉN** atiende peticiones REST de clientes remotos

---

### 2.2 Terminal en MODO CLIENTE

```
┌──────────────────────────────────┐         ┌─────────────────────────────────┐
│ TERMINAL CLIENTE                 │         │ TERMINAL SERVIDOR               │
│ (QuipuNet.exe + QuipuNetX.dll)   │         │ (QuipuNet.exe + QuipuNetX.dll)  │
│                                  │         │                                 │
│  ┌─────────────────┐             │         │                                 │
│  │ QuipuNet.exe    │             │         │                                 │
│  │ (Front/UI)      │             │         │                                 │
│  │                 │             │         │                                 │
│  │  Presenter      │             │         │                                 │
│  │    └→ Iterator  │             │         │                                 │
│  │         └→ Router             │         │                                 │
│  └─────────┬───────┘             │         │                                 │
│            │                     │         │                                 │
│            │ Util.esModoServidor() = FALSE│                                 │
│            │ → Router hace petición REST  │                                 │
│            │                     │         │                                 │
│            └─────────────────────┼─────────┼→ HTTP REST                      │
│                                  │         │  POST /api/pedido/addLista      │
│                                  │         │                                 │
│                                  │         │  ┌─────────────────┐            │
│                                  │         │  │ REST Endpoint   │            │
│                                  │         │  │  └→ Controller  │            │
│                                  │         │  └────────┬────────┘            │
│                                  │         │           │                     │
│                                  │         │           ▼                     │
│  ┌─────────────────┐             │         │  ┌─────────────────┐            │
│  │ QuipuNetX.dll   │             │         │  │ QuipuNetX.dll   │            │
│  │ (Backend local) │             │         │  │ (Backend)       │            │
│  │                 │             │         │  │                 │            │
│  │  NO SE USA      │             │         │  │  Controller     │            │
│  │  (cliente no    │             │         │  │    └→ Procesa   │            │
│  │   ejecuta lógica│             │         │  │    └→ Retorna   │            │
│  │   de negocio)   │             │    HTTP │  │       Respuesta │            │
│  └─────────────────┘             │  ◄──────┼──┘                 │            │
│                                  │  Response                    │            │
└──────────────────────────────────┘         └─────────────────────────────────┘
```

**Características**:
- Router detecta `Util.esModoServidor() == false`
- **NO llama a Controllers locales**
- Hace petición **REST HTTP** al servidor (ej: `POST /api/pedido/addLista`)
- El servidor procesa la lógica de negocio
- El cliente recibe la `Respuesta` vía HTTP
- La DLL `QuipuNetX.dll` del cliente **NO ejecuta lógica de negocio** (solo entidades y utilidades)

---

## 3. El rol del Router

El **Router** es el componente que decide si ejecutar localmente o hacer petición REST:

```csharp
// Ejemplo: PedidoRouter.addLista()
public static Respuesta addLista(IList<Pedido> pedidoList, ...)
{
    if (Util.esModoServidor())
    {
        // SERVIDOR: llamar Controller local DIRECTO
        return PedidoController.addLista(pedidoList, ...);
    }
    else
    {
        // CLIENTE: hacer petición REST al servidor
        return PedidoClient.addLista(pedidoList, ...);
    }
}
```

**El Front (QuipuNet.exe) SIEMPRE llama al Router**, nunca directamente al Controller ni al Client.

---

## 4. Implicaciones para el feature flag USAR_PRINTER_SERVICE

### 4.1 Terminal SERVIDOR con flag ON

```
Front (QuipuNet.exe)
  └→ Presenter.enviarPedidos()
       └→ Iterator.enviarPedidos()
            └→ Router.addLista() [detecta esModoServidor() = TRUE]
                 └→ Controller.addLista() [ejecuta localmente]
                      │
                      ├─ Genera ImpresionList
                      │
                      ├─ Verifica: esModoServidor() && USAR_PRINTER_SERVICE
                      │
                      ├─ SI: Envía a PrinterServices vía HTTP
                      │      Vacía ImpresionList (línea 639)
                      │      Retorna Respuesta SIN ImpresionList
                      │
                      └─ NO: Retorna Respuesta CON ImpresionList
```

### 4.2 Terminal CLIENTE (independiente del flag)

```
Front Cliente (QuipuNet.exe)
  └→ Presenter.enviarPedidos()
       └→ Iterator.enviarPedidos()
            └→ Router.addLista() [detecta esModoServidor() = FALSE]
                 └→ Client.addLista() [petición REST al servidor]
                      │
                      │ HTTP POST → Servidor
                      │
                      ▼
Front Servidor recibe petición REST
  └→ REST Endpoint → Controller.addLista()
       │
       ├─ Genera ImpresionList
       │
       ├─ Verifica: esModoServidor() && USAR_PRINTER_SERVICE
       │
       ├─ SI: Envía a PrinterServices
       │      Vacía ImpresionList
       │      Retorna Respuesta SIN ImpresionList → HTTP → Cliente
       │
       └─ NO: Retorna Respuesta CON ImpresionList → HTTP → Cliente
```

**El cliente recibe la Respuesta del servidor**. Si el servidor tiene flag ON, recibirá `ImpresionList` vacía. Si flag OFF, recibirá `ImpresionList` con comandas.

### 4.3 Lógica en el Front (IGUAL para servidor y cliente)

```csharp
// ListaPedidosTemporalesPresenter.enviarPedidos() - callback
if (respuesta.Tipo == SUCCESS)
{
    // El BACKEND (local si servidor, remoto si cliente) ya decidió
    // Si backend tiene flag ON + es servidor → ImpresionList vacía
    // Si backend tiene flag OFF o es cliente → ImpresionList con comandas
    
    if (respuesta.ImpresionList != null && respuesta.ImpresionList.Count > 0)
    {
        // Hay comandas para imprimir localmente
        this.imprimirComandas(respuesta.ImpresionList);
    }
    else
    {
        // No hay comandas (backend las delegó a PrinterServices O no había nada)
        // Solo mostrar éxito
    }
    
    view.showSuccessRegister();
}
```

**IMPORTANTE**: El Front **NO verifica el flag**. Solo verifica si `ImpresionList` tiene datos. El backend (local o remoto) ya tomó la decisión.

---

## 5. ¿Por qué el Front NO debe verificar el flag?

### 5.1 Escenario problemático (INCORRECTO)

Si el Front verifica `USAR_PRINTER_SERVICE` y `esModoServidor()`:

```csharp
// ❌ INCORRECTO
if (esModoServidor() && FeatureFlagConfigReader.IsEnabled("USAR_PRINTER_SERVICE"))
{
    // No imprimir
}
else
{
    if (respuesta.ImpresionList != null && respuesta.ImpresionList.Count > 0)
        imprimirComandas(respuesta.ImpresionList);
}
```

**Problema**: Un cliente recibiría `ImpresionList` vacía del servidor (flag ON), pero `esModoServidor() = false` en el cliente, entonces entraría al `else` y **NO imprimiría nada**, pero tampoco mostraría error. El usuario no vería las comandas impresas.

### 5.2 Solución correcta

```csharp
// ✅ CORRECTO
if (respuesta.ImpresionList != null && respuesta.ImpresionList.Count > 0)
{
    // Imprimir (servidor con flag OFF, cliente con servidor flag OFF)
    imprimirComandas(respuesta.ImpresionList);
}
else
{
    // No imprimir (servidor con flag ON delegó a PrinterServices)
    // El usuario verá éxito igualmente
}
```

**Ventaja**: El Front es "tonto". Solo confía en lo que el backend le envía. El backend (servidor) es quien decide si delegar o no.

---

## 6. Tabla de decisión completa

| Terminal | Modo | Flag Backend | Backend ejecuta | Retorna ImpresionList | Front imprime |
|---|---|---|---|---|---|
| Servidor | Servidor | OFF | Controller local | ✅ CON comandas | ✅ Localmente |
| Servidor | Servidor | ON | Controller local → PrinterServices | ❌ Vacía | ❌ No (delegó) |
| Cliente | Cliente | N/A | REST → Servidor (flag OFF) | ✅ CON comandas vía HTTP | ✅ Localmente |
| Cliente | Cliente | N/A | REST → Servidor (flag ON) | ❌ Vacía vía HTTP | ❌ No (servidor delegó) |

**Conclusión**: 
- El **servidor** decide si delegar a PrinterServices basándose en `esModoServidor() && USAR_PRINTER_SERVICE`
- El **cliente** SIEMPRE confía en lo que el servidor le envía (ImpresionList o vacía)
- El **Front** (servidor o cliente) SOLO verifica si `ImpresionList` tiene datos

---

## 7. Casos de uso

### 7.1 Restaurante con 1 servidor + 3 clientes

```
Servidor (caja principal):
  - QuipuNet.exe + QuipuNetX.dll
  - esModoServidor() = TRUE
  - USAR_PRINTER_SERVICE = TRUE
  - PrinterServices.exe corriendo
  - Imprime vía PrinterServices

Cliente 1 (tablet mesero):
  - QuipuNet.exe + QuipuNetX.dll
  - esModoServidor() = FALSE
  - Envía pedidos → REST → Servidor
  - Servidor retorna ImpresionList vacía
  - NO imprime (servidor delegó)

Cliente 2 (caja secundaria):
  - QuipuNet.exe + QuipuNetX.dll
  - esModoServidor() = FALSE
  - Envía ventas → REST → Servidor
  - Servidor retorna ImpresionList vacía
  - NO imprime (servidor delegó)

Cliente 3 (cocina):
  - QuipuNet.exe + QuipuNetX.dll
  - esModoServidor() = FALSE
  - Recibe pedidos → REST → Servidor
  - Servidor retorna ImpresionList vacía
  - NO imprime (servidor delegó)
```

**Todas las impresiones ocurren en el servidor vía PrinterServices.**

### 7.2 Restaurante con servidores independientes (sin clientes)

```
Terminal A:
  - QuipuNet.exe + QuipuNetX.dll
  - esModoServidor() = TRUE
  - USAR_PRINTER_SERVICE = FALSE
  - Imprime localmente (flujo antiguo)

Terminal B:
  - QuipuNet.exe + QuipuNetX.dll
  - esModoServidor() = TRUE
  - USAR_PRINTER_SERVICE = FALSE
  - Imprime localmente (flujo antiguo)
```

**Cada terminal imprime sus propias comandas sin PrinterServices.**

---

## 8. Resumen de responsabilidades

| Componente | Responsabilidad |
|---|---|
| **Router** | Decidir si ejecutar local (servidor) o REST (cliente) |
| **Controller (servidor)** | Procesar lógica de negocio + decidir si delegar a PrinterServices |
| **PrinterServiceClient** | Enviar comandas a PrinterServices vía HTTP |
| **PrinterServices.exe** | Ejecutar impresión física + notificar resultado vía gRPC |
| **Front (Presenter)** | Confiar en la Respuesta del backend, imprimir solo si ImpresionList tiene datos |

---

## 9. Referencias

- Documento de acoplamiento: `vibe_engeneering_acoplequipunet.md`
- Código relevante:
  - `QuipuNetX/controller/PedidoController.cs` — línea 615-640 (lógica flag)
  - `QuipuNet/MainApp/venta/Pedidos/ListaPedidosTemporalesPresenter.cs` — línea 93-117 (lógica Front)
  - `QuipuNetX/util/Util.cs` — método `esModoServidor()`
