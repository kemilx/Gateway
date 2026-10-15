# Especificación de Requisitos de Software (SRS)
## YARP Data Privacy Gateway

**Versión:** 0.1 — Borrador para validación<br>
**Fecha:** 15 de octubre de 2026<br>
**Estado:** Propuesta; no constituye aprobación de arquitectura ni certificación de cumplimiento.

## 1. Propósito

Definir los requisitos de un proxy inverso que detecte y mitigue exposiciones accidentales de información personal y otros datos sensibles en respuestas JSON de APIs internas. El documento servirá de base para implementación, pruebas y aceptación.

El gateway es una defensa adicional. No sustituye la autorización de las APIs, la minimización de datos, los DTO de salida, la protección de bases de datos ni la corrección de vulnerabilidades en origen. No evita filtraciones en logs internos, respaldos, volcados o canales que no atraviesen el gateway.

## 2. Objetivos y actores

### 2.1 Objetivos

- Centralizar políticas de inspección y enmascaramiento.
- Detectar exposiciones y atribuirlas a una ruta y servicio sin registrar los valores sensibles.
- Permitir adopción gradual mediante observación, enmascaramiento y bloqueo.
- Mantener un consumo de recursos acotado y medir el costo de inspección.

### 2.2 Actores

| Actor | Responsabilidad |
|---|---|
| Cliente de API | Consume respuestas a través del gateway. |
| API interna | Atiende solicitudes y conserva su autorización y validaciones de negocio. |
| Operador autorizado | Despliega configuración, consulta salud y atiende fallos operativos. |
| Responsable de seguridad | Define clasificación, reglas y tratamiento ante fallos. |
| Desarrollador de API | Investiga hallazgos y corrige el origen de las exposiciones. |

## 3. Alcance

### 3.1 MVP propuesto

- ASP.NET Core y YARP como proxy inverso; versión objetivo propuesta: .NET 10 LTS, sujeta a validación de compatibilidad y soporte al iniciar implementación.
- Inspección de respuestas completas con tipo `application/json`, incluidos parámetros como `charset=utf-8`.
- Reglas por identificador de ruta YARP y ruta de campo JSON; soporte de objetos y elementos de arrays.
- Transformaciones de email, teléfono y número de tarjeta (PAN).
- Detección secundaria opcional de PAN en valores string mediante formato y validación Luhn.
- Modos `Observe`, `Mask` y `Block`, configurables por ruta.
- Políticas versionadas en archivos y cargadas en memoria.
- Telemetría sin valores originales y endpoints de salud.
- Límite inicial propuesto de cuerpo inspeccionable: 1 MiB (1.048.576 bytes), configurable.

### 3.2 Fuera del MVP

- Panel administrativo, API pública de administración y persistencia SQL.
- Decisiones de visibilidad basadas en roles, scopes, finalidad o identidad del consumidor.
- NLP, clasificación médica, nombres/direcciones en texto libre, OCR y detección universal de PII.
- Inspección de solicitudes, headers, archivos, gRPC, WebSockets, SSE, NDJSON o JSON transmitido indefinidamente.
- Inspección de cuerpos comprimidos o cifrados a nivel de aplicación.
- Validación Luhn como prueba de que una tarjeta existe o está activa.
- Garantía automática de cumplimiento GDPR, PCI DSS o HIPAA.

## 4. Supuestos, restricciones y decisiones pendientes

- Las APIs conservan la autenticación y autorización existentes; el gateway no concede permisos adicionales.
- Para proteger realmente un flujo, el entorno debe impedir el acceso externo directo a las APIs. Este control requiere configuración de red fuera del motor de censura.
- En rutas inspeccionadas se solicitará al upstream contenido sin compresión. Si aun así llega comprimido, se aplicará la política de contenido no inspeccionable; no se lo tratará como JSON inspeccionado.
- Las políticas del MVP son uniformes para todos los consumidores de una ruta. Las rutas que necesiten entregar datos completos a ciertos consumidores no deben activar enmascaramiento indiscriminado.
- Los límites, formatos de máscaras y presupuestos de rendimiento de este documento son propuestas, pendientes de aceptación.
- Deben aprobarse antes de producción: rutas protegidas, severidades, catálogo de reglas, retención de telemetría, política de fallos, capacidad objetivo y propietario operativo.

