# Gateway.LoadHarness — MVP-05, MVP-06 y MVP-07

Harness local y reproducible para medir carga y memoria del gateway con datos
exclusivamente sintéticos. No es un benchmark de producción, no despliega servicios,
no configura exporters y no utiliza tarjetas reales.

## Construcción y comandos

```powershell
dotnet build Gateway.LoadHarness/Gateway.LoadHarness.csproj -c Release
$harness = "Gateway.LoadHarness/bin/Release/net10.0/Gateway.LoadHarness.dll"

dotnet $harness --help
dotnet $harness memory --output artifacts/mvp05/memory-run
dotnet $harness http --output artifacts/mvp05/http-run --requests-per-concurrency 16
dotnet $harness all --output artifacts/mvp05/all-run --requests-per-concurrency 16
```

Los comandos deben iniciarse desde la raíz local del repositorio, identificada por
`ProyectoSeguridad.slnx`; en Windows se exige una unidad fija. El destino debe
permanecer bajo `artifacts/mvp05/`. Se rechazan escapes y cualquier reparse point
existente desde el destino hasta la raíz del sistema, incluidos `artifacts`, el
repositorio y sus ancestros. `--requests-per-concurrency` acepta de 8 a 100.000
solicitudes y su valor predeterminado es 16. Los comandos tienen timeouts acotados y
devuelven un código distinto de cero si falla un argumento, timeout o invariante.
`artifacts/mvp05/` está ignorado por Git.

## Modo `memory`

El coordinador crea un proceso worker nuevo para cada concurrencia 1, 2, 4 y 8.
Cada worker:

1. Genera y valida antes del baseline un payload `token-dense-v1` de exactamente
   1.048.576 bytes.
2. Calienta el transform y fuerza una GC completa para el baseline.
3. Detiene las inspecciones en un checkpoint interno `output-ready`, después de
   crear la salida y mientras captura, DOM y stream transformado siguen en scope.
4. Toma muestras naturales y posteriores a GC en `output-ready` y durante una
   escritura downstream bloqueada que no copia el cuerpo.
5. Verifica que la reserva sea exactamente `N × 1 MiB`.
6. Con ocho inspecciones, exige que una novena termine con 503 sin leer el origen y
   sin alterar la reserva 8/8 MiB.
7. Libera las escrituras, comprueba máscara/longitud, outcomes y duraciones,
   `Snapshot == (0, 0)` y un canario final.

El checkpoint es un constructor `internal` accesible solo a assemblies friend. La
ruta productiva de DI usa el constructor público sin callback; no existe opción en
`appsettings.json` ni cambio de comportamiento configurado.

`memory.json` es la fuente canónica y `memory.md` se deriva del mismo modelo. Incluye
runtime y características genéricas del proceso, escenario, muestras por fase,
contadores GC, heap administrado, committed/fragmentación, bytes asignados
acumulados, working set, private bytes y reserva lógica.

## Modo `http`

Levanta Kestrel para API sintética y Gateway, y realiza peticiones completas por el
recorrido Cliente → YARP → API en loopback. Para concurrencias 1/2/4/8 registra:

- total y códigos HTTP;
- solicitudes por segundo;
- latencias p50, p95 y máxima mediante reloj monotónico;
- outcomes y duraciones emitidos;
- máximos observados de memoria y reserva;
- liberación exacta del presupuesto después de cada escenario.

Las latencias proceden de carga closed-loop y no tienen SLO. API, Gateway y cliente
comparten proceso; por ello la memoria HTTP no aísla el gateway. El sampler de 10 ms
también perturba la medición y puede omitir transitorios.

## Interpretación

Los reportes declaran `MEASURED_NOT_SIZED` porque no existe un límite de memoria por
instancia ni porcentaje de headroom acordado. Los 8 MiB reservados son límites de
entrada, no heap real. Además:

- `GC.GetTotalMemory`, heap GC, working set y private bytes no son equivalentes;
- los bytes asignados acumulados no representan memoria viva;
- una GC forzada modifica el estado y solo se usa en el modo determinista;
- LOH, pools, JIT y el runtime pueden conservar memoria legítimamente;
- que working set/private bytes no vuelvan al baseline no prueba una fuga;
- loopback omite TLS, autenticación, red, exporters y clientes reales.

No se deben cambiar los defaults 8/8 MiB únicamente con estos resultados. Para una
decisión de sizing hacen falta un límite por instancia, headroom, infraestructura y
carga representativas, varias repeticiones y criterios operativos acordados.

## Privacidad de los artefactos

Los JSON y Markdown no guardan payload, PAN, selector, URL, hostname, usuario, rutas
personales ni textos de excepción. Solo contienen identificadores fijos del perfil,
revisión compilada, runtime/SO genérico y valores de medición.
## MVP-06: sizing local aislado

