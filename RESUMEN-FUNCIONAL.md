# Rate Limiter — Resumen Funcional

## ¿Qué hace este sistema?

Este proyecto es un componente de control de tráfico para una API web. Su función es detectar cuándo un cliente específico (identificado por su dirección IP) está enviando demasiadas solicitudes en un período de tiempo, y bloquearlas automáticamente cuando supera el límite configurado.

---

## ¿Cómo funciona para el usuario final?

Cada cliente tiene asignado un "balde" virtual con una cantidad de fichas. Cada vez que hace una solicitud, se consume una ficha. Cuando el balde se vacía, las solicitudes son rechazadas hasta que las fichas se recarguen.

Las fichas se recargan progresivamente con el tiempo, sin necesidad de esperar que se reinicie ningún contador. Si el cliente espera, acumula fichas proporcionales al tiempo transcurrido.

### Ejemplo concreto

Con una regla de 10 solicitudes por minuto:

| Momento | Acción del cliente | Resultado |
|---|---|---|
| 0:00 | Envía 10 solicitudes | Todas pasan. Balde vacío. |
| 0:10 | Envía otra solicitud | Bloqueada. Solo hay ~1.6 fichas (10 segundos × 10/60 por segundo = no alcanza para 1 entera). |
| 0:30 | Envía otra solicitud | Pasa. En 30 segundos se recargaron 5 fichas. Quedan 4. |

---

## ¿Qué recibe el cliente en cada respuesta?

Cada respuesta a un endpoint protegido incluye cabeceras informativas:

- **X-RateLimit-Limit**: cuántas solicitudes están permitidas en la ventana configurada
- **X-RateLimit-Remaining**: cuántas fichas quedan antes de ser bloqueado
- **X-RateLimit-Retry-After**: (solo en bloqueos) cuántos segundos debe esperar antes de reintentar

Cuando el cliente es bloqueado, recibe código HTTP **429 Too Many Requests** con este cuerpo:

```json
{
  "error": "rate_limit_exceeded",
  "message": "Too many requests. Please retry after N seconds."
}
```

---

## ¿Qué protege y qué no protege?

| Escenario | Comportamiento |
|---|---|
| Endpoint configurado en las reglas | Se aplica el límite por IP |
| Endpoint sin regla configurada | La solicitud pasa sin ninguna restricción |
| Dos clientes con distinta IP | Cada uno tiene su propio balde independiente |
| Fallo interno del sistema de límites | Por defecto, las solicitudes pasan (fail-open). Configurable para bloquear todo (fail-closed). |

---

## Configuración

Las reglas se configuran en `appsettings.json` bajo la sección `RateLimiting`. Cada endpoint tiene:

- **Limit**: cantidad máxima de solicitudes en la ventana
- **Window**: duración de la ventana (formato `HH:mm:ss`)
- **BucketCapacity**: tamaño máximo del balde (controla los bursts permitidos)
- **RefillRate**: fichas que se recargan por segundo (opcional; si se omite, se calcula automáticamente)
- **FailOpen**: si es `true` (por defecto), ante fallo del sistema las solicitudes pasan; si es `false`, se devuelve 503

---

## ¿Qué garantiza el sistema ante uso simultáneo?

Si 20 clientes con la misma IP envían solicitudes al mismo tiempo y el límite es 10, exactamente 10 reciben respuesta exitosa y 10 son bloqueados. No hay posibilidad de que "pasen de más" por condiciones de carrera.

Distintos clientes (distintas IPs) son procesados en paralelo y nunca se bloquean entre sí.

---

## Modos de almacenamiento

El sistema soporta dos modos de almacenamiento configurables:

### InMemory (por defecto)

Los baldes de cada cliente se guardan en la memoria del proceso. Es el modo recomendado para una sola instancia de la API o para desarrollo local. Los datos se pierden al reiniciar el servidor.

### Redis (distribuido)

Los baldes se guardan en Redis. Todas las instancias de la API comparten el mismo estado, lo que garantiza que el límite se respete globalmente aunque el tráfico se distribuya entre múltiples servidores.

La operación de evaluar si un request pasa o no se ejecuta como un script atómico dentro de Redis, lo que elimina cualquier posibilidad de condición de carrera entre instancias.

Para activar Redis, cambiar `"Store": "Redis"` en `appsettings.json` y tener Redis corriendo (ver README).

---

## Estado actual del sistema

| Aspecto | Estado |
|---|---|
| Tests unitarios del algoritmo | 11/11 pasando |
| Tests de concurrencia | 2/2 pasando |
| Tests de integración HTTP | 10/10 pasando |
| Tests de integración Redis | 8/8 pasando |
| Total de tests | 31/31 pasando |
| Advertencias de compilación | 0 |

---

## Limitaciones conocidas

- **Identificación solo por IP**: no hay soporte actual para identificar clientes por token de autenticación o usuario registrado.
- **Limpieza de memoria (modo InMemory)**: el sistema elimina automáticamente los registros de clientes inactivos cada 5 minutos (configurable con `RateLimiting:CleanupIntervalSeconds`).
- **Redis modo InMemory**: al reiniciar el servidor los baldes se resetean; con Redis los datos persisten entre reinicios.
