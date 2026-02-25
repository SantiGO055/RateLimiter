# Rate Limiter

HTTP middleware para ASP.NET Core que implementa rate limiting con el algoritmo **Token Bucket** y refill lazy. Identifica clientes por IP, configura reglas por endpoint vía `appsettings.json`, y soporta fail-open/fail-closed ante fallos del store.

## Estructura

```
src/
  RateLimiter.Domain/          # Algoritmo, interfaces, modelos de valor
  RateLimiter.Infrastructure/  # InMemoryRateLimitStore (ConcurrentDictionary)
  RateLimiter.Api/             # Middleware HTTP, configuración, Program.cs
tests/
  RateLimiter.Tests/
    Unit/                      # TokenBucketAlgorithm, InMemoryRateLimitStore
    Integration/               # Flujos HTTP completos via WebApplicationFactory
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

Correr solo tests de integración:
```bash
dotnet test --filter "Integration"
```

## Cómo ejecutar

```bash
dotnet run --project src/RateLimiter.Api
```

Probar con curl:
```bash
# Request permitido — retorna 200 con headers X-RateLimit-*
curl -v http://localhost:5000/api/resource

# Después de 10 requests — retorna 429
curl -v http://localhost:5000/api/resource

# Endpoint sin regla — retorna 200 sin headers de rate limit
curl -v http://localhost:5000/api/unrestricted
```

## Cómo configurar reglas

En `src/RateLimiter.Api/appsettings.json`:

```json
{
  "RateLimiting": {
    "FailOpen": true,
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

- `Limit`: máximo de requests por ventana
- `Window`: duración de la ventana (formato `HH:mm:ss`)
- `BucketCapacity` (opcional): capacidad máxima del bucket. Por defecto: `Limit`
- `RefillRate` (opcional): tokens por segundo. Por defecto: `Limit / Window.TotalSeconds`
- `FailOpen`: si el store falla, `true` pasa el request (default), `false` retorna 503

## Proceso de desarrollo

El diseño y la implementación siguieron un proceso documentado:
- `DESIGN.md`: decisiones de arquitectura y trade-offs
- `specs/rate-limiter/spec.md`: contratos de comportamiento y casos de borde
- `specs/rate-limiter/plan.md`: plan de implementación fase por fase (TDD)

Se utilizó Claude Code como herramienta de asistencia.
Todo el código fue revisado y es explicable en detalle.