# Gateway.LoadHarness — MVP-05

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
