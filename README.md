# MVP-02.1 — política PAN configurable y cobertura mínima

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

## Contrato de configuración y arranque

`Gateway/appsettings.json` contiene una única sección `PanPolicies`, con el contrato versionado `CoverageContract` y una lista `Policies`:

```json
{
  "PanPolicies": {
    "CoverageContract": {
      "Version": "v1",
      "Required": [
        {
          "RouteId": "protected-payment",
          "Selector": "$.payment.cardNumber"
        }
      ]
    },
    "Policies": [
      {
        "PolicyId": "PAN",
        "Version": "v1",
        "RouteId": "protected-payment",
        "Mode": "Mask",
        "Selector": "$.payment.cardNumber",
        "MaxBodyBytes": 1048576
      }
    ]
  }
}
```

- Options nativo enlaza tipos y aplica `ValidateOnStart`. Se exige entre 1 y 32 políticas; sección ausente, lista vacía o configuración inválida impiden arrancar/servir tráfico. YARP también valida al compilar sus transforms durante `Build`, antes de abrir el listener. Campos desconocidos se rechazan.
- `PolicyId`, `Version` y `RouteId`: 1–64 caracteres ASCII, patrón `[A-Za-z0-9][A-Za-z0-9._-]{0,63}`; RouteId único con comparación ordinal y existente en el mismo conjunto de RouteConfig entregado a YARP (`protected-payment` o `passthrough` en esta demo). Una ruta inexistente impide Build/Start, nunca queda una política silenciosamente sin usar. `Mode` es obligatorio y acepta exclusivamente el texto exacto `Mask`; números, distinta capitalización, espacios y vacío se rechazan. IDs/versiones acotados evitan inyección en logs y cardinalidad ilimitada durante el proceso.
- Selector exacto de propiedades de objeto: `$.a.b`, de 1 a 16 segmentos, cada uno `[A-Za-z_][A-Za-z0-9_]{0,63}` (máximo total 1041 caracteres). Sin arrays, índices, wildcards, comillas, escape, descenso recursivo ni filtros. Comparación ordinal case-sensitive; se visitan todos los duplicados en cada segmento. Segmentos intermedios no objeto no coinciden.
- `CoverageContract` es obligatorio: `Version` usa el mismo formato seguro de identificadores y `Required` contiene entre 1 y 32 pares RouteId/Selector. Cada RouteId debe ser conocido y único dentro del contrato, y cada selector usa la gramática anterior. Cada par debe coincidir **exactamente (ordinal, case-sensitive)** con una política efectiva. Cambiar solo ruta o selector, omitir cobertura, duplicar requisitos o configurar un contrato inválido impide Build/Start. Es cobertura **mínima**: pueden existir políticas adicionales válidas, pero no sustituyen requisitos. La versión identifica el contrato; no debe coincidir necesariamente con la versión de cada política.
- El contrato detecta desviaciones de las políticas respecto a una cobertura declarada, no descubre dónde existe PAN. **Cambiar conjuntamente política y contrato es un cambio de seguridad que requiere revisión** (y versionado explícito del contrato). No protege frente a quien controla ambos archivos/configuración o sus sobrescrituras: ese control está dentro del límite de confianza. El valor distribuido exige `protected-payment` + `$.payment.cardNumber`; migrar configuraciones anteriores requiere añadir explícitamente el contrato antes de iniciar esta versión.
- `MaxBodyBytes`: entero obligatorio de 1 a **16.777.216** bytes inclusive. El techo operativo de 16 MiB acota el buffering por respuesta; no constituye un presupuesto global de memoria ni cambia el valor aprobado por defecto de 1 MiB.
- Catálogo inmutable por RouteId y selectores segmentados una sola vez al iniciar. **Sin BD ni reload dinámico de políticas**: `IOptions`, no monitor/snapshot; cambiar archivo/configuración requiere reiniciar. Las fuentes estándar de configuración .NET pueden sobrescribir valores antes del arranque, sujetos a la misma validación. Las rutas YARP de esta demo siguen definidas en código; cambiar RouteId asigna una política a una ruta, no crea rutas nuevas. Rutas sin política son passthrough.

## Comportamiento por defecto (preservado de MVP-01)

