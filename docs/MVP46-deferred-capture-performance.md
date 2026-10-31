# MVP-46 — Captura preasignada diferida y rendimiento observado

## Resultado y límites

Optimización local de asignaciones, **no aceptación de rendimiento**. Se conserva
el [perfil MVP-44](MVP44-local-performance-profile.json), el motor de reglas, los
paquetes y la configuración. Build Release limpio; **1618/1618 pruebas PASS**,
cero omitidas; revisión independiente de seguridad favorable exclusivamente local.

En la campaña de 1 MiB, la mediana del coste adicional de asignaciones Mask menos
passthrough pasó de **8.20 a 6.73 MiB/solicitud**: descenso observado de **1.47 MiB
(17.91%)** entre campañas con el mismo perfil. No es RAM residente ni una garantía
para otras cargas. Los resultados **no demuestran una mejora general de latencia**:
el delta p95 observado empeoró de +66.72 a +80.77 ms. El coste de inspección sigue
siendo alto; CPU, throughput y latencia requieren más diagnóstico y mediciones
controladas. Los presupuestos continúan `PENDING_APPROVAL` y
`performanceAcceptance=not_evaluated`.

## Cambio acotado y modelo de confianza

[PanResponseTransform.CaptureChunk](../Gateway/PanResponseTransform.cs) conserva
el límite real antes de escribir y añade una capacidad inicial diferida:

1. Admission, lease, elegibilidad y rechazo de `Content-Length > MaxBodyBytes`
   conservan su orden. El stream de captura empieza sin buffer preasignado.
2. Solo tras una primera lectura positiva y su checkpoint de cancelación/deadline,
   se comprueba que los bytes reales no superen el límite.
3. Si el límite está dentro de `1..PanPolicyValidator.MaximumBodyBytes` (16 MiB)
   y la longitud declarada es positiva y no supera ese límite, se prepara una
   capacidad de al menos el máximo entre el primer chunk y la longitud declarada.
   Nunca se reduce una capacidad existente.
4. Longitud ausente/cero/negativa o límite público fuera del rango operacional:
   crecimiento anterior, sin usar la metadata para preasignar. Una declaración
   excesiva se rechaza en el transform antes de abrir el stream.
5. Siempre se sigue leyendo hasta EOF o hasta detectar el byte que excede el
   límite. La longitud declarada **no** prueba integridad ni delimita la inspección.

Un upstream que declara un cuerpo grande pero entrega pocos bytes puede anticipar
una asignación de hasta el límite configurado tras el primer read positivo. Es un
tradeoff explícito, acotado por la política y la reserva **nominal** ya adquirida;
la reserva no es memoria física ni un techo de RSS. Parser, salida, buffers,
copias y overhead siguen existiendo. La asignación síncrona no es interrumpible;
los checkpoints posteriores siguen evitando un commit cancelado. No se afirma
un deadline temporal estricto de toda operación síncrona.

No se añade pooling ni retención nueva de cuerpos. El buffer alquilado de lectura
sigue devolviéndose con limpieza. No se preasigna la salida: el experimento no
mostró un beneficio material. Se conservan `ToArray`, UTF-8 completo, profundidad
64, duplicados, Unicode, máscaras, snapshots, modos, errores genéricos, telemetría
y atomicidad de inspección previa al envío. No se garantiza entrega atómica de red.

**Rollback:** restaurar el crecimiento anterior en la captura mediante reversión
del cambio de código y verificar el binario; no hay migración de políticas ni
paquetes. No se ha ejecutado un rollback en un destino desplegado.

## Evidencia previa y regresiones

El diagnóstico secuencial usó `Common.Payload()` y `WriteSelected` reales. Un A/B
scratch con tres pares AB/BA/AB, ocho warmups y cuarenta iteraciones por brazo
incluyó ambos constructores de streams en ventanas simétricas. La preasignación
ahorró aproximadamente **0.9845 MiB por iteración (12.75%)**, principalmente en
captura, sin mejora material de asignaciones del transformado. Se comprobó toda
la salida en las 288 iteraciones, incluidos warmups. La longitud exacta era un
oráculo del experimento, no una propiedad fiable del upstream. Esas ventanas no
son las del proceso HTTP: no equiparar ambos ahorros ni atribuir su diferencia
a una causa concreta sin medirla.