## 5. Requisitos funcionales

### RF-01 — Enrutamiento
El sistema deberá enrutar solicitudes a las APIs configuradas en YARP, conservando métodos, parámetros y semántica HTTP salvo las modificaciones explícitas de esta especificación. No deberá modificar cuerpos de solicitudes en el MVP.

### RF-02 — Selección de respuestas
El sistema deberá seleccionar la política mediante el identificador estable de ruta YARP, no mediante URLs completas. Deberá evaluar método, estado HTTP, tipo de contenido, codificación y presencia de cuerpo. Las respuestas de error JSON también deberán ser inspeccionables. `HEAD`, `204` y `304` no deberán recibir un cuerpo añadido.

### RF-03 — Configuración de políticas
Cada política deberá declarar: identificador, versión, rutas aplicables, modo, selectores de campos, transformaciones/detectores, severidad, límite de tamaño y comportamiento ante fallo o contenido no inspeccionable. Una referencia desconocida, selector inválido o configuración contradictoria deberá invalidar la nueva configuración.

### RF-04 — Selectores JSON
El motor deberá soportar una sintaxis limitada y documentada de rutas: propiedades de objetos y comodín de arrays, por ejemplo `$.payment.cardNumber` y `$.users[*].email`. No se prometerá compatibilidad completa con JSONPath. Las propiedades serán sensibles a mayúsculas; nombres que requieran escape deberán soportarse mediante una sintaxis documentada o rechazarse al validar la política.

### RF-05 — Enmascaramiento
En modo `Mask`, el motor deberá sustituir los valores seleccionados conservando un documento JSON válido, su estructura y los valores no afectados semánticamente. No se exige conservar espaciado ni representación textual exacta. Reglas iniciales propuestas:

| Tipo | Regla propuesta | Ejemplo |
|---|---|---|
| Email | Sustituir íntegramente la parte local por `***`; conservar dominio. | `juan@empresa.com` → `***@empresa.com` |
| PAN | Conservar únicamente los últimos cuatro dígitos; ocultar el resto, conservando separadores admitidos. | `4532-1111-2222-3333` → `****-****-****-3333` |
| Teléfono | Conservar únicamente los últimos cuatro dígitos; ocultar los demás y conservar separadores. | `+1-809-555-0199` → `+*-***-***-0199` |

Estas máscaras siguen revelando información parcial y no constituyen anonimización. Los valores cortos o incompatibles en campos declarados sensibles deberán sustituirse por `[REDACTED]`; los valores `null` permanecerán como `null`. Los tipos no string en campos sensibles deberán bloquearse o redactarse según una política explícita; nunca ignorarse silenciosamente. Redactarlos puede cambiar el tipo y deberá considerarse en el contrato de la ruta.

### RF-06 — Detección secundaria
Cuando esté habilitada, la detección secundaria deberá examinar valores string, no el JSON serializado completo, y distinguir hallazgos de alta confianza de candidatos. Para PAN combinará formato y Luhn. Un resultado Luhn negativo no impedirá redactar un campo declarado explícitamente como sensible. Los formatos de email/teléfono cubiertos deberán documentarse; no se garantiza cobertura internacional universal.

### RF-07 — Modos
- `Observe`: inspeccionar y emitir hallazgos; entregar el cuerpo original. Este modo permite exposición deliberadamente y requiere aprobación para su uso con datos reales.
- `Mask`: aplicar las transformaciones configuradas antes de entregar el cuerpo.
- `Block`: ante un hallazgo configurado para bloqueo, descartar el cuerpo original y devolver una respuesta genérica `502` con código estable `privacy_response_blocked`.

