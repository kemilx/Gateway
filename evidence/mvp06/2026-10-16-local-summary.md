# MVP-06 — sizing local aislado

Fecha: 16 de octubre de 2026

Revisión medida: `5444da71e2807d54ab04ff79eca3e03c8293dfeb`

Clasificación: **SIZING_FAILED**

## Objetivo y resultado

Se evaluó un objetivo de 512 MiB por proceso gateway con 30% de headroom. El
criterio exige que Private Bytes permanezca en o por debajo de **375.809.638
bytes** en todas las repeticiones.

La funcionalidad pasó: 12 ejecuciones, 768/768 respuestas HTTP 200, 768 outcomes,
768 duraciones, todos los canarios y todas las liberaciones de presupuesto. El
sizing no pasó porque una de tres repeticiones con concurrencia 8 alcanzó
**382.943.232 bytes**, 7.133.594 bytes sobre el máximo permitido. El headroom
observado en ese pico fue aproximadamente 28,67%, inferior al 30% requerido.

## Agregados

| Concurrencia | Runs | Máximo Private Bytes | Mediana Private Bytes | Máximo managed | Máximo working set | Mediana req/s | Mediana p50 ms | Mediana p95 ms | Todos pasan |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| 1 | 3 | 154.169.344 | 129.687.552 | 125.931.584 | 192.163.840 | 48,76 | 14,74 | 48,89 | Sí |
| 2 | 3 | 158.965.760 | 130.633.728 | 111.104.504 | 196.456.448 | 71,86 | 20,30 | 59,50 | Sí |
| 4 | 3 | 219.004.928 | 213.766.144 | 195.624.344 | 247.287.808 | 75,81 | 51,71 | 72,54 | Sí |
| 8 | 3 | 382.943.232 | 357.535.744 | 354.515.472 | 390.377.472 | 137,84 | 54,40 | 70,05 | **No** |

La repetición fallida conservó sus invariantes funcionales: 64/64 respuestas 200,
64 outcomes, 64 duraciones, máscara y longitud correctas, y presupuesto liberado.
El único criterio fallido fue el umbral de Private Bytes.

## Decisión

**El candidato 512 MiB/30% no queda aprobado.** No se cambian los defaults de 8
inspecciones/8 MiB y esta evidencia no autoriza producción. Un resultado previo
por debajo del umbral no compensa una repetición fallida cuando el contrato exige
que pasen todas.

Antes de proponer un cambio separado se requiere decidir entre reducir la presión
concurrente, aumentar el límite por instancia o revisar el headroom, y después
repetir en un entorno representativo con límite real de contenedor, carga sostenida
y procesos/telemetría de producción.

## Validación e integridad

- Build Release: 0 errores y 0 advertencias.
- Suite: 117/117 PASS; 0 fallos y 0 omitidos.
- `dotnet format --verify-no-changes`: PASS.
- Los reportes no contienen payload, PAN, selector, direcciones, hostname, usuario,
  rutas personales, PID ni diagnósticos internos.
- SHA-256 de artefactos locales saneados e ignorados por Git:
  - `isolated.json`: `7943C14B87A1D7088F0853834EEE4CF6BFB78010AC3D9BCC4D2B8EDFA8B43830`
  - `isolated.md`: `98B085F9BEE0705318135F419A7114EB52899C544C666A0E1D269FD9CF24D596`
  - `build.sanitized.log`: `D28D6F2CB1BF6326A54FDDBB40D23B14C300AD23714EC18F2451873B06B8D1EE`
  - `test.sanitized.log`: `31A7D14B49EAC3788B523EC495EE1651661C9AB91C69483BEA60B24EA10C0D94`
  - `format.sanitized.log`: `7EB70257593DA06F682A3DDDA54A9D260D4FC514F645237F5CA74B08F8DA61A6`

## Límites de interpretación

Private Bytes se muestrea cada 10 ms y puede omitir transitorios más breves. La
carga es local, sintética y closed-loop; no aplica un hard limit de contenedor y
omite TLS, autenticación, red remota, exporters y clientes productivos. Un lote de
tres repeticiones no establece una capacidad universal.
