# MVP-08 — hard limit local de proceso

Fecha: 16 de octubre de 2026

Revisión medida: `7863a373332f70fbfe9025b1619942df69829870`

Clasificación: **HARD_LIMIT_LOCAL_CANDIDATE**

## Escenario

Se validó el candidato de 6 inspecciones en Windows de 64 bits. Cada gateway se
ejecutó dentro de un Job Object independiente con límite real de commit por proceso
de **536.870.912 bytes**, `KILL_ON_JOB_CLOSE` y verificación de pertenencia/límite/
flags antes de reanudar el proceso suspendido y después de la carga.

El criterio de headroom permaneció en 30%: tanto Private Bytes muestreado como el
pico de commit conservado por Windows debían ser <= **375.809.638 bytes**.

## Resultado

- 40/40 runs PASS sobre concurrencias 1/2/4/6, diez repeticiones cada una.
- 2.560/2.560 respuestas HTTP 200, outcomes y duraciones.
- Todos los canarios, presupuestos, verificaciones Job y cierres cooperativos pasaron.
- Máximo Private Bytes: **329.945.088**; margen mínimo **45.864.550** bytes.
- Máximo pico de commit del Job: **334.942.208**; margen mínimo **40.867.430** bytes.
- Headroom peor caso observado por commit: **37,61%**.

| Concurrencia | Runs | Máximo Private | Mediana Private | Máximo commit Job | Mediana commit Job | Máximo managed | Máximo working set | Mediana req/s | p50 mediana ms | p95 mediana ms |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 10 | 160.948.224 | 134.727.680 | 161.087.488 | 137.443.328 | 139.056.016 | 197.685.248 | 37,00 | 21,01 | 59,37 |
| 2 | 10 | 230.727.680 | 158.574.592 | 230.940.672 | 160.192.512 | 191.592.776 | 261.472.256 | 53,57 | 29,22 | 76,34 |
| 4 | 10 | 275.824.640 | 219.000.832 | 276.049.920 | 222.715.904 | 234.011.912 | 302.903.296 | 69,60 | 55,45 | 93,35 |
| 6 | 10 | 329.945.088 | 246.231.040 | 334.942.208 | 248.907.776 | 304.296.880 | 354.066.432 | 80,44 | 69,90 | 111,78 |

## Decisión

N6 pasa el experimento local con hard limit y headroom. **No se cambia todavía el
default productivo de 8 inspecciones/8 MiB y no se autoriza producción.** La siguiente
decisión debe ser explícita y basarse además en carga sostenida e infraestructura
representativa con TLS, autenticación, red y telemetría operativa.

El Job controla commit por proceso; no equivale a working set, suma del Job o límite
de contenedor. No se ejecutó un probe deliberado de fallo de asignación. Una muerte
abrupta del coordinador entre creación y asignación podría dejar un child suspendido
fuera del Job, sin ventana de ejecución ilimitada.

## Validación e integridad

- Build Release: 0 errores y 0 advertencias.
- Suite: 122/122 PASS; 0 fallos y 0 omitidos.
- `dotnet format --verify-no-changes`: PASS.
- Los reportes no contienen payload, PAN, selector, direcciones, hostname, usuario,
  rutas personales, PID, argumentos ni diagnósticos internos.
- SHA-256 de artefactos locales saneados e ignorados por Git:
  - `hard-limit.json`: `5E95069C1D2243FBF4DE7964C3269810BD378C0C8BC7B2FEE0E23DE109CB5653`
  - `hard-limit.md`: `B047013BC251C91A4BCB00E2AA2D33FD982BDC5E2E3E055BE106E0C608734A98`
  - `build.sanitized.log`: `585BDC7C7AA3D73D2488CDFD9788C2BDD07FEB61475B0940CC28B733A8D0A31C`
  - `test.sanitized.log`: `7FDFFB7704ED0DE904EAF13235D741B9E5C39A8D0EC4243B4916ECAAF6A2C9E5`
  - `format.sanitized.log`: `7EB70257593DA06F682A3DDDA54A9D260D4FC514F645237F5CA74B08F8DA61A6`

Esta evidencia acredita únicamente el experimento local sintético indicado.
