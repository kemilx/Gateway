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

### Salud local RF13 (MVP-17)

En el listener loopback existente, `GET /health/live` devuelve siempre
`200 {"status":"alive"}` cuando el pipeline puede atenderlo. `GET /health/ready`
devuelve `200 {"status":"ready"}` solo con el catálogo PAN válido inicializado,
`ApplicationStarted` señalado y `ApplicationStopping` no señalado; en otro estado
devuelve `503 {"status":"not_ready"}`. Ambos usan JSON UTF-8
(`application/json; charset=utf-8`) y `Cache-Control: no-store`, sin detalles internos.
El catálogo se resuelve explícitamente durante Build: configuración PAN inválida
impide arrancar; no se captura para continuar sirviendo health.

Son endpoints locales, fuera de YARP y de sus permisos de admisión/inspección.
Ready no indica upstream disponible ni capacidad libre. Consultan los tokens del
host por petición, con prioridad a stopping, sin sondeos externos, estado propio,
listeners nuevos ni reload de políticas. Durante shutdown real el transporte puede
cerrarse: no se garantiza observar un 503 después del cierre del socket.

Las pruebas nuevas usan Kestrel real con seis peticiones mixtas YARP retenidas
antes de headers: ambos health siguen en 200, sin contactos upstream adicionales,
con admisión en 6 y presupuesto de inspección en cero. También cubren upstream
inaccesible, contrato exacto, ausencia de RouteModel, rechazo de PAN inválido y
transiciones de lifetime con un fake explícito (no prueba de transporte shutdown).
No se repitió sizing: MVP-16 corresponde al snapshot anterior; no demuestra una
medición exacta del binario con health. No se modifica esa evidencia histórica.

Compatibilidad: solo se añaden estas rutas GET locales. Sin migración, paquetes ni
configuración nueva; actualizar binario y reiniciar para incorporarlas. Se conservan
admisión 6, inspección 6/6 MiB/10 s, ActivityTimeout 15 s y deadline de headers
opt-in desactivado. No se realizó despliegue ni se autoriza producción.

### Deadline opcional hasta headers upstream (local)

`UpstreamHeadersDeadline:TimeoutSeconds` es un entero nullable **desactivado por
defecto** y ausente de la configuración distribuida. Si se proporciona, admite
1..120 segundos (rango técnico, no SLA aprobado). El binder rechaza campos
desconocidos y valores inválidos al iniciar. Se captura una instantánea al
construir el host; cambiar configuración después requiere reinicio.

Solo se aplica a GET/HEAD cuyo servidor confirme `CanHaveBody == false` mediante
`IHttpRequestBodyDetectionFeature`, sin upgrade ni extended CONNECT. Sin esa
feature se excluye la petición. Es independiente de la política PAN y cubre ambas
rutas; no utiliza headers públicos para marcar elegibilidad. No cubre uploads.

El intervalo empieza inmediatamente antes del envío upstream y termina al aceptar
sus headers, sin tocar versión, headers ni cuerpo de respuesta. Usa el pool y
ownership de la factory YARP 2.3 mediante `WrapHandler` y `HttpMessageInvoker`.
La expiración cancela solo el token enlazado de esa invocación y YARP la clasifica
como 504 si no prevalece cancelación cliente/YARP. No hay retries ni métricas de
inspección nuevas. El timer se retira antes de entregar headers aceptados: un
cuerpo lento sigue sujeto a los límites de actividad/inspección ya existentes
(15 s de actividad YARP y 10 s de inspección por defecto), no a este deadline.

No es un hard timeout: se espera el envío y los callbacks de cancelación ya
iniciados; código no cooperativo puede retener el permiso de admisión hasta
terminar. Una respuesta que pierde contra expiración/cancelación se dispone.
No se ha seleccionado duración operativa ni aprobado SLA/uploads. No cambia
defaults ni requiere migración; activar expresamente solo tras decidir la
duración para el entorno local y reiniciar con el nuevo binario. Solo se activa
en pruebas sintéticas; permanece desactivado en la configuración distribuida.
No se ha realizado despliegue.

### Listener local obligatorio