Las 39 regresiones de [CaptureCapacityTests](../Gateway.Tests/CaptureCapacityTests.cs)
incluyen capacidad exacta, primer chunk/vacío, longitud ausente, cero, negativa,
falsa menor/mayor, límites declarados enormes, límite real y exceso, tres modos,
fallo de primera lectura, admission denegada, cancelación y devolución del lease.
Casos representativos:

- `CaptureCapacityTests.First_positive_chunk_preallocates_known_length_for_tiny_body`.
- `CaptureCapacityTests.Transform_reads_to_eof_despite_inaccurate_or_unknown_length`.
- `CaptureCapacityTests.Actual_size_boundary_is_enforced_independently_of_declared_length`.
- `CaptureCapacityTests.Invalid_high_maximum_uses_normal_growth_instead_of_declared_length`.

El RED inicial fue de compilación por seam ausente, no prueba conductual. Después,
retirar temporalmente solo la preasignación hizo fallar tres casos (27 pasaron);
restaurar los bytes exactos devolvió GREEN. La revisión encontró el caso público
`MaxBodyBytes > 16 MiB`: dos casos RED observaron capacidad 4096 en vez de 256
usando allocations pequeñas. La guarda final conserva el crecimiento legado
fuera del rango operacional; los 39 casos pasaron, seguidos del full nuevo de
1618. Los casos nuevos de cancelación simulan antes/durante/después del read; no
se presentan como prueba nueva de deadline vencido. La suite existente de
deadlines y de HTTP real también pasó dentro del full.

## Campaña HTTP y memoria

