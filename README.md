# MVP-47 — Copia escalar JSON optimizada y regresiones de privacidad

Slice local, datos exclusivamente sintéticos. .NET SDK **10.0.401**, ASP.NET Core 10 y **YARP 2.3.0**. No implementa todo el SRS ni está aprobado para producción.

## Copia escalar verificada (MVP-47)

La [optimización de copia escalar JSON](docs/MVP47-scalar-copy-performance.md)
evita materializar nombres de propiedades escalares, manteniendo recursión de
contenedores y checkpoints. Sin cambios de políticas, máscaras o paquetes.
Unicode, duplicados, sustitutos aislados, cancelación y telemetría tienen 41
regresiones nuevas: **1659/1659 PASS**, cero omitidos, build limpio y revisión de
seguridad favorable exclusivamente local.

Con el mismo perfil, el coste adicional de asignaciones bajó de **6.73 a 5.10
MiB/req (−24.27%)** frente a MVP-46; −37.84% observado desde MVP-44. Son campañas
separadas, no una mejora universal. El delta p95 observado pasó de +80.77 a +65.24
ms, pero CPU/throughput no mejoraron claramente y hubo un pico mayor de RAM
residente. El informe detalla corpus, trazabilidad, granularidad cooperativa de
cancelación y límites: **no hay aceptación de rendimiento ni de producción**.

## Optimización de captura anterior (MVP-46)

La [captura preasignada diferida](docs/MVP46-deferred-capture-performance.md)
usa una longitud declarada acotada solo tras el primer read positivo, con lease
adquirido y comprobación de bytes reales. La metadata upstream nunca delimita la
lectura ni sustituye la validación completa. Sin pooling, preasignación de salida,
cambios de políticas o paquetes. Una longitud falsa puede anticipar memoria hasta
el límite del cuerpo; la reserva nominal no es un techo de RSS.

Build Release limpio, **1618/1618 PASS**, cero omitidos, 39 casos nuevos y revisión
de seguridad favorable solo local. Tres pares de carga validaron 3072 cuerpos
exactos y cuatro workers de memoria comprobaron liberación/saturación. Con el
mismo perfil de 1 MiB, el delta de asignaciones Mask menos baseline bajó de
**8.20 a 6.73 MiB/req** (−17.91%, observación entre campañas separadas).
**No se demostró una mejora general de latencia:** el delta p95 empeoró de +66.72
a +80.77 ms. Rendimiento y capacidad de negocio siguen sin aceptar; el informe
incluye reproducción, hashes y límites.

## Contrato vigente de protección (MVP-44)

El [contrato de políticas, PAN y caché](docs/MVP44-policy-and-cache-contract.md)
añade metadata validada `Severity` (Low/Medium/High/Critical, default High),
`FailureBehavior=FailClosed` y `NonInspectableBehavior=ExcludeNonJson`. No hay
fail-open ni selección de permisos por severidad. Los valores ausentes conservan
defaults compatibles; valores explícitos inválidos se rechazan. La metadata se
congela en cada snapshot y un cambio efectivo exige nueva versión. No cambia el
DTO del dashboard ni los schemas de eventos.

La clasificación PAN interna distingue NotCandidate, Candidate y HighConfidence.
Solo HighConfidence (formato vigente + Luhn) activa la detección secundaria:
Candidate no añade nuevas máscaras, eventos ni valores a telemetría. Las reglas
explícitas conservan su prioridad incluso con Luhn negativo.

**Cambio intencional:** un 304 upstream en ruta protegida y método distinto de HEAD
ya no se conserva: se reemplaza por 502 `privacy_inspection_failed`, sin body ni
headers upstream, con no-store y sin reservar presupuesto ni leer el origen.
HEAD y 204 siguen sin cuerpo; passthrough continúa expresamente no protegido.
Encoding/charset no soportados y 206 se rechazan antes de permitir la exclusión
non-JSON. Esto sustituye cualquier descripción histórica de 304 protegido como
exclusión normal; no añade body a una respuesta que conserve el estado 304.

La [campaña reproducible](docs/MVP44-performance-campaign.md) utiliza el
[perfil local fijado antes de medir](docs/MVP44-local-performance-profile.json).
Sus gates comprueban integridad de medición y liberación, no presupuestos de
negocio: los umbrales siguen `PENDING_APPROVAL`, sin aceptación de rendimiento,
capacidad productiva ni autorización de despliegue.

Validación MVP-44: build Release limpio (**0 warnings / 0 errors**), suite completa
**1579/1579 PASS**, cero omitidos, y revisión de seguridad favorable únicamente
local sobre el TARGET comprobado. La campaña completó tres pares y 3072 cuerpos
exactos, más cuatro workers de memoria con presupuesto liberado. La inspección de
1 MiB mostró coste alto (mediana pareada p95 +66.72 ms y throughput −88.97%):
los gates de integridad pasaron, **el rendimiento de negocio no está aceptado**.
El informe detalla límites, fuentes/binarios y reproducción.

## Estado frente al SRS (MVP-43, actualizado en MVP-47)

La [matriz de aceptación y brechas](docs/MVP43-acceptance-matrix.md) relaciona los
13 requisitos funcionales, 9 no funcionales y 12 criterios de aceptación del
[SRS v0.1](Documentos%20SRS/SRS.md) con fuentes, pruebas y trabajo pendiente.
La cobertura técnica local se distingue de la aprobación del producto y de la
preparación de un entorno operativo. Los criterios de aceptación reutilizan
requisitos: no se deben sumar las 34 filas como si fueran tareas independientes.

La estimación previa de aproximadamente 70% era orientativa, **no una medición
del proyecto**. La matriz usa estados y conteos por familia, sin asignar pesos
arbitrarios ni equiparar el número de pruebas o de MVP con avance porcentual.

| Familia | Verificado localmente | Parcial |
| --- | ---: | ---: |
| Requisitos funcionales | 12/13 | 1/13 |
| Requisitos no funcionales | 4/9 | 5/9 |
| Criterios de aceptación (evidencia técnica, no aprobación formal) | 10/12 | 2/12 |

MVP-43 fue una revisión documental con evidencia anterior de 1474/1474 PASS.
MVP-44 modifica el motor y la sustituye por una ejecución nueva de 1579/1579 PASS.
MVP-46 sustituye esa evidencia funcional para el motor vigente por **1618/1618 PASS**;
los resultados de las campañas anteriores permanecen históricos, no se inventa
una ejecución posterior sobre esos binarios.
MVP-47 vuelve a ejecutar la suite del motor modificado: **1659/1659 PASS**. Las
evidencias funcionales anteriores quedan históricas para sus respectivos TARGET.
Los conteos describen cobertura técnica local, no aprobación formal del SRS,
presupuestos de rendimiento ni autorización de piloto o producción.

Los siguientes avances recomendados son revisar las decisiones de contrato
implementadas, aislar el coste restante de parseo/recorrido y definir/aprobar carga, entorno
y presupuestos antes de una campaña de aceptación. Telemetría, privacidad y
operación conservan brechas explícitas en la matriz. Panel administrativo, SQL y
distribución multiinstancia son evolución fuera del MVP, no bloqueos añadidos
para cerrarlo.

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

### Dashboard local sintético (MVP-39)

En Windows, con Node >=20 y npm/npx disponibles, abrir una terminal desde la raíz:

```powershell
powershell -NoProfile -File scripts/observability/Start-LocalObservability.ps1
```

El primer inicio descarga `@microsoft/aspire-cli@13.6.4` y su dashboard; no instala
globalmente ni usa Docker. npm usa configuración de usuario y global vacías propias,
registro oficial y scripts de instalación desactivados; no modifica las opciones npm
del usuario. Abrir el enlace de login que muestra la terminal sin
copiarlo a logs o evidencia: contiene un BrowserToken efímero. La API de consulta
exige autenticación; el receptor OTLP no autentica y escucha solo en loopback. No
permitir que otros usuarios o procesos no confiables accedan al equipo durante la
sesión. Si 18888, 4317 o 4318 está ocupado, liberar el puerto por medios propios;
el launcher no termina procesos ajenos. No usar con datos reales ni en producción.

En otras dos terminales ejecutar los comandos opt-in de MVP-38 de abajo y solicitar
`curl.exe http://127.0.0.1:5100/protected/payment`. En **Structured logs**, buscar
un evento del Gateway, copiar su atributo `CorrelationId` y buscar ese mismo valor
para localizar el evento de TestApi. Es correlación técnica, no una traza W3C.
En **Metrics**, seleccionar el recurso Gateway y el meter `Gateway.Privacy`; los
nueve instrumentos disponibles dependen de que se hayan producido sus eventos y
del intervalo de exportación de cinco segundos. Passthrough no genera eventos de
inspección Gateway y devuelve el original sintético, no una respuesta protegida.

Detener hosts y dashboard con Ctrl+C en sus respectivas terminales. Comprobar después
`Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object LocalPort -in 18888,4317,4318`
y, si queda algún listener, identificar su propietario y detener solo procesos cuya
pertenencia a esta sesión se haya comprobado; no matar procesos por nombre. El cierre
de la terminal no garantiza por sí solo que todos los descendientes hayan terminado.
`None` usa una base SQLite temporal, no garantiza ausencia de escritura en disco ni
borrado seguro. La sesión nueva no pretende reabrir la anterior. Incluso tras Ctrl+C
normal pueden quedar `dashboard.db` y carpetas temporales; un cierre forzado también
puede dejar archivos WAL.

Para una limpieza limitada, inventariar antes y después de la sesión las carpetas de
`$env:LOCALAPPDATA\Temp\opencode\MVP39-local-observability-runtime` y registrar el
árbol de procesos propio. Solo tras verificar su salida y los puertos libres, revisar
cada carpeta nueva cuya propiedad esté probada. Una carpeta `session-<guid>` que
contenga exclusivamente `user.npmrc` y `global.npmrc` vacíos puede limpiarse eliminando
esos dos archivos y después la carpeta vacía, sin borrado recursivo. Ante contenido
inesperado, reparse points o propiedad incierta, conservarla. No borrar el directorio
runtime completo ni cachés compartidas/históricas: también contiene logs del CLI y
temporales de otras sesiones. No eliminar bases SQLite de atribución dudosa. Tratar
estos archivos como sensibles y no compartir enlaces de login, tokens ni claves API.
No borrar directorios temporales generales ni procesos de otros usuarios. Esta
limpieza no garantiza eliminación de copias, backups ni recuperación forense.

Los límites configurados son 1000 logs, 100 trazas, 1000 puntos métricos por
dimensión, 10 recursos, 32 atributos y longitud 256. No son TTL ni límite global
de bytes; los datos viejos pueden expulsarse, atributos truncarse y recursos nuevos
rechazarse. La disponibilidad, entrega y retención no están garantizadas. QA local
final sobre el launcher aislado: **8/8 PASS**, con consulta autenticada, correlación
y privacidad de logs, respuestas protected/passthrough, nueve instrumentos con
puntos almacenados, expulsión al límite de 1000 logs, reinicio sin logs/recursos/
puntos métricos y navegación real en Edge por logs y métricas. El instrumento
secundario se generó reiniciando Gateway con configuración sintética opt-in; no se
observaron los nueve desde una sola instancia. En MVP-39 no se probó dinámicamente la
efectividad de los otros límites; MVP-42 amplía la evidencia según la sección siguiente.
Revisión independiente MVP-39 aprobada
exclusivamente para uso local sintético; sin despliegue.

Validación de ciclo de vida MVP-40: **5/5 comprobaciones PASS**, sin cambios al launcher.
Se envió `CTRL_C_EVENT` real (evento 0, equivalente a Ctrl+C) en una consola Windows
dedicada: en dos ciclos terminaron el launcher y sus descendientes inventariados,
quedaron libres los tres puertos y no se recurrió a terminación forzada. Antes del
primer cierre, la API autenticada confirmó un marcador de log sintético y un recurso
con métricas; tras reiniciar sin emisores devolvió cero logs y cero recursos. Esto
no es una consulta independiente de puntos métricos en SQLite. Se retiraron únicamente
dos carpetas propias con cuatro archivos npmrc vacíos, sin afectar otras carpetas;
los directorios de dashboard y una base SQLite residual de propiedad no concluyente
se conservaron. La captura del árbol anterior a la señal no prueba ausencia de procesos
ya desvinculados, ni garantiza el mismo comportamiento al cerrar una ventana o en
todas las terminales. No se probó borrado seguro ni limpieza automática de bases/cachés.