### RF-08 — Fallos y contenido no inspeccionable
Las rutas protegidas deberán usar por defecto `fail-closed`: ante JSON inválido, límite excedido, fallo del inspector o codificación no admitida, no enviar el cuerpo upstream y responder `502` con código `privacy_inspection_failed`. Los valores de upstream no aparecerán en el error. Las cancelaciones del cliente no exigirán generar una respuesta adicional.

`fail-open` solo se permitirá mediante configuración explícita y auditable, emitiendo un evento de omisión. Las rutas de passthrough se identificarán expresamente como no protegidas. Los formatos sin cuerpo y los excluidos por contrato se distinguirán de un fallo de inspección.

### RF-09 — Límites y atomicidad
El sistema deberá comprobar el tamaño real leído, sin confiar únicamente en `Content-Length`. No deberá enviar bytes del cuerpo upstream al cliente antes de terminar la inspección en rutas `Mask` o `Block`. Esto permitirá bloquear de forma efectiva un hallazgo o fallo situado al final del documento. Ante exceso de tamaño deberá detener la captura y aplicar RF-08; no truncar y entregar JSON parcial.

### RF-10 — Metadatos HTTP y caché
Al cambiar el cuerpo, el sistema deberá eliminar o recalcular `Content-Length` y retirar validadores, digests o firmas dependientes del contenido original. No deberá entregar una codificación que no corresponda a los bytes enviados. Las rutas protegidas del MVP no usarán caché compartida de respuestas ni reutilizarán un `304` upstream sin una estrategia aprobada; la configuración deberá evitar que una respuesta cruda o una versión anterior de política eluda la inspección.

### RF-11 — Telemetría
El sistema deberá emitir contadores de respuestas inspeccionadas, hallazgos, máscaras, bloqueos, omisiones y errores; además de latencia y tamaño. Los eventos incluirán identificador de ruta, servicio, regla, versión de política y correlación técnica. No incluirán valores sensibles, cuerpos, tokens, query strings ni rutas concretas con identificadores personales. Los identificadores de correlación deberán validarse y acotarse; las métricas usarán etiquetas de cardinalidad limitada.

### RF-12 — Carga y actualización de políticas
La configuración se validará antes de activarse y se publicará como una instantánea inmutable. Cada solicitud utilizará una sola versión. Si una actualización es inválida se conservará la última versión válida y se notificará el error. Al arrancar sin una configuración válida para rutas protegidas, la instancia no estará lista para recibir tráfico protegido.

### RF-13 — Salud
El sistema deberá exponer comprobaciones separadas de proceso vivo y disponibilidad operativa. La disponibilidad deberá reflejar la presencia de políticas válidas. Los endpoints no expondrán contenido de políticas sensible, secretos o topología detallada al público.

## 6. Requisitos no funcionales

| ID | Requisito verificable |
|---|---|
| RNF-01 Seguridad | TLS en el borde; protección del tramo interno según el modelo de amenazas. Archivos de política modificables únicamente por operadores autorizados. Ningún secreto en el repositorio. |
| RNF-02 Memoria | Buffering acotado por respuesta y presupuesto global de inspecciones concurrentes; al agotarse el presupuesto se rechazará sin capturar más cuerpos, con error genérico `503`. Se medirán buffers de entrada/salida y overhead. |
| RNF-03 Rendimiento | Medir p50/p95/p99, throughput, CPU, asignaciones y memoria frente a YARP sin inspección. No se promete procesamiento sin asignaciones. El umbral numérico de aceptación se fijará con entorno y carga aprobados antes de la prueba de rendimiento. |
| RNF-04 Resiliencia | Aplicar timeout, cancelación y liberación de buffers en éxito, error y desconexión. La falla de telemetría no desactivará la inspección ni producirá colas sin límite. |
| RNF-05 Parser | Limitar profundidad JSON, tamaño total y complejidad de detección. Contemplar propiedades duplicadas para no dejar valores sensibles sin inspeccionar; procesar todas las coincidencias o rechazar el documento. |
| RNF-06 Compatibilidad | Probar Unicode, escapes, arrays, propiedades repetidas y cuerpos fragmentados. Documentar tipos y protocolos excluidos. |
| RNF-07 Privacidad | No persistir cuerpos ni valores detectados. Desactivar captura de cuerpos en logs/trazas y revisar diagnósticos y dumps; su protección operativa no queda resuelta solo por el gateway. |
| RNF-08 Mantenibilidad | Separar enrutamiento, políticas, detectores, transformaciones y telemetría; mantener pruebas por regla y por integración. |
| RNF-09 Operación | Configuración y artefactos versionados, reversión de política validada y despliegue sin acceso público directo al backend protegido. |

