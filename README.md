# Rate Limiter

HTTP middleware para ASP.NET Core que implementa rate limiting con el algoritmo **Token Bucket** y refill lazy. Identifica clientes por IP, configura reglas por endpoint vía `appsettings.json`, y soporta dos modos de almacenamiento: **InMemory** (por defecto) y **Redis** (distribuido).

## Estructura

```
src/
  RateLimiter.Domain/          # Algoritmo, interfaces, modelos de valor
  RateLimiter.Infrastructure/  # InMemoryRateLimitStore, RedisTokenBucketAlgorithm
  RateLimiter.Api/             # Middleware HTTP, configuración, Program.cs
tests/
  RateLimiter.Tests/
    Unit/                      # TokenBucketAlgorithm, InMemoryRateLimitStore
    Integration/               # Flujos HTTP (WebApplicationFactory), Redis (Testcontainers)
```

## Cómo buildear

```bash
dotnet build RateLimiter.slnx
```

## Cómo correr los tests

```bash
dotnet test RateLimiter.slnx
```

Correr solo tests unitarios:
```bash
dotnet test --filter "Unit"
```

Correr solo tests de integración (incluye Redis — requiere Docker):
```bash
dotnet test --filter "Integration"
```

Correr solo tests de Redis:
```bash
dotnet test --filter "RedisTokenBucketAlgorithmTests"
```

> Los tests de Redis usan Testcontainers y levantan un contenedor Redis automáticamente. Requieren Docker corriendo.

## Cómo ejecutar

### Modo InMemory (sin dependencias externas)

```bash
dotnet run --project src/RateLimiter.Api
```

### Modo Redis (distribuido)

1. Levantar Redis con Docker:

```bash
docker-compose up -d
```

2. En `src/RateLimiter.Api/appsettings.json`, configurar:

```json
"RateLimiting": {
  "Store": "Redis"
}
```

3. Correr la API:

```bash
dotnet run --project src/RateLimiter.Api
```

Al iniciar, la consola muestra:
```
info: Program[0]
      Rate limiting store: Redis
```

### Probar con curl

```bash
# Request permitido — retorna 200 con headers X-RateLimit-*
curl -i http://localhost:5047/api/resource

# Agotar el límite y ver 429
for i in $(seq 1 11); do curl -s -o /dev/null -w "Status: %{http_code}\n" http://localhost:5047/api/resource; done

# Simular dos IPs distintas
curl -H "X-Forwarded-For: 10.0.0.1" http://localhost:5047/api/resource
curl -H "X-Forwarded-For: 10.0.0.2" http://localhost:5047/api/resource

# Endpoint sin regla — retorna 200 sin headers de rate limit
curl -i http://localhost:5047/api/unrestricted
```

### Inspeccionar el estado en Redis

```bash
# Ver todos los keys (uno por cliente+endpoint)
docker exec rate-limiter-redis-1 redis-cli KEYS "*"

# Ver el estado del bucket de un cliente (IP local = ::1)
docker exec rate-limiter-redis-1 redis-cli HGETALL "::1:/api/resource"

# Monitorear comandos en tiempo real
docker exec -it rate-limiter-redis-1 redis-cli MONITOR
```

## Cómo configurar

En `src/RateLimiter.Api/appsettings.json`:

```json
{
  "ConnectionStrings": {
    "Redis": "localhost:6380,connectTimeout=1000,syncTimeout=500,abortConnect=false"
  },
  "RateLimiting": {
    "Store": "InMemory",
    "FailOpen": true,
    "CleanupIntervalSeconds": 300,
    "CircuitBreaker": {
      "FailureThreshold": 5,
      "SamplingDurationSeconds": 30,
      "BreakDurationSeconds": 15
    },
    "Rules": {
      "/api/resource": {
        "Limit": 10,
        "Window": "00:01:00",
        "BucketCapacity": 10,
        "RefillRate": 0.16667
      },
      "/api/otro-endpoint": {
        "Limit": 5,
        "Window": "00:00:10"
      }
    }
  }
}
```

| Campo | Descripción |
|---|---|
| `Store` | `"InMemory"` (default) o `"Redis"` |
| `FailOpen` | `true`: ante fallo del store, el request pasa. `false`: retorna 503 |
| `CleanupIntervalSeconds` | Cada cuántos segundos limpiar entradas expiradas (solo InMemory) |
| `CircuitBreaker.FailureThreshold` | Fallos consecutivos antes de abrir el circuito (solo Redis) |
| `CircuitBreaker.SamplingDurationSeconds` | Ventana de tiempo para contar fallos (solo Redis) |
| `CircuitBreaker.BreakDurationSeconds` | Segundos que el circuito permanece abierto (solo Redis) |
| `Limit` | Máximo de requests por ventana |
| `Window` | Duración de la ventana (`HH:mm:ss`) |
| `BucketCapacity` | Capacidad máxima del bucket (opcional, default: `Limit`) |
| `RefillRate` | Tokens por segundo (opcional, default: `Limit / Window.TotalSeconds`) |

## Métricas

El sistema expone contadores con `System.Diagnostics.Metrics` (compatible con OpenTelemetry):

| Métrica | Descripción | Tags |
|---|---|---|
| `ratelimit.requests.allowed` | Requests que pasaron | `endpoint` |
| `ratelimit.requests.blocked` | Requests rechazados con 429 | `endpoint` |
| `ratelimit.store.errors` | Fallos de evaluación del store | `endpoint` |

Ver en tiempo real con `dotnet-counters`:
```bash
dotnet-counters monitor --name RateLimiter.Api --counters RateLimiter
```

## Proceso de desarrollo

El diseño y la implementación siguieron un proceso documentado:
- `DESIGN.md`: decisiones de arquitectura y trade-offs
- `specs/spec.md`: contratos de comportamiento y casos de borde
- `specs/plan.md`: plan de implementación fase por fase (TDD)

Se utilizó Claude Code como herramienta de asistencia.
Todo el código fue revisado y es explicable en detalle.
