# Plan: Mejoras al Instalador de PrinterServices

## Contexto

El instalador actual (`PrinterServiceInstaller.cs`) tiene dos problemas:

1. **Puerto 8090 — "Acceso denegado"**: `HttpApiServer.cs` usa `HttpListener` (http.sys) y hace bind a IPs como `192.168.x.x` y `127.0.0.1` que requieren URL ACL. Sin la reserva, Windows bloquea el bind con "Acceso denegado" en cualquier usuario no-admin.

2. **Carpeta no se limpia correctamente**: El Paso 4 borra archivos uno por uno ignorando fallos, lo que deja archivos de versiones anteriores mezclados con los nuevos. Esto causa instalaciones corruptas.

**Nota importante sobre la carpeta**: El servicio Windows (SCM) registra la ruta absoluta del `.exe` al instalarse con Topshelf. Si el servicio se reinicia (auto-recovery, reboot), el SCM usa esa ruta. Por eso la carpeta debe quedar limpia pero en la misma ubicación.

---

## Archivo a modificar

- `front/QuipuNet/Services/PrinterServices/PrinterServiceInstaller.cs`

---

## Cambio 1: Eliminar carpeta completa antes de extraer (Paso 4)

### Ubicación
Líneas ~389-414 de `PrinterServiceInstaller.cs` — bloque `if (Directory.Exists(extractPath))`

### Qué hacer
Reemplazar el borrado archivo-por-archivo por `Directory.Delete(extractPath, true)` con retry.

En este punto del flujo el servicio ya fue detenido y desinstalado (Paso 3), así que los archivos deberían estar libres. Pero el SCM puede tardar ~1-2s en liberar handles, por lo que se necesita retry.

### Lógica

```
- Intentar Directory.Delete(extractPath, true) hasta 3 veces
- Entre cada intento, esperar 2 segundos
- Si después de 3 intentos falla, loguear advertencia y continuar
  (la extracción sobreescribirá lo que pueda via temp folder + File.Move)
- Después del delete exitoso, crear el directorio limpio con Directory.CreateDirectory()
```

### Por qué retry en vez de forzar
El SCM libera handles de forma asíncrona. Un retry con espera corta es más robusto que un solo intento. Si aún falla (antivirus bloqueando, etc.), el fallback es continuar con la extracción sobre la carpeta existente — mismo comportamiento actual.

---

## Cambio 2: Agregar reserva URL ACL con netsh (nuevo paso)

### Ubicación
Entre la extracción del ZIP (Paso 4 actual) y la instalación del servicio (Paso 5 actual).

### Qué hacer
Ejecutar `netsh http add urlacl url=http://+:{puerto}/ user=Everyone` para reservar el puerto en http.sys.

### Lógica

```
1. Leer el puerto del .exe.config extraído (key "HttpPort", default "8090")
2. Ejecutar: netsh http add urlacl url=http://+:{puerto}/ user=Everyone
3. Si el comando retorna éxito (exit code 0) → loguear OK
4. Si retorna error con "ya existe" (URL reservation already exists) → loguear y continuar
5. Si retorna otro error → loguear advertencia pero NO abortar la instalación
   (el servicio podría funcionar si solo escucha en localhost)
```

### Lectura del puerto desde config

```csharp
// Leer puerto del .exe.config extraído
string httpPort = "8090"; // default
string exeConfigPath = Path.Combine(extractPath, EXE_NAME + ".config");
if (File.Exists(exeConfigPath))
{
    // Parsear appSettings/add[@key='HttpPort'] del XML
    // Si no se encuentra, usar default 8090
}
```

### Por qué `http://+:{port}/` y no IPs individuales
- `http://+:port/` cubre TODAS las interfaces IPv4 e IPv6 con una sola reserva
- Es lo que `HttpListener` necesita cuando agrega prefijos con IPs específicas
- Si en el futuro cambian las IPs de la máquina, la reserva sigue válida
- `user=Everyone` permite que el servicio corra bajo cualquier cuenta (LocalSystem, NetworkService, etc.)

### Por qué no abortar si falla
El netsh puede fallar si el usuario ya hizo la reserva manualmente o si hay un conflicto. PrinterServices tiene fallback a localhost en `HttpApiServer.cs` (línea 56-57). Mejor loguear la advertencia y dejar que el servicio intente arrancar.

---

## Nueva secuencia de pasos (renumeración)

| Paso | Descripción | Cambio |
|------|-------------|--------|
| 1/8 | Validar admin + URL | Sin cambios |
| 2/8 | Descargar ZIP a %TEMP% | Sin cambios |
| 3/8 | Stop + Uninstall servicio anterior | Sin cambios |
| 4/8 | **Eliminar carpeta PrinterServices completa** | **MODIFICADO** — `Directory.Delete` con retry |
| 5/8 | Extraer ZIP a carpeta limpia | Se simplifica (ya no necesita borrar archivos individuales) |
| 6/8 | **Reservar URL ACL (netsh)** | **NUEVO** |
| 7/8 | Instalar servicio (Topshelf CLI) | Renumerado (era 5/7) |
| 8/8 | Iniciar + Verificar servicio | Renumerado (fusión de 6/7 y 7/7 anteriores) |

---

## Mensajes de progreso para el UI

```
Paso 4/8: Eliminando instalación anterior...
  Eliminando carpeta: C:\...\PrinterServices
  Carpeta eliminada correctamente.
  (o) Intento 1/3 falló: {error}. Reintentando en 2s...
  (o) ADVERTENCIA: No se pudo eliminar la carpeta después de 3 intentos. Continuando...

Paso 6/8: Configurando permisos de red (URL ACL)...
  Reservando puerto HTTP {puerto} en http.sys...
  netsh: {salida del comando}
  Reserva de URL completada correctamente.
  (o) Reserva ya existente, continuando.
  (o) ADVERTENCIA: No se pudo reservar URL. El servicio podría requerir permisos elevados.
```

---

## Consideraciones

- **Ya tenemos admin**: Validado en Paso 1. Tanto `Directory.Delete` como `netsh` funcionan con admin.
- **Idempotente**: Si `netsh add urlacl` ya existe, falla con mensaje conocido — se ignora.
- **Un solo puerto**: Solo el puerto HTTP (8090) usa http.sys. gRPC (50051) y UDP discovery (9999) usan sockets raw y no necesitan URL ACL.
- **Config generada**: Si el ZIP no trae `.exe.config`, el instalador lo genera (líneas 496-506). El paso de netsh lee el config DESPUÉS de la extracción, así que siempre tendrá el archivo disponible.