Gateway solo permite listeners IP loopback (`127.0.0.0/8` y `::1`), con el
transporte sockets estándar de Kestrel. Conserva puertos efímeros (`:0` sobre IP)
y el perfil `https://localhost:52734;http://localhost:52735` (HTTPS requiere un
certificado local válido). No se añade configuración TLS ni autenticación.

El guard verifica el **EndPoint efectivo antes de cada bind de transporte**, no
solo `Upstream` ni una lista de URLs que Kestrel puede eclipsar. Aplica a `--urls`,
`urls` de configuración, `ASPNETCORE_URLS`/`DOTNET_URLS`, `HTTP_PORTS`/`HTTPS_PORTS`
(incluidas variantes prefijadas), endpoints `Kestrel:Endpoints`, `Listen` en código
y precedencia `PreferHostingUrls`. Las expansiones wildcard de puertos se rechazan.
Kestrel 10 expande `localhost` y nombres bajo `.localhost` a IP loopback; el guard
valida esas IP resultantes y las admite. Otros hostnames cuyos bindings efectivos
sean wildcard se rechazan; el guard no realiza resolución DNS como excepción de confianza.
La misma comprobación se ejecuta en binds posteriores, incluidas recargas de
endpoints: no hay una ventana de apertura no-loopback antes de validar.

Wildcard, IP no loopback, IPv4-mapped IPv6 (incluso loopback), Unix sockets,
named pipes y handles heredados se rechazan como no soportados/no verificables.
También se rechazan transportes sockets/servidores alternativos y callbacks de
creación de sockets personalizados; no se habilita transporte multiplexado/HTTP3.
El error del guard es fijo, sin reproducir endpoints ni configuración. El ejecutable
devuelve código 1 y diagnóstico genérico ante fallo de inicio/ejecución, sin
imprimir excepciones (también para sintaxis inválida de configuración).

Límites: se valida **cada bind**, no todo el conjunto de endpoints de forma
atómica; un listener loopback válido puede abrirse antes de que falle otro, pero
ningún endpoint incompatible llega al bind del SO. Una recarga inválida nunca
abre el endpoint incompatible; la continuidad de los anteriores depende de Kestrel.
Las pruebas verifican el guard repetido y una recarga real de archivo: primero
un cambio loopback válido con petición HTTP positiva, después wildcard con señal
de rechazo del guard capturada exclusivamente en memoria de test. Esto no prueba
todos los escenarios de recarga ni garantiza continuidad de endpoints anteriores.
El código que configura o modifica el host dentro del proceso es de
confianza; esto no es un sandbox frente a código arbitrario, proxies externos,
port forwarding o cambios del runtime. No cambia ni protege el listener de TestApi.

Compatibilidad: configuraciones que antes exponían Gateway fuera de loopback ahora
fallan; sustituirlas por IP loopback/localhost antes de reiniciar con el binario
nuevo. No hay migraciones, paquetes nuevos ni despliegue realizado.

`Gateway/appsettings.json` contiene una única sección `PanPolicies`, con el contrato versionado `CoverageContract` y una lista `Policies`:

```json
{
  "PanPolicies": {
    "InspectionBudget": {
      "InspectionTimeoutSeconds": 10,
      "MaxConcurrentInspections": 6,
      "MaxReservedBodyBytes": 6291456
    },
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
- No hay HTTP body logging. Se deshabilitan logs de framework/YARP que podrían incluir URLs y queries. Solo se emiten eventos de inspección, fallo de inspección (502) y capacidad excedida (503), todos sin payload/PAN, URL sensible ni excepciones; la inspección incluye PolicyId/Version validados. La API sintética no emite logs.

## Evidencia automatizada

Las pruebas de recorrido de `Gateway.Tests` arrancan **dos servidores Kestrel reales efímeros en 127.0.0.1**, con puertos asignados por el SO; no usan TestServer in-memory ni mocks del transporte. HttpClient atraviesa Gateway → YARP → TestApi. El header sintético del upstream y el contraste con su respuesta directa demuestran el salto. Cada prueba detiene y libera sus hosts.

Se verifica equivalencia semántica del documento salvo PAN, Unicode, duplicados, null, tipos incompatibles, errores JSON tardíos, Content-Length/ETag/digest, no inspección fuera de alcance, HEAD/204/304, errores HTTP, codificaciones, profundidad y cuerpos de tamaño límite − 1 / límite / límite + 1. Hay transferencias fragmentadas sin Content-Length y exceso declarado. Una prueba pausa el upstream **después de hacer flush de un fragmento sensible**: la solicitud cliente con `ResponseHeadersRead` sigue pendiente; tras entregar un final inválido solo recibe el error genérico. Se capturan logs reales y se comprueba ausencia de PAN, cuerpo y marcador privado en query.

También están cubiertos **206 JSON** (bloqueo sin cuerpo/metadatos originales) e **interrupción real del upstream** tras flush (sin headers/cuerpo temprano, solo error genérico). MVP-02 añade configuración in-memory explícita y determinista, cambio real de ruta/selector/límite por YARP, duplicados en selector profundo, ausencia de coincidencia sin reserialización, configuración inválida/ausente (incluidos RouteId duplicado y selector no admitido), enlace del appsettings distribuido e inmutabilidad ante reload/mutación de opciones.

## Límites y riesgos

Sin autenticación/autorización, TLS, aislamiento de red, políticas dinámicas, BD, UI, email/teléfono ni detección universal. Los headers/trailers y cuerpos no JSON no se inspeccionan. No protege canales directos ni logs/dumps del entorno. La máscara revela cuatro dígitos y **no anonimiza**.

El DOM y la salida reserializada consumen memoria adicional (incluidos escapes). El
harness local MVP-05 aporta mediciones sintéticas reproducibles, pero no dimensiona
producción ni sustituye pruebas en infraestructura representativa. Faltan métricas
operativas exportadas y endurecimiento. YARP usa un activity timeout de 15 s;
cancelaciones del cliente se propagan. Las pruebas cubren interrupción, cancelación y
saturación controlada del presupuesto, pero no certifican timeouts ni todos los
escenarios de desconexión. No activar sobre datos reales ni desplegar sin diseño y
revisión adicionales.

## Presupuesto global de inspección

### Admisión global de proxy (MVP-11)

`ProxyAdmission:MaxConcurrentProxyRequests` tiene default CLR y distribuido **6**,
entero en **1..1024**, validado antes de servir tráfico. La sección es opcional;
campos desconocidos o valores inválidos impiden arrancar. Se congela al construir
la instancia: los cambios requieren reinicio.

Un singleton por instancia admite todas las rutas YARP, incluido passthrough,
después del routing y antes del forwarding. No hay cola: la adquisición atómica
inmediata rechaza saturación con `503 {"code":"gateway_capacity_exceeded"}`,
JSON UTF-8 y `Cache-Control: no-store`, sin contactar al upstream. Los endpoints
locales/no YARP quedan fuera. Se conserva `MapReverseProxy()` estándar.

El permiso cubre el pipeline esperado completo, incluidas escritura y limpieza:
se libera una vez al retornar o lanzar, nunca por timer, RequestAborted ni
OnCompleted. Un trabajo que no termina puede retenerlo indefinidamente. No es
un límite distribuido ni un límite de memoria. Es independiente del presupuesto
PAN (6 slots/6 MiB/10 s), que no cambia; ActivityTimeout sigue en 15 s, sin nuevos
timeouts. No se añaden métricas ni tags al contrato de telemetría de inspección.
Los perfiles HTTP/aislados históricos del harness fijan explícitamente admisión 8
para no truncar sus pruebas de inspección N7/N8; no son el default operativo.

Compatibilidad: ahora passthrough y la espera de headers también consumen admisión.
No hay migraciones ni paquetes nuevos. Actualizar binario/configuración juntos y
reiniciar; para rollback retirar la sección nueva. No se realizó despliegue ni
se autoriza uso con datos reales.

`PanPolicies.InspectionBudget` es obligatorio. Sus valores predeterminados son
`MaxConcurrentInspections: 6` (rango 1..1024) y `MaxReservedBodyBytes: 6291456`
(6 MiB; rango 1..1073741824, 1 GiB). Cada `Policy.MaxBodyBytes` debe caber en
el presupuesto. Los campos desconocidos impiden iniciar.

El presupuesto es compartido por todas las rutas protegidas de **una instancia**,
sin cola: una respuesta JSON elegible reserva atómicamente un slot y su límite
completo `MaxBodyBytes`, antes de leer el cuerpo upstream, hasta terminar la escritura
downstream. Sin capacidad devuelve inmediatamente 503
`{"code":"privacy_capacity_exceeded"}`, JSON UTF-8 y `no-store`, sin metadatos upstream.
HEAD, 204, 304, non-JSON y passthrough no reservan capacidad. La reserva se libera
también ante 502, cancelación o excepción. La configuración se fija al iniciar.

`MaxReservedBodyBytes` reserva **límites de entrada**, no equivale al heap real:
DOM, buffers, salida y transporte necesitan memoria adicional. No es un límite
distribuido ni impide que upstream haya producido datos antes del rechazo.

## Contrato técnico de telemetría (MVP-04)

La implementación usa `System.Diagnostics.Metrics` con el `Meter`
`Gateway.Privacy`, versión `1.0.0`. Este MVP no configura exporter ni backend
(incluidos Prometheus u OpenTelemetry) y no está desplegado.

| Instrumento | Tipo | Unidad |
| --- | --- | --- |
| `gateway.privacy.inspection.outcomes` | `Counter<long>` | `{inspection}` |
| `gateway.privacy.inspection.duration` | `Histogram<double>` | `ms` |
| `gateway.privacy.inspection.active` | `ObservableGauge<int>` | `{inspection}` |
| `gateway.privacy.inspection.reserved_body_bytes` | `ObservableGauge<long>` | `By` |

- Los outcomes son únicamente `inspected`, `blocked`, `capacity_exceeded`,
  `canceled`, `write_error`, `faulted` y `timed_out`. Cada operación JSON protegida que llega
  al control de capacidad registra un solo outcome terminal. La duración se
  registra solo si se adquirió el lease; usa el timestamp conservador previo al
  intento de adquisición e incluye escritura downstream y cleanup (véase MVP-09).
- Counter e histograma usan exclusivamente los tags acotados
  `gateway.route.id`, `gateway.policy.id`, `gateway.policy.version` y
  `gateway.privacy.outcome`; los gauges no añaden tags. Nunca se incluye cuerpo,
  PAN, selector, URL/query ni excepción.
- Los gauges son valores por instancia obtenidos de `InspectionBudget.Snapshot`.
  HEAD, 204, 304, respuestas no JSON y rutas sin política no emiten outcomes.

Sigue siendo un MVP local con datos exclusivamente sintéticos y no está
aprobado para producción.

### Aislamiento de consumidores de métricas (MVP-12)

La publicación del contador y del histograma es best effort e independiente:
una excepción ordinaria de un consumidor síncrono no cambia la respuesta HTTP,
la cancelación ni el error original, y no impide intentar el otro instrumento.
Puede perderse parte de la telemetría; no hay atomicidad entre HTTP y métricas.
Se conservan los nombres, tags, outcomes y la liberación del presupuesto.
Los errores de validación propios y fallos graves como `OutOfMemoryException`
no se ocultan. No se registran excepciones del consumidor ni se añaden tareas,
colas o publicaciones recursivas.

Esto **no aísla callbacks bloqueantes**: los consumidores deben retornar rápido.
Tampoco garantiza que otros listeners reciban una medición si uno de ellos lanza
dentro de la misma publicación. Los gauges observables conservan su contrato.

## Carga y memoria local (MVP-05)

El nuevo modo separado `compare` mide passthrough frente a PAN/Mask con procesos
aislados, p50/p95/p99, CPU, asignaciones y memoria muestreada. Su primera
[comparación local MVP-18](evidence/mvp18/2026-10-20-local-summary.md) validó
1024 respuestas en dos parejas AB/BA y mostró un coste importante para el payload
sintético denso de 1 MiB. Es observacional: no acredita SLA ni sizing y no cambia
límites operativos. Los comandos y esquemas históricos permanecen separados.

Revalidación posterior a los cambios locales de resiliencia:
[MVP-16, 19 de octubre de 2026](evidence/mvp16/2026-10-19-local-summary.md).
Las 40 ejecuciones del perfil sintético N6 cumplen el umbral de memoria con
32,30% de headroom mínimo observado. El perfil usa admisión 8 y presupuesto
8 MiB, no todos los defaults operativos; no es sizing de producción ni autoriza
subir límites. Se conserva intacta la evidencia histórica.

`Gateway.LoadHarness` ejecuta dos mediciones separadas sin paquetes nuevos ni
cambios en los límites predeterminados. Compilar y consultar opciones:

```powershell
dotnet build Gateway.LoadHarness/Gateway.LoadHarness.csproj -c Release
dotnet Gateway.LoadHarness/bin/Release/net10.0/Gateway.LoadHarness.dll --help
```

Ejecutar ambos modos y guardar resultados locales ignorados por Git:

```powershell
dotnet Gateway.LoadHarness/bin/Release/net10.0/Gateway.LoadHarness.dll all `
  --output artifacts/mvp05/run --requests-per-concurrency 16
```

