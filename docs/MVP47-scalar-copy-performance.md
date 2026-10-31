# MVP-47 — Copia escalar JSON y reducción de asignaciones

## Resultado

Cambio acotado a `CopyElement` en [PanResponseTransform](../Gateway/PanResponseTransform.cs):
copiar una propiedad escalar con `JsonProperty.WriteTo` evita materializar su
nombre como `string`. Objetos y arrays mantienen el recorrido recursivo. No se
modifican políticas, selectores, máscaras, detección secundaria, captura,
snapshots, telemetría, paquetes ni configuración.

Build Release limpio y **1659/1659 pruebas PASS**, cero omitidas; 41 regresiones
nuevas y revisión independiente de seguridad favorable **solo local**. La campaña
del mismo perfil de 1 MiB pasó sus gates de integridad, no aceptación de negocio.

La mediana de asignaciones adicionales Mask menos passthrough bajó de **6.73 a
5.10 MiB/solicitud** frente a MVP-46: **1.63 MiB menos (24.27%)** observados.
Frente a MVP-44, el descenso acumulado observado es 37.84%. Son campañas separadas,
no un A/B intercalado de binarios antiguos/nuevos ni una predicción universal.

**No se demuestra una mejora global de rendimiento:** el delta p95 bajó de
+80.77 a +65.24 ms, pero el delta CPU se mantuvo alrededor de +31.5 ms/req y el
impacto relativo en throughput sigue alrededor de −89.5%. El mayor pico muestreado
de working set de Mask fue **314.61 MiB**, superior al máximo de 257.54 MiB de
MVP-46. Menos bytes asignados no implica menos RAM residente. Continúan
`PENDING_APPROVAL`, `not_evaluated` y `MEASURED_NOT_SIZED` para presupuestos,
aceptación de rendimiento y sizing, respectivamente.

## Contrato conservado y granularidad de cancelación

- Solo las propiedades cuyo valor **no** es objeto ni array usan `property.WriteTo`.
  Los contenedores nunca se delegan como subárboles completos: siguen recorriéndose
  con checkpoints por nodo y propiedad, incluyendo duplicados.
- Se conserva el número de checks del experimento. En escalares, el check antes
  situado entre nombre y valor pasa a preceder el par completo; permanece el check
  posterior. La escritura privada del par es una operación síncrona cooperativa,
  **no interrumpible a mitad**. Igual número de checks no significa idéntica
  latencia de cancelación. Nombres y valores grandes se prueban en ambos bordes.
- La entrada completa sigue validándose como UTF-8 estricto y parseándose con
  profundidad máxima 64 antes de seleccionar salida. La validez UTF-8 no equivale
  a aceptar escapes JSON de sustitutos aislados; las regresiones fijan su rechazo
  observado en el recorrido, incluso en campos no seleccionados.
- La escritura parcial de prueba está en un buffer privado. `ApplyAsync` conserva
  descarte, error genérico, checkpoints anteriores al envío y devolución del lease.
  No se envía el upstream antes de terminar la inspección. No se garantiza entrega
  atómica de red una vez comenzada la escritura downstream.
- Se mantiene la captura diferida de [MVP-46](MVP46-deferred-capture-performance.md),
  incluido el límite real, la lectura independiente de `Content-Length` y el
  tradeoff de metadata falsa. No se añade pooling, `WriteRawValue`, `unsafe` ni un
  límite nuevo de salida. El escaping puede expandirla: el límite de entrada no
  constituye un techo de RAM o de tiempo síncrono.

Rollback: restaurar la copia anterior de nombre y valor en `CopyElement` y
verificar el binario. No requiere migración de políticas. No se ejecutó rollback
en un destino desplegado ni se autorizó producción.

## Experimento causal previo

Scratch local `Temp/opencode/MVP47-json-traversal/`: baseline mediante delegado al
`CopyElement` real y variante local del método. Tres procesos nuevos, orden
AB/BA/AB; dentro de cada uno, ocho warmups y cuarenta muestras intercaladas por
brazo. Se forzó GC entre muestras, **no** durante la campaña HTTP posterior.
La validación comparó todos los bytes del payload real de `Common.Payload()` de
1 MiB y tres corpus de Unicode/escapes/duplicados/números/estructuras.

| Proceso | Orden | Baseline, elapsed medio ms | Variante, elapsed medio ms | Asignaciones baseline → variante, bytes |
| ---: | --- | ---: | ---: | ---: |
| 1 | AB | 15.666 | 14.343 | 4963288 → 3235312 |
| 2 | BA | 14.724 | 13.529 | 4963288 → 3235312 |
| 3 | AB | 15.635 | 14.558 | 4963288 → 3235312 |