Reproducción: seguir la [receta MVP-44](MVP44-performance-campaign.md#reproducción)
con directorios nuevos bajo `artifacts/mvp05`. Mismo perfil de 1 MiB, 512 solicitudes
por brazo, tres pares AB/BA/AB y concurrencia cuatro. No se modificó el arnés ni
la validación completa de cuerpos. Baseline: el mismo host YARP por passthrough,
no YARP vacío. Configuración candidata del arnés distinta de los defaults de app.

Comandos ejecutados, 31 de octubre de 2026:

```powershell
dotnet build ProyectoSeguridad.slnx -c Release --no-restore --no-incremental
dotnet test ProyectoSeguridad.slnx -c Release --no-build --no-restore --logger "trx;LogFileName=mvp46-full-final.trx" --results-directory <scratch-nuevo>
dotnet Gateway.LoadHarness/bin/Release/net10.0/Gateway.LoadHarness.dll compare --output artifacts/mvp05/<nuevo-run>/compare --requests-per-arm 512 --repetitions 3 --concurrency 4
dotnet Gateway.LoadHarness/bin/Release/net10.0/Gateway.LoadHarness.dll memory --output artifacts/mvp05/<nuevo-run>/memory
```

Todos terminaron con salida 0. Build: cero advertencias y errores; full: 1m33s.
Windows X64, .NET 10.0.12, SDK 10.0.401, 12 procesadores lógicos, workstation GC.

| Par | Brazo | p50 ms | p95 ms | p99 ms | req/s | CPU ms/req | MiB asignados/req | Pico working set MiB |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | baseline | 3.43 | 5.15 | 16.81 | 1041.6 | 3.754 | 0.011 | 72.38 |
| 1 | mask | 26.68 | 85.92 | 105.11 | 117.4 | 35.126 | 6.744 | 231.00 |
| 2 | mask | 24.92 | 74.53 | 107.24 | 121.8 | 33.234 | 6.745 | 257.54 |
| 2 | baseline | 3.18 | 4.47 | 11.13 | 1149.1 | 3.113 | 0.011 | 71.96 |
| 3 | baseline | 3.18 | 4.22 | 11.89 | 1129.1 | 2.899 | 0.010 | 71.46 |
| 3 | mask | 31.07 | 90.40 | 102.79 | 110.0 | 38.055 | 6.745 | 231.07 |

Medianas de deltas pareados, Mask menos baseline; no percentiles agrupados:

| Métrica | MVP-44 | MVP-46 |
| --- | ---: | ---: |
| Asignaciones adicionales, MiB/req | +8.20 | +6.73 |
| CPU adicional, ms/req | +32.44 | +31.37 |
| p95 adicional, ms | +66.72 | +80.77 |
| Throughput relativo al baseline | −88.97% | −89.40% |

Son campañas separadas con el mismo perfil, no un A/B intercalado de binarios
antiguo/nuevo ni prueba estadística de mejora de CPU/latencia. La reducción de
asignaciones coincide con la dirección del experimento, pero no elimina el coste
dominante de parseo/recorrido ni permite afirmar mejor rendimiento global.

Gates comprobados sobre los JSON, no solo sobre exit code: tres pares válidos,
seis brazos con 512 cuerpos exactos cada uno (3072 más warmups), ocho warmups por
brazo; baseline 0 outcomes/duraciones y Mask 512 de cada uno, budgets released,
mediciones finitas/no negativas. Cuatro workers de memoria nuevos N=1/2/4/8 con
canary/release correctos, active=0/reserved=0 al terminar; N=8 saturado devolvió
503 sin lecturas del origen y sin alterar el presupuesto. Los picos muestreados
pueden perder transitorios; no son sizing ni un límite duro de memoria.

## Identidad y trazabilidad

TARGET ejecutado: BASE `662f9c14bd7544bfa871888f8940a5597e33368b` + cambios de
`PanResponseTransform.cs` y `CaptureCapacityTests.cs`, construidos antes del
commit de implementación. El atributo compilado conserva BASE; **no** se atribuye
la ejecución al código original de BASE ni a una reconstrucción posterior.
Las mismas fuentes se consolidaron en
`0feef4a113c0d865e4ebf1b100b0219841343c48`; no se reconstruyeron binarios ni se
repitió la campaña por crear ese commit.

Artefactos locales, no distribuidos en un clon nuevo:

- Campaña: `artifacts/mvp05/mvp46-capture-7b693e3b8e454b2e89b3ae933dcdfd44/`.
- Scratch: `Temp/opencode/MVP46-capture/`, con TRX, manifiesto de 95 fuentes y
  cinco binarios, comprobación de integridad y evidencia RED/GREEN.
- Experimento anterior: `Temp/opencode/MVP45-performance-diagnosis/ab-capacity/`.

| Artefacto | SHA-256 |
| --- | --- |
| PanResponseTransform.cs | `3C8B06BB24839B7D93F35E5EE762A028FD58AE3FA8EA4B67AAFE5559AA8BBFC3` |
| CaptureCapacityTests.cs | `14437FBB7AF25B8A9D29E07BA1FD72F28C433894EF63BF966A3A82FBC0BADC13` |
| Gateway.dll | `59DEF22096BA058D6B92D5D73F2A0CA70F5521D0F4E2E9DFD0FE680C81E05FEC` |
| Gateway.LoadHarness.dll | `2C4F0900E307F8D824392554AA03CBBF639FC636CE35DDAC0F14CB86DCFF4094` |
| campaign-source-manifest.json | `1E9C1C6F9262287F66C322382E7DBF9924B20E59CBFC2B98F27A9137DDBE7E43` |
| full-final/mvp46-full-final.trx | `C49858E049871DFFD292E0782C547F625534CFD0E355BECAEDEB3E872124639F` |
| compare/compare.json | `E9B21907FB84984E880B0B20B927DC1CFA58E78636EE17740D69EA70DDD768B5` |
| memory/memory.json | `1ECE9C2DECC7419A60D87A6168E46CC1E6222CFD7CE3E3DC86A133C9D9C9D711` |

Fuentes/binarios/perfil se cotejaron idénticos antes/después de la campaña y contra
el TARGET de seguridad. Las ediciones documentales posteriores no invalidan esa
evidencia; modificaciones ejecutables relacionadas sí requerirían nueva validación.
La deuda de formato anterior no se declara resuelta ni se atribuye un PASS nuevo.

Siguiente prioridad: aislar coste de parseo y recorrido/escritura con un experimento
controlado antes de otra optimización; conservar las garantías JSON y de privacidad.
La [matriz](MVP43-acceptance-matrix.md) mantiene parciales RNF-02/03 y CA-12, además
de los pendientes de telemetría y operación. No hay aprobación de producción.
