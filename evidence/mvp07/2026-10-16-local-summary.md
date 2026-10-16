# MVP-07 — comparación local de concurrencia

Fecha: 16 de octubre de 2026

Revisión medida: `9e6bd654b0b1679c6ef59be63df973ee19e39da3`

Clasificación: **SIZED_LOCAL_CANDIDATE**

## Objetivo

Comparar candidatos de 7 y 6 inspecciones manteniendo 512 MiB por gateway, 30%
de headroom, reserva máxima de 8 MiB y cuerpos sintéticos de 1 MiB. Cada candidato
se ejecutó diez veces sobre su calendario 1/2/4/N, con 64 solicitudes por run y
un proceso gateway nuevo por run.

## Resultado formal desde el commit

| Candidato | Runs | Solicitudes 200 | Máximo Private Bytes | Margen bajo 375.809.638 | Headroom peor caso | Resultado |
|---:|---:|---:|---:|---:|---:|---|
| 7 | 40 | 2.560/2.560 | 371.003.392 | 4.806.246 | 30,90% | PASS local, margen bajo |
| 6 | 40 | 2.560/2.560 | 310.337.536 | 65.472.102 | 42,20% | PASS local, seleccionado para hard-limit |

Ambos lotes conservaron outcomes y duraciones exactos, canarios correctos y
presupuesto liberado. Cada worker verificó antes de `ready` que N leases eran
admitidos, N+1 era rechazado y el presupuesto volvía a 0/0.

### Candidato 7

| Concurrencia | Runs | Máximo Private | Mediana Private | Máximo managed | Máximo working set | Mediana req/s | p50 mediana ms | p95 mediana ms |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 10 | 230.604.800 | 116.498.432 | 177.814.752 | 263.868.416 | 39,41 | 18,50 | 57,30 |
| 2 | 10 | 240.451.584 | 186.853.376 | 213.943.912 | 274.952.192 | 62,03 | 25,45 | 66,71 |
| 4 | 10 | 258.658.304 | 230.473.728 | 227.985.464 | 288.538.624 | 79,86 | 51,79 | 83,95 |
| 7 | 10 | 371.003.392 | 286.248.960 | 337.963.112 | 381.554.688 | 94,34 | 68,96 | 105,20 |

El lote formal pasó, pero el margen mínimo observado fue solo 4.806.246 bytes. Además se
conserva una observación adversa precommit: otro lote N7 alcanzó 388.161.536 bytes
y falló 1/20 runs. No se presenta esa observación como evidencia formal del commit,
pero tampoco se descarta. Por margen y variación, N7 no se selecciona.

### Candidato 6

| Concurrencia | Runs | Máximo Private | Mediana Private | Máximo managed | Máximo working set | Mediana req/s | p50 mediana ms | p95 mediana ms |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 10 | 175.075.328 | 136.710.144 | 152.758.128 | 212.824.064 | 36,47 | 20,58 | 58,69 |
| 2 | 10 | 227.848.192 | 155.414.528 | 196.812.280 | 260.657.152 | 60,65 | 23,52 | 76,93 |
| 4 | 10 | 218.779.648 | 204.222.464 | 194.454.320 | 247.451.648 | 71,42 | 54,91 | 97,70 |
| 6 | 10 | 310.337.536 | 269.387.776 | 278.811.528 | 337.850.368 | 87,28 | 61,32 | 119,27 |

N6 conserva 65.472.102 bytes bajo el umbral observado y queda seleccionado solo
para una prueba posterior con hard limit real. **No se cambia todavía el default
productivo de 8 inspecciones/8 MiB.**

## Validación e integridad

- Build Release: 0 errores y 0 advertencias.
- Suite: 121/121 PASS; 0 fallos y 0 omitidos.
- `dotnet format --verify-no-changes`: PASS.
- Los reportes no contienen payload, PAN, selector, direcciones, hostname, usuario,
  rutas personales, PID ni diagnósticos internos.
- SHA-256 de artefactos locales saneados e ignorados por Git:
  - `candidate7-10/isolated.json`: `4979927043A2ABA48D2668EBBC7014C62A86B9A424B7BBCB4AFC73DC120876E4`
  - `candidate7-10/isolated.md`: `3D9A85756F3047186E98BA58F30E969E1A48CB58C176D9B6FFBADD5FF13777B9`
  - `candidate6-10/isolated.json`: `16CFA556163D85DA038588623D0398A56BCD065701E6BFA759FDE3BE024775E8`
  - `candidate6-10/isolated.md`: `16F4D95D075435B96A5D97E362EA90EE73AC3041CAFDA02BB76476A2A18C296B`
  - `build.sanitized.log`: `826B014B1C222E770B5F7F7BAF5CD63E87EAC2C666A3F9F3354E3DC09634482A`
  - `test.sanitized.log`: `2854C2AE6026AB67C46EDB2D58EC8EADDC4686C5E338299CCA25FE76124BBCB2`
  - `format.sanitized.log`: `7EB70257593DA06F682A3DDDA54A9D260D4FC514F645237F5CA74B08F8DA61A6`

## Límites

Private Bytes se muestrea cada 10 ms y puede omitir transitorios. La carga es
local, sintética y closed-loop; no aplica un hard limit de contenedor y omite TLS,
autenticación, red remota, exporters y clientes productivos. La selección de N6
es únicamente el siguiente candidato de validación, no aprobación productiva.