```powershell
dotnet build Gateway.LoadHarness/Gateway.LoadHarness.csproj -c Release --no-restore
dotnet Gateway.LoadHarness/bin/Release/net10.0/Gateway.LoadHarness.dll isolated --output artifacts/mvp05/mvp06-smoke --requests-per-concurrency 8 --repetitions 1
dotnet Gateway.LoadHarness/bin/Release/net10.0/Gateway.LoadHarness.dll isolated --output artifacts/mvp05/mvp06-local
```

`isolated` es independiente de `all`; los comandos existentes y los defaults de la
aplicación no cambian. Las opciones usan enteros decimales estrictos e invariantes:
64 solicitudes por ejecución (8..100000), tres repeticiones (1..10), 512 MiB
(64..65536) y 30% de headroom (1..90). El output conserva la validación local bajo
`artifacts/mvp05`.

Candidato elegido: 512 MiB por gateway con 30% de headroom. El máximo efectivo de
Private Bytes es `floor(536870912 × 70 / 100)` = **375809638 bytes**. Es un umbral
observacional de aceptación, no un límite aplicado por el SO o un contenedor.

El cliente coordinador, el worker persistente de API sintética y cada gateway están
en procesos separados. Cada repetición recorre por defecto concurrencias 1, 2, 4 y 8 con un
gateway nuevo. Cada ejecución reutiliza un `HttpClient` sin proxy, calienta con dos
respuestas enmascaradas consumidas por completo, reinicia métricas sin forzar GC y
envía la cantidad exacta de solicitudes closed-loop. El cuerpo `token-dense` mide
exactamente 1 MiB. Toda respuesta medida debe ser 200, conservar longitud, estar
enmascarada y no contener el PAN sintético. p50/p95 usan nearest-rank; no hay p99.

Los comandos internos `api-worker` (sin opciones) y `gateway-worker --upstream <loopback-origin> --max-concurrent-inspections N`
reservan stdout para JSON-lines compacto. Las direcciones permanecen solo en memoria.
El protocolo admite `stop`; gateway exige además un `reset` tras dos warmups y un
`report` con el conteo esperado. El reporte espera hasta cinco segundos por presupuesto
0/0 y telemetría suficiente, y luego exige outcomes/duraciones exactos. stderr se drena
sin reportarlo. El cleanup acotado mata el árbol si no puede cerrar: timeout global de
diez minutos, dos minutos por run y treinta segundos por comando.

El gateway se automuestrea cada 10 ms y conserva máximos independientes de managed
memory, working set, Private Bytes, inspecciones activas y bytes reservados. No hay
muestreo del padre ni GC forzada. La memoria retenida del warmup permanece incluida y
los picos más breves pueden no observarse. Los agregados por concurrencia incluyen
máximo/mediana de Private Bytes, máximos managed/working set y medianas de throughput,
p50 y p95. Deben pasar todas las repeticiones; un run abortado falla explícitamente.

`isolated.json` (`gateway.mvp06.isolated.v1`) y `isolated.md`, derivado del mismo
modelo, se guardan atómicamente. Omiten direcciones, PID, payloads, selectores y
diagnósticos de excepciones. `SIZED_LOCAL_CANDIDATE` solo indica que el candidato
sintético local cumplió; no aprueba producción ni cambios de defaults. No representa
carga sostenida, límite real de contenedor ni capacidad productiva. No hay despliegue,
cambio de esquema ni rollback de datos.

## MVP-07: concurrencia candidata, solo workers sintéticos

`isolated --max-concurrent-inspections N` acepta un entero decimal estricto entre
4 y 8, con valor predeterminado 8. Solo configura el presupuesto de inspecciones de
los workers sintéticos; **no modifica Gateway/appsettings ni defaults productivos**.
`all` no cambia. La reserva máxima permanece en 8388608 bytes y el cuerpo en 1 MiB;
el umbral de memoria y el headroom tampoco cambian.

Cada repetición usa el conjunto ordenado sin duplicados `{1,2,4,N}`: N7 ejecuta
1/2/4/7, N4 ejecuta 1/2/4 y el default N8 conserva 1/2/4/8. Cada run exige además
concurrencia no superior al candidato, pico activo no superior a su concurrencia y
reserva no superior a su concurrencia × 1 MiB. Antes de publicar `ready`, cada
gateway worker adquiere exactamente N leases mínimos, exige que N+1 sea rechazado y
libera el presupuesto a 0/0; esto verifica conductualmente el límite configurado.

```powershell
dotnet Gateway.LoadHarness/bin/Release/net10.0/Gateway.LoadHarness.dll isolated --output artifacts/mvp05/mvp07-smoke --requests-per-concurrency 8 --repetitions 1 --max-concurrent-inspections 6
```

JSON añade `maxConcurrentInspections` y `concurrencies` (schedule exacto); Markdown
muestra ambos. Se conserva `gateway.mvp06.isolated.v1` como extensión compatible.
Un smoke no acredita sizing sostenido: la decisión sigue siendo candidate-only,
sin autorización de producción ni cambio de defaults.