- `memory` usa workers nuevos para concurrencias 1/2/4/8, un JSON sintético
  `token-dense-v1` de exactamente 1 MiB, un checkpoint interno antes de abandonar
  el DOM y una escritura downstream controlada. Registra heap/GC, working set,
  private bytes y reserva lógica; con ocho inspecciones comprueba además que la
  novena devuelve 503 sin leer el origen y que toda capacidad se libera.
- `http` recorre Cliente → YARP → API sintética real en loopback y registra
  throughput y latencias closed-loop. API, gateway y cliente comparten proceso, de
  modo que su memoria es observacional y el muestreo puede omitir picos.

Los resultados se clasifican `MEASURED_NOT_SIZED`: sin un límite de memoria por
instancia y headroom acordado no demuestran que 8 inspecciones/8 MiB sean límites
universalmente conservadores. Una GC forzada solo se usa en `memory`; que working
set o private bytes no vuelvan al baseline no prueba por sí solo una fuga. Los
reportes no incluyen payload, PAN, URL, hostname, usuario, rutas personales ni
excepciones. Detalles en [`Gateway.LoadHarness/README.md`](Gateway.LoadHarness/README.md).

### Sizing local aislado (MVP-06)

El objetivo seleccionado es 512 MiB por proceso gateway con 30% de headroom:
Private Bytes debe permanecer en o por debajo de 375.809.638 bytes. `isolated`
separa API, gateway y cliente en procesos distintos, crea un gateway nuevo para
cada concurrencia 1/2/4/8 y ejecuta tres repeticiones por defecto:

```powershell
dotnet Gateway.LoadHarness/bin/Release/net10.0/Gateway.LoadHarness.dll isolated `
  --output artifacts/mvp05/mvp06-local
```

`SIZED_LOCAL_CANDIDATE` significa únicamente que todas las repeticiones sintéticas
locales cumplieron ese umbral observacional. No es un límite de contenedor, no
autoriza producción y no cambia los defaults. El muestreo de 10 ms puede omitir
transitorios; loopback omite TLS, autenticación, red remota y exporters.

MVP-07 permite evaluar de forma explícita un candidato entre 4 y 8 inspecciones
sin modificar `Gateway/appsettings.json`. Por ejemplo, para probar seis
inspecciones con cinco repeticiones:

```powershell
dotnet Gateway.LoadHarness/bin/Release/net10.0/Gateway.LoadHarness.dll isolated `
  --output artifacts/mvp05/mvp07-candidate6 --repetitions 5 `
  --max-concurrent-inspections 6
```

El calendario resultante es 1/2/4/6; omitir la opción conserva 1/2/4/8. Incluso
si el candidato pasa localmente, cambiar el default requiere una decisión separada,
reversible y nueva evidencia bajo un límite real de memoria.

En Windows de 64 bits, MVP-08 añade una validación opt-in con Job Object y límite
real de commit por proceso. Solo los gateways sintéticos entran al Job; API y
cliente quedan fuera:

```powershell
dotnet Gateway.LoadHarness/bin/Release/net10.0/Gateway.LoadHarness.dll hard-limit `
  --output artifacts/mvp05/mvp08-local --max-concurrent-inspections 6
```

El gateway se crea suspendido, se asigna a un Job de 512 MiB y se reanuda únicamente
tras verificar límite, flags y pertenencia. `HARD_LIMIT_LOCAL_CANDIDATE` exige además
que Private Bytes y el pico de commit permanezcan bajo 375.809.638 bytes. Sigue
siendo evidencia local: el Job limita commit por proceso, no equivale a un límite
de contenedor ni autoriza por sí sola cambiar defaults o desplegar.