## 7. Arquitectura propuesta y persistencia

```text
Cliente → YARP → API interna
             ← Respuesta upstream
             → Selección de política
             → Captura acotada + inspección
             → Observe / Mask / Block → Cliente

Archivo versionado → Validación → Instantánea de políticas en memoria
Motor → Métricas y eventos sanitizados → Plataforma de observabilidad
```

La implementación deberá verificar mediante una prueba de integración cómo interceptar el cuerpo dentro del pipeline de YARP. No se asumirá que un transform estándar de headers permite transformar el cuerpo.

El MVP prioriza captura acotada frente a streaming de salida: facilita bloquear sin haber expuesto bytes. Puede usarse pooling o `RecyclableMemoryStream`, sin afirmar que eliminan el consumo de memoria. `Utf8JsonReader`/`Utf8JsonWriter` son candidatos para evitar deserializar objetos de negocio; deben manejarse correctamente buffers y estado del parser. `GeneratedRegex` será una optimización selectiva, no una garantía de detección o de cero asignaciones.

### 7.1 Base de datos
No es obligatoria para el MVP. Las políticas residirán en archivos versionados y las solicitudes no consultarán una BD. La telemetría se enviará a una plataforma de observabilidad con acceso y retención definidos antes de producción.

En una fase posterior, SQL Server o PostgreSQL podría almacenar políticas, versiones, asociaciones de rutas e historial administrativo. Un plano de administración cargaría versiones validadas en cada instancia; la BD no formaría parte del procesamiento de cada respuesta. La publicación multiinstancia, permisos administrativos y reversión requerirán requisitos adicionales. No se almacenarán payloads originales en esa BD.

## 8. Casos de uso principales

| Caso | Flujo y resultado |
|---|---|
| CU-01 Enmascarar | Una API devuelve un campo configurado; el gateway inspecciona, sustituye, ajusta metadatos y entrega JSON válido. |
| CU-02 Observar | El gateway detecta y registra metadatos sanitizados; el cliente recibe el cuerpo original. |
| CU-03 Bloquear | Una regla crítica coincide; el cliente recibe solo el error genérico y nunca el cuerpo original. |
| CU-04 Fallo de inspección | El cuerpo excede el límite o es inválido; se aplica la política de fallo y se registra el motivo sin contenido. |
| CU-05 Actualizar reglas | El operador publica una configuración; se valida y activa atómicamente, o se rechaza conservando la anterior. |

## 9. Criterios de aceptación y pruebas

Se utilizarán exclusivamente datos sintéticos. Los resultados deberán identificar versión de código, política, entorno y comandos de ejecución.