### Prueba reproducible del ciclo de vida (MVP-41)

En Windows, con Node >=20, Windows PowerShell y los requisitos del launcher MVP-39:

```powershell
node scripts/observability/Test-LocalObservabilityLifecycle.test.cjs
node scripts/observability/Test-LocalObservabilityLifecycle.cjs
```

El primer comando ejecuta las pruebas focales mediante `node:test`. El segundo
arranca el launcher en una consola dedicada, sin requerir hosts Gateway/TestApi,
envía únicamente OTLP sintético y ejecuta dos ciclos. Puede invocarse desde otra
carpeta usando la ruta absoluta del script. No admite parámetros salvo `--help`.
Antes de empezar deben estar libres 18888, 4317 y 4318: si alguno está ocupado,
falla sin detener al propietario ni crear una sesión de prueba. No ejecutar dos
pruebas simultáneamente ni usar con datos reales.

La salida JSON y `results.json` bajo una carpeta única en
`$env:LOCALAPPDATA\Temp\opencode\MVP41-lifecycle\run-*` contienen resultados
cerrados, no tokens ni claves API. `positive` confirma un log y un recurso con
métricas mediante API autenticada; `stop1` y `stop2` comprueban `CTRL_C_EVENT` real,
salida del árbol inventariado y puertos libres; `restart` comprueba cero logs y cero
recursos antes de nuevos emisores. Código de salida 0 exige los cuatro PASS y la
confirmación de eliminación de los logs temporales propios (`logsSanitized=true`).
Los snapshots de procesos no prueban ausencia de procesos previamente desvinculados,
ni la consulta de recursos vacíos sustituye una lectura independiente de puntos SQLite.

No termina procesos por nombre o puerto, no aplica terminación forzada y no elimina
sesiones del launcher, bases SQLite ni cachés. Si no puede confirmar el cierre,
devuelve FAIL; `manualRecovery=true` identifica que el helper conserva su consola
para no cerrarla sobre hijos vivos. Inspeccionar el árbol actual y cotejar PID,
fecha de creación y ascendencia con las identidades del resultado antes de cualquier
intervención. No terminar un proceso basándose solo en un PID. Los archivos raw
`out.txt`/`err.txt` de los ciclos pueden contener credenciales efímeras mientras el
helper está vivo o si falla la sanitización (`logsSanitized=false`): no compartirlos.
Los timeouts limitan las esperas del runner, pero la consola de recuperación puede
permanecer abierta hasta una intervención manual verificada. La protección de rutas
no garantiza seguridad frente a carreras de un adversario con acceso al filesystem.

### Validación dinámica de retención (MVP-42)

Validación local con Aspire CLI **13.6.4** y el launcher sin modificaciones:
**16/16 comprobaciones PASS**, comando del harness de QA con salida 0. No se
ejecutaron Gateway/TestApi ni se enviaron datos reales. El harness y sus resultados
son artefactos locales de QA, no un nuevo runner versionado como el de MVP-41.

| Límite | Observación comprobada |
| --- | --- |
| 100 trazas | API autenticada: 0, 1, 99 y 100 trazas. Tras enviar la 101, quedaron exactamente las identidades 2–101; se expulsó la primera. Cada traza sintética contenía un solo span. |
| 1000 puntos por dimensión | Lectura SQLite de la base propia en modo solo lectura: instrumento objetivo con 0, 1, 999 y 1000 puntos en dimensión A. Tras el punto adicional, quedaron exactamente los valores 1–1000, sin el 0. Otra dimensión B conservó sus tres valores 10000–10002 mientras A estaba llena y después de la expulsión. Se usó un gauge entero, no todos los tipos de instrumento. |
| 10 recursos | API autenticada: 0, 1 y 10 recursos. Un log para el undécimo recibió HTTP 200 **con** `partialSuccess.rejectedLogRecords="1"`; permanecieron exactamente los diez recursos anteriores, sin aceptar ni reemplazar por el nuevo. No se probó ese rechazo mediante las otras señales OTLP. |

Casos de frontera: `Traces_AtLimit_OneHundred`,
`Traces_OverLimit_EvictsOldest`, `Metrics_AtLimit_OneThousand`,
`Metrics_SeparateDimension_IndependentCapacity`, `Metrics_OverLimit_EvictsOldest`,
`Resources_AtLimit_Ten` y `Resources_OverLimit_RejectsEleventh`.
No se infirió retención de puntos a partir de `hasMetrics`, una página de resultados
o un gráfico agregado: se compararon conjuntos exactos de valores almacenados por
dimensión. La base se seleccionó como única carpeta de dashboard nueva respecto
al inventario previo y se verificó un marcador aleatorio de esa ejecución tanto en
API como en SQLite. El caso de instrumento vacío se consultó después de ese marcador:
no significa que todo el dashboard careciera de métricas en ese instante.

`Lifecycle_Shutdown_ProcessesAndPortsGone` confirmó `CTRL_C_EVENT` real,
salida de los procesos inventariados con PID y fecha de creación, y puertos
18888/4317/4318 libres. El helper confirmó eliminación de sus logs temporales,
sin terminación forzada ni recuperación manual. No se borraron bases ni cachés.

La evidencia de esta ejecución no certifica TTL, límite global de bytes, borrado
seguro, concurrencia, todas las ordenaciones temporales ni retención de todos los
instrumentos. Los límites de 32 atributos y longitud 256 siguen sin validación
dinámica. No es aceptación productiva ni una nueva revisión independiente de
seguridad; se reutilizó el protocolo de consola MVP-41 sin modificarlo.

### Exportación OTLP sintética (MVP-38)

Desactivada por defecto e independiente en ambos procesos. Para enviar únicamente
las métricas y eventos sanitizados existentes a un receptor local OTLP HTTP/protobuf:

```powershell
dotnet run --project TestApi -- --urls http://127.0.0.1:5101 --TechnicalCorrelation:Enabled true --SyntheticOtlp:Enabled true --SyntheticOtlp:Endpoint http://127.0.0.1:4318/
dotnet run --project Gateway -- --urls http://127.0.0.1:5100 --TechnicalCorrelation:Enabled true --SyntheticOtlp:Enabled true --SyntheticOtlp:Endpoint http://127.0.0.1:4318/
```

`Endpoint` solo acepta `http` con IP literal canónica `127.0.0.1` o `::1`, puerto
explícito y raíz `/`; las rutas fijas son `/v1/metrics` y `/v1/logs`. Rechaza DNS,
userinfo, query, fragment, redirects y proxy. La configuración se congela al arranque;
claves desconocidas o un endpoint inválido impiden servir. Desactivado no crea
providers, readers, colas, workers ni tráfico OTLP.

Gateway exporta exclusivamente los nueve instrumentos actuales de `Gateway.Privacy`
y los eventos 4/5/6 de `Gateway.PanResponseTransform`; TestApi solo EventId 1
`TechnicalCorrelationObserved`. No exporta logs framework, trazas, scopes, Activity,
baggage, excepciones, cuerpos, PAN, URLs/query, headers ni atributos ambientales. El
recurso contiene únicamente `service.name` fijo. La correlación nunca es tag métrico.

La cola de logs es acotada (256, lotes de 64, espera 1 s); export y métricas usan
timeouts de 2 s, intervalo métrico de 5 s y apagado best-effort. Collector caído,
lento o con error no cambia HTTP, inspección, admisión ni readiness; pueden perderse
o duplicarse datos. HTTP loopback no autentica al proceso collector: este alcance es
solo local y sintético, sin TLS, credenciales, retención, backend remoto, producción,
datos reales ni garantía de entrega. Para rollback, desactivar `SyntheticOtlp` y
reiniciar; retirar la sección antes de volver a binarios previos. Sin migraciones.

### Correlación técnica sintética (MVP-37)

Desactivada por defecto en **ambos procesos**: `TechnicalCorrelation:Enabled=false`.
Para observar una demostración exclusivamente local con datos sintéticos, reiniciar
ambos procesos con opt-in explícito (sin modificar appsettings):

```powershell
dotnet run --project TestApi -- --urls http://127.0.0.1:5101 --TechnicalCorrelation:Enabled true
dotnet run --project Gateway -- --urls http://127.0.0.1:5100 --TechnicalCorrelation:Enabled true
```

La opción se captura al arranque; no hay reload. Gateway genera un GUID N lowercase
de 32 caracteres por solicitud proxy, en protected y passthrough, y reemplaza todos
los valores de `X-Gateway-Correlation-Id` enviados por el cliente. Dashboard y health
quedan fuera. Activado, elimina hacia upstream `traceparent`, `tracestate`, `baggage`,
`Request-Id` y `Correlation-Context` e impide su reinyección automática en el transporte.
No modifica Activity.Current ni crea spans/scopes de correlación.

TestApi solo observa un valor lowercase hex32 no cero, sin trim ni normalización.
Emite categoría `TestApi.SyntheticApi`, EventId 1 `TechnicalCorrelationObserved`, con
plantilla fija `Synthetic request received; correlation {CorrelationId}.`, sin URL,
query, cuerpos, excepciones, headers adicionales ni framework request logs. Los fallos
ordinarios de IsEnabled/publicación se aíslan del resultado HTTP; los providers externos
pueden enriquecer o perder logs y quedan fuera de la garantía del productor.

Los eventos existentes 4/5 y sus 6 del gateway comparten esa identidad con la API,
sin cambiar schema1 ni inventar eventos en passthrough. Sin feature, conservan su
fallback local anterior. No se retorna el header reservado al cliente, tampoco como
trailer; la API no lo refleja. Desactivado se conserva el comportamiento anterior.

Un ID válido **no autentica procedencia**: alguien que acceda a la API directa puede
enviar uno. No es identidad de usuario, idempotencia, transacción ni garantía de entrega
de logs. No añade persistencia/exporter, modifica protección, políticas, admisión o
deadlines, ni habilita uso con datos reales. Passthrough sigue sin proteger los datos.
Para rollback, desactivar en ambos procesos y reiniciar; retirar la opción antes de
volver a binarios previos. Sin migraciones. Validación local sintética final: build
Release sin errores ni advertencias y suite completa **1424/1424 PASS**, cero omitidas.
Las revisiones globales de código y seguridad quedaron aprobadas sin bloqueantes y el
incremento se publicó en `cc44c025c1f2d033a88707975b0495a0a47cb84d`. Esto no acredita
RF11 completo, rendimiento aceptado, despliegue ni uso con datos reales; las cifras
históricas de otros MVP más abajo no son validación de MVP37.

### Estado de recarga de solo lectura (MVP-36)

El panel distingue la **última evaluación terminada de recarga** de la disponibilidad
del gateway. Si una actualización se rechaza, muestra **«La última política válida
sigue activa»**; ese rechazo no cambia readiness ni convierte la política vigente
en una política inválida. No certifica que el archivo actual ya se haya leído,
la frescura de la captura, privacidad ni disponibilidad del upstream.

GET `/dashboard/api/summary` conserva `schemaVersion: 1` y añade únicamente
`policyReload`, con estos cinco campos:

```json
{
  "enabled": true,
  "status": "rejected",
  "reason": "invalid_configuration",
  "lastCheckedAtUtc": "2026-10-29T12:00:00+00:00",
  "lastAppliedAtUtc": "2026-10-29T11:59:00+00:00"
}
```

- `disabled`: enabled false, motivo y ambas horas null; no se activa lector ni timer.
- `valid`: enabled true, motivo null y ambas horas presentes; incluye evaluación
  válida sin cambios, no solo una publicación nueva.
- `rejected`: enabled true, ambas horas presentes y motivo exclusivamente
  `read_failed`, `too_large`, `invalid_configuration` o `incompatible_snapshot`.
  El panel usa traducciones constantes, nunca mensajes de excepción.