MVP-08.1 adopta de forma reversible N6/6291456 (6 MiB) como default local a
partir de `HARD_LIMIT_LOCAL_CANDIDATE`; esta decisión no autoriza producción.
Las secciones MVP-05/06/07/08 anteriores conservan la historia de medición: su
evidencia, por sí sola, no cambió los defaults.
# MVP-09: deadline cooperativo de inspección

`PanPolicies:InspectionBudget:InspectionTimeoutSeconds` es un entero de **1 a 120**,
con default **10** tanto CLR como distribuido. Se congela con el presupuesto al
startup: cambiar configuración exige reiniciar. Se inyecta `TimeProvider` BCL
(`TimeProvider.System` en producción); el tiempo transcurrido es monotónico.

El timestamp se captura inmediatamente **antes** de intentar adquirir el lease:
es una aproximación conservadora que incluye desplanificación/espera del lock de
adquisición, no una marca atómica exacta del lease. No se renueva después ni se
invoca el reloj inyectable dentro del lock del presupuesto. El deadline incluye lectura, transformación,
checkpoint diagnóstico, escritura y cleanup. No cubre la espera por headers
upstream, respuestas excluidas ni el 503 de capacidad sin lease. Al vencer se
cancela el token ligado y se solicita `HttpContext.Abort()`: **no hay 504 ni
respuesta de reemplazo**, tampoco si `HasStarted` es false. Errores inducidos por
el aborto no producen un fallback 502. La cancelación del cliente anterior al
aborto propio conserva `canceled`; el aborto propio produce `timed_out` (nuevo
valor al final del enum y nuevo label del instrumento existente). Se registra un
solo outcome y una duración, al terminar el trabajo, sin tags adicionales.

Todos los IO iniciados se esperan directamente: el timer no libera capacidad ni
completa métricas. Un Stream no cooperativo puede retener el slot indefinidamente;
abortar HTTP no garantiza su terminación. Parser, UTF-8 y copias síncronas no son
preemptibles: se comprueba cancelación/deadline alrededor de esas operaciones y
durante traversal. Si el timeout ocurre durante escritura puede haber una
respuesta enmascarada parcial; nunca se reenvía el cuerpo original protegido.

La transición de cancelación protege solo estado bajo lock. `CancelAsync` solicita
cancelación sin esperar los callbacks y se solicita `Abort` fuera de ese lock,
sin depender de que esos callbacks retornen. El cleanup asíncrono espera timer,
registros, callbacks de cancelación (observando sus fallos) y el retorno de Abort
antes de disponer el token, emitir métricas o liberar el lease; la duración incluye
ese cleanup. Se aplica también a la cancelación del cliente. Una implementación
arbitraria de `Abort` o sus callbacks de `RequestAborted` puede bloquear o fallar:
se garantiza la solicitud, no su retorno ni la terminación física del transporte.
Un callback puede consultar el estado o iniciar `DisposeAsync`, pero esperar
sincrónicamente su finalización desde el mismo callback crea una dependencia
circular inevitable: el cleanup debe esperar que dicho callback termine.

Despliegue/migración: no hay schema ni dependencias nuevas. Actualizar primero
consumidores de métricas que enumeren outcomes para aceptar `timed_out`; desplegar
binario/configuración juntos y reiniciar. La clave puede omitirse y toma 10 s.
Para rollback al commit base `392149839b6fa42c929e5447e306acaaa80065f0`, restaurar
también la configuración anterior (el binder rechaza claves desconocidas),
reiniciar y conservar compatibilidad histórica del label en dashboards. No se
ha ejecutado despliegue ni rollback. El perfil sintético de memoria N8 conserva
8 slots/8 MiB y fija 120 s para aislar sus barreras/GC del default operativo;
sus schemas y evidencia histórica no cambian.

Las pruebas lógicas de deadline usan `TimeProvider`/`ITimer` manuales y señales,
sin sleeps. `Kestrel_one_second_read_stall_aborts_transport_without_response_body`
es un smoke local separado por trait `Category=TransportSmoke`: usa 1 s real y
solo comprueba aborto/transporte, no un umbral de latencia estrecho.
