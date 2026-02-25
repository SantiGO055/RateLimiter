# Feature Specification: Rate Limiter

**Created**: 2026-02-24

## Behavior Stories & Testing

### Behavior Story 1 - Request permitido con tokens disponibles (Priority: P1)

Un cliente con tokens disponibles en su bucket envía un request a un endpoint protegido. El sistema consume un token, permite el request, y retorna headers informativos con el estado del rate limit.

**Why this priority**: Sin esta funcionalidad no existe rate limiter. Es el happy path que valida que el algoritmo Token Bucket calcula refill, consume tokens, y produce el resultado correcto. Todo lo demás depende de esto.

**Independent Test**: Se puede validar completamente enviando un request HTTP a un endpoint protegido y verificando que retorna 200 con los headers `X-RateLimit-*` correctos.

**Acceptance Scenarios**:

1. **Scenario**: Cliente con bucket lleno hace un request
    - **Given** un cliente con IP `192.168.1.1` que no ha hecho requests previos, y una regla de 10 req/minuto con bucket de capacidad 10
    - **When** envía un GET a `/api/resource`
    - **Then** el response es 200, `X-RateLimit-Limit: 10`, `X-RateLimit-Remaining: 9`, y no incluye `X-RateLimit-Retry-After`

2. **Scenario**: Cliente con tokens parcialmente consumidos hace un request
    - **Given** un cliente que ya consumió 7 de 10 tokens en la ventana actual
    - **When** envía un GET a `/api/resource`
    - **Then** el response es 200, `X-RateLimit-Remaining: 2`

3. **Scenario**: Dos clientes distintos no comparten tokens
    - **Given** cliente A con 1 token restante y cliente B con bucket lleno (10 tokens)
    - **When** ambos envían un request
    - **Then** cliente A recibe `X-RateLimit-Remaining: 0`, cliente B recibe `X-RateLimit-Remaining: 9`

---

### Behavior Story 2 - Request bloqueado con 429 y headers correctos (Priority: P2)

Un cliente que agotó sus tokens recibe HTTP 429 con un body JSON descriptivo y headers que le indican cuándo puede reintentar.

**Why this priority**: Es la segunda mitad del contrato del rate limiter. Sin el rechazo correcto, no hay limitación real. Los headers `Retry-After` son necesarios para que clientes bien implementados puedan hacer backoff.

**Independent Test**: Enviar N+1 requests (donde N es el límite) y verificar que el request N+1 retorna 429 con el body y headers especificados.

**Acceptance Scenarios**:

1. **Scenario**: Cliente excede el límite de requests
    - **Given** un cliente con 0 tokens restantes y una regla de 10 req/minuto
    - **When** envía un GET a `/api/resource`
    - **Then** el response es 429, `Content-Type: application/json`, body contiene `{"error": "rate_limit_exceeded", "message": "Too many requests. Please retry after N seconds."}`, headers incluyen `X-RateLimit-Limit: 10`, `X-RateLimit-Remaining: 0`, `X-RateLimit-Retry-After: N` donde N > 0

2. **Scenario**: Retry-After refleja el tiempo real hasta el próximo token
    - **Given** un cliente con 0 tokens, tasa de refill de 10 tokens/minuto (1 token cada 6 segundos), y último refill hace 2 segundos
    - **When** envía un request
    - **Then** `X-RateLimit-Retry-After` es 4 (segundos restantes hasta el próximo token)

3. **Scenario**: Requests rechazados no consumen tokens
    - **Given** un cliente con 0 tokens
    - **When** envía 5 requests adicionales
    - **Then** los 5 retornan 429, y cuando pasa suficiente tiempo para 1 refill, el siguiente request es 200 con `Remaining: 0` (no se perdieron tokens extra)

---

### Behavior Story 3 - Refill de tokens con el tiempo (Priority: P3)

Los tokens se recargan proporcionalmente al tiempo transcurrido desde el último consumo, sin necesidad de un timer en background. El refill se calcula lazy al evaluar cada request.

