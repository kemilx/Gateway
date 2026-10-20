# MVP-18 — comparación local observacional

**20 de octubre de 2026.** Ejecución completa, no aceptación de rendimiento: `LOCAL_OBSERVATIONAL_COMPARISON`, `performanceAcceptance: not_evaluated`.

## Método e identidad

Se comparó el mismo gateway sin inspección de respuesta (baseline) y con PAN/Mask. Cada brazo tuvo API y gateway nuevos en procesos separados del cliente; orden de las dos parejas: baseline/máscara y máscara/baseline. Cuerpo sintético de **1 MiB**, concurrencia **6**, **256 solicitudes por brazo** y dos oleadas completas de warmup de seis solicitudes antes de medir. Sin GC forzada ni Job/hard limit.

Se ejecutó una sola vez: **1024/1024 respuestas medidas validadas**, 48 warmups, cuatro brazos y dos parejas válidos. Baseline produjo cero outcomes/duraciones de inspección; máscara produjo 256/256 por brazo. Todas las reservas y la admisión quedaron liberadas; no quedaron workers.

La base fue `9a90f0acab8f5eacc5544dd41209858743ab8d0a` **más salud y comparación sin commit**. La revisión compilada por sí sola no identifica este árbol. Snapshot del comparador: `99F631BE8A4EFA9A7E86C92A53A945AE59748AE47641209DAC5DD24851659CF6`; fuentes y binarios se verificaron antes y después. Los hashes de los tres binarios y del reporte canónico están en el [resumen JSON](2026-10-20-local-summary.json).

```powershell
dotnet Gateway.LoadHarness/bin/Release/net10.0/Gateway.LoadHarness.dll compare `
  --output artifacts/mvp05/mvp18-compare-20261020T043408Z-03b7afc1e54341058e0b87c6415b2eaf `
  --requests-per-arm 256 --repetitions 2 --concurrency 6
```

Windows X64, .NET 10.0.12, Release, 12 procesadores reportados y workstation GC. Perfil sintético idéntico en ambos brazos: admisión **8**, inspección **6**, presupuesto **8 MiB**, timeout de inspección **10 s**, deadline de headers desactivado. **No equivale a todos los defaults operativos** de admisión 6 y presupuesto 6 MiB.

## Observaciones

Medianas de las dos mediciones por brazo; las medianas de p95/p99 **no son percentiles globales**:

| Métrica | Baseline | PAN/Mask |
|---|---:|---:|
| p50 por ejecución | 3,76 ms | 44,88 ms |
| p95 por ejecución | 8,42 ms | 101,77 ms |
| p99 por ejecución | 15,23 ms | 157,38 ms |
| Throughput | 1379,71 solicitudes/s | 106,68 solicitudes/s |
| CPU del gateway por solicitud | 4,15 ms | 55,88 ms |
| Asignaciones del gateway por solicitud | 11.649,69 bytes | 8.640.117,98 bytes |
| Máximo Private Bytes muestreado entre los dos brazos de cada tipo | 27.807.744 bytes | 373.276.672 bytes |

El coste de inspección resulta importante en este payload denso: la mediana de diferencias pareadas es **+93,35 ms en p95**, **+51,73 ms de CPU por solicitud** y **+8.628.468,30 bytes asignados por solicitud**. Son observaciones del perfil, no umbrales aprobados ni motivo para cambiar límites automáticamente. Las asignaciones acumuladas por solicitud no equivalen a memoria simultáneamente retenida.

## Validación y límites

- Build Release sin errores ni advertencias, **272/272 tests PASS**, sin omitidos. Revisión independiente `APPROVED_WITH_WARNINGS`.
- La revisión inicial detectó tres problemas del harness que se corrigieron antes de medir: cierre posterior al ack sin exigir salida cero, métricas omitidas convertidas en cero y timeout de stdin no efectivo. La regresión incluye exit 0/1/bloqueo de procesos reales, 51 mutaciones de campos y worker real con stdin abierto.
- La lectura nativa pendiente pertenece al proceso worker y termina con él; no se garantiza cancelación nativa. La prueba de línea sobredimensionada podría distinguir mejor rechazo inmediato de timeout; el código limita el buffer antes de decodificar.
- CPU y asignaciones son del proceso gateway completo, incluyendo runtime, sampler y protocolo. Las ventanas del cliente y del worker difieren. La latencia incluye consumir y validar el cuerpo completo.
- Los picos son muestreados e independientes, pueden omitir transitorios y no representan memoria combinada de API/gateway/cliente. Este reporte **no es sizing** y no sustituye ni reescribe MVP-16.
- Dos parejas, un único payload y carga closed-loop: sin intervalos de confianza, corrección de coordinated omission, comparación entre entornos ni presupuesto numérico aprobado para RNF-03.

Artefactos completos conservados localmente en `MVP18-compare/measurement`, con staging ignorado y copia externa verificada. El primer preflight de QA no pudo escribir por permisos; el coordinador ejecutó la carga autorizada y QA completó la verificación independiente en solo lectura: identidad, conteos, métricas, deltas pareados, privacidad y cleanup correctos, sin defectos nuevos. No se recalcularon percentiles desde latencias crudas porque no se persisten; se validó su regla con tests. Sin commit, push, despliegue ni cambios de defaults.
