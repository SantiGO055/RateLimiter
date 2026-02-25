# Implementation Plan: Rate Limiter

**Date**: 2026-02-24
**Spec**: [specs/rate-limiter/spec.md](./spec.md)

## Summary

Rate limiter HTTP middleware usando Token Bucket con refill lazy, implementado sobre ASP.NET Core Minimal API con 3 capas (Domain, Infrastructure, Api) sin capa Application (eliminada como shallow module). Storage in-memory con `ConcurrentDictionary`, preparado para swap a Redis.

## Technical Context

**Language/Version**: C# / .NET 10
**Primary Dependencies**: ASP.NET Core (Minimal API), Microsoft.Extensions.Options
**Storage**: `ConcurrentDictionary<string, object>` in-memory (swappeable a Redis via `IRateLimitStore`)
**Testing**: xUnit 2.9+, FluentAssertions 7+, Microsoft.AspNetCore.Mvc.Testing
**Target Platform**: Linux/Windows server, contenedor Docker
**Project Type**: Web application (multi-proyecto .NET solution)
**Performance Goals**: < 1ms overhead por request en el middleware
**Constraints**: Thread-safe sin lock global, fail-open por defecto, zero allocations en hot path donde sea posible
**Scale/Scope**: Prototipo funcional, un algoritmo (Token Bucket), un store (in-memory)

## Project Structure

### Documentation

```text
specs/rate-limiter/
├── plan.md              # This file
└── spec.md              # Behavior stories, requirements, interface contracts
```

### Source Code

```text
rate-limiter/
├── RateLimiter.sln
├── DESIGN.md
│
├── src/
│   ├── RateLimiter.Domain/
│   │   ├── RateLimiter.Domain.csproj
│   │   ├── IRateLimitAlgorithm.cs
│   │   ├── IRateLimitStore.cs
│   │   ├── RateLimitRule.cs
│   │   ├── RateLimitResult.cs
│   │   ├── ClientRequestInfo.cs
│   │   ├── BucketState.cs
│   │   └── Algorithms/
│   │       └── TokenBucketAlgorithm.cs
│   │
│   ├── RateLimiter.Infrastructure/
│   │   ├── RateLimiter.Infrastructure.csproj
│   │   └── Storage/
│   │       └── InMemoryRateLimitStore.cs
│   │
│   └── RateLimiter.Api/
│       ├── RateLimiter.Api.csproj
│       ├── Program.cs
│       ├── appsettings.json
│       ├── Middleware/
│       │   └── RateLimitMiddleware.cs
│       └── Configuration/
│           └── RateLimitOptions.cs
│
└── tests/
    └── RateLimiter.Tests/
        ├── RateLimiter.Tests.csproj
        ├── Unit/
        │   ├── TokenBucketAlgorithmTests.cs
        │   └── InMemoryRateLimitStoreTests.cs
        └── Integration/
            └── RateLimitMiddlewareTests.cs
```

**Structure Decision**: 3 proyectos de producción + 1 de tests. Domain no referencia nada. Infrastructure referencia Domain. Api referencia Domain e Infrastructure. Tests referencia los 3.

---

## Phase 1: Setup (Scaffolding)

**Purpose**: Crear la solución .NET 10, los 4 proyectos, referencias entre ellos, y dependencias NuGet. Al terminar, `dotnet build` compila sin errores.

- [ ] T001 Crear solución y proyectos .NET 10
  - **Archivo**: `rate-limiter/RateLimiter.sln`
  - **Acción**: `dotnet new sln`, crear 4 proyectos (`classlib` para Domain e Infrastructure, `web` para Api, `xunit` para Tests)
  - **Done**: `dotnet build RateLimiter.sln` compila sin errores, los 4 proyectos aparecen en la solución
  - **Riesgo**: Asegurar que el target framework sea `net9.0` en todos los `.csproj`

- [ ] T002 Configurar referencias entre proyectos
  - **Archivos**: Todos los `.csproj`
  - **Acción**: Infrastructure → Domain, Api → Domain + Infrastructure, Tests → Api + Domain + Infrastructure
  - **Done**: `dotnet build` resuelve todas las dependencias entre proyectos sin errores