- `lastCheckedAtUtc` se registra en cada intento terminado, incluso un rechazo
  repetido suprimido en logs. `lastAppliedAtUtc` cambia solo con publicación efectiva,
  incluida la inicial. Un rechazo o evaluación válida sin cambios conserva la última
  activación. Cancelación antes de completar no registra un rechazo ni otra evaluación.

Las horas proceden de TimeProvider en UTC y son **observacionales**: el reloj puede
retroceder. No se deducen duraciones, orden monotónico ni SLA. El proveedor publica
catálogo y estado juntos en una vista inmutable y el summary captura esa vista una
vez; las métricas siguen siendo acumulados observacionales independientes.
Solo se conserva el estado actual, sin historial, archivos, rutas, selectores,
contenido, excepciones ni nueva configuración. No cambia el mecanismo de recarga
opt-in ni sus restricciones Windows/disco local descritas en MVP35.

La sección muestra «Recarga desactivada», «Última comprobación válida» o
«Actualización rechazada», con horas de última comprobación y activación. No añade
botones, edición, peticiones ni endpoints. Mantiene controles locales, CSP, métodos,
demo aislada y eventos 4–7. En servidores anteriores sin `policyReload`, la UI indica
«Estado de recarga no disponible en este servidor», no desactivada. Si el objeto está
presente pero malformado, no renderiza parcialmente: conserva la captura anterior y
muestra el aviso de actualización fallida existente. Los textos se insertan como
texto, no como HTML; los motivos recibidos no se muestran libremente.

Validación sintética local: build Release sin errores ni advertencias; focal
**143/143 PASS**, solución **1340/1340 PASS**, cero omitidas, y Edge real **4/4 PASS**.
Incluye rechazo/recuperación con archivo y YARP reales, coherencia de catálogo/horas
durante cambios concurrentes, preservación de readiness, 22 objetos inválidos y legacy
mediante intercepción CDP del summary, 320 px reales y offline/recuperación con demo
persistente. Las pruebas distinguen IO real de fixtures del consumidor; no acreditan
auditoría WCAG ni lector de pantalla exhaustivos. La recarga automática visible tiene
además evidencia focal de frontend; la aceptación HTTP usa refresh manual del panel.
Sin despliegue, datos reales, nuevo benchmark ni aprobación productiva.

### Snapshots locales completos (MVP-35)

**Opt-in**: `--LocalPolicySnapshotFile C:\ruta-controlada\policy.json`. Sin esa
opción no se registra reader ni servicio/timer de reload; continúan los defaults
y el catálogo inmutable de arranque. La opción presente pero vacía es inválida.
En este slice el reader admite **solo Windows, disco fijo local**, ruta absoluta
ordinaria y archivo regular; rechaza URLs, rutas relativas, UNC/red, namespaces
device/pipe, alternate data streams y reparse points observables en archivo o
ancestros. No es una API administrativa ni añade autenticación/autorización.

El operador privilegiado debe controlar el directorio, ancestros y archivo mediante
permisos del sistema operativo; no ponerlos bajo escritura de usuarios no confiables.
La comprobación de ubicación **no resiste carreras de un adversario del filesystem**.
No se abre ningún archivo elegido por requests HTTP. Lectura real acotada a 1 MiB
+ 1 byte para detectar exceso; máximo aceptado 1 MiB. No hay hashes de body,
historial, caché de contenido, watcher, cola, exporter ni instrumentos nuevos.

El archivo inicial válido y compatible es obligatorio **antes de servir**; un fallo
aborta arranque con error genérico, sin fallback silencioso. También debe ser válida
la configuración base habitual. El archivo contiene exclusivamente la raíz
`PanPolicies`, completa y aislada, sin merge de appsettings, entorno o CLI. Se reutilizan
binding estricto, validación y compilación normalizada existentes. Ejemplo completo,
compatible con la base por defecto (PowerShell, directorio local previamente protegido):

```powershell
$file = 'C:\gateway-policy\policy.json'
$policy = @'
{
  "PanPolicies": {
    "InspectionBudget": {
      "InspectionTimeoutSeconds": 10,
      "MaxConcurrentInspections": 6,
      "MaxReservedBodyBytes": 6291456
    },
    "CoverageContract": {
      "Version": "v1",
      "Required": [{ "RouteId": "protected-payment", "Selector": "$.payment.cardNumber" }]
    },
    "Policies": [{
      "PolicyId": "PAN", "Version": "v1", "RouteId": "protected-payment",
      "Mode": "Mask", "Transformation": "PAN", "Selector": "$.payment.cardNumber",
      "MaxBodyBytes": 1048576, "SecondaryPanDetection": false, "AdditionalRules": []
    }]
  }
}
'@ | ConvertFrom-Json
function Publish-LocalPolicy {
  $temp = Join-Path (Split-Path $file) ('policy-' + [guid]::NewGuid().ToString('N') + '.tmp')
  [IO.File]::WriteAllText($temp, ($policy | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
  [IO.File]::Move($temp, $file, $true)
}
Publish-LocalPolicy
dotnet run --project Gateway -- --urls http://127.0.0.1:5100 --LocalPolicySnapshotFile $file
```

Usar temporal **en el mismo directorio/volumen y reemplazo atómico**; no editar el
archivo activo in-place. Mantener TestApi en otra terminal como en «Ejecutar».
En otra terminal, conservando `$policy`, `$file` y la función anterior:

1. `curl.exe http://127.0.0.1:5100/protected/payment` muestra Mask/v1;
   `/dashboard/api/summary` indica v1.
2. `$policy.PanPolicies.Policies[0].Mode = 'Block'` y
   `$policy.PanPolicies.Policies[0].Version = 'v2'`; `Publish-LocalPolicy`.
   Tras una lectura válida, requests nuevas usan Block/v2 (respuesta bloqueada).
3. Cambiar Mode a `Mask` sin cambiar v2 y publicar: se rechaza y se retiene Block/v2.
4. Cambiar Version a `v3` y publicar: recuperación válida Mask/v3.

Quedan congelados respecto a la base: conjunto de rutas protegidas, PolicyId por
ruta, CoverageContract completo e InspectionBudget global. YARP, upstream, admisión,
deadlines y listeners no pertenecen al archivo. Son mutables reglas/tipos, Mode,
SecondaryPanDetection, MaxBodyBytes y Version sujetos al validador. Un comportamiento
compilado distinto exige Version distinta de la **activa**, por política; no hay
registro histórico que prohíba reutilizar versiones anteriores. Cambiar solo Version
publica; contenido semánticamente idéntico no publica (defaults explícitos equivalentes
no cuentan como cambio). La compatibilidad inicial fija se compara con la base;
el archivo inicial establece la primera política activa antes de servir.

Un único BackgroundService espera un segundo nominal, lee, valida/compara y publica
una referencia atómica catálogo+metadatos inmutables. No solapa intentos ni acumula
ticks. Es polling de ingeniería, **no SLA**; cancelación cooperativa no promete
latencia universal de apagado ante filesystem bloqueado. La cancelación observada
antes del swap conserva la activa. Cada instancia de PanPolicyCatalog sigue siendo
inmutable: el servicio público de DI conserva la base; los consumidores internos
capturan el snapshot activo. Cada request protegida captura después del routing y
antes de transforms/envío upstream; incluso esperando headers mantiene su captura
anterior. Requests en curso retienen snapshots antiguos en memoria hasta terminar.
El summary del dashboard usa una sola captura; métricas siguen siendo totales de
proceso, sin reset por publicación. DTO HTTP, controles y eventos 4/5/6 no cambian.

Solo la nueva categoría `Gateway.LocalPolicyReloadService` se habilita adicionalmente
en el filtro del host. EventId 7 (`LocalPolicySnapshot`), Information, plantilla fija
`Local policy snapshot; status {Status} reason {Reason}.`: Status `applied`/Reason
`none` por publicación real (incluida inicial); Status `rejected`/Reason exclusivamente
`read_failed`, `too_large`, `invalid_configuration` o `incompatible_snapshot`.
Sin ruta, archivo, selector, body ni excepción. Rechazos consecutivos con igual razón
se suprimen; cualquier intento válido, incluso sin cambio, reinicia silenciosamente
la supresión. El estado se confirma antes del logger, fallos ordinarios de IsEnabled
o Log se aíslan sin retry ni rollback; no se garantiza entrega del log.

Despliegue local: preparar base y archivo compatibles antes de habilitar la opción.
Sin migración ni dependencias nuevas. **Retirar la opción antes de rollback binario**
para no creer que un binario anterior recarga; conservar una base válida. RF12 cubre
solo este alcance fijo/local por instancia; coordinación distribuida y RF11 distribuido
siguen pendientes. RNF y producto no aprobados.

Validación local sintética final: build Release sin errores ni advertencias,
focal **72/72 PASS**, suite completa **1325/1325 PASS**, cero omitidos, y Edge
**4/4 PASS** para actualización del panel, rechazo/recuperación y demo de solo lectura.
Incluye HTTP real por YARP, captura anterior en vuelo, publicación atómica y
cancelación/disposición/join de una lectura cooperativa retenida mediante seam interno;
esta última no acredita cancelación universal de drivers. Revisiones independientes
de código y seguridad **APPROVED_WITH_WARNINGS**, sin bloqueantes para este alcance.
Junctions reales prueban rechazo antes de abrir, tanto en entrada como ancestro;
el fixture de symlink real de archivo queda **NOT_RUN**, por privilegios insuficientes.
La revisión de seguridad consideró proporcional esa cobertura junto al chequeo estático
del mismo flag ReparsePoint; no se presenta el caso no ejecutado como PASS.
La evidencia de fases anteriores y sus fallos de fixtures/compilación se conserva.
Estos resultados no aprueban datos reales, despliegues ni operación productiva.

### Eventos agregados por regla (MVP-34)

Después de intentar event5, la terminal ganadora intenta un evento `Information`,
`EventId(6, "PrivacyRuleMatched")`, por cada regla con matches validados. Plantilla
exacta, excepción null:

```text
Privacy rule matched; schema {SchemaVersion} route {RouteId} service {ServiceId} policy {PolicyId} version {PolicyVersion} correlation {CorrelationId} outcome {Outcome} rule {RuleId} transformation {Transformation} validated matches {ValidatedMatches} validated masks {ValidatedMasks}.
```

Estado exacto: esos once campos más `{OriginalFormat}`; `SchemaVersion=1` int,
`ValidatedMatches` long >0, `ValidatedMasks` long >=0, demás campos string.
`RuleId` es exclusivamente `primary`, `additional-0`..`additional-14` por posición
en AdditionalRules normalizado, o `secondary-pan`. **Identidad posicional, estable
solo dentro de una versión**, no identidad estable de negocio. Un cambio de orden
debe distinguirse con `PolicyVersion`; MVP34 no añadía enforcement de versión
(MVP35 sí lo exige para cambios de comportamiento publicados por reload opt-in).
Nunca se deriva identidad del selector, hash ni valor. `Transformation` solo puede
ser `PAN`, `Email` o `Phone`; secundaria siempre `PAN`.

Son conteos agregados de strings seleccionados por declaración: no prueba de PII
individual ni de entrega. Observe y Block cuentan matches y cero máscaras. Las
reglas explícitas conservan prioridad sobre secundaria sin doble conteo, incluso
si su transformación declarada es Email/Phone. Se recorren duplicados y arrays.
Sumar matches explícitos/secundarios y máscaras de event6 reproduce los respectivos
conteos de event5 (si el productor habilita y valida todos los eventos del lote).

El acumulador local tiene 17 posiciones; un snapshot inmutable se confirma junto
a los conteos tras recorrido, Dispose del writer y check de deadline. Fallos antes
descartan todo event6; después conservan el snapshot con el outcome terminal final.
La notificación ocurre después de métricas y liberar el lease de inspección, fuera
de locks. Ningún payload/contexto ni acumulador mutable pasa al callback.