Ahorro de **1727976 bytes (~1.65 MiB)** por copia en los tres procesos. La ventana
incluye `MemoryStream`, `Utf8JsonWriter` y recorrido; `ToArray` y la comparación
quedan fuera. El parseo UTF-8 estricto + DOM64 se midió aparte (5.001–5.268 ms).
Elapsed no es CPU; la granularidad de CPU de proceso y el GC forzado impiden
extrapolar esas cifras a p95 o a una solicitud real. El árbol se copió completo
sin máscara en este experimento, no fue una prueba HTTP ni de políticas.

El manifiesto exploratorio llama `runtime` al valor 10.0.401: ese valor es el
**SDK**, no evidencia de una versión de runtime. La campaña sí identifica .NET
10.0.12. El build posterior de pruebas recompiló los binarios del repositorio;
se conserva la DLL original del probe y sus hashes. No se atribuyen las muestras
exploratorias a esos binarios reconstruidos.

## Regresiones

[ScalarCopyTests](../Gateway.Tests/ScalarCopyTests.cs) pasó **41/41 tanto antes como
después** del cambio. Al ser una optimización que preserva comportamiento, no se
inventa un RED funcional: la comparación de coste anterior es el baseline medido.

- `ScalarCopyTests.CopyElement_matches_legacy_bytes_or_exception`: bytes completos
  frente al algoritmo anterior para escapes, Unicode BMP/suplementario, números,
  duplicados, contenedores, nombres/valores grandes, profundidad 64 y sustitutos
  aislados; los casos válidos exigen ausencia de error y los inválidos fijan
  `InvalidOperationException`. Profundidad 65 se rechaza por el parser.
- `ScalarCopyTests.Selected_pan_modes_preserve_or_block_unselected_scalar_structure`:
  Mask fuerza entrega transformada, Observe conserva bytes originales, Block
  devuelve el error fijo; conteos de máscaras/hallazgos y outcome exactos.
- `ScalarCopyTests.Unpaired_surrogates_in_unselected_subtree_pin_baseline`: doce
  casos de sustitutos altos/bajos en nombres/valores y tres modos devuelven el
  mismo 502 genérico, sin publicar conteos parciales ni datos en logs.
- `ScalarCopyTests.Large_scalar_pair_checks_cancellation_before_and_after_value`:
  cancelación/expiración deterministas antes/después del escalar grande, más
  controles de descenso a contenedores y `ApplyAsync` sin salida y con lease devuelto.

También se comprueban reglas adicionales con/sin detección secundaria, UTF-8
inválido en nombres y valores no seleccionados, truncamiento, exceso de cuerpo,
eventos terminales y ausencia de canarios en su estado/mensaje. La suite completa
añade las regresiones HTTP reales, snapshots, máscaras y privacidad existentes.

## Campaña comparable y reproducción