- [ ] T003 Agregar dependencias NuGet
  - **Archivos**: `RateLimiter.Tests.csproj`, `RateLimiter.Api.csproj`
  - **Paquetes Tests**: `xunit`, `xunit.runner.visualstudio`, `FluentAssertions`, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.NET.Test.Sdk`
  - **Done**: `dotnet restore` sin errores, `dotnet test` ejecuta 0 tests sin fallos

- [ ] T004 Crear estructura de directorios vacía
  - **Archivos**: `src/RateLimiter.Domain/Algorithms/`, `src/RateLimiter.Infrastructure/Storage/`, `src/RateLimiter.Api/Middleware/`, `src/RateLimiter.Api/Configuration/`, `tests/RateLimiter.Tests/Unit/`, `tests/RateLimiter.Tests/Integration/`
  - **Done**: La estructura de carpetas coincide con el árbol documentado arriba

**Checkpoint**: `dotnet build && dotnet test` pasan. La solución compila vacía.

---

## Phase 2: Foundational (Domain Models + Interfaces)

**Purpose**: Definir los modelos de valor y las interfaces que todas las capas consumen. Esto bloquea Phase 3-6 porque todo depende de estos tipos.

**CRITICAL**: Ninguna implementación puede comenzar sin estos tipos definidos.

- [ ] T005 Crear record `RateLimitRule`
  - **Archivo**: `src/RateLimiter.Domain/RateLimitRule.cs`
  - **Contenido**: `record RateLimitRule(int Limit, TimeSpan Window, int? BucketCapacity, double? RefillRate)`
  - **Done**: Compila. Los campos opcionales tienen defaults coherentes (si `BucketCapacity` es null, se usa `Limit`; si `RefillRate` es null, se calcula como `Limit / Window.TotalSeconds`)

- [ ] T006 Crear record `RateLimitResult`
  - **Archivo**: `src/RateLimiter.Domain/RateLimitResult.cs`
  - **Contenido**: `record RateLimitResult(bool IsAllowed, int Limit, int Remaining, int? RetryAfterSeconds)`
  - **Done**: Compila. `RetryAfterSeconds` es nullable (solo presente cuando `IsAllowed == false`)

- [ ] T007 Crear record `ClientRequestInfo`
  - **Archivo**: `src/RateLimiter.Domain/ClientRequestInfo.cs`
  - **Contenido**: `record ClientRequestInfo(string ClientIp, string? UserId, string Endpoint, string HttpMethod)`
  - **Done**: Compila

- [ ] T008 Crear clase `BucketState`
  - **Archivo**: `src/RateLimiter.Domain/BucketState.cs`
  - **Contenido**: Clase mutable con `AvailableTokens` (double), `LastRefillTimestamp` (DateTime), y un `object Lock` para sincronización por instancia
  - **Done**: Compila. Es `class` (no record) porque es mutable — el algoritmo modifica tokens in-place bajo lock
  - **Riesgo**: Asegurar que `Lock` sea `readonly` para que no se reemplace accidentalmente

- [ ] T009 Crear interfaz `IRateLimitAlgorithm`
  - **Archivo**: `src/RateLimiter.Domain/IRateLimitAlgorithm.cs`
  - **Contenido**: Un método `Task<RateLimitResult> EvaluateAsync(string clientKey, RateLimitRule rule, CancellationToken ct = default)`
  - **Done**: Compila. Cumple el contrato documentado en spec.md (Interface Contracts)

- [ ] T010 Crear interfaz `IRateLimitStore`
  - **Archivo**: `src/RateLimiter.Domain/IRateLimitStore.cs`
  - **Contenido**: `GetOrCreateAsync<T>` y `RemoveExpiredEntriesAsync` según spec.md
  - **Done**: Compila. Constraint `where T : class` presente

**Checkpoint**: `dotnet build` compila. Domain tiene 6 archivos. Cero implementaciones, solo contratos. Todas las fases siguientes pueden comenzar.

---

## Phase 3: BS1 — Token Bucket Algorithm (Priority: P1+P2+P3)

**Goal**: Implementar `TokenBucketAlgorithm` con refill lazy, consumo atómico, y cálculo de `RetryAfter`. Cubre Behavior Stories 1, 2 y 3 del spec.

**Independent Test**: Instanciar `TokenBucketAlgorithm` con un `InMemoryRateLimitStore` mock/real, invocar `EvaluateAsync`, y verificar tokens consumidos, rechazos, y refill temporal.

### Tests for Token Bucket

- [ ] T011 [P] [BS1] Test: primer request de un cliente retorna allowed con remaining = capacity - 1
  - **Archivo**: `tests/RateLimiter.Tests/Unit/TokenBucketAlgorithmTests.cs`
  - **Escenario spec**: BS1 Scenario 1
  - **Done**: Test pasa. Verifica `IsAllowed == true`, `Remaining == 9` para capacity 10

- [ ] T012 [P] [BS2] Test: request con 0 tokens retorna denied con RetryAfter > 0
  - **Archivo**: `tests/RateLimiter.Tests/Unit/TokenBucketAlgorithmTests.cs`
  - **Escenario spec**: BS2 Scenario 1
  - **Done**: Test pasa. Verifica `IsAllowed == false`, `Remaining == 0`, `RetryAfterSeconds > 0`

- [ ] T013 [P] [BS2] Test: requests rechazados no consumen tokens
  - **Archivo**: `tests/RateLimiter.Tests/Unit/TokenBucketAlgorithmTests.cs`
  - **Escenario spec**: BS2 Scenario 3
  - **Done**: Test pasa. Después de agotar tokens, N requests rechazados no afectan el refill posterior

- [ ] T014 [P] [BS3] Test: refill proporcional al tiempo transcurrido
  - **Archivo**: `tests/RateLimiter.Tests/Unit/TokenBucketAlgorithmTests.cs`
  - **Escenario spec**: BS3 Scenario 1
  - **Done**: Test pasa. Después de 30s con refill rate 10/min, hay 5 tokens nuevos
  - **Riesgo**: Necesita abstracción de tiempo (`TimeProvider` o `Func<DateTime>`) para tests determinísticos. No usar `Thread.Sleep`.

- [ ] T015 [P] [BS3] Test: refill no excede capacidad del bucket
  - **Archivo**: `tests/RateLimiter.Tests/Unit/TokenBucketAlgorithmTests.cs`
  - **Escenario spec**: BS3 Scenario 2
  - **Done**: Test pasa. Con 8 tokens y 60s de espera, tokens se clampean a capacity (10), no 18

- [ ] T016 [P] [BS3] Test: tokens fraccionarios no alcanzan para consumir
  - **Archivo**: `tests/RateLimiter.Tests/Unit/TokenBucketAlgorithmTests.cs`
  - **Escenario spec**: BS3 Scenario 3
  - **Done**: Test pasa. 0.5 tokens después de 500ms con rate 1/s retorna denied

- [ ] T017 [P] [BS1] Test: dos clientes distintos tienen buckets independientes
  - **Archivo**: `tests/RateLimiter.Tests/Unit/TokenBucketAlgorithmTests.cs`
  - **Escenario spec**: BS1 Scenario 3
  - **Done**: Test pasa. Cliente A agotado no afecta a cliente B

- [ ] T018 [P] [Edge] Test: RetryAfter refleja tiempo exacto hasta próximo token
  - **Archivo**: `tests/RateLimiter.Tests/Unit/TokenBucketAlgorithmTests.cs`
  - **Escenario spec**: BS2 Scenario 2
  - **Done**: Test pasa. Con refill 1 token/6s y último refill hace 2s, RetryAfter == 4

### Implementation for Token Bucket

- [ ] T019 [BS1] Implementar `TokenBucketAlgorithm`
  - **Archivo**: `src/RateLimiter.Domain/Algorithms/TokenBucketAlgorithm.cs`
  - **Contenido**: Constructor recibe `IRateLimitStore` y `TimeProvider`. Método `EvaluateAsync` hace: obtener/crear `BucketState`, lock en la instancia, calcular refill, consumir o rechazar, retornar `RateLimitResult`
  - **Done**: Todos los tests T011-T018 pasan
  - **Riesgo**: El cálculo de refill debe usar `TimeProvider` (no `DateTime.UtcNow`) para testabilidad. `lock` debe ser sobre `BucketState.Lock`, no sobre el store

- [ ] T020 [BS1] Implementar `InMemoryRateLimitStore` (versión mínima para tests)
  - **Archivo**: `src/RateLimiter.Infrastructure/Storage/InMemoryRateLimitStore.cs`
  - **Contenido**: `ConcurrentDictionary<string, object>` con `GetOrAdd` para `GetOrCreateAsync`. `RemoveExpiredEntriesAsync` puede ser no-op por ahora
  - **Done**: `TokenBucketAlgorithm` funciona con este store. Tests T011-T018 pasan

**Checkpoint**: `dotnet test --filter "TokenBucketAlgorithm"` — todos pasan. El algoritmo funciona aislado del HTTP stack. Refill, consumo, rechazo, y RetryAfter son correctos.

---

## Phase 4: BS4 — Store + Concurrencia (Priority: P4)

**Goal**: Completar `InMemoryRateLimitStore` con cleanup de entries expiradas y validar thread-safety del sistema completo (store + algoritmo) bajo contención.

**Independent Test**: Lanzar 20 requests concurrentes con `Task.WhenAll` contra un límite de 10 y verificar exactamente 10 allowed.

### Tests for Store + Concurrency

- [ ] T021 [P] [BS4] Test: GetOrCreateAsync retorna misma instancia para mismo key
  - **Archivo**: `tests/RateLimiter.Tests/Unit/InMemoryRateLimitStoreTests.cs`
  - **Done**: Test pasa. Dos llamadas con mismo key retornan `ReferenceEquals == true`

- [ ] T022 [P] [BS4] Test: GetOrCreateAsync es thread-safe — factory se invoca una sola vez
  - **Archivo**: `tests/RateLimiter.Tests/Unit/InMemoryRateLimitStoreTests.cs`
  - **Done**: Test pasa. 100 llamadas concurrentes con mismo key → factory invocada exactamente 1 vez (usar `Interlocked.Increment` para contar)

- [ ] T023 [P] [BS4] Test: 20 requests concurrentes al mismo cliente respetan el límite de 10
  - **Archivo**: `tests/RateLimiter.Tests/Unit/TokenBucketAlgorithmTests.cs`
  - **Escenario spec**: BS4 Scenario 1
  - **Done**: Test pasa. Exactamente 10 `IsAllowed == true` y 10 `IsAllowed == false`
  - **Riesgo**: Usar `Task.WhenAll` con `Task.Run` para generar contención real. No usar `Parallel.For` (no garantiza concurrencia). Ejecutar el test varias veces para detectar flakiness

- [ ] T024 [P] [BS4] Test: 1 token restante + 5 requests concurrentes = exactamente 1 allowed
  - **Archivo**: `tests/RateLimiter.Tests/Unit/TokenBucketAlgorithmTests.cs`
  - **Escenario spec**: BS4 Scenario 2
  - **Done**: Test pasa. Exactamente 1 `IsAllowed == true` y 4 `false`

- [ ] T025 [P] [BS4] Test: clientes distintos no se bloquean entre sí bajo concurrencia
  - **Archivo**: `tests/RateLimiter.Tests/Unit/TokenBucketAlgorithmTests.cs`
  - **Escenario spec**: BS4 Scenario 3
  - **Done**: Test pasa. Cada cliente consume independientemente sus tokens

- [ ] T026 [P] [Store] Test: RemoveExpiredEntriesAsync elimina entries viejas
  - **Archivo**: `tests/RateLimiter.Tests/Unit/InMemoryRateLimitStoreTests.cs`
  - **Done**: Test pasa. Entry con último acceso > threshold se elimina, entry reciente sobrevive

### Implementation for Store

- [ ] T027 [BS4] Completar `InMemoryRateLimitStore` con tracking de último acceso y cleanup
  - **Archivo**: `src/RateLimiter.Infrastructure/Storage/InMemoryRateLimitStore.cs`
  - **Contenido**: Agregar `ConcurrentDictionary<string, DateTime>` para tracking de último acceso. `RemoveExpiredEntriesAsync` itera y elimina entries cuyo último acceso supera un umbral configurable
  - **Done**: Tests T021-T026 pasan

**Checkpoint**: `dotnet test --filter "InMemoryRateLimitStore|Concurren"` — todos pasan. El store es thread-safe y el algoritmo no tiene race conditions bajo contención.

---

## Phase 5: BS1+BS2+BS5 — Middleware HTTP (Priority: P1+P2+P5)

**Goal**: Implementar `RateLimitMiddleware`, binding de configuración, y `Program.cs`. Al terminar, `curl` contra un endpoint devuelve 200 con headers o 429 con body correcto.

**Independent Test**: Levantar el server con `dotnet run`, hacer curl al endpoint, verificar headers y status code.

### Tests for Middleware

- [ ] T028 [P] [BS5] Test: middleware hace fail-open cuando el algoritmo lanza excepción
  - **Archivo**: `tests/RateLimiter.Tests/Integration/RateLimitMiddlewareTests.cs`
  - **Escenario spec**: BS5 Scenario 1 y 2
  - **Done**: Test pasa. Con `IRateLimitAlgorithm` que lanza `InvalidOperationException`, response es 200 sin headers `X-RateLimit-*`
  - **Riesgo**: Usar `WebApplicationFactory` con override de DI para inyectar algoritmo que falla

- [ ] T029 [P] [BS5] Test: middleware retorna 503 cuando FailOpen = false y algoritmo falla
  - **Archivo**: `tests/RateLimiter.Tests/Integration/RateLimitMiddlewareTests.cs`
  - **Escenario spec**: BS5 Scenario 3
  - **Done**: Test pasa. Con `FailOpen = false`, response es 503

- [ ] T030 [P] [Middleware] Test: endpoint sin regla configurada pasa sin rate limiting
  - **Archivo**: `tests/RateLimiter.Tests/Integration/RateLimitMiddlewareTests.cs`
  - **Escenario spec**: FR-008
  - **Done**: Test pasa. Request a endpoint sin regla retorna 200 sin headers `X-RateLimit-*`

### Implementation for Middleware

- [ ] T031 [Middleware] Crear `RateLimitOptions`
  - **Archivo**: `src/RateLimiter.Api/Configuration/RateLimitOptions.cs`
  - **Contenido**: `Dictionary<string, RateLimitRule> Rules`, `string DefaultClientIdentifier` ("ip"), `bool FailOpen` (default true)
  - **Done**: Compila. Se puede bindear desde `appsettings.json` sección `"RateLimiting"`

- [ ] T032 [Middleware] Implementar `RateLimitMiddleware`
  - **Archivo**: `src/RateLimiter.Api/Middleware/RateLimitMiddleware.cs`
  - **Contenido**: `InvokeAsync` que: extrae client identity (método privado), resuelve regla desde `IOptions<RateLimitOptions>`, llama `IRateLimitAlgorithm.EvaluateAsync`, escribe headers o retorna 429 (métodos privados). Try-catch para fail-open/fail-closed
  - **Done**: Tests T028-T030 pasan
  - **Riesgo**: El middleware debe registrarse antes de los endpoints en el pipeline (`app.UseMiddleware<RateLimitMiddleware>()` antes de `app.MapGet`)

- [ ] T033 [Middleware] Configurar `Program.cs` y `appsettings.json`
  - **Archivos**: `src/RateLimiter.Api/Program.cs`, `src/RateLimiter.Api/appsettings.json`
  - **Contenido**: Registrar `IRateLimitAlgorithm` → `TokenBucketAlgorithm`, `IRateLimitStore` → `InMemoryRateLimitStore` (singleton), `IOptions<RateLimitOptions>`, middleware, y al menos un endpoint de prueba (`/api/resource`)
  - **Done**: `dotnet run` levanta el server. `curl localhost:5000/api/resource` retorna 200 con headers `X-RateLimit-*`

**Checkpoint**: `dotnet run` + `curl` manual. Request 1-10 retornan 200 con `Remaining` decreciente. Request 11 retorna 429 con `Retry-After`.

---

## Phase 6: Integration Tests (Cobertura completa)

**Goal**: Tests de integración con `WebApplicationFactory` que cubren todos los Behavior Stories del spec como flujos HTTP completos.

**Independent Test**: `dotnet test --filter "Integration"` pasa todos los escenarios.

### Tests

- [ ] T034 [P] [BS1] Integration: request con tokens retorna 200 + headers correctos
  - **Archivo**: `tests/RateLimiter.Tests/Integration/RateLimitMiddlewareTests.cs`
  - **Escenario spec**: BS1 Scenario 1
  - **Done**: Response 200, headers `X-RateLimit-Limit` y `X-RateLimit-Remaining` presentes con valores correctos

- [ ] T035 [P] [BS2] Integration: request excedido retorna 429 + body JSON + headers
  - **Archivo**: `tests/RateLimiter.Tests/Integration/RateLimitMiddlewareTests.cs`
  - **Escenario spec**: BS2 Scenario 1
  - **Done**: Response 429, body tiene `error` y `message`, header `X-RateLimit-Retry-After` presente

- [ ] T036 [P] [BS3] Integration: tokens se recargan después de esperar
  - **Archivo**: `tests/RateLimiter.Tests/Integration/RateLimitMiddlewareTests.cs`
  - **Escenario spec**: BS3 Scenario 1
  - **Done**: Agotar tokens, avanzar tiempo via `FakeTimeProvider`, verificar que siguiente request es 200
  - **Riesgo**: Requiere inyectar `FakeTimeProvider` en el `WebApplicationFactory`. Configurar override de DI correctamente

- [ ] T037 [P] [BS4] Integration: requests concurrentes via HttpClient respetan límite
  - **Archivo**: `tests/RateLimiter.Tests/Integration/RateLimitMiddlewareTests.cs`
  - **Escenario spec**: BS4 Scenario 1
  - **Done**: 20 requests concurrentes con `Task.WhenAll` → exactamente 10 retornan 200

- [ ] T038 [P] [BS1] Integration: dos IPs distintas tienen límites independientes
  - **Archivo**: `tests/RateLimiter.Tests/Integration/RateLimitMiddlewareTests.cs`
  - **Escenario spec**: BS1 Scenario 3
  - **Done**: Simular IPs distintas (via header custom o `HttpContext` override), verificar buckets independientes
  - **Riesgo**: `WebApplicationFactory` no permite fácilmente simular IPs distintas. Opción: usar header `X-Forwarded-For` y configurar el middleware para leerlo, o usar `X-Client-Id` custom para tests

- [ ] T039 [P] [Edge] Integration: endpoint sin regla pasa sin headers de rate limit
  - **Archivo**: `tests/RateLimiter.Tests/Integration/RateLimitMiddlewareTests.cs`
  - **Escenario spec**: FR-008
  - **Done**: Request a endpoint no configurado retorna 200 sin headers `X-RateLimit-*`

- [ ] T040 [P] [Edge] Integration: regla con límite 0 rechaza siempre
  - **Archivo**: `tests/RateLimiter.Tests/Integration/RateLimitMiddlewareTests.cs`
  - **Escenario spec**: Edge case "Regla con límite 0"
  - **Done**: Todo request a endpoint con `Limit: 0` retorna 429

**Checkpoint**: `dotnet test` — todos los tests (unit + integration) pasan. Cobertura completa de los 5 Behavior Stories + edge cases.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Documentación final, cleanup de código, README para onboarding.

- [ ] T041 Actualizar `DESIGN.md` con decisiones finales de implementación
  - **Archivo**: `rate-limiter/DESIGN.md`
  - **Done**: DESIGN.md refleja la implementación real (no solo el plan). Diferencias entre plan e implementación están documentadas

- [ ] T042 Crear `README.md` con instrucciones de uso
  - **Archivo**: `rate-limiter/README.md`
  - **Contenido**: Cómo buildear (`dotnet build`), cómo correr tests (`dotnet test`), cómo ejecutar (`dotnet run`), ejemplo de curl, cómo configurar reglas en `appsettings.json`
  - **Done**: Un desarrollador nuevo puede clonar, buildear, y testear siguiendo solo el README

- [ ] T043 Revisar y limpiar warnings del compilador
  - **Done**: `dotnet build --warnaserror` compila sin warnings. No hay `#pragma warning disable` innecesarios

