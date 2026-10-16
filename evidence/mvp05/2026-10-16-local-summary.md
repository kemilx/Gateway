# MVP-05 — evidencia local de carga y memoria

Fecha: 16 de octubre de 2026

Revisión medida: `21527d26f30ec888746ecd114fff4ba46da8e212`

Clasificación: **MEASURED_NOT_SIZED**

Esta evidencia procede del harness publicado en la revisión indicada, con datos
sintéticos `token-dense-v1`, cuerpo exacto de 1 MiB, .NET runtime 10.0.12,
Windows, 12 procesadores lógicos, Workstation GC y modo `Interactive`.

## Memoria determinista del transform

Cada concurrencia se ejecutó en un worker nuevo. `output-ready` prolonga de forma
intencional la coexistencia de captura, DOM, stream transformado y salida. Los deltas
son respecto del baseline del mismo worker, que ya incluye el payload fuente.

| Concurrencia | Reserva lógica | Managed natural en output-ready | Managed post-GC en output-ready | Managed con escritura bloqueada | Managed post-release | Working set delta | Private bytes delta |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 1.048.576 | 6.376.160 | 3.141.816 | 3.199.616 | 1.054.960 | 5.828.608 | 5.459.968 |
| 2 | 2.097.152 | 16.938.488 | 10.485.016 | 10.551.024 | 5.251.104 | 14.233.600 | 15.306.752 |
| 4 | 4.194.304 | 27.320.304 | 25.171.240 | 25.253.680 | 13.643.104 | 25.972.736 | 31.313.920 |
| 8 | 8.388.608 | 69.575.424 | 54.543.784 | 54.650.944 | 30.427.336 | 55.721.984 | 68.837.376 |

Con ocho inspecciones retenidas, la novena devolvió 503, realizó cero lecturas del
origen y no alteró la reserva 8/8 MiB. Después de liberar las escrituras, el
presupuesto volvió a 0/0 y el canario pasó. Se emitieron nueve outcomes —ocho éxitos
y un rechazo de capacidad— y ocho duraciones.

El delta managed natural de 69,6 MB en el checkpoint conservador es aproximadamente
8,3 veces la reserva lógica de 8 MiB. No debe interpretarse como factor universal:
el checkpoint prolonga vidas deliberadamente y el perfil token-dense es sintético.

## Carga HTTP real por YARP

Se ejecutaron 64 solicitudes por escenario y se consumió cada cuerpo completo.
Todas las respuestas fueron HTTP 200, con máscara correcta, un outcome y una
duración por solicitud. Los máximos observados de presupuesto coincidieron con la
concurrencia configurada y cada escenario terminó en 0/0.

| Concurrencia | Total 200 | Solicitudes/s | p50 ms | p95 ms | Máxima ms | Máximo activo/reservado |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 64 | 24,50 | 23,21 | 87,84 | 108,35 | 1 / 1.048.576 |
| 2 | 64 | 30,14 | 65,64 | 85,91 | 94,56 | 2 / 2.097.152 |
| 4 | 64 | 70,39 | 53,91 | 70,70 | 74,31 | 4 / 4.194.304 |
| 8 | 64 | 138,79 | 52,48 | 80,84 | 85,33 | 8 / 8.388.608 |

Estas latencias son closed-loop y no tienen SLO. API, gateway y cliente comparten
proceso; la memoria HTTP es observacional, no una medición aislada del gateway.

## Decisión

**No se cambian los defaults 8 inspecciones/8 MiB.** Falta definir un límite de
memoria por instancia y el headroom requerido, repetir en procesos HTTP aislados e
infraestructura representativa, y ejecutar varias repeticiones antes de decidir
sizing. Que managed heap, working set o private bytes no vuelvan al baseline no
prueba por sí solo una fuga.

## Integridad y privacidad

- Build Release: 0 errores y 0 advertencias.
- Suite: 114/114 PASS.
- Saturación, liberación, canarios y métricas: PASS.
- Los reportes no contienen payload, PAN, selector, URL, hostname, usuario, rutas
  personales ni textos de excepción.
- SHA-256 de los reportes locales crudos ignorados por Git:
  - `memory.json`: `673B586BFDCB5669910DBC7B3E71274766F4BF19FC2257E5B881EF9D03E2612D`
  - `http.json`: `F83D8380DF6E958D2CC635FC03FC02FE94833C33139AD6AA0826C53F11B18163`
  - `build.sanitized.log`: `C0E39FB237362440C36FD50FEE57F7C1AE3CB5A5FE3DB66812075D9F1D639AF0`
  - `test.sanitized.log`: `B31265BEE0AD8B86F73B073CE7096C0FB0542108C6A51224885377DED8BEF4DA`

Los crudos son locales y reproducibles mediante `Gateway.LoadHarness`; este resumen
saneado es la evidencia versionada.