Event5 y event6 comparten un GUID nuevo por invocación (`N`, 32 hex minúsculas),
solo local, nunca de Activity/headers ni propagado por HTTP. IDs inválidos de
ruta/política/versión omiten ambos; servicio inválido usa `unknown`. Una política
pública con más de 16 reglas explícitas o cualquier transformación fuera de la
allowlist omite **todos** los eventos6, sin truncar ni alterar inspección/event5.
Capacity, exclusiones, upstream/política ausente y precancelación no generan event6.
Event4/5 conservan IDs, plantillas y campos; APIs públicas, configuración, catálogo,
defaults, métricas/tags, HTTP y dependencias no cambian.

Cada publicación tiene su propio IsEnabled/Log aislado ante fallos ordinarios;
fallar event5 o una regla no suprime las siguientes. Con Information deshabilitado
no se preparan correlación ni argumentos de evento. No hay retry, tasks, colas,
historial ni estado global de logging nuevo. Máximo **18 eventos por lote**
(event5 + hasta 17 event6), además de los eventos preexistentes: **no es rate cap**.
Providers síncronos pueden retener admisión de proxy y demorar el retorno, pero ya
no el lease de inspección. La garantía de allowlist/orden/intento es del productor,
no de entrega, orden final ni enriquecimiento de un sink externo.

Cambio aditivo sin migración ni orden obligatorio de despliegue; consumidores
pueden aceptar event6/schema1 primero. Rollback a MVP33 retira solo event6 y su
snapshot interno; no cambia contratos públicos ni persistencia. RF11 sigue
**parcial**: correlación distribuida, reload y RNF pendientes; no producción ni
aprobación de producto. Validación MVP34 y manifiestos se conservan en scratch
externo `MVP34-rule-events`; la evidencia de MVP33 de abajo es histórica.

Validación local MVP34: build Release de solución PASS (0 advertencias/errores),
focal `PrivacyRuleEventTests|PrivacyInspectionEventTests` **148/148 PASS**,
suite completa **1253/1253 PASS**, cero omitidas. HTTP YARP loopback real cubre
Mask/Block/Observe, correlación compartida entre 5/6 y distinta entre requests;
otros fallos terminales se validan por fixtures/seam, no se afirman por HTTP.
Revisión independiente y aceptación del Coordinador pendientes.

### Eventos terminales de inspección (MVP-33)

Cada terminal ganadora intenta una sola publicación `Information`,
`EventId(5, "PrivacyInspectionCompleted")`, por el ILogger existente del transform.
Plantilla constante, excepción siempre null:

```text
Privacy inspection completed; schema {SchemaVersion} route {RouteId} service {ServiceId} policy {PolicyId} version {PolicyVersion} correlation {CorrelationId} outcome {Outcome} validated masks {ValidatedMasks} validated explicit sensitive matches {ValidatedExplicitSensitiveMatches} validated secondary PAN matches {ValidatedSecondaryPanMatches}.
```

Estado exacto: `SchemaVersion=1` (int), `RouteId`, `ServiceId`, `PolicyId`,
`PolicyVersion`, `CorrelationId`, `Outcome`, `ValidatedMasks`,
`ValidatedExplicitSensitiveMatches`, `ValidatedSecondaryPanMatches` (los tres long),
más `{OriginalFormat}`. Las fuentes, gramática ASCII 1..64 y fallback `unknown`
son los de MVP32: IDs de política inválidos omiten **solo event5**; servicio del
ClusterId propietario del host, nunca de request/address. La API pública sin
metadato de servicio conserva `unknown`. La correlación es GUID local nuevo `N`,
32 hex minúsculas, no toma Activity/headers/TraceIdentifier ni se propaga en HTTP.
No incluye RuleId, RuleEvaluation, selector, payload, URL, headers, excepción,
método, status, media type, tamaños, duración ni contexto ambiental.

`Outcome` conserva los ocho textos existentes: `inspected`, `blocked`,
`capacity_exceeded`, `canceled`, `write_error`, `faulted`, `timed_out`,
`policy_blocked`. Los conteos son los del recorrido completamente validado:
antes del commit son cero; después se conservan aunque falle escritura, checkpoint,
cancelación o timeout. Observe cuenta matches pero calcula cero máscaras; las
reglas explícitas tienen prioridad sobre detección secundaria, sin doble conteo.
Son agregados de cálculo/declaración, **no prueba de PII individual, de una regla
individual ni de entrega al cliente**. No se normalizan como respuestas entregadas.

El overload interno específico de telemetry transporta solo resumen inmutable
outcome+conteos, no logger/servicio. Complete ganador publica métricas, libera lease
y luego notifica fuera de locks. Capacity denegada publica primero su outcome,
luego event5 con ceros, sin adquirir lease. Firmas públicas y overloads previos
permanecen; llamadas directas a telemetry no emiten eventos. Exclusiones publican
solo event4; upstream nulo, política ausente y cancelación previa a TryStart no
publican event5. Eventos 1–4 y semántica de fallos del logger 1–3 se conservan.

Solo se preparan correlación/argumentos si Information está habilitado. Fallos
ordinarios de IsEnabled/Log y listeners se aíslan independientemente, sin reintento
ni logging alternativo; errores críticos no se ocultan. No hay tareas, colas,
historial ni estado compartido entre invocaciones nuevo. Providers síncronos pueden
bloquear o descartar: event5 ya no retiene lease, pero puede demorar el retorno.
Volumen máximo: un intento por terminal habilitada y válida (además de eventos
preexistentes), proporcional al tráfico, sin sampling/rate limit nuevo. No se
garantiza latencia ni entrega a todos los consumidores. La allowlist cubre solo
este productor; enriquecimiento ambiental de providers externos queda fuera de
la garantía, como en MVP32. No cambia configuración de logging/host.

Cambio aditivo de logging sin migración ni orden obligatorio de despliegue;
consumidores pueden aceptar EventId 5/schema 1 antes del binario. Rollback a MVP32
retira solo event5; eventos1–4, métricas/tags, dashboard, HTTP, catálogo, defaults
y dependencias permanecen. RF11 sigue **parcial**, local y sintético: no se ha
desplegado ni aprobado el producto.

Validación local MVP33: build Release de solución PASS (0 advertencias/errores),
focal `PrivacyInspectionEventTests` **107/107 PASS**, suite completa **1212/1212
PASS**, cero omitidas. Se conservan el build inicial fallido por inicializador
del test (corregido) y el focal anterior, no se presentan como ejecución final.
La evidencia MVP33 distingue ocho outcomes por seam interno de HTTP real YARP
en Mask/Block/Observe (`inspected`/`policy_blocked`); no afirma ocho outcomes
reproducidos por HTTP. Tests y artefactos se registran en scratch externo
`MVP33-terminal-events`, sin reemplazar el histórico MVP32 de abajo. La revisión
independiente y aceptación siguen pendientes del Coordinador.

### Eventos de exclusión (MVP-32)

Con upstream y política de catálogo, cada exclusión intenta publicar una sola vez
por el ILogger existente: `Information`, `EventId(4, "PrivacyExcluded")`. Plantilla
constante (sin excepción adjunta):

```text
Privacy excluded; schema {SchemaVersion} route {RouteId} service {ServiceId} policy {PolicyId} version {PolicyVersion} reason {Reason} rule evaluation {RuleEvaluation} correlation {CorrelationId}.
```

Estado exacto: `SchemaVersion=1` (entero), `RouteId`, `PolicyId` y `PolicyVersion`
de catálogo, `ServiceId` del ClusterId propietario de la ruta del host
(`synthetic-api`, nunca destination/address/request), `Reason` con la prioridad
MVP31 indicada abajo, `RuleEvaluation=not_evaluated` y `CorrelationId` GUID nuevo
local de 32 caracteres hexadecimales minúsculos (`N`). Más `{OriginalFormat}`
normal del framework. No existe RuleId porque no se evalúan reglas. No se incluyen
valor, URL, headers, método, status, media type, excepción, contexto ni selector.
La API pública `ApplyAsync(context, policy)` y los constructores se conservan:
sin contexto de servicio usa el literal acotado `unknown`, sin inventar atribución.
El overload interno recibe solo metadato del host y valida identificador ASCII
de 1..64 caracteres (alfanumérico inicial, luego alfanumérico/`.`/`_`/`-`);
metadato inválido o ausente también usa `unknown`. El host usa identificadores del
catálogo validado, no texto de request ni payload. Como `CompiledPanPolicy` es
públicamente construible, el nuevo evento valida también **los tres** identificadores
RouteId/PolicyId/Version con esa misma gramática; si cualquiera es inválido (incluidos
null, vacío, control/newline o más de 64 caracteres), **omite el evento**, sin
sanitizar/truncar ni modificar la métrica o el HTTP preexistentes. La validación
local asegura formato acotado, no pertenencia al catálogo para llamadas públicas.

La correlación identifica **esta invocación**, no end-to-end: no toma correlación
entrante, Activity, TraceIdentifier ni baggage; no se propaga ni devuelve en HTTP.
Solo se prepara estado/correlación si Information está habilitado. Se aíslan
excepciones ordinarias tanto de IsEnabled como de Log, incluida cancelación del
logger. Contador y evento son publicaciones independientes ante esos fallos; sin
reintento ni log alternativo. Errores críticos no se ocultan. Sin upstream, sin
política o para JSON elegible no se emite este evento nuevo.

La garantía de privacidad describe el estado allowlist y texto que emite este
productor, siempre con excepción null. No se añaden scopes ni enriquecedores.
Un provider confiable puede añadir contexto ambiental por su propia configuración:
ese enriquecimiento queda **fuera de esta garantía**. El host vigente registra
ConsoleLoggerProvider con formatter simple y scopes deshabilitados por defecto;
la configuración ambiental de LoggerFactory conserva sus defaults de Activity.
Overrides externos pueden cambiar esa configuración: no se promete controlar
otros providers, scopes del llamador ni sinks externos.

No hay lease, lectura de cuerpo, tareas, colas ni historial nuevos. Los proveedores
de logging y listeners son síncronos: pueden bloquear o descartar; no se garantiza
latencia, entrega ni recepción por todos los consumidores. El volumen máximo de
intentos de evento es uno por exclusión con Information habilitado: puede crecer
con el tráfico; la correlación no es tag métrico ni se almacena en el dashboard.
No hay muestreo ni rate limit nuevo. No prueba protección de cuerpos excluidos.

Cambio aditivo de contrato de logging, sin migración ni orden obligatorio de
despliegue. Consumidores pueden aceptar EventId 4/schema 1 antes de actualizar;
rollback del binario a MVP31 elimina solo este evento y conserva sus contadores.
Sin cambios de configuración, defaults, HTTP, elegibilidad, enum, métricas/tags,
panel o dependencias. RF11 sigue **parcial**, solo uso local sintético; sin
despliegue ni aprobación de producto/producción.

Validación local MVP32: build Release de solución PASS (0 advertencias/errores),
focal `PrivacyExclusionEventTests` **94/94 PASS**, suite completa **1105/1105 PASS**,
cero omitidas. Incluye HTTP real cliente → YARP → upstream sintético, fallos de
IsEnabled/Log (también cancelación), independencia del contador, esquema exacto,
precedencia, ausencia de payload y correlaciones locales distintas. Se conserva
el primer intento completo interrumpido por timeout del runner a 120 s, sin TRX
final; la ejecución final terminó correctamente con mayor límite del runner.
Logs, TRX, snapshots, manifiestos SHA-256, cuatro DLL y matriz Requirement/Evidence
en scratch externo `MVP32-exclusion-events/v2` para la revisión de la frontera
pública; la evidencia anterior se conserva en el directorio padre. La revisión propia no sustituye
revisión independiente ni aceptación de producto.

### Exclusiones observables (MVP-31)

Con upstream y política, cada invocación excluida publica **+1** en el contador
existente `gateway.privacy.inspection.outcomes` (`{inspection}`, meter
`Gateway.Privacy` versión `1.0.0`). Conserva exactamente los cuatro tags
`gateway.route.id`, `gateway.policy.id`, `gateway.policy.version` y
`gateway.privacy.outcome`. Prioridad: `excluded_head` para HEAD, luego
`excluded_bodyless` para 204/304 restantes, luego `excluded_media_type` para
Content-Type ausente, inválido o distinto de `application/json`. No se admiten
nuevos tipos (por ejemplo `application/problem+json` sigue excluido); JSON
admitido, incluidos sus parámetros y comparación case-insensitive, queda intacto.

