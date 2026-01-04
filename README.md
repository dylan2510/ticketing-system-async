# Ticketing Reservation POC

This repository contains a **high-volume, async ticket reservation POC** using:

- API → enqueue requests
- Worker service → async processing
- Redis → atomic stock control & reservation state
- Message queue → decoupling & backpressure

The design is optimized for **high concurrency** and **correctness under load**.

---

## Architecture Overview (High Level)
- API is **fast & non-blocking**
- Worker is **horizontally scalable**
- Redis enforces **atomicity & correctness**

---

## Prerequisites

- Docker & Docker Compose
- .NET SDK (compatible with the projects in this repo)

---

## 1. Start Infrastructure (Docker)

Start all required infrastructure (e.g. Redis, RabbitMQ, etc.):
docker compose -f docker-compose.yml up

## 2. Run the following services
dotnet run --project Ticketing.Reservation.Api
dotnet run --project Ticketing.Reservation.Worker


## . Test endpoints:
POST /admin/events/{eventId}/seed/{stock} -> seed the slots/tickets
POST /api/reservations -> enqueue + set status = Pending; returns 202 + trackingId
GET /api/reservations/{trackingId} -> read status from Redis

POST /api/reservations/{trackingId}/confirm -> call by worker service to confirm and set reservation, not called by client. 