Se mantiene sin cambios el [perfil MVP-44](MVP44-local-performance-profile.json):
1 MiB, 512 solicitudes por brazo, tres pares AB/BA/AB, concurrencia cuatro.
Baseline es el mismo host YARP por passthrough, no YARP vacío. La configuración
del arnés no equivale a los defaults de la aplicación. Seguir la
[receta de campaña](MVP44-performance-campaign.md#reproducción), siempre con
directorios nuevos y destinos locales sintéticos.

```powershell
dotnet build ProyectoSeguridad.slnx -c Release --no-restore --no-incremental
dotnet test ProyectoSeguridad.slnx -c Release --no-build --no-restore --logger "trx;LogFileName=mvp47-full-final.trx" --results-directory <scratch-nuevo>
dotnet Gateway.LoadHarness/bin/Release/net10.0/Gateway.LoadHarness.dll compare --output artifacts/mvp05/<nuevo-run>/compare --requests-per-arm 512 --repetitions 3 --concurrency 4
dotnet Gateway.LoadHarness/bin/Release/net10.0/Gateway.LoadHarness.dll memory --output artifacts/mvp05/<nuevo-run>/memory
```

Ejecución observada, 31 de octubre de 2026: todos salida 0; build sin advertencias
ni errores (5.46s), full 1659 PASS/0 omitidas (1m31s). Windows X64, SDK 10.0.401,
.NET 10.0.12, 12 procesadores lógicos, workstation GC.

| Par | Brazo | p50 ms | p95 ms | p99 ms | req/s | CPU ms/req | MiB asignados/req | Pico working set MiB |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | baseline | 3.43 | 7.49 | 14.36 | 1004.9 | 2.594 | 0.011 | 72.85 |
| 1 | mask | 27.04 | 61.80 | 96.17 | 125.4 | 33.417 | 5.137 | 314.61 |
| 2 | mask | 27.93 | 78.81 | 137.17 | 119.3 | 35.797 | 5.096 | 215.20 |
| 2 | baseline | 3.31 | 5.24 | 7.83 | 1132.3 | 3.906 | 0.010 | 72.23 |
| 3 | baseline | 3.14 | 3.76 | 8.40 | 1207.1 | 2.838 | 0.010 | 70.32 |
| 3 | mask | 23.87 | 69.00 | 102.73 | 123.5 | 34.332 | 5.110 | 219.90 |

Medianas de deltas pareados Mask menos baseline, nunca percentiles agrupados:

| Métrica | MVP-44 | MVP-46 | MVP-47 |
| --- | ---: | ---: | ---: |
| Asignaciones adicionales, MiB/req | +8.20 | +6.73 | +5.10 |
| CPU adicional, ms/req | +32.44 | +31.37 | +31.49 |
| p95 adicional, ms | +66.72 | +80.77 | +65.24 |
| Throughput relativo al baseline | −88.97% | −89.40% | −89.46% |

Se cotejaron JSON y manifiesto, no solo exit codes: tres pares válidos, seis brazos
con 512 cuerpos completos cada uno (3072 más warmups), ocho warmups por brazo,
outcomes/duraciones 0 en baseline y 512 en Mask, mediciones finitas/no negativas y
budgets liberados. Los cuatro workers nuevos N=1/2/4/8 pasaron canary/release,
terminaron active=0/reserved=0; la saturación N=8 devolvió 503 con cero lecturas y
presupuesto conservado. Los picos son muestreados cada 10 ms y pueden omitir
transitorios; no prueban sizing ni límite físico de memoria.

## Identidad y límites de la evidencia

TARGET ejecutado: BASE `d7ab6951b33eef3df8d7409a2da647359cd286ca` más cambios de
`PanResponseTransform.cs` y `ScalarCopyTests.cs`, construidos antes del commit de
implementación. El atributo compilado conserva BASE; no se atribuye la ejecución
al código original de BASE ni a un build posterior inventado.
Las mismas fuentes se consolidaron en
`1dc95cd91f5a1440cf9760312c9c7f36c972e725`; no se recompiló ni se repitió la
campaña por crear ese commit.

Artefactos locales no disponibles en un clon nuevo:

- `artifacts/mvp05/mvp47-scalars-cf801c2e50b64524bd1f82357e4093bc/`.
- `Temp/opencode/MVP47-json-traversal/`: probe, JSON, manifiestos, TRX y validación
  de campaña. Los resultados QA se conservaron bajo `tests/qa-baseline-final/`.

| Artefacto | SHA-256 |
| --- | --- |
| PanResponseTransform.cs | `EE9D8FE6E320EABE783162B2BA5243F691A0CB42B2097644D9E4D4B324B33101` |
| ScalarCopyTests.cs | `609706133F385E97213A01960EECC097DB3A386790993559E8A9D4DFADFF1F4A` |
| Gateway.dll | `AAEA0A74E16C8FC439D28FAC642E91722E84441839247A5A38DEAF068C9F0655` |
| Gateway.LoadHarness.dll | `912D52874E8B5541766F3449558251E9F73BB4ADDB43DD18FCB0E144FB8ACAEA` |
| campaign-source-manifest.json | `8DC1DBF77F4C0646E4E2C0B01190BDD03C610BBB3F3AA56DBC5E9F3F9C3CB062` |
| tests/full-final/mvp47-full-final.trx | `C940E141EAF42908639AB99BD9D180B7E9B6C9CCA1F56DABAED08722E47D9E3E` |
| compare/compare.json | `CC3361789A2BB7F389CC6D8F6197924C4FC86CAAF2DF3EB5774986303A78F959` |
| memory/memory.json | `EEB6909834FA29FAD08C77FAAC7610AC0775C1680A4F385C5146D083C4440282` |

96 fuentes, cinco binarios y perfil se cotejaron idénticos antes/después de la
campaña y las fuentes coinciden con el TARGET revisado por seguridad. No se
resuelve ni se declara PASS de la deuda de formato anterior. La
[matriz SRS](MVP43-acceptance-matrix.md) mantiene sus estados: reducir asignaciones
no cierra RNF-02/03, RF-11, CA-09/12 ni operación. La aceptación formal sigue
pendiente. El siguiente diagnóstico de rendimiento debe atribuir CPU con mayor
precisión antes de otra optimización; no eliminar checkpoints por intuición.
