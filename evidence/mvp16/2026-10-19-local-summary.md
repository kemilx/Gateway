# MVP-16 — revalidación sintética local de memoria

**Resultado:** `HARD_LIMIT_LOCAL_CANDIDATE`, 19 de octubre de 2026. No es aprobación de producción ni cambio de defaults.

## Identidad y método

Se midió la base `995e5d53754e0fa3315462f2381591faf23b39b5` **más los cambios locales sin commit** de resiliencia. El commit base por sí solo no identifica el ejecutable medido. El snapshot de 14 archivos tiene SHA256 `CF916E31D84EFD76EE9A4A96C9A56B714F9672AC9EFC9584038F3065E21B3601`; los 14 hashes coincidían antes y después, sin cambios de binarios.

Windows X64, .NET/ASP.NET Core 10.0.12. API, gateway y cliente en procesos separados; gateway nuevo y Job Object de 512 MiB en cada ejecución, asignado antes de reanudarlo. Perfil sintético `token-dense-v1`, cuerpos de 1 MiB, concurrencias 1/2/4/6, diez repeticiones y 64 solicitudes medidas por ejecución. No se repitieron build ni la suite ya vigente.

```powershell
dotnet Gateway.LoadHarness/bin/Release/net10.0/Gateway.LoadHarness.dll hard-limit `
  --output artifacts/mvp05/mvp16-sizing-20261019T232401152Z `
  --requests-per-concurrency 64 --repetitions 10 `
  --memory-limit-mib 512 --headroom-percent 30 --max-concurrent-inspections 6
```

Se ejecutó una sola vez, sin reintentos. El harness exige salida local bajo `artifacts/mvp05`; se conservó ese staging ignorado y se verificó por SHA256 su copia externa. El preflight inicialmente `NOT_RUN` queda conservado como historial, no como ejecución fallida de sizing.

## Resultado

| Comprobación | Resultado |
|---|---:|
| Ejecuciones aprobadas | 40/40 |
| HTTP 200 con longitud y canarios correctos | 2560/2560 |
| Warmups adicionales validados | 80 |
| Outcomes / duraciones | 2560 / 2560 |
| Reservas finales 0/0 y cleanup cooperativo | 40/40 |
| Fallos de enforcement | 0 |
| Máximo Private Bytes | 363.360.256 bytes |
| Máximo commit del proceso en el Job | 363.474.944 bytes |
| Umbral de aceptación con 30% de headroom | 375.809.638 bytes |
| Menor margen frente a ese umbral | 12.334.694 bytes |
| Menor headroom observado | 32,30% |

El margen pasa el criterio, pero es ajustado: no justifica subir concurrencia ni extrapolar estabilidad bajo carga sostenida. No hubo comparación controlada que permita atribuir diferencias frente a evidencia histórica a un cambio concreto.

## Perfil efectivo y límites

- Admisión **8**, inspecciones **6**, presupuesto **8 MiB**, timeout de inspección **10 s**, deadline opcional de headers **desactivado**. La referencia inicial a 120 s se corrigió antes de ejecutar; no se cambió configuración para medir.
- Los defaults operativos siguen siendo admisión 6 y presupuesto 6 MiB. Se observaron como máximo seis inspecciones y 6 MiB reservados; **no se midió una configuración idéntica a todos los defaults**.
- El Job limita commit por proceso; excluye API y cliente, no limita working set ni un contenedor. El pico del Job incluye startup, warmup, carga y apagado cooperativo; el muestreo de Private Bytes cada 10 ms puede omitir transitorios.
- Sin prueba OOM, GC forzada, deadline de headers activo, baseline sin inspección ni nuevo SLA de rendimiento. Esto no completa toda la aceptación RNF-03 ni la regresión de fallos.

Los artefactos crudos permanecen locales en `MVP16-sizing-revalidation`, fuera del commit; el reporte canónico `hard-limit.json` tiene SHA256 `0C500568EE9A478E03139C7948A07DEC73163FA74F0CC32135703B6D7C8D5E44`. El [resumen JSON](2026-10-19-local-summary.json) contiene los valores saneados. No se modificó evidencia histórica ni se hizo commit, push o despliegue.