**Why this priority**: Sin refill, el rate limiter bloquea permanentemente después de agotar tokens. Es la mecánica central de Token Bucket, pero depende de que P1 y P2 ya funcionen para poder observar el efecto.

**Independent Test**: Enviar requests hasta agotar tokens, esperar un tiempo conocido, y verificar que la cantidad de tokens disponibles corresponde al tiempo transcurrido multiplicado por la tasa de refill.

**Acceptance Scenarios**:

1. **Scenario**: Tokens se recargan después de esperar
    - **Given** un cliente con 0 tokens, capacidad 10, tasa de refill 10 tokens/minuto
    - **When** pasan 30 segundos y el cliente envía un request
    - **Then** el response es 200, `X-RateLimit-Remaining: 4` (5 tokens recargados, 1 consumido por el request actual)

2. **Scenario**: Refill no excede la capacidad del bucket
    - **Given** un cliente con 8 tokens restantes, capacidad 10, tasa de refill 10 tokens/minuto
    - **When** pasan 60 segundos y el cliente envía un request
    - **Then** `X-RateLimit-Remaining: 9` (se llenó a 10, consumió 1), no 17

3. **Scenario**: Refill parcial produce tokens fraccionarios redondeados hacia abajo
    - **Given** un cliente con 0 tokens, tasa de refill 1 token/segundo
    - **When** pasan 500ms y envía un request
    - **Then** el response es 429 (0.5 tokens no es suficiente para consumir 1)

---

### Behavior Story 4 - Concurrencia: múltiples requests simultáneos (Priority: P4)

Múltiples requests del mismo cliente que llegan simultáneamente son procesados de forma thread-safe. No se producen race conditions que permitan más requests que el límite, ni se pierden tokens por escrituras concurrentes.

**Why this priority**: Correcto manejo de concurrencia es un requisito no funcional crítico, pero requiere que toda la lógica secuencial (P1-P3) funcione antes de validar su comportamiento bajo contención.

**Independent Test**: Lanzar N requests concurrentes (ej: 20) contra un límite de 10, y verificar que exactamente 10 reciben 200 y 10 reciben 429.

**Acceptance Scenarios**:

1. **Scenario**: Requests concurrentes respetan el límite
    - **Given** un cliente con bucket lleno de 10 tokens
    - **When** 20 requests del mismo cliente llegan simultáneamente (via `Task.WhenAll`)
    - **Then** exactamente 10 reciben 200 y exactamente 10 reciben 429

2. **Scenario**: No hay tokens fantasma por race conditions
    - **Given** un cliente con 1 token restante
    - **When** 5 requests llegan simultáneamente
    - **Then** exactamente 1 recibe 200 y 4 reciben 429 (no 2 o más reciben 200)

3. **Scenario**: Clientes distintos no se bloquean entre sí
    - **Given** cliente A y cliente B, ambos con 5 tokens
    - **When** 10 requests de cada cliente llegan simultáneamente
    - **Then** cliente A: 5 con 200 y 5 con 429. Cliente B: mismo resultado, independiente de A

---

### Behavior Story 5 - Fail-open: resiliencia ante fallo del store (Priority: P5)

Si el store lanza una excepción (ej: Redis caído, ConcurrentDictionary corrupto), el middleware permite el request en vez de rechazarlo. Un rate limiter roto no debe convertirse en un sistema de denegación total.

**Why this priority**: Es diseño defensivo. En producción, preferís perder rate limiting temporalmente a que una excepción en el store rompa toda la API. Es un nice-to-have.

**Independent Test**: Inyectar un `IRateLimitStore` que lanza `InvalidOperationException` en `GetOrCreateAsync`, enviar un request, y verificar que retorna 200 (no 500).

**Acceptance Scenarios**:

1. **Scenario**: Store lanza excepción, request pasa
    - **Given** un `IRateLimitStore` que lanza `InvalidOperationException` en cualquier operación
    - **When** un cliente envía un request
    - **Then** el response es 200 (no 429 ni 500), y no incluye headers `X-RateLimit-*`

