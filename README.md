# MVP-01 — prueba vertical PAN

Slice local, datos exclusivamente sintéticos. .NET SDK **10.0.401**, ASP.NET Core 10 y **YARP 2.3.0**. No implementa todo el SRS ni está aprobado para producción.

## Ejecutar

Desde esta carpeta:

```powershell
dotnet restore ProyectoSeguridad.slnx
dotnet build ProyectoSeguridad.slnx --no-restore
dotnet test ProyectoSeguridad.slnx --no-build --no-restore
```

Para una demostración manual, en dos terminales (detener ambas con Ctrl+C):

```powershell
dotnet run --project TestApi -- --urls http://127.0.0.1:5101
dotnet run --project Gateway -- --urls http://127.0.0.1:5100
```

```powershell
curl.exe http://127.0.0.1:5100/protected/payment
```

El origen se configura con `--Upstream http://127.0.0.1:5101/` en Gateway; este slice solo acepta orígenes HTTP loopback. `/passthrough/payment` es **explícitamente no protegida**, demuestra el original sintético y no debe exponerse con datos reales. La API directa también devuelve el original.

## Contrato fijo

- Ruta YARP `protected-payment`: `/protected/{**rest}` elimina `/protected` al reenviar. Solo inspecciona respuestas `application/json` (media type insensible a mayúsculas, admite `charset=utf-8`); incluye errores HTTP JSON. Otros tipos pasan sin inspección.
- Modo **Mask**, selector exacto y sensible a mayúsculas `$.payment.cardNumber`. No busca PAN en otros campos ni dentro de arrays. Se procesan **todas** las propiedades seleccionadas duplicadas.
- Strings de dígitos ASCII con espacios/guiones: oculta todos salvo los últimos cuatro dígitos y conserva separadores. Con cuatro dígitos o menos, o formato incompatible: `[REDACTED]`. `null` permanece `null`; tipos sensibles no string/no null bloquean. No usa Luhn. Otros campos conservan su valor JSON, no necesariamente representación/espaciado.
- Máximo **1.048.576 bytes** de entrada real, inclusive; profundidad JSON máxima 64. JSON inválido, exceso, codificación/charset no admitido, respuesta parcial JSON o fallo de lectura: `502 {"code":"privacy_inspection_failed"}` sin cuerpo original ni excepción pública. No descomprime. La lectura se detiene como máximo tras el byte límite + 1, aunque no haya Content-Length.
- Un **response transform real de YARP** establece `SuppressResponseBody` antes de leer. Captura acotada, parseo y transformación terminan antes de escribir al cliente; no es middleware que amortigua un stream después de iniciar la respuesta.
- Se recalcula Content-Length y se eliminan ETag, Last-Modified, digests, firmas y metadatos incompatibles del cuerpo transformado. Se solicita `identity`, se eliminan condiciones de caché y rangos de la solicitud protegida y se marca `Cache-Control: no-store`. HEAD/204/304 no añaden cuerpo; no se implementa revalidación de caché.
- No hay HTTP body logging. Se deshabilitan logs de framework/YARP que podrían incluir URLs y queries. Solo se emiten dos eventos fijos de inspección/bloqueo, sin valores ni excepciones. La API sintética no emite logs.

## Evidencia automatizada

`Gateway.Tests` arranca **dos servidores Kestrel reales efímeros en 127.0.0.1**, con puertos asignados por el SO; no usa TestServer in-memory ni mocks del transporte. HttpClient atraviesa Gateway → YARP → TestApi. El header sintético del upstream y el contraste con su respuesta directa demuestran el salto. Cada prueba detiene y libera sus hosts.

Se verifica equivalencia semántica del documento salvo PAN, Unicode, duplicados, null, tipos incompatibles, errores JSON tardíos, Content-Length/ETag/digest, no inspección fuera de alcance, HEAD/204/304, errores HTTP, codificaciones, profundidad y cuerpos de tamaño límite − 1 / límite / límite + 1. Hay transferencias fragmentadas sin Content-Length y exceso declarado. Una prueba pausa el upstream **después de hacer flush de un fragmento sensible**: la solicitud cliente con `ResponseHeadersRead` sigue pendiente; tras entregar un final inválido solo recibe el error genérico. Se capturan logs reales y se comprueba ausencia de PAN, cuerpo y marcador privado en query.

## Límites y riesgos

Sin autenticación/autorización, TLS, aislamiento de red, políticas dinámicas, BD, UI, email/teléfono ni detección universal. Los headers/trailers y cuerpos no JSON no se inspeccionan. No protege canales directos ni logs/dumps del entorno. La máscara revela cuatro dígitos y **no anonimiza**.

El límite es por respuesta de entrada: el DOM y la salida reserializada consumen memoria adicional (incluidos escapes). No hay presupuesto global de concurrencia, pruebas de carga, métricas completas ni endurecimiento operativo. YARP usa un activity timeout de 15 s; cancelaciones del cliente se propagan. Las pruebas no certifican presión de memoria, timeouts, desconexiones o saturación. No activar sobre datos reales ni desplegar sin diseño y revisión adicionales.