| ID | Escenario y resultado esperado | Requisitos |
|---|---|---|
| CA-01 | En `Mask`, email/PAN/teléfono en objetos y arrays coinciden con las máscaras aprobadas; el resto del documento permanece semánticamente equivalente. | RF-04–06 |
| CA-02 | En `Observe`, el cuerpo original no cambia y se emite un hallazgo sin el valor detectado. | RF-07, RF-11 |
| CA-03 | Un hallazgo al final de un cuerpo en `Block` produce `502` y cero bytes del cuerpo upstream en el cliente. | RF-07, RF-09 |
| CA-04 | JSON inválido, comprimido no soportado o tamaño superior al límite produce la política de fallo; se verifica también sin `Content-Length` y con transferencias fragmentadas. | RF-08–09 |
| CA-05 | Probar tamaños justo por debajo, igual y por encima del límite; no hay truncamiento ni buffering ilimitado. | RF-09, RNF-02 |
| CA-06 | Actualización inválida conserva versión anterior; actualización válida no mezcla versiones dentro de una solicitud. Arranque inválido no está listo. | RF-12–13 |
| CA-07 | Transformaciones no conservan longitudes, validadores o firmas obsoletas; `HEAD`, `204` y `304` no incorporan cuerpo. Verificar política de caché. | RF-02, RF-10 |
| CA-08 | Unicode, escapes, nulos, tipos inesperados, duplicados y profundidad excesiva cumplen el comportamiento documentado sin fugas ni corrupción. | RF-05, RNF-05–06 |
| CA-09 | Revisar logs, trazas, métricas y errores con marcadores sintéticos: no contienen cuerpos, valores, credenciales ni URLs personales. | RF-11, RNF-07 |
| CA-10 | Desconexiones, timeouts y saturación liberan recursos; agotamiento del presupuesto retorna rechazo controlado. | RNF-02, RNF-04 |
| CA-11 | La prueba integrada atraviesa cliente → YARP → API de prueba real; no se acepta únicamente un mock del motor. | RF-01–10 |
| CA-12 | Comparar carga con/sin inspección usando cuerpos y concurrencia representativos; cumplir presupuestos previamente aprobados y documentar falsos positivos/negativos del corpus. | RNF-03 |

## 10. Riesgos y mitigaciones

| Riesgo | Mitigación o límite |
|---|---|
| Falsos negativos | Reglas explícitas de campos, corpus negativo/positivo y corrección en origen; no prometer detección universal. |
| Falsos positivos o ruptura de clientes | Contratos por ruta, pruebas de consumidores y observación controlada antes de activar máscaras. |
| Agotamiento de memoria o aumento de latencia | Límites por cuerpo, presupuesto global, timeouts y pruebas de saturación. |
| Filtración por fallback | Fail-closed predeterminado y excepciones fail-open explícitamente aprobadas. |
| Exposición por otro canal | Control de red y revisión de logs, errores, backups y acceso directo a APIs. |
| Políticas obsoletas entre instancias | Versionado visible; operación de publicación y convergencia definida antes de escalar. |
| Reidentificación mediante máscara parcial | Minimizar fragmentos visibles; permitir redacción total para campos/rutas que lo requieran. |

## 11. Fases y definición de terminado

1. **Validación del contrato:** aprobar rutas, máscaras, política de fallos, recursos y catálogo de datos.
2. **Prueba técnica:** verificar intercepción real de YARP, atomicidad y límites con API sintética.
3. **MVP:** implementar políticas en archivos, modos, telemetría y pruebas de aceptación.
4. **Evaluación operativa:** revisión independiente de código y seguridad, prueba de carga y despliegue piloto autorizado.
5. **Evolución opcional:** panel, BD, distribución dinámica y políticas dependientes de identidad con diseño específico.

El MVP estará terminado cuando los criterios aplicables estén ejecutados y aprobados, los presupuestos de rendimiento estén satisfechos y no existan hallazgos altos/críticos sin resolver o sin excepción explícita del responsable autorizado. Crear este SRS no implica implementar, validar ni desplegar el sistema. Publicar en producción requiere autorización separada de destino y alcance.

## 12. Glosario

- **PII:** información que identifica o permite identificar a una persona, según contexto.
- **PAN:** número principal de cuenta de una tarjeta de pago.
- **Enmascaramiento:** ocultación parcial o total de un valor; no equivale por sí sola a anonimización.
- **Upstream:** API interna cuya respuesta recibe el proxy.
- **Fail-closed / fail-open:** bloquear o permitir la respuesta cuando no puede completarse la inspección.
- **SRS:** especificación de requisitos de software.