2. **Scenario**: Algoritmo lanza excepción, request pasa
    - **Given** un `IRateLimitAlgorithm` que lanza `TimeoutException`
    - **When** un cliente envía un request
    - **Then** el response es 200, el error se loguea como Warning

3. **Scenario**: Fail-open es configurable
    - **Given** `RateLimitOptions.FailOpen = false` y un store que lanza excepción
    - **When** un cliente envía un request
    - **Then** el response es 503 Service Unavailable (el operador eligió fail-closed)

---

### Edge Cases

- **Clock skew**: Si `DateTime.UtcNow` retrocede (ej: ajuste NTP), el refill no debe producir tokens negativos. Se usa `max(0, elapsed)`.
- **Key extremadamente largo**: Un `clientKey` compuesto (IP + endpoint + userId) podría ser muy largo. No se trunca, pero el store debe manejar keys de longitud arbitraria.
- **Primer request de un cliente**: El bucket se crea con capacidad máxima. El primer request siempre pasa (asumiendo capacidad >= 1).
- **Regla con límite 0**: Si `Limit = 0`, todo request se rechaza. Es un caso válido (endpoint deshabilitado).
- **Regla no encontrada para el endpoint**: Si no hay regla configurada para un endpoint, el request pasa sin rate limiting.
- **Overflow en refill**: Si pasa mucho tiempo entre requests (ej: días), `tokensToAdd` podría ser un número enorme. Se clampea a `BucketCapacity` antes de sumar.

## Requirements

### Functional Requirements

- **FR-001**: El sistema DEBE limitar requests por cliente usando el algoritmo Token Bucket con refill lazy.
- **FR-002**: El sistema DEBE retornar HTTP 429 con body JSON `{"error": "rate_limit_exceeded", "message": "..."}` cuando un cliente excede su límite.
- **FR-003**: El sistema DEBE incluir headers `X-RateLimit-Limit`, `X-RateLimit-Remaining` en toda respuesta a endpoints protegidos.
- **FR-004**: El sistema DEBE incluir header `X-RateLimit-Retry-After` (en segundos) solo en respuestas 429.
- **FR-005**: Las reglas de rate limiting DEBEN ser configurables por endpoint via `appsettings.json`.
- **FR-006**: El sistema DEBE identificar clientes por IP de origen (`HttpContext.Connection.RemoteIpAddress`).
- **FR-007**: El sistema DEBE permitir bursts hasta la capacidad del bucket sin penalización.
- **FR-008**: Endpoints sin regla configurada DEBEN pasar sin rate limiting.
- **FR-009**: El sistema DEBE ser fail-open por defecto (configurable a fail-closed via `RateLimitOptions.FailOpen`).

### Key Entities

- **BucketState**: Estado mutable de un bucket por cliente. Contiene `AvailableTokens` (double) y `LastRefillTimestamp` (DateTime). Es la unidad mínima de almacenamiento por key. Se crea lazy en el primer request.
- **RateLimitRule**: Configuración inmutable de un endpoint. Define `Limit` (requests por ventana), `Window` (duración), `BucketCapacity` (máximo de tokens), `RefillRate` (tokens/segundo). Se lee de `appsettings.json` al iniciar.
- **RateLimitResult**: Resultado inmutable de una evaluación. Contiene `IsAllowed`, `Limit`, `Remaining`, `RetryAfterSeconds`. Se produce por el algoritmo y se consume por el middleware para generar headers.
- **ClientRequestInfo**: Datos extraídos del `HttpContext` para identificar un request. Contiene `ClientIp`, `Endpoint`, `HttpMethod`. Se usa para construir el key del store.

## Success Criteria

### Measurable Outcomes

- **SC-001**: Todos los tests unitarios del algoritmo Token Bucket pasan, cubriendo refill, consumo, agotamiento, y edge cases de tiempo.
- **SC-002**: Tests de concurrencia demuestran que N requests simultáneos contra un límite L producen exactamente L respuestas 200 y N-L respuestas 429.
- **SC-003**: Tests de integración validan el flujo completo HTTP: request -> middleware -> algoritmo -> response con headers correctos.
- **SC-004**: El rate limiter agrega menos de 1ms de latencia al request (medido como overhead del middleware en un benchmark simple).
- **SC-005**: El sistema opera correctamente con fail-open cuando el store lanza excepciones.

