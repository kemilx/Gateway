# MVP-44 — Campaña local reproducible de rendimiento

## Alcance y perfil fijado antes de medir

El [perfil versionado](MVP44-local-performance-profile.json) fija una comparación
local, sintética y cerrada: cuerpo token-dense-v1 de 1 MiB, 512 solicitudes por
brazo, tres pares alternados AB/BA/AB y concurrencia 4. Son 3072 respuestas
medidas, además del warmup de dos oleadas por brazo. Los arneses ya existentes
validan **todos los bytes**, no solo tamaño o presencia de una máscara.

El baseline es el mismo host YARP por passthrough sin inspección, no un YARP
vacío de cualquier middleware. La configuración candidata del arnés
(admission 8, reserva 8 MiB, inspecciones 6) no es la configuración distribuida
por defecto de la aplicación (admission 6, reserva 6 MiB). El perfil de memoria
usa trabajadores nuevos para concurrencias 1/2/4/8, reserva 8 MiB y timeout de
120 segundos porque sus barreras/GC no son una prueba de latencia de producción.

Los gates son de **integridad de la medición**: cuerpos exactos, número de
solicitudes y pares, métricas presentes/finitas/no negativas y recursos liberados.
No son presupuestos de negocio. Los umbrales p50/p95/p99, throughput, CPU,
asignaciones y memoria del perfil son null con `PENDING_APPROVAL`. No se
convierten resultados observados a posteriori en umbrales supuestamente
aprobados. La campaña no aprueba RNF-03/CA-12 ni autoriza un piloto.

## Reproducción

En Windows, desde la raíz, con el SDK de [global.json](../global.json) y sin otras
campañas paralelas. Inspeccionar los proyectos y el destino antes de ejecutar:

```powershell
dotnet build ProyectoSeguridad.slnx -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$profile = Get-Content -Raw docs/MVP44-local-performance-profile.json | ConvertFrom-Json
$run = 'mvp44-' + [guid]::NewGuid().ToString('N')
$compare = 'artifacts/mvp05/' + $run + '/compare'
$memory = 'artifacts/mvp05/' + $run + '/memory'
$harness = 'Gateway.LoadHarness/bin/Release/net10.0/Gateway.LoadHarness.dll'
dotnet $harness compare --output $compare --requests-per-arm $profile.compare.requestsPerArm --repetitions $profile.compare.repetitions --concurrency $profile.compare.concurrency
if ($LASTEXITCODE -ne 0) { throw 'Comparison failed; preserve partial evidence' }
dotnet $harness memory --output $memory
if ($LASTEXITCODE -ne 0) { throw 'Memory run failed; preserve partial evidence' }
```

Estos arneses crean procesos y servidores propios loopback con puertos efímeros.
La salida debe permanecer bajo `artifacts/mvp05`; no reutilizar ni sobrescribir
directorios anteriores. No usar procesos ajenos, URLs de producción ni datos
reales. No borrar artefactos por inferencia. Restaurar dependencias antes del
build si el clon aún no está preparado; esta receta no introduce paquetes.

## Lectura y límites

- `compare/compare.json` es canónico: `status=completed`, tres `validPairs`, seis brazos válidos con 512 solicitudes cada uno, ventanas completas y budgets released. `performanceAcceptance` permanece `not_evaluated`.
- Comparar percentiles **por brazo** y deltas de pares; no sumar ni promediar percentiles como si fueran muestras de una sola distribución. CPU/asignaciones del worker incluyen protocolo, muestreador y runtime.
- Los picos de memoria se muestrean cada 10 ms y pueden perder transitorios. No hay límite duro de proceso ni promesa de RAM global. No se infiere una capacidad productiva de la reserva lógica.
- `memory/memory.json` describe el arnés con barreras y GC; verificar workers, canary, release y saturación. No usarlo como benchmark de latencia ni compararlo directamente con el proceso HTTP compartido.
- El cuerpo fuente y el esperado se mantienen vivos fuera de ciertos deltas de memoria. La validación completa añade coste frente a históricos que solo verificaban longitud/máscara; no comparar como si fueran cargas equivalentes.
- Registrar commit base y hashes de fuentes/binarios/perfil. Un binario construido con cambios sin commit debe identificarse como BASE+diff/hashes, no atribuirse retrospectivamente a un commit anterior.

