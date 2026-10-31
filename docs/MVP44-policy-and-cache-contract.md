# MVP-44 — Contrato seguro de políticas, PAN y caché

Alcance local sintético. Este documento fija las decisiones de implementación del
coordinador revisadas por seguridad, no una clasificación de negocio aprobada ni
una autorización de datos reales, piloto o producción. No añade fail-open,
caché compartida, almacenamiento de respuestas, APIs administrativas ni paquetes.

## Políticas: metadata y acciones explícitas

| Campo | Valores exactos | Default compatible | Semántica |
| --- | --- | --- | --- |
| Severity | Low, Medium, High, Critical | High | Clasificación informativa de la política. No determina Mode ni permisos. |
| FailureBehavior | FailClosed | FailClosed | Fallos de inspección rechazan el cuerpo upstream. No se acepta FailOpen. |
| NonInspectableBehavior | ExcludeNonJson | ExcludeNonJson | Exclusión contractual de media types no JSON; no garantiza protección de respuestas arbitrarias. |

La configuración distribuida declara los campos explícitamente. Su ausencia en
políticas anteriores mantiene los defaults, pero un valor presente nulo, vacío,
con espacios, casing diferente, objeto o array no se transforma silenciosamente
en un default: se rechaza. Los campos se copian a la política compilada inmutable
y forman parte de su comparación semántica. Cambiar un valor efectivo exige una
versión distinta de la activa; una actualización inválida conserva la anterior.
No se modifica el constructor posicional público, DTO del dashboard ni schemas
de eventos/OTLP. Esta fase no publica la nueva metadata en esos canales.

Mode sigue siendo Observe, Mask o Block y decide el comportamiento ya existente.
Severity High por default es una elección técnica conservadora, no una decisión
del propietario sobre la severidad real de todas sus rutas.

## PAN: clasificación sin ampliar la detección

`SecondaryPanDetector.Classify` devuelve solamente el enum interno
`PanClassification`: `NotCandidate`, `Candidate` o `HighConfidence`.

- Se considera el **string completo original decodificado**: 13–19 dígitos ASCII,
  longitud total 13–37, con espacios ASCII/guiones individuales entre dígitos.
  No se hace trim, búsqueda de substrings ni normalización.
- Forma válida y Luhn negativo: Candidate. Misma forma y Luhn positivo:
  HighConfidence. Forma no admitida: NotCandidate.
- IsMatch sigue siendo verdadero únicamente para HighConfidence. Por tanto no
  se enmascaran nuevos strings como consecuencia de introducir el enum.
- Las reglas explícitas tienen prioridad y no dependen de Luhn: un PAN explícito
  compatible puede conservar los últimos cuatro aunque el checksum sea negativo.
- No se devuelven/retienen valores, no se emiten nuevos eventos o métricas de
  candidatos y no se interpreta HighConfidence como existencia de una tarjeta.

Email/teléfono mantienen sus formatos conservadores y pruebas existentes. Esta
clasificación no añade un detector universal de PII ni promete cobertura de texto libre.

## Caché: no reutilizar un 304 protegido

Se mantiene la supresión de condicionales/rangos en requests protegidos, petición
de encoding identity y `Cache-Control: no-store`. Eso reduce revalidaciones, pero
no basta para aceptar un 304 inesperado del upstream.

Orden del contrato de respuesta:

1. HEAD y 204 conservan su exclusión sin cuerpo añadido.
2. En cualquier otro método, un 304 de ruta protegida **se reemplaza por 502**,
   con JSON fijo `{"code":"privacy_inspection_failed"}` y no-store. Se limpian
   headers upstream, incluidos cookies, ETag, Last-Modified, digests, firmas y
   marcadores arbitrarios. No se abre/lee el cuerpo upstream ni se reserva
   presupuesto de inspección para rechazarlo; la falta de capacidad no lo convierte
   en un 503 ni permite que vuelva a ser 304. Se respeta cancelación del cliente.
3. Encoding/charset no admitidos y 206 protegido fallan antes de que un cambio
   de media type pueda convertirlos en exclusión no JSON.
4. Un non-JSON normal sigue excluido por contrato; no se declara inspeccionado.
   JSON válido continúa con su límite/captura y Mode. JSON inválido falla cerrado.

El rechazo de 304 se contabiliza como blocked, no como exclusión o inspección
exitosa. Los schemas públicos permanecen iguales. Las rutas passthrough son
expresamente no protegidas y conservan su comportamiento, incluido 304.

Ese contador significa **decisión de bloqueo preflight**, no entrega confirmada
del error: se publica una vez aunque la escritura del 502 falle o se cancele.
Una cancelación ya observada antes de intentar escribir no publica esa decisión.
No se añaden muestras de duración/tamaño de una inspección que no tuvo lugar ni
se adquiere una lease para contabilizarla.

**Aclaración RF-02/CA-07:** no se añade cuerpo a una respuesta que conserve estado
304. La respuesta protegida no conserva ese estado: es un error 502 nuevo. HEAD
no obtiene body por este endurecimiento. Es un cambio intencional respecto a la
exclusión 304 anterior; no es un fallo incidental que deba ocultarse cambiando tests.

No hay cache store ni claves de caché basadas en versión de política: ninguna
respuesta protegida puede revalidarse como 304. Después de una nueva política,
un 304 upstream sigue siendo rechazado. Los proxies externos y el acceso directo
al backend necesitan configuración de red/caché del destino, no quedan controlados
por estos cambios del motor.

## Reversión y límites

La reversión binaria de una configuración que ya declare los campos nuevos exige
retirar esos campos antes de usar un binario antiguo que rechaza claves desconocidas.
Retirar LocalPolicySnapshotFile antes de volver a un binario que no soporte esa
opción; conservar una base compatible. Revertir el tratamiento 304 reabre la
posibilidad condicionada de reutilizar representaciones previas y debe evaluarse,
no hacerse automáticamente para recuperar compatibilidad.

No cambia el presupuesto global, los límites del parser, máscaras, severidades
de negocio, DTO de salida ni autenticación. La campaña de rendimiento se define
por separado en [su perfil y procedimiento](MVP44-performance-campaign.md), con
aceptación de negocio explícitamente pendiente.