Se publica antes del retorno, sin lease, lectura del body, duración, tamaños,
hallazgos ni máscaras. Sin upstream o sin política no hay muestra. Funciona igual
en Mask, Block y Observe, sin cambiar defaults, elegibilidad, HTTP, host ni UI.
Las exclusiones **no son éxito ni fail-open** ni evidencia de protección: cuerpos
non-JSON pueden pasar sin inspección. No se agregan al panel: el enum público y
sus ocho totales permanecen intactos. No hay instrumentos, API ni dependencias
nuevas. Consumidores del contador deben tolerar los tres nuevos textos de outcome.

Las excepciones ordinarias de listeners se aíslan como antes (incluida su
OperationCanceledException); errores críticos no se ocultan. Los callbacks son
síncronos: un listener bloqueante puede demorar el retorno, sin garantía de
latencia, entrega ni aislamiento entre listeners. No se garantiza entrega de la
muestra a todos los consumidores si uno falla.

Sin migraciones ni orden de despliegue obligatorio; rollback del binario elimina
estos tres outcomes sin cambiar configuración. No se ha desplegado. RF11 sigue
**parcial**: solo validación local sintética; no implica aceptación productiva.
Las secciones siguientes conservan el contrato y la evidencia histórica de cada
incremento; las afirmaciones históricas de «sin outcomes nuevos» o exclusiones
«sin outcomes» describen su momento, no MVP31/MVP32. Desde MVP31 las exclusiones
sí publican los tres outcomes del contador; desde MVP32 intentan además el evento.

Validación local MVP31: build Release PASS (0 advertencias/errores), focal
final de cinco clases **384/384 PASS** y suite completa **1011/1011 PASS**, cero
omitidas. El focal incluye PrivacyTelemetryTests, InspectionDeadlineTests,
ObservePolicyTests, SecondaryPanMatchTelemetryTests y ExplicitSensitiveMatchTelemetryTests.
Se conserva la ejecución inicial **997/1011 PASS, 14 FAIL**: analizados uno por
uno, eran expectativas históricas de cero outcomes en exclusiones de las cuatro
últimas clases. Tras autorización para esos archivos se actualizaron solo esas
expectativas a una muestra +1 con el outcome exacto, preservando las aserciones
de ausencia de otros instrumentos, hallazgos, lectura, presupuesto y HTTP.
Se conserva también el build inicial fallido por asignación test-only a propiedad
init, corregido antes del build final. Logs, TRX, snapshots y manifiestos SHA-256
finales están en scratch local externo `MVP31-exclusions/v2`, sin sobrescribir la
evidencia inicial. No se declara revisión independiente realizada ni aceptación
de producto o producción.

### Observe (MVP-30): exposición deliberada, solo datos sintéticos locales

**Observe entrega los datos originales**, incluso PAN, Email y Phone seleccionados
o detectados. No enmascara ni bloquea por hallazgos. No activar sobre datos reales:
no hay autorización de rollout, despliegue ni uso productivo. RF11 sigue parcial y
RNF03 continúa pendiente de aceptación; este slice no certifica privacidad ni SLA.

`Mode` admite los textos exactos `Mask`, `Block` y ahora `Observe`; null, vacío,
espacios, distinta capitalización, números y desconocidos impiden arrancar. No hay
otra opción ni gating: el opt-in es un override explícito local, congelado al
arrancar. Ningún appsettings, selector, versión, presupuesto o default Mask cambia.
Con TestApi sintética ejecutándose en otra terminal como arriba:

```powershell
dotnet run --project Gateway -- --urls http://127.0.0.1:5100 --PanPolicies:Policies:0:Mode Observe
```

El detector secundario conserva su opt-in independiente, desactivado por defecto.
Para comprobarlo con datos sintéticos puede añadirse al comando
`--PanPolicies:Policies:0:SecondaryPanDetection true`. La demo del panel sigue
siendo SOLO Mask/Block y simulación sin API: no activa ni simula Observe.
El summary muestra el Mode actual sin cambiar DTO/schema/API/JS.

Observe **no es passthrough ni fail-open**: captura completa acotada, UTF-8 estricto,
JSON/profundidad máxima 64, tipos terminales, codificación, límites, IO, deadline,
lease y capacidad se validan como antes. Null pasa; otro terminal no-string falla
cerrado. Los errores tardíos de validación impiden entregar el original y mantienen
el 502 genérico existente. Tras iniciar la escritura, una cancelación o fallo puede
dejar bytes originales parcialmente entregados; no se garantiza atomicidad ni
sustitución por 502. Capacidad devuelve 503 sin leer. HEAD/204/304/non-JSON siguen
excluidos. Cancelación, timeout y trabajo no cooperativo retienen el lease hasta
cleanup real; callbacks síncronos pueden retenerlo.

Solo tras inspección completa se selecciona **siempre el cuerpo capturado original
byte por byte**: whitespace, escapes, duplicados y UTF-8, con status upstream,
incluidos errores HTTP JSON. Nunca se entrega la salida del writer provisional.
Esto es exactitud del **body capturado**, no wire exact ni preservación de headers,
framing o trailers. Se mantienen no-store, eliminación de validadores/digests/firmas,
Content-Type JSON UTF-8 y Content-Length final; otros headers no se inspeccionan
ni se promete que sean privados. No hay cobertura para canales directos/non-JSON.

CoverageContract acredita reglas configuradas, **no protección efectiva en Observe**.
No se debilita ni se modifica: puede satisfacerse sin que los datos estén ocultos.
Readiness NO garantiza privacidad, upstream disponible ni capacidad. La advertencia
estática visible del panel informa esa diferencia sin mostrar valores sensibles.

No se añaden instrumentos/tags/outcomes: éxito es `inspected`, máscaras siempre 0,
sin llamar ni calcular máscaras. El contador explícito MVP29 cuenta strings **por
declaración, no PII probada**, incluso vacíos/incompatibles; el secundario MVP28
solo coincidencias fuera de reglas explícitas cuando está activo. Prioridad y
algoritmo intactos. Conteos se confirman tras recorrido, writer.Dispose y deadline
check: fallo previo descarta, fallo posterior conserva con outcome final. Bytes de
salida preparada miden el original seleccionado, no entrega efectiva al cliente.

Activación/rollback: instalar primero un binario compatible y reiniciar con el
override opt-in solo en el entorno sintético local. Para desactivar, retirar el
override (recupera Mask distribuido) o restaurar explícitamente Mask/Block y
**reiniciar antes de volver al binario anterior**, que rechaza Observe. Sin
migraciones ni dependencias nuevas; no se ha ejecutado despliegue.

Validación sintética local MVP30: build Release de solución PASS, cero errores y
advertencias; focal relevante **651/651 PASS**, suite completa **1001/1001 PASS**,
cero omitidas. Incluye Kestrel real cliente → YARP → upstream sintético, errores
HTTP JSON, bytes originales y barrera `Duration.Samples >= N` **y** presupuesto
`0/0`. Se conservan build inicial fallido (propiedad test-only init) y focal
**631/643** (comparación test-only int/long del mismo valor), corregidos solo en
tests. Artefactos en scratch local externo, no en el repositorio. Revisiones
independientes de código y seguridad **APPROVED_WITH_WARNINGS**, sin bloqueantes;
Edge headless sobre copia de las DLL finales: **7/7 PASS**, incluidos aviso visible,
Observe real con cuerpo original y máscaras cero, demo sin API, recuperación,
320 px sin overflow documental y lectura cross-origin denegada.
Los tests con StackTrace/checkpoints son sensibles al JIT/runtime y los relojes
manuales no prueban entrega real del timer. Sin benchmark/sizing ni aprobación del
producto o de producción; teclado y lector de pantalla exhaustivos no verificados.

Las secciones MVP anteriores conservan evidencia histórica: sus afirmaciones de
«sin Observe» describen aquellos incrementos, no el contrato vigente de MVP30.

### Coincidencias sensibles explícitas validadas (MVP-29, previo a Observe)

`gateway.privacy.inspection.explicit_sensitive_matches` es un `Counter<long>` de
unidad `{match}`, en `Gateway.Privacy` versión `1.0.0`, con los mismos cuatro tags:
`gateway.route.id`, `gateway.policy.id`, `gateway.policy.version` y
`gateway.privacy.outcome` final. Cuenta una vez cada ocurrencia string terminal
seleccionada por declaración primary o AdditionalRules PAN/Email/Phone, incluidos
vacíos, incompatibles, escapes decodificados, arrays y propiedades duplicadas.
**No prueba que el valor sea PAN, Email o Phone.** Null, otros tipos, ausencia de
coincidencias, respuestas no elegibles, capacidad denegada y precancelación no
cuentan. El terminal explícito no-string/no-null sigue fallando cerrado.

Mask y Block cuentan igual; Block sigue calculando cero máscaras. Las reglas
explícitas tienen prioridad: su contador no solapa el secundario MVP28, cuyo
contrato y conteos se conservan. No hay nueva clasificación ni segunda pasada.
Un acumulador `long` local en cada recorrido se confirma junto con máscaras y
secundarios solo tras recorrido completo, disposición del writer y deadline check.
Los fallos anteriores descartan el parcial; los posteriores (checkpoint, headers,
escritura, cancelación o deadline) conservan el conteo con el outcome final.

Solo se publica un positivo una vez en Complete terminal; no es entrega al cliente
ni garantía de recepción por observadores. Cada publicación aísla excepciones
ordinarias independientemente. Callbacks síncronos bloqueantes conservan sus
límites: pueden bloquear finalización y retener el lease, sin liberación anticipada.
No se añaden strings de payload a telemetría, muestras retenidas ni tags.

Cambio aditivo de telemetría: consumidores por allowlist pueden incorporar el
nombre nuevo; rollback al binario anterior elimina solo este instrumento, sin
migración de datos/configuración ni cambio HTTP. **No se ha implementado Observe**:
los únicos modos siguen siendo Mask y Block. Sin cambios de defaults, host,
detector, opciones, cobertura mínima, dependencias ni panel/modelo/API/assets/UI.
El contador no aparece en el dashboard. RF11 continúa parcial; no acredita RNF03
ni producción. Sin despliegue, benchmark/sizing ni browser nuevo (UI intacta).
Los tests de StackTrace/checkpoint son sensibles al JIT/runtime y no prueban
entrega real del timer.

Validación sintética local MVP29: build Release de solución **PASS**, cero errores
y advertencias; focal con `ExplicitSensitiveMatchTelemetryTests`, `SecondaryPan*`,
`PrivacyTelemetryTests`, `InspectionDeadlineTests`, `MultiRuleTransformTests` y
`DashboardTests`: **468/468 PASS**; suite completa **914/914 PASS**, cero omitidas.
Incluye HTTP real Mask/Block con barrera `Duration.Samples >= N` **y** presupuesto
`0/0`. Se conservan un build inicial fallido (asignación test-only a propiedad
init) y un focal **459/460** (expectativa JSON de `+` frente a `\u002B`), corregidos
solo en tests. Revisión independiente estática **APPROVED_WITH_WARNINGS**, sin
defectos que requieran cambios; no implica aprobación del producto ni de producción.

### Detección secundaria PAN (MVP-27, solo datos sintéticos)

`PanPolicies:Policies:i:SecondaryPanDetection` es un booleano opcional: ausente
equivale a `false` y conserva el recorrido legacy. `true` activa inspección de
**todos los valores string originales** del JSON elegible, incluidos raíz, arrays,
propiedades duplicadas y escapes JSON decodificados; nunca nombres de propiedades.
El contrato es la configuración efectiva `IConfiguration`: acepta escalares
`true`/`false` sin espacios, sin distinguir mayúsculas (el proveedor JSON normaliza
booleanos a `True`/`False`). Rechaza null observable, vacío, números, otros textos
y cualquier hijo, incluso junto a un escalar válido. No promete distinguir formas
raw que un proveedor haya normalizado al mismo valor. Se congela al arrancar;
no hay reload. No cambian appsettings ni los defaults distribuidos.

Ejemplo opt-in con origen exclusivamente sintético:

```powershell
dotnet run --project Gateway -- --urls http://127.0.0.1:5100 --PanPolicies:Policies:0:SecondaryPanDetection true
```

El detector manual exige el valor completo: 13..19 dígitos ASCII, sin separador o
un único espacio ASCII/guion entre cada par de dígitos, longitud máxima 37 y Luhn
válido. No hace trim, no acepta Unicode, prefijos ni texto libre. No aplica reglas
de emisor: todos ceros también coincide si cumple formato y Luhn. No descubre PAN
en números JSON, headers, nombres de campos ni canales que antes no eran elegibles.

Las reglas **explícitas tienen prioridad**, incluso Email/Phone: una transformación
y como máximo una máscara por ocurrencia. Luhn negativo nunca evita una regla
explícita. Para coincidencias secundarias, Mask reutiliza la máscara PAN existente
(separadores y cuatro últimos dígitos visibles); Block responde 502 con
`privacy_response_blocked` sin calcular máscaras. Se completa toda la validación:
errores posteriores prevalecen con `privacy_inspection_failed`; deadline/cancelación
descartan la salida parcial. Un DOM, recorrido integrado, lease y deadline existentes;
sin segundo parse, regex de detección, listas de candidatos ni nuevos límites.

MVP28 añade solo el contador de coincidencias **secundarias validadas**, descrito
abajo, sin tags nuevos. El contador de máscaras sigue midiendo
sustituciones validadas, **no hallazgos ni entrega**; Block cuenta cero máscaras.
RF11 continúa parcial. El dashboard no expone el flag ni incluye el detector en
`ruleCount`/`transformations`: solo describe reglas explícitas; su demo no simula
este detector. No se modifica la cobertura mínima configurada: el detector no
sustituye selectores requeridos. No implica detección universal ni anonimización.

Despliegue/rollback: instalar primero el binario compatible y luego habilitar la
clave, con reinicio; retirar la clave antes de volver a un binario anterior, que la
rechaza como desconocida. No hay migración de datos ni despliegue realizado.
Se conserva el constructor/deconstruct de cinco posiciones de `CompiledPanPolicy`.

### Coincidencias secundarias PAN validadas (MVP-28, RF11 parcial)

`gateway.privacy.inspection.secondary_pan_matches` es un `Counter<long>`, unidad
`{match}`, en el Meter `Gateway.Privacy` versión `1.0.0`. Conserva exactamente los
cuatro tags actuales: `gateway.route.id`, `gateway.policy.id`,
`gateway.policy.version` y `gateway.privacy.outcome` (outcome **final**).

Cuenta una coincidencia por ocurrencia original que el detector secundario existente
acepta fuera de una regla explícita; arrays y propiedades duplicadas cuentan por
separado. Excluye strings seleccionados por PAN/Email/Phone explícitos, incluso
Luhn positivos; también null, otros tipos, nombres, Luhn negativos, opt-out,
respuestas no elegibles y rechazos de capacidad. Mask y Block cuentan igual;
Block sigue calculando cero máscaras. No cuenta todos los PAN, candidatos ni
hallazgos universales, ni demuestra entrega al cliente.

El acumulador `long` es local al recorrido integrado existente. Se confirma solo
tras completar todo el recorrido, disponer el writer y superar el deadline check,
en el mismo punto que las máscaras. Errores de tipo/JSON, deadline o cancelación
anteriores descartan el parcial. Una vez confirmado se conserva ante fallos tardíos
de checkpoint, headers o escritura, cancelación y timeout, con el outcome final.
Solo publica positivos una vez en Complete terminal, nunca una muestra cero, ni
durante una escritura pendiente. Cada instrumento aísla excepciones ordinarias
independientemente. Un listener síncrono bloqueante puede demorar la finalización
y retiene el lease: no se promete aislamiento temporal ni entrega exactamente una vez
a consumidores externos. Sin muestras/valores guardados, tags nuevos, otro parse,
recorrido, detector, opciones o dependencias.

**El contador no es visible en el panel** ni se añade al modelo/summary/API del
dashboard. No hay exporter, Observe, reload, BD, migración ni despliegue. Cambio
aditivo de telemetría: consumidores por allowlist pueden incorporar el nombre;
rollback de este contador elimina el instrumento sin cambiar HTTP ni configuración.
Los defaults siguen intactos. RF11 sigue parcial; no acredita RNF03 ni producción.

Las pruebas manuales monotónicas con StackTrace/checkpoint reutilizan la técnica
histórica sensible al JIT: no prueban entrega real del timer. Sin benchmark/sizing
ni browser nuevos (UI intacta).

Validación sintética local MVP28: `dotnet build ProyectoSeguridad.slnx -c Release
--no-restore` PASS, cero errores/advertencias. Focal Release con filtro
`FullyQualifiedName~SecondaryPan|FullyQualifiedName~PrivacyTelemetryTests|FullyQualifiedName~MultiRuleTransformTests|FullyQualifiedName~DashboardTests`:
**355/355 PASS**; `dotnet test ProyectoSeguridad.slnx -c Release --no-build
--no-restore`: **830/830 PASS**, cero omitidas. Incluye HTTP real Mask/Block y
barrera terminal `Duration.Samples >= N` **y** presupuesto `0/0`. Primer build
falló por una asignación test-only a propiedad init; corregido, sin cambiar contrato.
Revisión independiente estática **APPROVED**, sin hallazgos que requieran cambios;
no implica aprobación de producto ni de producción.

Validación sintética local MVP27: build Release de solución sin errores/advertencias,
**81/81** pruebas focalizadas y **765/765** suite completa PASS, cero omitidas.
Sin benchmark/sizing, cambios frontend ni aprobación para producción.

### Transformaciones opt-in (MVP-25, solo datos sintéticos)

`PanPolicies:Policies:0:Transformation` admite únicamente los strings exactos
`PAN`, `Email` y `Phone`. Omitirlo conserva `PAN`; null explícito, vacío,
espacios, diferencias de mayúsculas, números y valores desconocidos impiden el
arranque. `PolicyId` sigue siendo un identificador arbitrario, no el tipo de
transformación. El catálogo congela estas opciones al arrancar; cambiarlo requiere
reiniciar. No cambia ningún appsettings distribuido ni los defaults PAN / Mask.

Ejemplo de configuración **opt-in** por CLI (upstream local sintético que devuelva
`{"contact":{"email":"user@example.test"}}` en la ruta protegida):

```powershell
dotnet run --project Gateway -- --urls http://127.0.0.1:5100 --PanPolicies:Policies:0:Transformation Email --PanPolicies:Policies:0:Selector '$.contact.email' --PanPolicies:CoverageContract:Required:0:Selector '$.contact.email'
```

El mismo conjunto de claves puede inyectarse con `AddInMemoryCollection` en un
host de pruebas. Para Phone, usar `Transformation Phone` y el selector
`$.contact.phone` **también en CoverageContract**; un valor sintético es
`+1-809-555-0199` → `+*-***-***-0199`. Email produce
`user@example.test` → `***@example.test`. `/payment` y `Sample` de TestApi conservan
la demo PAN. El endpoint nuevo `/customer` contiene exclusivamente PAN, email y
teléfono sintéticos. No usar datos reales.

El selector/transformación principal permanece obligatorio/default PAN; las reglas
adicionales opt-in de MVP26 se describen abajo. Solo transforma strings
seleccionados en JSON elegible; null pasa, otro tipo terminal causa 502 de
inspección sin salida parcial. En Mask, cada string sustituido cuenta una máscara,
incluida redacción completa. En Block, **cualquier string**, incluso vacío o
incompatible, bloquea sin ejecutar máscaras; null/no-match no bloquean y un error
posterior de inspección tiene precedencia. La demo local del panel sigue siendo
una simulación fija Mask/Block, no una configuración de estas transformaciones.

Formatos conservadores explícitos, **no detección ni validación universal**:

- Email: ASCII dot-atom, total 1..254 caracteres, local 1..64; letras/dígitos y
  `!#$%&'*+-/=?^_` más acento grave, `{|}~` y puntos no iniciales/finales/consecutivos.
  Exactamente un `@`; dominio de etiquetas DNS ASCII de 1..63 caracteres alfanuméricos
  o guiones interiores, sin etiquetas vacías ni punto final; acepta una sola etiqueta
  y punycode por formato, sin consulta DNS. Conserva el dominio exactamente y sustituye
  todo el local por `***`, incluso local de un carácter. No admite quoted local,
   display name, literales de dirección entre corchetes, Unicode ni whitespace.
   La gramática permite etiquetas numéricas, incluido `a@127.0.0.1` →
   `***@127.0.0.1`: no interpreta ni resuelve el dominio como una dirección IP.
- Phone: 1..64 caracteres, 5..15 dígitos ASCII, espacios/guiones internos y `+`
  opcional solo inicial; comienza con dígito o `+` y termina en dígito. Conserva
  plus/separadores y los últimos cuatro dígitos. No admite paréntesis, extensiones,
  plus repetido ni Unicode; no garantiza E.164 ni validez internacional.
- Cualquier valor incompatible o demasiado corto/largo se sustituye completamente
   por `[REDACTED]`. PAN conserva su algoritmo previo: más de cuatro dígitos ASCII,
   espacios/guiones permitidos, sin plus; últimos cuatro visibles.

Estas máscaras no anonimizan: el dominio completo y los últimos cuatro dígitos
siguen visibles; por ejemplo, `12345` → `*2345` oculta solo un dígito.

El summary añade solo `policies[].transformation` a la allowlist (cambio aditivo,
`schemaVersion: 1`), sin selectores ni valores; no añade tags/outcomes de telemetría.
Rollback: **retirar la clave Transformation antes de volver al binario anterior**,
que rechaza esa opción desconocida; restaurar selector y coverage PAN coincidentes
si se pretende recuperar la demo original. No requiere migración de datos.

Validación MVP-25: build Release sin errores/advertencias, focalizados
**103/103 PASS**, suite completa **549/549 PASS**, sin omitidos; Edge headless
**6/6 PASS**, incluida la columna Transformación con Email configurado.
Revisión independiente estática de código **APPROVED** y de seguridad
**APPROVED_WITH_WARNINGS** (privacidad residual y alcance limitado de las gramáticas).
Solo validación sintética local: sin despliegue, aceptación RNF03 ni aprobación
para datos reales. La configuración y demostración predeterminadas siguen en PAN.

### Varias reglas opt-in por la misma política/ruta (MVP-26)

Implementación validada localmente con datos sintéticos; no implica aprobación de producto.
`Selector` principal sigue siendo obligatorio y `Transformation` principal sigue
siendo PAN al omitirse. `AdditionalRules` ausente equivale a lista vacía; permite
0..15 reglas `{Selector, Transformation}` (1..16 reglas totales), con PAN por
defecto al omitir la transformación adicional. Los tipos admitidos son los mismos
strings exactos PAN/Email/Phone; no se deducen de PolicyId. MVP26 no añade detectores
(el detector opt-in MVP27 se describe arriba),
Observe, reload, persistencia ni editor nuevo.

Ejemplo sintético opt-in, conservando el selector PAN distribuido. Mantener TestApi
en otra terminal (`dotnet run --project TestApi -- --urls http://127.0.0.1:5101`).
Detener primero cualquier Gateway anterior con Ctrl+C y ejecutar en su terminal:

```powershell
dotnet run --project Gateway -- --urls http://127.0.0.1:5100 `
  --PanPolicies:Policies:0:Version v2 --PanPolicies:CoverageContract:Version v2 `
  --PanPolicies:Policies:0:AdditionalRules:0:Selector '$.contact.email' `
  --PanPolicies:Policies:0:AdditionalRules:0:Transformation Email `
  --PanPolicies:Policies:0:AdditionalRules:1:Selector '$.contact.phone' `
  --PanPolicies:Policies:0:AdditionalRules:1:Transformation Phone `
  --PanPolicies:CoverageContract:Required:1:RouteId protected-payment `
  --PanPolicies:CoverageContract:Required:1:Selector '$.contact.email' `
  --PanPolicies:CoverageContract:Required:2:RouteId protected-payment `
  --PanPolicies:CoverageContract:Required:2:Selector '$.contact.phone'