## Resultado observado — 31 de octubre de 2026

Ejecución de ambos comandos con salida 0; gates de integridad cotejados en los
JSON, **no solo por el código de salida**. Windows X64, .NET 10.0.12, 12
procesadores lógicos, workstation GC y build Release. SDK fijado 10.0.401.

| Par | Brazo | p50 ms | p95 ms | p99 ms | req/s | CPU ms/req | MiB asignados/req | Pico working set MiB |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | baseline | 3.38 | 7.83 | 15.25 | 1021.2 | 3.662 | 0.010 | 72.57 |
| 1 | mask | 28.58 | 73.18 | 104.21 | 118.5 | 36.102 | 8.268 | 265.80 |
| 2 | mask | 27.42 | 73.65 | 94.35 | 123.3 | 33.234 | 8.213 | 244.77 |
| 2 | baseline | 3.28 | 5.35 | 9.85 | 1117.5 | 3.174 | 0.010 | 71.74 |
| 3 | baseline | 3.47 | 5.84 | 19.60 | 976.1 | 3.143 | 0.011 | 72.47 |
| 3 | mask | 28.96 | 72.56 | 91.45 | 100.6 | 42.664 | 8.214 | 244.29 |

Tres pares válidos, seis brazos con 512 cuerpos exactos cada uno (3072), ocho
warmups por brazo y budgets released. Baseline emitió 0 outcomes/duraciones de
inspección; Mask, 512 de cada uno por brazo. Los cuatro workers de memoria
N=1/2/4/8 pasaron canary/release, terminaron active=0/reserved=0 y contaron
1/2/4/9 outcomes con 1/2/4/8 duraciones. En N=8, la solicitud adicional devolvió
503 con cero lecturas del origen y presupuesto sin cambios.

**Hallazgo material:** el coste de inspeccionar 1 MiB no es pequeño. La mediana de
deltas pareados fue p95 **+66.72 ms**, CPU **+32.44 ms/req**, asignaciones
**+8.20 MiB/req** y throughput **−88.97%**. Son deltas del perfil concreto, no
una predicción de todas las APIs. Las asignaciones son bytes acumulados, no RAM
residente. La medición justifica priorizar diagnóstico/optimización y repetir una
campaña representativa acordada; no declarar rendimiento aceptado por pasar tests.

Artefactos locales no versionados bajo
`artifacts/mvp05/mvp44-541fc2907fe7400ba25586dc1b4361cc/`:

- `compare/compare.json`: SHA-256 `C8CA8250CAEA2FB2AFB1151FE279CF2EAFC5C46E108E0FEEF68D15BDD6954547`.
- `memory/memory.json`: SHA-256 `E5535A903B1D96B64DB5B529F58EA3DD81C730D3571FF39B3E383F0FBEC983EF`.
- Perfil previo a la medición: SHA-256 `2AEA268235EE5EA241943C10AB5AF9AA2029B3624B13D6F7D1047DCFA8A6A648`.
- Manifiesto local de fuentes/binarios: `Temp/opencode/MVP44-contract/tests/campaign-source-manifest.json`, SHA-256 `89A4DAF465A1B5A8EAF650413600CDE40DF64A134DFF44094DEC00259A8F3636`, cotejado idéntico antes/después.
- DLL Gateway: SHA-256 `CD8EF2A8FB1498B34810449B8A059FA9619BCDEB18C3596629EFD149C4127984`; arnés: `9CDEC5CFBF64EAFDA0C598E3D8768F3A7C06C4DFF43EC8A8B9C19D45E1F55C82`.

El atributo compilado conserva BASE `08bb919ec2ca2b4c2c2f4fdefc10802720518bdc`,
pero la ejecución se realizó **con los cambios MVP-44 aún sin commit**: el TARGET
es BASE+fuentes/binarios del manifiesto, no el código original de BASE. Una edición
documental posterior no cambia esa identidad binaria. Estos hashes vinculan
artefactos, no sustituyen repetir la medición en otro entorno.

Las mismas fuentes quedaron consolidadas en el commit
`6396ec005ee2a8701fa3712509f0ad61d4a19a9e`. No se reconstruyeron binarios ni se
repitió la campaña después del commit; la evidencia sigue ligada a los hashes anteriores.