- [ ] T044 Verificar que `dotnet test` pasa desde cero (clean build)
  - **Done**: `dotnet clean && dotnet build && dotnet test` pasa todos los tests sin errores

---

## Dependencies & Execution Order

### Phase Dependencies

```
Phase 1 (Setup) ──── blocks everything
       │
Phase 2 (Domain) ── blocks Phase 3, 4, 5, 6
       │
Phase 3 (Token Bucket) ── can start after Phase 2
       │
Phase 4 (Store + Concurrency) ── depends on Phase 3 (needs working algorithm)
       │
Phase 5 (Middleware) ── depends on Phase 3 + Phase 4 (needs algorithm + store)
       │
Phase 6 (Integration Tests) ── depends on Phase 5 (needs running HTTP stack)
       │
Phase 7 (Polish) ── depends on Phase 6 (needs everything working)
```

### Critical Path

`Phase 1 → Phase 2 → Phase 3 → Phase 4 → Phase 5 → Phase 6 → Phase 7`

No hay paralelismo real en este plan — cada fase produce artefactos que la siguiente consume. Esto es intencional: con un solo desarrollador, el feedback loop de "test rojo → implementar → test verde" es más valioso que intentar paralelizar.

### Within Each Phase

- Tests antes de implementación (TDD: marcados con `[P]`)
- Modelos antes de lógica
- Lógica antes de integración
- Checkpoint de verificación antes de avanzar a la siguiente fase

## Notes

- `[P]` marca tests que se escriben ANTES de la implementación (TDD)
- `[BS1]`..`[BS5]` mapea tarea a Behavior Story del spec para trazabilidad
- `[Edge]` marca tests de edge cases
- `[Store]` y `[Middleware]` marcan tests de infraestructura
- Cada fase tiene un checkpoint explícito — no avanzar sin validarlo
- Commit después de cada fase completa o grupo lógico de tareas
- `TimeProvider` (built-in en .NET 8+) se usa para abstracción de tiempo — no `DateTime.UtcNow` directo