- Ruta YARP `protected-payment`: `/protected/{**rest}` elimina `/protected` al reenviar. Solo inspecciona respuestas `application/json` (media type insensible a mayúsculas, admite `charset=utf-8`); incluye errores HTTP JSON. Otros tipos pasan sin inspección.
- Modo **Mask**, selector exacto y sensible a mayúsculas `$.payment.cardNumber`. No busca PAN en otros campos ni dentro de arrays. Se procesan **todas** las propiedades seleccionadas duplicadas.
- Strings de dígitos ASCII con espacios/guiones: oculta todos salvo los últimos cuatro dígitos y conserva separadores. Con cuatro dígitos o menos, o formato incompatible: `[REDACTED]`. `null` permanece `null`; tipos sensibles no string/no null bloquean. No usa Luhn. Otros campos conservan su valor JSON, no necesariamente representación/espaciado. Sin coincidencia se conserva el cuerpo JSON original byte por byte.
- Máximo **1.048.576 bytes** de entrada real, inclusive; profundidad JSON máxima 64. JSON inválido, exceso, codificación/charset no admitido, respuesta parcial JSON o fallo de lectura: `502 {"code":"privacy_inspection_failed"}` sin cuerpo original ni excepción pública. No descomprime. La lectura se detiene como máximo tras el byte límite + 1, aunque no haya Content-Length.
- Un **response transform real de YARP** establece `SuppressResponseBody` antes de leer. Captura acotada, parseo y transformación terminan antes de escribir al cliente; no es middleware que amortigua un stream después de iniciar la respuesta.
- Se recalcula Content-Length y se eliminan ETag, Last-Modified, digests, firmas y metadatos incompatibles del cuerpo transformado. Se solicita `identity`, se eliminan condiciones de caché y rangos de la solicitud protegida y se marca `Cache-Control: no-store`. HEAD/204/304 no añaden cuerpo; no se implementa revalidación de caché.
- No hay HTTP body logging. Se deshabilitan logs de framework/YARP que podrían incluir URLs y queries. Solo se emiten dos tipos de evento de inspección/bloqueo, sin cuerpo/PAN ni excepciones; la inspección incluye PolicyId/Version validados. La API sintética no emite logs.

## Evidencia automatizada

Las pruebas de recorrido de `Gateway.Tests` arrancan **dos servidores Kestrel reales efímeros en 127.0.0.1**, con puertos asignados por el SO; no usan TestServer in-memory ni mocks del transporte. HttpClient atraviesa Gateway → YARP → TestApi. El header sintético del upstream y el contraste con su respuesta directa demuestran el salto. Cada prueba detiene y libera sus hosts.

Se verifica equivalencia semántica del documento salvo PAN, Unicode, duplicados, null, tipos incompatibles, errores JSON tardíos, Content-Length/ETag/digest, no inspección fuera de alcance, HEAD/204/304, errores HTTP, codificaciones, profundidad y cuerpos de tamaño límite − 1 / límite / límite + 1. Hay transferencias fragmentadas sin Content-Length y exceso declarado. Una prueba pausa el upstream **después de hacer flush de un fragmento sensible**: la solicitud cliente con `ResponseHeadersRead` sigue pendiente; tras entregar un final inválido solo recibe el error genérico. Se capturan logs reales y se comprueba ausencia de PAN, cuerpo y marcador privado en query.

También están cubiertos **206 JSON** (bloqueo sin cuerpo/metadatos originales) e **interrupción real del upstream** tras flush (sin headers/cuerpo temprano, solo error genérico). MVP-02 añade configuración in-memory explícita y determinista, cambio real de ruta/selector/límite por YARP, duplicados en selector profundo, ausencia de coincidencia sin reserialización, configuración inválida/ausente (incluidos RouteId duplicado y selector no admitido), enlace del appsettings distribuido e inmutabilidad ante reload/mutación de opciones.

## Límites y riesgos

Sin autenticación/autorización, TLS, aislamiento de red, políticas dinámicas, BD, UI, email/teléfono ni detección universal. Los headers/trailers y cuerpos no JSON no se inspeccionan. No protege canales directos ni logs/dumps del entorno. La máscara revela cuatro dígitos y **no anonimiza**.

El límite es por respuesta de entrada: el DOM y la salida reserializada consumen memoria adicional (incluidos escapes). No hay presupuesto global de concurrencia, pruebas de carga, métricas completas ni endurecimiento operativo. YARP usa un activity timeout de 15 s; cancelaciones del cliente se propagan. Las pruebas cubren la interrupción descrita, pero no certifican presión de memoria, timeouts, todos los escenarios de desconexión o saturación. No activar sobre datos reales ni desplegar sin diseño y revisión adicionales.
