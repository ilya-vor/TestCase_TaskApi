# TaskApi — тестовое задание (C#/.NET Backend, Middle)

Минимальный API задач на **.NET 9 / ASP.NET Core Minimal API / PostgreSQL (EF Core) / RabbitMQ**
(только публикация).

## Что реализовано

| Метод | Эндпоинт | Поведение |
|-------|----------|-----------|
| POST | `/tasks` | создаёт задачу (`IsCompleted=false`, `Priority=Medium`, `CompletedAt=null`); валидация `Title` → 400 |
| GET | `/tasks` | возвращает все задачи (без пагинации) |
| PUT | `/tasks/{id}/complete` | завершает задачу, ставит `CompletedAt`, сохраняет в БД, **затем** публикует `task.completed`; повторный/конкурентный вызов → 409 |
| DELETE | `/tasks/{id}` | жёсткое удаление |

- **Валидация**: `Title` не пустой (в т.ч. не whitespace) и ≤ 200 символов → `400 ValidationProblem`.
  Некорректный `priority` (число вне enum) тоже даёт 400.
- **Конкурентность**: используется системная колонка PostgreSQL `xmin` как optimistic concurrency
  token (эквивалент `RowVersion`). Второй параллельный `complete` получает `409 Conflict`.
- **RabbitMQ**: exchange `task.events` (topic, durable), routing key `task.completed`, тело:
  ```json
  { "taskId": "...", "title": "Buy milk", "completedAt": "...", "priority": "High" }
  ```
  Публикация не блокирует запрос (без ожидания confirms). Если брокер недоступен — задача
  всё равно завершается, ошибка только логируется (fail silently).
- **Порядок**: событие публикуется **после** успешного `SaveChangesAsync`, а не до — чтобы не
  отправить событие о записи, которая не сохранилась.
- **Graceful shutdown**: `RabbitMqPublisher` реализует `IAsyncDisposable`; хост при SIGTERM/Ctrl+C
  ждёт завершения «в полёте» публикаций (таймаут 4 c), затем закрывает канал и соединение.
- **Swagger UI** (необязательный бонус): `http://localhost:5154/swagger` — удобно дёргать эндпоинты из браузера.

## Структура проекта

```
Test1/
├── TaskApi.slnx
├── docker-compose.yml                 # PostgreSQL + RabbitMQ для локального запуска
├── README.md
├── TaskApi/
│   ├── Program.cs                     # #region: Configuration & DI / Database bootstrap / Endpoints
│   ├── Domain/
│   │   ├── TaskItem.cs                # сущность + Complete()
│   │   └── Priority.cs                # enum Low/Medium/High
│   ├── Data/
│   │   └── TaskDbContext.cs           # маппинг, ограничения, xmin concurrency token
│   ├── Contracts/
│   │   └── TaskContracts.cs           # DTO запроса/ответа и событие
│   ├── Messaging/
│   │   ├── ITaskEventPublisher.cs     # абстракция (для тестов/мока)
│   │   ├── RabbitMqOptions.cs
│   │   └── RabbitMqPublisher.cs       # публикация + реконнект + graceful shutdown
│   └── appsettings.json
└── Tests/
    ├── TaskApiFixture.cs              # Testcontainers: PostgreSQL + RabbitMQ, WebApplicationFactory
    └── CompleteTaskIntegrationTest.cs # 6 интеграционных тестов
```

## Запуск

```bash
# 1. Инфраструктура
docker compose up -d

# 2. API
dotnet run --project TaskApi
# по умолчанию слушает http://localhost:5154 (см. launchSettings), либо задайте ASPNETCORE_URLS
```

Swagger UI: `http://localhost:5154/swagger` (доступен во всех окружениях).

### Пример запросов

```bash
curl -X POST http://localhost:5080/tasks \
  -H "Content-Type: application/json" \
  -d '{"title":"Buy milk","priority":"High"}'

curl http://localhost:5080/tasks
curl -X PUT http://localhost:5080/tasks/<id>/complete
curl -X DELETE http://localhost:5080/tasks/<id>
```

## Тесты

```bash
dotnet test
```

Интеграционные тесты поднимают **реальные** PostgreSQL и RabbitMQ через Testcontainers
(нужен запущенный Docker), поэтому проверяют настоящий путь: EF Core + Npgsql + AMQP.

Покрытие:
1. `Create_then_complete_task_publishes_task_completed_event` — создать → завершить → проверить,
   что RabbitMQ получил `task.completed` с корректными полями (обязательный тест из задания).
2. `Create_task_with_blank_title_returns_400` (пустая строка и whitespace).
3. `Create_task_with_title_longer_than_200_returns_400`.
4. `Concurrent_completion_returns_one_success_and_one_conflict` — два одновременных `complete`
   дают ровно один `200` и один `409` (проверка xmin).
5. `Complete_task_succeeds_even_when_rabbitmq_is_unreachable` — fail-silent при недоступном брокере.

## Принятые решения и компромиссы

- **`xmin` вместо отдельной колонки `RowVersion`** — «человеческий» способ для PostgreSQL без
  ручного управления версиями строк. В Npgsql 9 `UseXminAsConcurrencyToken()` удалён, поэтому
  токен настроен как `uint`-свойство `OnAddOrUpdate + IsConcurrencyToken`, которое конвенция
  мапит на системную колонку `xmin`.
- **Создание схемы через `EnsureCreated()`** (помечено `// HACK` в `Program.cs`). Миграции
  приветствовались, но не были обязательны; чтобы не тратить время на `dotnet ef` и снизить
  риск в окружении, схема создаётся при старте. Доделал бы так:
  `dotnet ef migrations add InitialCreate` + `db.Database.MigrateAsync()`.
- **Публикация без publisher confirms** — по условию задания; событие может потеряться при
  падении брокера сразу после публикации, но не блокирует API. В реальном проекте добавил бы
  outbox-таблицу и/или confirms для гарантии доставки.
- **Fail-silent публикация** — источником истины является БД; ошибка брокера логируется и не
  откатывает завершение задачи.

## Что доделал бы (TODO)

- [ ] EF-миграции вместо `EnsureCreated`.
- [ ] Outbox-паттерн для гарантированной доставки событий.
- [ ] Пагинация/фильтрация/сортировка для `GET /tasks`.
- [ ] Health-checks для PostgreSQL и RabbitMQ.
- [ ] Тест graceful shutdown с реальным SIGTERM (сейчас закрытие проверяется через dispose хоста в тестах).
```