---

## Interface Contracts

### `IRateLimitAlgorithm`

```csharp
public interface IRateLimitAlgorithm
{
    Task<RateLimitResult> EvaluateAsync(
        string clientKey, RateLimitRule rule, CancellationToken ct = default);
}
```

**Precondiciones**:
- `clientKey` no es null ni vacío. Es la responsabilidad del caller (el middleware) construir un key válido.
- `rule` no es null. Sus campos `Limit` >= 0, `Window` > TimeSpan.Zero, `RefillRate` > 0, `BucketCapacity` >= 1.
- Si `clientKey` no tiene un bucket existente, el algoritmo lo crea internamente con capacidad máxima.

**Postcondiciones**:
- Retorna un `RateLimitResult` no null.
- `result.Limit` == `rule.Limit` (siempre refleja la regla aplicada).
- Si `result.IsAllowed == true`: se consumió exactamente 1 token del bucket del cliente. `result.Remaining` >= 0. `result.RetryAfterSeconds` es null.
- Si `result.IsAllowed == false`: no se consumieron tokens. `result.Remaining` == 0. `result.RetryAfterSeconds` > 0 y refleja los segundos hasta el próximo token disponible.
- La operación es **atómica**: no existe un estado intermedio observable donde el token fue consultado pero no consumido.

**Comportamiento ante excepciones**:
- El algoritmo NO captura excepciones del store. Si `IRateLimitStore.GetOrCreateAsync` falla, la excepción propaga al caller.
- El algoritmo no lanza excepciones propias bajo uso normal. Solo propaga las del store.
- Es responsabilidad del middleware decidir qué hacer con la excepción (fail-open o fail-closed).

---

### `IRateLimitStore`

```csharp
public interface IRateLimitStore
{
    Task<T> GetOrCreateAsync<T>(string key, Func<T> factory) where T : class;
    Task RemoveExpiredEntriesAsync(CancellationToken ct = default);
}
```

#### `GetOrCreateAsync<T>`

**Precondiciones**:
- `key` no es null ni vacío.
- `factory` no es null. Se invoca solo si el key no existe. La factory debe retornar un objeto no null.
- `T` debe ser un tipo por referencia (constraint `where T : class`) para permitir locking por instancia.

**Postcondiciones**:
- Retorna un objeto no null de tipo `T`.
- Si el key ya existía, retorna la instancia existente (misma referencia). `factory` no se invoca.
- Si el key no existía, invoca `factory` una sola vez, almacena el resultado, y lo retorna.
- La operación es **thread-safe**: dos llamadas concurrentes con el mismo key nunca crean dos instancias distintas. Ambas reciben la misma referencia.
- El objeto retornado puede ser mutado por el caller (el algoritmo), quien es responsable de su propio locking si necesita atomicidad en la mutación.

**Comportamiento ante excepciones**:
- Si `factory` lanza una excepción, esta propaga al caller. El key no se almacena.
- Implementaciones distribuidas (futuro Redis) pueden lanzar `TimeoutException` o `RedisConnectionException`. El contrato no especifica qué excepciones concretas; el caller debe manejar `Exception` genéricamente.

#### `RemoveExpiredEntriesAsync`

**Precondiciones**:
- `ct` puede ser `CancellationToken.None`.

**Postcondiciones**:
- Elimina del store entries cuyo último acceso excede un umbral configurable (ej: 2x la ventana más larga).
- Es una operación best-effort: si se cancela via `ct`, puede dejar entries sin limpiar.
- No afecta entries que están siendo usadas activamente.

**Comportamiento ante excepciones**:
- Lanza `OperationCanceledException` si `ct` es cancelado.
- Errores internos (ej: corrupción) se loguean pero no propagan — la limpieza es best-effort.