```

El servidor mantiene ocupada esa terminal. Desde una tercera terminal:

```powershell
curl.exe http://127.0.0.1:5100/protected/customer
```

La ruta YARP sigue siendo `protected-payment`: la política se aplica a **todo**
`/protected/{**rest}`, no solo a `/customer`. El origen `/customer` devuelve
`{"payment":{"cardNumber":"4111111111111111"},"contact":{"email":"user@example.test","phone":"+1-809-555-0199"}}`.
La ruta passthrough y el origen directo siguen sin protección.

Los selectores conservan 1..16 pasos y hasta 1041 caracteres. Se rechazan
duplicados y prefijos por pasos ordinales entre todas las reglas, en cualquier
orden: `$.a` frente a `$.a.b` y `$.a[*]` frente a `$.a[*].email` se solapan;
`$.a` frente a `$.ab`, y `$.a.b` frente a `$.a[*].b`, no. Sigue habiendo una
política por RouteId y hasta 32 políticas.

CoverageContract mantiene 1..32 requisitos mínimos; ahora la unicidad corresponde
al **par ordinal RouteId/Selector**, no a RouteId solo. Cada par debe coincidir con
la regla principal o una adicional de la política de esa ruta. El ejemplo declara
los tres campos en coverage para detectar la pérdida o cambio de cualquiera de esas
reglas; el contrato permite otras reglas válidas no incluidas en ese mínimo.
No se exige ni promete cobertura completa de
todos los campos. Las versiones de política/coverage las suministra el operador:
revisarlas explícitamente al cambiar el contrato, sin incremento automático ni
cambios en appsettings distribuido.

Con varias reglas se construye un trie local por respuesta y se realiza una sola
pasada de transformación del DOM, preservando propiedades repetidas. El máximo
257 nodos (1 + 16 × 16) **no** es límite total de asignaciones ni de memoria. Sin
adicionales se conserva el recorrido previo. Modo, lease, deadline y métricas son
por política; Block considera cualquier string seleccionado y los fallos tardíos
prevalecen, sin contar máscaras parciales ni entregar el original.

El summary conserva `transformation` principal y añade `ruleCount` y
`transformations` (tipos distintos en orden principal/adicionales), schemaVersion1,
sin selectores/valores ni tags nuevos. El panel conserva seis columnas y muestra,
por ejemplo, `PAN, Email, Phone · 3 reglas`, mediante textContent. Si ambos campos
nuevos faltan acepta el contrato anterior de una regla; presencia parcial o datos
inválidos se rechazan. La simulación de la UI sigue fija y no configura reglas.

Antes de rollback a MVP25, **retirar AdditionalRules y los requisitos dependientes**;
el binario anterior no reconoce la clave. Recuperar el comportamiento legacy no
equivale a proteger varios campos. No hay migración de datos ni despliegue.
El constructor/deconstruct de cinco argumentos de `CompiledPanPolicy` se conserva.
Consumidores binarios del constructor anterior de `PanPolicyCatalog` deben
recompilarse: añadir el parámetro opcional conserva compatibilidad fuente, no su
firma IL anterior. No se conocen consumidores binarios externos en este proyecto.

El contrato se aplica a la **configuración efectiva normalizada por IConfiguration**,
no a los tipos JSON originales. Usar ausencia o array JSON como formas recomendadas.
Ausencia, `[]` y `""` equivalen a cero adicionales. Un objeto con claves canónicas
contiguas `"0"`...`"n-1"` equivale al array de reglas en ese orden; sus reglas se
validan, congelan y ejecutan, nunca se omiten. Son aliases explícitos porque el
proveedor estándar aplana esas formas de manera indistinguible.
Se rechazan malformas observables: null conservado por el proveedor estándar,
escalares no vacíos, huecos/índices no canónicos, valores escalares junto a hijos,
elementos inválidos y campos desconocidos/anidados. No se promete recuperar
información descartada por proveedores arbitrarios. Coverage sigue exigiendo cada
par declarado: una lista vacía no permite omitir una regla que coverage requiera.

Validación MVP-26: build Release sin errores/advertencias; focalizada **135/135 PASS**
y suite completa **684/684 PASS**, sin omitidos. Edge headless **8/8 PASS**:
tres campos realmente enmascarados vía YARP, métricas de tres máscaras, tabla de
tipos/cantidad, fallback legacy y 13 particiones de metadata inválida, recuperación,
vista móvil y barreras CSP/cross-origin. La prueba HTTP espera publicación terminal
y liberación del lease: recibir el cuerpo no garantiza que las métricas ya estén
publicadas. Revisión independiente de código y seguridad **APPROVED_WITH_WARNINGS**;
sin bloqueantes locales. Persisten privacidad residual, límites de configuración
normalizada, observadores de checkpoints sensibles a implementación y restricciones
locales del panel. No certifica SLA, sizing, producción ni accesibilidad exhaustiva.

### Panel local de consulta (MVP-24)

Tras ejecutar Gateway como se indica arriba, abrir [panel local](http://127.0.0.1:5100/dashboard/)
en el listener existente. HTML, JS y CSS
se sirven embebidos, sin CDN; `GET /dashboard/api/summary` devuelve schemaVersion 1,
inicio UTC, salud local, metadatos permitidos de políticas Mask/Block y totales de
inspección por instancia desde su construcción. No expone selectores, destinos,
URLs, cuerpos, PAN, tokens, excepciones ni correlaciones. No hay edición, BD,
CORS, exporter ni datos reales. La demo es una **SIMULACIÓN** fija en el navegador:
no llama al upstream ni incrementa métricas.

Solo admite cliente IP loopback y Host `localhost` exacto o IP loopback (puerto
permitido solo si coincide con el puerto local efectivo; sin puerto usa 80/443
según HTTP/HTTPS); rechaza nombres arbitrarios, punto final, zonas IPv6,
subdominios `.localhost` e IPv4-mapped
con 403 genérico antes de consultar datos/recursos. No sustituye autenticación
frente a otros procesos locales: **sin autenticación, cualquier proceso local puede
consultarlo**. Rechaza `Sec-Fetch-Site: cross-site` y Origin presente distinto del
origen validado (sin paths, userinfo ni sufijos); no confía en forwarded headers.
Usa no-store, nosniff, no-referrer, CORP same-origin y CSP restrictiva.
HEAD no entrega cuerpo; otros métodos se rechazan sin mutaciones.

Los acumuladores tienen memoria fija, no retienen eventos ni muestras individuales
y no dependen de MeterListener. Se actualizan antes de callbacks externos; los
instrumentos, tags y cleanup conservan su contrato. Entrada/salida cuentan solo
marcas nullable existentes (incluido cero); máscaras solo conteos positivos
validados. Duración muestra conteo/suma, no percentiles. Snapshots y gauges son
observacionales, no una transacción con HTTP ni entre totales y capacidad.
Ready conserva el contrato de health: no indica upstream disponible ni capacidad.
El panel queda fuera de admisión proxy y no contacta al upstream.
Los totales long/double saturan en su máximo finito en vez de hacer wrap; tras
saturación no conservan exactitud incremental. Presupuesto independiente fijo de
**4 solicitudes dashboard concurrentes**, sin cola: exceso devuelve 503 vacío,
sin detalles, y el permiso se libera en finally solo al retornar el trabajo real
de escritura (no por timer ni cancelación). No modifica límites Kestrel ni proxy.
Permanecen los defaults de transporte Kestrel: conexiones concurrentes sin límite
explícito y timeouts/rates predeterminados del framework. Este límite de aplicación
no es una garantía dura de memoria, conexiones o disponibilidad: un proceso local
puede retener IO/conexiones y causar DoS residual. Los enlaces `evidence/` históricos
son evidencia local ignorada, no se publican desde el panel ni se versionan aquí.

Compatibilidad aditiva: incorporar binario con recursos embebidos y reiniciar;
sin migraciones, dependencias ni configuración nueva. Sin despliegue realizado
ni autorización para datos reales o producción.

Validación local: build Release sin errores/advertencias, focalizados **129/129**
y suite completa **502/502 PASS**, sin omitidos. Edge headless: **6/6 PASS**
(carga real, demo aislada, error/recuperación, disposición a 320 px, errores JS/CSP
y lectura cross-origin denegada). Revisión independiente estática de código y
seguridad: **APPROVED_WITH_WARNINGS**, sin bloqueantes para este alcance local.
Los números mayores que `Number.MAX_SAFE_INTEGER` pueden mostrarse redondeados
en el navegador; los acumuladores del servidor conservan su contrato de saturación.
No se certifica accesibilidad exhaustiva con lector de pantalla ni rendimiento.

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
- `PolicyId`, `Version` y `RouteId`: 1–64 caracteres ASCII, patrón `[A-Za-z0-9][A-Za-z0-9._-]{0,63}`; RouteId único con comparación ordinal y existente en el mismo conjunto de RouteConfig entregado a YARP (`protected-payment` o `passthrough` en esta demo). Una ruta inexistente impide Build/Start, nunca queda una política silenciosamente sin usar. `Mode` es obligatorio y acepta exclusivamente los textos exactos `Mask`, `Block` y `Observe`; números, distinta capitalización, espacios y vacío se rechazan. La gramática acota longitud y caracteres, no acredita ausencia semántica de secretos. Con reload, versiones sucesivas pueden acumular valores distintos del tag `gateway.policy.version` durante el proceso: no hay un límite global de cardinalidad entre publicaciones. Antes de añadir exporters debe definirse su tratamiento operativo.
- Selector estructural acotado (no JSONPath completo): `$` seguido de 1 a 16 pasos, cada uno una propiedad `.[A-Za-z_][A-Za-z0-9_]{0,63}` o comodín de array `[*]` explícito; máximo total 1041 caracteres. Se conservan los límites operativos anteriores, contando cada comodín como un paso. Ejemplos: `$.payment.cardNumber`, `$.payments[*].cardNumber`, `$.cards[*]`, `$.groups[*][*].cardNumber` y `$[*].cardNumber` para array raíz. Sin índices, filtros, descenso recursivo, comodín de propiedades, comillas ni escapes. Comparación ordinal case-sensitive; se visitan todos los duplicados y todos los elementos de arrays seleccionados. Un paso de propiedad solo recorre objetos y `[*]` solo arrays: otro tipo antes del terminal no coincide y se conserva. Arrays vacíos no cambian. Terminal `null` pasa; string aplica Mask o Block según Mode; otro tipo terminal falla cerrado para toda la respuesta, sin publicar una transformación parcial. Se comprueba cancelación/deadline por elemento; no cambia la reserva ni concurrencia.
- `CoverageContract` es obligatorio: `Version` usa el mismo formato seguro de identificadores y `Required` contiene entre 1 y 32 pares RouteId/Selector. Cada RouteId debe ser conocido; la unicidad es por par RouteId/Selector, permitiendo varios selectores de la misma ruta. Cada selector usa la gramática anterior y cada par debe coincidir **exactamente (ordinal, case-sensitive)** con la regla principal o una adicional de la política efectiva. Cambiar solo ruta o selector, omitir cobertura, duplicar pares o configurar un contrato inválido impide Build/Start. Es cobertura **mínima**, no necesariamente completa. La versión identifica el contrato; no debe coincidir necesariamente con la versión de cada política y la suministra el operador.
- El contrato detecta desviaciones de las políticas respecto a una cobertura declarada, no descubre dónde existe PAN. **Cambiar conjuntamente política y contrato es un cambio de seguridad que requiere revisión** (y versionado explícito del contrato). No protege frente a quien controla ambos archivos/configuración o sus sobrescrituras: ese control está dentro del límite de confianza. El valor distribuido exige `protected-payment` + `$.payment.cardNumber`; migrar configuraciones anteriores requiere añadir explícitamente el contrato antes de iniciar esta versión.
- `MaxBodyBytes`: entero obligatorio de 1 a **16.777.216** bytes inclusive. El techo operativo de 16 MiB acota el buffering por respuesta; no constituye un presupuesto global de memoria ni cambia el valor aprobado por defecto de 1 MiB.
- Cada catálogo es inmutable por RouteId y segmenta selectores al compilarse. **Sin BD**; por defecto se usa `IOptions`, no monitor/snapshot, y cambiar configuración requiere reiniciar. El opt-in `LocalPolicySnapshotFile` de MVP35 permite intercambiar catálogos completos dentro de sus restricciones fijas; no recarga proveedores ordinarios de configuración ni rutas YARP. Las fuentes estándar de configuración .NET pueden sobrescribir valores antes del arranque, sujetos a la misma validación. Las rutas YARP de esta demo siguen definidas en código; cambiar RouteId asigna una política a una ruta, no crea rutas nuevas. Rutas sin política son passthrough.

## RF07 Block (MVP-20, opt-in local)

`Mode` admite exclusivamente los textos exactos `Mask` y `Block`. El default
distribuido sigue siendo **Mask**, con versiones, cobertura, selector, presupuestos
y límites intactos. `Block` bloquea ante **cualquier string seleccionado**, incluso
vacío, corto o no-PAN: no es un detector ni aplica Luhn. `null` pasa; otro tipo
terminal sigue siendo fallo de inspección. No hay modo Observe ni fail-open.

Una coincidencia válida de Block devuelve **502** con JSON UTF-8
`{"code":"privacy_response_blocked"}`, `Cache-Control: no-store` y sin headers
upstream ni bytes originales/enmascarados. Se termina la captura, parseo y validación
de todos los terminales antes de decidir: JSON inválido o un terminal inválido,
incluso posterior a un string en arrays o duplicados, prevalece con
`privacy_inspection_failed`. No se corta el recorrido al encontrar un string.
Sin coincidencia o con solo null se conservan bytes y status originales. HEAD,
204/304 y tipos excluidos conservan su semántica; Block **no protege passthrough
ni non-JSON**. Cancelación, deadline y errores de escritura/internos no se
convierten en bloqueos de política; leases y admisión duran hasta terminar el trabajo.

Telemetría añade únicamente `policy_blocked` al listado cerrado de outcomes.
`blocked` conserva su significado histórico de fallo de inspección; sin match
se registra `inspected`. No cambian nombres, unidades ni tags; cada inspección
admitida intenta publicar exactamente un outcome y una duración terminales,
con precedencia de errores y aislamiento de consumidores existentes.

Compatibilidad: actualizar primero consumidores que enumeren outcomes para aceptar
`policy_blocked`, luego actualizar el binario y reiniciar; activar Block requiere
cambiar explícitamente Mode. Para rollback, volver a Mask antes de usar un binario
anterior (rechaza Block), conservando el label histórico en consumidores. Sin
migraciones de BD, paquetes, reload, panel ni exporter nuevos; no se realizó despliegue.
Las pruebas son sintéticas locales, no aceptación **RNF03**, aprobación de producto
ni certificación/sizing de producción. Revisión independiente estática: **APPROVED**;
build Release sin errores/advertencias y suite completa **384/384 PASS**, sin omitidos.

## Comportamiento por defecto (preservado de MVP-01)

- Ruta YARP `protected-payment`: `/protected/{**rest}` elimina `/protected` al reenviar. Solo inspecciona respuestas `application/json` (media type insensible a mayúsculas, admite `charset=utf-8`); incluye errores HTTP JSON. Otros tipos pasan sin inspección.
- Modo **Mask**, selector distribuido sin cambios y sensible a mayúsculas `$.payment.cardNumber`. No busca PAN en otros campos ni dentro de arrays sin `[*]` explícito. Se procesan **todas** las propiedades seleccionadas duplicadas. Usar selectores de arrays requiere configurar el mismo par RouteId/Selector exacto en el contrato de cobertura y reiniciar; no hay migración ni cambio de defaults/versiones distribuidos.
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
| `gateway.privacy.inspection.input_body_bytes` | `Histogram<long>` | `By` |
| `gateway.privacy.inspection.output_body_bytes` | `Histogram<long>` | `By` |
| `gateway.privacy.inspection.masks` | `Counter<long>` | `{mask}` |
| `gateway.privacy.inspection.secondary_pan_matches` | `Counter<long>` | `{match}` |
| `gateway.privacy.inspection.explicit_sensitive_matches` | `Counter<long>` | `{match}` |
| `gateway.privacy.inspection.active` | `ObservableGauge<int>` | `{inspection}` |
| `gateway.privacy.inspection.reserved_body_bytes` | `ObservableGauge<long>` | `By` |

- Los outcomes son únicamente `inspected`, `blocked`, `capacity_exceeded`,
  `canceled`, `write_error`, `faulted`, `timed_out` y `policy_blocked`.
  `blocked` significa fallo de inspección; `policy_blocked`, bloqueo por política Block.
  Cada operación JSON protegida que llega
  al control de capacidad registra un solo outcome terminal. La duración se
  registra solo si se adquirió el lease; usa el timestamp conservador previo al
  intento de adquisición e incluye escritura downstream y cleanup (véase MVP-09).
- Counter e histogramas usan exclusivamente los tags acotados
  `gateway.route.id`, `gateway.policy.id`, `gateway.policy.version` y
  `gateway.privacy.outcome`; los gauges no añaden tags. Nunca se incluye cuerpo,
  PAN, selector, URL/query ni excepción.
- Los gauges son valores por instancia obtenidos de `InspectionBudget.Snapshot`.
  HEAD, 204, 304, respuestas no JSON y rutas sin política no emiten outcomes.

Sigue siendo un MVP local con datos exclusivamente sintéticos y no está
aprobado para producción.

### Tamaño de entrada capturada completa (MVP-21, RF11 parcial)

`gateway.privacy.inspection.input_body_bytes` mide bytes reales de entrada, no
caracteres, Content-Length, salida, reserva ni hallazgos. Solo se marca la captura
tras llegar a EOF dentro del límite y comprobar el deadline, antes de validar
UTF-8, parsear JSON o recorrer terminales. EOF vacío registra **0** aunque luego
falle JSON; **ausencia** de muestra significa que no se completó una captura elegible,
no tamaño cero ni total inferido.

Se intenta publicar una muestra terminal en `Complete`, junto al outcome y duración,
con el mismo outcome final: `inspected`, `policy_blocked`, `blocked`, `canceled`,
`timed_out`, `write_error` o `faulted`. Fallos posteriores a captura (incluidos
UTF-8/JSON/terminal inválidos, cancelación y escritura) conservan la muestra.
No se publica por capacidad, HEAD/204/304/non-JSON/rutas sin política, rechazo
prelectura (charset, encoding, 206 o tamaño declarado), exceso real, lectura
fallida/cancelada o deadline antes de completar captura. No es una distribución
de todos los cuerpos recibidos: excluye deliberadamente capturas incompletas.

Usa el mismo Meter y versión y exclusivamente las cuatro tags acotadas anteriores.
Cada instrumento aísla errores ordinarios del consumidor de forma independiente;
ninguno impide intentar los otros ni liberar el lease. La publicación es best
effort, no atómica, y callbacks bloqueantes conservan las limitaciones de MVP-12.
No hay callbacks de trabajo nuevos ni liberación anticipada de capacidad.

Compatibilidad aditiva: sin cambiar instrumentos existentes, dependencias,
configuración, defaults, límites o modos; sin migraciones. Los consumidores que
filtren nombres pueden incorporar el nuevo histograma antes de actualizar binario
y reiniciar; rollback simplemente deja de emitirlo. No se ejecutó despliegue,
exporter ni campaña de sizing. MVP-21: build Release sin errores/advertencias,
suite completa **410/410 PASS** sin omitidos y revisión independiente estática
**APPROVED**. RF11 completo sigue pendiente; esto no acredita RNF03 ni aprobación
del producto.

### Tamaño de salida preparada (MVP-22, RF11 parcial)

`gateway.privacy.inspection.output_body_bytes` mide la longitud exacta en bytes
del cuerpo final preparado para escritura, no caracteres, bytes entregados al
cliente, tamaño de entrada ni memoria reservada. Se marca inmediatamente antes
de `WriteAsync`, después de preparar los headers y superar el último chequeo
de deadline/cancelación. Incluye el cuerpo enmascarado, el original conservado
sin coincidencias y los errores genéricos de inspección o de política Block;
nunca mide el buffer transformado descartado por Block.

Solo se intenta publicar una muestra terminal en `Complete`, tras el cleanup,
con el outcome final y junto a los otros instrumentos. Una escritura pendiente
no publica todavía; si luego falla, se cancela o vence el deadline, se conserva
el tamaño preparado con `write_error`, `canceled` o `timed_out`. La muestra no
certifica entrega total ni parcial al cliente.

No hay muestra si no se alcanza el punto de marca: rechazo por capacidad,
HEAD/204/304/non-JSON/passthrough o terminación anterior por cancelación, deadline
o fallo. Ausencia no significa cero. Un rechazo prelectura o captura incompleta
sí puede registrar salida si se prepara y se intenta escribir el error genérico,
aunque no exista muestra de entrada. No es una distribución de todo el tráfico.

Usa el Meter/version actuales y exclusivamente las cuatro tags acotadas; no
añade cuerpos, valores, selectores ni identificadores. Las cuatro publicaciones
(outcome, duración, entrada y salida) aíslan independientemente excepciones
ordinarias del consumidor. Son best effort y no atómicas; callbacks bloqueantes
mantienen las limitaciones existentes, sin liberar capacidad anticipadamente.

Compatibilidad aditiva: consumidores pueden incorporar el nombre antes de
actualizar el binario y reiniciar; rollback deja de emitirlo. Sin migraciones,
dependencias, cambios HTTP, configuración, defaults, modos ni límites nuevos.
No añade contadores de máscaras/hallazgos ni detección, Observe, exporter o BD.
No hubo benchmark, sizing ni despliegue. MVP-22: build Release de la solución sin
errores/advertencias, focalizados **124/124 PASS** y suite completa **431/431 PASS**,
sin omitidos; revisión independiente estática **APPROVED**. RF11 sigue parcial; no acredita
RNF03 ni aprobación del producto.

### Máscaras calculadas validadas (MVP-23, RF11 parcial)

`gateway.privacy.inspection.masks` cuenta sustituciones string calculadas por
Mask, no hallazgos/detección PAN ni máscaras entregadas. Cada terminal string
seleccionado cuenta, incluido vacío, corto o incompatible; se recorren todos los
duplicados y arrays. `null`, ausencia de coincidencias y Block no cuentan.

Solo se conserva el conteo tras terminar el recorrido completo válido y superar
su `deadline.Check`. Un fallo tardío de traversal descarta todo conteo parcial.
Una vez validado, fallos posteriores en checkpoint diagnóstico, headers, escritura,
cancelación o deadline conservan el conteo. Solo se intenta `Add` si es positivo,
en `Complete` tras cleanup, con el outcome final y el mismo Meter/version y las
cuatro tags acotadas. Una escritura/limpieza pendiente aún no publica. Cada
instrumento aísla independientemente excepciones ordinarias del consumidor; la
publicación sigue siendo best effort, no atómica, sin aislar callbacks bloqueantes.

Sin muestra por capacidad, exclusiones, passthrough, prelectura o invalidez antes
de la marca. Ausencia no equivale a cero ni a ausencia de PAN. Compatibilidad
aditiva: consumidores con filtros de nombre pueden incorporar el instrumento
antes de actualizar binario y reiniciar; rollback deja de emitirlo. Sin cambios
HTTP, modos, defaults, tags, límites, dependencias ni migraciones. Sin exporter,
benchmark, sizing ni despliegue. Build Release sin errores/advertencias,
focalizados **139/139 PASS** y suite completa **446/446 PASS**, sin omitidos.
Revisión independiente estática **APPROVED_WITH_WARNINGS**: el test
`Masks_require_post_traversal_deadline_check` observa el límite mediante StackTrace
y depende del JIT/inlining/runtime; revisar ese observador al actualizar runtime.
RF11 sigue parcial y esto no aprueba el producto ni RNF03.

### Aislamiento de consumidores de métricas (MVP-12)

La publicación del contador y de los histogramas es best effort e independiente:
una excepción ordinaria de un consumidor síncrono no cambia la respuesta HTTP,
la cancelación ni el error original, y no impide intentar los demás instrumentos.
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
